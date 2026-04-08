// src/TotalRecall.Cli/Commands/Memory/ExportCommand.cs
//
// Plan 5 Task 5.6 — `total-recall memory export [--tiers ...] [--types ...]
// [--out path] [--pretty]`. Ports src-ts/tools/extra-tools.ts:294-337
// (memory_export). Default emits the envelope to stdout; --out writes it to
// a file. Each entry carries its originating (tier, content_type) so that
// `memory import` can restore rows to their original location.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class ExportCommand : ICliCommand
{
    private readonly ISqliteStore? _store;
    private readonly TextWriter? _out;

    public ExportCommand() { }

    // Test/composition seam: inject a store and a writer to capture JSON
    // without touching real SQLite or the filesystem.
    public ExportCommand(ISqliteStore store, TextWriter output)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "export";
    public string? Group => "memory";
    public string Description => "Export memory/knowledge entries as JSON";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        HashSet<Tier>? tierFilter = null;
        HashSet<ContentType>? typeFilter = null;
        string? outPath = null;
        bool pretty = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--tiers":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory export: --tiers requires a value");
                        return 2;
                    }
                    tierFilter = new HashSet<Tier>();
                    foreach (var token in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var t = TierNames.ParseTier(token);
                        if (t is null)
                        {
                            Console.Error.WriteLine($"memory export: invalid tier '{token}' (expected hot|warm|cold)");
                            return 2;
                        }
                        tierFilter.Add(t);
                    }
                    break;
                case "--types":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory export: --types requires a value");
                        return 2;
                    }
                    typeFilter = new HashSet<ContentType>();
                    foreach (var token in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var c = TierNames.ParseContentType(token);
                        if (c is null)
                        {
                            Console.Error.WriteLine($"memory export: invalid content type '{token}' (expected memory|knowledge)");
                            return 2;
                        }
                        typeFilter.Add(c);
                    }
                    break;
                case "--out":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("memory export: --out requires a value");
                        return 2;
                    }
                    outPath = args[++i];
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                default:
                    Console.Error.WriteLine($"memory export: unknown argument '{a}'");
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
                var dbPath = Path.Combine(ConfigLoader.GetDataDir(), "total-recall.db");
                owned = SqliteConnection.Open(dbPath);
                MigrationRunner.RunMigrations(owned);
                store = new SqliteStore(owned);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory export: failed to open db: {ex.Message}");
            return 1;
        }

        try
        {
            // Gather entries for each filtered (tier, type) pair.
            var collected = new List<(Tier Tier, ContentType Type, Entry Entry)>();
            foreach (var pair in TierNames.AllTablePairs)
            {
                if (tierFilter is not null && !tierFilter.Contains(pair.Tier)) continue;
                if (typeFilter is not null && !typeFilter.Contains(pair.Type)) continue;
                var rows = store.List(pair.Tier, pair.Type, null);
                foreach (var e in rows)
                {
                    collected.Add((pair.Tier, pair.Type, e));
                }
            }

            var exportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var json = SerializeEnvelope(collected, exportedAt, pretty);

            if (outPath is not null)
            {
                var parent = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.WriteAllText(outPath, json);
                Console.Out.WriteLine($"exported {collected.Count} entries to {outPath}");
            }
            else if (_out is not null)
            {
                _out.WriteLine(json);
            }
            else
            {
                Console.Out.WriteLine(json);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory export: failed: {ex.Message}");
            return 1;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    // ---------- JSON emission (hand-rolled, AOT-safe) ----------

    internal static string SerializeEnvelope(
        IReadOnlyList<(Tier Tier, ContentType Type, Entry Entry)> entries,
        long exportedAt,
        bool pretty)
    {
        var sb = new StringBuilder();
        var nl = pretty ? "\n" : "";
        var ind1 = pretty ? "  " : "";
        var ind2 = pretty ? "    " : "";
        var kvSep = pretty ? ": " : ":";
        var sep = pretty ? "," + nl : ",";

        sb.Append('{').Append(nl);
        sb.Append(ind1); AppendString(sb, "version"); sb.Append(kvSep); sb.Append('1'); sb.Append(sep);
        sb.Append(ind1); AppendString(sb, "exported_at"); sb.Append(kvSep);
        sb.Append(exportedAt.ToString(CultureInfo.InvariantCulture));
        sb.Append(sep);
        sb.Append(ind1); AppendString(sb, "entries"); sb.Append(kvSep); sb.Append('[');
        if (entries.Count > 0)
        {
            sb.Append(nl);
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',').Append(nl);
                AppendEntry(sb, entries[i].Tier, entries[i].Type, entries[i].Entry, pretty, ind2);
            }
            sb.Append(nl).Append(ind1);
        }
        sb.Append(']').Append(nl);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendEntry(
        StringBuilder sb, Tier tier, ContentType type, Entry e, bool pretty, string indent)
    {
        var nl = pretty ? "\n" : "";
        var kvSep = pretty ? ": " : ":";
        var sep = pretty ? "," + nl : ",";
        var ind = pretty ? indent + "  " : "";

        sb.Append(indent); sb.Append('{').Append(nl);

        sb.Append(ind); AppendString(sb, "id"); sb.Append(kvSep); AppendString(sb, e.Id); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "content"); sb.Append(kvSep); AppendString(sb, e.Content); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "summary"); sb.Append(kvSep); AppendNullableString(sb, OptString(e.Summary)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "source"); sb.Append(kvSep); AppendNullableString(sb, OptString(e.Source)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "source_tool"); sb.Append(kvSep); AppendNullableString(sb, SourceToolNameOrNull(e.SourceTool)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "project"); sb.Append(kvSep); AppendNullableString(sb, OptString(e.Project)); sb.Append(sep);

        sb.Append(ind); AppendString(sb, "tags"); sb.Append(kvSep); sb.Append('[');
        var tagArr = ListModule.ToArray(e.Tags);
        for (int i = 0; i < tagArr.Length; i++)
        {
            if (i > 0) sb.Append(pretty ? ", " : ",");
            AppendString(sb, tagArr[i]);
        }
        sb.Append(']'); sb.Append(sep);

        sb.Append(ind); AppendString(sb, "created_at"); sb.Append(kvSep); sb.Append(e.CreatedAt.ToString(CultureInfo.InvariantCulture)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "updated_at"); sb.Append(kvSep); sb.Append(e.UpdatedAt.ToString(CultureInfo.InvariantCulture)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "last_accessed_at"); sb.Append(kvSep); sb.Append(e.LastAccessedAt.ToString(CultureInfo.InvariantCulture)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "access_count"); sb.Append(kvSep); sb.Append(e.AccessCount.ToString(CultureInfo.InvariantCulture)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "decay_score"); sb.Append(kvSep); sb.Append(e.DecayScore.ToString("R", CultureInfo.InvariantCulture)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "parent_id"); sb.Append(kvSep); AppendNullableString(sb, OptString(e.ParentId)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "collection_id"); sb.Append(kvSep); AppendNullableString(sb, OptString(e.CollectionId)); sb.Append(sep);

        sb.Append(ind); AppendString(sb, "metadata"); sb.Append(kvSep);
        AppendRawMetadata(sb, e.MetadataJson);
        sb.Append(sep);

        sb.Append(ind); AppendString(sb, "tier"); sb.Append(kvSep); AppendString(sb, TierNames.TierName(tier)); sb.Append(sep);
        sb.Append(ind); AppendString(sb, "content_type"); sb.Append(kvSep); AppendString(sb, TierNames.ContentTypeName(type));

        sb.Append(nl); sb.Append(indent); sb.Append('}');
    }

    // Embed the metadata JSON verbatim. The db stores it as a JSON string;
    // emit raw. Fall back to {} if empty or malformed (validated via
    // JsonDocument.Parse — AOT-safe, no source-gen).
    private static void AppendRawMetadata(StringBuilder sb, string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            sb.Append("{}");
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            sb.Append(metadataJson);
        }
        catch
        {
            sb.Append("{}");
        }
    }

    private static string? OptString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : null;

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

    private static void AppendNullableString(StringBuilder sb, string? value)
    {
        if (value is null) sb.Append("null");
        else AppendString(sb, value);
    }

    private static void AppendString(StringBuilder sb, string s) =>
        TotalRecall.Infrastructure.Json.JsonWriter.AppendString(sb, s);

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory export [--tiers hot,warm,cold] [--types memory,knowledge] [--out path] [--pretty]");
    }
}
