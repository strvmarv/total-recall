// src/TotalRecall.Server/Handlers/EvalGrowHandler.cs
//
// Plan 6 Task 6.0c — ports `total-recall eval grow` to MCP. Manages
// benchmark candidates (list pending / resolve accepts+rejects) via
// BenchmarkCandidates. Single tool that dispatches on an `action` arg —
// parity with the CLI's `grow list` and `grow resolve` sub-actions.
//
// Args:
//   action: "list" | "resolve" (required)
//   for "resolve":
//     accept?: string[]  (candidate ids to accept)
//     reject?: string[]  (candidate ids to reject)
//     benchmark?: string (default-resolved path to retrieval.jsonl)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// Test seam for <see cref="EvalGrowHandler"/>. Implementations dispatch
/// on the action string — "list" vs "resolve".
/// </summary>
public interface IEvalGrowExecutor
{
    IReadOnlyList<CandidateRow> ListPending();
    CandidateResolveResult Resolve(
        IReadOnlyList<string> accepts,
        IReadOnlyList<string> rejects,
        string benchmarkPath);
}

public sealed class EvalGrowHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {"type":"string","enum":["list","resolve"],"description":"list pending candidates, or resolve accepts/rejects"},
            "accept": {"type":"array","items":{"type":"string"},"description":"candidate ids to accept (resolve)"},
            "reject": {"type":"array","items":{"type":"string"},"description":"candidate ids to reject (resolve)"},
            "benchmark": {"type":"string","description":"path to benchmark JSONL (resolve)"}
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private readonly IEvalGrowExecutor? _executor;

    public EvalGrowHandler() { _executor = null; }

    public EvalGrowHandler(IEvalGrowExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Name => "eval_grow";
    public string Description => "List or resolve pending benchmark candidates";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("eval_grow requires arguments object");
        var args = arguments.Value;
        if (!args.TryGetProperty("action", out var aEl) || aEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("action is required");
        var action = aEl.GetString();
        if (string.IsNullOrEmpty(action))
            throw new ArgumentException("action must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        var executor = _executor ?? BuildProductionExecutor();

        switch (action)
        {
            case "list":
            {
                var rows = executor.ListPending();
                var candidates = new EvalGrowCandidateDto[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = rows[i];
                    candidates[i] = new EvalGrowCandidateDto(
                        Id: r.Id,
                        QueryText: r.QueryText,
                        TopScore: r.TopScore,
                        TopResultContent: r.TopResultContent,
                        TopResultEntryId: r.TopResultEntryId,
                        FirstSeen: r.FirstSeen,
                        LastSeen: r.LastSeen,
                        TimesSeen: r.TimesSeen,
                        Status: r.Status);
                }
                var dto = new EvalGrowListResultDto(
                    Action: "list",
                    Candidates: candidates,
                    Count: candidates.Length);
                var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.EvalGrowListResultDto);
                return Task.FromResult(new ToolCallResult
                {
                    Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
                    IsError = false,
                });
            }
            case "resolve":
            {
                var accepts = ReadStringArray(args, "accept");
                var rejects = ReadStringArray(args, "reject");
                string benchmarkPath = ResolveDefaultBenchmarkPath();
                if (args.TryGetProperty("benchmark", out var bEl) && bEl.ValueKind == JsonValueKind.String)
                {
                    var s = bEl.GetString();
                    if (!string.IsNullOrEmpty(s)) benchmarkPath = s;
                }
                var result = executor.Resolve(accepts, rejects, benchmarkPath);
                var corpus = new string[result.CorpusEntries.Count];
                for (int i = 0; i < corpus.Length; i++) corpus[i] = result.CorpusEntries[i];
                var dto = new EvalGrowResolveResultDto(
                    Action: "resolve",
                    Accepted: result.Accepted,
                    Rejected: result.Rejected,
                    CorpusEntries: corpus,
                    BenchmarkPath: benchmarkPath);
                var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.EvalGrowResolveResultDto);
                return Task.FromResult(new ToolCallResult
                {
                    Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
                    IsError = false,
                });
            }
            default:
                throw new ArgumentException($"unknown action '{action}' (expected list|resolve)");
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el)) return Array.Empty<string>();
        if (el.ValueKind == JsonValueKind.Null) return Array.Empty<string>();
        if (el.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} must be an array of strings");
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{name} must contain only strings");
            var s = item.GetString();
            if (!string.IsNullOrEmpty(s)) list.Add(s);
        }
        return list;
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

    private static IEvalGrowExecutor BuildProductionExecutor() => new ProductionGrowExecutor();

    private sealed class ProductionGrowExecutor : IEvalGrowExecutor
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

        public CandidateResolveResult Resolve(
            IReadOnlyList<string> accepts, IReadOnlyList<string> rejects, string benchmarkPath)
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
}
