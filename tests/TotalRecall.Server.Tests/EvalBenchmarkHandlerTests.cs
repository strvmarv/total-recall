// Plan 6 Task 6.0c — EvalBenchmarkHandler contract tests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Server.Handlers;
using Xunit;

namespace TotalRecall.Server.Tests;

public class EvalBenchmarkHandlerTests : IDisposable
{
    private readonly string _corpusPath;
    private readonly string _benchmarkPath;

    public EvalBenchmarkHandlerTests()
    {
        _corpusPath = Path.Combine(Path.GetTempPath(), $"corpus-{Guid.NewGuid():N}.jsonl");
        _benchmarkPath = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(_corpusPath, "{\"content\":\"c\",\"type\":\"imported\",\"tags\":[]}\n");
        File.WriteAllText(_benchmarkPath, "{\"query\":\"q\",\"expected_content_contains\":\"c\",\"expected_tier\":\"warm\"}\n");
    }

    public void Dispose()
    {
        try { if (File.Exists(_corpusPath)) File.Delete(_corpusPath); } catch { }
        try { if (File.Exists(_benchmarkPath)) File.Delete(_benchmarkPath); } catch { }
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static BenchmarkResult FakeResult() =>
        new(
            TotalQueries: 2,
            ExactMatchRate: 0.5,
            FuzzyMatchRate: 0.75,
            TierRoutingRate: 1.0,
            NegativePassRate: 1.0,
            AvgLatencyMs: 12.3,
            Details: new List<BenchmarkDetail>
            {
                new("q1", "c1", "c1-top", 0.9, true, true, false, true),
                new("q2", "c2", null, 0.1, false, false, false, true),
            });

    [Fact]
    public async Task HappyPath_ReturnsStructuredResult()
    {
        var handler = new EvalBenchmarkHandler((_, _) => Task.FromResult(FakeResult()));
        var result = await handler.ExecuteAsync(
            Args($$"""{"corpus":"{{_corpusPath.Replace("\\", "\\\\")}}","benchmark":"{{_benchmarkPath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);
        using var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(2, doc.RootElement.GetProperty("totalQueries").GetInt32());
        Assert.Equal(0.5, doc.RootElement.GetProperty("exactMatchRate").GetDouble());
        Assert.Equal(2, doc.RootElement.GetProperty("details").GetArrayLength());
    }

    [Fact]
    public async Task MissingCorpus_Throws()
    {
        var handler = new EvalBenchmarkHandler((_, _) => Task.FromResult(FakeResult()));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => handler.ExecuteAsync(
                Args("""{"corpus":"/nonexistent-corpus.jsonl","benchmark":"/nonexistent-bench.jsonl"}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task ExecutorThrows_Propagates()
    {
        var handler = new EvalBenchmarkHandler((_, _) => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.ExecuteAsync(
                Args($$"""{"corpus":"{{_corpusPath.Replace("\\", "\\\\")}}","benchmark":"{{_benchmarkPath.Replace("\\", "\\\\")}}"}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task JsonRoundtrip_MatchesDto()
    {
        var handler = new EvalBenchmarkHandler((_, _) => Task.FromResult(FakeResult()));
        var result = await handler.ExecuteAsync(
            Args($$"""{"corpus":"{{_corpusPath.Replace("\\", "\\\\")}}","benchmark":"{{_benchmarkPath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);
        var dto = JsonSerializer.Deserialize(result.Content[0].Text, JsonContext.Default.EvalBenchmarkResultDto);
        Assert.NotNull(dto);
        Assert.Equal(2, dto!.TotalQueries);
        Assert.Equal(2, dto.Details.Length);
    }

    [Fact]
    public void Name_Is_eval_benchmark()
    {
        var handler = new EvalBenchmarkHandler((_, _) => Task.FromResult(FakeResult()));
        Assert.Equal("eval_benchmark", handler.Name);
    }

    [Fact]
    public async Task NoArgs_ResolvesDefaultsViaEvalPaths_NotCwdRelative()
    {
        // Regression: the Web UI calls eval_benchmark with no args, so the
        // handler must resolve the bundled corpus/benchmark via EvalPaths
        // (binary-relative walk-up from AppContext.BaseDirectory), not a bare
        // CWD-relative path that throws FileNotFoundException when the host's
        // CWD is not the package root. The CWD-independent walk-up + fallback
        // logic itself is covered deterministically by EvalPathsTests; here we
        // verify the handler is wired to it. Skip when the bundled eval/ tree
        // isn't discoverable from the test binary (mirrors BenchmarkRunnerTests).
        var expectedCorpus = EvalPaths.Resolve("corpus", "memories.jsonl");
        var expectedBenchmark = EvalPaths.Resolve("benchmarks", "retrieval.jsonl");
        if (!File.Exists(expectedCorpus) || !File.Exists(expectedBenchmark))
        {
            Console.WriteLine("skipping: bundled eval corpus not discoverable from test binary");
            return;
        }

        BenchmarkOptions? captured = null;
        var handler = new EvalBenchmarkHandler((opts, _) =>
        {
            captured = opts;
            return Task.FromResult(FakeResult());
        });

        await handler.ExecuteAsync(arguments: null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(expectedCorpus, captured!.CorpusPath);
        Assert.Equal(expectedBenchmark, captured.BenchmarkPath);
        Assert.True(Path.IsPathRooted(captured.CorpusPath),
            $"corpus path should be absolute, was: {captured.CorpusPath}");
    }
}
