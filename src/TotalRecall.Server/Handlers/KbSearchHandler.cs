// src/TotalRecall.Server/Handlers/KbSearchHandler.cs
//
// Plan 4 Task 4.9 — ports the `kb_search` branch of src-ts/tools/kb-tools.ts
// (lines 120-191) to the .NET Server. The Plan 4 scope is the SIMPLE
// knowledge-base search: embed the query, run hybrid search scoped to
// (cold, knowledge), and optionally post-filter by a caller-supplied
// collection id. The more elaborate TS features are deliberately stubbed:
//
//   - Hierarchical collection matching via collection summaries
//     (TS kb-tools.ts:128-154) — Plan 5+.
//   - Access-count tracking on hit + `needsSummary` threshold
//     (TS kb-tools.ts:169-188) — Plan 5+. We always return needsSummary=false
//     and hierarchicalMatch=null to preserve the TS wire shape.
//
// Constructor takes (IEmbedder, IHybridSearch). No store dependency is
// needed because we post-filter against Entry.CollectionId / Entry.ParentId
// already carried on the SearchResult.Entry payload.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>kb_search</c> tool. Searches the cold/knowledge
/// tier only, with optional post-hoc filtering by collection id. Hierarchical
/// collection matching and access-count tracking are stubbed until Plan 5+.
/// </summary>
public sealed class KbSearchHandler : IToolHandler
{
    // Mirror of src-ts/tools/kb-tools.ts:37-48. Note: TS uses `top_k`
    // (snake_case) for this tool, unlike `memory_search`'s `topK`. We match
    // TS exactly so the MCP wire contract stays byte-compatible.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query":      {"type":"string","description":"Search query"},
            "collection": {"type":"string","description":"Optional collection ID to restrict search"},
            "top_k":      {"type":"number","description":"Number of results to return (default: 10)"}
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    // kb_search is cold/knowledge-only; we do not expose tier/contentType
    // filters on the wire.
    private static readonly IReadOnlyList<(Tier Tier, ContentType Type)> ColdKnowledgeOnly =
        new[] { (Tier.Cold, ContentType.Knowledge) };

    private readonly IEmbedder _embedder;
    private readonly IHybridSearch _hybridSearch;

    public KbSearchHandler(IEmbedder embedder, IHybridSearch hybridSearch)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _hybridSearch = hybridSearch ?? throw new ArgumentNullException(nameof(hybridSearch));
    }

    public string Name => "kb_search";

    public string Description => "Search the knowledge base (cold/knowledge tier)";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("kb_search requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_search arguments must be a JSON object", nameof(arguments));

        var query = ReadRequiredString(args, "query");
        if (query.Length == 0)
            throw new ArgumentException("query must be a non-empty string");

        var collection = ReadOptionalString(args, "collection");
        var topK = ReadOptionalInt(args, "top_k", 1, 1000) ?? 10;

        ct.ThrowIfCancellationRequested();

        var vector = _embedder.Embed(query);

        ct.ThrowIfCancellationRequested();

        // Match TS: when a collection filter is present, request topK*2 so
        // the post-filter has headroom (src-ts/tools/kb-tools.ts:162).
        var requestTopK = collection is not null ? topK * 2 : topK;

        var opts = new HybridSearchOpts(
            TopK: requestTopK,
            MinScore: null,
            FtsWeight: null);

        var searchResults = _hybridSearch.Search(ColdKnowledgeOnly, query, vector, opts);

        // TODO(Plan 5+): hierarchical collection matching
        // (port src-ts/tools/kb-tools.ts:128-154). We currently never
        // populate hierarchicalMatch — callers always see null.

        if (collection is not null)
        {
            var filtered = new List<SearchResult>(searchResults.Count);
            foreach (var r in searchResults)
            {
                var collId = OptString(r.Entry.CollectionId);
                var parentId = OptString(r.Entry.ParentId);
                if (collId == collection || parentId == collection)
                    filtered.Add(r);
            }
            if (filtered.Count > topK)
                filtered.RemoveRange(topK, filtered.Count - topK);
            searchResults = filtered;
        }

        // TODO(Plan 5+): collection access-count tracking + needsSummary
        // threshold (port src-ts/tools/kb-tools.ts:169-188). Plan 4 always
        // reports needsSummary=false to preserve the TS wire shape.

        var dtos = new MemorySearchResultDto[searchResults.Count];
        for (var i = 0; i < searchResults.Count; i++)
        {
            var r = searchResults[i];
            dtos[i] = new MemorySearchResultDto(
                Entry: ToEntryDto(r.Entry),
                Score: r.Score,
                Tier: TierName(r.Tier),
                ContentType: ContentTypeName(r.ContentType),
                Rank: r.Rank);
        }

        // Hand-assemble the payload so `hierarchicalMatch: null` stays on
        // the wire. JsonContext's DefaultIgnoreCondition = WhenWritingNull
        // would otherwise strip it, and Plan 4 must match TS's
        // JSON.stringify output which always emits the key.
        var resultsJson = JsonSerializer.Serialize(dtos, JsonContext.Default.MemorySearchResultDtoArray);
        var jsonText = $"{{\"results\":{resultsJson},\"hierarchicalMatch\":null,\"needsSummary\":false}}";

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    // ---------- argument parsing helpers ----------

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
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        var s = prop.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int? ReadOptionalInt(JsonElement args, string name, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"{name} must be a number");
        if (!prop.TryGetDouble(out var d))
            throw new ArgumentException($"{name} must be a number");
        if (d < min || d > max)
            throw new ArgumentException($"{name} must be between {min} and {max}");
        return (int)d;
    }

    private static string TierName(Tier t) =>
        t.IsHot ? "hot" : t.IsWarm ? "warm" : "cold";

    private static string ContentTypeName(ContentType c) =>
        c.IsMemory ? "memory" : "knowledge";

    private static EntryDto ToEntryDto(Entry e)
    {
        return new EntryDto(
            Id: e.Id,
            Content: e.Content,
            Summary: OptString(e.Summary),
            Source: OptString(e.Source),
            Project: OptString(e.Project),
            Tags: ListModule.ToArray(e.Tags),
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            LastAccessedAt: e.LastAccessedAt,
            AccessCount: e.AccessCount,
            DecayScore: e.DecayScore);
    }

    private static string? OptString(FSharpOption<string> opt) =>
        FSharpOption<string>.get_IsSome(opt) ? opt.Value : null;
}
