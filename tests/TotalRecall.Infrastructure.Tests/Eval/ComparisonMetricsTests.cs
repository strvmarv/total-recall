using System;
using System.Collections.Generic;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class ComparisonMetricsTests
{
    private static RetrievalEventRow Ev(string query, double? topScore, bool? used, string tier = "warm", string ct = "memory")
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: 1,
            SessionId: "s",
            QueryText: query,
            QuerySource: "test",
            QueryEmbedding: null,
            ResultsJson: "[]",
            ResultCount: 0,
            TopScore: topScore,
            TopTier: tier,
            TopContentType: ct,
            OutcomeUsed: used,
            OutcomeSignal: null,
            ConfigSnapshotId: "cfg",
            LatencyMs: 5,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    [Fact]
    public void Compute_ComputesDeltas()
    {
        var before = new List<RetrievalEventRow>
        {
            Ev("q1", 0.9, true),
            Ev("q2", 0.4, false),
        };
        var after = new List<RetrievalEventRow>
        {
            Ev("q1", 0.9, true),
            Ev("q2", 0.8, true),
        };

        var result = ComparisonMetrics.Compute(before, after, 0.5);
        Assert.Null(result.Warning);
        Assert.True(result.Deltas.Precision > 0);
        Assert.True(result.Deltas.HitRate > 0);
    }

    [Fact]
    public void Compute_RegressionsAndImprovements()
    {
        var before = new List<RetrievalEventRow>
        {
            Ev("q-regress", 0.9, true),
            Ev("q-improve", 0.3, false),
            Ev("q-same-used", 0.9, true),
        };
        var after = new List<RetrievalEventRow>
        {
            Ev("q-regress", 0.9, false),
            Ev("q-improve", 0.9, true),
            Ev("q-same-used", 0.9, true),
        };

        var result = ComparisonMetrics.Compute(before, after, 0.5);
        Assert.Single(result.QueryDiff.Regressions);
        Assert.Equal("q-regress", result.QueryDiff.Regressions[0].QueryText);
        Assert.Single(result.QueryDiff.Improvements);
        Assert.Equal("q-improve", result.QueryDiff.Improvements[0].QueryText);
    }

    [Fact]
    public void Compute_EmptySide_SetsWarning()
    {
        var before = new List<RetrievalEventRow>();
        var after = new List<RetrievalEventRow> { Ev("q", 0.9, true) };
        var result = ComparisonMetrics.Compute(before, after, 0.5);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Compute_PerTierDeltas()
    {
        var before = new List<RetrievalEventRow> { Ev("q1", 0.9, true, tier: "warm") };
        var after = new List<RetrievalEventRow>
        {
            Ev("q1", 0.9, true, tier: "warm"),
            Ev("q2", 0.8, true, tier: "hot"),
        };
        var result = ComparisonMetrics.Compute(before, after, 0.5);
        Assert.True(result.ByTier.ContainsKey("warm"));
        Assert.True(result.ByTier.ContainsKey("hot"));
        Assert.Equal(0, result.ByTier["hot"].Before.Count);
    }
}
