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

            while (true)
            {
                if (ct.IsCancellationRequested)
                    return Cancelled();
                if (await AttemptAsync(state, ct).ConfigureAwait(false) is { } outcome)
                    return outcome;
                // null: a damaged clone was replaced, or the push lost the race; run again from the scan
            }
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

    /// <summary>
    /// One attempt, from the scan to the push (decision 4). Returns the outcome that ends the cycle, or null to run
    /// another attempt from the scan (a clone was recovered). Every part's result is read only after the token was
    /// checked (decision 5).
    /// </summary>
    private async Task<CycleOutcome?> AttemptAsync(RunState state, CancellationToken ct)
    {
        Enter(state, SyncCycle.Step.Scan);
        var scan = new GitSyncScanner(_store.Connection, _nowMs).Scan();
        if (ct.IsCancellationRequested)
            return Cancelled();
        switch (scan)
        {
            case ScanOutcome.NotJoined:
                return new CycleOutcome.NotJoined();
            case ScanOutcome.Failed failed:
                return Fail(state.Step, "Store", failed.Reason);
            case ScanOutcome.Done done:
                state.Scan = done.Summary;
                break;
        }

        if (await EnsureCloneAsync(state, ct).ConfigureAwait(false) is { } cloneStop)
            return cloneStop;
        if (ct.IsCancellationRequested)
            return Cancelled();

        // the fetch
        Enter(state, SyncCycle.Step.Fetch);
        var fetched = await _fetcher.FetchAsync(ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            return Cancelled();
        FetchResult pulled;
        switch (fetched)
        {
            case FetchOutcome.Fetched f:
                pulled = f.Result;
                break;
            case FetchOutcome.NothingNew n:
                pulled = n.Result;
                break;
            case FetchOutcome.Refused refused:
                return new CycleOutcome.Refused(refused.Reason, state.Step);
            case FetchOutcome.Failed failed:
            {
                var kind = SyncCycle.parseKind(failed.Kind);
                if (kind.IsCancelled)
                    return Cancelled();
                if (await IsDamageAsync(kind, ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested)
                        return Cancelled();
                    Enter(state, SyncCycle.Step.Clone);
                    // null on success: another attempt from the scan; otherwise the outcome that ends the cycle
                    return await ReplaceDamagedCloneAsync(state, ct).ConfigureAwait(false);
                }
                return Fail(state.Step, failed.Kind, failed.Message);
            }
            default:
                return Fail(state.Step, "Unexpected", "the fetch returned no outcome");
        }
        state.Fetch = pulled;

        // the apply, after both a fetch and a "nothing new" (it retries deferred and failed entries)
        Enter(state, SyncCycle.Step.Apply);
        var applied = await new GitSyncApplier(_client, _store, _embedder, new ApplyOptions(_settings.HotMaxContentChars), _nowMs)
            .ApplyAsync(pulled, state.Scan, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            return Cancelled();
        if (applied is ApplyOutcome.Failed applyFailed)
            return Fail(state.Step, "Apply", applyFailed.Reason);
        state.Apply = ((ApplyOutcome.Done)applied).Summary;

        // the push gate
        var gate = SyncCycle.pushDecision(true, true, state.Apply.Complete);
        if (gate is SyncCycle.PushGate.Skip skip)
        {
            state.PushSkipped = skip.reason;
            return new CycleOutcome.Done(state.ToSummary());
        }

        return await PushAsync(state, ct).ConfigureAwait(false);
    }

    /// <summary>The push step. Filled in by the next task: for now a complete pull ends the cycle without a push.</summary>
    private Task<CycleOutcome?> PushAsync(RunState state, CancellationToken ct) =>
        Task.FromResult<CycleOutcome?>(new CycleOutcome.Done(state.ToSummary()));
}
