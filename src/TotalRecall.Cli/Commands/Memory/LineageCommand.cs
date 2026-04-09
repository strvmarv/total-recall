// src/TotalRecall.Cli/Commands/Memory/LineageCommand.cs
//
// Plan 5 Task 5.5 — `total-recall memory lineage <id> [--json]`.
// Ports buildLineage() at src-ts/tools/extra-tools.ts:129-164 and the
// handler at :280-292. Recurses on compaction_log target/source links,
// depth-capped at 10 to match TS behavior (handles cycles defensively).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Cli.Internal;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class LineageCommand : ICliCommand
{
    // Max recursion depth — matches src-ts/tools/extra-tools.ts:134.
    internal const int MaxDepth = 10;

    private readonly ICompactionLogReader? _reader;

    public LineageCommand() { }

    public LineageCommand(ICompactionLogReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public string Name => "lineage";
    public string? Group => "memory";
    public string Description => "Show compaction ancestry for an entry";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    /// <summary>
    /// In-memory lineage tree node. Mirrors the TS
    /// <c>LineageNode</c> interface at extra-tools.ts:119-127.
    /// Optional fields that are still null/empty are omitted from JSON.
    /// </summary>
    internal sealed class LineageNode
    {
        public required string Id { get; init; }
        public string? CompactionLogId { get; init; }
        public string? Reason { get; init; }
        public long? Timestamp { get; init; }
        public string? SourceTier { get; init; }
        public string? TargetTier { get; init; }
        public List<LineageNode>? Sources { get; init; }
    }

    private int Execute(string[] args)
    {
        string? id = null;
        bool emitJson = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json":
                    emitJson = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"memory lineage: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"memory lineage: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("memory lineage: <id> is required");
            PrintUsage(Console.Error);
            return 2;
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
            ExceptionLogger.LogChain("memory lineage: failed to open db", ex);
            return 1;
        }

        try
        {
            var tree = BuildLineage(reader, id, 0, new HashSet<string>(StringComparer.Ordinal));
            if (emitJson)
            {
                Console.Out.WriteLine(SerializeJson(tree));
            }
            else
            {
                Render(tree);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory lineage: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    // ---------- tree building ----------

    // 3-arg overload preserved for tests that don't care about the
    // visited-set plumbing. Forwards to the cycle-detecting core.
    internal static LineageNode BuildLineage(ICompactionLogReader reader, string id, int depth) =>
        BuildLineage(reader, id, depth, new HashSet<string>(StringComparer.Ordinal));

    internal static LineageNode BuildLineage(
        ICompactionLogReader reader,
        string id,
        int depth,
        HashSet<string> visited)
    {
        if (depth >= MaxDepth)
        {
            return new LineageNode { Id = id, Sources = new List<LineageNode>() };
        }

        // Cycle break: if we've already expanded this id on the current
        // walk, emit a leaf so we don't burn depth budget on a loop.
        if (!visited.Add(id))
        {
            return new LineageNode { Id = id };
        }

        var row = reader.GetByTargetEntryId(id);
        if (row is null)
        {
            return new LineageNode { Id = id };
        }

        var sources = new List<LineageNode>(row.SourceEntryIds.Count);
        foreach (var srcId in row.SourceEntryIds)
        {
            sources.Add(BuildLineage(reader, srcId, depth + 1, visited));
        }

        return new LineageNode
        {
            Id = id,
            CompactionLogId = row.Id,
            Reason = row.Reason,
            Timestamp = row.Timestamp,
            SourceTier = row.SourceTier,
            TargetTier = row.TargetTier,
            Sources = sources,
        };
    }

    // ---------- rendering ----------

    private static void Render(LineageNode root)
    {
        var tree = new Tree($"[bold]{Markup.Escape(root.Id)}[/]");
        AppendChildren(tree, root);
        AnsiConsole.Write(tree);
    }

    private static void AppendChildren(IHasTreeNodes parent, LineageNode node)
    {
        if (node.Sources is null || node.Sources.Count == 0) return;
        foreach (var src in node.Sources)
        {
            var label = FormatNodeLabel(src);
            var child = parent.AddNode(label);
            AppendChildren(child, src);
        }
    }

    private static string FormatNodeLabel(LineageNode node)
    {
        // Compacted child: show id + reason @ iso + compaction log id.
        if (node.CompactionLogId is not null)
        {
            var iso = node.Timestamp.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(node.Timestamp.Value).UtcDateTime
                    .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                : "?";
            var reason = node.Reason ?? "?";
            return $"[bold]{Markup.Escape(node.Id)}[/] [dim]({Markup.Escape(reason)} @ {iso}, log={Markup.Escape(node.CompactionLogId)})[/]";
        }
        // Leaf: just the id.
        return Markup.Escape(node.Id);
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(LineageNode root)
    {
        var sb = new StringBuilder();
        AppendNode(sb, root);
        return sb.ToString();
    }

    private static void AppendNode(StringBuilder sb, LineageNode node)
    {
        sb.Append('{');
        AppendString(sb, "id");
        sb.Append(':');
        AppendString(sb, node.Id);
        if (node.CompactionLogId is not null)
        {
            sb.Append(',');
            AppendString(sb, "compaction_log_id");
            sb.Append(':');
            AppendString(sb, node.CompactionLogId);
        }
        if (node.Reason is not null)
        {
            sb.Append(',');
            AppendString(sb, "reason");
            sb.Append(':');
            AppendString(sb, node.Reason);
        }
        if (node.Timestamp.HasValue)
        {
            sb.Append(',');
            AppendString(sb, "timestamp");
            sb.Append(':');
            sb.Append(node.Timestamp.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (node.SourceTier is not null)
        {
            sb.Append(',');
            AppendString(sb, "source_tier");
            sb.Append(':');
            AppendString(sb, node.SourceTier);
        }
        if (node.TargetTier is not null)
        {
            sb.Append(',');
            AppendString(sb, "target_tier");
            sb.Append(':');
            AppendString(sb, node.TargetTier);
        }
        if (node.Sources is not null)
        {
            sb.Append(',');
            AppendString(sb, "sources");
            sb.Append(":[");
            for (int i = 0; i < node.Sources.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendNode(sb, node.Sources[i]);
            }
            sb.Append(']');
        }
        sb.Append('}');
    }

    private static void AppendString(StringBuilder sb, string s) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory lineage <id> [--json]");
    }
}
