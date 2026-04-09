// src/TotalRecall.Cli/Commands/Memory/HistoryCommand.cs
//
// Plan 5 Task 5.5 — `total-recall memory history [--limit N] [--json]`.
// Ports the `memory_history` tool at src-ts/tools/extra-tools.ts:238-277.
// Read-only on compaction_log; no embedder / vector search needed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Cli.Internal;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class HistoryCommand : ICliCommand
{
    // Test seam — inject a reader to avoid touching SQLite.
    private readonly ICompactionLogReader? _reader;

    public HistoryCommand() { }

    public HistoryCommand(ICompactionLogReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public string Name => "history";
    public string? Group => "memory";
    public string Description => "Show recent compaction movements";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        int limit = 20;
        bool emitJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json":
                    emitJson = true;
                    break;
                case "--limit":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory history: --limit requires a value");
                        return 2;
                    }
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
                    {
                        Console.Error.WriteLine($"memory history: invalid --limit '{args[i]}'");
                        return 2;
                    }
                    if (limit < 1 || limit > 1000)
                    {
                        Console.Error.WriteLine("memory history: --limit must be between 1 and 1000");
                        return 2;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"memory history: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        ICompactionLogReader reader;
        MsSqliteConnection? owned = null;
        try
        {
            if (_reader is not null)
            {
                reader = _reader;
            }
            else
            {
                var dbPath = ConfigLoader.GetDbPath();
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                reader = new CompactionLog(owned);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory history: failed to open db: {ex.Message}");
            return 1;
        }

        try
        {
            var movements = reader.GetRecentMovements(limit);
            if (emitJson)
            {
                Console.Out.WriteLine(SerializeJson(movements));
            }
            else if (movements.Count == 0)
            {
                Console.Out.WriteLine("(no compactions yet)");
            }
            else
            {
                Render(movements);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory history: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    // ---------- rendering ----------

    private static void Render(IReadOnlyList<CompactionMovementRow> movements)
    {
        var table = new Table().Title("[bold]memory history[/]");
        table.AddColumn("Timestamp");
        table.AddColumn("Reason");
        table.AddColumn("Source \u2192 Target");
        table.AddColumn(new TableColumn("# Src").RightAligned());
        table.AddColumn("Target ID");
        table.AddColumn("Session");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var m in movements)
        {
            table.AddRow(
                Markup.Escape(FormatTimestamp(m.Timestamp, nowMs)),
                Markup.Escape(m.Reason),
                Markup.Escape($"{m.SourceTier} \u2192 {m.TargetTier ?? "(none)"}"),
                m.SourceEntryIds.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(TruncateId(m.TargetEntryId)),
                Markup.Escape(TruncateId(m.SessionId)));
        }

        AnsiConsole.Write(table);
    }

    private static string FormatTimestamp(long unixMs, long nowMs)
    {
        var iso = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $"{iso} ({FormatRelative(nowMs - unixMs)})";
    }

    private static string FormatRelative(long deltaMs)
    {
        if (deltaMs < 0) deltaMs = 0;
        var sec = deltaMs / 1000;
        if (sec < 60) return $"{sec}s ago";
        var min = sec / 60;
        if (min < 60) return $"{min}m ago";
        var hr = min / 60;
        if (hr < 24) return $"{hr}h ago";
        var days = hr / 24;
        return $"{days}d ago";
    }

    private static string TruncateId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(none)";
        if (id.Length <= 8) return id;
        return id.Substring(0, 8) + "\u2026";
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(IReadOnlyList<CompactionMovementRow> movements)
    {
        var sb = new StringBuilder();
        sb.Append("{\"movements\":[");
        for (int i = 0; i < movements.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendMovement(sb, movements[i]);
        }
        sb.Append("],\"count\":");
        sb.Append(movements.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendMovement(StringBuilder sb, CompactionMovementRow m)
    {
        sb.Append('{');
        AppendStringField(sb, "id", m.Id); sb.Append(',');
        AppendIntField(sb, "timestamp", m.Timestamp); sb.Append(',');
        AppendNullableStringField(sb, "session_id", m.SessionId); sb.Append(',');
        AppendStringField(sb, "source_tier", m.SourceTier); sb.Append(',');
        AppendNullableStringField(sb, "target_tier", m.TargetTier); sb.Append(',');

        sb.Append("\"source_entry_ids\":[");
        for (int j = 0; j < m.SourceEntryIds.Count; j++)
        {
            if (j > 0) sb.Append(',');
            AppendString(sb, m.SourceEntryIds[j]);
        }
        sb.Append("],");

        AppendNullableStringField(sb, "target_entry_id", m.TargetEntryId); sb.Append(',');
        AppendStringField(sb, "reason", m.Reason); sb.Append(',');

        sb.Append("\"decay_scores\":{");
        var first = true;
        foreach (var kvp in m.DecayScores)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendString(sb, kvp.Key);
            sb.Append(':');
            sb.Append(kvp.Value.ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append('}');
        sb.Append('}');
    }

    private static void AppendStringField(StringBuilder sb, string name, string value)
    {
        AppendString(sb, name);
        sb.Append(':');
        AppendString(sb, value);
    }

    private static void AppendNullableStringField(StringBuilder sb, string name, string? value)
    {
        AppendString(sb, name);
        sb.Append(':');
        if (value is null) sb.Append("null");
        else AppendString(sb, value);
    }

    private static void AppendIntField(StringBuilder sb, string name, long value)
    {
        AppendString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendString(StringBuilder sb, string s) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory history [--limit N] [--json]");
    }
}
