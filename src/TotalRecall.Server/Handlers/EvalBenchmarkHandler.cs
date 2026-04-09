// src/TotalRecall.Server/Handlers/EvalBenchmarkHandler.cs
//
// Plan 6 Task 6.0c — ports `total-recall eval benchmark` to MCP. Runs the
// retrieval benchmark harness via BenchmarkRunner.
//
// IMPORTANT: this handler is LONG-RUNNING. The benchmark suite seeds the
// corpus, issues N embedding + hybrid-search calls, then tears the seeded
// rows down. Against the bundled corpus this is seconds; against a larger
// corpus it may be minutes. The MCP transport is stdio, so while this tool
// is executing the dispatch loop is blocked for its entire duration —
// consistent with how `kb_ingest_dir` already behaves. Clients should use a
// generous timeout and not run this tool concurrently with other requests.
//
// Args: { corpus? (path, default eval/corpus/memories.jsonl),
//         benchmark? (path, default eval/benchmarks/retrieval.jsonl) }

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>Test seam for <see cref="EvalBenchmarkHandler"/>.</summary>
public delegate Task<BenchmarkResult> EvalBenchmarkExecutor(
    BenchmarkOptions opts, CancellationToken ct);

public sealed class EvalBenchmarkHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "corpus": {"type":"string","description":"Path to corpus JSONL"},
            "benchmark": {"type":"string","description":"Path to benchmark JSONL"}
          }
        }
        """).RootElement.Clone();

    private readonly EvalBenchmarkExecutor? _executor;

    public EvalBenchmarkHandler() { _executor = null; }

    /// <summary>Test/composition seam.</summary>
    public EvalBenchmarkHandler(EvalBenchmarkExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Name => "eval_benchmark";
    public string Description => "Run the retrieval benchmark suite (long-running)";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        string corpusPath = Path.Combine("eval", "corpus", "memories.jsonl");
        string benchmarkPath = Path.Combine("eval", "benchmarks", "retrieval.jsonl");

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            if (args.TryGetProperty("corpus", out var cEl) && cEl.ValueKind == JsonValueKind.String)
            {
                var s = cEl.GetString();
                if (!string.IsNullOrEmpty(s)) corpusPath = s;
            }
            if (args.TryGetProperty("benchmark", out var bEl) && bEl.ValueKind == JsonValueKind.String)
            {
                var s = bEl.GetString();
                if (!string.IsNullOrEmpty(s)) benchmarkPath = s;
            }
        }

        if (!File.Exists(corpusPath))
            throw new FileNotFoundException($"corpus not found: {corpusPath}", corpusPath);
        if (!File.Exists(benchmarkPath))
            throw new FileNotFoundException($"benchmark file not found: {benchmarkPath}", benchmarkPath);

        ct.ThrowIfCancellationRequested();

        var executor = _executor ?? BuildProductionExecutor();
        var result = await executor(new BenchmarkOptions(corpusPath, benchmarkPath), ct).ConfigureAwait(false);

        var details = new EvalBenchmarkDetailDto[result.Details.Count];
        for (int i = 0; i < details.Length; i++)
        {
            var d = result.Details[i];
            details[i] = new EvalBenchmarkDetailDto(
                Query: d.Query,
                ExpectedContains: d.ExpectedContains,
                TopResult: d.TopResult,
                TopScore: d.TopScore,
                Matched: d.Matched,
                FuzzyMatched: d.FuzzyMatched,
                HasNegativeAssertion: d.HasNegativeAssertion,
                NegativePass: d.NegativePass);
        }

        var dto = new EvalBenchmarkResultDto(
            TotalQueries: result.TotalQueries,
            ExactMatchRate: result.ExactMatchRate,
            FuzzyMatchRate: result.FuzzyMatchRate,
            TierRoutingRate: result.TierRoutingRate,
            NegativePassRate: result.NegativePassRate,
            AvgLatencyMs: result.AvgLatencyMs,
            Details: details);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.EvalBenchmarkResultDto);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }

    private static EvalBenchmarkExecutor BuildProductionExecutor()
    {
        return async (opts, ct) =>
        {
            var dbPath = ConfigLoader.GetDbPath();
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                var vec = new VectorSearch(conn);
                var fts = new FtsSearch(conn);
                var hybrid = new HybridSearch(vec, fts, store);
                var embedder = EmbedderFactory.CreateProduction();
                var runner = new BenchmarkRunner(store, vec, hybrid, embedder);
                return await runner.RunAsync(opts, ct).ConfigureAwait(false);
            }
            finally
            {
                conn.Dispose();
            }
        };
    }
}
