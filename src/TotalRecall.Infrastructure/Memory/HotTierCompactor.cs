// src/TotalRecall.Infrastructure/Memory/HotTierCompactor.cs
//
// Shared heuristic hot→warm compaction. Recalculates decay scores for all
// hot-tier memory entries and compacts any whose score falls below
// warmThreshold. Used by both the session_end MCP tool and the
// `total-recall compact --run` CLI verb. Routes through the canonical
// Decay.calculateDecayScore F# function so the formula lives in one place.

using System;
using System.Collections.Generic;
using System.Threading;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Memory;

public static class HotTierCompactor
{
    public sealed record Result(int CarryForward, int Compacted, int Discarded);

    public static Result Compact(
        IStore store,
        string sessionId,
        long nowMs,
        double warmThreshold,
        double decayConstantHours,
        CompactionLog? compactionLog,
        string reason = "session_end_decay",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        // I1 (tier model v2): sticky is INCLUDED here for now. Excluding sticky
        // from compaction (so pinned rows are never compacted) is Task 8's
        // compaction fast/deep split — Task 5 only owns injection sourcing.
        var hotEntries = store.List(Tier.Hot, ContentType.Memory);
        var compacted = 0;

        foreach (var entry in hotEntries)
        {
            ct.ThrowIfCancellationRequested();

            var score = Decay.calculateDecayScore(
                entry.LastAccessedAt, entry.AccessCount, entry.EntryType, nowMs, decayConstantHours);

            try
            {
                store.Update(Tier.Hot, ContentType.Memory, entry.Id,
                    new UpdateEntryOpts { DecayScore = score });
            }
            catch (InvalidOperationException) { continue; } // concurrently deleted

            if (score >= warmThreshold) continue;

            try
            {
                store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, entry.Id);
                compactionLog?.LogEvent(new CompactionLogEntry(
                    SessionId: sessionId,
                    SourceTier: "hot",
                    TargetTier: "warm",
                    SourceEntryIds: [entry.Id],
                    TargetEntryId: null,
                    DecayScores: new Dictionary<string, double> { [entry.Id] = score },
                    Reason: reason,
                    ConfigSnapshotId: ""));
                compacted++;
            }
            catch (InvalidOperationException) { } // concurrently deleted — skip
        }

        // I1: carry-forward is total hot occupancy — INCLUDES sticky.
        var carryForward = store.Count(Tier.Hot, ContentType.Memory);
        return new Result(carryForward, compacted, Discarded: 0);
    }
}
