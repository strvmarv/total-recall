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

    [Fact]
    public void EmptyEvents_ReturnsZeroMetrics()
    {
        var r = Metrics.Compute(Array.Empty<RetrievalEventRow>(), 0.5);
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
            // 4 with outcome: 3 used, 1 unused
            Event("1", topScore: 0.9, topTier: "warm", topContentType: "memory", outcomeUsed: true,  latencyMs: 10),
            Event("2", topScore: 0.8, topTier: "warm", topContentType: "memory", outcomeUsed: true,  latencyMs: 20),
            Event("3", topScore: 0.7, topTier: "hot",  topContentType: "memory", outcomeUsed: true,  latencyMs: 30),
            Event("4", topScore: 0.6, topTier: "warm", topContentType: "memory", outcomeUsed: false, latencyMs: 40),
            // 1 without outcome — counts toward total + miss-rate calc but not precision
            Event("5", topScore: 0.1, topTier: "cold", topContentType: "memory"),
        };

        var r = Metrics.Compute(events, similarityThreshold: 0.5);

        Assert.Equal(5, r.TotalEvents);
        Assert.Equal(3.0 / 4.0, r.Precision);
        Assert.Equal(3.0 / 4.0, r.HitRate);
        // miss = score < 0.5 OR null → only event "5"
        Assert.Equal(1.0 / 5.0, r.MissRate);
        Assert.Equal(3.0 / 4.0, r.Mrr);
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

        var r = Metrics.Compute(events, 0.5);
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
            events.Add(Event($"m{i}", topScore: 0.1 * (i + 1), queryText: $"missQ{i}"));
        }
        // false positives: high score but rejected
        events.Add(Event("fp1", topScore: 0.95, outcomeUsed: false, queryText: "fpA"));
        events.Add(Event("fp2", topScore: 0.85, outcomeUsed: false, queryText: "fpB"));

        var r = Metrics.Compute(events, similarityThreshold: 0.5);

        // Misses are everything with score < 0.5 → 5 entries (0.1..0.5),
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
        var r = Metrics.Compute(events, 0.5);
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
        var r = Metrics.Compute(Array.Empty<RetrievalEventRow>(), 0.5, rows);
        Assert.Equal(4, r.CompactionHealth.TotalCompactions);
        Assert.Equal((0.8 + 0.6 + 1.0) / 3.0, r.CompactionHealth.AvgPreservationRatio!.Value, 6);
        Assert.Equal(2, r.CompactionHealth.EntriesWithDrift); // 0.3 and 0.25 > 0.2
    }
}
