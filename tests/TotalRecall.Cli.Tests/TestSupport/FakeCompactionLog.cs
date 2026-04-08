// tests/TotalRecall.Cli.Tests/TestSupport/FakeCompactionLog.cs
//
// Plan 5 Task 5.5 — thin in-memory ICompactionLogReader double used by
// HistoryCommandTests / LineageCommandTests. Deliberately keeps the
// analytics + last-timestamp methods as no-ops since neither CLI verb
// exercises them.

using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Cli.Tests.TestSupport;

internal sealed class FakeCompactionLog : ICompactionLogReader
{
    public List<CompactionMovementRow> Rows { get; } = new();

    public void Add(CompactionMovementRow row) => Rows.Add(row);

    public long? GetLastTimestampExcludingReason(string excludedReason) => null;

    public IReadOnlyList<CompactionAnalyticsRow> GetAllForAnalytics(long? sinceTimestamp = null)
        => Array.Empty<CompactionAnalyticsRow>();

    public IReadOnlyList<CompactionMovementRow> GetRecentMovements(int limit)
    {
        return Rows
            .OrderByDescending(r => r.Timestamp)
            .Take(limit)
            .ToList();
    }

    public CompactionMovementRow? GetByTargetEntryId(string targetEntryId)
    {
        CompactionMovementRow? best = null;
        foreach (var r in Rows)
        {
            if (r.TargetEntryId == targetEntryId && (best is null || r.Timestamp > best.Timestamp))
            {
                best = r;
            }
        }
        return best;
    }

    public static CompactionMovementRow Row(
        string id,
        long timestamp = 1_700_000_000_000L,
        string? sessionId = "sess-1",
        string sourceTier = "hot",
        string? targetTier = "warm",
        IReadOnlyList<string>? sourceEntryIds = null,
        string? targetEntryId = null,
        string reason = "decay",
        IReadOnlyDictionary<string, double>? decayScores = null)
    {
        return new CompactionMovementRow(
            Id: id,
            Timestamp: timestamp,
            SessionId: sessionId,
            SourceTier: sourceTier,
            TargetTier: targetTier,
            SourceEntryIds: sourceEntryIds ?? Array.Empty<string>(),
            TargetEntryId: targetEntryId,
            Reason: reason,
            DecayScores: decayScores ?? new Dictionary<string, double>());
    }
}
