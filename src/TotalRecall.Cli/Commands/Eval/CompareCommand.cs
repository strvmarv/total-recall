// src/TotalRecall.Cli/Commands/Eval/CompareCommand.cs
//
// Plan 5 Task 5.3b — `total-recall eval compare --before X [--after Y]`.
// Resolves before/after snapshot refs via ConfigSnapshotStore, pulls
// retrieval events per side from RetrievalEventLog, calls
// ComparisonMetrics.Compute, and renders deltas tables + regressions +
// improvements via Spectre.Console. A --json mode emits an AOT-safe
// hand-rolled JSON blob (same pattern as ReportCommand).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Eval;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Cli.Commands.Eval;

/// <summary>
/// Inputs the <see cref="CompareCommand"/> needs to produce a
/// <see cref="ComparisonResult"/>. Tests fake this directly; production
/// constructs it from SqliteStore + ConfigSnapshotStore + RetrievalEventLog.
/// </summary>
public sealed record CompareInputs(
    IReadOnlyList<RetrievalEventRow> EventsBefore,
    IReadOnlyList<RetrievalEventRow> EventsAfter,
    double SimilarityThreshold,
    string? BeforeResolvedId,
    string? AfterResolvedId,
    IReadOnlyList<ConfigSnapshotRow> RecentSnapshots);

/// <summary>
/// Test seam for <see cref="CompareCommand"/>. Given the raw (before,
/// after, days) flags, returns either an error message (to print + exit 1)
/// or a populated <see cref="CompareInputs"/>.
/// </summary>
public delegate CompareInputs CompareInputProvider(string beforeRef, string afterRef, int days);

public sealed class CompareCommand : ICliCommand
{
    private readonly CompareInputProvider? _provider;

    public CompareCommand() { _provider = null; }

    public CompareCommand(CompareInputProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "compare";
    public string? Group => "eval";
    public string Description => "Compare retrieval metrics between two config snapshots.";

    public Task<int> RunAsync(string[] args)
    {
        string? before = null;
        string after = "latest";
        int days = 30;
        bool emitJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--before":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval compare: --before requires a value");
                        return Task.FromResult(2);
                    }
                    before = args[++i];
                    break;
                case "--after":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval compare: --after requires a value");
                        return Task.FromResult(2);
                    }
                    after = args[++i];
                    break;
                case "--days":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || days <= 0)
                    {
                        Console.Error.WriteLine("eval compare: --days requires a positive integer");
                        return Task.FromResult(2);
                    }
                    i++;
                    break;
                case "--json":
                    emitJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"eval compare: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return Task.FromResult(2);
            }
        }

        if (string.IsNullOrEmpty(before))
        {
            Console.Error.WriteLine("eval compare: --before <nameOrId> is required");
            PrintUsage(Console.Error);
            return Task.FromResult(2);
        }

        CompareInputs inputs;
        try
        {
            var provider = _provider ?? BuildProductionProvider();
            inputs = provider(before, after, days);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"eval compare: failed to load: {ex.Message}");
            return Task.FromResult(1);
        }

        if (inputs.BeforeResolvedId is null || inputs.AfterResolvedId is null)
        {
            if (inputs.BeforeResolvedId is null)
                Console.Error.WriteLine($"eval compare: could not resolve --before '{before}'");
            if (inputs.AfterResolvedId is null)
                Console.Error.WriteLine($"eval compare: could not resolve --after '{after}'");
            PrintRecentSnapshots(inputs.RecentSnapshots);
            return Task.FromResult(1);
        }

        var result = ComparisonMetrics.Compute(inputs.EventsBefore, inputs.EventsAfter, inputs.SimilarityThreshold);

        if (emitJson)
        {
            Console.Out.WriteLine(SerializeJson(result, inputs.BeforeResolvedId, inputs.AfterResolvedId));
        }
        else
        {
            Render(result, inputs.BeforeResolvedId, inputs.AfterResolvedId);
        }
        return Task.FromResult(0);
    }

    private static CompareInputProvider BuildProductionProvider()
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
                var recent = snapshots.ListRecent(10);
                var beforeId = snapshots.ResolveRef(beforeRef);
                var afterId = snapshots.ResolveRef(afterRef);
                if (beforeId is null || afterId is null)
                {
                    return new CompareInputs(
                        Array.Empty<RetrievalEventRow>(),
                        Array.Empty<RetrievalEventRow>(),
                        threshold,
                        beforeId,
                        afterId,
                        recent);
                }

                var log = new RetrievalEventLog(conn);
                var eventsBefore = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: beforeId, Days: days));
                var eventsAfter = log.GetEvents(new RetrievalEventQuery(ConfigSnapshotId: afterId, Days: days));
                return new CompareInputs(eventsBefore, eventsAfter, threshold, beforeId, afterId, recent);
            }
            finally
            {
                conn.Dispose();
            }
        };
    }

    private static void PrintRecentSnapshots(IReadOnlyList<ConfigSnapshotRow> rows)
    {
        if (rows.Count == 0)
        {
            Console.Error.WriteLine("(no config_snapshots rows found)");
            return;
        }
        Console.Error.WriteLine("Recent snapshots:");
        foreach (var r in rows)
        {
            Console.Error.WriteLine(
                $"  {r.Id}  {(r.Name ?? "(unnamed)"),-20}  ts={r.Timestamp}");
        }
    }

    // ---------- rendering ----------

    private static void Render(ComparisonResult result, string beforeId, string afterId)
    {
        if (result.Warning is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.Warning)}[/]");
        }

        AnsiConsole.MarkupLine($"[bold]Before:[/] {Markup.Escape(beforeId)}  [bold]After:[/] {Markup.Escape(afterId)}");

        var top = new Table().Title("[bold]Deltas[/]");
        top.AddColumn("Metric");
        top.AddColumn(new TableColumn("Before").RightAligned());
        top.AddColumn(new TableColumn("After").RightAligned());
        top.AddColumn(new TableColumn("Δ").RightAligned());
        top.AddRow("Precision", FormatPct(result.Before.Precision), FormatPct(result.After.Precision), FormatDelta(result.Deltas.Precision, asPct: true));
        top.AddRow("Hit Rate", FormatPct(result.Before.HitRate), FormatPct(result.After.HitRate), FormatDelta(result.Deltas.HitRate, asPct: true));
        top.AddRow("MRR", result.Before.Mrr.ToString("F3", CultureInfo.InvariantCulture), result.After.Mrr.ToString("F3", CultureInfo.InvariantCulture), FormatDelta(result.Deltas.Mrr, asPct: false));
        top.AddRow("Miss Rate", FormatPct(result.Before.MissRate), FormatPct(result.After.MissRate), FormatDelta(result.Deltas.MissRate, asPct: true));
        top.AddRow("Avg Latency (ms)", result.Before.AvgLatencyMs.ToString("F2", CultureInfo.InvariantCulture), result.After.AvgLatencyMs.ToString("F2", CultureInfo.InvariantCulture), FormatDelta(result.Deltas.AvgLatencyMs, asPct: false));
        AnsiConsole.Write(top);

        if (result.ByTier.Count > 0)
        {
            var tt = new Table().Title("[bold]By Tier[/]");
            tt.AddColumn("Tier");
            tt.AddColumn(new TableColumn("Before.Precision").RightAligned());
            tt.AddColumn(new TableColumn("After.Precision").RightAligned());
            tt.AddColumn(new TableColumn("ΔPrec").RightAligned());
            tt.AddColumn(new TableColumn("Before.HitRate").RightAligned());
            tt.AddColumn(new TableColumn("After.HitRate").RightAligned());
            tt.AddColumn(new TableColumn("ΔHit").RightAligned());
            foreach (var kvp in result.ByTier)
            {
                tt.AddRow(
                    Markup.Escape(kvp.Key),
                    FormatPct(kvp.Value.Before.Precision),
                    FormatPct(kvp.Value.After.Precision),
                    FormatDelta(kvp.Value.Deltas.Precision, asPct: true),
                    FormatPct(kvp.Value.Before.HitRate),
                    FormatPct(kvp.Value.After.HitRate),
                    FormatDelta(kvp.Value.Deltas.HitRate, asPct: true));
            }
            AnsiConsole.Write(tt);
        }

        if (result.ByContentType.Count > 0)
        {
            var ct = new Table().Title("[bold]By Content Type[/]");
            ct.AddColumn("Type");
            ct.AddColumn(new TableColumn("Before.Precision").RightAligned());
            ct.AddColumn(new TableColumn("After.Precision").RightAligned());
            ct.AddColumn(new TableColumn("ΔPrec").RightAligned());
            ct.AddColumn(new TableColumn("Before.HitRate").RightAligned());
            ct.AddColumn(new TableColumn("After.HitRate").RightAligned());
            ct.AddColumn(new TableColumn("ΔHit").RightAligned());
            foreach (var kvp in result.ByContentType)
            {
                ct.AddRow(
                    Markup.Escape(kvp.Key),
                    FormatPct(kvp.Value.Before.Precision),
                    FormatPct(kvp.Value.After.Precision),
                    FormatDelta(kvp.Value.Deltas.Precision, asPct: true),
                    FormatPct(kvp.Value.Before.HitRate),
                    FormatPct(kvp.Value.After.HitRate),
                    FormatDelta(kvp.Value.Deltas.HitRate, asPct: true));
            }
            AnsiConsole.Write(ct);
        }

        if (result.QueryDiff.Regressions.Count > 0)
        {
            AppendDiffTable("Regressions", result.QueryDiff.Regressions);
        }
        if (result.QueryDiff.Improvements.Count > 0)
        {
            AppendDiffTable("Improvements", result.QueryDiff.Improvements);
        }
    }

    private static void AppendDiffTable(string title, IReadOnlyList<QueryDiffEntry> rows)
    {
        var t = new Table().Title($"[bold]{title}[/]");
        t.AddColumn("Query");
        t.AddColumn("Before Outcome");
        t.AddColumn("After Outcome");
        t.AddColumn(new TableColumn("Before Score").RightAligned());
        t.AddColumn(new TableColumn("After Score").RightAligned());
        foreach (var r in rows)
        {
            t.AddRow(
                Markup.Escape(r.QueryText),
                Markup.Escape(r.BeforeOutcome),
                Markup.Escape(r.AfterOutcome),
                r.BeforeScore.HasValue ? r.BeforeScore.Value.ToString("F3", CultureInfo.InvariantCulture) : "(null)",
                r.AfterScore.HasValue ? r.AfterScore.Value.ToString("F3", CultureInfo.InvariantCulture) : "(null)");
        }
        AnsiConsole.Write(t);
    }

    private static string FormatPct(double v) =>
        (v * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static string FormatDelta(double v, bool asPct)
    {
        var sign = v >= 0 ? "+" : "";
        if (asPct)
            return sign + (v * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";
        return sign + v.ToString("F3", CultureInfo.InvariantCulture);
    }

    // ---------- JSON emission ----------

    internal static string SerializeJson(ComparisonResult r, string beforeId, string afterId)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendStringField(sb, "beforeId", beforeId); sb.Append(',');
        AppendStringField(sb, "afterId", afterId); sb.Append(',');

        sb.Append("\"deltas\":{");
        AppendNumberField(sb, "precision", r.Deltas.Precision); sb.Append(',');
        AppendNumberField(sb, "hitRate", r.Deltas.HitRate); sb.Append(',');
        AppendNumberField(sb, "mrr", r.Deltas.Mrr); sb.Append(',');
        AppendNumberField(sb, "missRate", r.Deltas.MissRate); sb.Append(',');
        AppendNumberField(sb, "avgLatencyMs", r.Deltas.AvgLatencyMs);
        sb.Append("},");

        sb.Append("\"regressions\":");
        AppendDiffArray(sb, r.QueryDiff.Regressions);
        sb.Append(',');
        sb.Append("\"improvements\":");
        AppendDiffArray(sb, r.QueryDiff.Improvements);

        if (r.Warning is not null)
        {
            sb.Append(',');
            AppendStringField(sb, "warning", r.Warning);
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendDiffArray(StringBuilder sb, IReadOnlyList<QueryDiffEntry> rows)
    {
        sb.Append('[');
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = rows[i];
            sb.Append('{');
            AppendStringField(sb, "queryText", e.QueryText); sb.Append(',');
            AppendStringField(sb, "beforeOutcome", e.BeforeOutcome); sb.Append(',');
            AppendStringField(sb, "afterOutcome", e.AfterOutcome); sb.Append(',');
            sb.Append("\"beforeScore\":");
            if (e.BeforeScore.HasValue) sb.Append(e.BeforeScore.Value.ToString("R", CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append(',');
            sb.Append("\"afterScore\":");
            if (e.AfterScore.HasValue) sb.Append(e.AfterScore.Value.ToString("R", CultureInfo.InvariantCulture));
            else sb.Append("null");
            sb.Append('}');
        }
        sb.Append(']');
    }

    private static void AppendNumberField(StringBuilder sb, string name, double value)
    {
        sb.Append('"').Append(name).Append("\":");
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void AppendStringField(StringBuilder sb, string name, string value)
    {
        sb.Append('"').Append(name).Append("\":");
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall eval compare --before <nameOrId> [--after <nameOrId>] [--days N] [--json]");
    }
}
