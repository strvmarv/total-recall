// src/TotalRecall.Cli/Commands/Eval/GrowCommand.cs
//
// Plan 5 Task 5.3b — `total-recall eval grow list|resolve`. Lists pending
// benchmark candidates, or flips accepted/rejected ids via
// BenchmarkCandidates.Resolve and appends accepted rows to the benchmark
// corpus JSONL file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Eval;

/// <summary>
/// Test seam for <see cref="GrowCommand"/>. Dispatch happens on the
/// <c>action</c> string — implementations must honor "list" vs "resolve".
/// </summary>
public interface IGrowExecutor
{
    IReadOnlyList<CandidateRow> ListPending();
    CandidateResolveResult Resolve(IReadOnlyList<string> accepts, IReadOnlyList<string> rejects, string benchmarkPath);
}

public sealed class GrowCommand : ICliCommand
{
    private readonly IGrowExecutor? _executor;

    public GrowCommand() { _executor = null; }

    public GrowCommand(IGrowExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Name => "grow";
    public string? Group => "eval";
    public string Description => "List or resolve pending benchmark candidates.";

    public Task<int> RunAsync(string[] args)
    {
        string? action = null;
        var accepts = new List<string>();
        var rejects = new List<string>();
        string? benchmarkPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--accept":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval grow: --accept requires a value");
                        return Task.FromResult(2);
                    }
                    ParseCsv(args[++i], accepts);
                    break;
                case "--reject":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval grow: --reject requires a value");
                        return Task.FromResult(2);
                    }
                    ParseCsv(args[++i], rejects);
                    break;
                case "--benchmark":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval grow: --benchmark requires a value");
                        return Task.FromResult(2);
                    }
                    benchmarkPath = args[++i];
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"eval grow: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return Task.FromResult(2);
                    }
                    if (action is null) { action = a; break; }
                    Console.Error.WriteLine($"eval grow: unexpected positional argument '{a}'");
                    return Task.FromResult(2);
            }
        }

        if (string.IsNullOrEmpty(action))
        {
            Console.Error.WriteLine("eval grow: <action> is required (list|resolve)");
            PrintUsage(Console.Error);
            return Task.FromResult(2);
        }

        try
        {
            var executor = _executor ?? BuildProductionExecutor();
            switch (action)
            {
                case "list":
                    RenderList(executor.ListPending());
                    return Task.FromResult(0);
                case "resolve":
                    var path = benchmarkPath ?? ResolveDefaultBenchmarkPath();
                    var result = executor.Resolve(accepts, rejects, path);
                    Console.Out.WriteLine(
                        $"Accepted {result.Accepted}, rejected {result.Rejected}. " +
                        $"Appended {result.CorpusEntries.Count} corpus entries to {path}.");
                    return Task.FromResult(0);
                default:
                    Console.Error.WriteLine($"eval grow: unknown action '{action}' (expected list|resolve)");
                    PrintUsage(Console.Error);
                    return Task.FromResult(2);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"eval grow: failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static void ParseCsv(string value, List<string> target)
    {
        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) target.Add(trimmed);
        }
    }

    private static string ResolveDefaultBenchmarkPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "eval", "benchmarks", "retrieval.jsonl");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine("eval", "benchmarks", "retrieval.jsonl");
    }

    private static void RenderList(IReadOnlyList<CandidateRow> rows)
    {
        var t = new Table().Title("[bold]Pending Benchmark Candidates[/]");
        t.AddColumn("Id");
        t.AddColumn("Query");
        t.AddColumn(new TableColumn("Top Score").RightAligned());
        t.AddColumn("Top Content");
        t.AddColumn(new TableColumn("Times Seen").RightAligned());
        t.AddColumn("First Seen");
        t.AddColumn("Last Seen");
        foreach (var r in rows)
        {
            t.AddRow(
                Markup.Escape(r.Id),
                Markup.Escape(Truncate(r.QueryText, 40)),
                r.TopScore.ToString("F3", CultureInfo.InvariantCulture),
                Markup.Escape(Truncate(r.TopResultContent ?? "(none)", 50)),
                r.TimesSeen.ToString(CultureInfo.InvariantCulture),
                r.FirstSeen.ToString(CultureInfo.InvariantCulture),
                r.LastSeen.ToString(CultureInfo.InvariantCulture));
        }
        AnsiConsole.Write(t);
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }

    private static IGrowExecutor BuildProductionExecutor()
    {
        return new ProductionGrowExecutor();
    }

    private sealed class ProductionGrowExecutor : IGrowExecutor
    {
        public IReadOnlyList<CandidateRow> ListPending()
        {
            var dbPath = ConfigLoader.GetDbPath();
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                return new BenchmarkCandidates(conn).ListPending();
            }
            finally
            {
                conn.Dispose();
            }
        }

        public CandidateResolveResult Resolve(IReadOnlyList<string> accepts, IReadOnlyList<string> rejects, string benchmarkPath)
        {
            var dbPath = ConfigLoader.GetDbPath();
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                return new BenchmarkCandidates(conn).Resolve(accepts, rejects, benchmarkPath);
            }
            finally
            {
                conn.Dispose();
            }
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall eval grow list");
        w.WriteLine("       total-recall eval grow resolve --accept <id1,id2,...> --reject <id1,id2,...> [--benchmark <path>]");
    }
}
