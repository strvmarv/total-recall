using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using StorePlan = TotalRecall.Infrastructure.GitSync.ApplyPlanning.StorePlan;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// How a carried-out decision counts: an entry inserted, updated (a store, row or held-item write) or whose row was dropped,
/// or <see cref="Unchanged"/>: nothing was written but the decision still reports something (a push deletion whose row
/// already agrees, milestone 1b-ii D9).
/// </summary>
internal enum CarriedKind { Inserted, Updated, Dropped, Unchanged }

/// <summary>
/// What a carried-out decision did besides its kind (milestone 1b-ii, D6 to D11, D16 and D20): the entry was deleted by a
/// remote deletion, its local deletion is for the push to carry out (<paramref name="PushDeletion"/>), a held-delete item
/// was written (its direction and cause), the open item was closed, the decision's notes (<c>edit-beats-deletion</c>,
/// <c>pending-changes-dropped</c>), and the <c>repush_groups</c> marker it set (<paramref name="Repush"/>; null when it set
/// none or left the marker as it was).
/// </summary>
internal sealed record EntryEffects(bool Deleted, bool PushDeletion, HeldEntry? Held, bool Closed, IReadOnlyList<string> Notes, string? Repush)
{
    public static readonly EntryEffects None = new(false, false, null, false, [], null);
}

/// <summary>What applying one entry came to (decisions 7 to 13).</summary>
internal abstract record EntryResult
{
    private EntryResult() { }

    /// <summary>The decision was carried out. <paramref name="Colder"/>: the hot limit stored or moved the entry to the warm tier.</summary>
    public sealed record Carried(CarriedKind Kind, bool Colder, EntryEffects Effects) : EntryResult;

    /// <summary>
    /// Nothing needed doing (the resulting row equals the stored one and the store is untouched). <paramref name="Repush"/>:
    /// the <c>repush_groups</c> marker the decision set, already on the row (milestone 1b-ii, D16).
    /// </summary>
    public sealed record NoOp(string? Repush = null) : EntryResult;

    /// <summary>Left for the next pull; nothing was changed. <paramref name="Busy"/>: the database was locked, so the apply stops.</summary>
    public sealed record Deferred(string Reason, bool Busy) : EntryResult;

    /// <summary>The entry cannot be applied (an id in two tiers, an unreadable entry, an id that is not eligible); nothing was changed.</summary>
    public sealed record Skipped(string Reason) : EntryResult;

    /// <summary>The entry failed and its transaction was rolled back.</summary>
    public sealed record Failed(string Detail, int Attempts = 0) : EntryResult;
}

public sealed partial class GitSyncApplier
{
    /// <summary>
    /// The delete guard's verdict for one pull (milestone 1b-ii, D3 and D4): a tripped direction carries none of its
    /// deletions out. The default trips neither; <see cref="ApplyOne"/> takes it explicitly. (Named apart:
    /// <see cref="TotalRecall.Infrastructure.GitSync.GuardVerdict"/> is the repository guard's.)
    /// </summary>
    internal readonly record struct DeleteGuardVerdict(bool RemoteTripped, bool LocalTripped)
    {
        /// <summary>The decision is a deletion of a tripped direction: a <c>DeleteEntry</c> (remote) or a <c>PushDeletion</c> (local).</summary>
        public bool Converts(SyncMerge.MergeDecision d) =>
            d.Entry.IsDeleteEntry && RemoteTripped || d.Entry.IsPushDeletion && LocalTripped;
    }

    private static FSharpOption<T> Some<T>(T value) => FSharpOption<T>.Some(value);

    private static FSharpOption<T>? Opt<T>(T? value) where T : class => value is null ? null : FSharpOption<T>.Some(value);

    private static T? Unopt<T>(FSharpOption<T>? option) where T : class => option is null ? null : option.Value;

    private static bool IsBusy(SqliteException ex) => SyncSql.IsBusy(ex);

    private static string OneLine(string text) => SyncSql.OneLine(text);

    private void Step(string id, string step) => BeforeEntryStep?.Invoke(id, step);

    private LocalRead ReadLocal(string id) =>
        LocalEntryReader.Read(
            tier => _store.Get(tier, ContentType.Memory, id),
            () => _store.IsSticky(ContentType.Memory, id));

    private bool InKnowledge(string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(tier => _store.Get(tier, ContentType.Knowledge, id) is not null);

    // ---------------------------------------------------------------------------------------------
    // One entry

    /// <summary>
    /// Applies one id: reads its row and local entry, runs <c>SyncMerge.decide</c>, computes the vector, then carries the
    /// decision out in one immediate transaction that first re-reads the entry (decisions 7 to 13). No decision is
    /// postponed (conflict review D3): a text conflict is carried out and its loser filed as a <c>conflict</c> item. <paramref name="theirs"/> is null when the id has no accepted record;
    /// <paramref name="held"/> is the id's held-delete item, open or approved, as the pull read it (milestone 1b-ii, D8;
    /// milestone 1b-iii, D4), null when it has none; <paramref name="blobSha"/> is the record's blob id; <paramref name="guard"/> is the pull's delete guard verdict (required: <c>default</c> trips nothing),
    /// which turns a deletion of a tripped direction into a held item with the cause <c>guard</c> (D4).
    /// </summary>
    internal EntryResult ApplyOne(string id, ParsedRecord? theirs, bool proven, HeldItem? held, string? blobSha, DeleteGuardVerdict guard)
    {
        var result = ApplyCore(id, theirs, proven, held, blobSha, guard);
        return result switch
        {
            EntryResult.Failed failed => RecordFailure(id, blobSha, failed),
            EntryResult.Skipped skipped => RecordSkip(id, skipped),
            EntryResult.NoOp => CleanUp(id, result),
            _ => result,
        };
    }

    /// <summary>
    /// The work of <see cref="ApplyOne"/> before its issue bookkeeping. An insert (W1) whose record's origin hash maps in
    /// <c>import_log</c> to a different existing memory is skipped and recorded in <c>git_sync_alias</c> (alias D1).
    /// </summary>
    private EntryResult ApplyCore(string id, ParsedRecord? theirs, bool proven, HeldItem? held, string? blobSha, DeleteGuardVerdict guard)
    {
        BeforeEntry?.Invoke(id);
        var conn = _store.Connection;

        if (!SyncValidation.isEligibleId(id))
            return new EntryResult.Skipped("the id is not eligible");

        GitSyncRow? row;
        LocalRead read;
        IReadOnlyList<PinItem> pinItems;
        ConflictItem? conflictItem;
        string thisMachine;
        try
        {
            row = GitSyncRows.Read(conn, id);
            read = ReadLocal(id);
            pinItems = PinPlanning.ItemsOf(conn, id);
            conflictItem = GitSyncReviews.ReadConflictItem(conn, id);
            thisMachine = ThisMachine(conn);
        }
        catch (FormatException ex)
        {
            return new EntryResult.Failed("the row cannot be read: " + OneLine(ex.Message));
        }
        if (read is LocalRead.Duplicate)
            return new EntryResult.Skipped("the id is in more than one tier");
        if (read is LocalRead.Unreadable unreadable)
            return new EntryResult.Skipped(unreadable.Reason);
        var local = (read as LocalRead.Present)?.View;

        // An id that is a knowledge entry and has no row is never inserted as a memory (decision 7).
        if (row is null && local is null)
        {
            bool knowledge;
            try
            {
                knowledge = InKnowledge(id);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException)
            {
                return new EntryResult.Skipped("the id is a knowledge entry that cannot be read: " + OneLine(ex.Message));
            }
            if (knowledge)
                return new EntryResult.NoOp();
        }

        ParsedRecord? oldBase = null;
        if (row?.BaseJson is { } baseText)
        {
            var parsedBase = ApplyPlanning.ParseBase(id, baseText);
            if (parsedBase.Parsed is null)
                return new EntryResult.Failed("the row's base cannot be read: " + OneLine(SyncValidation.RejectModule.describe(parsedBase.Rejected)));
            oldBase = parsedBase.Parsed;
        }

        var (decision, approved) = Decide(row, oldBase, theirs, blobSha, local, proven, held);
        // Alias D1: an insert (W1) whose record's origin hash maps in import_log to a different existing memory is skipped;
        // the alias rows are the only write, planned here and recomputed inside the transaction.
        AliasPlan? aliasPlan = null;
        if (row is null && local is null && decision.Entry.IsInsertFromRecord && theirs!.Record.Origin is { } origin)
        {
            try
            {
                aliasPlan = GitSyncAliases.PlanSkip(conn, id, origin.Value.Hash);
            }
            catch (SqliteException ex) when (IsBusy(ex))
            {
                return new EntryResult.Deferred("the database is busy", Busy: true);
            }
            catch (SqliteException ex)
            {
                return new EntryResult.Failed("the alias lookup failed: " + OneLine(ex.Message));
            }
            if (aliasPlan is not null)
                decision = SyncAlias.skipInstead(decision); // D1: the plain Ignore, so no row and no new-pin item
        }
        // Milestone 1b-iii, D4: a deletion an approved item answers is never converted by the guard.
        var converted = approved?.IsAsApproved != true && guard.Converts(decision);
        if (converted)
            decision = SyncDeletion.holdInstead(decision); // D4: kept base update, no notes, no row state change
        // An approved item whose record changed after the answer is opened again with the record's blob id.
        var reopen = approved?.IsReopened == true && !decision.CloseHeldDelete;
        // Conflict review D4: whether a deletion is held after this decision (the guard's conversion included), computed
        // once from the decision and the held item the pull read, so both plans see the same value. Not a database read:
        // the item step runs before the hold is upserted.
        var heldAfter = SyncConflict.heldAfter(decision, held is not null);

        if (decision.Entry.IsDefer)
            return new EntryResult.Deferred("the merge rules leave this entry for the next pull", Busy: false);
        if (decision.Entry.IsDropRow)
            return ApplyDrop(id, decision, row, read, held);

        StorePlan plan;
        try
        {
            plan = ApplyPlanning.PlanStore(decision, theirs?.Record, local, _options.HotMaxContentChars);
        }
        catch (InvalidOperationException ex)
        {
            return new EntryResult.Failed(OneLine(ex.Message));
        }

        float[]? vector = null;
        if (plan.NeedsVector)
        {
            try
            {
                vector = _embedder.Embed(theirs!.Record.Content);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return new EntryResult.Failed("the embedder failed: " + OneLine(ex.Message));
            }
        }

        // Pin review D4 ("When it runs"): the reconcile and the arrivals are planned here on the expected post-decision state
        // (the entry as the store operations will leave it, the row as BuildRow plans it with a placeholder stamp issuer,
        // whose stamps the reconcile reads only as present or not); the transaction recomputes them and defers on a difference.
        // Conflict review D3: the conflict item's arrival is planned in the same plan.
        var pinPlan = PinPlan.Empty;
        float[]? baseVector = null;
        if (PinPlanning.HasWork(pinItems, conflictItem, decision))
        {
            string? heldText = null; // the held text to embed: the base's, when pin-edit rule 4 applies it and it differs
            try
            {
                var expected = ApplyPlanning.ExpectedEntry(plan, theirs?.Record, local);
                var (planned, planError) = ApplyPlanning.BuildRow(
                    id, decision, row, oldBase, theirs, expected, hadEntry: local is not null, plan.Colder, ApplyPlanning.PlaceholderIssue);
                if (planError is not null)
                    return new EntryResult.Failed(OneLine(planError));
                pinPlan = PinPlanning.Plan(
                    pinItems, conflictItem, decision, expected, planned ?? row, theirs is not null, Unopt(theirs?.Record.WrittenBy), thisMachine,
                    heldAfter);
                if (pinPlan.ApplyBaseJson is { } applyBase)
                {
                    var text = BaseContent(id, applyBase);
                    if (!string.Equals(text, expected?.Content.Text, StringComparison.Ordinal))
                        heldText = text;
                }
            }
            catch (InvalidOperationException ex)
            {
                return new EntryResult.Failed(OneLine(ex.Message));
            }
            if (heldText is not null)
            {
                try
                {
                    baseVector = _embedder.Embed(heldText);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    return new EntryResult.Failed("the embedder failed: " + OneLine(ex.Message));
                }
            }
        }

        // Decision 13: a decision that changes nothing opens no transaction (unless a stamp must be issued). A held-delete
        // write, close, hash refresh (milestone 1b-iii, D10) or re-open (D4), and a decision with notes, always change
        // something, and a push deletion is reported even when its row already agrees (milestone 1b-ii, D9). A pin item
        // write, update or removal, or a held edit to apply (pin review D4, D5), is a change too, and so is a conflict item
        // write (conflict review D3).
        if (!plan.HasStoreOps && !HasRestamp(decision) && !TouchesHeldItem(decision) && decision.Notes.IsEmpty
            && !decision.Entry.IsPushDeletion && !RefreshesHash(held, blobSha, decision) && !reopen
            && pinPlan.IsEmpty && (aliasPlan is null || aliasPlan.IsEmpty))
        {
            var (preview, error) = ApplyPlanning.BuildRow(id, decision, row, oldBase, theirs, local, hadEntry: local is not null, plan.Colder, _ => null);
            if (error is not null)
                return new EntryResult.Failed(OneLine(error));
            if (Equals(preview, row))
                return new EntryResult.NoOp(MarkerSet(decision));
        }

        BeforeTransaction?.Invoke(id);
        SqliteStoreTransaction tx;
        try
        {
            tx = _store.BeginImmediateTransaction();
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new EntryResult.Deferred("the database is busy", Busy: true);
        }
        TransactionCount++;
        using (tx)
        {
            var phase = "reread";
            try
            {
                return CarryOut(
                    id, tx, decision, converted, reopen, plan, vector, row, read, held, proven, blobSha, oldBase, theirs, local,
                    pinItems, conflictItem, thisMachine, heldAfter, pinPlan, baseVector, aliasPlan, p => phase = p);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return Classify(phase, ex);
            }
        }
    }

    /// <summary>
    /// The decision for one id (milestone 1b-iii, D4), the one place it is made, so the preview and the loop decide an id
    /// the same way. For an open item or none: <c>decide</c> on <see cref="BuildInput"/>, and <c>Approved</c> is null. For
    /// an approved item, by <c>SyncDeletion.approvedUse</c>: <c>AsApproved</c> decides as proven with no held item (W8.3,
    /// or W8.1 first); <c>Reopened</c> decides with the real proof and the item as the open item it becomes (W8.2), a
    /// modified copy that is only the input of <c>decide</c>: the original <paramref name="held"/> is what the
    /// transaction's re-read compares; <c>NotApproved</c> decides with the real proof and no held item. Each is then passed
    /// through <c>SyncDeletion.closeApproved</c>, which removes the item when its situation is over.
    /// </summary>
    private static (SyncMerge.MergeDecision Decision, SyncDeletion.ApprovedUse? Approved) Decide(
        GitSyncRow? row, ParsedRecord? oldBase, ParsedRecord? theirs, string? blobSha, SyncMerge.LocalEntry? local, bool proven, HeldItem? held)
    {
        if (held is not { State: "approved" })
            return (SyncMerge.decide(BuildInput(row, oldBase, theirs, local, proven, held)), null);
        var approved = SyncDeletion.approvedUse(
            row is null ? null : Some(row.State), oldBase is not null, local is not null,
            theirs is null ? null : Opt(blobSha), Opt(held.RecordHash));
        var decision = approved.IsAsApproved
            ? SyncMerge.decide(BuildInput(row, oldBase, theirs, local, proven: true, held: null))
            : approved.IsReopened
                ? SyncMerge.decide(BuildInput(row, oldBase, theirs, local, proven, held with { State = "open", RecordHash = blobSha }))
                : SyncMerge.decide(BuildInput(row, oldBase, theirs, local, proven, held: null));
        return (SyncDeletion.closeApproved(local is not null, theirs is not null, decision), approved);
    }

    /// <summary>
    /// The input of <c>SyncMerge.decide</c> for one id from what was read of it: the row (state, pending stamps,
    /// <c>pushing_stamps</c>), its parsed base, the record at <c>R</c>, the local entry, whether the apply is proven, and
    /// the direction of its open held-delete item (milestone 1b-ii, D2 and D8). The one place the input is built, so every
    /// decision about an id sees the same input (the preview's included).
    /// </summary>
    private static SyncMerge.MergeInput BuildInput(
        GitSyncRow? row, ParsedRecord? oldBase, ParsedRecord? theirs, SyncMerge.LocalEntry? local, bool proven, HeldItem? held) =>
        new(
            row is null ? null : Some(row.State),
            Opt(oldBase?.Record),
            Opt(theirs?.Record),
            Opt(local),
            new SyncMerge.GroupStamps(Opt(row?.ContentStamp), Opt(row?.StickyStamp), Opt(row?.TierStamp)),
            SyncApply.PushingStamps.decode(row?.PushingStamps!),
            proven,
            held?.Direction switch
            {
                "remote" => Some(SyncMerge.Direction.RemoteDeletion),
                "local" => Some(SyncMerge.Direction.LocalDeletion),
                _ => null, // no item (the column admits only these two words)
            });

    /// <summary>
    /// This machine's id (<c>_meta git_sync_machine</c>, read as <c>IssueStamp</c> reads it), the machine of a conflict loser
    /// with no stamp (conflict review D1); <c>""</c> when it is missing or not a valid id.
    /// </summary>
    private static string ThisMachine(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var text = SyncSql.GetMeta(conn, SyncIdentity.MetaKeys.Machine);
        var machine = text is null ? null : SyncStamp.MachineIdModule.tryCreate(text);
        return machine is null ? "" : SyncStamp.MachineIdModule.value(machine.Value);
    }

    /// <summary>The text of the base <paramref name="baseJson"/>; throws <see cref="InvalidOperationException"/> when it cannot be read.</summary>
    private static string BaseContent(string id, string baseJson) =>
        ApplyPlanning.ParseBase(id, baseJson).Parsed?.Record.Content
        ?? throw new InvalidOperationException("the row's base cannot be read");

    private static bool HasRestamp(SyncMerge.MergeDecision d) =>
        d.FreshContentStamp
        || d.Entry is SyncMerge.EntryOutcome.MergeGroups m
        && (m.Item.Content.Item2.IsRestampPending || m.Item.Sticky.Item2.IsRestampPending || m.Item.Tier.Item2.IsRestampPending);

    /// <summary>The decision writes a held-delete item or closes the open one.</summary>
    private static bool TouchesHeldItem(SyncMerge.MergeDecision d) => d.CloseHeldDelete || d.Entry.IsHoldDeletion;

    /// <summary>
    /// The decision leaves the open local item <paramref name="held"/> open (it neither closes nor writes a hold) and the
    /// record's blob id <paramref name="blobSha"/> differs from the item's hash: the item's <c>record_hash</c> is refreshed
    /// to it, its state, direction, cause and <c>arrived_at</c> kept (milestone 1b-iii, D10). A remote item has no hash and
    /// is never refreshed; an id with no record has no blob id.
    /// </summary>
    private static bool RefreshesHash(HeldItem? held, string? blobSha, SyncMerge.MergeDecision d) =>
        held is { State: "open", Direction: "local" }
        && blobSha is not null
        && !string.Equals(blobSha, held.RecordHash, StringComparison.Ordinal)
        && !d.CloseHeldDelete
        && !d.Entry.IsHoldDeletion;

    /// <summary>The <c>repush_groups</c> text the decision writes (milestone 1b-ii, D16); null when it clears or keeps the marker.</summary>
    private static string? MarkerSet(SyncMerge.MergeDecision d) =>
        SyncApply.repushMarker(d) is SyncApply.MarkerChange.SetMarker set ? Unopt(set.Item) : null;

    /// <summary>The decision's notes as the words of <see cref="NoteEntry"/>.</summary>
    private static IReadOnlyList<string> NoteWords(SyncMerge.MergeDecision d) =>
        d.Notes.Select(n => n.IsEditBeatsDeletion ? "edit-beats-deletion" : "pending-changes-dropped").ToList();

    // ---------------------------------------------------------------------------------------------
    // The transaction

    private EntryResult CarryOut(
        string id,
        SqliteStoreTransaction tx,
        SyncMerge.MergeDecision decision,
        bool converted,
        bool reopen,
        StorePlan plan,
        float[]? vector,
        GitSyncRow? row,
        LocalRead read,
        HeldItem? held,
        bool proven,
        string? blobSha,
        ParsedRecord? oldBase,
        ParsedRecord? theirs,
        SyncMerge.LocalEntry? local,
        IReadOnlyList<PinItem> pinItems,
        ConflictItem? conflictItem,
        string thisMachine,
        bool heldAfter,
        PinPlan pinPlan,
        float[]? baseVector,
        AliasPlan? aliasPlan,
        Action<string> phase)
    {
        tx.BeforeStep = step => BeforeEntryStep?.Invoke(id, step);
        var conn = tx.Connection;
        var dbTx = tx.Transaction;

        phase("reread");
        Step(id, "reread");
        var rowNow = GitSyncRows.Read(conn, id, dbTx);
        var readNow = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
        var heldNow = GitSyncReviews.ReadHeldItem(conn, id, dbTx); // open or approved: an approved item is passed as read (D4)
        var pinItemsNow = PinPlanning.ItemsOf(conn, id, dbTx);
        var conflictItemNow = GitSyncReviews.ReadConflictItem(conn, id, dbTx);
        if (!Equals(row, rowNow) || !read.Equals(readNow) || !AgreesWithRow(rowNow, readNow) || !Equals(held, heldNow)
            || !pinItems.SequenceEqual(pinItemsNow) || !Equals(conflictItem, conflictItemNow))
            return new EntryResult.Deferred("the entry changed since the decision was made", Busy: false);
        if (plan.Insert && ApplyPlanning.InKnowledge(tx, id))
            return new EntryResult.Deferred("the id now exists as a knowledge entry", Busy: false);

        phase("work");
        var current = (read as LocalRead.Present)?.Tier;
        if (plan.Delete)
        {
            tx.Delete(current!, ContentType.Memory, id);
        }
        else if (plan.Insert)
        {
            ApplyPlanning.InsertRecord(tx, id, plan.InsertTier!, theirs!.Record, vector);
        }
        else
        {
            ApplyPlanning.WriteStore(tx, id, current!, plan, vector);
        }

        var storedRead = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
        var stored = (storedRead as LocalRead.Present)?.View;
        if (plan.Delete && storedRead is not LocalRead.Absent)
            throw new InvalidOperationException("the entry is still there after it was deleted");
        if (plan.HasStoreOps && !plan.Delete && stored is null)
            throw new InvalidOperationException("the entry cannot be read back after it was written");

        // A deleted entry's row is planned with no stored entry, so its scanned comparison columns are left as they are.
        Step(id, "row");
        var (next, error) = ApplyPlanning.BuildRow(
            id, decision, row, oldBase, theirs, plan.Delete ? null : stored ?? local, hadEntry: local is not null, plan.Colder,
            floor => ApplyPlanning.IssueStamp(conn, dbTx, _nowMs(), floor));
        if (error is not null)
            throw new InvalidOperationException(error);
        var rowWritten = false;
        if (next is not null && !next.Equals(row))
        {
            GitSyncRows.Write(conn, next, dbTx);
            rowWritten = true;
        }

        if (plan.Insert)
            ApplyPlanning.LogImport(tx, id, theirs!.Record, stored!);

        // Pin review D4 and D3: the id's items are reconciled with the state the decision left (the stored entry, the row
        // and its post-decision base), then each NewPin or PinEdit request the decision carried writes its item by the
        // arrival rule. The plan is recomputed here and must match the one made before the transaction; pin-edit rule 4
        // first applies the held content group to the entry (D5) with the vector computed before the transaction. Conflict
        // review D3: in the same step and the same plan, each Conflict request files its loser in the conflict item.
        var pinWrites = 0;
        var appliedColder = false;
        if (PinPlanning.HasWork(pinItems, conflictItem, decision))
        {
            Step(id, "pins");
            var rowAfter = next ?? row;
            var pins = PinPlanning.Plan(
                pinItems, conflictItem, decision, stored, rowAfter, theirs is not null, Unopt(theirs?.Record.WrittenBy), thisMachine,
                heldAfter);
            if (!pins.Matches(pinPlan))
                return new EntryResult.Deferred("the pin review changed since the decision was made", Busy: false);
            if (pins.ApplyBaseJson is { } applyBase)
            {
                var baseRecord = ApplyPlanning.ParseBase(id, applyBase).Parsed?.Record
                    ?? throw new InvalidOperationException("the row's base cannot be read");
                if (storedRead is not LocalRead.Present present || rowAfter is null)
                    throw new InvalidOperationException("a held edit is applied to an entry with a row");
                var textChanges = !string.Equals(baseRecord.Content, present.View.Content.Text, StringComparison.Ordinal);
                if ((baseVector is not null) != textChanges)
                    return new EntryResult.Deferred("the pin review changed since the decision was made", Busy: false);
                var applied = ApplyPlanning.ApplyBaseContent(tx, id, present, baseRecord, baseVector, _options.HotMaxContentChars);
                appliedColder = !applied.Tier.Equals(present.Tier);
                // As planRow does for a TakeTheirs the hot limit stored colder: a pending tier stamp that the stored tier
                // no longer exceeds the base's for is dropped.
                var rowApplied = ApplyPlanning.DropBeatenTierStamp(
                    ApplyPlanning.WithComparison(rowAfter, applied), applied.Tier, baseRecord, appliedColder);
                GitSyncRows.Write(conn, rowApplied, dbTx);
                rowWritten = true;
            }
            PinPlanning.Write(conn, dbTx, id, pins.Writes, _nowMs());
            pinWrites = pins.Writes.Count;
            if (pins.Conflict is { } conflictWrite)
            {
                ConflictItems.Write(conn, dbTx, id, conflictWrite, _nowMs());
                pinWrites++;
            }
        }

        // Alias D1: the skip's rows are recomputed from the same inputs and must match the plan, else the entry is deferred
        // (an importer wrote a matching import_log row or deleted a target in between). The writes are not counted: a skip
        // is a NoOp (D6).
        if (aliasPlan is not null)
        {
            Step(id, "alias");
            var aliasNow = GitSyncAliases.PlanSkip(conn, id, aliasPlan.Hash, dbTx);
            if (!aliasPlan.Matches(aliasNow))
                return new EntryResult.Deferred("the entry changed since the decision was made", Busy: false);
            foreach (var other in aliasNow!.Inserts)
                GitSyncAliases.InsertOpen(conn, aliasNow.Canonical, other, aliasNow.Hash, dbTx);
        }

        // Milestone 1b-ii, D7 and D8: the close runs before the hold's upsert, so a decision with both leaves one open item.
        if (decision.CloseHeldDelete)
        {
            Step(id, "close");
            GitSyncReviews.DeleteHeld(conn, id, dbTx);
        }
        HeldEntry? heldEntry = null;
        if (decision.Entry is SyncMerge.EntryOutcome.HoldDeletion hold)
        {
            Step(id, "hold");
            var direction = SyncDeletion.directionName(hold.Item);
            var cause = SyncDeletion.holdCause(proven, converted, hold.Item);
            GitSyncReviews.UpsertHeld(conn, new HeldItem(id, direction, theirs is null ? null : blobSha, cause, _nowMs()), dbTx);
            heldEntry = new HeldEntry(id, direction, cause);
        }
        // Milestone 1b-iii, D10: an open local item the decision leaves open follows the record's blob id, so the stale
        // check of a review answer reads the record as of this pull; nothing new is reported in the summary.
        var refreshed = RefreshesHash(held, blobSha, decision);
        if (refreshed)
        {
            Step(id, "refresh");
            GitSyncReviews.RefreshHeldHash(conn, id, blobSha!, dbTx);
        }
        // Milestone 1b-iii, D4: an approved item whose record changed after the answer is shown again, never deleted blind.
        if (reopen)
        {
            Step(id, "reopen");
            GitSyncReviews.ReopenHeld(conn, id, blobSha!, dbTx);
        }

        phase("issue");
        Step(id, "issue");
        GitSyncIssues.Delete(conn, id, GitSyncIssues.ApplyFailed, dbTx);

        phase("commit");
        Step(id, "commit");
        tx.Commit();

        var pushDeletion = decision.Entry.IsPushDeletion;
        var effects = new EntryEffects(plan.Delete, pushDeletion, heldEntry, decision.CloseHeldDelete, NoteWords(decision), MarkerSet(decision));
        var colder = plan.Colder || appliedColder;
        if (plan.Insert)
            return new EntryResult.Carried(CarriedKind.Inserted, colder, effects);
        if (plan.HasStoreOps || rowWritten || TouchesHeldItem(decision) || refreshed || reopen || pinWrites > 0)
            return new EntryResult.Carried(CarriedKind.Updated, colder, effects);
        return pushDeletion ? new EntryResult.Carried(CarriedKind.Unchanged, false, effects) : new EntryResult.NoOp(effects.Repush);
    }

    private EntryResult ApplyDrop(string id, SyncMerge.MergeDecision decision, GitSyncRow? row, LocalRead read, HeldItem? held)
    {
        BeforeTransaction?.Invoke(id);
        SqliteStoreTransaction tx;
        try
        {
            tx = _store.BeginImmediateTransaction();
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return new EntryResult.Deferred("the database is busy", Busy: true);
        }
        TransactionCount++;
        using (tx)
        {
            var phase = "reread";
            try
            {
                tx.BeforeStep = step => BeforeEntryStep?.Invoke(id, step);
                Step(id, "reread");
                var rowNow = GitSyncRows.Read(tx.Connection, id, tx.Transaction);
                var readNow = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
                var heldNow = GitSyncReviews.ReadHeldItem(tx.Connection, id, tx.Transaction);
                if (!Equals(row, rowNow) || !read.Equals(readNow) || readNow is not LocalRead.Absent || !Equals(held, heldNow))
                    return new EntryResult.Deferred("the entry changed since the decision was made", Busy: false);
                phase = "work";
                Step(id, "row");
                GitSyncRows.Delete(tx.Connection, id, tx.Transaction);
                if (decision.CloseHeldDelete)
                {
                    Step(id, "close");
                    GitSyncReviews.DeleteHeld(tx.Connection, id, tx.Transaction);
                }
                // Pin review D4: a dropped row takes the id's pin items with it, and its conflict item (conflict review D3).
                Step(id, "pins");
                foreach (var kind in PinPlanning.Kinds)
                    GitSyncReviews.DeletePinItem(tx.Connection, id, kind, tx.Transaction);
                GitSyncReviews.DeleteConflictItem(tx.Connection, id, tx.Transaction);
                phase = "issue";
                Step(id, "issue");
                GitSyncIssues.Delete(tx.Connection, id, GitSyncIssues.ApplyFailed, tx.Transaction);
                phase = "commit";
                Step(id, "commit");
                tx.Commit();
                return new EntryResult.Carried(
                    CarriedKind.Dropped, false, EntryEffects.None with { Closed = decision.CloseHeldDelete });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return Classify(phase, ex);
            }
        }
    }

    private static bool AgreesWithRow(GitSyncRow? row, LocalRead read) => LocalEntryReader.AgreesWithRow(row, read);
}
