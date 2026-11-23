using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncApplier
{
    internal const string ScanSkippedPrefix = "scan-skipped:";
    internal const string UnreadablePrefix = "unreadable:";

    private static bool IsSkipDetail(string? detail) =>
        detail is not null
        && (detail.StartsWith(ScanSkippedPrefix, StringComparison.Ordinal) || detail.StartsWith(UnreadablePrefix, StringComparison.Ordinal));

    /// <summary>
    /// A failure at the re-read (decision 12.1) or at the removal of the entry's issue (12.5) is a deferral (the lock
    /// that failed it would likely fail an issue write too); a failure in the store, row or import steps, or at the
    /// commit, is the entry's failure.
    /// </summary>
    private static EntryResult Classify(string phase, Exception ex) =>
        phase is "reread" or "issue"
            ? new EntryResult.Deferred(OneLine(ex.Message), Busy: ex is SqliteException s && IsBusy(s))
            : new EntryResult.Failed(OneLine(ex.Message));

    /// <summary>Writes the entry's <c>apply-failed</c> row in a transaction of its own (decision 15). A failure to write it is a deferral.</summary>
    private EntryResult RecordFailure(string id, string? blobSha, EntryResult.Failed failed)
    {
        try
        {
            using var tx = _store.BeginImmediateTransaction();
            TransactionCount++;
            Step(id, "write-issue");
            var existing = GitSyncIssues.Read(tx.Connection, id, GitSyncIssues.ApplyFailed, tx.Transaction);
            var attempts = existing is not null && !IsSkipDetail(existing.Detail) ? existing.Attempts + 1 : 1;
            GitSyncIssues.Upsert(tx.Connection, id, GitSyncIssues.ApplyFailed, blobSha, attempts, OneLine(failed.Detail), tx.Transaction);
            tx.Commit();
            return failed with { Attempts = attempts };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new EntryResult.Deferred("the failure could not be recorded: " + OneLine(ex.Message), Busy: ex is SqliteException s && IsBusy(s));
        }
    }

    /// <summary>
    /// The fallback hold of an unproven pull (milestone 1b-ii, D5 and D7; the umbrella's "a missing file is never proof"):
    /// a deletion candidate whose entry ended failed, skipped or scan-skipped got no hold from its own transaction (rolled
    /// back, or never opened), so it is held here, in a small transaction of its own opened after that rollback, as
    /// <see cref="RecordFailure"/> records the failure. Inside the transaction the row must still be <c>active</c> with a
    /// base, the entry must exist for a <paramref name="direction"/> of <c>remote</c> and be gone for <c>local</c>, and the
    /// id's item of any state must not be one the fallback keeps (<see cref="FallbackKeeps"/>; milestone 1b-iii, D10);
    /// otherwise nothing is written and both results are null. Any other item (an open one of the other direction, an
    /// approved one against a remote hold) is replaced. The item is open,
    /// with the cause <c>unproven</c>, <paramref name="recordSha"/> (the blob id of the record in <c>R</c>, null when it has
    /// none) and the applier clock. When it cannot be written (a busy lock or a failure) the result is a deferral, which
    /// holds <c>L</c> back.
    /// </summary>
    private (HeldEntry? Held, EntryResult.Deferred? Deferred) HoldUnprovenCandidate(string id, SyncMerge.Direction direction, string? recordSha)
    {
        try
        {
            using var tx = _store.BeginImmediateTransaction();
            TransactionCount++;
            var conn = tx.Connection;
            var row = GitSyncRows.Read(conn, id, tx.Transaction);
            if (row is null || !row.State.IsActive || row.BaseJson is null)
                return (null, null);
            var absent = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id)) is LocalRead.Absent;
            if (absent != direction.IsLocalDeletion)
                return (null, null); // a remote candidate whose entry is gone, or a local one whose entry is back
            if (FallbackKeeps(GitSyncReviews.ReadHeldItem(conn, id, tx.Transaction), direction))
                return (null, null);
            Step(id, "hold-unproven");
            var name = SyncDeletion.directionName(direction);
            var cause = SyncDeletion.holdCause(false, false, direction); // unproven
            GitSyncReviews.UpsertHeld(conn, new HeldItem(id, name, recordSha, cause, _nowMs()), tx.Transaction);
            tx.Commit();
            return (new HeldEntry(id, name, cause), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return (null, new EntryResult.Deferred(
                "an unproven deletion candidate could not be held: " + OneLine(ex.Message), Busy: ex is SqliteException s && IsBusy(s)));
        }
    }

    /// <summary>
    /// The fallback hold of <paramref name="direction"/> leaves <paramref name="existing"/> as it is (milestone 1b-iii,
    /// D10): an open item of the same direction (W6.3 and W8.2 protect it), and an approved item against a local hold (an
    /// approved item is of the local direction). An open item of the other direction is stale, and an approved item
    /// against a remote hold no longer answers the situation, so both are replaced.
    /// </summary>
    private static bool FallbackKeeps(HeldItem? existing, SyncMerge.Direction direction) =>
        existing switch
        {
            { State: "open" } => string.Equals(existing.Direction, SyncDeletion.directionName(direction), StringComparison.Ordinal),
            { State: "approved" } => direction.IsLocalDeletion,
            _ => false,
        };

    private EntryResult RecordSkip(string id, EntryResult.Skipped skipped)
    {
        if (!SyncValidation.isEligibleId(id))
            return skipped;
        return WriteSkipIssue(id, UnreadablePrefix, skipped.Reason) is { } deferred ? deferred : skipped;
    }

    /// <summary>
    /// Records that the entry was not applied: an <c>apply-failed</c> row whose detail begins with
    /// <paramref name="prefix"/>, with no attempt counted (decision 10). A real failure's row is never overwritten and an
    /// identical row is not rewritten. Null when done, a deferral when the row could not be written.
    /// </summary>
    internal EntryResult.Deferred? WriteSkipIssue(string id, string prefix, string reason)
    {
        var detail = OneLine(prefix + " " + reason);
        try
        {
            var seen = GitSyncIssues.Read(_store.Connection, id, GitSyncIssues.ApplyFailed);
            if (seen is not null && (!IsSkipDetail(seen.Detail) || seen.Detail == detail))
                return null;
            using var tx = _store.BeginImmediateTransaction();
            TransactionCount++;
            Step(id, "write-issue");
            var existing = GitSyncIssues.Read(tx.Connection, id, GitSyncIssues.ApplyFailed, tx.Transaction);
            if (existing is null || (IsSkipDetail(existing.Detail) && existing.Detail != detail))
                GitSyncIssues.Upsert(tx.Connection, id, GitSyncIssues.ApplyFailed, null, 0, detail, tx.Transaction);
            tx.Commit();
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new EntryResult.Deferred("the skip could not be recorded: " + OneLine(ex.Message), Busy: ex is SqliteException s && IsBusy(s));
        }
    }

    /// <summary>
    /// A decision that wrote nothing still removes the entry's <c>apply-failed</c> row, in one small transaction of its
    /// own and only when there is one (decisions 13 and 15). A busy lock or a failure there is a deferral.
    /// </summary>
    private EntryResult CleanUp(string id, EntryResult result)
    {
        try
        {
            if (GitSyncIssues.Read(_store.Connection, id, GitSyncIssues.ApplyFailed) is null)
                return result;
            using var tx = _store.BeginImmediateTransaction();
            TransactionCount++;
            tx.BeforeStep = step => BeforeEntryStep?.Invoke(id, step);
            Step(id, "issue");
            GitSyncIssues.Delete(tx.Connection, id, GitSyncIssues.ApplyFailed, tx.Transaction);
            Step(id, "commit");
            tx.Commit();
            return result;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new EntryResult.Deferred("the database is busy", Busy: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new EntryResult.Deferred("the issue could not be removed: " + OneLine(ex.Message), Busy: false);
        }
    }
}
