using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

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

    // ---- a hand-reset repository (milestone 1b-ii, D5 and D16) ----

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private static string[] Ids(int count) => [.. Enumerable.Range(0, count).Select(i => $"3f9c2a{i:x2}")];

    private static GitSyncRow RowOf(CycleRig machine, string id) => GitSyncRows.ReadAll(machine.Pull.Connection)[id];

    private static bool Has(CycleRig machine, string id) =>
        new[] { Tier.Hot, Tier.Warm, Tier.Cold }.Any(t => machine.Pull.Store.Get(t, ContentType.Memory, id) is not null);

    private string RemoteText(string id, string rev = "main") =>
        _rig.A.Push.Remote.Git("show", rev + ":" + SyncValidation.recordPath(id));

    private bool RemoteHas(string id) =>
        _rig.A.Push.Remote.Git("ls-tree", "--name-only", "main", "--", SyncValidation.recordPath(id)).Trim() != "";

    /// <summary>
    /// Both machines synced: <paramref name="kept"/> at minute 10 (the commit returned, which a hand reset goes back to), then
    /// another machine's commit (returned as <c>Newer</c>) with every kept record at minute 20 (newer text, promoted to hot)
    /// and <paramref name="vanished"/> added; both machines pulled it, so it is every row's base.
    /// </summary>
    private async Task<(string Reset, string Newer)> BothSyncedTwice(string[] kept, string[] vanished)
    {
        var reset = _rig.Seed([.. kept.Select(id => Rec(id, "Old text of " + id, minute: 10))]);
        Done(await _rig.RunA());
        _rig.JoinB();
        Done(await _rig.RunB());
        var source = _rig.A.Pull;
        foreach (var id in kept)
            source.PutRecord(Rec(id, "New text of " + id, tier: Tier.Hot, minute: 20));
        foreach (var id in vanished)
            source.PutRecord(Rec(id, "Text of " + id, minute: 20));
        var newer = source.Commit("another machine");
        source.Source.PushTo(_rig.A.Push.Remote.Path);
        _rig.A.Push.Now += 60_000;
        _rig.B.Push.Now += 60_000;
        Done(await _rig.RunA());
        Done(await _rig.RunB());
        foreach (var machine in new[] { _rig.A, _rig.B })
            Assert.All(kept.Concat(vanished), id => Assert.Equal(RemoteText(id), RowOf(machine, id).BaseJson));
        return (reset, newer);
    }

    [GitTheory]
    [InlineData(9, 6)]
    [InlineData(15, 3)]   // twelve vanished records: above the guard's limit of 10, which an unproven pull never consults
    public async Task HandResetRemote_SixOfNineRecords_NothingDeletedHeldAndRevertedGroupsRepublished(int total, int keptCount)
    {
        var ids = Ids(total);
        var kept = ids[..keptCount];
        var vanished = ids[keptCount..];
        var (reset, newer) = await BothSyncedTwice(kept, vanished);
        var demoted = kept[0];
        _rig.B.Push.Store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, demoted);   // a local demotion on B
        var remote = _rig.A.Push.Remote;
        remote.AddRef("refs/heads/main", reset);   // the hand reset: six of nine records, each at minute 10
        _rig.A.Push.Now += 60_000;
        _rig.B.Push.Now += 60_000;
        var summaries = new List<CycleSummary>();

        // the first cycle of each machine: unproven pulls
        var firstB = Done(await _rig.RunB());
        var firstA = Done(await _rig.RunA());
        summaries.AddRange([firstB, firstA]);
        Assert.False(firstB.Apply!.Proven);
        Assert.False(firstA.Apply!.Proven);
        Assert.Equal(kept.Select(id => new RepushEntry(id, "content,sticky,tier")), firstB.Apply.RepushMarked);
        var republished = Assert.IsType<PushOutcome.Pushed>(firstB.Push).Summary;
        Assert.Equal(kept, republished.Written);
        Assert.EndsWith($" +0 ~{keptCount} -0", remote.Git("log", "-1", "--format=%s", "main").Trim());
        Assert.Empty(firstA.Apply.RepushMarked);   // B republished the base A holds too
        Assert.IsType<PushOutcome.NothingToPush>(firstA.Push);

        // an unrelated push, then the second cycle of each machine: proven pulls
        remote.PushCommit("unrelated.txt");
        var secondB = Done(await _rig.RunB());
        var secondA = Done(await _rig.RunA());
        summaries.AddRange([secondB, secondA]);
        Assert.True(secondB.Apply!.Proven);
        Assert.True(secondA.Apply!.Proven);
        Assert.IsType<PushOutcome.NothingToPush>(secondB.Push);
        Assert.IsType<PushOutcome.NothingToPush>(secondA.Push);

        Assert.All(summaries, s =>
        {
            Assert.Empty(s.Apply!.Deleted);
            Assert.Empty(s.Apply.GuardTripped);
        });
        Assert.Equal(vanished.Select(id => new HeldEntry(id, "remote", "unproven")), firstB.Apply.Held);
        Assert.Equal(vanished.Select(id => new HeldEntry(id, "remote", "unproven")), firstA.Apply.Held);
        foreach (var machine in new[] { _rig.A, _rig.B })
        {
            Assert.All(ids, id => Assert.True(Has(machine, id), id));   // nothing is deleted locally
            var held = GitSyncReviews.ReadOpenHeld(machine.Pull.Connection);
            Assert.Equal(vanished, held.Keys.Order(StringComparer.Ordinal));
            Assert.All(held.Values, h => Assert.Equal(("remote", "unproven"), (h.Direction, h.Cause)));
            Assert.All(kept, id => Assert.Null(RowOf(machine, id).RepushGroups));
        }
        // each reverted group is republished from its base value and stamp: the records are the ones both machines agreed on
        Assert.All(kept, id => Assert.Equal(RemoteText(id, newer), RemoteText(id)));
        Assert.All(vanished, id => Assert.False(RemoteHas(id)));
        // the local demotion on B never enters the record
        Assert.Equal(Tier.Hot, ReadTier(RemoteText(demoted), demoted));
        Assert.NotNull(_rig.B.Pull.Store.Get(Tier.Warm, ContentType.Memory, demoted));
        Assert.NotNull(_rig.A.Pull.Store.Get(Tier.Hot, ContentType.Memory, demoted));
    }

    private static Tier ReadTier(string text, string id) =>
        RecordReader.Parse(System.Text.Encoding.UTF8.GetBytes(text), SyncValidation.recordPath(id)).Parsed!.Record.Placement.Tier;

    [GitFact]
    public async Task PushFailsAfterAnUnprovenApply_NextCycleStillRepublishes()
    {
        var kept = Ids(3);
        var (reset, newer) = await BothSyncedTwice(kept, []);
        var remote = _rig.A.Push.Remote;
        remote.AddRef("refs/heads/main", reset);
        _rig.A.Push.Now += 60_000;

        // the cycle after the reset: an unproven pull marks every group, and the push is refused
        _rig.A.Pull.Fault = c => c.IsPush ? FaultingGitProcess.Failure(SyncGit.FailureKind.Auth, "authentication failed") : null;
        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.RunA());
        Assert.Equal("push", failed.Step);
        Assert.Equal(reset, remote.Head());
        Assert.Equal(reset, _rig.A.Pull.Last);   // the complete pull advanced L to R
        Assert.All(kept, id => Assert.Equal("content,sticky,tier", RowOf(_rig.A, id).RepushGroups));

        // the next cycle: a pull with nothing new keeps the marker; its first push loses the race and the retry pushes
        string?[]? atRetry = null;
        var pushes = 0;
        _rig.A.Pull.Fault = c =>
        {
            if (c.IsPush && pushes++ == 0)
                remote.PushCommit("unrelated.txt");
            else if (c.IsPush)
                atRetry = [.. kept.Select(id => RowOf(_rig.A, id).RepushGroups)];
            return null;
        };
        _rig.A.Push.Now += 60_000;
        var summary = Done(await _rig.RunA());
        _rig.A.Pull.Fault = _ => null;

        Assert.Equal(2, summary.Attempts);
        Assert.Equal(kept, Assert.IsType<PushOutcome.Pushed>(summary.Push).Summary.Written);
        Assert.NotNull(atRetry);
        Assert.All(atRetry!, marker => Assert.Equal("content,sticky,tier", marker));   // the non-fast-forward kept the marker
        Assert.All(kept, id =>
        {
            Assert.Equal(RemoteText(id, newer), RemoteText(id));
            Assert.Null(RowOf(_rig.A, id).RepushGroups);
            Assert.Equal(RemoteText(id), RowOf(_rig.A, id).BaseJson);
        });

        // B, which holds the same base, finds nothing to republish
        _rig.B.Push.Now += 60_000;
        var onB = Done(await _rig.RunB());
        Assert.Empty(onB.Apply!.RepushMarked);
        Assert.IsType<PushOutcome.NothingToPush>(onB.Push);
        Assert.All(kept, id => Assert.Null(RowOf(_rig.B, id).RepushGroups));
    }
}
