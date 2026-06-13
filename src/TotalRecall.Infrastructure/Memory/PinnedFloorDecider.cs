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

        // Clamp degenerate config rather than injecting on every turn.
        var everyN = cfg.EveryNTurns > 0 ? cfg.EveryNTurns : 6;
        var growthBytes = cfg.GrowthTokens > 0 ? (long)cfg.GrowthTokens * BytesPerToken : long.MaxValue;

        // Re-inject on the Nth turn after the last injection (>= is inclusive and
        // also catches stale/skipped-turn state). Not off-by-one: last=1, everyN=6
        // first fires at nextTurn=7 (6 turns later).
        var inject = (nextTurn - state.LastInjectedTurn) >= everyN;

        if (!inject && bytes is long b)
        {
            var bytesDelta = b - state.LastInjectedBytes;
            if (bytesDelta >= growthBytes)
                inject = true;
        }

        if (inject)
        {
            return (FloorVerdict.Inject, state with
            {
                TurnCount = nextTurn,
                LastInjectedTurn = nextTurn,
                // note: if bytes are unavailable at inject time we keep the old
                // growth baseline; the growth arm may fire on the next turn that
                // does have a byte count. Acceptable — resetting to 0 is worse.
                LastInjectedBytes = bytes ?? state.LastInjectedBytes,
            });
        }

        return (FloorVerdict.Skip, state with { TurnCount = nextTurn });
    }
}
