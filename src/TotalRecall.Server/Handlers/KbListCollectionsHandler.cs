// src/TotalRecall.Server/Handlers/KbListCollectionsHandler.cs
//
// Plan 6 Task 6.0b — ports `kb list` (Cli/Commands/Kb/ListCommand.cs) to MCP.
// Also a parity port of src-ts/tools/kb-tools.ts:193-196 (kb_list_collections).
//
// Reads collection roots via IStore.ListByMetadata({type:"collection"})
// from cold_knowledge, then computes per-collection document/chunk counts in
// a single sweep of cold_knowledge to avoid O(N*M) per-collection rescan
// (matches the CLI's optimization).

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class KbListCollectionsHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {}
        }
        """).RootElement.Clone();

    private readonly IStore _store;

    public KbListCollectionsHandler(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "kb_list_collections";
    public string Description => "List knowledge base collections with document and chunk counts";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var filter = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "collection",
        };
        var collections = _store.ListByMetadata(Tier.Cold, ContentType.Knowledge, filter, null);

        // Single sweep of cold_knowledge to compute per-collection counts —
        // mirrors ListCommand.cs:101-117.
        var all = _store.List(Tier.Cold, ContentType.Knowledge, null);
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

        var dtos = new List<KbCollectionDto>(collections.Count);
        foreach (var e in collections)
        {
            var name = MetadataHelpers.ExtractString(e.MetadataJson, "name") ?? "(unnamed)";
            var sourcePath = MetadataHelpers.ExtractString(e.MetadataJson, "source_path");
            var docs = docCounts.TryGetValue(e.Id, out var d) ? d : 0;
            var chunks = chunkCounts.TryGetValue(e.Id, out var c2) ? c2 : 0;
            dtos.Add(new KbCollectionDto(
                Id: e.Id,
                Name: name,
                DocumentCount: docs,
                ChunkCount: chunks,
                CreatedAt: e.CreatedAt,
                Summary: EntryMapping.OptString(e.Summary),
                SourcePath: sourcePath));
        }

        var dto = new KbListCollectionsResultDto(
            Collections: dtos.ToArray(),
            Count: dtos.Count);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.KbListCollectionsResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
