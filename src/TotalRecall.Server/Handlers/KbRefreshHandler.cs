// src/TotalRecall.Server/Handlers/KbRefreshHandler.cs
//
// Plan 6 Task 6.0b — ports `kb refresh` (Cli/Commands/Kb/RefreshCommand.cs)
// to MCP. Parity port of src-ts/tools/kb-tools.ts:218-290 (kb_refresh).
//
// Looks up the collection root, deletes all child entries + the root +
// vector embeddings, then re-ingests from metadata.source_path via
// IFileIngester (file or directory entry point depending on the path type).
//
// Throws on missing collection / missing source_path / nonexistent source —
// the ErrorTranslator boundary handles wrapping into MCP error responses.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Ingestion;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class KbRefreshHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "collection": {"type":"string","description":"Collection id to refresh"}
          },
          "required": ["collection"]
        }
        """).RootElement.Clone();

    private readonly ISqliteStore _store;
    private readonly IVectorSearch _vec;
    private readonly IFileIngester _ingester;

    public KbRefreshHandler(ISqliteStore store, IVectorSearch vec, IFileIngester ingester)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _ingester = ingester ?? throw new ArgumentNullException(nameof(ingester));
    }

    public string Name => "kb_refresh";
    public string Description => "Re-ingest a knowledge base collection from its source path";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_refresh requires arguments object");

        var args = arguments.Value;
        if (!args.TryGetProperty("collection", out var cEl) || cEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("collection is required");
        var collectionId = cEl.GetString();
        if (string.IsNullOrEmpty(collectionId))
            throw new ArgumentException("collection must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        var entry = _store.Get(Tier.Cold, ContentType.Knowledge, collectionId);
        if (entry is null)
            throw new ArgumentException($"Collection not found: {collectionId}");

        var sourcePath = MetadataHelpers.ExtractString(entry.MetadataJson, "source_path");
        if (string.IsNullOrEmpty(sourcePath))
            throw new InvalidOperationException(
                "Collection has no source_path in metadata; cannot refresh");

        // Delete children (rows whose ParentId or CollectionId points at the
        // root, excluding the root itself). Mirrors RefreshCommand.cs:134-150.
        var all = _store.List(Tier.Cold, ContentType.Knowledge, null);
        var children = new List<Entry>();
        foreach (var e in all)
        {
            if (e.Id == collectionId) continue;
            var isChild = false;
            if (FSharpOption<string>.get_IsSome(e.ParentId) && e.ParentId.Value == collectionId)
                isChild = true;
            else if (FSharpOption<string>.get_IsSome(e.CollectionId) && e.CollectionId.Value == collectionId)
                isChild = true;
            if (isChild) children.Add(e);
        }
        foreach (var child in children)
        {
            var childRowid = _store.GetRowid(Tier.Cold, ContentType.Knowledge, child.Id);
            if (childRowid is not null)
                _vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, childRowid.Value);
            _store.Delete(Tier.Cold, ContentType.Knowledge, child.Id);
        }

        // Delete the root.
        var rootRowid = _store.GetRowid(Tier.Cold, ContentType.Knowledge, collectionId);
        if (rootRowid is not null)
            _vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, rootRowid.Value);
        _store.Delete(Tier.Cold, ContentType.Knowledge, collectionId);

        bool isDir;
        if (Directory.Exists(sourcePath))
        {
            isDir = true;
        }
        else if (File.Exists(sourcePath))
        {
            isDir = false;
        }
        else
        {
            throw new InvalidOperationException($"Source path does not exist: {sourcePath}");
        }

        int files;
        int chunks;
        if (isDir)
        {
            var result = _ingester.IngestDirectory(sourcePath, null);
            files = result.DocumentCount;
            chunks = result.TotalChunks;
        }
        else
        {
            var result = _ingester.IngestFile(sourcePath, null);
            files = 1;
            chunks = result.ChunkCount;
        }

        var dto = new KbRefreshResultDto(
            CollectionId: collectionId,
            Files: files,
            Chunks: chunks,
            Refreshed: true);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.KbRefreshResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
