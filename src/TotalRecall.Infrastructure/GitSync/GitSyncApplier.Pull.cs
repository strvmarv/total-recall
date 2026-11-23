using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncApplier
{
    /// <summary>The reason a local deletion whose path in <c>R</c> the listing rejects is skipped (milestone 1b-ii, D13).</summary>
    internal const string RecordRejected = "the record is rejected";

    /// <summary>The reason a local deletion whose accepted path in <c>R</c> holds a record that does not parse is skipped (D13).</summary>
    internal const string RecordInvalid = "the record is invalid";

    /// <summary>
    /// Applies the fetched repository (decisions 4 to 6, 10 and 14 to 16): reads the tree at <c>R</c>, builds the visit
    /// set (the rows with a <c>repush_groups</c> marker included, milestone 1b-ii D16, and every id with a held-delete item,
    /// open or approved, milestone 1b-iii D4), previews the deletions for the
    /// delete guard (D1 to D4), decides each entry in ordinal id order (one transaction each), records the rejections,
    /// sweeps the cleared ones, and advances <c>L</c> only for a complete pull (a tripped guard does not hold it back, nor
    /// does a repush marker, D19). In an unproven pull every deletion candidate is held before <c>L</c> moves (D5 and the
    /// umbrella's "a missing file is never proof"): one whose entry ended failed, skipped or scan-skipped, so that its own
    /// transaction wrote no hold, gets an open <c>unproven</c> item from a fallback transaction of its own (see
    /// <see cref="HoldUnprovenCandidate"/>), and when that cannot be written the entry is deferred and <c>L</c> stays.
    /// Never throws for a bad record, a busy database or a failed entry; the token stops it between entries.
    /// </summary>
    public async Task<ApplyOutcome> ApplyAsync(FetchResult fetch, ScanSummary scan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(scan);
        var conn = _store.Connection;

        var read = await PullTree.ReadAsync(_client, fetch.Head, fetch.Last, unproven: !fetch.Proven, cancellationToken).ConfigureAwait(false);
        if (read is TreeRead.Failed treeFailed)
            return new ApplyOutcome.Failed(treeFailed.Reason);
        var view = ((TreeRead.Ok)read).View;

        Dictionary<string, GitSyncRow> rows;
        Dictionary<string, IssueRow> failures;
        Dictionary<string, IssueRow> rejections;
        Dictionary<string, HeldItem> openHeld;
        Dictionary<string, HeldItem> approved;
        try
        {
            rows = GitSyncRows.ReadAll(conn);
            failures = GitSyncIssues.ReadOpen(conn, GitSyncIssues.ApplyFailed);
            rejections = GitSyncIssues.ReadOpen(conn, GitSyncIssues.RejectedRecord);
            openHeld = GitSyncReviews.ReadOpenHeld(conn);
            approved = GitSyncReviews.ReadApprovedHeld(conn);
        }
        catch (Exception ex) when (ex is FormatException or SqliteException or InvalidOperationException)
        {
            return new ApplyOutcome.Failed("the sync rows could not be read: " + OneLine(ex.Message));
        }

        var skippedByScan = scan.Skipped
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(i => (i.Group is null ? "" : i.Group + ": ") + i.Reason)), StringComparer.Ordinal);
        var localDeletions = scan.LocalDeletions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        var revisit = view.Accepted
            .Where(p => rejections.ContainsKey(SyncApply.rejectionKey(p.Value.Path)))
            .Select(p => p.Key);
        var input = new SyncApply.VisitInput(
            !fetch.Proven,
            SetModule.OfSeq(view.Accepted.Keys),
            SetModule.OfSeq(view.Changed),
            SetModule.OfSeq(view.Present),
            SetModule.OfSeq(rows.Values.Where(r => r.BaseJson is null).Select(r => r.EntryId)),
            SetModule.OfSeq(failures.Keys),
            SetModule.OfSeq(revisit),
            SetModule.OfSeq(rows.Values.Where(r => r.BaseJson is not null && r.State.IsActive).Select(r => r.EntryId)),
            SetModule.OfSeq(localDeletions),
            SetModule.OfSeq(rows.Values.Where(r => r.RepushGroups is not null).Select(r => r.EntryId)));
        // Milestone 1b-iii, D4: every id with a held-delete item, open or approved, is visited, so an entry re-created under
        // its still-active row with an unchanged record still closes its item.
        var ids = SyncApply.visitSet(input)
            .Concat(skippedByScan.Keys)
            .Concat(openHeld.Keys)
            .Concat(approved.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // Rejection keys seen in this pull (the sweep keeps exactly these) and the rejections to write.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var rejectedNow = new Dictionary<string, (string? Sha, string Reason)>(StringComparer.Ordinal);
        foreach (var rejected in view.Rejected)
        {
            keys.Add(rejected.Key);
            rejectedNow[rejected.Key] = (rejected.Sha, rejected.Reason);
        }
        var listingByKey = view.Rejected.GroupBy(r => r.NamesId).Where(g => g.Key is not null).ToDictionary(g => g.Key!, g => g.ToList(), StringComparer.Ordinal);

        // The held-delete items the preview and the loop decide with: open and approved (milestone 1b-iii, D4; an id has at
        // most one held-delete item).
        var held = new Dictionary<string, HeldItem>(openHeld, StringComparer.Ordinal);
        foreach (var (id, item) in approved)
            held[id] = item;

        // The delete guard (D1 to D4). The blobs the preview read are taken by the loop, so no blob is read twice.
        var blobs = new Dictionary<string, GitOutcome<byte[]>>(StringComparer.Ordinal);
        var activeRows = rows.Values.Count(r => r.State.IsActive);
        var preview = await PreviewDeletions(ids, skippedByScan, view, localDeletions, rows, held, fetch.Proven, activeRows, blobs, cancellationToken)
            .ConfigureAwait(false);

        int inserted = 0, updated = 0, dropped = 0, unchanged = 0, notAttempted = 0;
        var postponed = new List<PostponedEntry>();
        var deferred = new List<DeferredEntry>();
        var failed = new List<FailedEntry>();
        var skipped = new List<SkippedItem>(scan.Skipped);
        var colder = new List<string>();
        var deleted = new List<string>();
        var heldNow = new List<HeldEntry>();
        var closedHeld = new List<string>();
        var notes = new List<NoteEntry>();
        var pushDeletions = new List<string>(); // in ordinal id order, each id once, as the ids are visited
        var repushMarked = new List<RepushEntry>(); // likewise
        var stopped = false;
        var everyIdProcessed = true; // false on a stop or a cancellation
        var blobReadFailed = false;
        var gitCancelled = false; // a git call that reported the git client's Cancelled kind
        var recordedEveryIssue = true;

        for (var i = 0; i < ids.Count; i++)
        {
            if (stopped)
            {
                notAttempted++; // a busy database: every later entry would wait the timeout in turn
                continue;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                everyIdProcessed = false;
                break;
            }

            var id = ids[i];
            if (skippedByScan.TryGetValue(id, out var scanReason))
            {
                // Decision 10: not applied for any group, no deferral; a path of the id that was rejected keeps its row.
                if (listingByKey.TryGetValue(id, out var named))
                    foreach (var path in named)
                        keys.Add(path.Key);
                foreach (var accepted in view.Accepted.TryGetValue(id, out var acc) ? new[] { acc } : [])
                    if (rejections.ContainsKey(SyncApply.rejectionKey(accepted.Path)))
                        keys.Add(SyncApply.rejectionKey(accepted.Path));
                if (WriteSkipIssue(id, ScanSkippedPrefix, scanReason) is { } skipDeferred)
                {
                    deferred.Add(new DeferredEntry(id, skipDeferred.Reason));
                    if (skipDeferred.Busy)
                        stopped = true;
                }
                else if (approved.ContainsKey(id) && RemoveApprovedItem(id) is { } removeDeferred)
                {
                    deferred.Add(new DeferredEntry(id, removeDeferred.Reason));
                    if (removeDeferred.Busy)
                        stopped = true;
                }
                else
                {
                    // Milestone 1b-iii, D4: every skip reason means an entry exists under the id, so its approved item
                    // was removed above and nothing is deleted.
                    if (approved.ContainsKey(id))
                        closedHeld.Add(id);
                    HoldIfUnprovenCandidate();
                }
                continue;
            }

            if (view.Present.Contains(id) && !view.Accepted.ContainsKey(id))
            {
                // A rejected path of this id: never decided, never read as a deletion. A local deletion of it is skipped,
                // not postponed, so one bad record cannot stop every push (milestone 1b-ii, D13).
                if (localDeletions.Contains(id))
                    skipped.Add(new SkippedItem(id, null, RecordRejected));
                else if (approved.ContainsKey(id))
                    RemoveApprovedOfPresentEntry();
                continue;
            }

            ParsedRecord? theirs = null;
            string? sha = null;
            if (view.Accepted.TryGetValue(id, out var record))
            {
                sha = record.Sha;
                var blob = blobs.Remove(id, out var previewed)
                    ? previewed
                    : await _client.CatBlobAsync(record.Sha, SyncValidation.maxFileBytes, cancellationToken).ConfigureAwait(false);
                if (!blob.IsOk)
                {
                    if (blob.Failure.Failure == SyncGit.FailureKind.Cancelled || cancellationToken.IsCancellationRequested)
                    {
                        everyIdProcessed = false;
                        gitCancelled = true;
                        break;
                    }
                    blobReadFailed = true;
                    var detail = "the record could not be read: " + OneLine(string.IsNullOrWhiteSpace(blob.Failure.StderrText) ? "git failed" : blob.Failure.StderrText);
                    Tally(RecordFailure(id, sha, new EntryResult.Failed(detail)));
                    continue;
                }
                var parsed = RecordReader.Parse(blob.Value, record.Path);
                if (parsed.Parsed is null)
                {
                    // Accepted by the listing, invalid as a record: a rejection that names this id.
                    var key = SyncApply.rejectionKey(record.Path);
                    keys.Add(key);
                    rejectedNow[key] = (sha, OneLine(SyncValidation.RejectModule.describe(parsed.Rejected!)));
                    if (localDeletions.Contains(id))
                        skipped.Add(new SkippedItem(id, null, RecordInvalid)); // D13, as for a rejected path
                    else if (approved.ContainsKey(id))
                        RemoveApprovedOfPresentEntry();
                    continue;
                }
                theirs = parsed.Parsed;
            }

            // A deletion decided here for an id the preview did not count (the entry was deleted after the scan, or changed
            // concurrently) is carried out under this untripped verdict: an accepted race (D1).
            Tally(ApplyOne(id, theirs, fetch.Proven, held.GetValueOrDefault(id), sha, preview.Verdict));

            void Tally(EntryResult result)
            {
                switch (result)
                {
                    case EntryResult.Carried c:
                        if (c.Kind == CarriedKind.Inserted) inserted++;
                        else if (c.Kind == CarriedKind.Updated) updated++;
                        else if (c.Kind == CarriedKind.Unchanged) unchanged++;
                        else dropped++;
                        if (c.Colder) colder.Add(id);
                        if (c.Effects.Deleted) deleted.Add(id);
                        if (c.Effects.PushDeletion) pushDeletions.Add(id);
                        if (c.Effects.Held is { } h) heldNow.Add(h);
                        if (c.Effects.Closed) closedHeld.Add(id);
                        notes.AddRange(c.Effects.Notes.Select(n => new NoteEntry(id, n)));
                        if (c.Effects.Repush is { } marked) repushMarked.Add(new RepushEntry(id, marked));
                        break;
                    case EntryResult.NoOp n:
                        unchanged++;
                        if (n.Repush is { } kept) repushMarked.Add(new RepushEntry(id, kept));
                        break;
                    case EntryResult.Postponed p:
                        postponed.Add(new PostponedEntry(id, p.Reason));
                        break;
                    case EntryResult.Deferred d:
                        deferred.Add(new DeferredEntry(id, d.Reason));
                        if (d.Busy)
                            stopped = true;
                        break;
                    case EntryResult.Skipped s:
                        skipped.Add(new SkippedItem(id, null, s.Reason));
                        HoldIfUnprovenCandidate();
                        break;
                    case EntryResult.Failed f:
                        failed.Add(new FailedEntry(id, f.Attempts, f.Detail));
                        HoldIfUnprovenCandidate();
                        break;
                }
            }

            // Milestone 1b-iii, D4: an id whose record is rejected or invalid is never decided, so an approved item of an
            // entry that is present again (not a local deletion) is removed here, as for a scan-skipped id, or deferred.
            void RemoveApprovedOfPresentEntry()
            {
                if (RemoveApprovedItem(id) is { } notRemoved)
                {
                    deferred.Add(new DeferredEntry(id, notRemoved.Reason));
                    if (notRemoved.Busy)
                        stopped = true;
                }
                else
                {
                    closedHeld.Add(id);
                }
            }

            // An entry that ended failed, skipped or scan-skipped wrote no hold of its own: in an unproven pull a deletion
            // candidate is held here, or deferred so that L stays (the umbrella's "a missing file is never proof").
            void HoldIfUnprovenCandidate()
            {
                if (fetch.Proven || UnprovenCandidate(id, view, localDeletions, rows) is not { } candidate)
                    return;
                var (fallback, notHeld) = HoldUnprovenCandidate(id, candidate.Direction, candidate.RecordSha);
                if (fallback is not null)
                    heldNow.Add(fallback);
                if (notHeld is not null)
                {
                    deferred.Add(new DeferredEntry(id, notHeld.Reason));
                    if (notHeld.Busy)
                        stopped = true;
                }
            }
        }

        if (stopped)
            everyIdProcessed = false;

        // Rejections are recorded whatever happened to the entries; an identical row is not rewritten.
        var cancelled = cancellationToken.IsCancellationRequested || gitCancelled;
        var rejectionsWritten = cancelled || WriteRejections(rejectedNow, rejections); // a cancelled pull writes no issue
        if (!rejectionsWritten)
        {
            recordedEveryIssue = false;
            deferred.Add(new DeferredEntry("(rejected records)", "a rejected record could not be recorded"));
        }

        // The sweep: only after a pull that processed every id, so an unread invalid record is never forgotten.
        if (everyIdProcessed && !cancelled && !blobReadFailed && recordedEveryIssue)
            SweepRejections(keys);

        var complete = everyIdProcessed && !cancelled && postponed.Count == 0 && deferred.Count == 0 && recordedEveryIssue;
        if (complete && !(fetch.Proven && string.Equals(fetch.Last, fetch.Head, StringComparison.Ordinal)))
        {
            try
            {
                SyncSql.SetMeta(conn, SyncIdentity.MetaKeys.LastSha, fetch.Head);
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                return new ApplyOutcome.Failed("the last-synced commit could not be recorded: " + OneLine(ex.Message));
            }
            var ref_ = await _client.UpdateLastSyncedAsync(fetch.Head, cancellationToken).ConfigureAwait(false);
            if (!ref_.IsOk)
                return new ApplyOutcome.Failed("the last-synced reference could not be written: " + OneLine(string.IsNullOrWhiteSpace(ref_.Failure.StderrText) ? "git failed" : ref_.Failure.StderrText));
        }

        return new ApplyOutcome.Done(new ApplySummary(
            inserted, updated, dropped, unchanged, notAttempted, complete, fetch.Head, fetch.Proven,
            postponed, deferred, failed,
            rejectedNow.Select(p => new RejectedItem(p.Key, p.Value.Reason)).OrderBy(r => r.Key, StringComparer.Ordinal).ToList(),
            skipped, colder, deleted, heldNow, preview.Trips, pushDeletions, closedHeld, notes, repushMarked));
    }

    /// <summary>
    /// The direction and the record's blob id of an id that is a deletion candidate of an unproven pull (D1 and D5), read
    /// from what the pull read at its start: a row that is <c>active</c> with a base, and either no path of the id in
    /// <c>R</c> (a vanished record: <c>remote</c>, no blob) or an accepted record of an id the scan reports as a local
    /// deletion (<c>local</c>, the record's blob). Null for anything else: a row with no base (never deleted by a pull), a
    /// <c>forgotten</c> or <c>left</c> row, a rejected path, or an accepted record of an id that is not a local deletion.
    /// Whether the entry exists is checked by <see cref="HoldUnprovenCandidate"/> inside its transaction.
    /// </summary>
    private static (SyncMerge.Direction Direction, string? RecordSha)? UnprovenCandidate(
        string id, TreeView view, HashSet<string> localDeletions, Dictionary<string, GitSyncRow> rows)
    {
        if (!rows.TryGetValue(id, out var row) || !row.State.IsActive || row.BaseJson is null)
            return null;
        if (view.Accepted.TryGetValue(id, out var record))
            return localDeletions.Contains(id) ? (SyncMerge.Direction.LocalDeletion, record.Sha) : null;
        if (view.Present.Contains(id))
            return null; // a rejected path: never a candidate
        return (SyncMerge.Direction.RemoteDeletion, null);
    }

    /// <summary>The guard's verdict for one pull and the directions it tripped, each with its count and the limit.</summary>
    private sealed record DeletionPreview(DeleteGuardVerdict Verdict, IReadOnlyList<GuardTrip> Trips);

    /// <summary>
    /// The deletion preview (milestone 1b-ii, D1 to D4), before the first entry transaction. The candidates are the visited
    /// ids with no accepted record and no rejected path, and the local deletions with an accepted record that parses;
    /// never a scan-skipped id. Each is decided with the same input as the real decision (its held-delete item, open or
    /// approved, included, so an entry with an open one is left alone and not counted) and nothing is written.
    /// <c>DeleteEntry</c> counts as remote, <c>PushDeletion</c> as local, except a <c>PushDeletion</c> an approved item
    /// answers (milestone 1b-iii, D4); a hold that the decision itself makes is not counted, nor is a candidate that
    /// cannot be decided (the real decision reports it). A direction trips when its count is greater than
    /// <c>max(10, activeRows * percent / 100)</c>. The blob reads (failed ones included) are left in <paramref name="blobs"/>
    /// for the loop.
    /// </summary>
    private async Task<DeletionPreview> PreviewDeletions(
        IReadOnlyList<string> ids,
        Dictionary<string, string> skippedByScan,
        TreeView view,
        HashSet<string> localDeletions,
        Dictionary<string, GitSyncRow> rows,
        Dictionary<string, HeldItem> held,
        bool proven,
        int activeRows,
        Dictionary<string, GitOutcome<byte[]>> blobs,
        CancellationToken cancellationToken)
    {
        int remote = 0, local = 0;
        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
                break; // the loop carries nothing out after a cancellation
            if (skippedByScan.ContainsKey(id))
                continue;

            ParsedRecord? theirs = null;
            string? sha = null;
            if (view.Accepted.TryGetValue(id, out var record))
            {
                if (!localDeletions.Contains(id))
                    continue;
                sha = record.Sha;
                var blob = await _client.CatBlobAsync(record.Sha, SyncValidation.maxFileBytes, cancellationToken).ConfigureAwait(false);
                blobs[id] = blob;
                if (!blob.IsOk)
                    continue;
                theirs = RecordReader.Parse(blob.Value, record.Path).Parsed;
                if (theirs is null)
                    continue; // an invalid record: skipped by the loop (D13)
            }
            else if (view.Present.Contains(id))
            {
                continue; // a rejected path: never decided
            }

            if (PreviewDecision(id, rows.GetValueOrDefault(id), theirs, sha, proven, held.GetValueOrDefault(id)) is not { } decided)
                continue;
            var entry = decided.Decision.Entry;
            if (entry.IsDeleteEntry)
                remote++;
            else if (entry.IsPushDeletion && decided.Approved?.IsAsApproved != true)
                local++;
        }

        var limit = SyncDeletion.guardLimit(_options.DeleteGuardPercent, activeRows);
        var remoteTripped = SyncDeletion.guardTrips(remote, limit);
        var localTripped = SyncDeletion.guardTrips(local, limit);
        var trips = new List<GuardTrip>();
        if (remoteTripped)
            trips.Add(new GuardTrip(SyncDeletion.directionName(SyncMerge.Direction.RemoteDeletion), remote, limit));
        if (localTripped)
            trips.Add(new GuardTrip(SyncDeletion.directionName(SyncMerge.Direction.LocalDeletion), local, limit));
        return new DeletionPreview(new DeleteGuardVerdict(remoteTripped, localTripped), trips);
    }

    /// <summary>
    /// The decision the entry would get and the use of its approved item (<see cref="Decide"/>), read without writing
    /// anything; null when it cannot be decided (an id that is not eligible, a local entry that cannot be read or is in two
    /// tiers, a base that does not parse). <paramref name="blobSha"/> is the record's blob id.
    /// </summary>
    private (SyncMerge.MergeDecision Decision, SyncDeletion.ApprovedUse? Approved)? PreviewDecision(
        string id, GitSyncRow? row, ParsedRecord? theirs, string? blobSha, bool proven, HeldItem? held)
    {
        if (!SyncValidation.isEligibleId(id))
            return null;
        LocalRead read;
        try
        {
            read = ReadLocal(id);
        }
        catch (FormatException)
        {
            return null;
        }
        if (read is not (LocalRead.Present or LocalRead.Absent))
            return null;
        ParsedRecord? oldBase = null;
        if (row?.BaseJson is { } baseText)
        {
            oldBase = ApplyPlanning.ParseBase(id, baseText).Parsed;
            if (oldBase is null)
                return null;
        }
        return Decide(row, oldBase, theirs, blobSha, (read as LocalRead.Present)?.View, proven, held);
    }

    /// <summary>Writes a <c>rejected-record</c> row for each rejection that has none or a different one. False when it could not be written.</summary>
    private bool WriteRejections(Dictionary<string, (string? Sha, string Reason)> rejectedNow, Dictionary<string, IssueRow> open)
    {
        var pending = rejectedNow
            .Where(p => !open.TryGetValue(p.Key, out var row) || row.RecordHash != p.Value.Sha || row.Detail != p.Value.Reason)
            .ToList();
        if (pending.Count == 0)
            return true;
        try
        {
            using var tx = _store.BeginImmediateTransaction();
            TransactionCount++;
            tx.BeforeStep = step => BeforeEntryStep?.Invoke("(rejected records)", step);
            Step("(rejected records)", "write-issue");
            foreach (var (key, (sha, reason)) in pending)
                GitSyncIssues.Upsert(tx.Connection, key, GitSyncIssues.RejectedRecord, sha, 0, reason, tx.Transaction);
            tx.Commit();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Removes the <c>rejected-record</c> rows whose key was not seen in this pull. A failure is ignored until the next pull.</summary>
    private void SweepRejections(HashSet<string> keys)
    {
        try
        {
            var open = GitSyncIssues.ReadOpen(_store.Connection, GitSyncIssues.RejectedRecord);
            var stale = open.Keys.Where(k => !keys.Contains(k)).ToList();
            if (stale.Count == 0)
                return;
            using var tx = _store.BeginImmediateTransaction();
            TransactionCount++;
            foreach (var key in stale)
                GitSyncIssues.Delete(tx.Connection, key, GitSyncIssues.RejectedRecord, tx.Transaction);
            tx.Commit();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
        }
    }
}
