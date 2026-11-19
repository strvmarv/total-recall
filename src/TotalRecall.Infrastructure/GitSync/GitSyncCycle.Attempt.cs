using TotalRecall.Core;
using TotalRecall.Infrastructure.Diagnostics;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncCycle
{
    /// <summary>What one run knows as it goes; the step name is what an unexpected exception is reported under.</summary>
    private sealed class RunState
    {
        public string Step = "identity";
        public int Attempts;
        public ScanSummary Scan = ScanSummary.Empty;
        public FetchResult? Fetch;
        public ApplySummary? Apply;
        public PushOutcome? Push;
        public string? PushSkipped;
        public bool Cloned;
        public string? SidelinedClone;

        public CycleSummary ToSummary() => new(Attempts, Scan, Fetch, Apply, Push, PushSkipped, Cloned, SidelinedClone);
    }

    private static CycleOutcome Cancelled() => new CycleOutcome.Cancelled();

    private void Enter(RunState state, SyncCycle.Step step)
    {
        state.Step = SyncCycle.stepName(step);
        OnStep?.Invoke(state.Step);
    }

    private static CycleOutcome.Failed Fail(string step, string kind, string message) =>
        new(step, kind, message, SyncPush.Published.No);

    private static CycleOutcome.Failed Fail(string step, string kind, string message, SyncPush.Published published) =>
        new(step, kind, message, published);

    /// <summary>The state a stored <c>CloneUnusable</c> failure is kept in (decision 9); set by the clone recovery.</summary>
    private CycleOutcome? StoredCloneFailure() => null;

    /// <summary>Everything under the lock: decisions 3 and 4, the identity check, the stale lock files, then the attempts.</summary>
    private async Task<CycleOutcome> RunLockedAsync(CancellationToken ct)
    {
        var state = new RunState();
        try
        {
            Enter(state, SyncCycle.Step.Identity);
            var identity = await _fetcher.CheckIdentityAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                return Cancelled();
            switch (identity)
            {
                case IdentityCheck.NotJoined:
                    return new CycleOutcome.NotJoined();
                case IdentityCheck.Refused refused:
                    return new CycleOutcome.Refused(refused.Text, state.Step);
            }

            Enter(state, SyncCycle.Step.Locks);
            try
            {
                _client.RemoveStaleLocks();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // best effort (decision 2): a lock file that cannot be removed fails the step that needs the clone, not this one
            }

            return await AttemptAsync(state, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ExceptionLogger.LogChain("[total-recall] sync cycle", ex);
            var published = state.Step == SyncCycle.stepName(SyncCycle.Step.Push) ? SyncPush.Published.Unknown : SyncPush.Published.No;
            return Fail(state.Step, "Unexpected", ex.Message, published);
        }
    }

    /// <summary>One attempt, from the scan to the push (decision 4). Filled in by the later tasks; now it ends after the scan.</summary>
    private Task<CycleOutcome> AttemptAsync(RunState state, CancellationToken ct)
    {
        Enter(state, SyncCycle.Step.Scan);
        var scan = new GitSyncScanner(_store.Connection, _nowMs).Scan();
        if (ct.IsCancellationRequested)
            return Task.FromResult(Cancelled());
        switch (scan)
        {
            case ScanOutcome.NotJoined:
                return Task.FromResult<CycleOutcome>(new CycleOutcome.NotJoined());
            case ScanOutcome.Failed failed:
                return Task.FromResult<CycleOutcome>(Fail(state.Step, "Store", failed.Reason));
            case ScanOutcome.Done done:
                state.Scan = done.Summary;
                break;
        }
        return Task.FromResult<CycleOutcome>(new CycleOutcome.Done(state.ToSummary()));
    }
}
