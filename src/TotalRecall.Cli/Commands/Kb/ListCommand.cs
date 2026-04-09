// src/TotalRecall.Cli/Commands/Kb/ListCommand.cs
//
// Plan 5 Task 5.7 — `total-recall kb list [--json]`. Ports
// src-ts/tools/kb-tools.ts:193-196 (kb_list_collections), delegating to
// ISqliteStore.ListByMetadata({type:"collection"}).
//
// For each collection we compute document/chunk counts by doing ONE sweep
// of (Cold, Knowledge) via List() and grouping by CollectionId — avoids
// the O(N*M) naive approach that would re-scan the table per collection.
// Document count = rows with CollectionId == id && ParentId == null.
// Chunk count    = rows with CollectionId == id && ParentId != null.
//
// Two rendering modes:
//   * default: Spectre.Console table (Name | ID | Documents | Chunks | Source).
//   * --json:  hand-rolled JSON array, AOT-safe (no source generators).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Spectre.Console;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Kb;

public sealed class ListCommand : ICliCommand
{
    private readonly ISqliteStore? _store;
    private readonly TextWriter? _out;

    public ListCommand() { }

    // Test/composition seam.
    public ListCommand(ISqliteStore store, TextWriter output)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "list";
    public string? Group => "kb";
    public string Description => "List knowledge base collections";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
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
                    Console.Error.WriteLine($"kb list: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
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
                var dbPath = ConfigLoader.GetDbPath();
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
            }
        }
        catch (Exception ex)
        {
            ExceptionLogger.LogChain("kb list: failed to open db", ex);
            return 1;
        }

        try
        {
            var filter = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "collection",
            };
            var collections = store.ListByMetadata(Tier.Cold, ContentType.Knowledge, filter, null);

            // Single sweep of cold_knowledge to avoid O(N*M) re-scan per collection.
            var all = store.List(Tier.Cold, ContentType.Knowledge, null);
            var docCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var chunkCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in all)
            {
                if (!FSharpOption<string>.get_IsSome(e.CollectionId)) continue;
                var cid = e.CollectionId.Value;
                if (FSharpOption<string>.get_IsSome(e.ParentId))
                {
                    chunkCounts[cid] = (chunkCounts.TryGetValue(cid, out var c) ? c : 0) + 1;
                }
                else
                {
                    docCounts[cid] = (docCounts.TryGetValue(cid, out var c) ? c : 0) + 1;
                }
            }

            var rows = new List<Row>();
            foreach (var e in collections)
            {
                var name = ExtractMetadataString(e.MetadataJson, "name") ?? "(unnamed)";
                var source = ExtractMetadataString(e.MetadataJson, "source_path");
                var docs = docCounts.TryGetValue(e.Id, out var d) ? d : 0;
                var chunks = chunkCounts.TryGetValue(e.Id, out var c2) ? c2 : 0;
                rows.Add(new Row(e.Id, name, docs, chunks, source));
            }

            if (emitJson)
            {
                var json = SerializeJson(rows);
                if (_out is not null) _out.WriteLine(json);
                else Console.Out.WriteLine(json);
                return 0;
            }

            if (rows.Count == 0)
            {
                if (_out is not null) _out.WriteLine("(no collections)");
                else Console.Out.WriteLine("(no collections)");
                return 0;
            }

            Render(rows);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"kb list: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    internal readonly record struct Row(string Id, string Name, int Docs, int Chunks, string? SourcePath);

    private static void Render(List<Row> rows)
    {
        var table = new Table().Title("[bold]kb collections[/]");
        table.AddColumn("Name");
        table.AddColumn("ID");
        table.AddColumn(new TableColumn("Documents").RightAligned());
        table.AddColumn(new TableColumn("Chunks").RightAligned());
        table.AddColumn("Source");
        foreach (var r in rows)
        {
            var shortId = r.Id.Length >= 8 ? r.Id.Substring(0, 8) : r.Id;
            var src = TruncateMiddle(r.SourcePath ?? "(none)", 40);
            table.AddRow(
                Markup.Escape(r.Name),
                Markup.Escape(shortId),
                r.Docs.ToString(CultureInfo.InvariantCulture),
                r.Chunks.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(src));
        }
        AnsiConsole.Write(table);
    }

    private static string TruncateMiddle(string s, int max)
    {
        if (s.Length <= max) return s;
        if (max <= 3) return s.Substring(0, max);
        return s.Substring(0, max - 3) + "...";
    }

    private static string? ExtractMetadataString(string? json, string propName)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(propName, out var prop)) return null;
            if (prop.ValueKind != JsonValueKind.String) return null;
            return prop.GetString();
        }
        catch
        {
            return null;
        }
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var r = rows[i];
            sb.Append('{');
            AppendStringField(sb, "id", r.Id); sb.Append(',');
            AppendStringField(sb, "name", r.Name); sb.Append(',');
            AppendString(sb, "documents"); sb.Append(':');
            sb.Append(r.Docs.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            AppendString(sb, "chunks"); sb.Append(':');
            sb.Append(r.Chunks.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            AppendString(sb, "source_path"); sb.Append(':');
            if (r.SourcePath is null) sb.Append("null");
            else AppendString(sb, r.SourcePath);
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendStringField(StringBuilder sb, string name, string value)
    {
        AppendString(sb, name);
        sb.Append(':');
        AppendString(sb, value);
    }

    private static void AppendString(StringBuilder sb, string s) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall kb list [--json]");
    }
}
