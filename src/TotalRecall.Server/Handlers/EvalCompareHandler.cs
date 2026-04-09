// src/TotalRecall.Server/Handlers/EvalCompareHandler.cs
//
// Plan 6 Task 6.0c — ports `total-recall eval compare` to MCP. Compares
// retrieval metrics between two config snapshots via ComparisonMetrics.
//
// Args:
//   { before (required string: snapshot name/id),
//     after? (default "latest"),
//     days? (default 30) }
// The task spec nominates `baseline` as the required arg. `baseline` maps to
// the CLI's `--before`. We accept both `baseline` and `before` for ergonomic
// parity.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// Resolved inputs for <see cref="EvalCompareHandler"/>. Mirrors the CLI
/// <c>CompareInputs</c> shape but lives in Server so we don't take a Cli
/// project reference.
/// </summary>
public sealed record EvalCompareInputs(
    IReadOnlyList<RetrievalEventRow> EventsBefore,
    IReadOnlyList<RetrievalEventRow> EventsAfter,
    double SimilarityThreshold,
    string? BeforeResolvedId,
    string? AfterResolvedId);

/// <summary>Test seam.</summary>
public delegate EvalCompareInputs EvalCompareInputProvider(
    string beforeRef, string afterRef, int days);

public sealed class EvalCompareHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "baseline": {"type":"string","description":"Snapshot name/id to compare against (alias: before)"},
            "before": {"type":"string","description":"Snapshot name/id to compare against"},
            "after": {"type":"string","description":"Snapshot name/id to compare (default 'latest')"},
            "days": {"type":"number","description":"Window in days (default 30)"}
          }
        }
        """).RootElement.Clone();

    private readonly EvalCompareInputProvider? _provider;

    public EvalCompareHandler() { _provider = null; }

    /// <summary>Test/composition seam.</summary>
    public EvalCompareHandler(EvalCompareInputProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "eval_compare";
    public string Description => "Compare retrieval metrics between two config snapshots";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("eval_compare requires arguments object");

        var args = arguments.Value;
        string? before = null;
        if (args.TryGetProperty("baseline", out var blEl) && blEl.ValueKind == JsonValueKind.String)
            before = blEl.GetString();
        else if (args.TryGetProperty("before", out var bEl) && bEl.ValueKind == JsonValueKind.String)
            before = bEl.GetString();
        if (string.IsNullOrEmpty(before))
            throw new ArgumentException("baseline is required");

        string after = "latest";
        if (args.TryGetProperty("after", out var aEl) && aEl.ValueKind == JsonValueKind.String)
        {
            var s = aEl.GetString();
            if (!string.IsNullOrEmpty(s)) after = s;
        }

        int days = 30;
        if (args.TryGetProperty("days", out var dEl))
        {
            if (dEl.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("days must be a number");
            days = dEl.GetInt32();
            if (days <= 0) throw new ArgumentException("days must be positive");
        }

        ct.ThrowIfCancellationRequested();

        var provider = _provider ?? BuildProductionProvider();
        var inputs = provider(before, after, days);

        if (inputs.BeforeResolvedId is null)
            throw new ArgumentException($"could not resolve baseline snapshot '{before}'");
        if (inputs.AfterResolvedId is null)
            throw new ArgumentException($"could not resolve after snapshot '{after}'");

        var result = ComparisonMetrics.Compute(
            inputs.EventsBefore, inputs.EventsAfter, inputs.SimilarityThreshold);

        var regressions = new EvalCompareQueryDiffDto[result.QueryDiff.Regressions.Count];
        for (int i = 0; i < regressions.Length; i++)
        {
            var r = result.QueryDiff.Regressions[i];
            regressions[i] = new EvalCompareQueryDiffDto(
                QueryText: r.QueryText,
                BeforeOutcome: r.BeforeOutcome,
                AfterOutcome: r.AfterOutcome,
                BeforeScore: r.BeforeScore,
                AfterScore: r.AfterScore);
        }
        var improvements = new EvalCompareQueryDiffDto[result.QueryDiff.Improvements.Count];
        for (int i = 0; i < improvements.Length; i++)
        {
            var r = result.QueryDiff.Improvements[i];
            improvements[i] = new EvalCompareQueryDiffDto(
                QueryText: r.QueryText,
                BeforeOutcome: r.BeforeOutcome,
                AfterOutcome: r.AfterOutcome,
                BeforeScore: r.BeforeScore,
                AfterScore: r.AfterScore);
        }

        var dto = new EvalCompareResultDto(
            BeforeId: inputs.BeforeResolvedId,
            AfterId: inputs.AfterResolvedId,
            Deltas: new EvalCompareDeltasDto(
                Precision: result.Deltas.Precision,
                HitRate: result.Deltas.HitRate,
                Mrr: result.Deltas.Mrr,
                MissRate: result.Deltas.MissRate,
                AvgLatencyMs: result.Deltas.AvgLatencyMs),
            Regressions: regressions,
            Improvements: improvements,
            Warning: result.Warning);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.EvalCompareResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static EvalCompareInputProvider BuildProductionProvider()
    {
        return (beforeRef, afterRef, days) =>
        {
            var loader = new ConfigLoader();
            var cfg = loader.LoadEffectiveConfig();
            var threshold = cfg.Tiers.Warm.SimilarityThreshold;

            var dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                var snapshots = new ConfigSnapshotStore(conn);
                var beforeId = snapshots.ResolveRef(beforeRef);
                var afterId = snapshots.ResolveRef(afterRef);
                if (beforeId is null || afterId is null)
                {
                    return new EvalCompareInputs(
                        Array.Empty<RetrievalEventRow>(),
                        Array.Empty<RetrievalEventRow>(),
                        threshold,
                        beforeId,
                        afterId);
                }
                var log = new RetrievalEventLog(conn);
                var eventsBefore = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: beforeId, Days: days));
                var eventsAfter = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: afterId, Days: days));
                return new EvalCompareInputs(eventsBefore, eventsAfter, threshold, beforeId, afterId);
            }
            finally
            {
                conn.Dispose();
            }
        };
    }
}
