// src/TotalRecall.Cli/Commands/Memory/ImportCommand.cs
//
// Plan 5 Task 5.6 — `total-recall memory import <path> [--dry-run]`. Ports
// src-ts/tools/extra-tools.ts:339-451 (memory_import). Reads an export
// envelope, dedupes by existing id and by existing content (across all 6
// tables AND within the current batch), skips malformed entries, then
// re-embeds and inserts survivors. Invalid (tier, content_type) values
// fall back to (hot, memory) exactly as the TS version does.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TotalRecall.Cli.Internal;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Cli.Commands.Memory;

public sealed class ImportCommand : ICliCommand
{
    private readonly ISqliteStore? _store;
    private readonly IVectorSearch? _vec;
    private readonly IEmbedder? _embedder;
    private readonly TextWriter? _out;

    public ImportCommand() { }

    // Test/composition seam.
    public ImportCommand(ISqliteStore store, IVectorSearch vec, IEmbedder embedder, TextWriter output)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public string Name => "import";
    public string? Group => "memory";
    public string Description => "Import memory/knowledge entries from a JSON file";

    public Task<int> RunAsync(string[] args) => Task.FromResult(Execute(args));

    private int Execute(string[] args)
    {
        string? path = null;
        bool dryRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (a.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"memory import: unknown argument '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    if (path is not null)
                    {
                        Console.Error.WriteLine($"memory import: unexpected positional '{a}'");
                        PrintUsage(Console.Error);
                        return 2;
                    }
                    path = a;
                    break;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("memory import: <path> is required");
            PrintUsage(Console.Error);
            return 2;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory import: Failed to read file: {ex.Message}");
            return 1;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"memory import: Invalid JSON in export file: {ex.Message}");
            return 1;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("entries", out var entriesElem)
                || entriesElem.ValueKind != JsonValueKind.Array)
            {
                Console.Error.WriteLine("memory import: Export file missing entries array");
                return 1;
            }

            ISqliteStore store;
            IVectorSearch vec;
            IEmbedder? embedder;
            MemoryComponents? owned = null;
            try
            {
                if (_store is not null)
                {
                    store = _store;
                    vec = _vec!;
                    embedder = _embedder;
                }
                else if (dryRun)
                {
                    // Dry-run still needs a store to read existing ids/contents
                    // for dedup. Open a connection but skip the embedder cost.
                    owned = MemoryComponents.OpenProduction();
                    store = owned.Store;
                    vec = owned.Vec;
                    embedder = null;
                }
                else
                {
                    owned = MemoryComponents.OpenProduction();
                    store = owned.Store;
                    vec = owned.Vec;
                    embedder = owned.Embedder;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"memory import: failed to initialize: {ex.Message}");
                return 1;
            }

            try
            {
                // Build dedup sets from existing store contents.
                var existingIds = new HashSet<string>(StringComparer.Ordinal);
                var existingContents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in TierNames.AllTablePairs)
                {
                    foreach (var e in store.List(pair.Tier, pair.Type, null))
                    {
                        existingIds.Add(e.Id);
                        existingContents.Add(e.Content);
                    }
                }

                var seenContents = new HashSet<string>(existingContents, StringComparer.Ordinal);
                int imported = 0;
                int skipped = 0;

                foreach (var entryElem in entriesElem.EnumerateArray())
                {
                    if (entryElem.ValueKind != JsonValueKind.Object)
                    {
                        skipped++;
                        continue;
                    }

                    // content must be a non-empty string.
                    if (!entryElem.TryGetProperty("content", out var contentEl)
                        || contentEl.ValueKind != JsonValueKind.String)
                    {
                        skipped++;
                        continue;
                    }
                    var content = contentEl.GetString();
                    if (string.IsNullOrEmpty(content))
                    {
                        skipped++;
                        continue;
                    }

                    // Dedup by id.
                    if (entryElem.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                        && existingIds.Contains(idEl.GetString()!))
                    {
                        skipped++;
                        continue;
                    }

                    // Dedup by content.
                    if (seenContents.Contains(content))
                    {
                        skipped++;
                        continue;
                    }
                    seenContents.Add(content);

                    // Resolve tier / content_type with TS-matching fallbacks.
                    Tier tier = Tier.Hot;
                    if (entryElem.TryGetProperty("tier", out var tierEl)
                        && tierEl.ValueKind == JsonValueKind.String)
                    {
                        var parsed = TierNames.ParseTier(tierEl.GetString()!);
                        if (parsed is not null) tier = parsed;
                    }
                    ContentType ctype = ContentType.Memory;
                    if (entryElem.TryGetProperty("content_type", out var ctEl)
                        && ctEl.ValueKind == JsonValueKind.String)
                    {
                        var parsed = TierNames.ParseContentType(ctEl.GetString()!);
                        if (parsed is not null) ctype = parsed;
                    }

                    if (dryRun)
                    {
                        imported++;
                        continue;
                    }

                    var opts = new InsertEntryOpts(
                        Content: content,
                        Summary: ReadOptionalString(entryElem, "summary"),
                        Source: ReadOptionalString(entryElem, "source"),
                        SourceTool: SourceToolParser.Parse(ReadOptionalString(entryElem, "source_tool")),
                        Project: ReadOptionalString(entryElem, "project"),
                        Tags: ReadStringArray(entryElem, "tags"),
                        ParentId: ReadOptionalString(entryElem, "parent_id"),
                        CollectionId: ReadOptionalString(entryElem, "collection_id"),
                        MetadataJson: ReadMetadataJson(entryElem));

                    var newId = store.Insert(tier, ctype, opts);
                    // TODO(Plan 5+): atomicity gap (carry-forward #2).
                    var embedding = embedder!.Embed(content);
                    vec.InsertEmbedding(tier, ctype, newId, embedding);
                    imported++;
                }

                var msg = dryRun
                    ? $"DRY RUN: would import {imported}, skip {skipped}"
                    : $"imported {imported}, skipped {skipped}";
                if (_out is not null) _out.WriteLine(msg);
                else Console.Out.WriteLine(msg);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"memory import: failed: {ex.Message}");
                return 1;
            }
            finally
            {
                owned?.Dispose();
            }
        }
    }

    private static string? ReadOptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var t in el.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (s is not null) list.Add(s);
            }
        }
        return list;
    }

    private static string? ReadMetadataJson(JsonElement obj)
    {
        if (!obj.TryGetProperty("metadata", out var el)) return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return null;
        // Preserve the raw JSON text so metadata round-trips unchanged.
        return el.GetRawText();
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Usage: total-recall memory import <path> [--dry-run]");
    }
}
