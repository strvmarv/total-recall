using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-ii: the two-machine rig, on deletions that cross machines.</summary>
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

    [GitTheory]
    [InlineData("A first")]
    [InlineData("B first")]
    public async Task TextEditOnB_BeatsAForgetOnA_InEitherPushOrder(string order)
    {
        const string edited = "Use pnpm, not npm.";
        _rig.Seed();
        _rig.A.Push.AddMemory(Tier.Warm, Id, "Use bun, not npm.");
        Assert.IsType<CycleOutcome.Done>(await _rig.RunA());
        _rig.JoinB();
        Assert.IsType<CycleOutcome.Done>(await _rig.RunB());
        _rig.A.Push.Now += 1000;
        _rig.B.Push.Now += 1000;

        _rig.A.Push.Store.Delete(Tier.Warm, ContentType.Memory, Id);
        _rig.B.Push.Edit(Tier.Warm, Id, new UpdateEntryOpts { Content = edited });
        if (order == "A first")
        {
            var a = Assert.IsType<CycleOutcome.Done>(await _rig.RunA()).Summary;
            Assert.Equal([Id], Assert.IsType<PushOutcome.Pushed>(a.Push).Summary.Removed);
            var b = Assert.IsType<CycleOutcome.Done>(await _rig.RunB()).Summary;
            Assert.Equal([new NoteEntry(Id, "edit-beats-deletion")], b.Apply!.Notes);
            Assert.Equal(1, Assert.IsType<PushOutcome.Pushed>(b.Push).Summary.New);   // the write-back counts as added
            Assert.EndsWith(" +1 ~0 -0", _rig.A.Push.Remote.Git("log", "-1", "--format=%s", "main").Trim());
            var again = Assert.IsType<CycleOutcome.Done>(await _rig.RunA()).Summary;
            Assert.Equal([new NoteEntry(Id, "edit-beats-deletion")], again.Apply!.Notes);
        }
        else
        {
            var b = Assert.IsType<CycleOutcome.Done>(await _rig.RunB()).Summary;
            Assert.Equal(1, Assert.IsType<PushOutcome.Pushed>(b.Push).Summary.Changed);
            var a = Assert.IsType<CycleOutcome.Done>(await _rig.RunA()).Summary;
            Assert.Equal([new NoteEntry(Id, "edit-beats-deletion")], a.Apply!.Notes);
            Assert.Empty(a.Apply.PushDeletions);
            Assert.IsType<PushOutcome.NothingToPush>(a.Push);
        }
        Assert.IsType<CycleOutcome.Done>(await _rig.RunB());

        foreach (var machine in new[] { _rig.A, _rig.B })
        {
            var entry = machine.Pull.Store.Get(Tier.Warm, ContentType.Memory, Id);
            Assert.NotNull(entry);
            Assert.Equal(edited, entry!.Content);
            Assert.False(machine.Pull.Store.IsSticky(ContentType.Memory, Id));
            var row = GitSyncRows.ReadAll(machine.Pull.Connection)[Id];
            Assert.Equal(SyncMerge.RowState.Active, row.State);
            Assert.Null(row.ContentStamp);
        }
        Assert.Contains(edited, _rig.A.Push.Remote.Git("show", "main:" + SyncValidation.recordPath(Id)));
        Assert.Empty(GitSyncReviews.ReadOpenHeld(_rig.A.Pull.Connection));
        Assert.Empty(GitSyncReviews.ReadOpenHeld(_rig.B.Pull.Connection));
    }
}
