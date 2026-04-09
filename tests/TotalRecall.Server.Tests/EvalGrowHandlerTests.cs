// Plan 6 Task 6.0c — EvalGrowHandler contract tests.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class EvalGrowHandlerTests
{
    private sealed class FakeGrow : IEvalGrowExecutor
    {
        public List<CandidateRow> Rows { get; } = new();
        public IReadOnlyList<string>? LastAccepts;
        public IReadOnlyList<string>? LastRejects;
        public string? LastPath;
        public CandidateResolveResult? NextResolveResult;

        public IReadOnlyList<CandidateRow> ListPending() => Rows;

        public CandidateResolveResult Resolve(
            IReadOnlyList<string> accepts,
            IReadOnlyList<string> rejects,
            string benchmarkPath)
        {
            LastAccepts = accepts;
            LastRejects = rejects;
            LastPath = benchmarkPath;
            return NextResolveResult ?? new CandidateResolveResult(
                Accepted: accepts.Count,
                Rejected: rejects.Count,
                CorpusEntries: Array.Empty<string>());
        }
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task List_ReturnsCandidates()
    {
        var fake = new FakeGrow();
        fake.Rows.Add(new CandidateRow("id1", "why?", 0.3, "top", "eid", 100, 200, 3, "pending"));
        fake.Rows.Add(new CandidateRow("id2", "what?", 0.2, null, null, 150, 250, 5, "pending"));
        var handler = new EvalGrowHandler(fake);

        var result = await handler.ExecuteAsync(Args("""{"action":"list"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("list", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("candidates").GetArrayLength());
        Assert.Equal("id1", doc.RootElement.GetProperty("candidates")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Resolve_AppliesAcceptsAndRejects()
    {
        var fake = new FakeGrow
        {
            NextResolveResult = new CandidateResolveResult(
                Accepted: 2,
                Rejected: 1,
                CorpusEntries: new[] { "{\"query\":\"q1\"}", "{\"query\":\"q2\"}" }),
        };
        var handler = new EvalGrowHandler(fake);
        var result = await handler.ExecuteAsync(
            Args("""{"action":"resolve","accept":["a","b"],"reject":["c"],"benchmark":"/tmp/bench.jsonl"}"""),
            CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, fake.LastAccepts);
        Assert.Equal(new[] { "c" }, fake.LastRejects);
        Assert.Equal("/tmp/bench.jsonl", fake.LastPath);

        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal("resolve", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("rejected").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("corpusEntries").GetArrayLength());
    }

    [Fact]
    public async Task List_Empty_ReturnsZero()
    {
        var handler = new EvalGrowHandler(new FakeGrow());
        var result = await handler.ExecuteAsync(Args("""{"action":"list"}"""), CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task MissingAction_Throws()
    {
        var handler = new EvalGrowHandler(new FakeGrow());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{}"""), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownAction_Throws()
    {
        var handler = new EvalGrowHandler(new FakeGrow());
        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.ExecuteAsync(Args("""{"action":"wat"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_List()
    {
        var fake = new FakeGrow();
        fake.Rows.Add(new CandidateRow("id1", "q", 0.1, null, null, 1, 2, 1, "pending"));
        var handler = new EvalGrowHandler(fake);
        var result = await handler.ExecuteAsync(Args("""{"action":"list"}"""), CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.EvalGrowListResultDto);
        Assert.NotNull(dto);
        Assert.Equal("list", dto!.Action);
        Assert.Single(dto.Candidates);
    }

    [Fact]
    public void Name_Is_eval_grow()
    {
        Assert.Equal("eval_grow", new EvalGrowHandler(new FakeGrow()).Name);
    }
}
