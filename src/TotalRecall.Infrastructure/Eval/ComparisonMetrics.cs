// src/TotalRecall.Infrastructure/Eval/ComparisonMetrics.cs
//
// Plan 5 Task 5.3b — port of computeComparisonMetrics from
// src-ts/eval/metrics.ts:229-316. Pure math over two RetrievalEventRow
// lists. Uses MetricsReport.Compute for the before/after aggregates and
// then produces per-tier, per-content-type, and query-level diffs
// following the TS algorithm exactly (union of keys, per-pair delta,
// regression = "used -> not used", improvement = the reverse).

using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Eval;

public sealed record MetricDeltas(
    double Precision,
    double HitRate,
    double Mrr,
    double MissRate,
    double AvgLatencyMs);

public sealed record TierDeltas(double Precision, double HitRate, double AvgScore);

public sealed record TierComparison(TierMetrics Before, TierMetrics After, TierDeltas Deltas);

public sealed record ContentTypeDeltas(double Precision, double HitRate);

public sealed record ContentTypeComparison(
    ContentTypeMetrics Before,
    ContentTypeMetrics After,
    ContentTypeDeltas Deltas);

public sealed record QueryDiffEntry(
    string QueryText,
    string BeforeOutcome,
    string AfterOutcome,
    double? BeforeScore,
    double? AfterScore);

public sealed record QueryDiff(
    IReadOnlyList<QueryDiffEntry> Regressions,
    IReadOnlyList<QueryDiffEntry> Improvements);

public sealed record ComparisonResult(
    MetricsReport Before,
    MetricsReport After,
    MetricDeltas Deltas,
    IReadOnlyDictionary<string, TierComparison> ByTier,
    IReadOnlyDictionary<string, ContentTypeComparison> ByContentType,
    QueryDiff QueryDiff,
    string? Warning);

/// <summary>
/// Pure before/after metrics diff. Ports <c>computeComparisonMetrics</c>
/// from <c>src-ts/eval/metrics.ts</c>. Produces a full <see cref="ComparisonResult"/>
/// in one call, delegating the per-side aggregation to
/// <see cref="Metrics.Compute"/>.
/// </summary>
public static class ComparisonMetrics
{
    public static ComparisonResult Compute(
        IReadOnlyList<RetrievalEventRow> eventsBefore,
        IReadOnlyList<RetrievalEventRow> eventsAfter,
        double similarityThreshold)
    {
        ArgumentNullException.ThrowIfNull(eventsBefore);
        ArgumentNullException.ThrowIfNull(eventsAfter);

        var before = Metrics.Compute(eventsBefore, similarityThreshold);
        var after = Metrics.Compute(eventsAfter, similarityThreshold);

        var deltas = new MetricDeltas(
            Precision: after.Precision - before.Precision,
            HitRate: after.HitRate - before.HitRate,
            Mrr: after.Mrr - before.Mrr,
            MissRate: after.MissRate - before.MissRate,
            AvgLatencyMs: after.AvgLatencyMs - before.AvgLatencyMs);

        var emptyTier = new TierMetrics(0, 0, 0, 0);
        var byTier = new Dictionary<string, TierComparison>(StringComparer.Ordinal);
        var tierKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in before.ByTier.Keys) tierKeys.Add(k);
        foreach (var k in after.ByTier.Keys) tierKeys.Add(k);
        foreach (var tier in tierKeys)
        {
            var b = before.ByTier.TryGetValue(tier, out var bv) ? bv : emptyTier;
            var a = after.ByTier.TryGetValue(tier, out var av) ? av : emptyTier;
            byTier[tier] = new TierComparison(b, a, new TierDeltas(
                Precision: a.Precision - b.Precision,
                HitRate: a.HitRate - b.HitRate,
                AvgScore: a.AvgScore - b.AvgScore));
        }

        var emptyType = new ContentTypeMetrics(0, 0, 0);
        var byContentType = new Dictionary<string, ContentTypeComparison>(StringComparer.Ordinal);
        var typeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in before.ByContentType.Keys) typeKeys.Add(k);
        foreach (var k in after.ByContentType.Keys) typeKeys.Add(k);
        foreach (var ct in typeKeys)
        {
            var b = before.ByContentType.TryGetValue(ct, out var bv) ? bv : emptyType;
            var a = after.ByContentType.TryGetValue(ct, out var av) ? av : emptyType;
            byContentType[ct] = new ContentTypeComparison(b, a, new ContentTypeDeltas(
                Precision: a.Precision - b.Precision,
                HitRate: a.HitRate - b.HitRate));
        }

        // Query-level diff — last event per query text wins, matching the
        // TS Map overwrite semantics.
        var beforeByQuery = new Dictionary<string, RetrievalEventRow>(StringComparer.Ordinal);
        foreach (var e in eventsBefore) beforeByQuery[e.QueryText] = e;
        var afterByQuery = new Dictionary<string, RetrievalEventRow>(StringComparer.Ordinal);
        foreach (var e in eventsAfter) afterByQuery[e.QueryText] = e;

        var regressions = new List<QueryDiffEntry>();
        var improvements = new List<QueryDiffEntry>();

        var allQueries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in beforeByQuery.Keys) allQueries.Add(q);
        foreach (var q in afterByQuery.Keys) allQueries.Add(q);

        foreach (var q in allQueries)
        {
            beforeByQuery.TryGetValue(q, out var b);
            afterByQuery.TryGetValue(q, out var a);
            var bOutcome = Outcome(b);
            var aOutcome = Outcome(a);
            if (bOutcome == aOutcome) continue;

            var entry = new QueryDiffEntry(
                QueryText: q,
                BeforeOutcome: bOutcome,
                AfterOutcome: aOutcome,
                BeforeScore: b?.TopScore,
                AfterScore: a?.TopScore);

            if (bOutcome == "used" && aOutcome != "used") regressions.Add(entry);
            if (aOutcome == "used" && bOutcome != "used") improvements.Add(entry);
        }

        string? warning = null;
        if (eventsBefore.Count == 0 || eventsAfter.Count == 0)
        {
            warning = "Comparison requires retrieval events from both snapshots. One side has no data — metrics may not be meaningful.";
        }

        return new ComparisonResult(
            Before: before,
            After: after,
            Deltas: deltas,
            ByTier: byTier,
            ByContentType: byContentType,
            QueryDiff: new QueryDiff(regressions, improvements),
            Warning: warning);
    }

    private static string Outcome(RetrievalEventRow? e)
    {
        if (e is null) return "missing";
        return e.OutcomeUsed == true ? "used" : "unused";
    }
}
