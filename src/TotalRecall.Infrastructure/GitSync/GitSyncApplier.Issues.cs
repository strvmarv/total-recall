using TotalRecall.Core;
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
        if (!SyncValidation.isEligibleId(id))
            return failed;
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
