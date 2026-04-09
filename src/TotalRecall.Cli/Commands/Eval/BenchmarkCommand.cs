// src/TotalRecall.Cli/Commands/Eval/BenchmarkCommand.cs
//
// Plan 5 Task 5.3a — `total-recall eval benchmark`. Wraps BenchmarkRunner.
//
// Production constructor wires SqliteStore + VectorSearch + FtsSearch +
// HybridSearch + OnnxEmbedder lazily inside RunAsync (so --help is fast and
// does not load the model). Test constructor accepts a delegate that
// produces a fake BenchmarkRunner from the parsed paths, so tests can swap
// in a recording / fake runner without spinning up SQLite or ONNX.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Cli.Internal;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Cli.Commands.Eval;

/// <summary>
/// Test seam for swapping the runner construction. Returns the runner that
/// will execute against <see cref="BenchmarkOptions"/>.
/// </summary>
public delegate Task<BenchmarkResult> BenchmarkExecutor(
    BenchmarkOptions opts, CancellationToken ct);

public sealed class BenchmarkCommand : ICliCommand
{
    private readonly BenchmarkExecutor? _executor;

    public BenchmarkCommand() { _executor = null; }

    /// <summary>Test/composition seam.</summary>
    public BenchmarkCommand(BenchmarkExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Name => "benchmark";
    public string? Group => "eval";
    public string Description => "Run the retrieval benchmark suite and report match rates.";

    public async Task<int> RunAsync(string[] args)
    {
        string corpusPath = Path.Combine("eval", "corpus", "memories.jsonl");
        string benchmarkPath = Path.Combine("eval", "benchmarks", "retrieval.jsonl");
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--corpus":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval benchmark: --corpus requires a value");
                        return 2;
                    }
                    corpusPath = args[++i];
                    break;
                case "--benchmark":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval benchmark: --benchmark requires a value");
                        return 2;
                    }
                    benchmarkPath = args[++i];
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    Console.Error.WriteLine($"eval benchmark: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine($"eval benchmark: corpus not found: {corpusPath}");
            return 1;
        }
        if (!File.Exists(benchmarkPath))
        {
            Console.Error.WriteLine($"eval benchmark: benchmark file not found: {benchmarkPath}");
            return 1;
        }

        BenchmarkResult result;
        try
        {
            var executor = _executor ?? BuildProductionExecutor();
            result = await executor(
                new BenchmarkOptions(corpusPath, benchmarkPath),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"eval benchmark: failed: {ex.Message}");
            return 1;
        }

        Render(result, verbose);
        return 0;
    }

    private static BenchmarkExecutor BuildProductionExecutor()
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

    private static void Render(BenchmarkResult result, bool verbose)
    {
        var table = new Table().Title("[bold]Benchmark Summary[/]");
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Total Queries", result.TotalQueries.ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Exact Match Rate", FormatPct(result.ExactMatchRate));
        table.AddRow("Fuzzy Match Rate", FormatPct(result.FuzzyMatchRate));
        table.AddRow("Tier Routing Rate", FormatPct(result.TierRoutingRate));
        table.AddRow("Negative Pass Rate", FormatPct(result.NegativePassRate));
        table.AddRow("Avg Latency (ms)", result.AvgLatencyMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        AnsiConsole.Write(table);

        if (verbose)
        {
            var details = new Table().Title("[bold]Per-query Details[/]");
            details.AddColumn("Query");
            details.AddColumn("Expected");
            details.AddColumn("Top");
            details.AddColumn(new TableColumn("Score").RightAligned());
            details.AddColumn("Match");
            foreach (var d in result.Details)
            {
                details.AddRow(
                    Markup.Escape(Truncate(d.Query, 40)),
                    Markup.Escape(Truncate(d.ExpectedContains, 30)),
                    Markup.Escape(Truncate(d.TopResult ?? "(none)", 40)),
                    d.TopScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    d.Matched ? "[green]exact[/]"
                              : d.FuzzyMatched ? "[yellow]fuzzy[/]"
                              : "[red]miss[/]");
            }
            AnsiConsole.Write(details);
        }
    }

    private static string FormatPct(double v) =>
        (v * 100.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall eval benchmark [--corpus <path>] [--benchmark <path>] [--verbose]");
    }
}
