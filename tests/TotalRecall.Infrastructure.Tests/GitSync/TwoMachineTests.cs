using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-ii: the two-machine rig, on behaviour that already exists.</summary>
public sealed class TwoMachineTests : IDisposable
{
    private const string Id = "3f9c2a7e";

    private readonly TwoMachineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [GitFact]
    public async Task AMemoryAddedOnA_ReachesB_AfterTwoCycles()
    {
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, "Use bun, not npm.");

        Assert.IsType<CycleOutcome.Done>(await _rig.RunA());
        _rig.JoinB();
        Assert.IsType<CycleOutcome.Done>(await _rig.RunB());

        var onB = _rig.B.Pull.Store.Get(Tier.Warm, ContentType.Memory, Id);
        Assert.NotNull(onB);
        Assert.Equal("Use bun, not npm.", onB!.Content);
        Assert.NotEqual(_rig.A.Pull.DbPath, _rig.B.Pull.DbPath);
        Assert.NotEqual(_rig.A.Pull.Paths.CloneDirectory, _rig.B.Pull.Paths.CloneDirectory);
    }

    [GitFact]
    public async Task ALocalDeletion_IsPushed_ThroughTheCycle()
    {
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, "Use bun, not npm.");
        Assert.IsType<CycleOutcome.Done>(await _rig.RunA());
        _rig.JoinB();
        Assert.IsType<CycleOutcome.Done>(await _rig.RunB());
        Assert.NotNull(_rig.B.Pull.Store.Get(Tier.Warm, ContentType.Memory, Id));
        var path = SyncValidation.recordPath(Id);

        _rig.A.Push.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        var onA = Assert.IsType<CycleOutcome.Done>(await _rig.RunA()).Summary;

        Assert.Equal([Id], onA.Apply!.PushDeletions);
        Assert.Equal([Id], Assert.IsType<PushOutcome.Pushed>(onA.Push).Summary.Removed);
        var remote = _rig.A.Push.Remote;
        Assert.Equal("", remote.Git("ls-tree", "--name-only", "main", "--", path).Trim());
        Assert.EndsWith(" +0 ~0 -1", remote.Git("log", "-1", "--format=%s", "main").Trim());
        var rowA = GitSyncRows.ReadAll(_rig.A.Pull.Connection)[Id];
        Assert.Equal(SyncMerge.RowState.Forgotten, rowA.State);
        Assert.NotNull(rowA.BaseJson);

        var onB = Assert.IsType<CycleOutcome.Done>(await _rig.RunB()).Summary;

        Assert.Equal([Id], onB.Apply!.Deleted);
        Assert.Null(_rig.B.Pull.Store.Get(Tier.Warm, ContentType.Memory, Id));
        Assert.Equal(SyncMerge.RowState.Forgotten, GitSyncRows.ReadAll(_rig.B.Pull.Connection)[Id].State);
        Assert.IsType<PushOutcome.NothingToPush>(onB.Push);
        Assert.Equal(remote.Head(), _rig.B.Pull.Last);
    }
}
