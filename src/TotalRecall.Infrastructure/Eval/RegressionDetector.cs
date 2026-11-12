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
    /// <summary>How many of the newest snapshot rows are searched for the session's row and its predecessor.</summary>
    private const int SnapshotWindow = 20;

    /// <summary>Newest events read per snapshot, so a long history cannot slow session start.</summary>
    private const int EventCap = 1000;

    /// <summary>Only assistant-loop retrievals carry feedback, so only they are compared (as eval_report does).</summary>
    private const string QuerySource = "assistant";

    private readonly IConfigSnapshotStore _snapshots;
    private readonly RetrievalEventLog _log;
    private readonly SessionConfigSnapshot _session;
    private readonly RegressionOptions _options;
    private readonly double _similarityThreshold;
    private readonly Func<long> _nowMs;
    private readonly Action<string> _onError;

    /// <param name="snapshots">Snapshot store on the shared connection.</param>
    /// <param name="log">Retrieval event reader on the shared connection.</param>
    /// <param name="session">The session's snapshot id holder (created or reused on first use).</param>
    /// <param name="options">Resolved thresholds.</param>
    /// <param name="similarityThreshold">Score below which a retrieval counts as a miss (tiers.warm.similarity_threshold).</param>
    /// <param name="nowMs">Clock, for the grace window; defaults to the system clock.</param>
    /// <param name="onError">Receives one line per failed check; defaults to stderr.</param>
    public RegressionDetector(
        IConfigSnapshotStore snapshots,
        RetrievalEventLog log,
        SessionConfigSnapshot session,
        RegressionOptions options,
        double similarityThreshold,
        Func<long>? nowMs = null,
        Action<string>? onError = null)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _similarityThreshold = similarityThreshold;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _onError = onError ?? (line => Console.Error.WriteLine(line));
    }

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

    /// <summary>
    /// Compare this session's snapshot with the nearest older snapshot that
    /// has assistant events. Returns null when there is nothing to compare
    /// or on any failure (one line goes to the error sink); never throws.
    /// </summary>
    public IReadOnlyList<RegressionFinding>? Check()
    {
        try
        {
            var sessionId = _session.Resolve();
            if (sessionId is null) return null;

            var recent = _snapshots.ListRecent(SnapshotWindow);
            var at = -1;
            for (var i = 0; i < recent.Count; i++)
            {
                if (string.Equals(recent[i].Id, sessionId, StringComparison.Ordinal)) { at = i; break; }
            }
            if (at < 0) return null;

            var current = ReadEvents(sessionId);
            if (current.Count < _options.MinEvents) return null; // scored events can only be fewer

            // ListRecent is newest first, so the entries after `at` are older.
            for (var i = at + 1; i < recent.Count; i++)
            {
                var previous = ReadEvents(recent[i].Id);
                if (previous.Count == 0) continue; // e.g. a row left by `config set` or eval_snapshot
                return Evaluate(previous, current, _options, _similarityThreshold, _nowMs());
            }
            return null;
        }
        catch (Exception ex)
        {
            _onError($"[total-recall] regression check failed: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<RetrievalEventRow> ReadEvents(string snapshotId)
        => _log.GetEvents(new RetrievalEventQuery(
            ConfigSnapshotId: snapshotId, QuerySource: QuerySource, Limit: EventCap));
}
