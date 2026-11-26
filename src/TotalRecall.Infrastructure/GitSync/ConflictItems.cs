using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>What to do with a conflict item row.</summary>
internal enum ConflictWriteAction
{
    /// <summary>Delete the row.</summary>
    Remove,

    /// <summary>Insert the row or replace its state, hash, machine and payload (<c>arrived_at</c> too when <see cref="ConflictWrite.Fresh"/>).</summary>
    Upsert,

    /// <summary>Set the state to <c>unseen</c> and the hash; change nothing else.</summary>
    MarkUnseen,
}

/// <summary>One planned write of a conflict item. A value, so two plans compare by value.</summary>
internal sealed record ConflictWrite(
    ConflictWriteAction Action, string? State, string? Hash, string? Machine, string? PayloadJson, bool Fresh);

/// <summary>A conflict item as the arrival rule sees it: parsed losers; <paramref name="Fresh"/> marks an item that does not exist yet.</summary>
internal sealed record ConflictState(string State, string Hash, string Machine, FSharpList<SyncConflict.Loser> Losers, bool Fresh);

/// <summary>
/// The one arrival rule of a conflict item (conflict review, D1 and D3): a loser is appended to the kept alternatives
/// (<see cref="SyncConflict.append"/>: no duplicate text, at most eight), and the item becomes <c>unseen</c> when what it
/// shows changed. Pure over values, plus <see cref="Append"/>, the writer-side use of the same rule. It names neither the
/// cycle nor the pusher.
/// </summary>
internal static class ConflictItems
{
    /// <summary>The item as a state, or null when there is none.</summary>
    public static ConflictState? Of(ConflictItem? item) =>
        item is null ? null : new ConflictState(item.State, item.RecordHash, item.Machine, GitSyncReviews.ReadConflictLosers(item.PayloadJson), Fresh: false);

    /// <summary>
    /// The state after <paramref name="loser"/> arrived and <paramref name="appliedText"/> is the stored entry's text. No item:
    /// <c>open</c> with that one loser, the loser's machine, and <c>Fresh</c>. An item: the loser appended; when the shown hash
    /// changed, <c>unseen</c>, the new hash and the machine of the last loser; when it did not, <paramref name="current"/> itself.
    /// </summary>
    public static ConflictState Arrive(ConflictState? current, SyncConflict.Loser loser, string appliedText)
    {
        if (current is null)
        {
            var one = ListModule.OfSeq([loser]);
            return new ConflictState("open", ShownHash(appliedText, one), loser.Machine, one, Fresh: true);
        }

        var losers = SyncConflict.append(current.Losers, loser);
        var hash = ShownHash(appliedText, losers);
        if (hash == current.Hash)
            return current;
        return new ConflictState("unseen", hash, LastMachine(losers, current.Machine), losers, current.Fresh);
    }

    /// <summary>
    /// The write that turns <paramref name="before"/> (the stored item) into <paramref name="after"/>, or null when there is
    /// nothing to write: neither exists, or they are equal. Only state and hash changed: <c>MarkUnseen</c>.
    /// </summary>
    public static ConflictWrite? Diff(ConflictItem? before, ConflictState? after)
    {
        if (after is null)
            return before is null ? null : new ConflictWrite(ConflictWriteAction.Remove, null, null, null, null, false);
        if (before is null)
            return Upsert(after, fresh: true);

        var sameLosers = GitSyncReviews.ReadConflictLosers(before.PayloadJson).Equals(after.Losers);
        if (before.State == after.State && before.RecordHash == after.Hash && before.Machine == after.Machine && sameLosers)
            return null;
        if (after.State == "unseen" && before.Machine == after.Machine && sameLosers)
            return new ConflictWrite(ConflictWriteAction.MarkUnseen, after.State, after.Hash, null, null, false);
        return Upsert(after, fresh: false);
    }

    private static ConflictWrite Upsert(ConflictState after, bool fresh) =>
        new(ConflictWriteAction.Upsert, after.State, after.Hash, after.Machine, GitSyncReviews.ConflictPayload(after.Losers), fresh);

    /// <summary>Carries out <paramref name="w"/>; an <c>Upsert</c> that is <c>Fresh</c> sets <c>arrived_at</c> to <paramref name="nowMs"/>, any other keeps it.</summary>
    public static void Write(MsSqliteConnection conn, SqliteTransaction? tx, string id, ConflictWrite w, long nowMs)
    {
        switch (w.Action)
        {
            case ConflictWriteAction.Remove:
                GitSyncReviews.DeleteConflictItem(conn, id, tx);
                break;
            case ConflictWriteAction.MarkUnseen:
                GitSyncReviews.MarkConflictUnseen(conn, id, w.Hash!, tx);
                break;
            default:
                GitSyncReviews.UpsertConflictItem(
                    conn, new ConflictItem(id, w.State!, w.Hash!, w.Machine!, nowMs, w.PayloadJson!), keepArrivedAt: !w.Fresh, tx);
                break;
        }
    }

    /// <summary>
    /// Files <paramref name="loser"/> against the memory <paramref name="id"/> without a merge decision (the alias pass): reads
    /// the item and the stored entry's text inside <paramref name="tx"/>, applies the arrival rule and writes what changed. An
    /// entry that is absent, in more than one tier or unreadable throws <see cref="InvalidOperationException"/> before anything is written.
    /// </summary>
    public static void Append(SqliteStoreTransaction tx, string id, SyncConflict.Loser loser, long nowMs)
    {
        var conn = tx.Connection;
        var item = GitSyncReviews.ReadConflictItem(conn, id, tx.Transaction);
        var read = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
        if (read is not LocalRead.Present present)
            throw new InvalidOperationException($"the entry {id} has no single readable stored text to file a conflict against");
        var after = Arrive(Of(item), loser, present.View.Content.Text);
        if (Diff(item, after) is { } write)
            Write(conn, tx.Transaction, id, write, nowMs);
    }

    private static string ShownHash(string applied, FSharpList<SyncConflict.Loser> losers) =>
        SyncConflict.shownHash(FSharpOption<string>.Some(applied), ListModule.OfSeq(losers.Select(l => l.Text)));

    private static string LastMachine(FSharpList<SyncConflict.Loser> losers, string fallback) =>
        losers.IsEmpty ? fallback : ListModule.Last(losers).Machine;
}
