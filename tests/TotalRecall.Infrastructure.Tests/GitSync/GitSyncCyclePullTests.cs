using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-i-4, decision 5: the fetch, the apply and the push gate.</summary>
public sealed class GitSyncCyclePullTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";

    private readonly CycleRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private bool StoreHas(string id) =>
        _rig.Pull.Store.Get(Tier.Warm, ContentType.Memory, id) is not null
        || _rig.Pull.Store.Get(Tier.Hot, ContentType.Memory, id) is not null
        || _rig.Pull.Store.Get(Tier.Cold, ContentType.Memory, id) is not null;

    // ---- the fetch ----

    [GitFact]
    public async Task APullThatBringsAnotherMachinesMemory_ReachesTheApply_AndTheOrderIsFixed()
    {
        _rig.Push.Seed(Rec(A, "Use bun."));

        var summary = Done(await _rig.Run());

        Assert.True(StoreHas(A));
        Assert.Equal(["lock", "identity", "locks", "scan", "fetch", "apply", "push", "record"], _rig.Steps);
        Assert.NotNull(summary.Fetch);
        Assert.Equal(1, summary.Apply!.Inserted);
        Assert.True(summary.Apply.Complete);
    }

    [GitFact]
    public async Task NothingNew_StillRunsTheApply()
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        _rig.Steps.Clear();

        var summary = Done(await _rig.Run());

        Assert.True(summary.Fetch!.NothingNew);
        Assert.Contains("apply", _rig.Steps);
    }

    [GitTheory]
    [InlineData("replaced")]
    [InlineData("newer")]
    [InlineData("missing-branch")]
    public async Task ALateRefusal_IsRefusedAfterTheScan_NothingIsApplied_AndItIsRecorded(string which)
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        var okBefore = _rig.LastOk;
        _rig.Now += TimeSpan.FromMinutes(5);
        switch (which)
        {
            case "replaced":
                _rig.ReplaceManifestOnRemote(PullRig.ManifestBytes(repoId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
                break;
            case "newer":
                _rig.ReplaceManifestOnRemote(PullRig.ManifestBytes(format: "total-recall/v999"));
                break;
            default:
                _rig.Pull.Fault = c => c.IsLsRemote ? FaultingGitProcess.Text("") : null;
                break;
        }
        var rows = _rig.Push.RowDump();
        _rig.Steps.Clear();
        var pushes = _rig.PushCalls();

        var refused = Assert.IsType<CycleOutcome.Refused>(await _rig.Run());

        Assert.Equal("fetch", refused.Step);
        Assert.Equal(["lock", "identity", "locks", "scan", "fetch", "record"], _rig.Steps);
        Assert.Equal(rows, _rig.Push.RowDump());
        Assert.Equal(pushes, _rig.PushCalls());
        Assert.Matches(CycleRig.ErrorLine("fetch", "Refused"), _rig.LastError!);
        Assert.Equal(okBefore, _rig.LastOk);
        using var free = await _rig.HoldLock();
    }

    [GitTheory]
    [InlineData("Auth")]
    [InlineData("Network")]
    public async Task AFailedFetch_IsFailedWithItsKind_NotPublished_AndAClearedByALaterDone(string kind)
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        _rig.Pull.Fault = c => c.IsLsRemote
            ? FaultingGitProcess.Failure(kind == "Auth" ? SyncGit.FailureKind.Auth : SyncGit.FailureKind.Network, "failed")
            : null;

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("fetch", failed.Step);
        Assert.Equal(kind, failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Matches(CycleRig.ErrorLine("fetch", kind), _rig.LastError!);
        Assert.Equal(0, _rig.PushCalls());

        _rig.Pull.Fault = _ => null;
        Done(await _rig.Run());
        Assert.Null(_rig.LastError);
    }

    // ---- the apply and the gate ----

    [GitFact]
    public async Task AFailedApply_IsFailedAtTheApply_AndNeverReachesThePush()
    {
        _rig.Push.Seed(Rec(A));
        _rig.Pull.Fault = c => c.IsLsTree ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "tree unreadable") : null;

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("apply", failed.Step);
        Assert.Equal("Apply", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Matches(CycleRig.ErrorLine("apply", "Apply"), _rig.LastError!);
        Assert.Equal(0, _rig.PushCalls());
        Assert.Null(_rig.LastOk);
    }

    [GitFact]
    public async Task AnIncompletePull_IsDone_WithThePushSkipped_AndLastOkIsRecorded()
    {
        _rig.Push.Seed(Rec(A, "Use bun."), Rec(B, "Use pnpm."));
        Done(await _rig.Run());
        _rig.RemoveRecordOnRemote(B); // another machine deleted it: the pull postpones a deletion
        _rig.Pull.Store.DeleteMeta(SyncIdentity.MetaKeys.LastOk);
        _rig.Now += TimeSpan.FromMinutes(1);

        var summary = Done(await _rig.Run());

        Assert.False(summary.Apply!.Complete);
        Assert.NotEmpty(summary.Apply.Postponed);
        Assert.Equal("pull-incomplete", summary.PushSkipped);
        Assert.Null(summary.Push);
        Assert.True(StoreHas(B));
        Assert.Equal(0, _rig.PushCalls());
        Assert.Equal(SyncCycle.okValue(_rig.Now), _rig.LastOk);
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task EntryLevelApplyFailures_DoNotStopTheCycle()
    {
        _rig.Push.Seed(Rec(A, "Use bun."), Rec(B, "Use pnpm."));
        _rig.Push.Embedder.Fails = text => text.Contains("bun");

        var summary = Done(await _rig.Run());

        Assert.Single(summary.Apply!.Failed);
        Assert.True(summary.Apply.Complete);
        Assert.True(StoreHas(B));
        Assert.Null(summary.PushSkipped);
        Assert.NotNull(_rig.LastOk);
    }
}
