// src/TotalRecall.Infrastructure/Memory/PinnedFloorDecider.cs
//
// Pure adaptive-throttle decision for the pinned floor. No I/O. Re-inject when
// EITHER the turn-count or transcript-growth trigger trips since the last
// injection. The first invocation for a session seeds state and skips, because
// session_start already injected the pinned block at the top of context.

namespace TotalRecall.Infrastructure.Memory;

public enum FloorVerdict { Skip, Inject }

public readonly record struct FloorSignal(long? CurrentTranscriptBytes);

public readonly record struct FloorThresholds(bool Enabled, int EveryNTurns, int GrowthTokens);

public static class PinnedFloorDecider
{
    public const int BytesPerToken = 4;

    public static (FloorVerdict Verdict, FloorState Next) Decide(
        FloorState state, FloorSignal signal, FloorThresholds cfg)
    {
        var bytes = signal.CurrentTranscriptBytes;
        var nextTurn = state.TurnCount + 1;

        if (!state.Seeded)
        {
            return (FloorVerdict.Skip, state with
            {
                Seeded = true,
                TurnCount = nextTurn,
                LastInjectedTurn = nextTurn,
                LastInjectedBytes = bytes ?? 0L,
            });
        }

        if (!cfg.Enabled)
            return (FloorVerdict.Skip, state with { TurnCount = nextTurn });

        var inject = (nextTurn - state.LastInjectedTurn) >= cfg.EveryNTurns;

        if (!inject && bytes is long b)
        {
            var bytesDelta = b - state.LastInjectedBytes;
            if (bytesDelta >= (long)cfg.GrowthTokens * BytesPerToken)
                inject = true;
        }

        if (inject)
        {
            return (FloorVerdict.Inject, state with
            {
                TurnCount = nextTurn,
                LastInjectedTurn = nextTurn,
                LastInjectedBytes = bytes ?? state.LastInjectedBytes,
            });
        }

        return (FloorVerdict.Skip, state with { TurnCount = nextTurn });
    }
}
