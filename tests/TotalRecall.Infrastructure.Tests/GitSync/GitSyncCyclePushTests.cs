using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using static TotalRecall.Infrastructure.Tests.GitSync.ApplyRig;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-i-4, decisions 6 and 11: the push, the retry and the cancellation.</summary>
public sealed class GitSyncCyclePushTests : IDisposable
{
    private const string A = "a1b2c3d4";
    private const string B = "b2c3d4e5";
    private const string C = "c3d4e5f6";
    private const string D = "d4e5f6a7";

    private readonly CycleRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static CycleSummary Done(CycleOutcome outcome) => Assert.IsType<CycleOutcome.Done>(outcome).Summary;

    private bool RemoteHas(string id)
    {
        try
        {
            _rig.Push.RemoteFile(SyncValidation.recordPath(id));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int RemoteCommits() => int.Parse(_rig.Push.Remote.Git("rev-list", "--count", "main").Trim());

    private bool StoreHas(string id) =>
        _rig.Pull.Store.Get(Tier.Warm, ContentType.Memory, id) is not null
        || _rig.Pull.Store.Get(Tier.Hot, ContentType.Memory, id) is not null
        || _rig.Pull.Store.Get(Tier.Cold, ContentType.Memory, id) is not null;

    private void AnotherMachineAdds(string id, string content)
    {
        _rig.Pull.PutRecord(Rec(id, content));
        _rig.Pull.Commit("another machine adds " + id);
        _rig.Pull.Source.PushTo(_rig.Push.Remote.Path);
    }

    /// <summary>The state of a machine that synced once and now has a pending change.</summary>
    private async Task SyncedWithAPendingMemory()
    {
        _rig.Push.Seed(Rec(A, "Use bun."));
        Done(await _rig.Run());
        _rig.Push.Now += 60_000;
        _rig.Push.AddMemory(Tier.Warm, C, "A new local memory.");
    }

    // ---- AC 1: the whole cycle ----

    [GitFact]
    public async Task OneCycle_PushesANewAndAnEditedMemory_AndAppliesAnotherMachinesChange()
    {
        _rig.Push.Seed(Rec(A, "Use bun."));
        Done(await _rig.Run());
        _rig.Push.Now += 60_000;
        _rig.Push.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "Use bun, always." });
        _rig.Push.AddMemory(Tier.Warm, C, "A new local memory.");
        AnotherMachineAdds(B, "Use pnpm.");
        var commitsBefore = RemoteCommits();
        _rig.Steps.Clear();

        var summary = Done(await _rig.Run());

        Assert.Equal(["lock", "identity", "locks", "scan", "fetch", "apply", "push", "record"], _rig.Steps);
        Assert.Equal(1, summary.Attempts);
        Assert.Equal(1, summary.Apply!.Inserted);
        Assert.True(StoreHas(B));
        var pushed = Assert.IsType<PushOutcome.Pushed>(summary.Push);
        Assert.Equal(1, pushed.Summary.New);
        Assert.Equal(1, pushed.Summary.Changed);
        Assert.True(RemoteHas(C));
        Assert.Equal(commitsBefore + 1, RemoteCommits());
        Assert.StartsWith("sync: testbox +1 ~1 -0", _rig.Push.Remote.Git("log", "-1", "--format=%s", "main").Trim());
        Assert.Equal("2026-11-19T01:02:03.456Z", _rig.LastOk);
        Assert.Null(_rig.LastError);

        // nothing is pending any more: the next cycle sends nothing
        var pushes = _rig.PushCalls();
        Assert.IsType<PushOutcome.NothingToPush>(Done(await _rig.Run()).Push);
        Assert.Equal(pushes, _rig.PushCalls());
        Assert.Equal(commitsBefore + 1, RemoteCommits());
    }

    [GitFact]
    public async Task ACycleWithNothingToDo_MakesNoCommitAndNoPush()
    {
        _rig.Push.Seed(Rec(A));
        Done(await _rig.Run());
        var commits = RemoteCommits();
        var pushes = _rig.PushCalls();

        var summary = Done(await _rig.Run());

        Assert.IsType<PushOutcome.NothingToPush>(summary.Push);
        Assert.Equal(pushes, _rig.PushCalls());
        Assert.Equal(0, _rig.Pull.Count(c => c.IsCommit));
        Assert.Equal(commits, RemoteCommits());
    }

    [GitFact]
    public async Task AStaleIndexLock_IsRemovedBeforeTheFetchAndBeforeAnyCommandThatWrites()
    {
        await SyncedWithAPendingMemory();
        var stale = _rig.PlantIndexLock();
        var lockSeen = false;
        _rig.Pull.Fault = c =>
        {
            if (c.IsFetch || c.IsResetHard || c.IsClean || c.IsStagePaths || c.IsCommit || c.IsPush)
                lockSeen |= File.Exists(stale);
            return null;
        };

        Assert.IsType<PushOutcome.Pushed>(Done(await _rig.Run()).Push);

        Assert.False(lockSeen);
        Assert.True(RemoteHas(C));
    }

    // ---- AC 8: the race for the branch ----

    [GitFact]
    public async Task ANonFastForward_RunsTheCycleAgain_AndTheSecondPushSucceeds()
    {
        await SyncedWithAPendingMemory();
        var raced = 0;
        _rig.Pull.Fault = c =>
        {
            if (c.IsPush && raced++ == 0)
                _rig.Push.MoveRemote("other-1.txt", "another machine, first");
            return null;
        };
        _rig.Steps.Clear();

        var summary = Done(await _rig.Run());

        Assert.Equal(2, summary.Attempts);
        Assert.IsType<PushOutcome.Pushed>(summary.Push);
        Assert.Equal(2, _rig.PushCalls());
        Assert.True(RemoteHas(C));
        Assert.Equal("another machine, first", _rig.Push.RemoteFile("other-1.txt"));
        Assert.Equal(2, _rig.Steps.Count(s => s == "scan"));
        Assert.NotNull(_rig.LastOk);
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task ThreeRejectionsInARow_FailTheCycle_WithEverythingPending_AndTheNextCycleSucceeds()
    {
        await SyncedWithAPendingMemory();
        var raced = 0;
        _rig.Pull.Fault = c =>
        {
            if (c.IsPush)
                _rig.Push.MoveRemote($"other-{++raced}.txt", $"another machine, push {raced}");
            return null;
        };

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("push", failed.Step);
        Assert.Equal("NonFastForward", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Equal(3, _rig.PushCalls());
        Assert.False(RemoteHas(C));
        Assert.Matches(CycleRig.ErrorLine("push", "NonFastForward"), _rig.LastError!);
        using (await _rig.HoldLock()) { } // released

        _rig.Pull.Fault = _ => null;
        Assert.IsType<PushOutcome.Pushed>(Done(await _rig.Run()).Push);
        Assert.True(RemoteHas(C));
        Assert.Null(_rig.LastError);
    }

    // ---- AC 9: failures ----

    [GitTheory]
    [InlineData("Auth", "No")]
    [InlineData("Network", "Unknown")]
    [InlineData("TimedOut", "Unknown")]
    [InlineData("Other", "Unknown")]
    public async Task AFailedPush_NamesTheStep_TheKindAndWhetherItMayHaveReachedTheRemote(string kind, string published)
    {
        await SyncedWithAPendingMemory();
        var okBefore = _rig.LastOk;
        _rig.Pull.Fault = c => c.IsPush
            ? FaultingGitProcess.Failure(Enum.Parse<SyncKindForTests>(kind).ToCore(), "the host said no")
            : null;

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("push", failed.Step);
        Assert.Equal(kind, failed.Kind);
        Assert.Equal(published, failed.Published.ToString());
        Assert.Matches(CycleRig.ErrorLine("push", kind), _rig.LastError!);
        Assert.Equal(okBefore, _rig.LastOk); // a failure leaves last_ok alone

        // the next cycle recovers and clears the error
        _rig.Pull.Fault = _ => null;
        Done(await _rig.Run());
        Assert.Null(_rig.LastError);
        Assert.True(RemoteHas(C));
    }

    [GitFact]
    public async Task APushTheHostRejected_WithAHookDeclined_IsNotPublished()
    {
        await SyncedWithAPendingMemory();
        _rig.Pull.Fault = c => c.IsPush
            ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, " ! [remote rejected] HEAD -> main (pre-receive hook declined)")
            : null;

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("Other", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
    }

    [GitFact]
    public async Task ThePushIsNeverReached_AfterARefusedOrAnIncompletePull()
    {
        await SyncedWithAPendingMemory();
        // a local memory and a record of the same id with other text and no base: a conflict (NC4), incomplete
        _rig.Push.AddMemory(Tier.Warm, D, "My own text.");
        _rig.AddRecordOnRemote(Rec(D, "Their text."));
        var commits = RemoteCommits();

        var incomplete = Done(await _rig.Run());

        Assert.Equal("pull-incomplete", incomplete.PushSkipped);
        Assert.Null(incomplete.Push);
        Assert.Equal(0, _rig.PushCalls());
        Assert.False(RemoteHas(C));
        Assert.NotNull(_rig.LastOk);
        Assert.Equal("My own text.", _rig.Pull.Store.Get(Tier.Warm, ContentType.Memory, D)!.Content);

        _rig.ReplaceManifestOnRemote(PullRig.ManifestBytes(repoId: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        Assert.IsType<CycleOutcome.Refused>(await _rig.Run());
        Assert.Equal(0, _rig.PushCalls());
        Assert.True(RemoteCommits() > commits); // only the other machine's commits
    }

    [GitFact]
    public async Task NotReady_IsAFailureOfThePush()
    {
        await SyncedWithAPendingMemory();
        var cycle = _rig.Cycle();
        cycle.OnStep = step =>
        {
            _rig.Steps.Add(step);
            if (step == "push")
                _rig.Pull.Store.SetMeta(SyncIdentity.MetaKeys.LastSha, new string('1', 40));
        };

        var failed = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());

        Assert.Equal("push", failed.Step);
        Assert.Equal("NotReady", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
    }

    [GitFact]
    public async Task APushResetThatFindsTheCloneDamaged_ReplacesIt_AndTheNextAttemptPushes()
    {
        await SyncedWithAPendingMemory();
        var pushing = false;
        var resetFaulted = false;
        var probed = false;
        var cycle = _rig.Cycle();
        cycle.OnStep = step =>
        {
            _rig.Steps.Add(step);
            pushing = step == "push";
        };
        _rig.Pull.Fault = c =>
        {
            if (pushing && c.IsResetHard && !resetFaulted)
            {
                resetFaulted = true;
                return FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "fatal: unable to read tree");
            }
            if (resetFaulted && !probed && c.IsRevParse)
            {
                probed = true;
                return new GitResult(1, [], "", null, TimeSpan.Zero); // no commit: the clone is damaged
            }
            return null;
        };

        var summary = Done(await cycle.RunAsync());

        Assert.NotNull(summary.SidelinedClone);
        Assert.Single(_rig.Sidelined("broken"));
        Assert.Equal(1, summary.Attempts); // the recovery was not an attempt
        Assert.IsType<PushOutcome.Pushed>(summary.Push);
        Assert.True(RemoteHas(C));
    }

    [GitFact]
    public async Task ARejectionFollowedByAFailingFetch_IsFailedAtTheFetch_WithNothingPublished()
    {
        await SyncedWithAPendingMemory();
        var raced = false;
        _rig.Pull.Fault = c =>
        {
            if (c.IsPush)
            {
                raced = true;
                _rig.Push.MoveRemote("other-1.txt", "another machine");
            }
            return c.IsLsRemote && raced ? FaultingGitProcess.Failure(SyncGit.FailureKind.Network, "could not resolve host") : null;
        };

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("fetch", failed.Step);
        Assert.Equal("Network", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Equal(1, _rig.Pull.Count(c => c.IsPush));
        Assert.False(RemoteHas(C));

        _rig.Pull.Fault = _ => null;
        Assert.IsType<PushOutcome.Pushed>(Done(await _rig.Run()).Push);
        Assert.True(RemoteHas(C));
    }

    [GitFact]
    public async Task ADamagedCloneAtThePush_AfterTheSidelineWasUsed_IsCloneUnusable()
    {
        _rig.Push.Seed(Rec(A, "Use bun."));
        var cycle = _rig.Cycle();
        Done(await cycle.RunAsync());
        _rig.CorruptClone();
        Assert.NotNull(Done(await cycle.RunAsync()).SidelinedClone); // the one sideline of this process

        _rig.Push.Now += 60_000;
        _rig.Push.AddMemory(Tier.Warm, C, "A new local memory.");
        var pushing = false;
        var resetFaulted = false;
        var probed = false;
        cycle.OnStep = step => pushing = step == "push";
        _rig.Pull.Fault = c =>
        {
            if (pushing && c.IsResetHard && !resetFaulted)
            {
                resetFaulted = true;
                return FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "fatal: unable to read tree");
            }
            if (resetFaulted && !probed && c.IsRevParse)
            {
                probed = true;
                return new GitResult(1, [], "", null, TimeSpan.Zero);
            }
            return null;
        };

        var failed = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());

        Assert.Equal("clone", failed.Step);
        Assert.Equal("CloneUnusable", failed.Kind);
        Assert.Single(_rig.Sidelined("broken")); // no second sideline
        Assert.False(RemoteHas(C));
    }

    [GitFact]
    public async Task AnExceptionAtThePush_IsFailedWithPublishedUnknown_AndTheLockIsReleased()
    {
        await SyncedWithAPendingMemory();
        var cycle = _rig.Cycle();
        cycle.OnStep = step =>
        {
            _rig.Steps.Add(step);
            if (step == "push")
                throw new InvalidOperationException("synthetic failure");
        };

        var failed = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());

        Assert.Equal("push", failed.Step);
        Assert.Equal("Unexpected", failed.Kind);
        Assert.Equal(SyncPush.Published.Unknown, failed.Published);
        Assert.Matches(CycleRig.ErrorLine("push", "Unexpected"), _rig.LastError!);
        using var free = await _rig.HoldLock();
    }

    // ---- AC 10 and AC 11, end to end ----

    [GitTheory]
    [InlineData("lost", false)]
    [InlineData("lost", true)]
    [InlineData("corrupt", false)]
    public async Task ADamagedOrLostClone_IsReplaced_AndThePendingChangesArePushedInTheSameCycle(string how, bool remoteMovedOn)
    {
        _rig.Push.Seed(Rec(A, "Use bun."), Rec(B, "Use pnpm."));
        Done(await _rig.Run());
        _rig.Push.Now += 60_000;
        _rig.Push.Edit(Tier.Warm, A, new UpdateEntryOpts { Content = "Use bun, always." });
        _rig.Push.AddMemory(Tier.Warm, C, "A new local memory.");
        if (remoteMovedOn)
            AnotherMachineAdds(D, "From another machine.");
        if (how == "lost") _rig.DeleteClone(); else _rig.CorruptClone();

        var summary = Done(await _rig.Run());

        Assert.True(summary.Cloned);
        Assert.Equal(how == "corrupt", summary.SidelinedClone is not null);
        Assert.IsType<PushOutcome.Pushed>(summary.Push);
        Assert.True(RemoteHas(C));
        Assert.Contains("always", _rig.Push.RemoteFile(SyncValidation.recordPath(A)));
        Assert.True(StoreHas(A) && StoreHas(B) && StoreHas(C)); // nothing deleted from the store
        Assert.Equal(remoteMovedOn, StoreHas(D));
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task AFailureAfterThePushWasAccepted_IsReportedAsThatFailure_NotAsACancel()
    {
        await SyncedWithAPendingMemory();
        using var cts = new CancellationTokenSource();
        _rig.Pull.After = (command, result) =>
        {
            if (command.IsPush)
                cts.Cancel(); // the push is on the remote; the token is cancelled while the rows are finalized
            return result;
        };
        _rig.Pull.Fault = c => c.IsUpdateLastSynced ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "ref update failed") : null;

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run(cts.Token));

        Assert.Equal("push", failed.Step);
        Assert.Equal(SyncPush.Published.Yes, failed.Published);
        Assert.True(RemoteHas(C));
        Assert.Matches(CycleRig.ErrorLine("push", failed.Kind), _rig.LastError!);
    }

    [GitFact]
    public async Task EntryLevelApplyFailures_DoNotStopThePushOfTheOtherEntries()
    {
        _rig.Push.Seed(Rec(A, "Use bun."), Rec(B, "Use pnpm."));
        _rig.Push.Embedder.Fails = text => text.Contains("bun");
        Done(await _rig.Run());
        _rig.Push.Now += 60_000;
        _rig.Push.AddMemory(Tier.Warm, C, "A new local memory.");

        var summary = Done(await _rig.Run());

        Assert.NotEmpty(summary.Apply!.Failed);
        Assert.IsType<PushOutcome.Pushed>(summary.Push);
        Assert.True(RemoteHas(C));
    }

    // ---- AC 13: cancellation ----

    [GitTheory]
    [InlineData("lock")]
    [InlineData("identity")]
    [InlineData("locks")]
    [InlineData("scan")]
    [InlineData("clone")]
    [InlineData("fetch")]
    [InlineData("apply")]
    [InlineData("push")]
    public async Task ACancelBeforeAStep_IsCancelled_RecordsNothing_ReleasesTheLock_AndTheNextCycleSucceeds(string step)
    {
        await SyncedWithAPendingMemory();
        var okBefore = _rig.LastOk;
        if (step == "clone")
            _rig.DeleteClone();
        using var cts = new CancellationTokenSource();
        var cycle = _rig.Cycle();
        cycle.OnStep = name =>
        {
            _rig.Steps.Add(name);
            if (name == step)
                cts.Cancel();
        };

        Assert.IsType<CycleOutcome.Cancelled>(await cycle.RunAsync(cts.Token));

        Assert.Equal(step, _rig.Steps[^1]);
        Assert.Equal(okBefore, _rig.LastOk);
        Assert.Null(_rig.LastError);
        using (await _rig.HoldLock()) { }

        _rig.Push.Now += 60_000;
        Assert.IsType<PushOutcome.Pushed>(Done(await _rig.Run()).Push);
        Assert.True(RemoteHas(C));
    }

    [GitTheory]
    [InlineData("fetch")]
    [InlineData("tree-real")]
    [InlineData("tree-failed")]
    [InlineData("ref-failed")]
    [InlineData("push")]
    public async Task ACancelInTheMiddleOfAPart_IsCancelled_AndLeavesTheStoreAndTheCloneConsistent(string where)
    {
        await SyncedWithAPendingMemory();
        AnotherMachineAdds(B, "Use pnpm.");
        using var cts = new CancellationTokenSource();
        _rig.Pull.Fault = c =>
        {
            switch (where)
            {
                case "fetch" when c.IsFetch:
                    cts.Cancel();
                    return FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "cancelled");
                case "tree-real" when c.IsLsTree:
                    cts.Cancel();
                    return null; // the apply runs on with a cancelled token
                case "tree-failed" when c.IsLsTree:
                    cts.Cancel();
                    return FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "tree unreadable");
                case "ref-failed" when c.IsUpdateLastSynced:
                    cts.Cancel();
                    return FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "ref update failed");
                case "push" when c.IsPush:
                    cts.Cancel();
                    return FaultingGitProcess.Failure(SyncGit.FailureKind.Cancelled, "cancelled");
                default:
                    return null;
            }
        };
        var okBefore = _rig.LastOk;

        Assert.IsType<CycleOutcome.Cancelled>(await _rig.Run(cts.Token));

        Assert.Equal(okBefore, _rig.LastOk);
        Assert.Null(_rig.LastError);
        using (await _rig.HoldLock()) { }

        // a clean cycle recovers whatever was left half way, and the pending memory reaches the remote
        _rig.Pull.Fault = _ => null;
        _rig.Push.Now += 60_000;
        Done(await _rig.Run());
        Assert.True(StoreHas(B));
        Assert.True(RemoteHas(C));
        Assert.Equal("", _rig.Push.Status().Trim());
    }

    [GitFact]
    public async Task ACancelAfterThePushWasAccepted_StillFinishesTheBookkeeping_AndIsDone()
    {
        await SyncedWithAPendingMemory();
        using var cts = new CancellationTokenSource();
        _rig.Pull.After = (command, result) =>
        {
            if (command.IsPush)
                cts.Cancel();
            return result;
        };

        var summary = Done(await _rig.Run(cts.Token));

        Assert.IsType<PushOutcome.Pushed>(summary.Push);
        Assert.True(RemoteHas(C));
        Assert.NotNull(_rig.LastOk);
        Assert.Null(_rig.LastError);
        Assert.Equal(_rig.Push.Remote_Head(), _rig.Pull.Last);
    }
}

/// <summary>The failure kinds the theory above passes by name (an F# union value cannot be an attribute argument).</summary>
internal enum SyncKindForTests { Auth, Network, TimedOut, Other }

internal static class SyncKindForTestsExtensions
{
    public static SyncGit.FailureKind ToCore(this SyncKindForTests kind) => kind switch
    {
        SyncKindForTests.Auth => SyncGit.FailureKind.Auth,
        SyncKindForTests.Network => SyncGit.FailureKind.Network,
        SyncKindForTests.TimedOut => SyncGit.FailureKind.TimedOut,
        _ => SyncGit.FailureKind.Other,
    };
}
