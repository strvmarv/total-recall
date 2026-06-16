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
        long nowMs,
        long graceWindowMs,
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

        // Resolve each event's outcome at read time; pending (recent NULL) events
        // are excluded from every rate.
        var scored = events
            .Select(e => (Event: e, Outcome: ResolveOutcome(e, nowMs, graceWindowMs)))
            .Where(x => x.Outcome.HasValue)
            .Select(x => (x.Event, Used: x.Outcome!.Value))
            .ToList();

        var hits = scored.Count(x => x.Used);
        var precision = scored.Count > 0 ? (double)hits / scored.Count : 0.0;
        var hitRate = precision;          // mirrors the prior identity
        var mrr = precision;              // simplified MRR on the same hit/miss basis

        // Miss rate (score quality) over the same scored set.
        var missCount = scored.Count(x =>
            x.Event.TopScore is null || x.Event.TopScore.Value < similarityThreshold);
        var missRate = scored.Count > 0 ? (double)missCount / scored.Count : 0.0;

        // Average latency over scored events that recorded one.
        var latencies = scored.Where(x => x.Event.LatencyMs.HasValue).ToList();
        var avgLatencyMs = latencies.Count > 0
            ? latencies.Average(x => (double)x.Event.LatencyMs!.Value)
            : 0.0;

        // Group by tier / content type over scored events, carrying the resolved outcome.
        var byTier = scored
            .Where(x => !string.IsNullOrEmpty(x.Event.TopTier))
            .GroupBy(x => x.Event.TopTier!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var (p, h, avg) = ComputeGroupMetrics(g);
                    return new TierMetrics(p, h, avg, g.Count());
                },
                StringComparer.Ordinal);

        var byContentType = scored
            .Where(x => !string.IsNullOrEmpty(x.Event.TopContentType))
            .GroupBy(x => x.Event.TopContentType!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var (p, h, _) = ComputeGroupMetrics(g);
                    return new ContentTypeMetrics(p, h, g.Count());
                },
                StringComparer.Ordinal);

        // Top misses: lowest scoring scored events (null sorts as -1).
        var topMisses = scored
            .Where(x => x.Event.TopScore is null || x.Event.TopScore.Value < similarityThreshold)
            .OrderBy(x => x.Event.TopScore ?? -1.0)
            .Take(10)
            .Select(x => new MissEntry(x.Event.QueryText, x.Event.TopScore, x.Event.Timestamp))
            .ToList();

        // False positives: explicit negatives with a high score.
        var falsePositives = scored
            .Where(x => !x.Used && x.Event.TopScore.HasValue && x.Event.TopScore.Value >= similarityThreshold)
            .OrderByDescending(x => x.Event.TopScore ?? 0.0)
            .Take(10)
            .Select(x => new MissEntry(x.Event.QueryText, x.Event.TopScore, x.Event.Timestamp))
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

    // Read-time outcome resolution. true = hit, false = miss, null = pending (excluded).
    private static bool? ResolveOutcome(RetrievalEventRow e, long nowMs, long graceWindowMs)
    {
        if (e.OutcomeUsed == true) return true;
        if (e.OutcomeUsed == false) return false;
        return e.Timestamp < nowMs - graceWindowMs ? false : (bool?)null;
    }

    private static (double Precision, double HitRate, double AvgScore) ComputeGroupMetrics(
        IEnumerable<(RetrievalEventRow Event, bool Used)> scored)
    {
        var list = scored as IList<(RetrievalEventRow Event, bool Used)> ?? scored.ToList();
        var used = list.Count(x => x.Used);
        var precision = list.Count > 0 ? (double)used / list.Count : 0.0;
        var withScore = list.Where(x => x.Event.TopScore.HasValue).ToList();
        var avgScore = withScore.Count > 0 ? withScore.Average(x => x.Event.TopScore!.Value) : 0.0;
        return (precision, precision, avgScore);
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
