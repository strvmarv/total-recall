// src/TotalRecall.Infrastructure/Eval/Metrics.cs
//
// Plan 5 Task 5.3a — port of src-ts/eval/metrics.ts (the read/compute side
// only). Pure math over the existing RetrievalEventRow + CompactionAnalyticsRow
// projections from TotalRecall.Infrastructure.Telemetry. No I/O, no
// candidate-write side effects (Task 5.3b owns the write path on top of
// these read shapes).
//
// Faithful to the TS shapes:
//   - precision / hitRate / missRate / mrr / avgLatencyMs / totalEvents
//   - per-tier and per-content-type group breakdowns
//   - top misses + false positives (each as a ranked MissEntry list)
//   - compaction health derived from preservation_ratio + semantic_drift
//
// The TS file ALSO contains computeComparisonMetrics — that comparison logic
// is intentionally NOT ported here. Task 5.3b owns the compare command and
// will land it then. Documented at the bottom of this file.
//
// CompactionAnalyticsRow is defined in this file (TS keeps it in types.ts) because
// nothing under Infrastructure/Telemetry/CompactionLog.cs currently exposes a
// "list rows for analytics" projection — we add a minimal reader extension on
// CompactionLog (see CompactionLog.GetAllForAnalytics) and shape rows into
// this DTO before handing them to Compute.

using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>Per-tier breakdown of precision/hitRate/avgScore.</summary>
public sealed record TierMetrics(
    double Precision,
    double HitRate,
    double AvgScore,
    int Count);

/// <summary>Per-content-type breakdown.</summary>
public sealed record ContentTypeMetrics(
    double Precision,
    double HitRate,
    int Count);

/// <summary>Single miss / false-positive entry.</summary>
public sealed record MissEntry(
    string Query,
    double? TopScore,
    long Timestamp);

/// <summary>Compaction health summary derived from compaction_log rows.</summary>
public sealed record CompactionHealthMetrics(
    int TotalCompactions,
    double? AvgPreservationRatio,
    int EntriesWithDrift);

/// <summary>
/// Aggregate retrieval-quality metrics computed by <see cref="Metrics.Compute"/>.
/// All numeric fields are zeroed when there are no events to score.
/// </summary>
public sealed record MetricsReport(
    double Precision,
    double HitRate,
    double MissRate,
    double Mrr,
    double AvgLatencyMs,
    int TotalEvents,
    IReadOnlyDictionary<string, TierMetrics> ByTier,
    IReadOnlyDictionary<string, ContentTypeMetrics> ByContentType,
    IReadOnlyList<MissEntry> TopMisses,
    IReadOnlyList<MissEntry> FalsePositives,
    CompactionHealthMetrics CompactionHealth);

/// <summary>
/// Pure-math metric aggregator. Read-only — produces a <see cref="MetricsReport"/>
/// from a list of retrieval events plus optional compaction rows. No DB,
/// no filesystem, no logging. Mirrors <c>computeMetrics</c> in
/// <c>src-ts/eval/metrics.ts</c>.
/// </summary>
public static class Metrics
{
    /// <summary>
    /// Compute the aggregate metrics report. Empty events produce a zeroed
    /// report (still includes compaction health if rows are supplied).
    /// </summary>
    public static MetricsReport Compute(
        IReadOnlyList<RetrievalEventRow> events,
        double similarityThreshold,
        IReadOnlyList<CompactionAnalyticsRow>? compactionRows = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        var compaction = compactionRows ?? Array.Empty<CompactionAnalyticsRow>();

        if (events.Count == 0)
        {
            return new MetricsReport(
                Precision: 0,
                HitRate: 0,
                MissRate: 0,
                Mrr: 0,
                AvgLatencyMs: 0,
                TotalEvents: 0,
                ByTier: new Dictionary<string, TierMetrics>(),
                ByContentType: new Dictionary<string, ContentTypeMetrics>(),
                TopMisses: Array.Empty<MissEntry>(),
                FalsePositives: Array.Empty<MissEntry>(),
                CompactionHealth: ComputeCompactionHealth(compaction));
        }

        // Precision = used / withOutcome.
        var withOutcome = events.Where(e => e.OutcomeUsed.HasValue).ToList();
        var usedCount = withOutcome.Count(e => e.OutcomeUsed == true);
        var precision = withOutcome.Count > 0 ? (double)usedCount / withOutcome.Count : 0.0;

        // Hit rate uses the same numerator/denominator (mirrors the TS impl).
        var hitRate = precision;

        // Miss rate: events whose top_score is null OR below the threshold.
        var missCount = events.Count(e =>
            e.TopScore is null || e.TopScore.Value < similarityThreshold);
        var missRate = (double)missCount / events.Count;

        // Simplified MRR: 1.0 for "used" top results, 0 otherwise.
        var mrrSum = withOutcome.Sum(e => e.OutcomeUsed == true ? 1.0 : 0.0);
        var mrr = withOutcome.Count > 0 ? mrrSum / withOutcome.Count : 0.0;

        // Average latency over events that recorded one.
        var latencies = events.Where(e => e.LatencyMs.HasValue).ToList();
        var avgLatencyMs = latencies.Count > 0
            ? latencies.Average(e => (double)e.LatencyMs!.Value)
            : 0.0;

        // Group by tier (only events that have a top tier).
        var byTier = events
            .Where(e => !string.IsNullOrEmpty(e.TopTier))
            .GroupBy(e => e.TopTier!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var (p, h, avg) = ComputeGroupMetrics(g);
                    return new TierMetrics(p, h, avg, g.Count());
                },
                StringComparer.Ordinal);

        // Group by content type.
        var byContentType = events
            .Where(e => !string.IsNullOrEmpty(e.TopContentType))
            .GroupBy(e => e.TopContentType!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var (p, h, _) = ComputeGroupMetrics(g);
                    return new ContentTypeMetrics(p, h, g.Count());
                },
                StringComparer.Ordinal);

        // Top misses: lowest scoring (null sorts as -1, matching TS).
        var topMisses = events
            .Where(e => e.TopScore is null || e.TopScore.Value < similarityThreshold)
            .OrderBy(e => e.TopScore ?? -1.0)
            .Take(10)
            .Select(e => new MissEntry(e.QueryText, e.TopScore, e.Timestamp))
            .ToList();

        // False positives: high score but rejected by the user.
        var falsePositives = events
            .Where(e => e.OutcomeUsed == false
                        && e.TopScore.HasValue
                        && e.TopScore.Value >= similarityThreshold)
            .OrderByDescending(e => e.TopScore ?? 0.0)
            .Take(10)
            .Select(e => new MissEntry(e.QueryText, e.TopScore, e.Timestamp))
            .ToList();

        return new MetricsReport(
            Precision: precision,
            HitRate: hitRate,
            MissRate: missRate,
            Mrr: mrr,
            AvgLatencyMs: avgLatencyMs,
            TotalEvents: events.Count,
            ByTier: byTier,
            ByContentType: byContentType,
            TopMisses: topMisses,
            FalsePositives: falsePositives,
            CompactionHealth: ComputeCompactionHealth(compaction));
    }

    private static (double Precision, double HitRate, double AvgScore) ComputeGroupMetrics(
        IEnumerable<RetrievalEventRow> events)
    {
        var list = events as IList<RetrievalEventRow> ?? events.ToList();
        var withOutcome = list.Where(e => e.OutcomeUsed.HasValue).ToList();
        var used = withOutcome.Count(e => e.OutcomeUsed == true);
        var precision = withOutcome.Count > 0 ? (double)used / withOutcome.Count : 0.0;
        var hitRate = precision;

        var withScore = list.Where(e => e.TopScore.HasValue).ToList();
        var avgScore = withScore.Count > 0
            ? withScore.Average(e => e.TopScore!.Value)
            : 0.0;

        return (precision, hitRate, avgScore);
    }

    private static CompactionHealthMetrics ComputeCompactionHealth(
        IReadOnlyList<CompactionAnalyticsRow> rows)
    {
        var withRatio = rows.Where(r => r.PreservationRatio.HasValue).ToList();
        var withDrift = rows.Count(r =>
            r.SemanticDrift.HasValue && r.SemanticDrift.Value > 0.2);

        return new CompactionHealthMetrics(
            TotalCompactions: rows.Count,
            AvgPreservationRatio: withRatio.Count > 0
                ? withRatio.Average(r => r.PreservationRatio!.Value)
                : null,
            EntriesWithDrift: withDrift);
    }
}
