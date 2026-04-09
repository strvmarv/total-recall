// src/TotalRecall.Server/Handlers/EvalReportHandler.cs
//
// Plan 6 Task 6.0c — ports `total-recall eval report` to MCP. Thin JSON-args
// adapter over Metrics.Compute. Args:
//   { days? (default 7), session?, config_snapshot?, threshold? }
// The production seam resolves the similarity threshold from the effective
// config (mirroring CLI ReportCommand); tests inject a delegate that returns
// fixtures directly.

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

// NOTE: Server must not reference Cli. The seam types below are declared in
// this file instead of reusing the CLI ones. See EvalReportInputs /
// EvalReportInputProvider just above the handler class.

namespace TotalRecall.Server.Handlers;

/// <summary>
/// Bag of inputs for <see cref="EvalReportHandler"/>. Mirrors the CLI
/// <c>ReportInputs</c> shape but lives in Server so we don't take a Cli
/// project reference.
/// </summary>
public sealed record EvalReportInputs(
    IReadOnlyList<RetrievalEventRow> Events,
    IReadOnlyList<CompactionAnalyticsRow> CompactionRows,
    double SimilarityThreshold);

/// <summary>Test seam for <see cref="EvalReportHandler"/>.</summary>
public delegate EvalReportInputs EvalReportInputProvider(RetrievalEventQuery query);

public sealed class EvalReportHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "days": {"type":"number","description":"Window in days (default 7)"},
            "session": {"type":"string","description":"Filter by session id"},
            "config_snapshot": {"type":"string","description":"Filter by config snapshot id"},
            "threshold": {"type":"number","description":"Similarity threshold override"}
          }
        }
        """).RootElement.Clone();

    private readonly EvalReportInputProvider? _provider;

    public EvalReportHandler() { _provider = null; }

    /// <summary>Test/composition seam.</summary>
    public EvalReportHandler(EvalReportInputProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "eval_report";
    public string Description => "Aggregate retrieval-event metrics over a time window";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        int days = 7;
        string? sessionId = null;
        string? configSnapshotId = null;
        double? thresholdOverride = null;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            if (args.TryGetProperty("days", out var dEl))
            {
                if (dEl.ValueKind != JsonValueKind.Number)
                    throw new ArgumentException("days must be a number");
                days = dEl.GetInt32();
                if (days <= 0) throw new ArgumentException("days must be positive");
            }
            if (args.TryGetProperty("session", out var sEl) && sEl.ValueKind == JsonValueKind.String)
                sessionId = sEl.GetString();
            if (args.TryGetProperty("config_snapshot", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                configSnapshotId = cEl.GetString();
            if (args.TryGetProperty("threshold", out var tEl))
            {
                if (tEl.ValueKind != JsonValueKind.Number)
                    throw new ArgumentException("threshold must be a number");
                thresholdOverride = tEl.GetDouble();
            }
        }

        ct.ThrowIfCancellationRequested();

        var query = new RetrievalEventQuery(
            SessionId: sessionId,
            ConfigSnapshotId: configSnapshotId,
            Days: days);
        var provider = _provider ?? BuildProductionProvider();
        var inputs = provider(query);
        var threshold = thresholdOverride ?? inputs.SimilarityThreshold;

        var report = Metrics.Compute(inputs.Events, threshold, inputs.CompactionRows);

        var tierMap = new Dictionary<string, EvalReportTierDto>(StringComparer.Ordinal);
        foreach (var kvp in report.ByTier)
        {
            tierMap[kvp.Key] = new EvalReportTierDto(
                Precision: kvp.Value.Precision,
                HitRate: kvp.Value.HitRate,
                AvgScore: kvp.Value.AvgScore,
                Count: kvp.Value.Count);
        }
        var ctMap = new Dictionary<string, EvalReportContentTypeDto>(StringComparer.Ordinal);
        foreach (var kvp in report.ByContentType)
        {
            ctMap[kvp.Key] = new EvalReportContentTypeDto(
                Precision: kvp.Value.Precision,
                HitRate: kvp.Value.HitRate,
                Count: kvp.Value.Count);
        }
        var topMisses = new EvalReportMissDto[report.TopMisses.Count];
        for (int i = 0; i < topMisses.Length; i++)
            topMisses[i] = new EvalReportMissDto(report.TopMisses[i].Query, report.TopMisses[i].TopScore, report.TopMisses[i].Timestamp);
        var fps = new EvalReportMissDto[report.FalsePositives.Count];
        for (int i = 0; i < fps.Length; i++)
            fps[i] = new EvalReportMissDto(report.FalsePositives[i].Query, report.FalsePositives[i].TopScore, report.FalsePositives[i].Timestamp);

        var dto = new EvalReportResultDto(
            Precision: report.Precision,
            HitRate: report.HitRate,
            MissRate: report.MissRate,
            Mrr: report.Mrr,
            AvgLatencyMs: report.AvgLatencyMs,
            TotalEvents: report.TotalEvents,
            ByTier: tierMap,
            ByContentType: ctMap,
            TopMisses: topMisses,
            FalsePositives: fps,
            CompactionHealth: new EvalReportCompactionHealthDto(
                TotalCompactions: report.CompactionHealth.TotalCompactions,
                AvgPreservationRatio: report.CompactionHealth.AvgPreservationRatio,
                EntriesWithDrift: report.CompactionHealth.EntriesWithDrift));

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.EvalReportResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static EvalReportInputProvider BuildProductionProvider()
    {
        return query =>
        {
            var loader = new ConfigLoader();
            var cfg = loader.LoadEffectiveConfig();
            var threshold = cfg.Tiers.Warm.SimilarityThreshold;

            var dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
            var conn = SqliteConnection.Open(dbPath);
            try
            {
                MigrationRunner.RunMigrations(conn);
                var eventsLog = new RetrievalEventLog(conn);
                var compactionLog = new CompactionLog(conn);
                var events = eventsLog.GetEvents(query);
                var compactionRows = compactionLog.GetAllForAnalytics();
                return new EvalReportInputs(events, compactionRows, threshold);
            }
            finally
            {
                conn.Dispose();
            }
        };
    }
}
