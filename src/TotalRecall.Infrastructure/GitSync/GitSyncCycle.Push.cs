using TotalRecall.Core;

namespace TotalRecall.Infrastructure.GitSync;

public sealed partial class GitSyncCycle
{
    /// <summary>
    /// The push step (decision 6). Returns the outcome that ends the cycle, or null to run another attempt from the scan
    /// (another machine won the race for the branch, or the push found the clone damaged and it was replaced).
    /// </summary>
    private async Task<CycleOutcome?> PushAsync(RunState state, CancellationToken ct)
    {
        Enter(state, SyncCycle.Step.Push, ct);
        state.Attempts++;
        var result = await new GitSyncPusher(_client, _store, _settings.Branch, _settings.MachineLabel)
            .PushAsync(state.Apply!, state.Scan, ct).ConfigureAwait(false);
        state.Push = result;

        // Once the push command has succeeded the push has finalized whatever the token says: that is a Done (decision 11).
        if (result is PushOutcome.Pushed or PushOutcome.NothingToPush)
            return new CycleOutcome.Done(state.ToSummary());
        if (ct.IsCancellationRequested)
            return Cancelled();

        var step = SyncCycle.stepName(SyncCycle.Step.Push);
        switch (result)
        {
            case PushOutcome.NotReady notReady:
                return Fail(step, "NotReady", notReady.Reason);

            case PushOutcome.NonFastForward:
                return SyncCycle.nextAfterPush(state.Attempts, SyncCycle.PushKind.NonFastForward).IsRetry
                    ? null
                    : Fail(
                        step,
                        "NonFastForward",
                        $"another machine pushed first on every one of {SyncCycle.maxAttempts} tries; every change is still pending and the next cycle tries again");

            case PushOutcome.Failed failed:
            {
                var kind = SyncCycle.parseKind(failed.Kind);
                if (kind.IsCancelled)
                    return Cancelled();
                // the push's own reset, clean or HEAD check failed: damage when a local probe says so (the kind maps to Other)
                if (failed.Kind == "Clone" && await IsDamageAsync(SyncGit.FailureKind.Other, ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested)
                        return Cancelled();
                    state.Attempts--; // no push command was made: a recovery is not an attempt
                    Enter(state, SyncCycle.Step.Clone, ct);
                    return await ReplaceDamagedCloneAsync(state, ct).ConfigureAwait(false);
                }
                if (ct.IsCancellationRequested) // a probe cut short by the token says nothing: decision 11
                    return Cancelled();
                return Fail(step, failed.Kind, failed.Message, failed.Published);
            }

            default:
                return Fail(step, "Unexpected", "the push returned no outcome", SyncPush.Published.Unknown);
        }
    }
}
