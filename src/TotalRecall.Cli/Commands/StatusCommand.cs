// src/TotalRecall.Cli/Commands/StatusCommand.cs
//
// Plan 5 Task 5.9 — `total-recall status [--json]`. Mirrors the MCP
// `status` tool (TotalRecall.Server.Handlers.StatusHandler) but renders a
// Spectre dashboard for interactive terminal use. The Cli project does
// NOT reference Server, so the reads are duplicated inline here against
// ISqliteStore / IConfigLoader primitives.
//
// Scope vs. StatusHandler:
//   * tierSizes         — 6 Count() calls, one per (tier, content_type).
//   * knowledgeBase     — ListByMetadata({type:"collection"}) enumeration.
//   * db                — path + size (FileInfo.Length, nullable).
//   * embedding         — model + dimensions from effective config.
//   * activity          — intentionally omitted (server stubs it to 0s).
//   * lastCompaction    — intentionally omitted (server stubs to null).
//
// Test seam: public ctor (ISqliteStore, IConfigLoader, dbPath, TextWriter).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Spectre.Console;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Json;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands;

public sealed class StatusCommand : ICliCommand
{
    private static readonly IReadOnlyDictionary<string, string> CollectionFilter =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "collection" };

    private readonly ISqliteStore? _store;
    private readonly IConfigLoader? _configLoader;
    private readonly string? _dbPath;
    private readonly TextWriter? _out;

    public StatusCommand() { }

    // Test/composition seam.
    public StatusCommand(
        ISqliteStore store,
        IConfigLoader configLoader,
        string dbPath,
        TextWriter output)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "status";
    public string? Group => null;
    public string Description => "Show a dashboard of tier sizes, KB collections, database, embedding config";

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
                    Console.Error.WriteLine($"status: unknown argument '{a}'");
                    PrintUsage(Console.Error);
                    return 2;
            }
        }

        ISqliteStore store;
        IConfigLoader configLoader;
        string dbPath;
        MsSqliteConnection? owned = null;
        try
        {
            if (_store is not null && _configLoader is not null && _dbPath is not null)
            {
                store = _store;
                configLoader = _configLoader;
                dbPath = _dbPath;
            }
            else
            {
                dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
                configLoader = new ConfigLoader();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"status: failed to open db: {ex.Message}");
            return 1;
        }

        try
        {
            var data = Gather(store, configLoader, dbPath);

            if (emitJson)
            {
                var json = SerializeJson(data);
                if (_out is not null) _out.WriteLine(json);
                else Console.Out.WriteLine(json);
                return 0;
            }

            Render(data);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"status: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    internal sealed record StatusData(
        int HotMemory,
        int HotKnowledge,
        int WarmMemory,
        int WarmKnowledge,
        int ColdMemory,
        int ColdKnowledge,
        IReadOnlyList<KbCollectionRow> Collections,
        int TotalChunks,
        string DbPath,
        long? DbSizeBytes,
        string EmbeddingModel,
        int EmbeddingDimensions);

    internal sealed record KbCollectionRow(string Id, string Name, int Chunks);

    private static StatusData Gather(ISqliteStore store, IConfigLoader configLoader, string dbPath)
    {
        int hotMem = store.Count(Tier.Hot, ContentType.Memory);
        int hotKnow = store.Count(Tier.Hot, ContentType.Knowledge);
        int warmMem = store.Count(Tier.Warm, ContentType.Memory);
        int warmKnow = store.Count(Tier.Warm, ContentType.Knowledge);
        int coldMem = store.Count(Tier.Cold, ContentType.Memory);
        int coldKnow = store.Count(Tier.Cold, ContentType.Knowledge);

        var collectionRows = store.ListByMetadata(
            Tier.Cold, ContentType.Knowledge, CollectionFilter, null);

        // Per-collection chunk counts: sweep Cold/Knowledge once and
        // group by collection_id. Matches the pattern in `kb list`.
        var chunkCountByCollection = new Dictionary<string, int>(StringComparer.Ordinal);
        var allColdKnow = store.List(Tier.Cold, ContentType.Knowledge, null);
        foreach (var e in allColdKnow)
        {
            if (Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(e.CollectionId))
            {
                var cid = e.CollectionId.Value;
                chunkCountByCollection.TryGetValue(cid, out var n);
                chunkCountByCollection[cid] = n + 1;
            }
        }

        var collections = new List<KbCollectionRow>(collectionRows.Count);
        foreach (var e in collectionRows)
        {
            var name = ExtractCollectionName(e.MetadataJson) ?? "(unnamed)";
            chunkCountByCollection.TryGetValue(e.Id, out var childCount);
            collections.Add(new KbCollectionRow(e.Id, name, childCount));
        }

        int totalChunks = coldKnow - collectionRows.Count;
        if (totalChunks < 0) totalChunks = 0;

        long? sizeBytes = null;
        try
        {
            if (File.Exists(dbPath))
            {
                sizeBytes = new FileInfo(dbPath).Length;
            }
        }
        catch (Exception ex) when (
            ex is IOException
            || ex is UnauthorizedAccessException
            || ex is System.Security.SecurityException
            || ex is ArgumentException
            || ex is PathTooLongException
            || ex is NotSupportedException)
        {
            sizeBytes = null;
        }

        var cfg = configLoader.LoadEffectiveConfig();
        var model = cfg.Embedding.Model;
        var dims = cfg.Embedding.Dimensions;

        return new StatusData(
            hotMem, hotKnow, warmMem, warmKnow, coldMem, coldKnow,
            collections, totalChunks,
            dbPath, sizeBytes,
            model, dims);
    }

    private static string? ExtractCollectionName(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("name", out var nameProp)) return null;
            return nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- rendering ----------

    private static void Render(StatusData d)
    {
        var tiers = new Table().Title("[bold]Tier Sizes[/]");
        tiers.AddColumn("Tier");
        tiers.AddColumn("Type");
        tiers.AddColumn(new TableColumn("Count").RightAligned());
        tiers.AddRow("hot", "memory", d.HotMemory.ToString(CultureInfo.InvariantCulture));
        tiers.AddRow("hot", "knowledge", d.HotKnowledge.ToString(CultureInfo.InvariantCulture));
        tiers.AddRow("warm", "memory", d.WarmMemory.ToString(CultureInfo.InvariantCulture));
        tiers.AddRow("warm", "knowledge", d.WarmKnowledge.ToString(CultureInfo.InvariantCulture));
        tiers.AddRow("cold", "memory", d.ColdMemory.ToString(CultureInfo.InvariantCulture));
        tiers.AddRow("cold", "knowledge", d.ColdKnowledge.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(tiers);

        if (d.Collections.Count == 0)
        {
            AnsiConsole.MarkupLine("[bold]Knowledge Base[/]: (no collections)");
        }
        else
        {
            var kb = new Table().Title("[bold]Knowledge Base[/]");
            kb.AddColumn("Name");
            kb.AddColumn("ID");
            kb.AddColumn(new TableColumn("Chunks").RightAligned());
            foreach (var c in d.Collections)
            {
                var shortId = c.Id.Length >= 8 ? c.Id.Substring(0, 8) : c.Id;
                kb.AddRow(
                    Markup.Escape(c.Name),
                    Markup.Escape(shortId),
                    c.Chunks.ToString(CultureInfo.InvariantCulture));
            }
            AnsiConsole.Write(kb);
        }

        var db = new Table().Title("[bold]Database[/]");
        db.AddColumn("Key");
        db.AddColumn("Value");
        db.AddRow("Path", Markup.Escape(d.DbPath));
        db.AddRow("Size", d.DbSizeBytes is null ? "missing" : FormatBytes(d.DbSizeBytes.Value));
        AnsiConsole.Write(db);

        var emb = new Table().Title("[bold]Embedding[/]");
        emb.AddColumn("Key");
        emb.AddColumn("Value");
        emb.AddRow("Model", Markup.Escape(d.EmbeddingModel));
        emb.AddRow("Dimensions", d.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.Write(emb);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return kb.ToString("F1", CultureInfo.InvariantCulture) + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
        double gb = mb / 1024.0;
        return gb.ToString("F1", CultureInfo.InvariantCulture) + " GB";
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeJson(StatusData d)
    {
        var sb = new StringBuilder();
        sb.Append('{');

        // tierSizes
        AppendString(sb, "tierSizes"); sb.Append(":{");
        AppendString(sb, "hot"); sb.Append(":{");
        AppendIntField(sb, "memory", d.HotMemory); sb.Append(',');
        AppendIntField(sb, "knowledge", d.HotKnowledge);
        sb.Append("},");
        AppendString(sb, "warm"); sb.Append(":{");
        AppendIntField(sb, "memory", d.WarmMemory); sb.Append(',');
        AppendIntField(sb, "knowledge", d.WarmKnowledge);
        sb.Append("},");
        AppendString(sb, "cold"); sb.Append(":{");
        AppendIntField(sb, "memory", d.ColdMemory); sb.Append(',');
        AppendIntField(sb, "knowledge", d.ColdKnowledge);
        sb.Append('}');
        sb.Append("},");

        // knowledgeBase
        AppendString(sb, "knowledgeBase"); sb.Append(":{");
        AppendString(sb, "collections"); sb.Append(":[");
        for (int i = 0; i < d.Collections.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var c = d.Collections[i];
            sb.Append('{');
            AppendStringField(sb, "id", c.Id); sb.Append(',');
            AppendStringField(sb, "name", c.Name); sb.Append(',');
            AppendIntField(sb, "chunks", c.Chunks);
            sb.Append('}');
        }
        sb.Append("],");
        AppendIntField(sb, "totalChunks", d.TotalChunks);
        sb.Append("},");

        // db
        AppendString(sb, "db"); sb.Append(":{");
        AppendStringField(sb, "path", d.DbPath); sb.Append(',');
        AppendString(sb, "sizeBytes"); sb.Append(':');
        if (d.DbSizeBytes is null) sb.Append("null");
        else sb.Append(d.DbSizeBytes.Value.ToString(CultureInfo.InvariantCulture));
        sb.Append("},");

        // embedding
        AppendString(sb, "embedding"); sb.Append(":{");
        AppendStringField(sb, "model", d.EmbeddingModel); sb.Append(',');
        AppendIntField(sb, "dimensions", d.EmbeddingDimensions);
        sb.Append('}');

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendStringField(StringBuilder sb, string name, string value)
    {
        AppendString(sb, name); sb.Append(':'); AppendString(sb, value);
    }

    private static void AppendIntField(StringBuilder sb, string name, int value)
    {
        AppendString(sb, name);
        sb.Append(':');
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendString(StringBuilder sb, string s) => JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall status [--json]");
    }
}
