// src/TotalRecall.Server/Handlers/MemoryImportHandler.cs
//
// Plan 6 Task 6.0a — ports `memory import` (ImportCommand.cs) to MCP.
// Accepts an `entries` array of objects matching the memory_export row
// shape, deduplicates (by existing id and by content across tables AND
// within the current batch), then re-embeds and inserts survivors.
// Returns { imported_count, skipped_count, errors }.
//
// Unlike the CLI verb, this handler takes a JSON array of entry objects
// directly rather than a file path, so an MCP client can roundtrip
// export→import entirely over the wire.
//
// Atomicity gap: the store.Insert + vec.InsertEmbedding sequence is NOT
// transactional — a crash between the two leaves a row without an
// embedding. This is Plan 5 carry-forward #9; see the TODO at the embed
// site below. Not fixed in Task 6.0a.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryImportHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "entries": {
              "type":"array",
              "description":"Array of entry objects matching the memory_export row shape",
              "items":{"type":"object"}
            }
          },
          "required": ["entries"]
        }
        """).RootElement.Clone();

    private readonly ISqliteStore _store;
    private readonly IVectorSearch _vec;
    private readonly IEmbedder _embedder;

    public MemoryImportHandler(ISqliteStore store, IVectorSearch vec, IEmbedder embedder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
    }

    public string Name => "memory_import";
    public string Description => "Import memory/knowledge entries from a JSON array";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_import requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_import arguments must be a JSON object", nameof(arguments));
        if (!args.TryGetProperty("entries", out var entriesEl) || entriesEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("entries is required and must be an array");

        ct.ThrowIfCancellationRequested();

        // Build dedup sets from existing store contents — same sweep the
        // CLI verb uses.
        var existingIds = new HashSet<string>(StringComparer.Ordinal);
        var existingContents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in TierNames.AllTablePairs)
        {
            foreach (var e in _store.List(pair.Tier, pair.Type, null))
            {
                existingIds.Add(e.Id);
                existingContents.Add(e.Content);
            }
        }

        var seenContents = new HashSet<string>(existingContents, StringComparer.Ordinal);
        var errors = new List<string>();
        int imported = 0;
        int skipped = 0;
        int index = -1;

        foreach (var entryElem in entriesEl.EnumerateArray())
        {
            index++;
            if (entryElem.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                errors.Add($"entry[{index}]: not an object");
                continue;
            }

            if (!entryElem.TryGetProperty("content", out var contentEl)
                || contentEl.ValueKind != JsonValueKind.String)
            {
                skipped++;
                errors.Add($"entry[{index}]: missing or non-string content");
                continue;
            }
            var content = contentEl.GetString();
            if (string.IsNullOrEmpty(content))
            {
                skipped++;
                errors.Add($"entry[{index}]: empty content");
                continue;
            }

            if (entryElem.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String
                && existingIds.Contains(idEl.GetString()!))
            {
                skipped++;
                continue;
            }

            if (seenContents.Contains(content))
            {
                skipped++;
                continue;
            }
            seenContents.Add(content);

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

            var newId = _store.Insert(tier, ctype, opts);
            // TODO(Plan 5+): atomicity gap (carry-forward #9) — a crash
            // between store.Insert and the vector insert leaves the row
            // without an embedding. Same gap MoveHelpers.MoveAndReEmbed
            // documents. Fix alongside that one.
            var embedding = _embedder.Embed(content);
            _vec.InsertEmbedding(tier, ctype, newId, embedding);
            imported++;
        }

        var dto = new MemoryImportResultDto(
            ImportedCount: imported,
            SkippedCount: skipped,
            Errors: errors.ToArray());
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryImportResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
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
        return el.GetRawText();
    }
}
