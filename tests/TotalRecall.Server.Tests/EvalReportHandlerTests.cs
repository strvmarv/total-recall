// Plan 6 Task 6.0c — EvalReportHandler contract tests.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class EvalReportHandlerTests
{
    private static RetrievalEventRow Event(double? topScore, bool? used, string? tier = "warm", string? ct = "memory", long? lat = 5, string query = "q")
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: 1000,
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
            LatencyMs: lat,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    private static EvalReportInputs MakeInputs() =>
        new(
            Events: new List<RetrievalEventRow>
            {
                Event(0.9, true),
                Event(0.8, true),
                Event(0.4, false),
            },
            CompactionRows: new List<CompactionAnalyticsRow>
            {
                new("c1", 1, 0.7, 0.1),
            },
            SimilarityThreshold: 0.5);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_ReturnsStructuredMetrics()
    {
        var handler = new EvalReportHandler(_ => MakeInputs());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(3, doc.RootElement.GetProperty("totalEvents").GetInt32());
        Assert.True(doc.RootElement.GetProperty("precision").GetDouble() > 0);
        Assert.Equal(1, doc.RootElement.GetProperty("compactionHealth").GetProperty("totalCompactions").GetInt32());
    }

    [Fact]
    public async Task EmptyEvents_ReturnsZeroedReport()
    {
        var handler = new EvalReportHandler(_ => new EvalReportInputs(
            Events: Array.Empty<RetrievalEventRow>(),
            CompactionRows: Array.Empty<CompactionAnalyticsRow>(),
            SimilarityThreshold: 0.5));
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("totalEvents").GetInt32());
        Assert.Equal(0.0, doc.RootElement.GetProperty("precision").GetDouble());
    }

    [Fact]
    public async Task DaysArg_PropagatedToProvider()
    {
        RetrievalEventQuery? captured = null;
        var handler = new EvalReportHandler(q => { captured = q; return MakeInputs(); });
        await handler.ExecuteAsync(Args("""{"days":14,"session":"s1","config_snapshot":"c1"}"""), CancellationToken.None);
        Assert.NotNull(captured);
        Assert.Equal(14, captured!.Days);
        Assert.Equal("s1", captured.SessionId);
        Assert.Equal("c1", captured.ConfigSnapshotId);
    }

    [Fact]
    public async Task InvalidDays_Throws()
    {
        var handler = new EvalReportHandler(_ => MakeInputs());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"days":0}"""), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"days":"abc"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var handler = new EvalReportHandler(_ => MakeInputs());
        var result = await handler.ExecuteAsync(null, CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.EvalReportResultDto);
        Assert.NotNull(dto);
        Assert.Equal(3, dto!.TotalEvents);
        Assert.NotNull(dto.ByTier);
        Assert.NotNull(dto.CompactionHealth);
    }

    [Fact]
    public void Name_Is_eval_report()
    {
        var handler = new EvalReportHandler(_ => MakeInputs());
        Assert.Equal("eval_report", handler.Name);
    }
}
