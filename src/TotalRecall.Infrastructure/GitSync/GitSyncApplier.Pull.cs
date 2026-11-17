using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncApplier
{
    /// <summary>
    /// Applies the fetched repository (decisions 4 to 6, 10 and 14 to 16): reads the tree at <c>R</c>, builds the visit
    /// set, decides each entry in ordinal id order (one transaction each), records the rejections, sweeps the cleared
    /// ones, and advances <c>L</c> only for a complete pull. Never throws for a bad record, a busy database or a failed
    /// entry; the token stops it between entries.
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
        try
        {
            rows = GitSyncRows.ReadAll(conn);
            failures = GitSyncIssues.ReadOpen(conn, GitSyncIssues.ApplyFailed);
            rejections = GitSyncIssues.ReadOpen(conn, GitSyncIssues.RejectedRecord);
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
            SetModule.OfSeq(localDeletions));
        var ids = SyncApply.visitSet(input)
            .Concat(skippedByScan.Keys)
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

        int inserted = 0, updated = 0, dropped = 0, unchanged = 0, notAttempted = 0;
        var postponed = new List<PostponedEntry>();
        var deferred = new List<DeferredEntry>();
        var failed = new List<FailedEntry>();
        var skipped = new List<SkippedItem>(scan.Skipped);
        var colder = new List<string>();
        var stopped = false;
        var everyIdProcessed = true; // false on a stop or a cancellation
        var blobReadFailed = false;
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
                continue;
            }

            if (view.Present.Contains(id) && !view.Accepted.ContainsKey(id))
            {
                // A rejected path of this id: never decided, never read as a deletion.
                if (localDeletions.Contains(id))
                    postponed.Add(new PostponedEntry(id, SyncApply.reasonName(SyncApply.PostponeReason.Deletion)));
                continue;
            }

            ParsedRecord? theirs = null;
            string? sha = null;
            if (view.Accepted.TryGetValue(id, out var record))
            {
                sha = record.Sha;
                var blob = await _client.CatBlobAsync(record.Sha, SyncValidation.maxFileBytes, cancellationToken).ConfigureAwait(false);
                if (!blob.IsOk)
                {
                    if (blob.Failure.Failure == SyncGit.FailureKind.Cancelled || cancellationToken.IsCancellationRequested)
                    {
                        everyIdProcessed = false;
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
                        postponed.Add(new PostponedEntry(id, SyncApply.reasonName(SyncApply.PostponeReason.Deletion)));
                    continue;
                }
                theirs = parsed.Parsed;
            }

            Tally(ApplyOne(id, theirs, fetch.Proven, sha));

            void Tally(EntryResult result)
            {
                switch (result)
                {
                    case EntryResult.Carried c:
                        if (c.Kind == CarriedKind.Inserted) inserted++;
                        else if (c.Kind == CarriedKind.Updated) updated++;
                        else dropped++;
                        if (c.Colder) colder.Add(id);
                        break;
                    case EntryResult.NoOp:
                        unchanged++;
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
                        break;
                    case EntryResult.Failed f:
                        failed.Add(new FailedEntry(id, f.Attempts, f.Detail));
                        break;
                }
            }
        }

        if (stopped)
            everyIdProcessed = false;

        // Rejections are recorded whatever happened to the entries; an identical row is not rewritten.
        var cancelled = cancellationToken.IsCancellationRequested;
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
            skipped, colder));
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
