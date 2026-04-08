// src/TotalRecall.Cli/Commands/Eval/ReportCommand.cs
//
// Plan 5 Task 5.3a — `total-recall eval report`. Reads retrieval_events +
// compaction_log over a configurable window and renders a multi-section
// metrics dashboard via Spectre.Console.Table. The --json path emits a
// hand-rolled JSON payload (no source-gen context) so we don't pollute
// Server.JsonContext for a CLI-only consumer; the shape is small and the
// emission is one-shot.
//
// Production constructor walks ConfigLoader -> data dir -> open DB -> read
// events. Test constructor accepts a delegate that returns the (events,
// compactionRows, threshold) triple so unit tests can hand in fixtures
// without spinning up SQLite.

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
/// Test seam for the read side. Returns the events / compaction rows /
/// similarity threshold needed to drive <see cref="Metrics.Compute"/>.
/// </summary>
public delegate ReportInputs ReportInputProvider(RetrievalEventQuery query);

/// <summary>Bag of inputs returned by <see cref="ReportInputProvider"/>.</summary>
public sealed record ReportInputs(
    IReadOnlyList<RetrievalEventRow> Events,
    IReadOnlyList<CompactionAnalyticsRow> CompactionRows,
    double SimilarityThreshold);

public sealed class ReportCommand : ICliCommand
{
    private readonly ReportInputProvider? _provider;

    public ReportCommand() { _provider = null; }

    /// <summary>Test/composition seam.</summary>
    public ReportCommand(ReportInputProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string Name => "report";
    public string? Group => "eval";
    public string Description => "Aggregate retrieval-event metrics over a time window.";

    public Task<int> RunAsync(string[] args)
    {
        int days = 7;
        string? sessionId = null;
        string? configSnapshotId = null;
        bool emitJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--days":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out days) || days <= 0)
                    {
                        Console.Error.WriteLine("eval report: --days requires a positive integer");
                        return Task.FromResult(2);
                    }
                    i++;
                    break;
                case "--session":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval report: --session requires a value");
                        return Task.FromResult(2);
                    }
                    sessionId = args[++i];
                    break;
                case "--config-snapshot":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("eval report: --config-snapshot requires a value");
                        return Task.FromResult(2);
                    }
                    configSnapshotId = args[++i];
                    break;
                case "--json":
                    emitJson = true;
                    break;
                default:
                    Console.Error.WriteLine($"eval report: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return Task.FromResult(2);
            }
        }

        ReportInputs inputs;
        try
        {
            var query = new RetrievalEventQuery(
                SessionId: sessionId,
                ConfigSnapshotId: configSnapshotId,
                Days: days);
            var provider = _provider ?? BuildProductionProvider();
            inputs = provider(query);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"eval report: failed to load events: {ex.Message}");
            return Task.FromResult(1);
        }

        var report = Metrics.Compute(inputs.Events, inputs.SimilarityThreshold, inputs.CompactionRows);

        if (emitJson)
        {
            Console.Out.WriteLine(SerializeJson(report));
        }
        else
        {
            RenderTables(report);
        }
        return Task.FromResult(0);
    }

    private static ReportInputProvider BuildProductionProvider()
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
                return new ReportInputs(events, compactionRows, threshold);
            }
            finally
            {
                conn.Dispose();
            }
        };
    }

    // ---------- rendering ----------

    private static void RenderTables(MetricsReport r)
    {
        var top = new Table().Title("[bold]Retrieval Metrics[/]");
        top.AddColumn("Metric");
        top.AddColumn(new TableColumn("Value").RightAligned());
        top.AddRow("Total Events", r.TotalEvents.ToString(CultureInfo.InvariantCulture));
        top.AddRow("Precision", FormatPct(r.Precision));
        top.AddRow("Hit Rate", FormatPct(r.HitRate));
        top.AddRow("Miss Rate", FormatPct(r.MissRate));
        top.AddRow("MRR", r.Mrr.ToString("F3", CultureInfo.InvariantCulture));
        top.AddRow("Avg Latency (ms)", r.AvgLatencyMs.ToString("F2", CultureInfo.InvariantCulture));
        AnsiConsole.Write(top);

        if (r.ByTier.Count > 0)
        {
            var tt = new Table().Title("[bold]By Tier[/]");
            tt.AddColumn("Tier");
            tt.AddColumn(new TableColumn("Precision").RightAligned());
            tt.AddColumn(new TableColumn("HitRate").RightAligned());
            tt.AddColumn(new TableColumn("AvgScore").RightAligned());
            tt.AddColumn(new TableColumn("Count").RightAligned());
            foreach (var kvp in r.ByTier)
            {
                tt.AddRow(
                    Markup.Escape(kvp.Key),
                    FormatPct(kvp.Value.Precision),
                    FormatPct(kvp.Value.HitRate),
                    kvp.Value.AvgScore.ToString("F3", CultureInfo.InvariantCulture),
                    kvp.Value.Count.ToString(CultureInfo.InvariantCulture));
            }
            AnsiConsole.Write(tt);
        }

        if (r.ByContentType.Count > 0)
        {
            var ct = new Table().Title("[bold]By Content Type[/]");
            ct.AddColumn("Type");
            ct.AddColumn(new TableColumn("Precision").RightAligned());
            ct.AddColumn(new TableColumn("HitRate").RightAligned());
            ct.AddColumn(new TableColumn("Count").RightAligned());
            foreach (var kvp in r.ByContentType)
            {
                ct.AddRow(
                    Markup.Escape(kvp.Key),
                    FormatPct(kvp.Value.Precision),
                    FormatPct(kvp.Value.HitRate),
                    kvp.Value.Count.ToString(CultureInfo.InvariantCulture));
            }
            AnsiConsole.Write(ct);
        }

        if (r.TopMisses.Count > 0)
        {
            var mt = new Table().Title("[bold]Top Misses[/]");
            mt.AddColumn("Query");
            mt.AddColumn(new TableColumn("TopScore").RightAligned());
            foreach (var m in r.TopMisses)
            {
                mt.AddRow(
                    Markup.Escape(m.Query),
                    m.TopScore.HasValue
                        ? m.TopScore.Value.ToString("F3", CultureInfo.InvariantCulture)
                        : "(null)");
            }
            AnsiConsole.Write(mt);
        }

        if (r.FalsePositives.Count > 0)
        {
            var fp = new Table().Title("[bold]False Positives[/]");
            fp.AddColumn("Query");
            fp.AddColumn(new TableColumn("TopScore").RightAligned());
            foreach (var m in r.FalsePositives)
            {
                fp.AddRow(
                    Markup.Escape(m.Query),
                    (m.TopScore ?? 0.0).ToString("F3", CultureInfo.InvariantCulture));
            }
            AnsiConsole.Write(fp);
        }

        var ch = new Table().Title("[bold]Compaction Health[/]");
        ch.AddColumn("Metric");
        ch.AddColumn(new TableColumn("Value").RightAligned());
        ch.AddRow("Total Compactions", r.CompactionHealth.TotalCompactions.ToString(CultureInfo.InvariantCulture));
        ch.AddRow("Avg Preservation Ratio",
            r.CompactionHealth.AvgPreservationRatio.HasValue
                ? r.CompactionHealth.AvgPreservationRatio.Value.ToString("F3", CultureInfo.InvariantCulture)
                : "(none)");
        ch.AddRow("Entries With Drift",
            r.CompactionHealth.EntriesWithDrift.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(ch);
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    /// <summary>
    /// Hand-rolled JSON serializer for the report. We do this rather than
    /// adding a JsonSerializerContext partial because the report shape only
    /// needs to be emitted from one CLI verb, and the existing
    /// Server.JsonContext is owned by Plan 4. The output is contract-locked
    /// by tests in <c>tests/TotalRecall.Cli.Tests/Commands/Eval/</c>.
    /// </summary>
    internal static string SerializeJson(MetricsReport r)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendNumberField(sb, "precision", r.Precision); sb.Append(',');
        AppendNumberField(sb, "hitRate", r.HitRate); sb.Append(',');
        AppendNumberField(sb, "missRate", r.MissRate); sb.Append(',');
        AppendNumberField(sb, "mrr", r.Mrr); sb.Append(',');
        AppendNumberField(sb, "avgLatencyMs", r.AvgLatencyMs); sb.Append(',');
        AppendIntField(sb, "totalEvents", r.TotalEvents); sb.Append(',');

        sb.Append("\"byTier\":{");
        var first = true;
        foreach (var kvp in r.ByTier)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendString(sb, kvp.Key);
            sb.Append(":{");
            AppendNumberField(sb, "precision", kvp.Value.Precision); sb.Append(',');
            AppendNumberField(sb, "hitRate", kvp.Value.HitRate); sb.Append(',');
            AppendNumberField(sb, "avgScore", kvp.Value.AvgScore); sb.Append(',');
            AppendIntField(sb, "count", kvp.Value.Count);
            sb.Append('}');
        }
        sb.Append("},");

        sb.Append("\"byContentType\":{");
        first = true;
        foreach (var kvp in r.ByContentType)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendString(sb, kvp.Key);
            sb.Append(":{");
            AppendNumberField(sb, "precision", kvp.Value.Precision); sb.Append(',');
            AppendNumberField(sb, "hitRate", kvp.Value.HitRate); sb.Append(',');
            AppendIntField(sb, "count", kvp.Value.Count);
            sb.Append('}');
        }
        sb.Append("},");

        sb.Append("\"topMisses\":");
        AppendMissArray(sb, r.TopMisses);
        sb.Append(',');
        sb.Append("\"falsePositives\":");
        AppendMissArray(sb, r.FalsePositives);
        sb.Append(',');

        sb.Append("\"compactionHealth\":{");
        AppendIntField(sb, "totalCompactions", r.CompactionHealth.TotalCompactions); sb.Append(',');
        sb.Append("\"avgPreservationRatio\":");
        if (r.CompactionHealth.AvgPreservationRatio.HasValue)
            sb.Append(r.CompactionHealth.AvgPreservationRatio.Value.ToString("R", CultureInfo.InvariantCulture));
        else
            sb.Append("null");
        sb.Append(',');
        AppendIntField(sb, "entriesWithDrift", r.CompactionHealth.EntriesWithDrift);
        sb.Append('}');

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendMissArray(StringBuilder sb, IReadOnlyList<MissEntry> misses)
    {
        sb.Append('[');
        for (int i = 0; i < misses.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var m = misses[i];
            sb.Append('{');
            AppendString(sb, "query"); sb.Append(':');
            AppendString(sb, m.Query); sb.Append(',');
            sb.Append("\"topScore\":");
            if (m.TopScore.HasValue)
                sb.Append(m.TopScore.Value.ToString("R", CultureInfo.InvariantCulture));
            else
                sb.Append("null");
            sb.Append(',');
            AppendIntField(sb, "timestamp", m.Timestamp);
            sb.Append('}');
        }
        sb.Append(']');
    }

    private static void AppendNumberField(StringBuilder sb, string name, double value)
    {
        AppendString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void AppendIntField(StringBuilder sb, string name, long value)
    {
        AppendString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
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

    private static string FormatPct(double v) =>
        (v * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall eval report [--days N] [--session ID] [--config-snapshot ID] [--json]");
    }
}
