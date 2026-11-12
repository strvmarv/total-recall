// src/TotalRecall.Infrastructure/Eval/RegressionDetector.cs
//
// Session-start retrieval regression detection. Compares the retrieval
// metrics recorded under the session's config snapshot with those of the
// previous snapshot and reports a miss-rate or latency worsening.
// Evaluate is pure; Check (added below) does the snapshot and event reads.
//
// Ports checkRegressions from the removed TypeScript engine, with two
// differences: only events with a resolved outcome are counted (a pending
// recent retrieval is not a miss yet), and thresholds are compared with a
// small tolerance so a rise of exactly the threshold alerts (0.3 - 0.2 is
// below 0.1 in doubles).

using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Infrastructure.Eval;

/// <summary>One detected worsening. <c>Delta</c> is the absolute rise for
/// <see cref="MissRate"/> and the current/previous ratio for <see cref="Latency"/>.</summary>
public sealed record RegressionFinding(string Kind, double Previous, double Current, double Delta, double Threshold)
{
    public const string MissRate = "miss_rate";
    public const string Latency = "latency";
}

public sealed class RegressionDetector
{
    /// <summary>Retrieval events become "missed" after this long without feedback, as in eval_report.</summary>
    internal const long GraceWindowMs = 60 * 60 * 1000L;

    private const double Tolerance = 1e-9;

    /// <summary>
    /// Compare the previous snapshot's events with the current snapshot's.
    /// Returns null when either side has fewer than <see cref="RegressionOptions.MinEvents"/>
    /// scored events, or when no threshold is crossed; never an empty list.
    /// </summary>
    public static IReadOnlyList<RegressionFinding>? Evaluate(
        IReadOnlyList<RetrievalEventRow> previousEvents,
        IReadOnlyList<RetrievalEventRow> currentEvents,
        RegressionOptions options,
        double similarityThreshold,
        long nowMs)
    {
        ArgumentNullException.ThrowIfNull(previousEvents);
        ArgumentNullException.ThrowIfNull(currentEvents);
        ArgumentNullException.ThrowIfNull(options);

        var previous = Metrics.Compute(previousEvents, similarityThreshold, nowMs, GraceWindowMs);
        var current = Metrics.Compute(currentEvents, similarityThreshold, nowMs, GraceWindowMs);
        if (previous.ScoredEvents < options.MinEvents || current.ScoredEvents < options.MinEvents)
            return null;

        List<RegressionFinding>? findings = null;

        var rise = current.MissRate - previous.MissRate;
        if (rise >= options.MissRateDelta - Tolerance)
        {
            (findings ??= new()).Add(new RegressionFinding(
                RegressionFinding.MissRate, previous.MissRate, current.MissRate, rise, options.MissRateDelta));
        }

        if (previous.AvgLatencyMs > 0)
        {
            var ratio = current.AvgLatencyMs / previous.AvgLatencyMs;
            if (ratio >= options.LatencyRatio - Tolerance)
            {
                (findings ??= new()).Add(new RegressionFinding(
                    RegressionFinding.Latency, previous.AvgLatencyMs, current.AvgLatencyMs, ratio, options.LatencyRatio));
            }
        }

        return findings;
    }
}
