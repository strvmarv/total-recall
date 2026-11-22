using System.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using Kind = TotalRecall.Core.SyncScan.ClockWarning.Kind;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncScanner
{
    private sealed class ScanPlan
    {
        public List<GitSyncRow> Rows { get; } = [];
        public List<(string Id, string Detail)> IssueUpserts { get; } = [];
        public List<string> IssueDeletes { get; } = [];

        /// <summary>
        /// The entries whose <c>held-delete</c> item goes with their base: a row moved to <c>left</c> or reactivated
        /// (milestone 1b-ii), so an item never outlives the base it was opened for.
        /// </summary>
        public List<string> HeldDeletes { get; } = [];
        public string? ClockText { get; set; }
        public string? WarningText { get; set; }
        public ScanSummary Summary { get; set; } = ScanSummary.Empty;

        public bool HasWrites =>
            Rows.Count > 0 || IssueUpserts.Count > 0 || IssueDeletes.Count > 0 || HeldDeletes.Count > 0
            || ClockText is not null || WarningText is not null;
    }

    /// <summary>Issues stamps from the persisted clock, in order, and remembers what happened to the clock.</summary>
    private sealed class Issuer(SyncStamp.MachineId machine, long now, FSharpOption<SyncStamp.Clock>? clock)
    {
        public FSharpOption<SyncStamp.Clock>? Clock { get; private set; } = clock;
        public bool ClockChanged { get; private set; }
        public List<Kind> Kinds { get; } = [];

        public void ResetClock(FSharpOption<SyncStamp.Clock> clock)
        {
            Clock = clock;
            ClockChanged = true;
        }

        /// <summary>A stamp above the clock and the floor, or false when the clock range is exhausted (the group is then skipped).</summary>
        public bool TryIssue(FSharpOption<SyncStamp.Stamp>? floor, out SyncStamp.Stamp stamp)
        {
            var result = SyncStamp.ClockModule.issue(machine, now, Clock, floor);
            if (!result.IsOk)
            {
                Kinds.Add(Kind.Exhausted);
                stamp = null!;
                return false;
            }
            var issued = result.ResultValue;
            if (issued.FarFuture)
                Kinds.Add(Kind.FarFuture);
            if (issued.Clock is not null)
            {
                // Persisted only when it moved: a far-future stamp leaves the clock as it was.
                if (Clock is null || !issued.Clock.Value.Equals(Clock.Value))
                    ClockChanged = true;
                Clock = issued.Clock;
            }
            stamp = issued.Stamp;
            return true;
        }
    }

    private static FSharpOption<T>? Opt<T>(T? value) where T : class => value is null ? null : FSharpOption<T>.Some(value);

    private static FSharpOption<bool>? OptBool(bool? value) => value is null ? null : FSharpOption<bool>.Some(value.Value);

    private const string ExhaustedReason = "the clock cannot issue a stamp above this group's base stamp";

    private ScanPlan BuildPlan(Snapshot snap, SyncStamp.MachineId machine)
    {
        var plan = new ScanPlan();
        var now = _nowMs();

        // The clock: once per scan, before any stamp (decision 11).
        var stored = snap.ClockText is null ? null : SyncStamp.ClockModule.tryDeserialize(snap.ClockText);
        var guard = SyncStamp.ClockModule.guardStartup(now, stored);
        var issuer = new Issuer(machine, now, guard.Clock);
        var kinds = issuer.Kinds;
        if (guard.Reset)
        {
            kinds.Add(Kind.ClockReset);
            issuer.ResetClock(guard.Clock!);
        }
        if (snap.ClockText is not null && stored is null)
        {
            kinds.Add(Kind.ClockUnreadable);
            issuer.ResetClock(FSharpOption<SyncStamp.Clock>.Some(
                new SyncStamp.Clock(Math.Clamp(now, 0L, SyncStamp.maxTimeMs), 0)));
        }

        int created = 0, content = 0, tags = 0, sticky = 0, tier = 0, left = 0, reactivated = 0;
        var deletions = new List<LocalDeletion>();
        var skipped = new List<SkippedItem>(snap.Skipped);

        var ids = snap.Rows.Keys.Union(snap.Entries.Keys)
            .Where(id => !snap.SkippedIds.Contains(id))
            .Order(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            snap.Rows.TryGetValue(id, out var row);
            snap.Entries.TryGetValue(id, out var entry);
            var action = SyncScan.decide(
                row is null ? null : FSharpOption<SyncScan.RowView>.Some(ToRowView(row)),
                entry is null ? null : FSharpOption<SyncMerge.LocalEntry>.Some(entry.View),
                snap.KnowledgeIds.Contains(id));

            if (action.IsNoChange)
                continue;

            if (action.IsNewRow || action.IsReactivate)
            {
                var fresh = FreshRow(id, entry!, issuer, skipped);
                if (fresh is null)
                    continue;
                plan.Rows.Add(fresh);
                if (action.IsNewRow)
                {
                    created++;
                }
                else
                {
                    plan.HeldDeletes.Add(id); // the old row's base ends here
                    reactivated++;
                }
            }
            else if (action.IsMoveToLeft)
            {
                plan.Rows.Add(row! with { State = SyncMerge.RowState.Left, ContentStamp = null, StickyStamp = null, TierStamp = null, RepushGroups = null });
                plan.HeldDeletes.Add(id); // a left row is not an entry: nothing is left to hold
                left++;
            }
            else if (action is SyncScan.ScanAction.ReportDeletion report)
            {
                deletions.Add(new LocalDeletion(id, report.hasBase, report.hasPushingStamps));
            }
            else if (action is SyncScan.ScanAction.UpdateRow update)
            {
                var changes = update.Item;
                var next = row!;
                var view = entry!.View;

                if (changes.Content is SyncScan.GroupAction.IssueStamp ic)
                {
                    if (issuer.TryIssue(ic.floor, out var stamp))
                    {
                        next = next with { ScannedContentHash = SyncScan.contentHash(view.Content), ContentStamp = stamp };
                        content++;
                    }
                    else skipped.Add(new SkippedItem(id, "content", ExhaustedReason));
                }
                else if (changes.Content.IsStoreValue)
                    next = next with { ScannedContentHash = SyncScan.contentHash(view.Content) };

                if (changes.Tags.IsStoreValue)
                    next = next with { ScannedTagsHash = SyncScan.tagsHash(view.Tags) };

                if (changes.Sticky is SyncScan.GroupAction.IssueStamp isticky)
                {
                    if (issuer.TryIssue(isticky.floor, out var stamp))
                    {
                        next = next with { LocalSticky = view.Sticky, StickyStamp = stamp };
                        sticky++;
                    }
                    else skipped.Add(new SkippedItem(id, "sticky", ExhaustedReason));
                }
                else if (changes.Sticky.IsStoreValue)
                    next = next with { LocalSticky = view.Sticky };

                if (changes.Tier is SyncScan.GroupAction.IssueStamp itier)
                {
                    if (issuer.TryIssue(itier.floor, out var stamp))
                    {
                        next = next with { LocalTier = view.Tier, TierStamp = stamp };
                        tier++;
                    }
                    else skipped.Add(new SkippedItem(id, "tier", ExhaustedReason));
                }
                else if (changes.Tier.IsStoreValue)
                    next = next with { LocalTier = view.Tier };
                else if (changes.Tier.IsDropPendingStamp)
                    next = next with { LocalTier = view.Tier, TierStamp = null };

                if (changes.Tags.IsStoreValue) tags++;
                if (!next.Equals(row))
                    plan.Rows.Add(next);
            }
        }

        // The ineligible-id issue rows, by difference (decision 2).
        var ineligibleIds = snap.Ineligible.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in snap.Ineligible)
            if (!snap.Issues.TryGetValue(item.Id, out var detail) || detail != item.TierName)
                plan.IssueUpserts.Add((item.Id, item.TierName));
        foreach (var id in snap.Issues.Keys.Order(StringComparer.Ordinal))
            if (!ineligibleIds.Contains(id))
                plan.IssueDeletes.Add(id);

        if (issuer.ClockChanged)
            plan.ClockText = SyncStamp.ClockModule.serialize(issuer.Clock!.Value);
        plan.WarningText = SyncScan.ClockWarning.addKinds(Opt(snap.WarningText), ListModule.OfSeq(kinds))?.Value;

        // The secret check on the rows the plan now holds, before the summary (#161, decision D4).
        var blocked = new List<string>();
        var unblocked = new List<string>();
        ApplySecretPass(snap, plan, blocked, unblocked);

        skipped = skipped.OrderBy(s => s.Id, StringComparer.Ordinal).ThenBy(s => s.Group, StringComparer.Ordinal).ToList();
        plan.Summary = new ScanSummary(
            created, content, tags, sticky, tier, left, reactivated, snap.Ineligible.Count, deletions, skipped, blocked, unblocked);
        return plan;
    }

    /// <summary>A fresh active row for an entry (a new one, or one that came back): no base, every group stamped. Null when a group cannot be stamped.</summary>
    private static GitSyncRow? FreshRow(string id, SnapshotEntry entry, Issuer issuer, List<SkippedItem> skipped)
    {
        var view = entry.View;
        // A new row has no floor, and a clock at the end of the range was reset by the guard, so a failure here is
        // practically unreachable; if it happens the entry is left alone and the next scan tries again.
        if (!issuer.TryIssue(null, out var cs))
        {
            skipped.Add(new SkippedItem(id, "content", ExhaustedReason));
            return null;
        }
        if (!issuer.TryIssue(null, out var ss))
        {
            skipped.Add(new SkippedItem(id, "sticky", ExhaustedReason));
            return null;
        }
        if (!issuer.TryIssue(null, out var ts))
        {
            skipped.Add(new SkippedItem(id, "tier", ExhaustedReason));
            return null;
        }
        return new GitSyncRow(
            id, SyncMerge.RowState.Active, null,
            SyncScan.contentHash(view.Content), SyncScan.tagsHash(view.Tags), view.Sticky, view.Tier,
            cs, ss, ts, null, null, null, null, null);
    }

    private static SyncScan.RowView ToRowView(GitSyncRow row) =>
        new(
            row.State,
            BaseOf(row),
            HasPushingStamps(row.PushingStamps),
            Opt(row.ScannedContentHash),
            Opt(row.ScannedTagsHash),
            OptBool(row.LocalSticky),
            Opt(row.LocalTier),
            Opt(row.ContentStamp),
            Opt(row.StickyStamp),
            Opt(row.TierStamp));

    /// <summary>Whether <c>pushing_stamps</c> holds anything. The store-schema design leaves its encoding open, so NULL, blank, <c>{}</c> and <c>[]</c> count as empty.</summary>
    private static bool HasPushingStamps(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        return t != "{}" && t != "[]";
    }

    /// <summary>What the row's base record says: none when there is no base; "a base, tier unknown, no floor" when it cannot be read.</summary>
    private static FSharpOption<SyncScan.BaseFacts>? BaseOf(GitSyncRow row)
    {
        if (row.BaseJson is null)
            return null;
        var unknown = FSharpOption<SyncScan.BaseFacts>.Some(new SyncScan.BaseFacts(null, null, null, null));
        try
        {
            if (!SyncValidation.isEligibleId(row.EntryId))
                return unknown;
            var result = RecordReader.Parse(Encoding.UTF8.GetBytes(row.BaseJson), SyncValidation.recordPath(row.EntryId));
            if (result.Parsed is null)
                return unknown;
            var record = result.Parsed.Record;
            return FSharpOption<SyncScan.BaseFacts>.Some(new SyncScan.BaseFacts(
                FSharpOption<Tier>.Some(record.Placement.Tier),
                FSharpOption<SyncStamp.Stamp>.Some(record.ContentStamp),
                FSharpOption<SyncStamp.Stamp>.Some(record.Placement.StickyStamp),
                FSharpOption<SyncStamp.Stamp>.Some(record.Placement.TierStamp)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return unknown;
        }
    }
}
