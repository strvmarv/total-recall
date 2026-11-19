using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using SyncRecordModule = TotalRecall.Core.SyncRecord;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Milestone 1b-ii, the umbrella's "a missing file is never proof": in an unproven pull a deletion candidate whose entry
/// ended failed, skipped or scan-skipped (so its own transaction wrote no hold) still gets an open <c>unproven</c> item
/// before <c>L</c> moves, so the next proven pull cannot delete the memory; when that item cannot be written the pull is
/// incomplete and <c>L</c> stays.
/// </summary>
public sealed class GitSyncApplierUnprovenTests : IDisposable
{
    private const string A = "3f9c2a7e-0000-4000-8000-0000000000a1";
    private const string B = "3f9c2a7e-0000-4000-8000-0000000000b2";

    private readonly PullRig _rig = new();
    private readonly GitSyncApplier _applier;
    private readonly long _now = 1_780_000_000_000L;

    public GitSyncApplierUnprovenTests()
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

    private static SyncRecordModule.SyncRecord Rec(string id, string content = "Use bun, not npm.") => ApplyRig.Rec(id, content);

    private bool Has(string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => _rig.Store.Get(t, ContentType.Memory, id) is not null);

    private Dictionary<string, HeldItem> OpenHeld() => GitSyncReviews.ReadOpenHeld(_rig.Connection);

    private string? Last => _rig.Store.GetMeta(SyncIdentity.MetaKeys.LastSha);

    private string RefL => _rig.CloneRef("refs/total-recall/last-synced") ?? "";

    /// <summary>
    /// A and B pulled (both rows have a base, <c>L</c> is set); then A's record is removed and the protective ref is
    /// deleted, so the next pull is unproven and A is a vanished-record candidate. Returns the head before the removal.
    /// </summary>
    private async Task<string> SyncedThenAVanishesUnproven()
    {
        _rig.PutRecord(Rec(A));
        _rig.PutRecord(Rec(B, "Other."));
        var before = _rig.Commit("records");
        await Pull();
        Assert.Equal(before, Last);
        _rig.Remove(SyncValidation.recordPath(A));
        _rig.Commit("remove a");
        _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced"); // no L: an unproven pull
        return before;
    }

    private void FaultAt(params string[] steps) =>
        _applier.BeforeEntryStep = (id, step) =>
        {
            if (id == A && steps.Contains(step))
                throw new InvalidOperationException("fault at " + step);
        };

    /// <summary>The next pull is proven and leaves A alone: the item the unproven pull wrote keeps it (W6.3).</summary>
    private async Task AssertAProvenPullKeepsA()
    {
        _applier.BeforeEntryStep = null;
        var next = await Pull();

        Assert.True(next.Proven);
        Assert.Empty(next.Deleted);
        Assert.True(Has(A));
        Assert.Equal("unproven", OpenHeld()[A].Cause);
    }

    [GitFact]
    public async Task AVanishedRecordWhoseHoldFails_IsHeldByTheFallback_AndAProvenPullDoesNotDeleteIt()
    {
        await SyncedThenAVanishesUnproven();
        FaultAt("hold");

        var summary = await Pull();

        Assert.False(summary.Proven);
        Assert.Equal(A, Assert.Single(summary.Failed).Id);
        Assert.Equal([new HeldEntry(A, "remote", "unproven")], summary.Held);
        Assert.Equal(new HeldItem(A, "remote", null, "unproven", _now), OpenHeld()[A]);
        Assert.True(Has(A));
        Assert.True(summary.Complete);
        Assert.Equal(_rig.Head, Last);
        Assert.Equal(_rig.Head, RefL);

        await AssertAProvenPullKeepsA();
    }

    [GitFact]
    public async Task AVanishedRecordTheScanSkipped_IsHeldByTheFallback_AndAProvenPullDoesNotDeleteIt()
    {
        await SyncedThenAVanishesUnproven();
        var scan = ScanSummary.Empty with { Skipped = [new SkippedItem(A, "content", "unreadable entry")] };

        var summary = await Pull(scan);

        Assert.False(summary.Proven);
        Assert.Contains(summary.Skipped, s => s.Id == A);
        Assert.Equal([new HeldEntry(A, "remote", "unproven")], summary.Held);
        Assert.Equal(new HeldItem(A, "remote", null, "unproven", _now), OpenHeld()[A]);
        Assert.True(Has(A));
        Assert.True(summary.Complete);
        Assert.Equal(_rig.Head, Last);

        await AssertAProvenPullKeepsA();
    }

    [GitFact]
    public async Task AVanishedRecordTheApplierSkipped_IsHeldByTheFallback_AndAProvenPullDoesNotDeleteIt()
    {
        await SyncedThenAVanishesUnproven();
        ScanRig.Exec(_rig.Connection, $"INSERT INTO cold_memories SELECT * FROM warm_memories WHERE id = '{A}'"); // in two tiers

        var summary = await Pull(ScanSummary.Empty); // a scan that did not see it: the applier skips it

        Assert.Contains(new SkippedItem(A, null, "the id is in more than one tier"), summary.Skipped);
        Assert.Equal([new HeldEntry(A, "remote", "unproven")], summary.Held);
        Assert.Equal(new HeldItem(A, "remote", null, "unproven", _now), OpenHeld()[A]);
        Assert.True(summary.Complete);
        Assert.Equal(_rig.Head, Last);

        ScanRig.Exec(_rig.Connection, $"DELETE FROM cold_memories WHERE id = '{A}'"); // readable again
        await AssertAProvenPullKeepsA();
    }

    [GitFact]
    public async Task AnOpenItemOfAScanSkippedCandidate_IsNotReplacedByTheFallback()
    {
        await SyncedThenAVanishesUnproven();
        var guard = new HeldItem(A, "remote", null, "guard", 1_700_000_000_000L);
        GitSyncReviews.UpsertHeld(_rig.Connection, guard);
        var scan = ScanSummary.Empty with { Skipped = [new SkippedItem(A, "content", "unreadable entry")] };

        var summary = await Pull(scan);

        Assert.Empty(summary.Held);
        Assert.Equal(guard, OpenHeld()[A]);
        Assert.True(summary.Complete);
    }

    [GitFact]
    public async Task AFallbackHoldThatCannotBeWritten_DefersTheEntry_AndLDoesNotMove()
    {
        var before = await SyncedThenAVanishesUnproven();
        FaultAt("hold", "hold-unproven");

        var summary = await Pull();

        Assert.False(summary.Complete);
        Assert.Equal(A, Assert.Single(summary.Failed).Id);
        var deferred = Assert.Single(summary.Deferred);
        Assert.Equal(A, deferred.Id);
        Assert.StartsWith("an unproven deletion candidate could not be held", deferred.Reason);
        Assert.Empty(summary.Held);
        Assert.Empty(OpenHeld());
        Assert.True(Has(A));
        Assert.Equal(before, Last);
        Assert.Equal("", RefL);
    }

    [GitFact]
    public async Task AProvenPull_WritesNoFallbackItem()
    {
        _rig.PutRecord(Rec(A));
        _rig.PutRecord(Rec(B, "Other."));
        _rig.Commit("records");
        await Pull();
        _rig.Remove(SyncValidation.recordPath(A));
        var head = _rig.Commit("remove a");
        FaultAt("row"); // the deletion's own transaction fails

        var summary = await Pull();

        Assert.True(summary.Proven);
        Assert.Equal(A, Assert.Single(summary.Failed).Id);
        Assert.Empty(summary.Held);
        Assert.Empty(OpenHeld());
        Assert.True(Has(A));
        Assert.True(summary.Complete);
        Assert.Equal(head, Last);
    }

    [GitFact]
    public async Task ANeverPushedEntryInAnUnprovenPull_IsNotACandidate()
    {
        _rig.PutRecord(Rec(B, "Other."));
        _rig.Commit("records");
        await Pull();
        _rig.Store.Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts(Content: "Local only.", Id: A));
        Scan(); // A gets a row with no base
        _rig.CloneGit("update-ref", "-d", "refs/total-recall/last-synced");
        var scan = ScanSummary.Empty with { Skipped = [new SkippedItem(A, "content", "unreadable entry")] };

        var summary = await Pull(scan);

        Assert.False(summary.Proven);
        Assert.Empty(summary.Held);
        Assert.Empty(OpenHeld());
        Assert.True(summary.Complete);
    }
}
