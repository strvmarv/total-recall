using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncApplier
{
    /// <summary>
    /// What a convergence (alias D3) read of the other id <paramref name="OtherId"/> before its transaction: its row, its entry,
    /// its pin items and its conflict item (null: none), the canonical id <paramref name="CanonicalId"/> and the group's origin
    /// hash <paramref name="Hash"/>. The transaction re-reads them and defers on any difference.
    /// </summary>
    private sealed record Convergence(
        string OtherId,
        string CanonicalId,
        string Hash,
        GitSyncRow OtherRow,
        LocalRead.Present OtherRead,
        IReadOnlyList<PinItem> OtherPins,
        ConflictItem? OtherConflict);

    /// <summary>
    /// Converges the memory <paramref name="x"/>, whose record disappeared under an open alias, onto the canonical record of
    /// <paramref name="target"/> (alias D3): one immediate transaction re-keys the entry and its conflict item to the canonical
    /// id and applies the canonical record to it as the ordinary first-sync merge with no base (D4), with the pin and conflict
    /// items it requests and the other id's conflict item reconciled first (D5); <paramref name="x"/>'s row, issue and pin
    /// items go, its <c>import_log</c> rows follow, and the alias is <c>resolved</c>. A failure, a skip or a deferral leaves
    /// <paramref name="x"/> and its row as they are; the issue bookkeeping is <see cref="ApplyOne"/>'s, under <paramref name="x"/>.
    /// </summary>
    internal EntryResult ApplyConvergence(string x, ConvergeTarget target, bool proven)
    {
        var result = ConvergeCore(x, target, proven);
        return result switch
        {
            EntryResult.Failed failed => RecordFailure(x, null, failed),
            EntryResult.Skipped skipped => RecordSkip(x, skipped),
            EntryResult.NoOp => CleanUp(x, result),
            _ => result,
        };
    }

    /// <summary>
    /// The work of <see cref="ApplyConvergence"/>: reads <paramref name="x"/> and the canonical id, checks conditions 1, 5
    /// and 7 again (a change since the pre-pass defers), decides the unchanged no-base merge of the canonical record against
    /// <paramref name="x"/>'s entry, and hands it to <see cref="PlanAndCarry"/> for the canonical id with no row,
    /// <paramref name="x"/>'s entry and <paramref name="x"/>'s conflict item as the canonical id's.
    /// </summary>
    private EntryResult ConvergeCore(string x, ConvergeTarget target, bool proven)
    {
        BeforeEntry?.Invoke(x);
        var conn = _store.Connection;
        var c = target.CanonicalId;

        if (!SyncValidation.isEligibleId(x))
            return new EntryResult.Skipped("the id is not eligible");

        GitSyncRow? rowX;
        LocalRead readX;
        IReadOnlyList<PinItem> pinsX;
        ConflictItem? conflictX;
        HeldItem? heldX;
        string thisMachine;
        bool cFree;
        try
        {
            rowX = GitSyncRows.Read(conn, x);
            readX = ReadLocal(x);
            pinsX = PinPlanning.ItemsOf(conn, x);
            conflictX = GitSyncReviews.ReadConflictItem(conn, x);
            heldX = GitSyncReviews.ReadHeldItem(conn, x);
            thisMachine = ThisMachine(conn);
            cFree = CanonicalFree(c);
        }
        catch (FormatException ex)
        {
            return new EntryResult.Failed("the row cannot be read: " + OneLine(ex.Message));
        }

        // Conditions 1, 5 and 7 as of now (the pre-pass decided on an earlier read).
        if (readX is not LocalRead.Present present || rowX is not { BaseJson: not null } || !rowX.State.IsActive || heldX is not null || !cFree)
            return new EntryResult.Deferred("the entry changed since the decision was made", Busy: false);

        // D3 step 3: the unchanged no-base merge (row none, no pending or pushing stamps, no held item).
        var decision = SyncMerge.decide(BuildInput(null, null, target.Record, present.View, proven, null));
        if (decision.Entry is not SyncMerge.EntryOutcome.MergeGroups || decision.RowState is not { Value.IsActive: true })
            return new EntryResult.Failed("convergence needs a merge decision");

        return PlanAndCarry(
            c, decision, converted: false, reopen: false, heldAfter: SyncConflict.heldAfter(decision, false), row: null, read: present,
            held: null, proven, blobSha: target.Sha, oldBase: null, theirs: target.Record, pinItems: [],
            conflictItem: conflictX is null ? null : conflictX with { EntryId = c }, thisMachine, aliasPlan: null,
            convergence: new Convergence(x, c, target.Hash, rowX, present, pinsX, conflictX));
    }

    /// <summary>
    /// Condition 5 for the canonical id <paramref name="c"/>: no memory, no knowledge entry, no row and no review row. A
    /// knowledge entry that cannot be read means not free (as in the pre-pass); a row that cannot be read throws.
    /// </summary>
    private bool CanonicalFree(string c)
    {
        var conn = _store.Connection;
        if (ReadLocal(c) is not LocalRead.Absent)
            return false;
        try
        {
            if (InKnowledge(c))
                return false;
        }
        catch (Exception ex) when (IsMappingFailure(ex))
        {
            return false;
        }
        return GitSyncRows.Read(conn, c) is null && !GitSyncReviews.HasAnyItem(conn, c);
    }

    /// <summary>
    /// Inside the convergence's transaction, before the re-key (alias D3 step 1): the other id's row, entry, pin items and
    /// conflict item equal what <paramref name="convergence"/> read, it has no held-delete item, and the canonical id
    /// <paramref name="c"/> is still free (no memory, knowledge entry, row or review row).
    /// </summary>
    private static bool ConvergenceHolds(SqliteStoreTransaction tx, string c, Convergence convergence)
    {
        var conn = tx.Connection;
        var dbTx = tx.Transaction;
        var x = convergence.OtherId;
        var rowX = GitSyncRows.Read(conn, x, dbTx);
        var readX = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, x), () => tx.IsSticky(ContentType.Memory, x));
        var pinsX = PinPlanning.ItemsOf(conn, x, dbTx);
        var conflictX = GitSyncReviews.ReadConflictItem(conn, x, dbTx);
        var heldX = GitSyncReviews.ReadHeldItem(conn, x, dbTx);
        var readC = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, c), () => tx.IsSticky(ContentType.Memory, c));
        return Equals(rowX, convergence.OtherRow)
            && readX.Equals(convergence.OtherRead)
            && pinsX.SequenceEqual(convergence.OtherPins)
            && Equals(conflictX, convergence.OtherConflict)
            && heldX is null
            && readC is LocalRead.Absent
            && !ApplyPlanning.InKnowledge(tx, c)
            && GitSyncRows.Read(conn, c, dbTx) is null
            && !GitSyncReviews.HasAnyItem(conn, c, dbTx);
    }
}
