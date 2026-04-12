// src/TotalRecall.Cli/Commands/UsageCommand.cs
//
// `total-recall usage [OPTIONS]` — primary CLI surface for feature A
// (visibility). Phase 1 ships the text-table output path; --json is
// Phase 2.
//
// Flags (spec §6.3):
//   --last <window>   5h|1d|7d|30d|90d|all  default: 7d
//   --by <dim>        host|project|day|model|session  default: host
//   --host <id>       filter to single host
//   --project <id>    filter to single project
//   --top <N>         limit buckets
//   --detail          break out cache_creation vs cache_read (not in Phase 1)
//
// Production constructor resolves dbPath via ConfigLoader; test
// constructor accepts an injected UsageQueryService.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Usage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands;

public sealed class UsageCommand : ICliCommand
{
    private readonly UsageQueryService? _injectedService;
    private readonly TextWriter _out;
    private readonly TextWriter _err;

    // Production ctor — opens the DB on demand so the verb doesn't
    // require db wiring at registry-build time.
    public UsageCommand() : this(null, Console.Out, Console.Error) { }

    // Test ctor — inject a seeded service, optionally redirect output.
    public UsageCommand(UsageQueryService? service, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        _injectedService = service;
        _out = stdout ?? Console.Out;
        _err = stderr ?? Console.Error;
    }

    public string Name => "usage";
    public string? Group => null;
    public string Description => "Show token usage and burn rate across hosts";

    public async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArgs(args, out var window, out var groupBy, out var hostFilter, out var projectFilter, out var topN, out var emitJson, out var error))
        {
            _err.WriteLine(error);
            return 2;
        }

        // --by session requires --last ≤ 30d because Phase 1 raw events
        // are capped at that retention (Phase 2 adds daily rollup which
        // drops session granularity past the cutoff).
        if (groupBy == GroupBy.Session && window > TimeSpan.FromDays(30))
        {
            _err.WriteLine("--by session requires --last ≤30d (raw event retention window)");
            return 2;
        }

        // Build service — either the injected one (tests) or construct
        // fresh against the resolved db path.
        UsageQueryService svc;
        MsSqliteConnection? ownedConn = null;
        if (_injectedService is not null)
        {
            svc = _injectedService;
        }
        else
        {
            var dbPath = ConfigLoader.GetDbPath();
            ownedConn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(ownedConn);
            svc = new UsageQueryService(ownedConn);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var query = new UsageQuery(
                Start: now - window,
                End: now,
                HostFilter: hostFilter,
                ProjectFilter: projectFilter,
                GroupBy: groupBy,
                TopN: topN);

            var report = svc.Query(query);
            if (emitJson)
                RenderJson(report, query);
            else
                RenderTable(report, window, groupBy);
        }
        finally
        {
            ownedConn?.Dispose();
        }

        await Task.CompletedTask;
        return 0;
    }

    // -------- argument parsing --------

    private bool TryParseArgs(
        string[] args,
        out TimeSpan window,
        out GroupBy groupBy,
        out IReadOnlyList<string>? hostFilter,
        out IReadOnlyList<string>? projectFilter,
        out int topN,
        out bool emitJson,
        out string? error)
    {
        window = TimeSpan.FromDays(7);
        groupBy = GroupBy.Host;
        hostFilter = null;
        projectFilter = null;
        topN = 0;
        emitJson = false;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--last":
                    if (i + 1 >= args.Length) { error = "--last requires a value (5h|1d|7d|30d|90d|all)"; return false; }
                    if (!TryParseWindow(args[++i], out window)) { error = $"--last: unknown window '{args[i]}' (expected 5h|1d|7d|30d|90d|all)"; return false; }
                    break;
                case "--by":
                    if (i + 1 >= args.Length) { error = "--by requires a value (host|project|day|model|session)"; return false; }
                    if (!TryParseGroupBy(args[++i], out groupBy)) { error = $"--by: unknown dimension '{args[i]}'"; return false; }
                    break;
                case "--host":
                    if (i + 1 >= args.Length) { error = "--host requires a value"; return false; }
                    hostFilter = new[] { args[++i] };
                    break;
                case "--project":
                    if (i + 1 >= args.Length) { error = "--project requires a value"; return false; }
                    projectFilter = new[] { args[++i] };
                    break;
                case "--top":
                    if (i + 1 >= args.Length) { error = "--top requires an integer"; return false; }
                    if (!int.TryParse(args[++i], out topN) || topN < 1) { error = $"--top: invalid integer '{args[i]}'"; return false; }
                    break;
                case "--detail":
                    // Phase 1 placeholder — parse but do not implement detail rendering.
                    break;
                case "--json":
                    emitJson = true;
                    break;
                default:
                    error = $"unknown argument: {a}";
                    return false;
            }
        }
        return true;
    }

    private static bool TryParseWindow(string s, out TimeSpan window)
    {
        window = default;
        switch (s)
        {
            case "5h":  window = TimeSpan.FromHours(5);    return true;
            case "1d":  window = TimeSpan.FromDays(1);     return true;
            case "7d":  window = TimeSpan.FromDays(7);     return true;
            case "30d": window = TimeSpan.FromDays(30);    return true;
            case "90d": window = TimeSpan.FromDays(90);    return true;
            case "all": window = TimeSpan.FromDays(36500); return true;
            default:    return false;
        }
    }

    private static bool TryParseGroupBy(string s, out GroupBy g)
    {
        switch (s)
        {
            case "host":    g = GroupBy.Host;    return true;
            case "project": g = GroupBy.Project; return true;
            case "day":     g = GroupBy.Day;     return true;
            case "model":   g = GroupBy.Model;   return true;
            case "session": g = GroupBy.Session; return true;
            default:        g = GroupBy.Host;    return false;
        }
    }

    // -------- rendering --------

    private void RenderTable(UsageReport report, TimeSpan window, GroupBy groupBy)
    {
        _out.WriteLine($"total-recall usage — last {FormatWindow(window)}, by {groupBy.ToString().ToLowerInvariant()}");

        if (report.Buckets.Count == 0)
        {
            _out.WriteLine("(no usage events in the selected window)");
            return;
        }

        // Simple fixed-width table — avoids Spectre.Console so test captures
        // stdout cleanly. See CliApp comment on why Spectre is reserved for
        // --version only.
        var colHeaders = new[] { groupBy.ToString().ToLowerInvariant(), "sessions", "turns", "input", "cached", "output" };
        var rows = new List<string[]>();
        foreach (var b in report.Buckets)
        {
            rows.Add(new[]
            {
                b.Key,
                b.Totals.SessionCount.ToString(),
                FormatInt(b.Totals.TurnCount),
                FormatTokens(b.Totals.InputTokens),
                FormatTokens(Combine(b.Totals.CacheCreationTokens, b.Totals.CacheReadTokens)),
                FormatTokens(b.Totals.OutputTokens),
            });
        }
        rows.Add(new[]
        {
            "total",
            report.GrandTotal.SessionCount.ToString(),
            FormatInt(report.GrandTotal.TurnCount),
            FormatTokens(report.GrandTotal.InputTokens),
            FormatTokens(Combine(report.GrandTotal.CacheCreationTokens, report.GrandTotal.CacheReadTokens)),
            FormatTokens(report.GrandTotal.OutputTokens),
        });

        RenderFixedWidthTable(colHeaders, rows);

        var tracked = report.SessionsWithFullTokenData;
        var total = tracked + report.SessionsWithPartialTokenData;
        if (total > 0)
        {
            var pct = 100.0 * tracked / total;
            _out.WriteLine();
            _out.WriteLine($"Tracked at token granularity: {tracked} of {total} sessions ({pct:F1}%)");
        }
    }

    private static long? Combine(long? a, long? b)
    {
        if (a is null && b is null) return null;
        return (a ?? 0) + (b ?? 0);
    }

    private static string FormatWindow(TimeSpan ts)
    {
        if (ts == TimeSpan.FromHours(5)) return "5 hours";
        if (ts == TimeSpan.FromDays(1))  return "1 day";
        if (ts == TimeSpan.FromDays(7))  return "7 days";
        if (ts == TimeSpan.FromDays(30)) return "30 days";
        if (ts == TimeSpan.FromDays(90)) return "90 days";
        return $"{ts.TotalDays:F0} days";
    }

    private static string FormatInt(long n) =>
        n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatTokens(long? n)
    {
        if (n is null) return "—";
        var v = n.Value;
        if (v >= 1_000_000) return $"{v / 1_000_000.0:F1}M";
        if (v >= 1_000)     return $"{v / 1_000.0:F0}k";
        return v.ToString();
    }

    private void RenderFixedWidthTable(string[] headers, List<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
            widths[i] = headers[i].Length;
        foreach (var row in rows)
            for (var i = 0; i < row.Length; i++)
                if (row[i].Length > widths[i]) widths[i] = row[i].Length;

        void Divider()
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < widths.Length; i++)
            {
                if (i > 0) sb.Append("-+-");
                sb.Append(new string('-', widths[i]));
            }
            _out.WriteLine(sb.ToString());
        }

        void Row(string[] values)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(values[i].PadRight(widths[i]));
            }
            _out.WriteLine(sb.ToString());
        }

        Row(headers);
        Divider();
        for (var i = 0; i < rows.Count - 1; i++) Row(rows[i]);
        Divider();
        Row(rows[rows.Count - 1]); // grand total
    }

    // -------- JSON rendering --------
    //
    // Delegates to UsageJsonRenderer (Infrastructure) so the `usage_status`
    // MCP tool handler produces byte-identical output. The top-level shape
    // {query, buckets[], grand_total, coverage} is the user-facing contract —
    // see spec §6.3.

    private void RenderJson(UsageReport report, UsageQuery query)
    {
        _out.WriteLine(UsageJsonRenderer.Render(report, query));
    }
}
