using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>What the push knows about one candidate row after the clone and the tree were read (decisions 3 to 5).</summary>
internal sealed record CandidateInfo(
    GitSyncRow Row,
    ParsedRecord? Base,
    ParsedRecord? Record,
    byte[]? RecordBytes,
    bool RecordUnreadable,
    bool BaseUnreadable);

/// <summary>
/// The clone, the tree at <c>L</c>, every row and the candidates with the exclusions already decided. <see cref="Removals"/>
/// are the local deletions whose record this push removes (milestone 1b-ii, D14), ordinal; <see cref="NotRemovable"/> the
/// other ids the apply listed for removal, ordinal, which are left alone and reported. <see cref="Held"/> are the open
/// <c>held-delete</c> items by entry id, read when preparing (D17): a candidate with one is excluded as <c>held-delete</c>.
/// </summary>
internal sealed record PushPrepared(
    string Head,
    TreeView Tree,
    IReadOnlyDictionary<string, GitSyncRow> Rows,
    IReadOnlyDictionary<string, CandidateInfo> Candidates,
    IReadOnlyDictionary<string, SyncPush.Exclusion> Excluded,
    IReadOnlyList<string> ListedPaths,
    IReadOnlySet<string> Deleted,
    IReadOnlyList<string> Removals,
    IReadOnlyList<string> NotRemovable,
    IReadOnlyDictionary<string, HeldItem> Held);

internal abstract record PrepareResult
{
    private PrepareResult() { }

    public sealed record Stop(PushOutcome Outcome) : PrepareResult;

    public sealed record Ready(PushPrepared Value) : PrepareResult;
}

public sealed partial class GitSyncPusher
{
    /// <summary>The attributes that switch every filter and conversion off, whatever the repository tracks (decision 10).</summary>
    internal const string InfoAttributes = "* -text -eol -ident -filter -working-tree-encoding\n";

    private static bool IsSha(string? text) =>
        text is { Length: 40 or 64 } && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static FSharpOption<T>? Opt<T>(T? value) where T : class => value is null ? null : FSharpOption<T>.Some(value);

    /// <summary>
    /// Decisions 1 to 5: readiness, the reset of the clone (attributes file first), the tree at <c>L</c>, and the
    /// candidates with their exclusions; and the removals of milestone 1b-ii (D14): each id of
    /// <see cref="ApplySummary.PushDeletions"/> that is still a local deletion of the scan and whose accepted record at
    /// <c>L</c> nothing is in the way of. A candidate with an open held item is excluded (D17); one with a base, no
    /// accepted record and a pending content stamp is not excluded: the plan writes it back from its base (D15, D18).
    /// Writes nothing to the store.
    /// </summary>
    internal async Task<PrepareResult> PrepareAsync(ApplySummary apply, ScanSummary scan, CancellationToken ct)
    {
        try
        {
            return await PrepareCoreAsync(apply, scan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PrepareResult.Stop(Cancelled());
        }
    }

    private async Task<PrepareResult> PrepareCoreAsync(ApplySummary apply, ScanSummary scan, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return new PrepareResult.Stop(Cancelled());

        // 1. ready: a complete pull, and L is the pull's head
        if (!apply.Complete)
            return new PrepareResult.Stop(new PushOutcome.NotReady("the pull did not complete"));
        string? last;
        try
        {
            last = _store.GetMeta(SyncIdentity.MetaKeys.LastSha);
        }
        catch (SqliteException ex)
        {
            return new PrepareResult.Stop(Fail("Store", "the last synced commit could not be read: " + ex.Message, SyncPush.Published.No));
        }
        if (!IsSha(apply.Head) || !string.Equals(last, apply.Head, StringComparison.Ordinal))
            return new PrepareResult.Stop(new PushOutcome.NotReady("the last synced commit is not the pull's head"));

        // 2. the clone: the attributes file first, then reset and clean, then HEAD must be L
        var reset = await ResetCloneAsync(apply.Head, ct).ConfigureAwait(false);
        if (reset is not null)
            return new PrepareResult.Stop(reset);
        if (ct.IsCancellationRequested)
            return new PrepareResult.Stop(Cancelled());

        // 3. the tree at L, as git objects
        var read = await PullTree.ReadAsync(_client, apply.Head, last: null, unproven: true, ct).ConfigureAwait(false);
        if (read is TreeRead.Failed treeFailed)
        {
            return new PrepareResult.Stop(treeFailed.Cancelled
                ? Cancelled()
                : Fail("Clone", treeFailed.Reason, SyncPush.Published.No));
        }
        var view = ((TreeRead.Ok)read).View;

        // the rows, the open apply failures and the open held items
        Dictionary<string, GitSyncRow> rows;
        Dictionary<string, IssueRow> failures;
        Dictionary<string, HeldItem> held;
        try
        {
            rows = GitSyncRows.ReadAll(_store.Connection);
            failures = GitSyncIssues.ReadOpen(_store.Connection, GitSyncIssues.ApplyFailed);
            held = GitSyncReviews.ReadOpenHeld(_store.Connection);
        }
        catch (Exception ex) when (ex is FormatException or SqliteException or InvalidOperationException)
        {
            return new PrepareResult.Stop(Fail("Store", "the sync rows could not be read: " + ex.Message, SyncPush.Published.No));
        }

        var skipped = scan.Skipped.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var deleted = scan.LocalDeletions.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var listed = view.Accepted.Values.Select(a => a.Path).Concat(view.Rejected.Select(r => r.Path)).ToList();
        var occupation = new PathIndex(listed);
        var rejectedIds = view.Rejected.Where(r => r.NamesId is not null).Select(r => r.NamesId!).ToHashSet(StringComparer.Ordinal);

        var candidates = new SortedDictionary<string, CandidateInfo>(StringComparer.Ordinal);
        var excluded = new SortedDictionary<string, SyncPush.Exclusion>(StringComparer.Ordinal);
        foreach (var (id, row) in rows.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (deleted.Contains(id))
                continue;

            ParsedRecord? parsedBase = null;
            var baseUnreadable = false;
            if (row.BaseJson is { } baseText)
            {
                parsedBase = SyncValidation.isEligibleId(id)
                    ? RecordReader.Parse(Encoding.UTF8.GetBytes(baseText), SyncValidation.recordPath(id)).Parsed
                    : null;
                baseUnreadable = parsedBase is null;
            }

            var pending = new SyncMerge.GroupStamps(Opt(row.ContentStamp), Opt(row.StickyStamp), Opt(row.TierStamp));
            var facts = new SyncPush.RowFacts(
                row.State,
                row.BaseJson is not null,
                parsedBase is null ? null : FSharpOption<string>.Some(SyncScan.tagsHash(parsedBase.Record.Tags)),
                row.ScannedTagsHash is null ? null : FSharpOption<string>.Some(row.ScannedTagsHash),
                pending);
            if (!SyncPush.isCandidate(facts))
                continue;

            var accepted = view.Accepted.TryGetValue(id, out var acceptedRecord);
            var exclusionFacts = new SyncPush.ExclusionFacts(
                hasIssueOrSkip: failures.ContainsKey(id) || skipped.Contains(id),
                rejectedNamesId: rejectedIds.Contains(id),
                heldDelete: held.ContainsKey(id),
                hasBase: row.BaseJson is not null,
                recordAccepted: accepted,
                pendingContent: row.ContentStamp is not null,
                recordUnreadable: false,
                baseUnreadable: baseUnreadable,
                pathOccupied: occupation.Occupied(id),
                blockedElsewhere: row.BlockedReason is { } reason && !SyncPush.isWriterReason(reason),
                entryUnreadable: false);

            var found = SyncPush.exclusion(exclusionFacts);
            ParsedRecord? record = null;
            byte[]? bytes = null;
            var recordUnreadable = false;
            var needsRecord = accepted && row.BaseJson is not null
                && (found is null || found.Value.IsRecordUnreadable || found.Value.IsUnreadableBase
                    || found.Value.IsPathOccupied || found.Value.IsBlockedElsewhere || found.Value.IsUnreadableEntry);
            if (needsRecord)
            {
                var blob = await _client.CatBlobAsync(acceptedRecord!.Sha, RecordReader.MaxFileBytes, ct).ConfigureAwait(false);
                if (!blob.IsOk)
                {
                    if (blob.Failure.Failure == SyncGit.FailureKind.Cancelled)
                        return new PrepareResult.Stop(Cancelled());
                    recordUnreadable = true;
                }
                else
                {
                    bytes = blob.Value;
                    record = RecordReader.Parse(bytes, acceptedRecord.Path).Parsed;
                    recordUnreadable = record is null;
                }
                if (recordUnreadable)
                    found = SyncPush.exclusion(new SyncPush.ExclusionFacts(
                        exclusionFacts.HasIssueOrSkip, exclusionFacts.RejectedNamesId, exclusionFacts.HeldDelete, exclusionFacts.HasBase,
                        exclusionFacts.RecordAccepted, exclusionFacts.PendingContent, true, exclusionFacts.BaseUnreadable,
                        exclusionFacts.PathOccupied, exclusionFacts.BlockedElsewhere, exclusionFacts.EntryUnreadable));
            }

            candidates[id] = new CandidateInfo(row, parsedBase, record, bytes, recordUnreadable, baseUnreadable);
            if (found is not null)
                excluded[id] = found.Value;
        }

        // the removals: the apply decided them, the tree at L must still allow them
        var removals = new List<string>();
        var notRemovable = new List<string>();
        foreach (var id in apply.PushDeletions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var removable = SyncPush.removable(
                deleted.Contains(id),
                view.Accepted.ContainsKey(id),
                occupation.Occupied(id),
                rejectedIds.Contains(id));
            (removable ? removals : notRemovable).Add(id);
        }

        return new PrepareResult.Ready(new PushPrepared(apply.Head, view, rows, candidates, excluded, listed, deleted, removals, notRemovable, held));
    }

    /// <summary>
    /// Decision 2: writes <c>.git/info/attributes</c> (every filter off), resets the clone to <c>L</c>, cleans it, and
    /// checks that <c>HEAD</c> is <c>L</c>. Returns the failure, or null.
    /// </summary>
    private async Task<PushOutcome?> ResetCloneAsync(string head, CancellationToken ct)
    {
        var gitDirectory = new DirectoryInfo(Path.Combine(_client.CloneDirectory, ".git"));
        if (!gitDirectory.Exists || gitDirectory.LinkTarget is not null || new DirectoryInfo(_client.CloneDirectory).LinkTarget is not null)
            return Fail("Clone", "the clone is missing or is not a plain git directory", SyncPush.Published.No);
        try
        {
            var info = Path.Combine(_client.CloneDirectory, ".git", "info");
            Directory.CreateDirectory(info);
            await File.WriteAllTextAsync(Path.Combine(info, "attributes"), InfoAttributes, new UTF8Encoding(false), ct).ConfigureAwait(false);
            // a push that was killed between the temporary write and the rename leaves its file behind (best effort)
            foreach (var stale in Directory.EnumerateFiles(gitDirectory.FullName, "tr-push-*.tmp"))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Clone", "the clone's attributes file could not be written: " + ex.Message, SyncPush.Published.No);
        }

        var reset = await _client.ResetHardAsync(SyncGit.Revision.NewSha(head), ct).ConfigureAwait(false);
        if (!reset.IsOk)
            return FailedFrom("Clone", reset.Failure, SyncPush.Published.No);
        var clean = await _client.CleanAsync(ct).ConfigureAwait(false);
        if (!clean.IsOk)
            return FailedFrom("Clone", clean.Failure, SyncPush.Published.No);
        var current = await _client.RevParseAsync(SyncGit.Revision.Head, ct).ConfigureAwait(false);
        if (!current.IsOk)
            return FailedFrom("Clone", current.Failure, SyncPush.Published.No);
        if (!string.Equals(current.Value, head, StringComparison.Ordinal))
            return Fail("Clone", "the clone's HEAD is not the last synced commit after the reset", SyncPush.Published.No);
        return null;
    }

    /// <summary>
    /// The listed paths indexed by the lower-case shard directory they sit in, so <c>path-occupied</c> compares an id with
    /// the few paths of its own shard and the blobs named <c>memories</c> instead of with the whole tree.
    /// </summary>
    private sealed class PathIndex
    {
        private readonly Dictionary<string, List<string>> _byShard = new(StringComparer.Ordinal);
        private readonly List<string> _roots = [];

        public PathIndex(IEnumerable<string> listed)
        {
            foreach (var path in listed)
            {
                var segments = path.Split('/');
                if (segments.Length == 1 && string.Equals(path, "memories", StringComparison.OrdinalIgnoreCase))
                    _roots.Add(path);
                else if (segments.Length >= 2 && string.Equals(segments[0], "memories", StringComparison.OrdinalIgnoreCase))
                {
                    var key = segments[1].ToUpperInvariant();
                    if (!_byShard.TryGetValue(key, out var list))
                        _byShard[key] = list = [];
                    list.Add(path);
                }
            }
        }

        public bool Occupied(string id)
        {
            if (!SyncValidation.isEligibleId(id))
                return false;
            var relevant = _roots.AsEnumerable();
            if (_byShard.TryGetValue(SyncValidation.shard(id).ToUpperInvariant(), out var same))
                relevant = relevant.Concat(same);
            return SyncPush.pathOccupied(id, relevant);
        }
    }
}
