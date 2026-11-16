using System.Globalization;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

public sealed class GitSyncScannerReadTests
{
    private const string A = "3f9c2a7e";
    private const string B = "aaaa1111";
    private const string C = "bbbb2222";

    [Fact]
    public void Eligible_MemoriesInHotWarmAndCold_AreRead_AndTheirViewMapsEmptyProjectAndScopeToNone()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A, "hot one", tags: ["b", "a", "a"], project: "owner/repo", scope: "owner/repo",
            entryType: EntryType.Preference, sourceTool: SourceTool.ClaudeCode, createdAt: 42);
        rig.AddMemory(Tier.Warm, B, "warm one", project: "", scope: "");
        rig.AddMemory(Tier.Cold, C, "cold one");
        rig.Pin(A);

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Equal([A, B, C], snap.Entries.Keys);
        var hot = snap.Entries[A];
        Assert.True(hot.Tier.IsHot);
        Assert.True(hot.View.Sticky);
        Assert.Equal("hot one", hot.View.Content.Text);
        Assert.Equal("owner/repo", hot.View.Content.Project.Value);
        Assert.Equal("owner/repo", hot.View.Content.Scope.Value);
        Assert.True(hot.View.Content.EntryType.IsPreference);
        Assert.True(hot.View.Content.Source.Value.IsClaudeCode);
        Assert.Equal(42L, hot.View.Content.CreatedAtMs);
        Assert.Equal(["a", "b"], hot.View.Tags);
        var warm = snap.Entries[B];
        Assert.True(warm.Tier.IsWarm);
        Assert.False(warm.View.Sticky);
        Assert.Null(warm.View.Content.Project);
        Assert.Null(warm.View.Content.Scope);
        Assert.True(snap.Entries[C].Tier.IsCold);
        Assert.Empty(snap.Skipped);
        Assert.Empty(snap.Ineligible);
    }

    [Fact]
    public void KnowledgeEntries_AreNeverRead_ExceptTheirIdsForRetypes()
    {
        using var rig = new ScanRig();
        rig.AddKnowledge(Tier.Warm, A);
        rig.AddKnowledge(Tier.Cold, B);

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Empty(snap.Entries);
        Assert.Equal([A, B], snap.KnowledgeIds.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnIneligibleId_GetsAnIneligibleIdIssueCandidate_AndNoEntry()
    {
        using var rig = new ScanRig();
        foreach (var id in new[] { "not-an-id", "../x", "abcdef0", "ABCDEF01" })
            rig.AddMemory(Tier.Warm, id);
        rig.AddMemory(Tier.Warm, A);

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Equal([A], snap.Entries.Keys);
        Assert.Equal(["../x", "ABCDEF01", "abcdef0", "not-an-id"], snap.Ineligible.Select(i => i.Id));
        Assert.All(snap.Ineligible, i => Assert.Equal("warm", i.TierName));
        Assert.Equal(4, snap.SkippedIds.Count);
    }

    [Fact]
    public void AnIneligibleIdInTwoTiers_YieldsOneCandidate()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, "not-an-id");
        rig.AddMemory(Tier.Cold, "not-an-id");

        var snap = rig.Scanner(1).ReadSnapshot();

        var one = Assert.Single(snap.Ineligible);
        Assert.Equal("warm", one.TierName);
    }

    [Fact]
    public void UnreadableEntries_AreSkippedAndListed()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A);
        rig.AddMemory(Tier.Warm, B);
        rig.AddMemory(Tier.Warm, C);
        rig.Exec($"UPDATE warm_memories SET source_tool = 'bogus' WHERE id = '{B}'");
        rig.Exec($"UPDATE warm_memories SET tags = 'not json' WHERE id = '{C}'");

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Equal([A], snap.Entries.Keys);
        Assert.Equal([B, C], snap.Skipped.Select(s => s.Id));
        Assert.All(snap.Skipped, s => { Assert.Null(s.Group); Assert.False(string.IsNullOrEmpty(s.Reason)); });
        Assert.Contains(B, snap.SkippedIds);
        Assert.Contains(C, snap.SkippedIds);
    }

    [Fact]
    public void AnEntryWithNullAccessCount_IsSkippedAndListed()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A);
        rig.AddMemory(Tier.Warm, B);
        rig.Exec($"UPDATE warm_memories SET access_count = NULL WHERE id = '{B}'");

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Equal([A], snap.Entries.Keys);
        Assert.Equal(B, Assert.Single(snap.Skipped).Id);
    }

    [Fact]
    public void AnIdInTwoTiers_IsSkipped()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A);
        rig.AddMemory(Tier.Warm, B);
        // hot_memories has the extra sticky column, so a raw copy goes warm -> cold.
        rig.Exec($"INSERT INTO cold_memories SELECT * FROM warm_memories WHERE id = '{A}'");

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Equal([B], snap.Entries.Keys);
        var skipped = Assert.Single(snap.Skipped);
        Assert.Equal(A, skipped.Id);
        Assert.Contains(A, snap.SkippedIds);
    }

    [Fact]
    public void AnUnreadableEntryWithAnIneligibleId_StillGetsItsIssue_AndCountsAsExisting()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, "not-an-id");
        rig.Exec("UPDATE warm_memories SET source_tool = 'bogus' WHERE id = 'not-an-id'");

        var snap = rig.Scanner(1).ReadSnapshot();

        Assert.Equal("not-an-id", Assert.Single(snap.Ineligible).Id);
        Assert.Contains("not-an-id", snap.SkippedIds);
        Assert.Empty(snap.Entries);
    }

    [Fact]
    public void EntriesAreProcessedInIdOrder()
    {
        using var rig = new ScanRig();
        // tier order (hot, warm, cold) and id order differ; the ids mix digits, letters and '-'
        rig.AddMemory(Tier.Hot, "ffff0001");
        rig.AddMemory(Tier.Warm, "0abc-def0");
        rig.AddMemory(Tier.Cold, "00000000");
        rig.AddMemory(Tier.Warm, "0abc0def");
        var expected = new[] { "00000000", "0abc-def0", "0abc0def", "ffff0001" };

        Assert.Equal(expected, rig.Scanner(1).ReadSnapshot().Entries.Keys);

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal(expected, rig.Scanner(1).ReadSnapshot().Entries.Keys);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void ACorruptRowStateFailsTheScanButLeavesTheConnectionUsable()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Warm, A);
        GitSyncRows.Write(rig.Connection, new GitSyncRow(A, SyncMerge.RowState.Active, null, null, null, null, null, null, null, null, null, null, null, null, null));
        rig.Exec("PRAGMA ignore_check_constraints = 1");
        rig.Exec("UPDATE git_sync_entry SET state = 'bogus'");
        rig.Exec("PRAGMA ignore_check_constraints = 0");

        var failed = Assert.IsType<ScanOutcome.Failed>(rig.Scanner(1).Scan());
        Assert.False(string.IsNullOrEmpty(failed.Reason));

        rig.Exec("UPDATE git_sync_entry SET state = 'active'");
        Assert.IsType<ScanOutcome.Done>(rig.Scanner(1).Scan());
    }

    [Fact]
    public void ACorruptLocalTierAlsoFailsTheScanButLeavesTheConnectionUsable()
    {
        using var rig = new ScanRig();
        GitSyncRows.Write(rig.Connection, new GitSyncRow(A, SyncMerge.RowState.Active, null, null, null, null, null, null, null, null, null, null, null, null, null));
        rig.Exec("UPDATE git_sync_entry SET local_tier = 'lukewarm'");

        Assert.IsType<ScanOutcome.Failed>(rig.Scanner(1).Scan());

        rig.Exec("UPDATE git_sync_entry SET local_tier = NULL");
        Assert.IsType<ScanOutcome.Done>(rig.Scanner(1).Scan());
    }

    [Fact]
    public void TheSnapshotIsOneReadTransaction()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A);
        rig.AddMemory(Tier.Cold, B);
        var (otherConn, otherStore) = rig.Other();
        using (otherConn)
        {
            var scanner = rig.Scanner(1);
            scanner.AfterFirstTableRead = () =>
            {
                otherStore.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "new", Id: C));
                otherStore.Move(Tier.Cold, ContentType.Memory, Tier.Warm, ContentType.Memory, B);
            };

            var snap = scanner.ReadSnapshot();

            Assert.Equal([A, B], snap.Entries.Keys);
            Assert.True(snap.Entries[B].Tier.IsCold);
            Assert.DoesNotContain(C, snap.Entries.Keys);
        }
        // and the next snapshot sees the new state
        var next = rig.Scanner(1).ReadSnapshot();
        Assert.Equal([A, B, C], next.Entries.Keys);
        Assert.True(next.Entries[B].Tier.IsWarm);
    }

    [Fact]
    public void TheScanNeverWritesAMemoryRow()
    {
        using var rig = new ScanRig();
        rig.AddMemory(Tier.Hot, A);
        rig.AddMemory(Tier.Warm, B);
        rig.AddKnowledge(Tier.Cold, C);
        var before = rig.Dump();

        rig.Scanner(1).ReadSnapshot();
        rig.Scanner(1).Scan();

        Assert.Equal(before, rig.Dump());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    public void NoValidMachineId_YieldsNotJoined(string? machine)
    {
        using var rig = new ScanRig(joined: false);
        rig.AddMemory(Tier.Warm, A);
        if (machine is not null)
            rig.SetMeta("git_sync_machine", machine);

        Assert.IsType<ScanOutcome.NotJoined>(rig.Scanner(1).Scan());
        Assert.Empty(rig.Rows());
        Assert.Empty(rig.Issues());
        Assert.Null(rig.Meta("git_sync_hlc"));
    }
}
