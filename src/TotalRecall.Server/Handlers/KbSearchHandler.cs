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
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
            "top_k":      {"type":"number","description":"Number of results to return (default: 10)"},
            "scopes":     {"type":"array","items":{"type":"string"},"description":"Scope(s) to search. Defaults to configured default scope. Pass multiple to broaden (e.g. [\"user:paul\",\"global:jira\"])."}
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
    private readonly TotalRecall.Infrastructure.Sync.IRemoteBackend? _remote;
    private readonly string? _scopeDefault;

    public KbSearchHandler(
        IEmbedder embedder,
        IHybridSearch hybridSearch,
        TotalRecall.Infrastructure.Sync.IRemoteBackend? remote = null,
        string? scopeDefault = null)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _hybridSearch = hybridSearch ?? throw new ArgumentNullException(nameof(hybridSearch));
        _remote = remote;
        _scopeDefault = scopeDefault;
    }

    public string Name => "kb_search";

    public string Description => "Search the knowledge base (cold/knowledge tier)";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
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

        IReadOnlyList<string>? scopes = null;
        if (args.TryGetProperty("scopes", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
        {
            scopes = scopesEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }
        if (scopes is null or { Count: 0 })
        {
            scopes = _scopeDefault is not null ? new[] { _scopeDefault } : null;
        }

        ct.ThrowIfCancellationRequested();

        // In cortex mode, delegate KB search to the remote Cortex backend
        // which has the global knowledge base with Cohere v4 embeddings.
        if (_remote is not null)
        {
            return await SearchRemoteAsync(query, topK, scopes, ct).ConfigureAwait(false);
        }

        var vector = _embedder.Embed(query);

        ct.ThrowIfCancellationRequested();

        // Match TS: when a collection filter is present, request topK*2 so
        // the post-filter has headroom (src-ts/tools/kb-tools.ts:162).
        var requestTopK = collection is not null ? topK * 2 : topK;

        var opts = new HybridSearchOpts(
            TopK: requestTopK,
            MinScore: null,
            FtsWeight: null,
            Scopes: scopes);

        var searchResults = _hybridSearch.Search(ColdKnowledgeOnly, query, vector, opts);

        // TODO(Plan 5+): hierarchical collection matching
        // (port src-ts/tools/kb-tools.ts:128-154). We currently never
        // populate hierarchicalMatch — callers always see null.

        if (collection is not null)
        {
            var filtered = new List<SearchResult>(searchResults.Count);
            foreach (var r in searchResults)
            {
                var collId = EntryMapping.OptString(r.Entry.CollectionId);
                var parentId = EntryMapping.OptString(r.Entry.ParentId);
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
                Entry: EntryMapping.ToEntryDto(r.Entry),
                Score: r.Score,
                Tier: EntryMapping.TierName(r.Tier),
                ContentType: EntryMapping.ContentTypeName(r.ContentType),
                Rank: r.Rank);
        }

        // Hand-assemble the payload so `hierarchicalMatch: null` stays on
        // the wire. JsonContext's DefaultIgnoreCondition = WhenWritingNull
        // would otherwise strip it, and Plan 4 must match TS's
        // JSON.stringify output which always emits the key.
        var resultsJson = JsonSerializer.Serialize(dtos, JsonContext.Default.MemorySearchResultDtoArray);
        var jsonText = $"{{\"results\":{resultsJson},\"hierarchicalMatch\":null,\"needsSummary\":false}}";

        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }

    /// <summary>
    /// Search Cortex's knowledge base remotely. Returns results formatted
    /// identically to the local search path.
    /// </summary>
    private async Task<ToolCallResult> SearchRemoteAsync(string query, int topK, IReadOnlyList<string>? scopes, CancellationToken ct)
    {
        TotalRecall.Infrastructure.Sync.SyncSearchResult[] results;
        try
        {
            results = await _remote!.SearchKnowledgeAsync(query, topK, scopes, ct).ConfigureAwait(false);
        }
        catch (TotalRecall.Infrastructure.Sync.CortexUnreachableException)
        {
            return new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = "Cortex is unreachable. Knowledge base search unavailable in offline mode." } },
                IsError = false,
            };
        }

        if (results.Length == 0)
        {
            var emptyJson = "{\"results\":[],\"hierarchicalMatch\":null,\"needsSummary\":false}";
            return new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = emptyJson } },
                IsError = false,
            };
        }

        // Convert remote results to the same DTO shape as local search
        var dtos = new MemorySearchResultDto[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            dtos[i] = new MemorySearchResultDto(
                Entry: new EntryDto(
                    Id: r.Id,
                    Content: r.Content,
                    Summary: null,
                    Source: r.Source,
                    Project: null,
                    Tags: r.Tags ?? Array.Empty<string>(),
                    CreatedAt: 0,
                    UpdatedAt: 0,
                    LastAccessedAt: 0,
                    AccessCount: 0,
                    DecayScore: 1.0,
                    Scope: ""),
                Score: r.Score,
                Tier: "cold",
                ContentType: "knowledge",
                Rank: i + 1);
        }

        var resultsJson = JsonSerializer.Serialize(dtos, JsonContext.Default.MemorySearchResultDtoArray);
        var jsonText = $"{{\"results\":{resultsJson},\"hierarchicalMatch\":null,\"needsSummary\":false}}";

        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
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

}
