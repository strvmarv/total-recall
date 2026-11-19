using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncPusher
{
    /// <summary>
    /// Decision 12: one immediate transaction that makes the rows say what is now in the repository, then (after a
    /// commit was pushed) <c>_meta.git_sync_last_sha</c>, then the protective ref. Returns
    /// the failure, or null. <paramref name="commit"/> is null when nothing was pushed (only the unchanged entries are
    /// finalized then, and <c>L</c> does not move). After a pushed commit each removed id's row becomes <c>forgotten</c>
    /// (milestone 1b-ii, D14), in the same transaction: see <see cref="Forget"/>. The token is never consulted: the steps
    /// are local and short.
    /// </summary>
    internal async Task<PushOutcome.Failed?> FinalizeAsync(PlanResult.Planned plan, string? commit)
    {
        var published = commit is null ? SyncPush.Published.No : SyncPush.Published.Yes;
        var finalized = plan.Unchanged.Select(u => (u.Id, Text: u.BaseText, u.Pushed))
            .Concat(commit is null ? [] : plan.Entries.Select(e => (e.Id, Text: StrictUtf8.GetString(e.Bytes), e.Pushed)))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();
        if (finalized.Count == 0 && commit is null)
            return null;

        SqliteStoreTransaction tx;
        try
        {
            tx = _store.BeginImmediateTransaction();
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            return Fail("Finalize", ex is SqliteException busy && SyncSql.IsBusy(busy) ? "the database is busy" : ex.Message, published);
        }
        TransactionCount++;
        using (tx)
        {
            var phase = "Finalize";
            try
            {
                foreach (var (id, text, pushed) in finalized)
                {
                    Step(id, "finalize");
                    var row = GitSyncRows.Read(tx.Connection, id, tx.Transaction);
                    if (row is null)
                        continue;
                    var pending = new SyncMerge.GroupStamps(Opt(row.ContentStamp), Opt(row.StickyStamp), Opt(row.TierStamp));
                    var after = SyncPush.afterPush(pending, Opt(row.PushingStamps), pushed);
                    var ownMark = row.BlockedReason is { } reason && SyncPush.isWriterReason(reason);
                    GitSyncRows.Write(tx.Connection, row with
                    {
                        BaseJson = text,
                        ContentStamp = after.Pending.Content?.Value,
                        StickyStamp = after.Pending.Sticky?.Value,
                        TierStamp = after.Pending.Tier?.Value,
                        PushingStamps = after.PushingText?.Value,
                        BlockedReason = ownMark ? null : row.BlockedReason,
                        BlockedHash = ownMark ? null : row.BlockedHash,
                    }, tx.Transaction);
                }

                if (commit is not null)
                {
                    foreach (var id in plan.Removals)
                        Forget(tx, id);
                    phase = "Meta";
                    Step("", "meta");
                    SyncSql.SetMeta(tx.Connection, SyncIdentity.MetaKeys.LastSha, commit, tx.Transaction);
                }
                tx.Commit();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Fail(phase, ex.Message, published);
            }
        }

        if (commit is null)
            return null;
        try
        {
            Step("", "ref");
            var updated = await _client.UpdateLastSyncedAsync(commit, CancellationToken.None).ConfigureAwait(false);
            if (!updated.IsOk)
            {
                var text = string.IsNullOrWhiteSpace(updated.Failure.StderrText) ? "git failed" : updated.Failure.StderrText;
                return Fail("Ref", text, SyncPush.Published.Yes);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Fail("Ref", ex.Message, SyncPush.Published.Yes);
        }
        return null;
    }

    /// <summary>
    /// D14: the row of a removed record says the memory is forgotten. Only a row that is still <c>active</c> and whose
    /// memory does not exist (an entry with the id that came back since the scan is never marked) is written: state
    /// <c>forgotten</c>, the base kept, the pending stamps, <c>pushing_stamps</c> and <c>repush_groups</c> cleared, every
    /// other column as it was. Anything else is left untouched (the next scan and pull see it as it is).
    /// </summary>
    private void Forget(SqliteStoreTransaction tx, string id)
    {
        Step(id, "forget");
        var row = GitSyncRows.Read(tx.Connection, id, tx.Transaction);
        if (row is null || row.State != SyncMerge.RowState.Active)
            return;
        var read = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
        if (read is not LocalRead.Absent)
            return;
        GitSyncRows.Write(tx.Connection, row with
        {
            State = SyncMerge.RowState.Forgotten,
            ContentStamp = null,
            StickyStamp = null,
            TierStamp = null,
            PushingStamps = null,
            RepushGroups = null,
        }, tx.Transaction);
    }
}
