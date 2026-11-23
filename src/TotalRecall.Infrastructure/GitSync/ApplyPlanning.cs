using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

// Git sync, milestone 1b-iii part 2 (held deletions), D1: the row and insert planning of the pull's apply, shared so the
// review service answers a held deletion with the same rows, inserts, stamps and import-log lines the apply writes, not
// a second copy. Moved from GitSyncApplier.Entry.cs unchanged in behaviour; the applier passes its own hot limit and clock.

/// <summary>The store operations, rows, stamps and inserts of a merge decision, as the apply and the review service carry them out.</summary>
internal static class ApplyPlanning
{
    private static FSharpOption<T> Some<T>(T value) => FSharpOption<T>.Some(value);

    private static FSharpOption<T>? Opt<T>(T? value) where T : class => value is null ? null : FSharpOption<T>.Some(value);

    private static T? Unopt<T>(FSharpOption<T>? option) where T : class => option is null ? null : option.Value;

    /// <summary>A null array must stay "no vector": the implicit array-to-memory conversion would make it an empty one.</summary>
    private static ReadOnlyMemory<float>? Memory(float[]? vector) => vector is null ? null : new ReadOnlyMemory<float>(vector);

    /// <summary>
    /// The store operations of a decision. <see cref="Delete"/>: a remote deletion removes the entry and its vector
    /// (milestone 1b-ii, D6); the entry is gone afterwards, so it is not read back.
    /// </summary>
    internal sealed record StorePlan(
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

    /// <summary>
    /// The store operations of <paramref name="d"/> for the record <paramref name="theirs"/> and the local entry
    /// <paramref name="local"/>; <paramref name="hotMaxContentChars"/> is the hot limit (0 or less: none) that stores or
    /// moves an entry to the warm tier. Throws <see cref="InvalidOperationException"/> when an input the decision needs is missing.
    /// </summary>
    internal static StorePlan PlanStore(SyncMerge.MergeDecision d, SyncRecord? theirs, SyncMerge.LocalEntry? local, int hotMaxContentChars)
    {
        if (d.Entry.IsInsertFromRecord)
        {
            var record = theirs ?? throw new InvalidOperationException("an insert needs a record");
            var colder = SyncApply.demotesToWarm(hotMaxContentChars, d, record, null);
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
        var colderResult = SyncApply.demotesToWarm(hotMaxContentChars, d, theirs ?? throw new InvalidOperationException("a merge needs a record"), local);

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

    /// <summary>The row's <c>base_json</c> read as the record at the id's path.</summary>
    internal static ParseResult ParseBase(string id, string baseText) =>
        RecordReader.Parse(Encoding.UTF8.GetBytes(baseText), SyncValidation.recordPath(id));

    /// <summary>Whether a knowledge entry in any tier has the id, read inside the caller's transaction (a memory is never inserted under it).</summary>
    internal static bool InKnowledge(SqliteStoreTransaction tx, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(tier => tx.Get(tier, ContentType.Knowledge, id) is not null);

    /// <summary>
    /// Inserts the record as the memory <paramref name="id"/> in <paramref name="tier"/> with its vector (null: none), inside
    /// the caller's transaction: the record's text, source, project, tags, scope, type and <c>created_at</c>.
    /// </summary>
    internal static void InsertRecord(SqliteStoreTransaction tx, string id, Tier tier, SyncRecord record, float[]? vector) =>
        tx.Insert(
            tier, ContentType.Memory,
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

    /// <summary>
    /// The <c>import_log</c> line of a memory inserted from a record (<c>git-sync</c>, <c>sync:&lt;id&gt;</c>): the hash is
    /// the record's origin hash, else the content hash of the entry as stored.
    /// </summary>
    internal static void LogImport(SqliteStoreTransaction tx, string id, SyncRecord record, SyncMerge.LocalEntry stored)
    {
        var originHash = record.Origin is null ? null : record.Origin.Value.Hash;
        var hash = originHash ?? SyncScan.contentHash(stored.Content);
        tx.LogImport("git-sync", "sync:" + id, hash, id, stored.Tier, ContentType.Memory);
    }

    /// <summary>
    /// A stamp above <paramref name="floor"/> from the persisted clock at <paramref name="nowMs"/> (read and written inside
    /// the caller's transaction), or null when this machine has no valid id or the clock range is exhausted.
    /// </summary>
    internal static FSharpOption<SyncStamp.Stamp>? IssueStamp(MsSqliteConnection conn, SqliteTransaction tx, long nowMs, FSharpOption<SyncStamp.Stamp>? floor)
    {
        var machineText = SyncSql.GetMeta(conn, SyncIdentity.MetaKeys.Machine, tx);
        var machine = machineText is null ? null : SyncStamp.MachineIdModule.tryCreate(machineText);
        if (machine is null)
            return null;
        var clockText = SyncSql.GetMeta(conn, SyncIdentity.MetaKeys.Hlc, tx);
        var clock = clockText is null ? null : SyncStamp.ClockModule.tryDeserialize(clockText);
        var result = SyncStamp.ClockModule.issue(machine.Value, nowMs, clock, floor);
        if (!result.IsOk)
            return null;
        var issued = result.ResultValue;
        if (issued.Clock is not null && (clock is null || !issued.Clock.Value.Equals(clock.Value)))
            SyncSql.SetMeta(conn, SyncIdentity.MetaKeys.Hlc, SyncStamp.ClockModule.serialize(issued.Clock.Value), tx);
        return FSharpOption<SyncStamp.Stamp>.Some(issued.Stamp);
    }

    private static bool AnyBaseBecomesTheirs(SyncMerge.BaseUpdate u) =>
        u.Content.IsBaseBecomesTheirs || u.Tags.IsBaseBecomesTheirs || u.Sticky.IsBaseBecomesTheirs || u.Tier.IsBaseBecomesTheirs;

    /// <summary>
    /// The row the entry has after the decision (decision 12.3): <c>null</c> when no row is to be written. The base is
    /// rendered only when a group's base becomes the record's; <c>repush_groups</c> is written or kept as <c>planRow</c>
    /// says (milestone 1b-ii, D16; an <c>older</c> group keeps its base, which a later repush reads).
    /// </summary>
    internal static (GitSyncRow? Row, string? Error) BuildRow(
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
