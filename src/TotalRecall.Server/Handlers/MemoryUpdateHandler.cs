// src/TotalRecall.Server/Handlers/MemoryUpdateHandler.cs
//
// Plan 4 Task 4.8 — ports the `memory_update` branch of
// src-ts/tools/memory-tools.ts (plus src-ts/memory/update.ts) to the
// .NET Server. Resolves the target row by iterating the 6 (tier, type)
// pairs (same trick as MemoryGetHandler), then forwards the provided
// fields to IStore.Update. If `content` is supplied the handler
// re-embeds and replaces the vec0 row via Delete + Insert, matching
// the TS semantics.
//
// Design notes:
//
//   - The plan prose specifies (IStore, IEmbedder, IVectorSearch)
//     because content changes require re-embedding AND replacing the
//     vec0 row. IVectorSearch.InsertEmbedding is INSERT-only (see
//     VectorSearch.cs:51) so the replace path is DeleteEmbedding +
//     InsertEmbedding. DeleteEmbedding is a silent no-op if the vec0
//     row is missing, which keeps the semantics safe.
//
//   - Response payload matches TS `{updated: bool}`. Built by hand
//     because the single-bool shape does not justify a DTO.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>memory_update</c> tool. Locates the row by id
/// across all tier/type tables, applies the requested field updates, and
/// (when content changes) re-embeds and replaces the vec0 row.
/// </summary>
public sealed class MemoryUpdateHandler : IToolHandler
{
    // Mirror of src-ts/tools/memory-tools.ts:80-93.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":      {"type":"string","description":"Entry ID"},
            "content": {"type":"string","description":"New content"},
            "summary": {"type":"string","description":"New summary"},
            "tags":    {"type":["array","string"],"items":{"type":"string"},"description":"New tags (array, JSON-encoded array string, or comma-separated string)"},
            "project": {"type":"string","description":"New project"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private const int MaxContentLength = 100_000;

    private readonly IStore _store;
    private readonly IEmbedder _embedder;
    private readonly IVectorSearch _vectorSearch;

    public MemoryUpdateHandler(IStore store, IEmbedder embedder, IVectorSearch vectorSearch)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _vectorSearch = vectorSearch ?? throw new ArgumentNullException(nameof(vectorSearch));
    }

    public string Name => "memory_update";

    public string Description => "Update an existing memory entry";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_update requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_update arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        // Optional content: if present, must be non-empty and within
        // the shared MAX_CONTENT_LENGTH budget (100_000 chars).
        string? content = null;
        var hasContent = args.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind != JsonValueKind.Null;
        if (hasContent)
        {
            if (contentProp.ValueKind != JsonValueKind.String)
                throw new ArgumentException("content must be a string");
            content = contentProp.GetString() ?? string.Empty;
            if (content.Length == 0)
                throw new ArgumentException("content must be a non-empty string");
            if (content.Length > MaxContentLength)
                throw new ArgumentException(
                    $"Content exceeds maximum length of {MaxContentLength} characters");
        }

        var summary = ReadOptionalString(args, "summary");
        var project = ReadOptionalString(args, "project");
        var tags = ReadTags(args);

        ct.ThrowIfCancellationRequested();

        // Locate the row across all 6 tables.
        (Tier Tier, ContentType Type)? located = null;
        foreach (var pair in EntryMapping.AllTablePairs)
        {
            var row = _store.Get(pair.Tier, pair.Type, id);
            if (row is not null)
            {
                located = (pair.Tier, pair.Type);
                break;
            }
        }

        if (located is null)
        {
            return Task.FromResult(new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = "{\"updated\":false}" } },
                IsError = false,
            });
        }

        var opts = new UpdateEntryOpts
        {
            Content = content,
            Summary = summary,
            Project = project,
            Tags = tags,
        };

        _store.Update(located.Value.Tier, located.Value.Type, id, opts);

        // Re-embed + replace vector row when content changed. vec0 INSERT
        // is not upsert (see VectorSearch.cs), so we delete the existing
        // row first. store.Update does not change the content rowid, so
        // the rowid we resolved before the update is still valid after.
        if (content is not null)
        {
            ct.ThrowIfCancellationRequested();
            var vector = _embedder.Embed(content);
            var rowid = _store.GetInternalKey(located.Value.Tier, located.Value.Type, id);
            if (rowid is not null)
                _vectorSearch.DeleteEmbedding(located.Value.Tier, located.Value.Type, rowid.Value);
            _vectorSearch.InsertEmbedding(located.Value.Tier, located.Value.Type, id, vector);
        }

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = "{\"updated\":true}" } },
            IsError = false,
        });
    }

    private static string ReadRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required");
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString() ?? throw new ArgumentException($"{name} must be a string");
    }

    private static string? ReadOptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString();
    }

    private static IReadOnlyList<string>? ReadTags(JsonElement args)
        => ArgumentParsing.ReadTags(args);
}
