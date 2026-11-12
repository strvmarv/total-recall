using System;
using System.Collections.Generic;
using System.Linq;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Telemetry;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Eval;

public sealed class RegressionDetectorEvaluateTests
{
    private const long Now = 100_000_000L;
    private const double Threshold = 0.65;
    private static readonly RegressionOptions Opts = RegressionOptions.Default; // 0.1, 2.0, 10

    private static RetrievalEventRow Ev(bool miss, long? latencyMs, bool? outcome = true, long ageMs = 2 * 60 * 60 * 1000L)
        => new(
            Id: Guid.NewGuid().ToString(), Timestamp: Now - ageMs, SessionId: "s", QueryText: "q",
            QuerySource: "assistant", QueryEmbedding: null, ResultsJson: "[]", ResultCount: 1,
            TopScore: miss ? 0.1 : 0.9, TopTier: "warm", TopContentType: "memory",
            OutcomeUsed: outcome, OutcomeSignal: null, ConfigSnapshotId: "cfg",
            LatencyMs: latencyMs, TiersSearchedJson: "[]", TotalCandidatesScanned: null);

    /// <summary>total events, of which <paramref name="misses"/> score below the threshold.</summary>
    private static List<RetrievalEventRow> Events(int total, int misses, long? latencyMs = 50)
        => Enumerable.Range(0, total).Select(i => Ev(miss: i < misses, latencyMs)).ToList();

    private static IReadOnlyList<RegressionFinding>? Run(
        List<RetrievalEventRow> previous, List<RetrievalEventRow> current, RegressionOptions? opts = null)
        => RegressionDetector.Evaluate(previous, current, opts ?? Opts, Threshold, Now);

    [Fact]
    public void MissRateRise_AtTheThreshold_IsReported_EvenWhenDoublesRoundBelowIt()
    {
        // 0.3 - 0.2 is 0.09999999999999998 in doubles; the rise of exactly 0.1 must still alert.
        var found = Run(Events(10, 2), Events(10, 3));
        var f = Assert.Single(found!);
        Assert.Equal(RegressionFinding.MissRate, f.Kind);
        Assert.Equal(0.2, f.Previous, 6);
        Assert.Equal(0.3, f.Current, 6);
        Assert.Equal(0.1, f.Delta, 6);
        Assert.Equal(0.1, f.Threshold);
    }

    [Fact]
    public void MissRateRise_JustBelowTheThreshold_IsNotReported()
        => Assert.Null(Run(Events(20, 4), Events(20, 5))); // 0.20 -> 0.25

    [Fact]
    public void MissRateFall_IsNotReported()
        => Assert.Null(Run(Events(10, 6), Events(10, 1)));

    [Fact]
    public void LatencyRise_AtTheRatio_IsReported()
    {
        var found = Run(Events(10, 0, latencyMs: 40), Events(10, 0, latencyMs: 80));
        var f = Assert.Single(found!);
        Assert.Equal(RegressionFinding.Latency, f.Kind);
        Assert.Equal(40, f.Previous, 6);
        Assert.Equal(80, f.Current, 6);
        Assert.Equal(2.0, f.Delta, 6);
        Assert.Equal(2.0, f.Threshold);
    }

    [Fact]
    public void LatencyRise_JustBelowTheRatio_IsNotReported()
        => Assert.Null(Run(Events(10, 0, latencyMs: 100), Events(10, 0, latencyMs: 199)));

    [Fact]
    public void BothKinds_AreReportedTogether()
    {
        var found = Run(Events(10, 0, latencyMs: 40), Events(10, 5, latencyMs: 120));
        Assert.NotNull(found);
        Assert.Equal(new[] { RegressionFinding.MissRate, RegressionFinding.Latency },
            found!.Select(f => f.Kind).ToArray());
    }

    [Fact]
    public void PreviousLatencyOfZero_NeverProducesALatencyFinding()
        => Assert.Null(Run(Events(10, 0, latencyMs: null), Events(10, 0, latencyMs: 500)));

    [Fact]
    public void Latency_AveragesOnlyEventsThatRecordedOne()
    {
        var current = Events(5, 0, latencyMs: 100);
        current.AddRange(Events(5, 0, latencyMs: null));
        var found = Run(Events(10, 0, latencyMs: 50), current);
        var f = Assert.Single(found!);
        Assert.Equal(RegressionFinding.Latency, f.Kind);
        Assert.Equal(100, f.Current, 6);
    }

    [Theory]
    [InlineData(9, 10)]
    [InlineData(10, 9)]
    [InlineData(0, 0)]
    public void TooFewEventsOnEitherSide_IsNull(int previousCount, int currentCount)
        => Assert.Null(Run(Events(previousCount, 0), Events(currentCount, currentCount)));

    [Fact]
    public void PendingEvents_AreNotCountedTowardsTheMinimum()
    {
        // 8 resolved + 2 still-pending (no outcome, inside the grace window) = 8 scored < 10.
        var previous = Events(8, 0);
        previous.AddRange(Enumerable.Range(0, 2).Select(_ => Ev(miss: false, 50, outcome: null, ageMs: 1000)));
        Assert.Null(Run(previous, Events(10, 10)));
    }

    [Fact]
    public void MinEvents_FromTheOptions_IsUsed()
    {
        var opts = new RegressionOptions(0.1, 2.0, 3);
        Assert.NotNull(Run(Events(3, 0), Events(3, 3), opts));
    }

    [Fact]
    public void NothingCrossed_IsNull_NotAnEmptyList()
        => Assert.Null(Run(Events(10, 2, latencyMs: 50), Events(10, 2, latencyMs: 60)));
}
