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
// Conflict review (Part 4), D3: the same step plans the conflict item's one write (ConflictWrite): each Conflict request
// files its loser by the shared arrival rule (ConflictItems), so the plan the transaction recomputes covers it too.

/// <summary>What a pin item write does: remove the item, mark it <c>unseen</c> with a new hash, or write it <c>open</c>.</summary>
internal enum PinWriteAction { Remove, MarkUnseen, Open }

/// <summary>
/// One pin item write for an id: the item's <paramref name="Kind"/> (<c>new-pin</c> or <c>pin-edit</c>), the action, the
/// shown hash it is written with (null for <see cref="PinWriteAction.Remove"/>) and the record's machine (<c>WrittenBy</c>).
/// </summary>
internal sealed record PinWrite(string Kind, PinWriteAction Action, string? Hash, string? Machine);

/// <summary>
/// What the pull does with an id's pin items (pin review D3 to D5): the item writes, reconcile first and arrivals last, in
/// order, and the <c>base_json</c> whose content group is applied to the entry first (pin-edit rule 4, D5; null when none);
/// and the conflict item's one write (conflict review D3; null when none). Planned before the transaction on the expected
/// post-decision state and again inside it on the stored one: the two must <see cref="Matches"/>, else the entry is deferred.
/// </summary>
internal sealed record PinPlan(IReadOnlyList<PinWrite> Writes, string? ApplyBaseJson, ConflictWrite? Conflict = null)
{
    public static readonly PinPlan Empty = new([], null);

    /// <summary>No item is written, updated or removed and no held edit is applied.</summary>
    public bool IsEmpty => Writes.Count == 0 && ApplyBaseJson is null && Conflict is null;

    /// <summary>The same writes in the same order, the same base (ordinal) and the same conflict write (by value: payload and hash included).</summary>
    public bool Matches(PinPlan other) =>
        Writes.SequenceEqual(other.Writes) && string.Equals(ApplyBaseJson, other.ApplyBaseJson, StringComparison.Ordinal)
        && Equals(Conflict, other.Conflict);
}

/// <summary>The pin item writes of a carried-out decision (pin review D1 to D5).</summary>
internal static class PinPlanning
{
    private static readonly string NewPinKind = SyncPin.kindName(SyncPin.ItemKind.NewPinItem);
    private static readonly string PinEditKind = SyncPin.kindName(SyncPin.ItemKind.PinEditItem);

    private static FSharpOption<T>? Opt<T>(T? value) where T : class => value is null ? null : FSharpOption<T>.Some(value);

    /// <summary>The two pin item kinds, in ordinal order.</summary>
    internal static readonly IReadOnlyList<string> Kinds = [NewPinKind, PinEditKind];

    /// <summary>The id's pin items (any of the four states), by kind in ordinal order, read inside <paramref name="tx"/> when given.</summary>
    internal static IReadOnlyList<PinItem> ItemsOf(MsSqliteConnection conn, string id, SqliteTransaction? tx = null)
    {
        var items = new List<PinItem>();
        foreach (var kind in Kinds)
            if (GitSyncReviews.ReadPinItem(conn, id, kind, tx) is { } item)
                items.Add(item);
        return items;
    }

    /// <summary>
    /// The item kinds the decision's requests ask for, distinct, in the order the decision carries them: <c>new-pin</c>
    /// for a <c>NewPin</c>, <c>pin-edit</c> for a <c>PinEdit</c>. A <c>Conflict</c> asks for no pin item (see
    /// <see cref="ConflictRequests"/>).
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

    /// <summary>The losers of the decision's <c>Conflict</c> requests, in the order the decision carries them (conflict review D3).</summary>
    internal static IReadOnlyList<SyncMerge.ConflictLoser> ConflictRequests(SyncMerge.MergeDecision d)
    {
        var losers = new List<SyncMerge.ConflictLoser>();
        foreach (var request in d.Reviews)
            if (request is SyncMerge.ReviewRequest.Conflict conflict)
                losers.Add(conflict.Item);
        return losers;
    }

    /// <summary>
    /// Whether the item step has anything to look at for an id: a pin item (<paramref name="pins"/>), a conflict item
    /// (<paramref name="conflict"/>), or a pin or conflict request of <paramref name="d"/>. The one gate of the plan before
    /// the transaction, of <see cref="Plan"/> and of the step inside the transaction.
    /// </summary>
    internal static bool HasWork(IReadOnlyList<PinItem> pins, ConflictItem? conflict, SyncMerge.MergeDecision d) =>
        pins.Count > 0 || conflict is not null || Requests(d).Count > 0 || ConflictRequests(d).Count > 0;

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
                hash = ApplyPlanning.BaseShownHash(record);
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
    /// The plan for an id's pin items (D4, then D3): <c>SyncPin.reconcile</c> for each of <paramref name="items"/> (the id's
    /// items, any of the four states) on the post-decision state, the entry <paramref name="stored"/> (null: none), the row
    /// <paramref name="rowAfter"/> (null: none) and its parsed base, and whether the record is present; then
    /// <see cref="Arrivals"/> over the items as the reconcile leaves them. An update to <c>unseen</c> carries
    /// <paramref name="machine"/>, the record's <c>WrittenBy</c>. Pin-edit rule 4 sets <see cref="PinPlan.ApplyBaseJson"/>
    /// to the row's base and removes the item; the arrivals then see the entry with the base's content group. Then
    /// (conflict review D3), over that same entry, each <c>Conflict</c> request files its loser
    /// (<c>SyncConflict.loserOf</c> with <paramref name="thisMachine"/>, the machine of a loser with no stamp) into the id's
    /// conflict item <paramref name="conflict"/> by <see cref="ConflictItems.Arrive"/>, and <see cref="PinPlan.Conflict"/>
    /// is the one write that takes the item there (<see cref="ConflictItems.Diff"/>). Before those arrivals the item is
    /// reconciled with the pull (conflict review D4, <see cref="ConflictItems.Reconcile"/>) over the same entry, the row's
    /// state and <paramref name="heldAfter"/> (whether the id has a held deletion after the decision, computed once by the
    /// caller with <c>SyncConflict.heldAfter</c>, the same value before and inside the transaction). Throws
    /// <see cref="InvalidOperationException"/> for a base that cannot be read, an unknown kind or state, or an arrival
    /// input that is missing (a conflict request with no entry included).
    /// </summary>
    public static PinPlan Plan(
        IReadOnlyList<PinItem> items, ConflictItem? conflict, SyncMerge.MergeDecision d, SyncMerge.LocalEntry? stored,
        GitSyncRow? rowAfter, bool recordPresent, string? machine, string thisMachine, bool heldAfter)
    {
        if (!HasWork(items, conflict, d))
            return PinPlan.Empty;

        SyncRecord? postBase = null;
        if (rowAfter?.BaseJson is { } baseText)
        {
            postBase = ApplyPlanning.ParseBase(rowAfter.EntryId, baseText).Parsed?.Record
                ?? throw new InvalidOperationException("the row's base cannot be read");
        }
        var entryView = stored is null ? null : Opt(SyncPin.entryView(stored));
        var rowView = rowAfter is null
            ? null
            : Opt(new SyncPin.PinRowView(
                rowAfter.State, rowAfter.BaseJson is not null, rowAfter.ContentStamp is not null, Opt(rowAfter.ScannedContentHash)));
        var baseView = postBase is null
            ? null
            : Opt(new SyncPin.BaseView(ApplyPlanning.BaseShownHash(postBase), postBase.Placement.Sticky));

        var writes = new List<PinWrite>();
        string? applyBase = null;
        var after = new Dictionary<string, PinItem?>(StringComparer.Ordinal);
        // Known limitation, accepted: items are reconciled in kind order, so a `new-pin` item of this id is checked against
        // `stored` BEFORE pin-edit rule 4 (below) applies the held content to the entry. When both kinds exist on one id and
        // rule 4 fires in the same plan, the new-pin item can be marked unseen with a hash of the pre-apply text. A pull never
        // leaves both kinds on one id (a new-pin needs the entry unpinned, a pin-edit pinned), and the next pull corrects
        // the hash, so the case is not planned for.
        foreach (var item in items.OrderBy(i => i.Kind, StringComparer.Ordinal))
        {
            var kind = SyncPin.tryKind(item.Kind) ?? throw new InvalidOperationException("the pin item's kind is not known: " + item.Kind);
            var state = SyncPin.tryState(item.State) ?? throw new InvalidOperationException("the pin item's state is not known: " + item.State);
            var reconciled = SyncPin.reconcile(new SyncPin.ReconcileInput(
                new SyncPin.PinItemView(kind.Value, state.Value, item.RecordHash ?? ""), entryView, rowView, baseView, recordPresent));
            if (reconciled.IsKeep)
            {
                after[item.Kind] = item;
            }
            else if (reconciled is SyncPin.Reconciled.MarkUnseen unseen)
            {
                writes.Add(new PinWrite(item.Kind, PinWriteAction.MarkUnseen, unseen.hash, machine));
                after[item.Kind] = item with { State = SyncPin.stateName(SyncPin.ItemState.Unseen), RecordHash = unseen.hash, Machine = machine };
            }
            else
            {
                if (reconciled.IsApplyBaseThenRemove)
                    applyBase = rowAfter!.BaseJson;
                writes.Add(new PinWrite(item.Kind, PinWriteAction.Remove, null, null));
                after[item.Kind] = null;
            }
        }

        // The arrivals see the entry as it is once the held content is applied (only the shown fields matter).
        var shown = stored is not null && applyBase is not null && postBase is not null
            ? new SyncMerge.LocalEntry(ApplyPlanning.RecordContent(postBase), stored.Tags, stored.Tier, stored.Sticky)
            : stored;
        writes.AddRange(Arrivals(
            d, kind => after.TryGetValue(kind, out var left) ? left : null, shown, postBase, machine));

        // Conflict review D4 then D3: the reconcile and the arrival of each loser, over the entry as the pin rules leave it,
        // so the plan before the transaction and the one inside it read the same text.
        var conflictState = ConflictItems.Reconcile(ConflictItems.Of(conflict), shown?.Content.Text, rowAfter?.State, heldAfter);
        foreach (var loser in ConflictRequests(d))
        {
            var entry = shown ?? throw new InvalidOperationException("a conflict item needs the stored entry");
            conflictState = ConflictItems.Arrive(conflictState, SyncConflict.loserOf(thisMachine, loser), entry.Content.Text);
        }
        var conflictWrite = ConflictItems.Diff(conflict, conflictState);

        return writes.Count == 0 && applyBase is null && conflictWrite is null
            ? PinPlan.Empty
            : new PinPlan(writes, applyBase, conflictWrite);
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
