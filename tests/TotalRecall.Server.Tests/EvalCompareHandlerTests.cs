// Plan 6 Task 6.0c — EvalCompareHandler contract tests.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Telemetry;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class EvalCompareHandlerTests
{
    private static RetrievalEventRow Event(string query, double? score, bool? used)
        => new(
            Id: Guid.NewGuid().ToString(),
            Timestamp: 1000,
            SessionId: "s",
            QueryText: query,
            QuerySource: "test",
            QueryEmbedding: null,
            ResultsJson: "[]",
            ResultCount: 0,
            TopScore: score,
            TopTier: "warm",
            TopContentType: "memory",
            OutcomeUsed: used,
            OutcomeSignal: null,
            ConfigSnapshotId: "cfg",
            LatencyMs: 5,
            TiersSearchedJson: "[]",
            TotalCandidatesScanned: null);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task HappyPath_ProducesDeltas()
    {
        var before = new List<RetrievalEventRow> { Event("q1", 0.9, true), Event("q2", 0.8, true) };
        var after = new List<RetrievalEventRow> { Event("q1", 0.9, true), Event("q2", 0.3, false) };
        var handler = new EvalCompareHandler((_, _, _) => new EvalCompareInputs(
            EventsBefore: before,
            EventsAfter: after,
            SimilarityThreshold: 0.5,
            BeforeResolvedId: "before-id",
            AfterResolvedId: "after-id"));

        var result = await handler.ExecuteAsync(Args("""{"baseline":"v1"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("before-id", doc.RootElement.GetProperty("beforeId").GetString());
        Assert.Equal("after-id", doc.RootElement.GetProperty("afterId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("deltas", out _));
        // q2 regression: used -> unused
        Assert.True(doc.RootElement.GetProperty("regressions").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task MissingBaseline_Throws()
    {
        var handler = new EvalCompareHandler((_, _, _) => throw new InvalidOperationException("should not be called"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task UnresolvedBaseline_Throws()
    {
        var handler = new EvalCompareHandler((_, _, _) => new EvalCompareInputs(
            EventsBefore: Array.Empty<RetrievalEventRow>(),
            EventsAfter: Array.Empty<RetrievalEventRow>(),
            SimilarityThreshold: 0.5,
            BeforeResolvedId: null,
            AfterResolvedId: "after-id"));
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"baseline":"nope"}"""), CancellationToken.None));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var handler = new EvalCompareHandler((_, _, _) => new EvalCompareInputs(
            EventsBefore: Array.Empty<RetrievalEventRow>(),
            EventsAfter: Array.Empty<RetrievalEventRow>(),
            SimilarityThreshold: 0.5,
            BeforeResolvedId: "b",
            AfterResolvedId: "a"));
        var result = await handler.ExecuteAsync(Args("""{"baseline":"v1"}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.EvalCompareResultDto);
        Assert.NotNull(dto);
        Assert.Equal("b", dto!.BeforeId);
        Assert.Equal("a", dto.AfterId);
    }

    [Fact]
    public void Name_Is_eval_compare()
    {
        var handler = new EvalCompareHandler((_, _, _) => throw new InvalidOperationException());
        Assert.Equal("eval_compare", handler.Name);
    }
}
