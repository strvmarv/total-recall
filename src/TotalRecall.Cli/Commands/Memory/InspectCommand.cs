// src/TotalRecall.Cli/Commands/Memory/InspectCommand.cs
//
// Plan 5 Task 5.4 — `total-recall memory inspect <id> [--json]`. Ports
// src-ts/memory/get.ts's read path into a CLI verb that renders the full
// field set for a single entry. Sweeps all 6 (tier, type) tables to
// locate the entry (no --tier flag required).
//
// Two rendering modes:
//   * default: Spectre.Console table (key/value) + optional metadata dump.
//   * --json:  hand-rolled JSON matching the ReportCommand/CompareCommand
//              style so we remain AOT-clean without touching Server.JsonContext.

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Spectre.Console;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class InspectCommand : ICliCommand
{
    private readonly ISqliteStore? _store;

    public InspectCommand() { }

    // Test/composition seam.
    public InspectCommand(ISqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "inspect";
    public string? Group => "memory";
    public string Description => "Show full details for a memory or knowledge entry";

    public Task<int> RunAsync(string[] args)
    {
        return Task.FromResult(Execute(args));
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
                        Console.Error.WriteLine($"memory inspect: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (id is not null)
                    {
                        Console.Error.WriteLine($"memory inspect: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    id = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            Console.Error.WriteLine("memory inspect: <id> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        ISqliteStore store;
        MsSqliteConnection? owned = null;
        try
        {
            if (_store is not null)
            {
                store = _store;
            }
            else
            {
                var dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory inspect: failed to open db: {ex.Message}");
            return 1;
        }

        try
        {
            var located = MoveHelpers.Locate(store, id);
            if (located is null)
            {
                Console.Error.WriteLine($"memory inspect: entry {id} not found");
                return 1;
            }

            var (tier, type, entry) = located.Value;
            if (emitJson)
            {
                Console.Out.WriteLine(SerializeJson(tier, type, entry));
            }
            else
            {
                Render(tier, type, entry);
            }
            // TODO(Plan 5.5): enrich with lineage when the log reader
            // exposes ancestry lookups keyed by entry id.
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory inspect: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    // ---------- rendering ----------

    private static void Render(Tier tier, ContentType type, Entry e)
    {
        var table = new Table().Title($"[bold]memory inspect[/] [dim]{Markup.Escape(e.Id)}[/]");
        table.AddColumn("Field");
        table.AddColumn("Value");

        void Row(string k, string v) => table.AddRow(Markup.Escape(k), Markup.Escape(v));

        Row("id", e.Id);
        Row("tier", TierNames.TierName(tier));
        Row("content_type", TierNames.ContentTypeName(type));
        Row("content", Truncate(e.Content, 500));
        Row("summary", OptString(e.Summary) ?? "(none)");
        Row("source", OptString(e.Source) ?? "(none)");
        Row("source_tool", SourceToolName(e.SourceTool));
        Row("project", OptString(e.Project) ?? "(none)");
        Row("tags", FormatTags(e.Tags));
        Row("created_at", FormatTimestamp(e.CreatedAt));
        Row("updated_at", FormatTimestamp(e.UpdatedAt));
        Row("last_accessed_at", FormatTimestamp(e.LastAccessedAt));
        Row("access_count", e.AccessCount.ToString(CultureInfo.InvariantCulture));
        Row("decay_score", e.DecayScore.ToString("F4", CultureInfo.InvariantCulture));
        Row("parent_id", OptString(e.ParentId) ?? "(none)");
        Row("collection_id", OptString(e.CollectionId) ?? "(none)");
        Row("metadata", string.IsNullOrEmpty(e.MetadataJson) ? "(none)" : e.MetadataJson);

        AnsiConsole.Write(table);
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var extra = s.Length - max;
        return s.Substring(0, max) + $"... [{extra} more chars]";
    }

    private static string FormatTimestamp(long unixMs)
    {
        // TS stores unix milliseconds; render ISO-8601 UTC + raw ms for debugging.
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        var iso = dto.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return $"{iso} ({unixMs.ToString(CultureInfo.InvariantCulture)})";
    }

    private static string FormatTags(FSharpList<string> tags)
    {
        var arr = ListModule.ToArray(tags);
        if (arr.Length == 0) return "[]";
        return "[" + string.Join(", ", arr) + "]";
    }

    private static string SourceToolName(FSharpOption<SourceTool> opt)
    {
        if (!FSharpOption<SourceTool>.get_IsSome(opt)) return "(none)";
        var t = opt.Value;
        return t.IsClaudeCode ? "claude-code"
             : t.IsCopilotCli ? "copilot-cli"
             : t.IsOpencode ? "opencode"
             : t.IsCursor ? "cursor"
             : t.IsCline ? "cline"
             : t.IsHermes ? "hermes"
             : "manual";
    }

    private static string? OptString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : null;

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(Tier tier, ContentType type, Entry e)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendStringField(sb, "id", e.Id); sb.Append(',');
        AppendStringField(sb, "tier", TierNames.TierName(tier)); sb.Append(',');
        AppendStringField(sb, "content_type", TierNames.ContentTypeName(type)); sb.Append(',');
        AppendStringField(sb, "content", e.Content); sb.Append(',');
        AppendNullableStringField(sb, "summary", OptString(e.Summary)); sb.Append(',');
        AppendNullableStringField(sb, "source", OptString(e.Source)); sb.Append(',');
        AppendNullableStringField(sb, "source_tool", SourceToolNameOrNull(e.SourceTool)); sb.Append(',');
        AppendNullableStringField(sb, "project", OptString(e.Project)); sb.Append(',');

        sb.Append("\"tags\":[");
        var tagArr = ListModule.ToArray(e.Tags);
        for (int i = 0; i < tagArr.Length; i++)
        {
            if (i > 0) sb.Append(',');
            AppendString(sb, tagArr[i]);
        }
        sb.Append("],");

        AppendIntField(sb, "created_at", e.CreatedAt); sb.Append(',');
        AppendIntField(sb, "updated_at", e.UpdatedAt); sb.Append(',');
        AppendIntField(sb, "last_accessed_at", e.LastAccessedAt); sb.Append(',');
        AppendStringField(sb, "created_at_iso", IsoString(e.CreatedAt)); sb.Append(',');
        AppendStringField(sb, "updated_at_iso", IsoString(e.UpdatedAt)); sb.Append(',');
        AppendStringField(sb, "last_accessed_at_iso", IsoString(e.LastAccessedAt)); sb.Append(',');
        AppendIntField(sb, "access_count", e.AccessCount); sb.Append(',');
        AppendNumberField(sb, "decay_score", e.DecayScore); sb.Append(',');
        AppendNullableStringField(sb, "parent_id", OptString(e.ParentId)); sb.Append(',');
        AppendNullableStringField(sb, "collection_id", OptString(e.CollectionId)); sb.Append(',');
        AppendStringField(sb, "metadata", e.MetadataJson ?? "");
        sb.Append('}');
        return sb.ToString();
    }

    private static string? SourceToolNameOrNull(FSharpOption<SourceTool> opt)
    {
        if (!FSharpOption<SourceTool>.get_IsSome(opt)) return null;
        var t = opt.Value;
        return t.IsClaudeCode ? "claude-code"
             : t.IsCopilotCli ? "copilot-cli"
             : t.IsOpencode ? "opencode"
             : t.IsCursor ? "cursor"
             : t.IsCline ? "cline"
             : t.IsHermes ? "hermes"
             : "manual";
    }

    private static string IsoString(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

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

    private static void AppendNumberField(StringBuilder sb, string name, double value)
    {
        AppendString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
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

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory inspect <id> [--json]");
    }
}
