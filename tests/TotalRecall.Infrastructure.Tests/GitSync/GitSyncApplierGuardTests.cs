using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// The delete guard of the pull (milestone 1b-ii, D1 to D5): the preview counts each direction's deletions, a direction
/// over <c>max(10, N * percent / 100)</c> applies none of them and holds each with the cause <c>guard</c>, and the pull is
/// still complete.
/// </summary>
public sealed class GitSyncApplierGuardTests : IDisposable
{
    private readonly PullRig _rig = new();
    private readonly CountingEmbedder _embedder = new();
    private GitSyncApplier _applier;
    private long _now = 1_780_000_000_000L;

    public GitSyncApplierGuardTests()
    {
        _applier = NewApplier(20);
        _rig.Join();
        _rig.PutManifest();
        _rig.Commit("manifest");
        _rig.Clone();
    }

    public void Dispose() => _rig.Dispose();

    private GitSyncApplier NewApplier(int percent) =>
        _applier = new GitSyncApplier(_rig.Client, _rig.Store, _embedder, new ApplyOptions(0, percent), () => _now);

    private static string IdOf(int i) => $"3f9c2a7e-0000-4000-8000-{i:x12}";

    private static string PathOf(string id) => $"memories/{id[..2]}/{id}.json";

    private static SyncRecordModule.SyncRecord Rec(string id, string? content = null, int minute = 10, Tier? tier = null) =>
        ApplyRig.Rec(id, content ?? "Text of " + id, tier, minute: minute);

    private ScanSummary Scan() => Assert.IsType<ScanOutcome.Done>(new GitSyncScanner(_rig.Connection, () => _now).Scan()).Summary;

    private async Task<ApplySummary> Pull(ScanSummary? scan = null)
    {
        var fetch = await _rig.Fetcher().FetchAsync();
        var result = fetch switch
        {
            FetchOutcome.Fetched f => f.Result,
            FetchOutcome.NothingNew n => n.Result,
            _ => throw new Xunit.Sdk.XunitException("the fetch did not succeed: " + fetch),
        };
        return Assert.IsType<ApplyOutcome.Done>(await _applier.ApplyAsync(result, scan ?? Scan())).Summary;
    }

    /// <summary>
    /// <paramref name="n"/> memories with records in <c>R</c>, rows and bases (one pull), in ordinal id order, numbered from
    /// <paramref name="first"/>; warm, except the first <paramref name="hot"/> ones.
    /// </summary>
    private async Task<string[]> Seed(int n, int first = 1, int hot = 0)
    {
        var ids = Enumerable.Range(first, n).Select(IdOf).ToArray();
        for (var i = 0; i < ids.Length; i++)
            _rig.PutRecord(Rec(ids[i], tier: i < hot ? Tier.Hot : Tier.Warm));
        _rig.Commit("records");
        var summary = await Pull();
        Assert.Equal(n, summary.Inserted);
        Assert.True(summary.Complete);
        return ids;
    }

    private void RemoveRecords(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            _rig.Remove(PathOf(id));
    }

    private void DeleteLocally(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            _rig.Store.Delete(Tier.Warm, ContentType.Memory, id);
    }

    private bool Has(string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => _rig.Store.Get(t, ContentType.Memory, id) is not null);

    private GitSyncRow Row(string id) => GitSyncRows.Read(_rig.Connection, id)!;

    private Dictionary<string, HeldItem> OpenHeld() => GitSyncReviews.ReadOpenHeld(_rig.Connection);

    private string BlobOf(string path) => _rig.Source.Git("rev-parse", "HEAD:" + path).Trim();

    private void AssertNoForgottenRow() =>
        Assert.DoesNotContain(GitSyncRows.ReadAll(_rig.Connection).Values, r => r.State.IsForgotten);

    // ---- the remote direction ----

    [GitFact]
    public async Task ExactlyTheLimit_RemoteDeletionsAreAllApplied()
    {
        var ids = await Seed(100);
        var gone = ids.Take(20).ToArray(); // 100 rows at 20 %: the limit is 20
        RemoveRecords(gone);
        _rig.Commit("remove twenty");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Empty(summary.GuardTripped);
        Assert.Equal(gone, summary.Deleted);
        Assert.Empty(summary.Held);
        Assert.All(gone, id => Assert.False(Has(id)));
    }

    [GitFact]
    public async Task OneOverTheLimit_NoRemoteDeletionIsApplied_EachIsHeldWithCauseGuard_AndTheCountAndLimitAreReported()
    {
        var ids = await Seed(100);
        var gone = ids.Take(21).ToArray();
        RemoveRecords(gone);
        _rig.Commit("remove twenty-one");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Equal([new GuardTrip("remote", 21, 20)], summary.GuardTripped);
        Assert.Empty(summary.Deleted);
        Assert.Equal(gone.Select(id => new HeldEntry(id, "remote", "guard")), summary.Held);
        Assert.All(gone, id => Assert.True(Has(id)));
        var held = OpenHeld();
        Assert.Equal(gone, held.Keys.Order(StringComparer.Ordinal));
        Assert.All(held.Values, h => Assert.Equal(new HeldItem(h.EntryId, "remote", null, "guard", _now), h));
        AssertNoForgottenRow();
    }

    [GitTheory]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public async Task SmallStore_LimitIsTen(int deletions, bool trips)
    {
        var ids = await Seed(12); // 12 * 20 / 100 = 2, so the limit is 10
        var gone = ids.Take(deletions).ToArray();
        RemoveRecords(gone);
        _rig.Commit("remove some");

        var summary = await Pull();

        Assert.True(summary.Complete);
        if (trips)
        {
            Assert.Equal([new GuardTrip("remote", deletions, 10)], summary.GuardTripped);
            Assert.Empty(summary.Deleted);
            Assert.Equal(deletions, summary.Held.Count);
            Assert.All(summary.Held, h => Assert.Equal("guard", h.Cause));
            Assert.All(gone, id => Assert.True(Has(id)));
        }
        else
        {
            Assert.Empty(summary.GuardTripped);
            Assert.Equal(gone, summary.Deleted);
            Assert.Empty(summary.Held);
        }
    }

    // ---- the local direction ----

    [GitFact]
    public async Task LocalDirectionIsJudgedSeparately()
    {
        var ids = await Seed(100);
        var local = ids.Take(21).ToArray();
        var remote = ids.Skip(21).Take(3).ToArray();
        DeleteLocally(local);
        RemoveRecords(remote);
        _rig.Commit("remove three");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Equal([new GuardTrip("local", 21, 20)], summary.GuardTripped);
        Assert.Equal(remote, summary.Deleted);
        Assert.Empty(summary.PushDeletions);
        Assert.Equal(local.Select(id => new HeldEntry(id, "local", "guard")), summary.Held);
        var held = OpenHeld();
        Assert.Equal(local, held.Keys.Order(StringComparer.Ordinal));
        Assert.All(held.Values, h => Assert.Equal(new HeldItem(h.EntryId, "local", BlobOf(PathOf(h.EntryId)), "guard", _now), h));
        Assert.All(local, id => Assert.Equal(SyncMerge.RowState.Active, Row(id).State));
    }

    [GitFact]
    public async Task BothDirections_TripTogether_AndEachReportsItsOwnCount()
    {
        var ids = await Seed(30); // the limit is 10
        var local = ids.Take(11).ToArray();
        var remote = ids.Skip(11).Take(12).ToArray();
        DeleteLocally(local);
        RemoveRecords(remote);
        _rig.Commit("remove twelve");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Equal([new GuardTrip("remote", 12, 10), new GuardTrip("local", 11, 10)], summary.GuardTripped);
        Assert.Empty(summary.Deleted);
        Assert.Empty(summary.PushDeletions);
        Assert.Equal(23, summary.Held.Count);
        Assert.All(summary.Held, h => Assert.Equal("guard", h.Cause));
        Assert.All(remote, id => Assert.True(Has(id)));
    }

    // ---- what is not counted ----

    [GitFact]
    public async Task HeldAndAlreadyOpenItems_AreNotCounted()
    {
        var ids = await Seed(12);
        var gone = ids.Take(11).ToArray();
        var open = gone.Take(2).ToArray();
        foreach (var id in open)
            GitSyncReviews.UpsertHeld(_rig.Connection, new HeldItem(id, "remote", null, "unproven", 1_700_000_000_000L));
        RemoveRecords(gone);
        _rig.Commit("remove eleven");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Empty(summary.GuardTripped); // 9 counted, not 11
        Assert.Equal(gone.Skip(2), summary.Deleted);
        Assert.Empty(summary.Held);
        Assert.All(open, id => Assert.True(Has(id)));
        Assert.Equal(open, OpenHeld().Keys.Order(StringComparer.Ordinal));
        Assert.All(OpenHeld().Values, h => Assert.Equal("unproven", h.Cause)); // left alone, not re-decided
    }

    [GitFact]
    public async Task PinnedHolds_AreNotCounted_AndKeepTheCauseSticky()
    {
        var ids = await Seed(12, hot: 2);
        var gone = ids.Take(11).ToArray();
        var pinned = gone.Take(2).ToArray();
        foreach (var id in pinned)
            _rig.Store.SetSticky(ContentType.Memory, id, true);
        RemoveRecords(gone);
        _rig.Commit("remove eleven");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Empty(summary.GuardTripped); // the two pins are holds that decide made: 9 counted
        Assert.Equal(gone.Skip(2), summary.Deleted);
        Assert.Equal(pinned.Select(id => new HeldEntry(id, "remote", "sticky")), summary.Held);
    }

    [GitFact]
    public async Task UnprovenApply_NeverTrips_AndHoldsEverythingWithCauseUnproven_EvenWithZeroPercent()
    {
        NewApplier(0);
        var ids = await Seed(20);
        var remote = ids.Take(12).ToArray();
        var local = ids.Skip(12).Take(1).ToArray();
        RemoveRecords(remote);
        DeleteLocally(local);
        _rig.Commit("remove twelve");
        _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced"); // no L: an unproven pull

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.True(summary.Complete);
        Assert.Empty(summary.GuardTripped);
        Assert.Empty(summary.Deleted);
        Assert.Empty(summary.PushDeletions);
        Assert.Equal(
            remote.Select(id => new HeldEntry(id, "remote", "unproven")).Concat(local.Select(id => new HeldEntry(id, "local", "unproven"))),
            summary.Held);
        Assert.All(remote, id => Assert.True(Has(id)));
        Assert.Equal(_rig.Head, _rig.Last);
    }

    [GitFact]
    public async Task ZeroPercent_LimitIsStillTen_AndHundredPercentNeverTripsBelowN()
    {
        var ids = await Seed(30);

        NewApplier(100);
        var most = ids.Take(29).ToArray(); // 30 active rows: the limit is 30
        RemoveRecords(most);
        _rig.Commit("remove twenty-nine");
        var atHundred = await Pull();
        Assert.Empty(atHundred.GuardTripped);
        Assert.Equal(most, atHundred.Deleted);

        var fresh = await Seed(20, first: 100); // 21 active rows
        NewApplier(0);
        RemoveRecords(fresh.Take(10));
        _rig.Commit("remove ten");
        var atZero = await Pull();
        Assert.Empty(atZero.GuardTripped); // 21 * 0 / 100 = 0, so the limit is 10
        Assert.Equal(10, atZero.Deleted.Count);

        RemoveRecords(fresh.Skip(10).Take(10).Append(ids[29])); // 11 of the 11 active rows left
        _rig.Commit("remove eleven");
        var overZero = await Pull();
        Assert.Equal([new GuardTrip("remote", 11, 10)], overZero.GuardTripped);
        Assert.Empty(overZero.Deleted);
    }

    // ---- the guard never blocks ----

    [GitFact]
    public async Task TrippedPull_IsCompleteAndAdvancesL()
    {
        var ids = await Seed(12);
        RemoveRecords(ids.Take(11));
        _rig.PutRecord(Rec(ids[11], "An edited text.", minute: 20));
        var head = _rig.Commit("remove eleven, edit one");

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Single(summary.GuardTripped);
        Assert.Empty(summary.Postponed);
        Assert.Empty(summary.Deferred);
        Assert.Equal(12, summary.Updated); // eleven held items written and the edit
        Assert.Equal(head, _rig.Last);
        Assert.Equal(head, _rig.CloneRef("refs/total-recall/last-synced"));
        Assert.Equal("An edited text.", _rig.Store.Get(Tier.Warm, ContentType.Memory, ids[11])!.Content);

        // a later pull with nothing new leaves the guard items open: they are not re-evaluated under the limit
        var again = await Pull();
        Assert.True(again.Complete);
        Assert.Empty(again.GuardTripped);
        Assert.Empty(again.Deleted);
        Assert.Equal(11, OpenHeld().Count);
        Assert.All(OpenHeld().Values, h => Assert.Equal("guard", h.Cause));
    }

    [GitFact]
    public async Task GuardConversion_DropsTheNotes_AndLeavesTheRowActive()
    {
        var ids = await Seed(12);
        var gone = ids.Take(11).ToArray();
        _rig.Store.Update(Tier.Warm, ContentType.Memory, gone[0], new UpdateEntryOpts { Tags = ["local-tag"] }); // a deletion would drop it
        RemoveRecords(gone);
        _rig.Commit("remove eleven");
        var scan = Scan();
        Assert.Equal(1, scan.Tags);
        var before = gone.ToDictionary(id => id, Row);

        var summary = await Pull(scan);

        Assert.Single(summary.GuardTripped);
        Assert.Empty(summary.Notes);
        Assert.Empty(summary.Deleted);
        AssertNoForgottenRow();
        Assert.All(gone, id => Assert.Equal(before[id], Row(id))); // active, base kept, no pending change dropped
        Assert.Equal(new[] { "local-tag" }, _rig.Store.Get(Tier.Warm, ContentType.Memory, gone[0])!.Tags);
    }

    [GitFact]
    public async Task UnreadableCandidate_IsNotCounted_AndReportedByTheRealDecision()
    {
        var ids = await Seed(12);
        var gone = ids.Take(11).ToArray();
        ScanRig.Exec(_rig.Connection, $"UPDATE git_sync_entry SET base_json = '{{' WHERE entry_id = '{gone[0]}'");
        RemoveRecords(gone);
        _rig.Commit("remove eleven");

        var summary = await Pull();

        Assert.Empty(summary.GuardTripped); // 10 counted
        Assert.Equal(gone.Skip(1), summary.Deleted);
        var failure = Assert.Single(summary.Failed);
        Assert.Equal(gone[0], failure.Id);
        Assert.Contains("base cannot be read", failure.Detail);
        Assert.True(Has(gone[0]));
    }

    [GitFact]
    public async Task AnOpenRemoteItem_ThenTheEntryDeletedHere_IsCountedLocal_AndTheConversionLeavesOneLocalItem()
    {
        var ids = await Seed(12);
        var local = ids.Take(11).ToArray();
        GitSyncReviews.UpsertHeld(_rig.Connection, new HeldItem(local[0], "remote", null, "unproven", 1_700_000_000_000L));
        DeleteLocally(local); // the record of local[0] is still in R: an item of the other direction is no reason to skip it
        _rig.PutText("README", "x");
        _rig.Commit("another commit");

        var summary = await Pull();

        Assert.Equal([new GuardTrip("local", 11, 10)], summary.GuardTripped);
        Assert.Equal([local[0]], summary.ClosedHeld);
        Assert.Equal(new HeldItem(local[0], "local", BlobOf(PathOf(local[0])), "guard", _now), OpenHeld()[local[0]]);
        using var cmd = _rig.Connection.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM git_sync_review WHERE entry_id = '{local[0]}'";
        Assert.Equal(1L, cmd.ExecuteScalar()); // closed, then held: exactly one item, of the new direction
    }

    [GitFact]
    public async Task W9OwnPush_IsCountedAsALocalDeletion_AndTheGuardCanConvertIt()
    {
        var ids = await Seed(12);
        var own = IdOf(999);
        _rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "Text of " + own, Id: own));
        Scan(); // a row with no base
        ScanRig.Exec(_rig.Connection,
            $"UPDATE git_sync_entry SET pushing_stamps = '{{\"content\":\"{SyncStamp.StampModule.format(ApplyRig.St(10))}\"}}' WHERE entry_id = '{own}'");
        _rig.PutRecord(Rec(own)); // this row's own push: the record carries the stamp it sent
        var local = ids.Take(10).ToArray();
        DeleteLocally(local.Append(own));
        _rig.Commit("the own push lands");
        Assert.Contains(Scan().LocalDeletions, d => d.Id == own && !d.HasBase);

        var summary = await Pull();

        Assert.True(summary.Complete);
        Assert.Equal([new GuardTrip("local", 11, 10)], summary.GuardTripped); // ten alone would not trip
        Assert.Empty(summary.PushDeletions);
        Assert.Contains(new HeldEntry(own, "local", "guard"), summary.Held);
        Assert.Equal(new HeldItem(own, "local", BlobOf(PathOf(own)), "guard", _now), OpenHeld()[own]);
        Assert.Equal(SyncMerge.RowState.Active, Row(own).State);
    }
}
