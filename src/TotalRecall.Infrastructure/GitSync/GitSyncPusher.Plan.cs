using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.GitSync;

/// <summary>
/// An entry whose file will be written: a new record, the record at <c>L</c> patched, or (milestone 1b-ii, D15) a
/// write-back: the row's base patched with the entry's groups, for a record a text edit saved from a remote deletion.
/// <see cref="IsNew"/> is true when the file is new in the repository (a new entry or a write-back; counted as <c>+</c>).
/// <see cref="Plan"/> holds the groups with a pending change; <see cref="Repush"/> the groups a reset reverted that the
/// record at <c>L</c> gets back from the row's base (D16; null when there are none), and <see cref="Pushed"/> the stamps
/// of both (a repushed group's stamp is the base's).
/// </summary>
internal sealed record PlannedEntry(
    string Id,
    bool IsNew,
    byte[] Bytes,
    SyncRecordModule.SyncRecord Record,
    SyncPush.GroupPlan? Plan,
    SyncMerge.GroupStamps Pushed,
    SyncPush.RepushPlan? Repush);

/// <summary>An entry that needs no file: the record at <c>L</c> already says what the entry says (decision 6).</summary>
internal sealed record UnchangedEntry(string Id, string BaseText, SyncMerge.GroupStamps Pushed);

/// <summary>
/// The plan's result. <see cref="Planned.Removals"/> are the ids whose record the push removes (milestone 1b-ii, D14): the
/// apply decided them, so the plan transaction reads and writes nothing for them.
/// </summary>
internal abstract record PlanResult
{
    private PlanResult() { }

    public sealed record Stop(PushOutcome Outcome) : PlanResult;

    public sealed record Planned(
        IReadOnlyList<PlannedEntry> Entries,
        IReadOnlyList<UnchangedEntry> Unchanged,
        IReadOnlyList<PushItem> Blocked,
        IReadOnlyList<PushItem> Deferred,
        IReadOnlyList<PushItem> Skipped,
        IReadOnlyList<string> Removals) : PlanResult
    {
        public PushSummary Summary(string? commit) =>
            new(
                commit,
                Entries.Count(e => e.IsNew),
                Entries.Count(e => !e.IsNew),
                Unchanged.Count,
                [.. Entries.Select(e => e.Id).Order(StringComparer.Ordinal)],
                Removals,
                Blocked, Deferred, Skipped);
    }
}

public sealed partial class GitSyncPusher
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>The skip reason of an id the apply listed for removal whose record this push cannot remove (D14).</summary>
    internal const string RemovalNotPossible = "removal-not-possible";

    /// <summary>
    /// Decisions 6 to 9: in one immediate transaction, re-reads each candidate that was not left out, requires that it
    /// still agrees with its row, builds the record to write, and writes the block marks and the <c>pushing_stamps</c>
    /// of the entries that will be written. A candidate with a base and no accepted record at <c>L</c> is a write-back
    /// (milestone 1b-ii, D15): its record is the row's base patched like a changed entry's, and only a content group that
    /// is written lets it out. A row with a <c>repush_groups</c> marker (D16) also gets back, in the record at <c>L</c>, each
    /// flagged group that has no pending stamp, from its base with the base's stamp (no clock call). Writes no file. A
    /// failure rolls the whole transaction back.
    /// </summary>
    internal PlanResult Plan(PushPrepared prepared, CancellationToken ct)
    {
        BeforePlanTransaction?.Invoke();
        if (ct.IsCancellationRequested)
            return new PlanResult.Stop(Cancelled());

        var clearable = prepared.Rows.Values
            .Where(r => r.State == SyncMerge.RowState.Active && !prepared.Candidates.ContainsKey(r.EntryId) && !prepared.Deleted.Contains(r.EntryId)
                && r.BlockedReason is { } reason && SyncPush.isWriterReason(reason))
            .Select(r => r.EntryId)
            .Order(StringComparer.Ordinal)
            .ToList();
        var work = prepared.Candidates.Keys.Where(id => !prepared.Excluded.ContainsKey(id)).ToList();
        var skipped = prepared.Excluded.Select(p => new PushItem(p.Key, SyncPush.exclusionName(p.Value)))
            .Concat(prepared.NotRemovable.Select(id => new PushItem(id, RemovalNotPossible)))
            .ToList();
        if (work.Count == 0 && clearable.Count == 0)
            return new PlanResult.Planned([], [], [], [], skipped, prepared.Removals);

        SqliteStoreTransaction tx;
        try
        {
            tx = _store.BeginImmediateTransaction();
        }
        catch (SqliteException ex) when (SyncSql.IsBusy(ex))
        {
            return new PlanResult.Stop(Fail("Busy", "the database is busy", SyncPush.Published.No));
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            return new PlanResult.Stop(Fail("Store", ex.Message, SyncPush.Published.No));
        }
        TransactionCount++;
        using (tx)
        {
            try
            {
                var entries = new List<PlannedEntry>();
                var unchanged = new List<UnchangedEntry>();
                var blocked = new List<PushItem>();
                var deferred = new List<PushItem>();

                foreach (var id in work)
                    PlanOne(prepared, tx, id, entries, unchanged, blocked, deferred, skipped);
                foreach (var id in clearable)
                    ClearMarkOf(tx, id);

                tx.Commit();
                return new PlanResult.Planned(entries, unchanged, blocked, deferred, skipped, prepared.Removals);
            }
            catch (SqliteException ex) when (SyncSql.IsBusy(ex))
            {
                return new PlanResult.Stop(Fail("Busy", "the database is busy", SyncPush.Published.No));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return new PlanResult.Stop(Fail("Store", ex.Message, SyncPush.Published.No));
            }
        }
    }

    private void ClearMarkOf(SqliteStoreTransaction tx, string id)
    {
        Step(id, "block");
        var row = GitSyncRows.Read(tx.Connection, id, tx.Transaction);
        if (row is null || row.State != SyncMerge.RowState.Active)
            return;
        var action = SyncPush.markAction(Opt(row.BlockedReason), null, false);
        if (action.IsClearMark)
            GitSyncRows.Write(tx.Connection, row with { BlockedReason = null, BlockedHash = null }, tx.Transaction);
    }

    private void PlanOne(
        PushPrepared prepared, SqliteStoreTransaction tx, string id,
        List<PlannedEntry> entries, List<UnchangedEntry> unchanged, List<PushItem> blocked, List<PushItem> deferred, List<PushItem> skipped)
    {
        var info = prepared.Candidates[id];
        Step(id, "reread");
        var row = GitSyncRows.Read(tx.Connection, id, tx.Transaction);
        var read = LocalEntryReader.Read(t => tx.Get(t, ContentType.Memory, id), () => tx.IsSticky(ContentType.Memory, id));

        if (row is null || row.State != SyncMerge.RowState.Active || !string.Equals(row.BaseJson, info.Row.BaseJson, StringComparison.Ordinal))
        {
            deferred.Add(new PushItem(id, "changed-since-scan"));
            return;
        }
        if (read is not LocalRead.Present present)
        {
            deferred.Add(new PushItem(id, "unreadable-entry"));
            return;
        }
        if (!LocalEntryReader.AgreesWithRow(row, read))
        {
            deferred.Add(new PushItem(id, "changed-since-scan"));
            return;
        }

        Step(id, "plan");
        var local = present.View;
        var pending = new SyncMerge.GroupStamps(Opt(row.ContentStamp), Opt(row.StickyStamp), Opt(row.TierStamp));
        byte[]? bytes;
        SyncRecordModule.SyncRecord? record;
        SyncPush.GroupPlan? plan = null;
        SyncPush.RepushPlan? repush = null;
        var pushed = pending;
        var isNew = row.BaseJson is null;
        var writeBack = !isNew && !prepared.Tree.Accepted.ContainsKey(id);
        SyncValidation.Block? violation = null;

        if (isNew)
        {
            var origin = FirstOrigin(tx, id);
            var made = SyncPush.newRecord(id, local, pending, _label, origin is null ? null : FSharpOption<SyncRecordModule.Origin>.Some(origin));
            if (made.IsError)
            {
                skipped.Add(new PushItem(id, made.ErrorValue));
                return;
            }
            record = made.ResultValue;
            bytes = RecordWriter.Write(record).Bytes;
            if (bytes is null)
                violation = SyncValidation.Block.NotEncodable;
        }
        else
        {
            // the record to patch: the one at L, or for a write-back the row's base (its unknown fields survive)
            var start = writeBack ? info.Base : info.Record;
            if (start is null || (!writeBack && info.RecordBytes is null))
            {
                skipped.Add(new PushItem(id, writeBack ? "unreadable-base" : "record-unreadable"));
                return;
            }
            plan = SyncPush.planChanged(start.Record, start.UnknownSource is not null, local, pending);
            // the groups a reset reverted, from the base (D16); a write-back starts from the base, so it has none to add
            if (!writeBack)
            {
                var flagged = SyncPush.repushPlan(info.Base!.Record, SyncPush.repushGroupsOf(row.RepushGroups), pending);
                repush = SyncPush.nothingToRepush(flagged) ? null : flagged;
            }
            pushed = repush is null ? SyncPush.pushedStamps(plan) : SyncPush.repushedStamps(plan, repush);
            if (!plan.Stale.IsEmpty)
                skipped.Add(new PushItem(id, "stale-stamp"));
            if (writeBack && plan.Content is null)
            {
                // only a pending text change newer than the base may write a vanished record back (D15, D18): a stale
                // content stamp is listed as stale above, a content stamp that is gone since the preparation as missing
                if (!plan.Stale.Contains("content"))
                    skipped.Add(new PushItem(id, SyncPush.exclusionName(SyncPush.Exclusion.RecordMissing)));
                return;
            }
            if (SyncPush.nothingToWrite(plan) && repush is null)
            {
                unchanged.Add(Unchanged(id, info, pushed));
                ApplyMark(tx, row, local, violation: null, candidate: true, pushing: null, keepPushing: true, id);
                return;
            }

            var patched = RecordPatcher.Patch(start, ToPatch(plan, local, repush, info.Base!));
            if (patched.Failure == PatchFailure.MissingTierStamp)
            {
                skipped.Add(new PushItem(id, "missing-stamp"));
                return;
            }
            record = patched.Record;
            bytes = patched.Bytes;
            if (patched.Failure == PatchFailure.NotEncodable || bytes is null || record is null)
                violation = SyncValidation.Block.NotEncodable;
        }

        if (violation is null && record is not null && bytes is not null)
            violation = SyncValidation.checkWritable(record, bytes.Length) is { } found ? found.Value : null;

        if (violation is { } block)
        {
            blocked.Add(new PushItem(id, SyncPush.blockName(block)));
            ApplyMark(tx, row, local, block, candidate: true, pushing: null, keepPushing: true, id);
            return;
        }

        if (!isNew && !writeBack && bytes!.AsSpan().SequenceEqual(info.RecordBytes!))
        {
            // also a repush whose record at L already says what the base says: nothing to write, the finalize clears the marker
            unchanged.Add(Unchanged(id, info, pushed));
            ApplyMark(tx, row, local, violation: null, candidate: true, pushing: null, keepPushing: true, id);
            return;
        }

        entries.Add(new PlannedEntry(id, isNew || writeBack, bytes!, record!, plan, pushed, repush));
        Step(id, "pushing");
        ApplyMark(tx, row, local, violation: null, candidate: true, pushing: SyncApply.PushingStamps.encode(pushed), keepPushing: false, id);
    }

    private static UnchangedEntry Unchanged(string id, CandidateInfo info, SyncMerge.GroupStamps pushed)
    {
        var rendered = RecordWriter.Write(info.Record!).Bytes ?? info.RecordBytes!;
        var text = StrictUtf8.GetString(rendered);
        return new UnchangedEntry(id, text, pushed);
    }

    /// <summary>Writes the row once: the block mark the rule asks for and, for an entry that will be written, its <c>pushing_stamps</c>.</summary>
    private void ApplyMark(
        SqliteStoreTransaction tx, GitSyncRow row, SyncMerge.LocalEntry local, SyncValidation.Block? violation, bool candidate,
        FSharpOption<string>? pushing, bool keepPushing, string id)
    {
        Step(id, "block");
        var current = Opt(row.BlockedReason);
        var action = SyncPush.markAction(current, violation is { } v ? FSharpOption<SyncValidation.Block>.Some(v) : null, candidate);
        var next = row;
        if (action.IsSetMark)
            next = next with { BlockedReason = ((SyncPush.MarkAction.SetMark)action).reason, BlockedHash = SyncScan.contentHash(local.Content) };
        else if (action.IsClearMark)
            next = next with { BlockedReason = null, BlockedHash = null };
        if (!keepPushing)
            next = next with { PushingStamps = pushing?.Value };
        if (!ReferenceEquals(next, row))
            GitSyncRows.Write(tx.Connection, next, tx.Transaction);
    }

    /// <summary>The first <c>import_log</c> row of the entry that does not come from git sync itself, as an origin; none when there is no such row.</summary>
    private static SyncRecordModule.Origin? FirstOrigin(SqliteStoreTransaction tx, string id)
    {
        using var cmd = tx.Connection.CreateCommand();
        cmd.Transaction = tx.Transaction;
        cmd.CommandText =
            "SELECT source_tool, content_hash FROM import_log WHERE target_entry_id = $id AND source_tool <> 'git-sync' " +
            "ORDER BY timestamp, id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SyncRecordModule.Origin(r.GetString(0), r.GetString(1)) : null;
    }

    /// <summary>
    /// The patch that writes a <see cref="SyncPush.GroupPlan"/> and a <see cref="SyncPush.RepushPlan"/> (D16): the groups they
    /// name and nothing else (the two never name the same group). A repushed group takes the base's values and stamp. The
    /// base's source is set, or cleared when the base has none; a base whose source this version cannot parse keeps the
    /// record's source (the patch cannot write the unparsed text, and clearing it would lose a source).
    /// </summary>
    private static RecordPatch ToPatch(SyncPush.GroupPlan plan, SyncMerge.LocalEntry local, SyncPush.RepushPlan? repush, ParsedRecord baseRecord)
    {
        ContentPatch? content = null;
        if (repush?.Content is { } rc)
        {
            var r = rc.Value;
            Change<SourceTool> source = r.Source is { } tool
                ? Change<SourceTool>.Set(tool.Value)
                : baseRecord.UnknownSource is null ? Change<SourceTool>.Clear : Change<SourceTool>.Keep;
            content = new ContentPatch(
                r.Text,
                r.EntryType,
                r.Stamp,
                r.Project is { } p ? Change<string>.Set(p.Value) : Change<string>.Clear,
                r.Scope is { } s ? Change<string>.Set(s.Value) : Change<string>.Clear,
                source);
        }
        else if (plan.Content is { } c)
        {
            var w = c.Value;
            Change<SourceTool> source = w.Source switch
            {
                { IsSetSource: true } set => Change<SourceTool>.Set(((SyncPush.SourceChange.SetSource)set).Item),
                { IsClearSource: true } => Change<SourceTool>.Clear,
                _ => Change<SourceTool>.Keep,
            };
            content = new ContentPatch(
                local.Content.Text,
                local.Content.EntryType,
                w.Stamp,
                w.Project is { } p ? Change<string>.Set(p.Value) : Change<string>.Clear,
                w.Scope is { } s ? Change<string>.Set(s.Value) : Change<string>.Clear,
                source);
        }

        IReadOnlyList<string>? tags = plan.Tags is { } t ? [.. t.Value] : null;
        var tierStamp = plan.Tier?.Value;
        StickyPatch? sticky = plan.Sticky is { } st ? new StickyPatch(local.Sticky, st.Value, local.Sticky ? tierStamp : null) : null;
        if (repush?.Sticky is { } rs)
            sticky = new StickyPatch(rs.Value.Sticky, rs.Value.Stamp, rs.Value.TierStamp?.Value);
        TierPatch? tier = tierStamp is { } ts ? new TierPatch(local.Tier, ts) : null;
        if (repush?.Tier is { } rt)
            tier = new TierPatch(rt.Value.Tier, rt.Value.Stamp);
        return new RecordPatch(content, tags, sticky, tier);
    }
}
