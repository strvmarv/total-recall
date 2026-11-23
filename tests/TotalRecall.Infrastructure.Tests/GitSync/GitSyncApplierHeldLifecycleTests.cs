using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-iii (held deletions), D10 and the D4 visit set: a pull that leaves a local item open refreshes its record
/// hash when the record changed (state, cause, direction and <c>arrived_at</c> kept); the unproven fallback hold replaces
/// an open item of the other direction and keeps one of the same direction, keeps an approved item against a local hold
/// and replaces it with a remote one; every id with a held item is visited, so an entry re-created under its row closes
/// its open local item.
/// </summary>
public sealed class GitSyncApplierHeldLifecycleTests : IDisposable
{
    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000b2";
    private const string OldHash = "0123456789abcdef0123456789abcdef01234567";

    private readonly PullRig _rig = new();
    private readonly GitSyncApplier _applier;
    private long _now = 1_780_000_000_000L;

    public GitSyncApplierHeldLifecycleTests()
    {
        _applier = new GitSyncApplier(_rig.Client, _rig.Store, new CountingEmbedder(), new ApplyOptions(0), () => _now);
        _rig.Join();
        _rig.PutManifest();
        _rig.Commit("manifest");
        _rig.Clone();
    }

    public void Dispose() => _rig.Dispose();

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

    private static SyncRecordModule.SyncRecord Rec(string id, string content = "Use bun, not npm.", IEnumerable<string>? tags = null) =>
        ApplyRig.Rec(id, content, tags: tags);

    private string BlobOf(string id) => _rig.Source.Git("rev-parse", "HEAD:" + SyncValidation.recordPath(id)).Trim();

    private Dictionary<string, HeldItem> OpenHeld() => GitSyncReviews.ReadOpenHeld(_rig.Connection);

    private HeldItem? Item(string id) => GitSyncReviews.ReadHeldItem(_rig.Connection, id);

    private void Unproven() => _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");

    private void FaultAt(string step) =>
        _applier.BeforeEntryStep = (id, s) =>
        {
            if (id == A && s == step)
                throw new InvalidOperationException("fault at " + step);
        };

    /// <summary>A and B pulled (both rows have a base, <c>L</c> is set).</summary>
    private async Task Synced()
    {
        _rig.PutRecord(Rec(A));
        _rig.PutRecord(Rec(B, "Other."));
        _rig.Commit("records");
        await Pull();
    }

    /// <summary>A synced, deleted here, and held by an unproven pull (an open local item with the record's blob id).</summary>
    private async Task<HeldItem> LocalHoldFromAnUnprovenPull()
    {
        await Synced();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
        Unproven();
        var summary = await Pull();
        Assert.False(summary.Proven);
        var item = OpenHeld()[A];
        Assert.Equal(new HeldItem(A, "local", BlobOf(A), "unproven", _now), item);
        return item;
    }

    /// <summary>A and B synced; then A's record is removed and the protective ref is deleted, so the next pull is unproven.</summary>
    private async Task SyncedThenAVanishesUnproven()
    {
        await Synced();
        _rig.Remove(SyncValidation.recordPath(A));
        _rig.Commit("remove a");
        Unproven();
    }

    private void SeedApproved(string id, string? hash)
    {
        GitSyncReviews.UpsertHeld(_rig.Connection, new HeldItem(id, "local", hash, "guard", 1_700_000_000_000L));
        GitSyncReviews.ApproveHeld(_rig.Connection, id);
    }

    // ---- D10.1: a refreshed hash ----

    [GitFact]
    public async Task APullThatLeavesALocalItemOpen_RefreshesItsHash_WhenTheRecordChanged_AndKeepsArrivedAt()
    {
        var held = await LocalHoldFromAnUnprovenPull();
        _now += 60_000;
        _rig.PutRecord(Rec(A, tags: ["retagged"]));
        _rig.Commit("retag a");

        var summary = await Pull();

        Assert.True(summary.Proven);
        var item = OpenHeld()[A];
        Assert.Equal(BlobOf(A), item.RecordHash);
        Assert.NotEqual(held.RecordHash, item.RecordHash);
        Assert.Equal(held.ArrivedAt, item.ArrivedAt);
        Assert.Equal(held.Cause, item.Cause);
        Assert.Equal("open", item.State);
        Assert.Equal(held with { RecordHash = item.RecordHash }, item);
        Assert.Empty(summary.Held);
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task AnUnchangedRecord_LeavesTheLocalItemUntouched()
    {
        var held = await LocalHoldFromAnUnprovenPull();
        _now += 60_000;

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Equal(held, OpenHeld()[A]);
        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(0, summary.Updated);
        Assert.Empty(summary.Held);
        Assert.True(summary.Complete);
    }

    // ---- D10.2: the unproven fallback hold ----

    [GitFact]
    public async Task TheFallback_ReplacesAnOpenItemOfTheOtherDirection()
    {
        await SyncedThenAVanishesUnproven();
        GitSyncReviews.UpsertHeld(_rig.Connection, new HeldItem(A, "local", OldHash, "unproven", 1_700_000_000_000L));
        var scan = ScanSummary.Empty with { Skipped = [new SkippedItem(A, "content", "unreadable entry")] };

        var summary = await Pull(scan);

        Assert.False(summary.Proven);
        Assert.Equal([new HeldEntry(A, "remote", "unproven")], summary.Held);
        Assert.Equal(new HeldItem(A, "remote", null, "unproven", _now), OpenHeld()[A]);
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task TheFallback_KeepsAnApprovedItem_AgainstALocalFallbackHold()
    {
        await Synced();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
        SeedApproved(A, BlobOf(A));
        var approved = Item(A);
        Unproven();
        FaultAt("commit");

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.Equal(A, Assert.Single(summary.Failed).Id);
        Assert.Empty(summary.Held);
        Assert.Equal("approved", Item(A)!.State);
        Assert.Equal(approved, Item(A));
    }

    [GitFact]
    public async Task TheFallback_ReplacesAnOpenRemoteItem_WithALocalFallbackHold()
    {
        await Synced();
        _rig.Store.Delete(Tier.Warm, ContentType.Memory, A);
        GitSyncReviews.UpsertHeld(_rig.Connection, new HeldItem(A, "remote", null, "guard", 1_700_000_000_000L));
        Unproven();
        FaultAt("commit");

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.Equal(A, Assert.Single(summary.Failed).Id);
        Assert.Equal([new HeldEntry(A, "local", "unproven")], summary.Held);
        Assert.Equal(new HeldItem(A, "local", BlobOf(A), "unproven", _now), OpenHeld()[A]);
    }

    [GitFact]
    public async Task TheFallback_ReplacesAnApprovedItem_WithARemoteFallbackHold()
    {
        await SyncedThenAVanishesUnproven();
        SeedApproved(A, OldHash);
        FaultAt("commit");

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.Equal(A, Assert.Single(summary.Failed).Id);
        Assert.Equal([new HeldEntry(A, "remote", "unproven")], summary.Held);
        Assert.Equal(new HeldItem(A, "remote", null, "unproven", _now), Item(A));
    }

    // ---- D4: every held id is visited ----

    [GitFact]
    public async Task AnOpenLocalItem_WhoseEntryWasRecreated_IsClosedByOneProvenPull()
    {
        await LocalHoldFromAnUnprovenPull();
        _rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(
            Content: "Use bun, not npm.", Id: A, EntryType: EntryType.Correction, CreatedAt: 1_700_000_000_000L));

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Equal([A], summary.ClosedHeld);
        Assert.Null(Item(A));
        Assert.True(summary.Complete);
    }
}
