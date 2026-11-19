using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-i-4, decisions 8 and 9: a missing or damaged clone is replaced, never deleted, once per process.</summary>
public sealed class GitSyncCycleCloneTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";

    private readonly CycleRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private static GitResult NoCommit() => new(1, [], "", null, TimeSpan.Zero);

    private string CloneDirectory => _rig.Pull.Paths.CloneDirectory;

    private bool StoreHas(string id) =>
        _rig.Pull.Store.Get(Tier.Warm, ContentType.Memory, id) is not null
        || _rig.Pull.Store.Get(Tier.Hot, ContentType.Memory, id) is not null
        || _rig.Pull.Store.Get(Tier.Cold, ContentType.Memory, id) is not null;

    // ---- a missing clone ----

    [GitFact]
    public async Task ALostClone_IsClonedAgain_TheApplyIsUnproven_AndNothingIsDeleted()
    {
        _rig.Push.Seed(Rec(A, "Use bun."), Rec(B, "Use pnpm."));
        Done(await _rig.Run());
        _rig.Steps.Clear();

        // the remote head equals L: the shallow clone holds L but has no protective ref
        _rig.DeleteClone();
        var summary = Done(await _rig.Run());

        Assert.True(summary.Cloned);
        Assert.Null(summary.SidelinedClone);
        Assert.Contains("clone", _rig.Steps);
        Assert.False(summary.Fetch!.Proven);
        Assert.True(StoreHas(A) && StoreHas(B));
        Assert.Empty(_rig.Sidelined("broken"));

        // the remote moved on without a record that this machine holds with a base: nothing is deleted
        _rig.RemoveRecordOnRemote(B);
        _rig.DeleteClone();
        var moved = Done(await _rig.Run());

        Assert.True(moved.Cloned);
        Assert.False(moved.Fetch!.Proven);
        Assert.True(StoreHas(A) && StoreHas(B));
    }

    [GitFact]
    public async Task APlainFailedClone_IsFailedWithItsKind_NothingIsCounted_AndTheNextCycleSucceeds()
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        _rig.DeleteClone();
        _rig.FaultOnce(c => c.IsClone, FaultingGitProcess.Failure(SyncGit.FailureKind.Network, "could not resolve host"));

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("clone", failed.Step);
        Assert.Equal("Network", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Matches(CycleRig.ErrorLine("clone", "Network"), _rig.LastError!);
        Assert.False(Directory.Exists(CloneDirectory));

        Assert.True(Done(await _rig.Run()).Cloned); // not CloneUnusable: it is retried
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task ARemoteWithoutTheBranch_AtClone_IsRefused()
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        _rig.DeleteClone();
        _rig.FaultOnce(c => c.IsClone, FaultingGitProcess.Failure(SyncGit.FailureKind.RemoteBranchMissing, "Remote branch main not found in upstream origin"));

        var refused = Assert.IsType<CycleOutcome.Refused>(await _rig.Run());

        Assert.Equal("clone", refused.Step);
        Assert.Contains("no branch", refused.Reason);
        Assert.Matches(CycleRig.ErrorLine("clone", "Refused"), _rig.LastError!);
    }

    // ---- a clone that did not finish ----

    [GitTheory]
    [InlineData("TimedOut")]
    [InlineData("Cancelled")]
    public async Task AClone_KilledByATimeoutOrACancel_LeavesNothingAndIsNeverCounted(string how)
    {
        _rig.Push.Seed(Rec(A));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());

        using var cts = new CancellationTokenSource();
        for (var round = 0; round < 2; round++) // two in a row never give CloneUnusable
        {
            _rig.DeleteClone();
            _rig.FaultOnce(
                c => c.IsClone,
                FaultingGitProcess.Failure(how == "TimedOut" ? SyncGit.FailureKind.TimedOut : SyncGit.FailureKind.Cancelled, "killed"),
                before: () =>
                {
                    _rig.LeavePartialClone();
                    if (how == "Cancelled")
                        cts.Cancel();
                });

            var outcome = await cycle.RunAsync(cts.Token);

            if (how == "TimedOut")
                Assert.Equal("TimedOut", Assert.IsType<CycleOutcome.Failed>(outcome).Kind);
            else
                Assert.IsType<CycleOutcome.Cancelled>(outcome);
            Assert.False(Directory.Exists(CloneDirectory), "the partial clone was deleted");
            Assert.Empty(_rig.Sidelined("partial"));
            Assert.Empty(_rig.Sidelined("broken"));
            if (how == "Cancelled")
                cts.TryReset();
        }

        _rig.Pull.Fault = _ => null;
        Assert.True(Done(await cycle.RunAsync()).Cloned);
    }

    [GitFact]
    public async Task APartialClone_ThatCannotBeDeleted_IsMovedAsideAsPartial_AndNeverCounted()
    {
        _rig.Push.Seed(Rec(A));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());
        cycle.DeleteDirectory = _ => throw new IOException("locked");
        _rig.DeleteClone();
        _rig.FaultOnce(c => c.IsClone, FaultingGitProcess.Failure(SyncGit.FailureKind.TimedOut, "killed"), before: _rig.LeavePartialClone);

        Assert.Equal("TimedOut", Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync()).Kind);

        var partial = Assert.Single(_rig.Sidelined("partial"));
        Assert.True(File.Exists(Path.Combine(partial, ".git", "objects", "pack", "tmp_pack_partial")));
        Assert.False(Directory.Exists(CloneDirectory));
        cycle.DeleteDirectory = null;
        Assert.True(Done(await cycle.RunAsync()).Cloned);
        Assert.Empty(_rig.Sidelined("broken"));
    }

    [GitFact]
    public async Task WhenNeitherTheDeleteNorTheMoveWorks_TheOriginalOutcomeStands()
    {
        _rig.Push.Seed(Rec(A));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());
        cycle.DeleteDirectory = _ => throw new IOException("locked");
        cycle.MoveDirectory = (_, _) => throw new IOException("locked too");
        _rig.DeleteClone();
        _rig.FaultOnce(c => c.IsClone, FaultingGitProcess.Failure(SyncGit.FailureKind.Network, "down"), before: _rig.LeavePartialClone);

        var failed = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());

        Assert.Equal("Network", failed.Kind);
        Assert.Equal("clone", failed.Step);
    }

    // ---- a damaged clone ----

    [GitFact]
    public async Task ACorruptClone_IsRenamed_KeptWithItsContent_AndClonedAgain()
    {
        _rig.Push.Seed(Rec(A), Rec(B));
        Done(await _rig.Run());

        // what the fetcher says about a clone git cannot open: the damage rule must cover it
        _rig.CorruptClone();
        var seen = Assert.IsType<FetchOutcome.Failed>(await _rig.Pull.Fetcher().FetchAsync());
        Assert.Contains(seen.Kind, new[] { "RemoteNotFound", "NotARepository", "Other" });

        var summary = Done(await _rig.Run());

        Assert.True(summary.Cloned);
        var moved = Assert.Single(_rig.Sidelined("broken"));
        Assert.Equal(moved, summary.SidelinedClone);
        Assert.StartsWith("garbage", File.ReadAllText(Path.Combine(moved, ".git", "HEAD"))); // kept with its content
        Assert.True(_rig.Pull.Client.CloneIsSound());
        Assert.True(StoreHas(A) && StoreHas(B)); // nothing was deleted from the store
        Assert.Contains("clone", _rig.Steps);
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task ANetworkFailure_WithASoundClone_NeverMovesTheClone()
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        var head = _rig.Pull.CloneRef("HEAD");
        _rig.Pull.Fault = c => c.IsLsRemote ? FaultingGitProcess.Failure(SyncGit.FailureKind.Network, "could not resolve host") : null;

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("fetch", failed.Step);
        Assert.Equal("Network", failed.Kind);
        Assert.Empty(_rig.Sidelined("broken"));
        Assert.Equal(head, _rig.Pull.CloneRef("HEAD"));
    }

    [GitTheory]
    [InlineData("Healthy", false)]
    [InlineData("NoCommit", true)]
    [InlineData("TimedOut", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Other", true)]
    public async Task TheProbe_DecidesWhetherAnOtherFailureIsDamage(string probe, bool damage)
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        var lsRemoteFaulted = false;
        var probed = false;
        _rig.Pull.Fault = c =>
        {
            if (c.IsLsRemote && !lsRemoteFaulted)
            {
                lsRemoteFaulted = true;
                return FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "remote: internal server error");
            }
            if (c.IsRevParse && lsRemoteFaulted && !probed)
            {
                probed = true; // only the probe is faulted: the restart after a recovery runs for real
                return probe switch
                {
                    "NoCommit" => NoCommit(),
                    "TimedOut" => FaultingGitProcess.Failure(SyncGit.FailureKind.TimedOut, "slow"),
                    "Cancelled" => FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "cancelled"),
                    "Other" => FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "fatal"),
                    _ => null, // the real probe: the clone is fine
                };
            }
            return null;
        };

        var outcome = await _rig.Run();

        if (damage)
        {
            Assert.Single(_rig.Sidelined("broken"));
            Assert.True(Done(outcome).Cloned);
        }
        else
        {
            Assert.Empty(_rig.Sidelined("broken"));
            Assert.Equal("Other", Assert.IsType<CycleOutcome.Failed>(outcome).Kind);
        }
    }

    [GitFact]
    public async Task ASecondDamagedClone_IsCloneUnusable_AndEveryLaterRunFailsAtOnceTouchingNothing()
    {
        _rig.Push.Seed(Rec(A));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());

        _rig.CorruptClone();
        Assert.NotNull(Done(await cycle.RunAsync()).SidelinedClone);

        _rig.CorruptClone();
        var unusable = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());
        Assert.Equal("clone", unusable.Step);
        Assert.Equal("CloneUnusable", unusable.Kind);
        Assert.Matches(CycleRig.ErrorLine("clone", "CloneUnusable"), _rig.LastError!);
        Assert.Single(_rig.Sidelined("broken")); // not a second one

        // from now on: the same failure at once, no lock, no command, no write
        var errorBefore = _rig.LastError;
        var commands = _rig.Pull.Recorder.Calls.Count;
        _rig.Steps.Clear();
        var writes = 0;
        cycle.BeforeMetaWrite = (_, _) => writes++;
        using (await _rig.HoldLock())
        {
            var again = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());
            Assert.Equal(unusable, again);
        }
        Assert.Empty(_rig.Steps);
        Assert.Equal(commands, _rig.Pull.Recorder.Calls.Count);
        Assert.Equal(0, writes);
        Assert.Equal(errorBefore, _rig.LastError);

        // a new instance starts with a clean count
        Done(await _rig.Run());
        Assert.Equal(2, _rig.Sidelined("broken").Length);
    }

    [GitFact]
    public async Task ALostClone_FollowedByADamagedOne_CountsTheDamageAsTheFirst()
    {
        _rig.Push.Seed(Rec(A));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());

        _rig.DeleteClone();
        Assert.True(Done(await cycle.RunAsync()).Cloned);

        _rig.CorruptClone();
        Assert.NotNull(Done(await cycle.RunAsync()).SidelinedClone); // the first damage, not CloneUnusable

        _rig.CorruptClone();
        Assert.Equal("CloneUnusable", Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync()).Kind);
    }

    [GitFact]
    public async Task AFailedMove_IsFailedUnexpected_NothingIsCounted_AndTheNextCycleTriesAgain()
    {
        _rig.Push.Seed(Rec(A));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());
        _rig.CorruptClone();
        cycle.MoveDirectory = (_, _) => throw new IOException("the directory is locked");

        var failed = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());

        Assert.Equal("clone", failed.Step);
        Assert.Equal("Unexpected", failed.Kind);
        Assert.Empty(_rig.Sidelined("broken"));

        cycle.MoveDirectory = null;
        Assert.NotNull(Done(await cycle.RunAsync()).SidelinedClone);

        _rig.CorruptClone(); // only now is the first damage counted: the second one is unusable
        Assert.Equal("CloneUnusable", Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync()).Kind);
    }
}
