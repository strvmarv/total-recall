using TotalRecall.Core;
using TotalRecall.Infrastructure.GitSync;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>Milestone 1b-i-4, decisions 1 to 4, 10 to 12: the shape of the cycle, the lock, the identity check, the scan and the recording.</summary>
[Collection("process-environment")]
public sealed class GitSyncCycleTests : IDisposable
{
    private readonly CycleRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>A joined machine whose remote holds a manifest and one memory, cloned.</summary>
    private void Joined() => _rig.Push.Seed();

    // ---- decision 1: the constructor ----

    [GitFact]
    public void TheConstructor_RefusesAnEmptyAddress_ADifferentAddress_AndDifferentPaths()
    {
        Assert.Throws<ArgumentException>(() => _rig.Cycle(_rig.Settings(address: "")));
        Assert.Throws<ArgumentException>(() => _rig.Cycle(_rig.Settings(address: "git@github.com:other/other.git")));

        var other = GitSyncPaths.For(_rig.Pull.DbPath, "git@github.com:other/other.git", caseInsensitive: false);
        Assert.Throws<ArgumentException>(() => new GitSyncCycle(
            _rig.Pull.Client, _rig.Pull.Guard, _rig.Pull.Store, _rig.Push.Embedder, other, _rig.Settings(), () => _rig.Now, () => 1L));

        var noClone = GitSyncPaths.ForDatabase(_rig.Pull.DbPath, caseInsensitive: false);
        Assert.Throws<ArgumentException>(() => new GitSyncCycle(
            _rig.Pull.Client, _rig.Pull.Guard, _rig.Pull.Store, _rig.Push.Embedder, noClone, _rig.Settings(), () => _rig.Now, () => 1L));

        Assert.NotNull(_rig.Cycle()); // the matching one builds
    }

    // ---- decision 2 and 3: not joined, refusals, lock ----

    [GitFact]
    public async Task ANeverJoinedMachine_IsNotJoined_RunsNothing_AndRecordsNothing()
    {
        var outcome = await _rig.Run();

        Assert.IsType<CycleOutcome.NotJoined>(outcome);
        Assert.Equal(["lock", "identity"], _rig.Steps);
        Assert.Null(_rig.LastOk);
        Assert.Null(_rig.LastError);
        Assert.Equal(0, _rig.NetworkCalls());
    }

    [GitFact]
    public async Task EachEarlyRefusal_StampsNothing_RunsNoNetworkCommand_AndIsRecordedAsTheLastError()
    {
        Joined();
        _rig.Push.AddMemory(Tier.Warm, "aaaaaaaa-0000-4000-8000-000000000001");
        var rowsBefore = _rig.Push.RowDump();

        // the data directory moved or was copied
        _rig.Pull.Store.SetMeta(SyncIdentity.MetaKeys.Host, "0123456789abcdef0123456789abcdef");
        await AssertRefused("moved or was copied", rowsBefore);

        // the stored remote is not the configured one
        _rig.Pull.Join();
        _rig.Pull.Store.SetMeta(SyncIdentity.MetaKeys.Remote, "git@github.com:someone/else.git");
        await AssertRefused("differs", rowsBefore);

        // the host cannot be verified
        _rig.Pull.Join();
        _rig.Pull.NewGuard(_rig.Pull.RemoteAddress, new HostIdentity(() => new MachineIdResult(MachineIdKind.Failed, null), _rig.Pull.DbPath, false));
        await AssertRefused("host identity", rowsBefore);
    }

    private async Task AssertRefused(string text, string rowsBefore)
    {
        _rig.Steps.Clear();
        _rig.Pull.Store.DeleteMeta(SyncIdentity.MetaKeys.LastError);

        var outcome = Assert.IsType<CycleOutcome.Refused>(await _rig.Run());

        Assert.Contains(text, outcome.Reason);
        Assert.Equal("identity", outcome.Step);
        Assert.Equal(["lock", "identity", "record"], _rig.Steps);
        Assert.Equal(rowsBefore, _rig.Push.RowDump());
        Assert.Equal(0, _rig.NetworkCalls());
        Assert.Matches(CycleRig.ErrorLine("identity", "Refused"), _rig.LastError!);
        Assert.Contains(text, _rig.LastError);
        Assert.Null(_rig.LastOk);
        using var free = await _rig.HoldLock(); // the lock was released
    }

    [GitFact]
    public async Task AHeldLock_IsBusy_RunsAndWritesNothing_AndTheSameProcessIsExcludedToo()
    {
        Joined();
        using (await _rig.HoldLock())
        {
            Assert.IsType<CycleOutcome.Busy>(await _rig.Run());
            Assert.Equal(["lock"], _rig.Steps);
            Assert.Null(_rig.LastOk);
            Assert.Null(_rig.LastError);
            Assert.Empty(_rig.Pull.Recorder.Calls);
        }

        // two cycles of one process exclude each other through the same lock
        var first = _rig.Cycle();
        var second = _rig.Cycle();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.OnStep = step =>
        {
            if (step != "scan")
                return;
            atScan.TrySetResult();
            gate.Task.Wait();
        };
        var running = Task.Run(() => first.RunAsync());
        try
        {
            await atScan.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.IsType<CycleOutcome.Busy>(await second.RunAsync());
        }
        finally
        {
            gate.TrySetResult(); // never leave the first cycle holding the lock
        }
        Assert.IsType<CycleOutcome.Done>(await running);

        // released: a new cycle runs
        Assert.IsType<CycleOutcome.Done>(await _rig.Run());
    }

    [GitFact]
    public async Task ALockWaitCancelledInTheMiddle_IsCancelled_AndRecordsNothing()
    {
        Joined();
        using var held = await _rig.HoldLock();
        using var cts = new CancellationTokenSource();
        var running = _rig.Cycle(_rig.Settings(lockWait: TimeSpan.FromSeconds(5))).RunAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        Assert.IsType<CycleOutcome.Cancelled>(await running);
        Assert.Null(_rig.LastOk);
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task ACancelledTokenBeforeTheLock_IsCancelled_AndTouchesNothing()
    {
        Joined();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.IsType<CycleOutcome.Cancelled>(await _rig.Run(cts.Token));

        Assert.Empty(_rig.Steps);
        Assert.Null(_rig.LastOk);
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task WhenTheLockCannotBeTaken_TheCycleFailsAtTheLockStep_AndRecordsItWithoutTheLock()
    {
        Joined();
        var saved = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", "1");
            var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

            Assert.Equal("lock", failed.Step);
            Assert.Equal("Unexpected", failed.Kind);
            Assert.Equal(SyncPush.Published.No, failed.Published);
            Assert.Equal(["lock", "record"], _rig.Steps);
            Assert.Matches(CycleRig.ErrorLine("lock", "Unexpected"), _rig.LastError!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING", saved);
        }
    }

    // ---- the scan ----

    [GitFact]
    public async Task TheScansOwnNotJoined_ARemoteAndRepoIdWithNoMachineId_IsNotJoined_AndRecordsNothing()
    {
        Joined();
        _rig.Pull.Store.DeleteMeta(SyncIdentity.MetaKeys.Machine);

        Assert.IsType<CycleOutcome.NotJoined>(await _rig.Run());

        Assert.Equal(["lock", "identity", "locks", "scan"], _rig.Steps);
        Assert.Null(_rig.LastOk);
        Assert.Null(_rig.LastError);
    }

    [GitFact]
    public async Task AScanFailure_IsFailedAtTheScan_WithTheStoreKind_AndRecorded()
    {
        Joined();
        using (var drop = _rig.Pull.Connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE git_sync_entry";
            drop.ExecuteNonQuery();
        }

        var failed = Assert.IsType<CycleOutcome.Failed>(await _rig.Run());

        Assert.Equal("scan", failed.Step);
        Assert.Equal("Store", failed.Kind);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Matches(CycleRig.ErrorLine("scan", "Store"), _rig.LastError!);
        Assert.Null(_rig.LastOk);
    }

    [GitFact]
    public async Task AStaleIndexLock_IsGoneBeforeTheScanStarts()
    {
        Joined();
        var stale = _rig.PlantIndexLock();
        var cycle = _rig.Cycle();
        bool? goneAtScan = null;
        var steps = _rig.Steps;
        cycle.OnStep = step =>
        {
            steps.Add(step);
            if (step == "scan")
                goneAtScan = !File.Exists(stale);
        };

        Assert.IsType<CycleOutcome.Done>(await cycle.RunAsync());

        Assert.True(goneAtScan); // the order against the commands that write is tested with the push
    }

    // ---- decision 10: recording ----

    [GitFact]
    public async Task ADone_DeletesTheLastError_ThenSetsTheLastOk()
    {
        Joined();
        _rig.Pull.Store.SetMeta(SyncIdentity.MetaKeys.LastError, "an old error");
        var writes = new List<(string Key, string? Value)>();
        var cycle = _rig.Cycle();
        cycle.BeforeMetaWrite = (key, value) => writes.Add((key, value));

        Assert.IsType<CycleOutcome.Done>(await cycle.RunAsync());

        Assert.Equal(2, writes.Count);
        Assert.Equal((SyncIdentity.MetaKeys.LastError, (string?)null), writes[0]);
        Assert.Equal(SyncIdentity.MetaKeys.LastOk, writes[1].Key);
        Assert.Equal(SyncCycle.okValue(_rig.Now), writes[1].Value);
        Assert.Null(_rig.LastError);
        Assert.Equal("2026-11-19T01:02:03.456Z", _rig.LastOk);
        Assert.Equal(["lock", "identity", "locks", "scan", "fetch", "apply", "push", "record"], _rig.Steps);
    }

    [GitFact]
    public async Task AFailureToWrite_IsSwallowed_AndTheOutcomeIsUnchanged()
    {
        Joined();
        _rig.Pull.Store.SetMeta(SyncIdentity.MetaKeys.LastError, "an old error");
        var cycle = _rig.Cycle();
        cycle.BeforeMetaWrite = (key, _) =>
        {
            if (key == SyncIdentity.MetaKeys.LastOk)
                throw new InvalidOperationException("the store is read-only");
        };

        Assert.IsType<CycleOutcome.Done>(await cycle.RunAsync());

        Assert.Null(_rig.LastOk);    // the second write failed and was swallowed
        Assert.Null(_rig.LastError); // the first one had already been made
    }

    [GitFact]
    public async Task AnExceptionFromAStep_IsAFailureOfThatStep_TheLockIsReleased_AndItIsRecorded()
    {
        Joined();
        var cycle = _rig.Cycle();
        cycle.OnStep = step =>
        {
            _rig.Steps.Add(step);
            if (step == "scan")
                throw new InvalidOperationException("synthetic failure");
        };

        var failed = Assert.IsType<CycleOutcome.Failed>(await cycle.RunAsync());

        Assert.Equal("scan", failed.Step);
        Assert.Equal("Unexpected", failed.Kind);
        Assert.Equal("synthetic failure", failed.Message);
        Assert.Equal(SyncPush.Published.No, failed.Published);
        Assert.Matches(CycleRig.ErrorLine("scan", "Unexpected"), _rig.LastError!);
        using var free = await _rig.HoldLock();
    }

    [GitFact]
    public async Task ARecordedFailure_IsClearedByALaterDone()
    {
        Joined();
        _rig.Pull.Store.DeleteMeta(SyncIdentity.MetaKeys.Machine);
        _rig.Pull.Store.SetMeta(SyncIdentity.MetaKeys.Host, "0123456789abcdef0123456789abcdef");
        Assert.IsType<CycleOutcome.Refused>(await _rig.Run());
        Assert.NotNull(_rig.LastError);

        _rig.Pull.Join();
        Assert.IsType<CycleOutcome.Done>(await _rig.Run());

        Assert.Null(_rig.LastError);
        Assert.NotNull(_rig.LastOk);
    }
}
