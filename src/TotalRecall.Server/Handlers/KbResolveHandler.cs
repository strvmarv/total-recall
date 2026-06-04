// src/TotalRecall.Server/Handlers/KbResolveHandler.cs
//
// Phase 3 idea 2c — MCP handler for the kb_resolve tool. Resolves a file
// path to its ingested KB chunks: a token-efficient alternative to Read
// for files that are already in the knowledge base.
//
// Document discovery uses the ListEntriesOpts.Source filter plus the
// cold_knowledge tree invariants (document = ParentId None + CollectionId
// Some; chunks hang off ParentId). No metadata JSON parsing required.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class KbResolveHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": {"type":"string","description":"File path to resolve to knowledge base chunks"}
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private readonly IStore _store;

    public KbResolveHandler(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "kb_resolve";

    public string Description =>
        "Resolve a file path to its knowledge base chunks (token-efficient alternative to reading the raw file)";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_resolve requires a JSON object argument", nameof(arguments));

        var path = ArgumentParsing.ReadRequiredString(arguments.Value, "path");
        if (path.Length == 0)
            throw new ArgumentException("path must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        // Try the path as given, then the normalized absolute form. Ingest
        // stored whatever the caller passed to kb_ingest_file, so both
        // spellings are legitimate lookups.
        var doc = FindDocument(path);
        var resolvedPath = path;
        if (doc is null)
        {
            string? fullPath = null;
            try { fullPath = Path.GetFullPath(path); } catch { /* invalid path chars — skip */ }
            if (fullPath is not null && !string.Equals(fullPath, path, StringComparison.Ordinal))
            {
                doc = FindDocument(fullPath);
                if (doc is not null) resolvedPath = fullPath;
            }
        }

        if (doc is null)
        {
            var miss = new KbResolveResultDto { Found = false, ChunkCount = 0 };
            return Task.FromResult(Serialize(miss));
        }

        var chunkEntries = _store.List(Tier.Cold, ContentType.Knowledge,
            new ListEntriesOpts { ParentId = doc.Id, OrderBy = "created_at ASC" });

        var chunks = new KbResolveChunkDto[chunkEntries.Count];
        var tokenEstimate = 0;
        for (var i = 0; i < chunkEntries.Count; i++)
        {
            chunks[i] = new KbResolveChunkDto(chunkEntries[i].Id, chunkEntries[i].Content);
            tokenEstimate += SessionLifecycle.HeuristicEstimateTokens(chunkEntries[i].Content);
        }

        int? rawFileTokens = null;
        int? savings = null;
        try
        {
            // Best-effort raw-file comparison. Sync I/O is intentional: this
            // handler is entirely synchronous (Task.FromResult), not a forgotten await.
            if (File.Exists(resolvedPath))
            {
                rawFileTokens = SessionLifecycle.HeuristicEstimateTokens(
                    File.ReadAllText(resolvedPath));
                savings = Math.Max(0, rawFileTokens.Value - tokenEstimate);
            }
        }
        catch
        {
            // Unreadable file — raw comparison is best-effort only.
        }

        var dto = new KbResolveResultDto
        {
            Found = true,
            CollectionId = FSharpOption<string>.get_IsSome(doc.CollectionId)
                ? doc.CollectionId.Value : null,
            DocumentId = doc.Id,
            ChunkCount = chunks.Length,
            Chunks = chunks,
            TokenEstimate = tokenEstimate,
            RawFileTokens = rawFileTokens,
            Savings = savings,
        };
        return Task.FromResult(Serialize(dto));
    }

    /// <summary>
    /// Document row = source matches AND no parent AND has a collection.
    /// Collections have no CollectionId; chunks have a ParentId. When the
    /// same file was ingested more than once, prefer the newest document.
    /// </summary>
    private Entry? FindDocument(string path)
    {
        // NOTE: Source matches the document AND all its chunks, so this
        // materializes chunk content just to find the doc id; the chunks are
        // then re-fetched by ParentId. Acceptable: kb_resolve is not a hot
        // path and IStore has no ParentId-IS-NULL projection. Don't "optimize"
        // this by filtering chunks from THIS result set — ordering and the
        // newest-document tiebreak depend on the second query.
        var rows = _store.List(Tier.Cold, ContentType.Knowledge,
            new ListEntriesOpts { Source = path });
        return rows
            .Where(e => FSharpOption<string>.get_IsNone(e.ParentId)
                && FSharpOption<string>.get_IsSome(e.CollectionId))
            .OrderByDescending(e => e.CreatedAt)
            .FirstOrDefault();
    }

    private static ToolCallResult Serialize(KbResolveResultDto dto)
    {
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.KbResolveResultDto);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }
}
