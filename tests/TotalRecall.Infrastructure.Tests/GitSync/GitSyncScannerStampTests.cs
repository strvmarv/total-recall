using System.Text;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecord = TotalRecall.Core.SyncRecord.SyncRecord;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncScannerStampTests
{
    private const string A = "3f9c2a7e";
    private const string B = "aaaa1111";
    private const string C = "bbbb2222";
    private const long T1 = 1_800_000_000_000L;
    private const long T2 = T1 + 60_000;
    private const long T3 = T2 + 60_000;

    private static readonly SyncStamp.MachineId Other = SyncStamp.MachineIdModule.tryCreate("0000000b").Value;

    private static ScanSummary Scan(ScanRig rig, long now)
    {
        var outcome = rig.Scanner(now).Scan();
        return Assert.IsType<ScanOutcome.Done>(outcome).Summary;
    }

    private static int Cmp(SyncStamp.Stamp? a, SyncStamp.Stamp? b)
    {
        Assert.NotNull(a);
        Assert.NotNull(b);
        return SyncStamp.StampModule.compare(a, b);
    }

    private static SyncStamp.Stamp Stamp(long timeMs, int counter = 0) => new(timeMs, counter, Other);

    /// <summary>The text of a valid record file, for a row's base.</summary>
    private static string BaseJson(string id, Tier tier, SyncStamp.Stamp content, SyncStamp.Stamp sticky, SyncStamp.Stamp tierStamp, bool isSticky = false)
    {
        var record = new SyncRecord(
            id, "base", EntryType.Decision, ListModule.Empty<string>(), null, 1_700_000_000_000L, null, null,
            new SyncRecordModule.Placement(tier, tierStamp, isSticky, sticky), content, null, null);
        return Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!);
    }

    private static GitSyncRow Row(string id, SyncMerge.RowState state = null!) =>
        new(id, state ?? SyncMerge.RowState.Active, null, null, null, null, null, null, null, null, null, null, null, null, null);

    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Scan_NewMemoriesInThreeTiers_GetAnActiveRowWithThreeStamps_AndNoMemoryRowChanges()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "hot one", tags: ["x"]);
        rig.Pin(A);
        rig.AddMemory(Tier.Warm, B, "warm one");
        rig.AddMemory(Tier.Cold, C, "cold one", tags: ["y", "x"]);
        var before = rig.Dump();

        var summary = Scan(rig, T1);

        var rows = rig.Rows();
        Assert.Equal(3, rows.Count);
        var stamps = new List<SyncStamp.Stamp>();
        foreach (var (id, hot, tier) in new[] { (A, true, Tier.Hot), (B, false, Tier.Warm), (C, false, Tier.Cold) })
        {
            var row = rows[id];
            Assert.True(row.State.IsActive);
            Assert.Null(row.BaseJson);
            Assert.Equal(hot, row.LocalSticky);
            Assert.Equal(tier, row.LocalTier);
            Assert.All(new[] { row.RepushGroups, row.PushingStamps, row.BlockedHash, row.BlockedReason, row.AllowedHash }, Assert.Null);
            stamps.AddRange([row.ContentStamp!, row.StickyStamp!, row.TierStamp!]);
        }
        var snap = rig.Scanner(T1).ReadSnapshot();
        foreach (var id in new[] { A, B, C })
        {
            Assert.Equal(SyncScan.contentHash(snap.Entries[id].View.Content), rows[id].ScannedContentHash);
            Assert.Equal(SyncScan.tagsHash(snap.Entries[id].View.Tags), rows[id].ScannedTagsHash);
        }
        for (var i = 1; i < stamps.Count; i++)
            Assert.True(Cmp(stamps[i], stamps[i - 1]) > 0, $"stamp {i} is above stamp {i - 1}");
        Assert.All(stamps, s => Assert.Equal(T1, s.TimeMs));
        Assert.Equal((3, 0, 0, 0, 0, 0, 0, 0), (summary.New, summary.Content, summary.Tags, summary.Sticky, summary.Tier, summary.Left, summary.Reactivated, summary.Ineligible));
        Assert.Empty(summary.LocalDeletions);
        Assert.Empty(summary.Skipped);
        Assert.Equal(before, rig.Dump());
    }

    [Theory]
    [InlineData("text")]
    [InlineData("project")]
    [InlineData("scope")]
    [InlineData("entry_type")]
    [InlineData("source_tool")]
    public void Scan_TextProjectScopeEntryTypeOrSourceToolEdit_StampsOnlyContent(string field)
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one", tags: ["x"]);
        Scan(rig, T1);
        var first = rig.Rows()[A];

        switch (field)
        {
            case "text": rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" }); break;
            case "project": rig.Exec($"UPDATE warm_memories SET project = 'owner/repo' WHERE id = '{A}'"); break;
            case "scope": rig.Exec($"UPDATE warm_memories SET scope = 'owner/repo' WHERE id = '{A}'"); break;
            case "entry_type": rig.Exec($"UPDATE warm_memories SET entry_type = 'Decision' WHERE id = '{A}'"); break;
            case "source_tool": rig.Exec($"UPDATE warm_memories SET source_tool = 'cursor' WHERE id = '{A}'"); break;
        }
        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.True(Cmp(row.ContentStamp, first.ContentStamp) > 0);
        Assert.Equal(T2, row.ContentStamp!.TimeMs);
        Assert.Equal(first.StickyStamp, row.StickyStamp);
        Assert.Equal(first.TierStamp, row.TierStamp);
        Assert.Equal(first.ScannedTagsHash, row.ScannedTagsHash);
        Assert.NotEqual(first.ScannedContentHash, row.ScannedContentHash);
        Assert.Equal((0, 1, 0, 0, 0), (summary.New, summary.Content, summary.Tags, summary.Sticky, summary.Tier));
    }

    [Fact]
    public void StampsFollowIdOrder_WhateverTheOrderOfTheRows()
    {
        using var rig = new ScanRig();
        // rows are created in the opposite order of the ids (decision 3: stamp counters do not depend on table order)
        foreach (var id in new[] { C, B, A })
        {
            rig.AddMemory(Tier.Warm, id, "one " + id);
            Scan(rig, T1);
        }
        Assert.Equal([C, B, A], rig.Rows().Keys);                                // the rows come back in rowid order
        foreach (var id in new[] { C, B, A })
            rig.Store.Update(Tier.Warm, ContentType.Memory, id, new UpdateEntryOpts { Content = "two " + id });

        Scan(rig, T2);

        var rows = rig.Rows();
        Assert.True(Cmp(rows[A].ContentStamp, rows[B].ContentStamp) < 0);
        Assert.True(Cmp(rows[B].ContentStamp, rows[C].ContentStamp) < 0);
    }

    [Fact]
    public void SecondEditBeforeAnyPush_IsStampedAboveTheFirst()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });
        Scan(rig, T2);
        var afterFirst = rig.Rows()[A];
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "three" });

        Scan(rig, T3);

        var row = rig.Rows()[A];
        Assert.True(Cmp(row.ContentStamp, afterFirst.ContentStamp) > 0);
        Assert.NotEqual(afterFirst.ScannedContentHash, row.ScannedContentHash);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("pin")]
    public void AnUpdate_KeepsTheBaseAndEveryColumnItDoesNotOwn(string change)
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "one");
        Scan(rig, T1);
        var seeded = rig.Rows()[A] with
        {
            BaseJson = BaseJson(A, Tier.Hot, Stamp(1000), Stamp(2000), Stamp(3000)),
            RepushGroups = "tier",
            PushingStamps = "{\"content\":\"x\"}",
            BlockedHash = "bh",
            BlockedReason = "secret",
            AllowedHash = "ah",
        };
        GitSyncRows.Write(rig.Connection, seeded);
        if (change == "content")
            rig.Store.Update(Tier.Hot, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });
        else
            rig.Pin(A);

        Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.Equal(seeded.BaseJson, row.BaseJson);
        Assert.Equal((seeded.RepushGroups, seeded.PushingStamps, seeded.BlockedHash, seeded.BlockedReason, seeded.AllowedHash),
            (row.RepushGroups, row.PushingStamps, row.BlockedHash, row.BlockedReason, row.AllowedHash));
        Assert.NotEqual(seeded, row);
    }

    [Fact]
    public void Scan_TagsOnlyChange_ChangesTheTagsHash_AndIssuesNoStamp()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one", tags: ["x"]);
        Scan(rig, T1);
        var first = rig.Rows()[A];
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Tags = ["x", "y"] });

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.NotEqual(first.ScannedTagsHash, row.ScannedTagsHash);
        Assert.Equal(first with { ScannedTagsHash = row.ScannedTagsHash }, row);
        Assert.Equal((0, 0, 1, 0, 0), (summary.New, summary.Content, summary.Tags, summary.Sticky, summary.Tier));
    }

    [Fact]
    public void Scan_ChangesToSummaryMetadataUsageAndDecay_ChangeNothing()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts
            { Summary = "a summary", MetadataJson = "{\"k\":1}", DecayScore = 0.25, Touch = true });
        rig.Exec($"UPDATE warm_memories SET updated_at = updated_at + 5, times_injected = 9, access_count = 7, last_accessed_at = 1 WHERE id = '{A}'");
        var hlc = rig.Meta("git_sync_hlc");

        var summary = Scan(rig, T2);

        Assert.Equal(first, rig.Rows()[A]);
        Assert.Equal(hlc, rig.Meta("git_sync_hlc"));
        Assert.Equal((0, 0, 0, 0, 0), (summary.New, summary.Content, summary.Tags, summary.Sticky, summary.Tier));
    }

    [Fact]
    public void Pin_OnAWarmEntry_StampsStickyAndTier()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        // pinning a warm entry means: promote it to hot and set the flag
        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, A);
        rig.Pin(A);

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.True(row.LocalSticky);
        Assert.True(row.LocalTier!.IsHot);
        Assert.True(Cmp(row.StickyStamp, first.StickyStamp) > 0);
        Assert.True(Cmp(row.TierStamp, first.TierStamp) > 0);
        Assert.Equal(first.ContentStamp, row.ContentStamp);
        Assert.Equal((0, 0, 1, 1), (summary.Content, summary.Tags, summary.Sticky, summary.Tier));
    }

    [Fact]
    public void Unpin_StampsSticky()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "one");
        rig.Pin(A);
        Scan(rig, T1);
        var first = rig.Rows()[A];
        rig.Store.SetSticky(ContentType.Memory, A, false);

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.False(row.LocalSticky);
        Assert.True(Cmp(row.StickyStamp, first.StickyStamp) > 0);
        Assert.Equal(first.TierStamp, row.TierStamp);
        Assert.Equal(first.ContentStamp, row.ContentStamp);
        Assert.Equal((0, 1, 0), (summary.Content, summary.Sticky, summary.Tier));
    }

    [Fact]
    public void Promotion_ByTheMachine_StampsTier()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Cold, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        rig.Store.Move(Tier.Cold, ContentType.Memory, Tier.Warm, ContentType.Memory, A);

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.True(row.LocalTier!.IsWarm);
        Assert.True(Cmp(row.TierStamp, first.TierStamp) > 0);
        Assert.Equal(first.StickyStamp, row.StickyStamp);
        Assert.Equal(1, summary.Tier);
    }

    [Fact]
    public void Demotion_FollowsTheTierAndIssuesNothing()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var first = rig.Rows()[A];
        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Cold, ContentType.Memory, A);

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.True(row.LocalTier!.IsCold);
        Assert.Equal(first with { LocalTier = Tier.Cold }, row);
        Assert.Equal(0, summary.Tier);
    }

    [Fact]
    public void UndonePromotion_DropsThePendingTierStamp()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Cold, A, "one");
        // a row with a base whose tier is cold and nothing pending for the tier
        GitSyncRows.Write(rig.Connection, Row(A) with
        {
            BaseJson = BaseJson(A, Tier.Cold, Stamp(T1 - 3000), Stamp(T1 - 2000), Stamp(T1 - 1000)),
            LocalSticky = false,
            LocalTier = Tier.Cold,
            ScannedContentHash = SyncScan.contentHash(rig.Scanner(T1).ReadSnapshot().Entries[A].View.Content),
            ScannedTagsHash = SyncScan.tagsHash(ListModule.Empty<string>()),
        });
        rig.Store.Move(Tier.Cold, ContentType.Memory, Tier.Warm, ContentType.Memory, A);
        Scan(rig, T2);
        Assert.NotNull(rig.Rows()[A].TierStamp);

        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Cold, ContentType.Memory, A);
        var summary = Scan(rig, T3);

        var row = rig.Rows()[A];
        Assert.Null(row.TierStamp);
        Assert.True(row.LocalTier!.IsCold);
        Assert.Equal(0, summary.Tier);
        var scanner = rig.Scanner(T3 + 1);
        Assert.IsType<ScanOutcome.Done>(scanner.Scan());
        Assert.Equal(0, scanner.WriteTransactionCount);
    }

    [Fact]
    public void OfflineEdit_IsStampedAtScanTime()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "two" });

        Scan(rig, T2);

        var stamp = rig.Rows()[A].ContentStamp!;
        Assert.Equal(T2, stamp.TimeMs);
        Assert.False(Directory.Exists(Path.Combine(rig.Dir, "sync")));
        rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Content = "three" });
        Scan(rig, T3);
        Assert.True(Cmp(rig.Rows()[A].ContentStamp, stamp) > 0);
        Assert.Equal(T3, rig.Rows()[A].ContentStamp!.TimeMs);
    }

    [Fact]
    public void RetypeToKnowledge_MovesTheRowToLeft_ClearsStamps_KeepsTheBase_DeletesNothing()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        Scan(rig, T1);
        var seeded = rig.Rows()[A] with
        {
            BaseJson = BaseJson(A, Tier.Warm, Stamp(1000), Stamp(2000), Stamp(3000)),
            RepushGroups = "content",
            PushingStamps = "{\"content\":\"x\"}",
            BlockedHash = "bh",
            BlockedReason = "secret",
            AllowedHash = "ah",
        };
        GitSyncRows.Write(rig.Connection, seeded);
        rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Warm, ContentType.Knowledge, A);

        var summary = Scan(rig, T2);

        var row = rig.Rows()[A];
        Assert.Equal(seeded with { State = SyncMerge.RowState.Left, ContentStamp = null, StickyStamp = null, TierStamp = null, RepushGroups = null }, row);
        Assert.Equal(1, summary.Left);
        Assert.Empty(summary.LocalDeletions);
        Assert.Single(rig.Rows());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowThatCameBack_ReactivatesWithTheBaseCleared_AndEverythingStampedAsNew(bool forgotten)
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one", tags: ["x"]);
        var old = Row(A, forgotten ? SyncMerge.RowState.Forgotten : SyncMerge.RowState.Left) with
        {
            BaseJson = BaseJson(A, Tier.Warm, Stamp(1000), Stamp(2000), Stamp(3000)),
            ScannedContentHash = "old",
            ScannedTagsHash = "old",
            LocalSticky = true,
            LocalTier = Tier.Cold,
            ContentStamp = Stamp(10),
            StickyStamp = Stamp(11),
            TierStamp = Stamp(12),
            RepushGroups = "tier",
            PushingStamps = "[1]",
            BlockedHash = "bh",
            BlockedReason = "secret",
            AllowedHash = "ah",
        };
        GitSyncRows.Write(rig.Connection, old);

        var summary = Scan(rig, T1);

        var row = rig.Rows()[A];
        var view = rig.Scanner(T1).ReadSnapshot().Entries[A].View;
        Assert.True(row.State.IsActive);
        Assert.Null(row.BaseJson);
        Assert.All(new[] { row.RepushGroups, row.PushingStamps, row.BlockedHash, row.BlockedReason, row.AllowedHash }, Assert.Null);
        Assert.Equal(SyncScan.contentHash(view.Content), row.ScannedContentHash);
        Assert.Equal(SyncScan.tagsHash(view.Tags), row.ScannedTagsHash);
        Assert.False(row.LocalSticky);
        Assert.True(row.LocalTier!.IsWarm);
        Assert.All(new[] { row.ContentStamp, row.StickyStamp, row.TierStamp }, s => Assert.Equal(T1, s!.TimeMs));
        Assert.Equal(1, summary.Reactivated);
        Assert.Equal(0, summary.New);
    }

    [Fact]
    public void AnUnreadableOrDuplicatedEntryWithAnActiveRow_IsNotADeletion_RowUntouched_OthersStamped()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");
        rig.AddMemory(Tier.Warm, B, "two");
        rig.AddMemory(Tier.Warm, C, "three");
        Scan(rig, T1);
        // every row gets a base, so a wrong deletion/left decision would show
        foreach (var id in new[] { A, B })
            GitSyncRows.Write(rig.Connection, rig.Rows()[id] with { BaseJson = BaseJson(id, Tier.Warm, Stamp(1), Stamp(2), Stamp(3)) });
        var before = rig.Rows();
        rig.Exec($"UPDATE warm_memories SET source_tool = 'bogus' WHERE id = '{A}'");            // unreadable
        rig.Exec($"INSERT INTO cold_memories SELECT * FROM warm_memories WHERE id = '{B}'");     // duplicate id
        rig.AddKnowledge(Tier.Warm, B);                                                          // and a knowledge entry with the same id
        rig.Store.Update(Tier.Warm, ContentType.Memory, C, new UpdateEntryOpts { Content = "three, edited" });

        var summary = Scan(rig, T2);

        var after = rig.Rows();
        Assert.Equal(before[A], after[A]);
        Assert.Equal(before[B], after[B]);
        Assert.Empty(summary.LocalDeletions);
        Assert.Equal([A, B], summary.Skipped.Select(s => s.Id));
        Assert.Equal(0, summary.Left);
        Assert.True(Cmp(after[C].ContentStamp, before[C].ContentStamp) > 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnActiveRowWithNoMemory_IsReportedAsALocalDeletion_AndLeftUnchanged(bool hasBase, bool pushing)
    {
        using var rig = new ScanRig();
        var row = Row(A) with
        {
            BaseJson = hasBase ? BaseJson(A, Tier.Warm, Stamp(1), Stamp(2), Stamp(3)) : null,
            PushingStamps = pushing ? "{\"content\":\"x\"}" : null,
            ScannedContentHash = "h",
            LocalTier = Tier.Warm,
        };
        GitSyncRows.Write(rig.Connection, row);

        var summary = Scan(rig, T1);

        Assert.Equal(new LocalDeletion(A, hasBase, pushing), Assert.Single(summary.LocalDeletions));
        Assert.Equal(row, rig.Rows()[A]);
        Assert.Equal(0, summary.Left);
    }

    [Fact]
    public void AnActiveRowWithNoMemoryAndACorruptBase_IsReportedWithAHasBase()
    {
        using var rig = new ScanRig();
        GitSyncRows.Write(rig.Connection, Row(A) with { BaseJson = "this is not json" });

        var summary = Scan(rig, T1);

        Assert.Equal(new LocalDeletion(A, HasBase: true, HasPushingStamps: false), Assert.Single(summary.LocalDeletions));
    }

    [Fact]
    public void AnUnreadableBase_MeansHasABase_NoFloor_NoClearing()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Cold, A, "one");
        var view = rig.Scanner(T1).ReadSnapshot().Entries[A].View;
        // a corrupt base, a pending tier stamp, a tier that rose: with an unreadable base nothing is cleared and the stamp is issued with no base floor
        GitSyncRows.Write(rig.Connection, Row(A) with
        {
            BaseJson = "not json",
            ScannedContentHash = SyncScan.contentHash(view.Content),
            ScannedTagsHash = SyncScan.tagsHash(view.Tags),
            LocalSticky = false,
            LocalTier = Tier.Cold,
            TierStamp = Stamp(T1 - 5000),
        });
        rig.Store.Move(Tier.Cold, ContentType.Memory, Tier.Warm, ContentType.Memory, A);

        Scan(rig, T1);

        var row = rig.Rows()[A];
        Assert.True(row.LocalTier!.IsWarm);
        Assert.True(Cmp(row.TierStamp, Stamp(T1 - 5000)) > 0);
        Assert.Equal(T1, row.TierStamp!.TimeMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForgottenOrLeftRows_WithNoMemory_DoNothing(bool forgotten)
    {
        using var rig = new ScanRig();
        var row = Row(A, forgotten ? SyncMerge.RowState.Forgotten : SyncMerge.RowState.Left) with { BaseJson = "x" };
        GitSyncRows.Write(rig.Connection, row);
        var scanner = rig.Scanner(T1);

        var summary = Assert.IsType<ScanOutcome.Done>(scanner.Scan()).Summary;

        Assert.Equal(row, rig.Rows()[A]);
        Assert.Empty(summary.LocalDeletions);
        Assert.Equal(0, scanner.WriteTransactionCount);
    }

    [Fact]
    public void Scan_PersistsTheClockSoTheNextStampIsAboveIt()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A, "one");

        Scan(rig, T1);

        var clock = SyncStamp.ClockModule.tryDeserialize(rig.Meta("git_sync_hlc")!);
        Assert.NotNull(clock);
        var last = rig.Rows()[A].TierStamp!;
        Assert.True(clock.Value.ClockMs >= last.TimeMs);
        Assert.True(clock.Value.ClockMs > last.TimeMs || clock.Value.ClockCounter >= last.Counter);
    }
}
