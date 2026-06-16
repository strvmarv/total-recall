using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class MetricsTests
{
    private static RetrievalEventRow Event(
        string id,
        long ts = 0,
        double? topScore = null,
        string? topTier = null,
        string? topContentType = null,
        bool? outcomeUsed = null,
        long? latencyMs = null,
        string queryText = "q")
        => new(
            Id: id,
            Timestamp: ts,
            SessionId: "s",
            QueryText: queryText,
            QuerySource: "test",
            QueryEmbedding: null,
            ResultsJson: "[]",
            ResultCount: 0,
            TopScore: topScore,
            TopTier: topTier,
            TopContentType: topContentType,
            OutcomeUsed: outcomeUsed,
            OutcomeSignal: null,
            ConfigSnapshotId: "cfg",
            LatencyMs: latencyMs,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    // Shorthand for the read-time-resolution tests: explicit outcome + timestamp.
    private static RetrievalEventRow Ev(bool? outcomeUsed, long ts, double? topScore,
        string tier = "warm", string contentType = "memory", string source = "assistant")
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: ts,
            SessionId: "s",
            QueryText: "q",
            QuerySource: source,
            QueryEmbedding: null,
            ResultsJson: "[]",
            ResultCount: 1,
            TopScore: topScore,
            TopTier: tier,
            TopContentType: contentType,
            OutcomeUsed: outcomeUsed,
            OutcomeSignal: null,
            ConfigSnapshotId: "cfg",
            LatencyMs: 5,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    private const long Now = 10_000_000L;
    private const long Grace = 60 * 60 * 1000L; // 60 min

    [Fact]
    public void Compute_UsedTrue_CountsAsHit()
    {
        var events = new[] { Ev(outcomeUsed: true, ts: Now - 2 * Grace, topScore: 0.9) };
        var r = Metrics.Compute(events, similarityThreshold: 0.5, nowMs: Now, graceWindowMs: Grace);
        Assert.Equal(1.0, r.HitRate, 3);
        Assert.Equal(1.0, r.Precision, 3);
    }

    [Fact]
    public void Compute_AgedNull_CountsAsMiss()
    {
        var events = new[] { Ev(outcomeUsed: null, ts: Now - 2 * Grace, topScore: 0.9) };
        var r = Metrics.Compute(events, 0.5, Now, Grace);
        Assert.Equal(0.0, r.HitRate, 3);
        Assert.Equal(1, r.TotalEvents);
    }

    [Fact]
    public void Compute_RecentNull_IsPending_Excluded()
    {
        var events = new[]
        {
            Ev(outcomeUsed: null, ts: Now - 60_000, topScore: 0.9), // 1 min old → pending
            Ev(outcomeUsed: true, ts: Now - 2 * Grace, topScore: 0.9),
        };
        var r = Metrics.Compute(events, 0.5, Now, Grace);
        Assert.Equal(1.0, r.HitRate, 3); // 1 hit / 1 scored; pending excluded
    }

    [Fact]
    public void Compute_Mixed_PrecisionIsHitsOverScored()
    {
        var events = new[]
        {
            Ev(outcomeUsed: true,  ts: Now - 2 * Grace, topScore: 0.9),
            Ev(outcomeUsed: false, ts: Now - 2 * Grace, topScore: 0.9),
            Ev(outcomeUsed: null,  ts: Now - 2 * Grace, topScore: 0.2), // aged → miss
            Ev(outcomeUsed: null,  ts: Now - 1000,      topScore: 0.9), // pending → excluded
        };
        var r = Metrics.Compute(events, 0.5, Now, Grace);
        Assert.Equal(1.0 / 3.0, r.HitRate, 3); // 1 hit / 3 scored
    }

    [Fact]
    public void Compute_Empty_IsZeroed()
    {
        var r = Metrics.Compute(Array.Empty<RetrievalEventRow>(), 0.5, Now, Grace);
        Assert.Equal(0, r.TotalEvents);
        Assert.Equal(0.0, r.HitRate, 3);
    }

    [Fact]
    public void EmptyEvents_ReturnsZeroMetrics()
    {
        var r = Metrics.Compute(Array.Empty<RetrievalEventRow>(), 0.5, Now, Grace);
        Assert.Equal(0, r.TotalEvents);
        Assert.Equal(0.0, r.Precision);
        Assert.Equal(0.0, r.HitRate);
        Assert.Equal(0.0, r.MissRate);
        Assert.Equal(0.0, r.Mrr);
        Assert.Equal(0.0, r.AvgLatencyMs);
        Assert.Empty(r.ByTier);
        Assert.Empty(r.ByContentType);
        Assert.Empty(r.TopMisses);
        Assert.Empty(r.FalsePositives);
        Assert.Equal(0, r.CompactionHealth.TotalCompactions);
    }

    [Fact]
    public void SyntheticMix_ComputesPrecisionHitRateMissRateAndMrr()
    {
        var events = new List<RetrievalEventRow>
        {
            // All aged (ts:0 ≪ Now - Grace): 3 used → hits, 1 unused → miss.
            Event("1", topScore: 0.9, topTier: "warm", topContentType: "memory", outcomeUsed: true,  latencyMs: 10),
            Event("2", topScore: 0.8, topTier: "warm", topContentType: "memory", outcomeUsed: true,  latencyMs: 20),
            Event("3", topScore: 0.7, topTier: "hot",  topContentType: "memory", outcomeUsed: true,  latencyMs: 30),
            Event("4", topScore: 0.6, topTier: "warm", topContentType: "memory", outcomeUsed: false, latencyMs: 40),
            // null outcome but aged → resolves to a miss, so it now scores too.
            Event("5", topScore: 0.1, topTier: "cold", topContentType: "memory"),
        };

        var r = Metrics.Compute(events, similarityThreshold: 0.5, nowMs: Now, graceWindowMs: Grace);

        Assert.Equal(5, r.TotalEvents);
        // 5 scored (aged null is a miss); 3 hits → 3/5.
        Assert.Equal(3.0 / 5.0, r.Precision);
        Assert.Equal(3.0 / 5.0, r.HitRate);
        // miss = score < 0.5 OR null → only event "5"
        Assert.Equal(1.0 / 5.0, r.MissRate);
        Assert.Equal(3.0 / 5.0, r.Mrr);
        // Avg latency over scored events that recorded one (events 1-4; "5" has none).
        Assert.Equal((10 + 20 + 30 + 40) / 4.0, r.AvgLatencyMs);
    }

    [Fact]
    public void GroupingByTier_ProducesPerGroupAggregates()
    {
        var events = new List<RetrievalEventRow>
        {
            Event("1", topScore: 0.9, topTier: "warm", outcomeUsed: true),
            Event("2", topScore: 0.8, topTier: "warm", outcomeUsed: true),
            Event("3", topScore: 0.4, topTier: "warm", outcomeUsed: false),
            Event("4", topScore: 0.7, topTier: "hot",  outcomeUsed: true),
        };

        var r = Metrics.Compute(events, 0.5, Now, Grace);
        Assert.True(r.ByTier.ContainsKey("warm"));
        Assert.True(r.ByTier.ContainsKey("hot"));

        var warm = r.ByTier["warm"];
        Assert.Equal(3, warm.Count);
        Assert.Equal(2.0 / 3.0, warm.Precision, 6);
        Assert.Equal((0.9 + 0.8 + 0.4) / 3.0, warm.AvgScore, 6);

        var hot = r.ByTier["hot"];
        Assert.Equal(1, hot.Count);
        Assert.Equal(1.0, hot.Precision);
    }

    [Fact]
    public void TopMissesAndFalsePositives_AreSortedAndCappedToTen()
    {
        var events = new List<RetrievalEventRow>();
        for (int i = 0; i < 12; i++)
        {
            // Mark these used so they're scored hits, not false-positive candidates;
            // top-miss selection is purely score-based, so they still feed TopMisses.
            events.Add(Event($"m{i}", topScore: 0.1 * (i + 1), outcomeUsed: true, queryText: $"missQ{i}"));
        }
        // false positives: high score but rejected
        events.Add(Event("fp1", topScore: 0.95, outcomeUsed: false, queryText: "fpA"));
        events.Add(Event("fp2", topScore: 0.85, outcomeUsed: false, queryText: "fpB"));

        var r = Metrics.Compute(events, similarityThreshold: 0.5, nowMs: Now, graceWindowMs: Grace);

        // Misses are everything with score < 0.5 → 4 entries (0.1..0.4),
        // sorted ascending by topScore.
        Assert.True(r.TopMisses.Count <= 10);
        Assert.True(r.TopMisses.Count >= 4);
        for (int i = 1; i < r.TopMisses.Count; i++)
        {
            var prev = r.TopMisses[i - 1].TopScore ?? -1;
            var cur = r.TopMisses[i].TopScore ?? -1;
            Assert.True(prev <= cur);
        }

        // False positives sorted descending by score.
        Assert.Equal(2, r.FalsePositives.Count);
        Assert.Equal("fpA", r.FalsePositives[0].Query);
        Assert.Equal("fpB", r.FalsePositives[1].Query);
    }

    [Fact]
    public void AllNullScoresAndOutcomes_ProducesZeroPrecisionAndHighMissRate()
    {
        var events = new List<RetrievalEventRow>
        {
            Event("a"), Event("b"), Event("c"),
        };
        var r = Metrics.Compute(events, 0.5, Now, Grace);
        Assert.Equal(0.0, r.Precision);
        Assert.Equal(0.0, r.HitRate);
        Assert.Equal(1.0, r.MissRate);
        Assert.Equal(0.0, r.AvgLatencyMs);
    }

    [Fact]
    public void CompactionHealth_AveragesPreservationRatioAndCountsDrift()
    {
        var rows = new List<CompactionAnalyticsRow>
        {
            new("1", 1000, PreservationRatio: 0.8, SemanticDrift: 0.1),
            new("2", 2000, PreservationRatio: 0.6, SemanticDrift: 0.3),
            new("3", 3000, PreservationRatio: null, SemanticDrift: 0.25),
            new("4", 4000, PreservationRatio: 1.0, SemanticDrift: null),
        };
        var r = Metrics.Compute(Array.Empty<RetrievalEventRow>(), 0.5, Now, Grace, rows);
        Assert.Equal(4, r.CompactionHealth.TotalCompactions);
        Assert.Equal((0.8 + 0.6 + 1.0) / 3.0, r.CompactionHealth.AvgPreservationRatio!.Value, 6);
        Assert.Equal(2, r.CompactionHealth.EntriesWithDrift); // 0.3 and 0.25 > 0.2
    }
}
