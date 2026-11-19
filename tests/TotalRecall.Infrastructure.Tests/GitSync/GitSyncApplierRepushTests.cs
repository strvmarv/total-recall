using System.Text;
using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// The repush marker of the pull (milestone 1b-ii, D16 and D19): a record whose stamp for a group is older than the base's
/// (a reset reverted it) is carried out, not postponed; the groups with no pending local change are written to the row's
/// <c>repush_groups</c>, the entry and its local-only state are left alone, and a marked row is decided on every pull.
/// </summary>
public sealed class GitSyncApplierRepushTests : IDisposable
{
    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string C = "3f9c2a7e-0000-4000-8000-0000000000c3";
    private const string Id = "3f9c2a7e-0000-4000-8000-000000000001";
    private const string All = "content,sticky,tier";

    private readonly PullRig _rig = new();
    private readonly CountingEmbedder _embedder = new();
    private readonly GitSyncApplier _applier;
    private long _now = 1_780_000_000_000L;

    public GitSyncApplierRepushTests()
    {
        _applier = new GitSyncApplier(_rig.Client, _rig.Store, _embedder, new ApplyOptions(0), () => _now);
        _rig.Join();
        _rig.PutManifest();
        _rig.Commit("manifest");
        _rig.Clone();
    }

    public void Dispose() => _rig.Dispose();

    private ScanSummary Scan() => Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(_rig.Connection, () => _now).Scan()).Summary;

    private async Task<(FetchResult Fetch, ApplySummary Summary)> PullWith()
    {
        var fetch = await _rig.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        return (result, Assert.IsType<ApplyOutcome.Done>(await _applier.ApplyAsync(result, Scan())).Summary);
    }

    private async Task<ApplySummary> Pull() => (await PullWith()).Summary;

    private static SyncRecordModule.SyncRecord Rec(
        string id, string content, int minute, Tier? tier = null, bool sticky = false, int? tierMinute = null, int? stickyMinute = null) =>
        ApplyRig.Rec(id, content, tier, sticky, minute: minute, tierMinute: tierMinute, stickyMinute: stickyMinute);

    private static string Older(string id) => "Text of " + id[^2..];

    private static string Newer(string id) => "Newer text of " + id[^2..];

    /// <summary>
    /// The ids synced twice: their records at minute 10, then at minute 20 (the rows' bases). Returns the commit of minute 10,
    /// the state a hand reset of the repository goes back to.
    /// </summary>
    private async Task<string> SyncedTwice(params string[] ids)
    {
        foreach (var id in ids)
            _rig.PutRecord(Rec(id, Older(id), 10));
        var old = _rig.Commit("minute 10");
        Assert.Equal(ids.Length, (await Pull()).Inserted);
        foreach (var id in ids)
            _rig.PutRecord(Rec(id, Newer(id), 20));
        _rig.Commit("minute 20");
        var second = await Pull();
        Assert.Equal(ids.Length, second.Updated);
        Assert.All(ids, id => Assert.Null(Row(id).RepushGroups));
        return old;
    }

    /// <summary>A hand reset of the repository's branch to an earlier commit: <c>L</c> is no longer an ancestor, so the next pull is unproven.</summary>
    private void ResetTo(string commit)
    {
        _rig.Source.Git("update-ref", "refs/heads/main", commit);
        _rig.Source.Git("read-tree", commit);
    }

    /// <summary>Synced twice, then the repository reset to minute 10 and pulled: every group of every id is marked.</summary>
    private async Task<ApplySummary> MarkedByAReset(params string[] ids)
    {
        ResetTo(await SyncedTwice(ids));
        var summary = await Pull();
        Assert.False(summary.Proven);
        Assert.All(ids, id => Assert.Equal(All, Row(id).RepushGroups));
        return summary;
    }

    private GitSyncRow Row(string id) => GitSyncRows.Read(_rig.Connection, id)!;

    private void SetBase(string id, SyncRecordModule.SyncRecord record)
    {
        using var cmd = _rig.Connection.CreateCommand();
        cmd.CommandText = "UPDATE git_sync_entry SET base_json = $b WHERE entry_id = $id";
        cmd.Parameters.AddWithValue("$b", Encoding.UTF8.GetString(RecordWriter.Write(record).Bytes!));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void ClearPending(string id)
    {
        using var cmd = _rig.Connection.CreateCommand();
        cmd.CommandText = "UPDATE git_sync_entry SET content_stamp = NULL, sticky_stamp = NULL, tier_stamp = NULL WHERE entry_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private (Tier Tier, Entry Entry, bool Sticky) Local(string id)
    {
        foreach (var tier in new[] { Tier.Hot, Tier.Warm, Tier.Cold })
            if (_rig.Store.Get(tier, ContentType.Memory, id) is { } e)
                return (tier, e, _rig.Store.IsSticky(ContentType.Memory, id));
        throw new Xunit.Sdk.XunitException("no such entry " + id);
    }

    /// <summary>What a test compares of an entry: its tier, text, tags and pin.</summary>
    private string Snapshot(string id)
    {
        var (tier, e, sticky) = Local(id);
        return $"{tier}|{e.Content}|{string.Join(",", e.Tags)}|{sticky}";
    }

    private void AssertScanQuiet()
    {
        var scan = Scan();
        Assert.Equal(0, scan.New + scan.Content + scan.Tags + scan.Sticky + scan.Tier + scan.Left + scan.Reactivated);
    }

    // ---- a reset repository ----

    [GitFact]
    public async Task ResetRepository_RecordGroupStampOlderThanBase_SetsTheMarker_AndChangesTheEntryNothing()
    {
        var old = await SyncedTwice(A, B, C);

        // A: a declined pin (the base pins, the entry stays unpinned; the state an answered new-pin review leaves).
        SetBase(A, Rec(A, Newer(A), 20, sticky: true));
        // B: a held pin-edit (the entry is pinned with its own text; the base holds the record's newer text; nothing pending).
        _rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, B);
        _rig.Store.Update(Tier.Hot, ContentType.Memory, B, new UpdateEntryOpts { Content = "My pinned text." });
        _rig.Store.SetSticky(ContentType.Memory, B, true);
        Scan();
        ClearPending(B);
        SetBase(B, Rec(B, Newer(B), 20, sticky: true));
        // C: a local demotion (recorded by the scan, never stamped: tiers only go up).
        _rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Cold, ContentType.Memory, C);
        Scan();
        Assert.Null(Row(C).TierStamp);

        var entries = new[] { A, B, C }.ToDictionary(id => id, Snapshot);
        var rows = new[] { A, B, C }.ToDictionary(id => id, Row);
        ResetTo(old);
        var calls = _embedder.Calls;

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.True(summary.Complete);
        Assert.Empty(summary.Postponed);
        Assert.Equal([new RepushEntry(A, All), new RepushEntry(B, All), new RepushEntry(C, All)], summary.RepushMarked);
        foreach (var id in new[] { A, B, C })
        {
            Assert.Equal(entries[id], Snapshot(id)); // the entry and its local-only state stay as they were
            Assert.Equal(rows[id] with { RepushGroups = All }, Row(id)); // the base is kept: a later repush reads it
        }
        Assert.Equal(calls, _embedder.Calls);
        Assert.Empty(GitSyncReviews.ReadOpenHeld(_rig.Connection));
        AssertScanQuiet();
    }

    [GitFact]
    public async Task GroupWithAPendingLocalChange_IsNotMarked()
    {
        var old = await SyncedTwice(A, B);
        _now += 60_000;
        _rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, A); // a pending promotion
        _rig.Store.Update(Tier.Warm, ContentType.Memory, B, new UpdateEntryOpts { Content = "A local edit." }); // a pending text edit
        Scan();
        var pendingA = Row(A).TierStamp;
        var pendingB = Row(B).ContentStamp;
        Assert.NotNull(pendingA);
        Assert.NotNull(pendingB);
        ResetTo(old);

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Equal([new RepushEntry(A, "content,sticky"), new RepushEntry(B, "sticky,tier")], summary.RepushMarked);
        Assert.Equal("content,sticky", Row(A).RepushGroups);
        Assert.Equal("sticky,tier", Row(B).RepushGroups);
        Assert.Equal(pendingA, Row(A).TierStamp); // the pending change takes the normal path
        Assert.Equal(pendingB, Row(B).ContentStamp);
        Assert.Equal(Tier.Hot, Local(A).Tier);
        Assert.Equal("A local edit.", Local(B).Entry.Content);
    }

    // ---- a marked row is decided on every pull ----

    [GitFact]
    public async Task MarkerRowWithNothingNew_IsStillVisitedAndStillDecided()
    {
        await MarkedByAReset(A, B);
        var visited = new List<string>();
        _applier.BeforeEntry = visited.Add;

        var (fetch, summary) = await PullWith();

        Assert.True(fetch.NothingNew);
        Assert.True(fetch.Proven);
        Assert.Equal(fetch.Last, fetch.Head);
        Assert.Equal([A, B], visited);
        Assert.True(summary.Complete);
        Assert.Equal([new RepushEntry(A, All), new RepushEntry(B, All)], summary.RepushMarked);
        Assert.Equal(All, Row(A).RepushGroups);
        Assert.Equal(All, Row(B).RepushGroups);

        // A local promotion of A: the next pull with nothing new decides A again and drops its tier from the marker.
        _now += 60_000;
        _rig.Store.Move(Tier.Warm, ContentType.Memory, Tier.Hot, ContentType.Memory, A);
        visited.Clear();

        (fetch, summary) = await PullWith();

        Assert.True(fetch.NothingNew);
        Assert.Equal([A, B], visited);
        Assert.Equal([new RepushEntry(A, "content,sticky"), new RepushEntry(B, All)], summary.RepushMarked);
        Assert.Equal("content,sticky", Row(A).RepushGroups);
        Assert.Equal(All, Row(B).RepushGroups);
    }

    [GitFact]
    public async Task MarkerSurvivesAFailedEntry_AndADeferral()
    {
        await MarkedByAReset(A);
        // a newer text on top of the reset: the content group is caught up, sticky and tier stay reverted
        _rig.PutRecord(Rec(A, "Newest text.", 30, tierMinute: 10, stickyMinute: 10));
        _rig.Commit("newest text");

        _applier.BeforeEntryStep = (_, step) => { if (step == "row") throw new InvalidOperationException("fault at row"); };
        var failed = await Pull();

        Assert.Equal(A, Assert.Single(failed.Failed).Id);
        Assert.Empty(failed.RepushMarked);
        Assert.Equal(All, Row(A).RepushGroups);
        Assert.Equal(Newer(A), Local(A).Entry.Content);

        _applier.BeforeEntryStep = null;
        _applier.BeforeTransaction = id =>
        {
            if (id == A)
                _rig.Store.Update(Tier.Warm, ContentType.Memory, A, new UpdateEntryOpts { Tags = ["local"] });
        };
        var deferred = await Pull();

        Assert.Equal(A, Assert.Single(deferred.Deferred).Id);
        Assert.False(deferred.Complete);
        Assert.Equal(All, Row(A).RepushGroups);
        Assert.Equal(Newer(A), Local(A).Entry.Content);

        _applier.BeforeTransaction = null;
        var carried = await Pull();

        Assert.True(carried.Complete);
        Assert.Equal([new RepushEntry(A, "sticky,tier")], carried.RepushMarked);
        Assert.Equal("sticky,tier", Row(A).RepushGroups);
        Assert.Equal("Newest text.", Local(A).Entry.Content);
        Assert.Equal(["local"], Local(A).Entry.Tags.ToArray());
    }

    [GitFact]
    public async Task CaughtUpRecord_ClearsTheMarker()
    {
        await MarkedByAReset(A, B);
        _rig.PutRecord(Rec(A, Newer(A), 20)); // every stamp equals the base's again
        _rig.PutRecord(Rec(B, Older(B), 10, tierMinute: 20)); // only the tier is caught up
        _rig.Commit("caught up");
        var entryA = Snapshot(A);
        var rowA = Row(A);

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.True(summary.Complete);
        Assert.Equal([new RepushEntry(B, "content,sticky")], summary.RepushMarked);
        Assert.Equal(rowA with { RepushGroups = null }, Row(A));
        Assert.Equal("content,sticky", Row(B).RepushGroups);
        Assert.Equal(entryA, Snapshot(A));
    }

    // ---- the pull's result ----

    [GitFact]
    public async Task PullWithOnlyMarkedGroups_IsComplete()
    {
        var old = await SyncedTwice(A, B);
        ResetTo(old);

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.True(summary.Complete);
        Assert.Empty(summary.Postponed);
        Assert.Empty(summary.Deferred);
        Assert.Empty(summary.Failed);
        Assert.Equal(2, summary.Updated);
        Assert.Equal([new RepushEntry(A, All), new RepushEntry(B, All)], summary.RepushMarked);
        Assert.Equal(old, _rig.Last);
        Assert.Equal(old, _rig.CloneRef("refs/total-recall/last-synced"));
    }

    [GitFact]
    public async Task ReviewStillPostpones()
    {
        await MarkedByAReset(A);
        // a newer pin on top of the reset, while content and tier stay reverted: a new-pin review
        _rig.PutRecord(Rec(A, Older(A), 10, sticky: true, stickyMinute: 30));
        var head = _rig.Commit("a pin");
        var entry = Snapshot(A);
        var row = Row(A);

        var summary = await Pull();

        Assert.Equal([new PostponedEntry(A, "review")], summary.Postponed);
        Assert.False(summary.Complete);
        Assert.NotEqual(head, _rig.Last);
        Assert.Empty(summary.RepushMarked);
        Assert.Equal(row, Row(A)); // the marker is kept with the rest
        Assert.Equal(entry, Snapshot(A));
    }

    // ---- what clears the marker, and a fault at every step ----

    private static EntryResult Run(ApplyRig rig, SyncRecordModule.SyncRecord? record, bool proven = true, GitSyncApplier.GuardVerdict guard = default) =>
        rig.Applier.ApplyOne(Id, record is null ? null : ApplyRig.Parsed(record), proven, rig.Held(Id), "abc", guard);

    /// <summary>A synced entry (base at minute 20) whose row carries the marker <c>tier</c>.</summary>
    private static ApplyRig MarkedRig()
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, minute: 20));
        rig.Scan.Exec($"UPDATE git_sync_entry SET repush_groups = 'tier' WHERE entry_id = '{Id}'");
        return rig;
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("remote-hold")]
    [InlineData("guard-hold")]
    [InlineData("local-hold")]
    [InlineData("forgotten")]
    public void ADeletion_AHoldOfEitherDirection_AndAForgottenRow_ClearTheMarker(string kind)
    {
        using var rig = MarkedRig();
        if (kind is "local-hold" or "forgotten")
        {
            rig.Scan.Store.Delete(Tier.Warm, ContentType.Memory, Id);
            rig.RunScan();
        }

        var result = kind switch
        {
            "delete" => Run(rig, null),
            "remote-hold" => Run(rig, null, proven: false),
            "guard-hold" => Run(rig, null, guard: new GitSyncApplier.GuardVerdict(RemoteTripped: true, LocalTripped: false)),
            "local-hold" => Run(rig, ApplyRig.Rec(Id, minute: 20), proven: false),
            _ => Run(rig, null), // W10: the record and the entry are gone, the row becomes forgotten
        };

        Assert.IsType<EntryResult.Carried>(result);
        var row = rig.Scan.Rows()[Id];
        Assert.Null(row.RepushGroups);
        Assert.Equal(kind is "delete" or "forgotten" ? SyncMerge.RowState.Forgotten : SyncMerge.RowState.Active, row.State);
    }

    [Fact]
    public void AnEditThatBeatsADeletion_KeepsTheMarker()
    {
        using var rig = MarkedRig();
        rig.Scan.Store.Update(Tier.Warm, ContentType.Memory, Id, new UpdateEntryOpts { Content = "A local edit." });
        rig.RunScan();

        var carried = Assert.IsType<EntryResult.Carried>(Run(rig, null)); // an edit beats the deletion: LeaveEntry with a fresh stamp

        Assert.Null(carried.Effects.Repush);
        Assert.Equal("tier", rig.Scan.Rows()[Id].RepushGroups);
    }

    public static IEnumerable<object[]> MarkerScenarios() =>
        new[] { "set", "set-with-tags", "clear" }.Select(n => new object[] { n });

    /// <summary>A rig and the record whose apply sets, sets with a tag change, or clears the marker.</summary>
    private static (ApplyRig Rig, SyncRecordModule.SyncRecord Record) MarkerScenario(string name)
    {
        var rig = new ApplyRig();
        rig.Apply(ApplyRig.Rec(Id, content: "Newer.", minute: 20));
        switch (name)
        {
            case "set":
                return (rig, ApplyRig.Rec(Id, content: "Older.", minute: 10));
            case "set-with-tags":
                return (rig, ApplyRig.Rec(Id, content: "Older.", tags: ["theirs"], minute: 10));
            default:
                Assert.IsType<EntryResult.Carried>(rig.Apply(ApplyRig.Rec(Id, content: "Older.", minute: 10)));
                return (rig, ApplyRig.Rec(Id, content: "Newer.", minute: 20));
        }
    }

    [Theory]
    [MemberData(nameof(MarkerScenarios))]
    public void MarkerWrite_FaultAtEveryStep_RollsBackTheMarkerWithTheRest(string scenario)
    {
        var steps = new List<string>();
        var (probe, probeRecord) = MarkerScenario(scenario);
        using (probe)
        {
            var markerBefore = probe.Scan.Rows()[Id].RepushGroups;
            probe.Applier.BeforeEntryStep = (_, step) => steps.Add(step);
            Assert.IsType<EntryResult.Carried>(Run(probe, probeRecord));
            Assert.NotEqual(markerBefore, probe.Scan.Rows()[Id].RepushGroups);
        }
        Assert.Contains("row", steps);
        Assert.Contains("commit", steps);
        if (scenario == "set-with-tags")
            Assert.Contains("update", steps);

        foreach (var step in steps.Distinct())
        {
            var (rig, record) = MarkerScenario(scenario);
            using (rig)
            {
                var before = rig.Full(issues: false); // content, vectors, import log, rows (the marker included), held items, clock
                var beforeIssues = rig.Scan.Issues();
                rig.Applier.BeforeEntryStep = (_, current) =>
                {
                    if (current == step)
                        throw new InvalidOperationException("fault at " + step);
                };

                var result = Run(rig, record);

                Assert.Equal(before, rig.Full(issues: false));
                if (step is "reread" or "issue")
                {
                    Assert.True(result is EntryResult.Deferred, $"{scenario}/{step}: {result}");
                    Assert.Equal(beforeIssues, rig.Scan.Issues());
                }
                else
                {
                    Assert.True(result is EntryResult.Failed { Attempts: 1 }, $"{scenario}/{step}: {result}");
                    var issue = Assert.Single(rig.Scan.Issues());
                    Assert.Equal((Id, "apply-failed"), (issue.Id, issue.Kind));
                }
            }
        }
    }
}
