using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

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

    /// <summary>The decision needs a part of git sync that is not built; nothing was changed.</summary>
    public sealed record Postponed(string Reason) : EntryResult;

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

    /// <summary>A null array must stay "no vector": the implicit array-to-memory conversion would make it an empty one.</summary>
    private static ReadOnlyMemory<float>? Memory(float[]? vector) => vector is null ? null : new ReadOnlyMemory<float>(vector);

    private static bool IsBusy(SqliteException ex) => SyncSql.IsBusy(ex);

    private static string OneLine(string text) => SyncSql.OneLine(text);

    private void Step(string id, string step) => BeforeEntryStep?.Invoke(id, step);

    private LocalRead ReadLocal(string id) =>
        LocalEntryReader.Read(
            tier => _store.Get(tier, ContentType.Memory, id),
            () => _store.IsSticky(ContentType.Memory, id));

    private bool InKnowledge(string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(tier => _store.Get(tier, ContentType.Knowledge, id) is not null);

    private static bool InKnowledge(SqliteStoreTransaction tx, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(tier => tx.Get(tier, ContentType.Knowledge, id) is not null);

    // ---------------------------------------------------------------------------------------------
    // One entry

    /// <summary>
    /// Applies one id: reads its row and local entry, runs <c>SyncMerge.decide</c>, postpones what this milestone does
    /// not carry out, computes the vector, then carries the decision out in one immediate transaction that first
    /// re-reads the entry (decisions 7 to 13). <paramref name="theirs"/> is null when the id has no accepted record;
    /// <paramref name="held"/> is the id's open held-delete item as the pull read it (milestone 1b-ii, D8), null when it
    /// has none; <paramref name="blobSha"/> is the record's blob id; <paramref name="guard"/> is the pull's delete guard verdict (required: <c>default</c> trips nothing),
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

    private EntryResult ApplyCore(string id, ParsedRecord? theirs, bool proven, HeldItem? held, string? blobSha, DeleteGuardVerdict guard)
    {
        BeforeEntry?.Invoke(id);
        var conn = _store.Connection;

        if (!SyncValidation.isEligibleId(id))
            return new EntryResult.Skipped("the id is not eligible");

        GitSyncRow? row;
        LocalRead read;
        try
        {
            row = GitSyncRows.Read(conn, id);
            read = ReadLocal(id);
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
            var parsedBase = ParseBase(id, baseText);
            if (parsedBase.Parsed is null)
                return new EntryResult.Failed("the row's base cannot be read: " + OneLine(SyncValidation.RejectModule.describe(parsedBase.Rejected)));
            oldBase = parsedBase.Parsed;
        }

        var decision = SyncMerge.decide(BuildInput(row, oldBase, theirs, local, proven, held));
        var converted = guard.Converts(decision);
        if (converted)
            decision = SyncDeletion.holdInstead(decision); // D4: kept base update, no notes, no row state change

        if (SyncApply.postponement(decision) is { } postponed)
            return new EntryResult.Postponed(SyncApply.reasonName(postponed.Value));
        if (decision.Entry.IsDefer)
            return new EntryResult.Deferred("the merge rules leave this entry for the next pull", Busy: false);
        if (decision.Entry.IsDropRow)
            return ApplyDrop(id, decision, row, read, held);

        StorePlan plan;
        try
        {
            plan = PlanStore(decision, theirs?.Record, local);
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

        // Decision 13: a decision that changes nothing opens no transaction (unless a stamp must be issued). A held-delete
        // write or close, and a decision with notes, always change something, and a push deletion is reported even when its
        // row already agrees (milestone 1b-ii, D9).
        if (!plan.HasStoreOps && !HasRestamp(decision) && !TouchesHeldItem(decision) && decision.Notes.IsEmpty
            && !decision.Entry.IsPushDeletion)
        {
            var (preview, error) = BuildRow(id, decision, row, oldBase, theirs, local, hadEntry: local is not null, plan.Colder, _ => null);
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
                return CarryOut(id, tx, decision, converted, plan, vector, row, read, held, proven, blobSha, oldBase, theirs, local, p => phase = p);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return Classify(phase, ex);
            }
        }
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

    /// <summary>The row's <c>base_json</c> read as the record at the id's path.</summary>
    private static ParseResult ParseBase(string id, string baseText) =>
        RecordReader.Parse(Encoding.UTF8.GetBytes(baseText), SyncValidation.recordPath(id));

    private static bool HasRestamp(SyncMerge.MergeDecision d) =>
        d.FreshContentStamp
        || d.Entry is SyncMerge.EntryOutcome.MergeGroups m
        && (m.Item.Content.Item2.IsRestampPending || m.Item.Sticky.Item2.IsRestampPending || m.Item.Tier.Item2.IsRestampPending);

    /// <summary>The decision writes a held-delete item or closes the open one.</summary>
    private static bool TouchesHeldItem(SyncMerge.MergeDecision d) => d.CloseHeldDelete || d.Entry.IsHoldDeletion;

    /// <summary>The <c>repush_groups</c> text the decision writes (milestone 1b-ii, D16); null when it clears or keeps the marker.</summary>
    private static string? MarkerSet(SyncMerge.MergeDecision d) =>
        SyncApply.repushMarker(d) is SyncApply.MarkerChange.SetMarker set ? Unopt(set.Item) : null;

    /// <summary>The decision's notes as the words of <see cref="NoteEntry"/>.</summary>
    private static IReadOnlyList<string> NoteWords(SyncMerge.MergeDecision d) =>
        d.Notes.Select(n => n.IsEditBeatsDeletion ? "edit-beats-deletion" : "pending-changes-dropped").ToList();

    private static bool AnyBaseBecomesTheirs(SyncMerge.BaseUpdate u) =>
        u.Content.IsBaseBecomesTheirs || u.Tags.IsBaseBecomesTheirs || u.Sticky.IsBaseBecomesTheirs || u.Tier.IsBaseBecomesTheirs;

    // ---------------------------------------------------------------------------------------------
    // What the decision does to the store

    /// <summary>
    /// The store operations of a decision. <see cref="Delete"/>: a remote deletion removes the entry and its vector
    /// (milestone 1b-ii, D6); the entry is gone afterwards, so it is not read back.
    /// </summary>
    private sealed record StorePlan(
        bool Insert,
        Tier? InsertTier,
        bool Unpin,
        UpdateEntryOpts? Update,
        Tier? MoveTo,
        bool TextChanged,
        bool Colder,
        bool Delete)
    {
        public bool NeedsVector => Insert || TextChanged;

        public bool HasStoreOps => Insert || Unpin || Update is not null || MoveTo is not null || TextChanged || Delete;
    }

    private StorePlan PlanStore(SyncMerge.MergeDecision d, SyncRecord? theirs, SyncMerge.LocalEntry? local)
    {
        if (d.Entry.IsInsertFromRecord)
        {
            var record = theirs ?? throw new InvalidOperationException("an insert needs a record");
            var colder = SyncApply.demotesToWarm(_options.HotMaxContentChars, d, record, null);
            return new StorePlan(true, colder ? Tier.Warm : record.Placement.Tier, false, null, null, true, colder, false);
        }

        if (d.Entry.IsDeleteEntry)
        {
            if (local is null)
                throw new InvalidOperationException("a deletion needs the local entry");
            return new StorePlan(false, null, false, null, null, false, false, true);
        }

        if (d.Entry is not SyncMerge.EntryOutcome.MergeGroups merge)
            return new StorePlan(false, null, false, null, null, false, false, false);

        var m = merge.Item;
        var l = local ?? throw new InvalidOperationException("a merge needs the local entry");
        var colderResult = SyncApply.demotesToWarm(_options.HotMaxContentChars, d, theirs ?? throw new InvalidOperationException("a merge needs a record"), local);

        var content = m.Content.Item1;
        var takes = content.IsTakeTheirs;
        var textChanged = takes && theirs.Content != l.Content.Text;
        var apply = m.Tags.ApplyLocal;
        UpdateEntryOpts? update = null;
        if (takes || content.IsAdoptCreatedAt || apply)
        {
            var project = takes ? Unopt(theirs.Project) : null;
            var scope = takes ? Unopt(theirs.Scope) : null;
            var source = takes ? Unopt(theirs.Source) : null;
            update = new UpdateEntryOpts
            {
                Content = takes ? theirs.Content : null,
                Project = project,
                ClearProject = takes && project is null,
                Scope = takes ? scope ?? "" : null,
                EntryType = takes ? theirs.EntryType : null,
                SourceTool = source,
                ClearSourceTool = takes && source is null,
                CreatedAt = takes || content.IsAdoptCreatedAt ? theirs.CreatedAtMs : null,
                Tags = apply ? m.Tags.Result.ToList() : null,
            };
        }

        var desired = m.Tier.Item1 is null ? l.Tier : m.Tier.Item1.Value;
        if (colderResult)
            desired = Tier.Warm;
        Tier? moveTo = desired.Equals(l.Tier) ? null : desired;
        return new StorePlan(false, null, m.Sticky.Item1.IsUnpinLocally, update, moveTo, textChanged, colderResult, false);
    }

    // ---------------------------------------------------------------------------------------------
    // The transaction

    private EntryResult CarryOut(
        string id,
        SqliteStoreTransaction tx,
        SyncMerge.MergeDecision decision,
        bool converted,
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
        Action<string> phase)
    {
        tx.BeforeStep = step => BeforeEntryStep?.Invoke(id, step);
        var conn = tx.Connection;
        var dbTx = tx.Transaction;

        phase("reread");
        Step(id, "reread");
        var rowNow = GitSyncRows.Read(conn, id, dbTx);
        var readNow = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
        var heldNow = GitSyncReviews.ReadOpenHeldItem(conn, id, dbTx);
        if (!Equals(row, rowNow) || !read.Equals(readNow) || !AgreesWithRow(rowNow, readNow) || !Equals(held, heldNow))
            return new EntryResult.Deferred("the entry changed since the decision was made", Busy: false);
        if (plan.Insert && InKnowledge(tx, id))
            return new EntryResult.Deferred("the id now exists as a knowledge entry", Busy: false);

        phase("work");
        var current = (read as LocalRead.Present)?.Tier;
        if (plan.Delete)
        {
            tx.Delete(current!, ContentType.Memory, id);
        }
        else if (plan.Insert)
        {
            var record = theirs!.Record;
            tx.Insert(
                plan.InsertTier!, ContentType.Memory,
                new InsertEntryOpts(
                    Content: record.Content,
                    SourceTool: Unopt(record.Source),
                    Project: Unopt(record.Project),
                    Tags: record.Tags.ToList(),
                    Id: id,
                    Scope: Unopt(record.Scope),
                    EntryType: record.EntryType,
                    CreatedAt: record.CreatedAtMs),
                Memory(vector));
        }
        else
        {
            if (plan.Unpin)
                tx.SetSticky(ContentType.Memory, id, false);
            if (plan.Update is not null)
                tx.Update(current!, ContentType.Memory, id, plan.Update);
            if (plan.MoveTo is not null)
                tx.Move(current!, ContentType.Memory, plan.MoveTo, ContentType.Memory, id, plan.TextChanged ? Memory(vector) : null);
            else if (plan.TextChanged)
                tx.ReplaceEmbedding(current!, ContentType.Memory, id, vector!);
        }

        var storedRead = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));
        var stored = (storedRead as LocalRead.Present)?.View;
        if (plan.Delete && storedRead is not LocalRead.Absent)
            throw new InvalidOperationException("the entry is still there after it was deleted");
        if (plan.HasStoreOps && !plan.Delete && stored is null)
            throw new InvalidOperationException("the entry cannot be read back after it was written");

        // A deleted entry's row is planned with no stored entry, so its scanned comparison columns are left as they are.
        Step(id, "row");
        var (next, error) = BuildRow(
            id, decision, row, oldBase, theirs, plan.Delete ? null : stored ?? local, hadEntry: local is not null, plan.Colder,
            floor => IssueStamp(conn, dbTx, floor));
        if (error is not null)
            throw new InvalidOperationException(error);
        var rowWritten = false;
        if (next is not null && !next.Equals(row))
        {
            GitSyncRows.Write(conn, next, dbTx);
            rowWritten = true;
        }

        if (plan.Insert)
        {
            var record = theirs!.Record;
            var originHash = record.Origin is null ? null : record.Origin.Value.Hash;
            var hash = originHash ?? SyncScan.contentHash(stored!.Content);
            tx.LogImport("git-sync", "sync:" + id, hash, id, stored!.Tier, ContentType.Memory);
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

        phase("issue");
        Step(id, "issue");
        GitSyncIssues.Delete(conn, id, GitSyncIssues.ApplyFailed, dbTx);

        phase("commit");
        Step(id, "commit");
        tx.Commit();

        var pushDeletion = decision.Entry.IsPushDeletion;
        var effects = new EntryEffects(plan.Delete, pushDeletion, heldEntry, decision.CloseHeldDelete, NoteWords(decision), MarkerSet(decision));
        if (plan.Insert)
            return new EntryResult.Carried(CarriedKind.Inserted, plan.Colder, effects);
        if (plan.HasStoreOps || rowWritten || TouchesHeldItem(decision))
            return new EntryResult.Carried(CarriedKind.Updated, plan.Colder, effects);
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
                var heldNow = GitSyncReviews.ReadOpenHeldItem(tx.Connection, id, tx.Transaction);
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

    /// <summary>
    /// A stamp above <paramref name="floor"/> from the persisted clock (read and written inside the entry's transaction),
    /// or null when this machine has no valid id or the clock range is exhausted.
    /// </summary>
    private FSharpOption<SyncStamp.Stamp>? IssueStamp(Microsoft.Data.Sqlite.SqliteConnection conn, SqliteTransaction tx, FSharpOption<SyncStamp.Stamp>? floor)
    {
        var machineText = SyncSql.GetMeta(conn, SyncIdentity.MetaKeys.Machine, tx);
        var machine = machineText is null ? null : SyncStamp.MachineIdModule.tryCreate(machineText);
        if (machine is null)
            return null;
        var clockText = SyncSql.GetMeta(conn, SyncIdentity.MetaKeys.Hlc, tx);
        var clock = clockText is null ? null : SyncStamp.ClockModule.tryDeserialize(clockText);
        var result = SyncStamp.ClockModule.issue(machine.Value, _nowMs(), clock, floor);
        if (!result.IsOk)
            return null;
        var issued = result.ResultValue;
        if (issued.Clock is not null && (clock is null || !issued.Clock.Value.Equals(clock.Value)))
            SyncSql.SetMeta(conn, SyncIdentity.MetaKeys.Hlc, SyncStamp.ClockModule.serialize(issued.Clock.Value), tx);
        return FSharpOption<SyncStamp.Stamp>.Some(issued.Stamp);
    }

    // ---------------------------------------------------------------------------------------------
    // The row

    /// <summary>
    /// The row the entry has after the decision (decision 12.3): <c>null</c> when no row is to be written. The base is
    /// rendered only when a group's base becomes the record's; <c>repush_groups</c> is written or kept as <c>planRow</c>
    /// says (milestone 1b-ii, D16; an <c>older</c> group keeps its base, which a later repush reads).
    /// </summary>
    private (GitSyncRow? Row, string? Error) BuildRow(
        string id,
        SyncMerge.MergeDecision decision,
        GitSyncRow? row,
        ParsedRecord? oldBase,
        ParsedRecord? theirs,
        SyncMerge.LocalEntry? stored,
        bool hadEntry,
        bool colder,
        Func<FSharpOption<SyncStamp.Stamp>?, FSharpOption<SyncStamp.Stamp>?> issue)
    {
        var before = new SyncApply.RowBefore(
            row is null ? null : Some(row.State),
            new SyncMerge.GroupStamps(Opt(row?.ContentStamp), Opt(row?.StickyStamp), Opt(row?.TierStamp)),
            Opt(row?.PushingStamps),
            Opt(oldBase?.Record.Placement.Tier),
            Opt(oldBase?.Record.ContentStamp));
        var planned = SyncApply.planRow(
            decision, before, Opt(theirs?.Record), Opt(stored), hadEntry, colder,
            FuncConvert.FromFunc(issue));
        if (planned.IsError)
            return (null, planned.ErrorValue);
        if (planned.ResultValue is null)
            return (null, null);
        var after = planned.ResultValue.Value;

        var baseJson = row?.BaseJson;
        if (AnyBaseBecomesTheirs(decision.Base))
        {
            if (theirs is null)
                return (null, "the decision takes the record into the base but there is no record");
            baseJson = BaseRenderer.Render(theirs, oldBase, decision.Base);
            if (baseJson is null)
                return (null, "the record cannot be encoded as the row's base");
        }

        var next = row ?? new GitSyncRow(id, after.State, null, null, null, null, null, null, null, null, null, null, null, null, null);
        next = next with
        {
            State = after.State,
            BaseJson = baseJson,
            ContentStamp = Unopt(after.Pending.Content),
            StickyStamp = Unopt(after.Pending.Sticky),
            TierStamp = Unopt(after.Pending.Tier),
            PushingStamps = Unopt(after.PushingText),
            RepushGroups = after.Repush is SyncApply.MarkerChange.SetMarker set ? Unopt(set.Item) : next.RepushGroups,
        };
        if (after.Comparison is not null)
        {
            var c = after.Comparison.Value;
            next = next with
            {
                ScannedContentHash = c.ContentHash,
                ScannedTagsHash = c.TagsHash,
                LocalSticky = c.Sticky,
                LocalTier = c.Tier,
            };
        }
        return (next, null);
    }
}
