using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

// Pin review (Part 3 of milestone 1b-iii), D1 to D3: the pin item writes of the pull's apply. The decision is carried out
// as for any entry (a sticky record is inserted non-sticky, FS4, FS7 and NS3 leave the entry unpinned, FC2 and NC2 hold
// the content); then, in the entry's transaction, each NewPin or PinEdit request the decision carried writes its item by
// the arrival rule (SyncPin.arrival). Pure planning (Arrivals) and the writes (Write) are apart so a caller can plan first.

/// <summary>What a pin item write does: remove the item, mark it <c>unseen</c> with a new hash, or write it <c>open</c>.</summary>
internal enum PinWriteAction { Remove, MarkUnseen, Open }

/// <summary>
/// One pin item write for an id: the item's <paramref name="Kind"/> (<c>new-pin</c> or <c>pin-edit</c>), the action, the
/// shown hash it is written with (null for <see cref="PinWriteAction.Remove"/>) and the record's machine (<c>WrittenBy</c>).
/// </summary>
internal sealed record PinWrite(string Kind, PinWriteAction Action, string? Hash, string? Machine);

/// <summary>The pin item writes of a carried-out decision (pin review D1 to D3).</summary>
internal static class PinPlanning
{
    private static readonly string NewPinKind = SyncPin.kindName(SyncPin.ItemKind.NewPinItem);
    private static readonly string PinEditKind = SyncPin.kindName(SyncPin.ItemKind.PinEditItem);

    private static FSharpOption<T>? Opt<T>(T? value) where T : class => value is null ? null : FSharpOption<T>.Some(value);

    /// <summary>
    /// The item kinds the decision's requests ask for, distinct, in the order the decision carries them: <c>new-pin</c>
    /// for a <c>NewPin</c>, <c>pin-edit</c> for a <c>PinEdit</c>. A <c>Conflict</c> asks for none (the decision is postponed).
    /// </summary>
    internal static IReadOnlyList<string> Requests(SyncMerge.MergeDecision d)
    {
        var kinds = new List<string>();
        foreach (var request in d.Reviews)
        {
            var kind = request.IsNewPin ? NewPinKind : request.IsPinEdit ? PinEditKind : null;
            if (kind is not null && !kinds.Contains(kind))
                kinds.Add(kind);
        }
        return kinds;
    }

    /// <summary>
    /// The arrival rule (D3) for each <c>NewPin</c> or <c>PinEdit</c> request of <paramref name="d"/>, from the state after
    /// the decision was carried out: a <c>new-pin</c> item shows the stored entry (<paramref name="stored"/>: its text,
    /// project and scope), a <c>pin-edit</c> item the post-decision base's content group (<paramref name="postBase"/>), each
    /// as <c>SyncPin.shownHash</c>. <paramref name="itemOf"/> reads the id's item of a kind (null: none); <c>SyncPin.arrival</c>
    /// decides: none or an answered one opens (<see cref="PinWriteAction.Open"/>), an open or unseen one with the same hash
    /// is left (no write), with another hash it turns unseen (<see cref="PinWriteAction.MarkUnseen"/>). Every write carries
    /// <paramref name="machine"/>, the record's <c>WrittenBy</c>. Throws <see cref="InvalidOperationException"/> when an
    /// input a request needs is missing or an item's state is not one of the four.
    /// </summary>
    public static IReadOnlyList<PinWrite> Arrivals(
        SyncMerge.MergeDecision d, Func<string, PinItem?> itemOf, SyncMerge.LocalEntry? stored, SyncRecord? postBase, string? machine)
    {
        var writes = new List<PinWrite>();
        foreach (var kind in Requests(d))
        {
            string hash;
            if (kind == NewPinKind)
            {
                var entry = stored ?? throw new InvalidOperationException("a new-pin item needs the stored entry");
                hash = SyncPin.shownHash(entry.Content.Text, entry.Content.Project, entry.Content.Scope);
            }
            else
            {
                var record = postBase ?? throw new InvalidOperationException("a pin-edit item needs the row's base");
                hash = SyncPin.shownHash(record.Content, record.Project, record.Scope);
            }

            FSharpOption<Tuple<SyncPin.ItemState, string>>? existing = null;
            if (itemOf(kind) is { } item)
            {
                var state = SyncPin.tryState(item.State)
                    ?? throw new InvalidOperationException("the pin item's state is not known: " + item.State);
                existing = Opt(Tuple.Create(state.Value, item.RecordHash ?? ""));
            }

            var arrived = SyncPin.arrival(existing, hash);
            if (arrived.IsOpenItem)
                writes.Add(new PinWrite(kind, PinWriteAction.Open, hash, machine));
            else if (arrived.IsUnseenItem)
                writes.Add(new PinWrite(kind, PinWriteAction.MarkUnseen, hash, machine));
        }
        return writes;
    }

    /// <summary>
    /// Carries the writes out for <paramref name="id"/> inside the caller's transaction: <see cref="PinWriteAction.Remove"/>
    /// deletes the item; <see cref="PinWriteAction.MarkUnseen"/> sets it <c>unseen</c> with the hash and machine (payload and
    /// <c>arrived_at</c> kept); <see cref="PinWriteAction.Open"/> writes it <c>open</c> with the hash, the machine, the
    /// payload <c>{}</c> and <c>arrived_at</c> = <paramref name="nowMs"/>, replacing any row of the same kind.
    /// </summary>
    public static void Write(MsSqliteConnection conn, SqliteTransaction tx, string id, IEnumerable<PinWrite> writes, long nowMs)
    {
        foreach (var write in writes)
        {
            switch (write.Action)
            {
                case PinWriteAction.Remove:
                    GitSyncReviews.DeletePinItem(conn, id, write.Kind, tx);
                    break;
                case PinWriteAction.MarkUnseen:
                    GitSyncReviews.MarkPinUnseen(
                        conn, id, write.Kind, write.Hash ?? throw new InvalidOperationException("an unseen pin item needs a hash"),
                        write.Machine, tx);
                    break;
                case PinWriteAction.Open:
                    GitSyncReviews.UpsertPinItem(
                        conn, new PinItem(id, write.Kind, SyncPin.stateName(SyncPin.ItemState.Open), write.Hash, write.Machine, nowMs), tx);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(writes), write.Action, "an unknown pin item write");
            }
        }
    }
}
