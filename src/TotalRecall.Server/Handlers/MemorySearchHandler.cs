// src/TotalRecall.Server/Handlers/MemorySearchHandler.cs
//
// Plan 4 Task 4.7 — ports the `memory_search` branch of
// src-ts/tools/memory-tools.ts (plus src-ts/memory/search.ts) to the .NET
// Server. The handler validates MCP arguments, embeds the query, runs a
// hybrid (vector + FTS) search across the requested tier/type pairs, and
// returns the results as a JSON array in the MCP response envelope.
//
// Design notes:
//
//   - Constructor shape is (IEmbedder, IHybridSearch). Plan 4 adds a minimal
//     IHybridSearch seam (see src/TotalRecall.Infrastructure/Search/
//     IHybridSearch.cs) so this handler can be unit-tested against a
//     recording fake without wiring the real SQLite/vector/FTS stack.
//
//   - The TS handler filters ALL_TABLE_PAIRS by optional `tiers` /
//     `contentTypes` arrays. We inline the 6-pair table set here so we do
//     not need to expose Schema.AllTablePairs (which is `internal`).
//
//   - Result serialization uses a source-gen DTO array registered in
//     JsonContext (MemorySearchResultDto). This mirrors the TS
//     `JSON.stringify(results)` output: every result carries an `entry`
//     object plus `score`, `tier`, `content_type`, and `rank`. Field names
//     match the TS wire-format exactly.
//
//   - NOTE: the TS handler additionally calls logRetrievalEvent(...) for
//     eval telemetry. That is deferred to Plan 5+ — this handler does not
//     log retrieval events. See TS memory-tools.ts:189-204.
//
//   - Validation throws ArgumentException, matching MemoryStoreHandler, so
//     ErrorTranslator (Task 4.5) can wrap failures into MCP tool-error shape.

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
/// MCP handler for the <c>memory_search</c> tool. Validates arguments,
/// embeds the query, runs hybrid search via <see cref="IHybridSearch"/>,
/// and returns the fused results as JSON.
/// </summary>
public sealed class MemorySearchHandler : IToolHandler
{
    // Mirror of src-ts/tools/memory-tools.ts:46-67.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query":        {"type":"string","description":"Search query"},
            "topK":         {"type":"number","description":"Number of results to return (default: 10)"},
            "minScore":     {"type":"number","description":"Minimum similarity score (0-1)"},
            "tiers":        {"type":"array","items":{"type":"string","enum":["hot","warm","cold"]},"description":"Tiers to search (default: all)"},
            "contentTypes": {"type":"array","items":{"type":"string","enum":["memory","knowledge"]},"description":"Content types to search (default: all)"},
            "scopes":       {"type":"array","items":{"type":"string"},"description":"Scope(s) to search. Defaults to configured default scope. Pass multiple to broaden (e.g. [\"user:paul\",\"global:jira\"])."}
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly IEmbedder _embedder;
    private readonly IHybridSearch _hybridSearch;

    public MemorySearchHandler(IEmbedder embedder, IHybridSearch hybridSearch)
    {
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _hybridSearch = hybridSearch ?? throw new ArgumentNullException(nameof(hybridSearch));
    }

    public string Name => "memory_search";

    public string Description => "Search memories and knowledge using semantic similarity";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_search requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_search arguments must be a JSON object", nameof(arguments));

        var query = ReadRequiredString(args, "query");
        if (query.Length == 0)
            throw new ArgumentException("query must be a non-empty string");

        var topK = ReadOptionalInt(args, "topK", 1, 1000) ?? 10;
        var minScore = ReadOptionalDouble(args, "minScore", 0.0, 1.0);

        IReadOnlyList<string>? scopes = null;
        if (args.TryGetProperty("scopes", out var scopesEl) && scopesEl.ValueKind == JsonValueKind.Array)
        {
            scopes = scopesEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToArray();
        }

        var tierFilter = ReadStringArray(args, "tiers");
        var typeFilter = ReadStringArray(args, "contentTypes");

        // Validate filter strings and convert to Tier / ContentType sets.
        HashSet<Tier>? tierSet = null;
        if (tierFilter is not null)
        {
            tierSet = new HashSet<Tier>();
            foreach (var t in tierFilter)
                tierSet.Add(ParseTier(t));
        }

        HashSet<ContentType>? typeSet = null;
        if (typeFilter is not null)
        {
            typeSet = new HashSet<ContentType>();
            foreach (var ct2 in typeFilter)
                typeSet.Add(ParseContentType(ct2));
        }

        var tiers = EntryMapping.AllTablePairs
            .Where(p =>
                (tierSet is null || tierSet.Contains(p.Tier)) &&
                (typeSet is null || typeSet.Contains(p.Type)))
            .ToList();

        ct.ThrowIfCancellationRequested();

        // Matches MemoryStoreHandler: synchronous Embed call warms the
        // ONNX singleton on first use.
        var vector = _embedder.Embed(query);

        ct.ThrowIfCancellationRequested();

        var opts = new HybridSearchOpts(
            TopK: topK,
            MinScore: minScore,
            FtsWeight: null,
            Scopes: scopes);

        var results = _hybridSearch.Search(tiers, query, vector, opts);

        var dtos = new MemorySearchResultDto[results.Count];
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            dtos[i] = new MemorySearchResultDto(
                Entry: EntryMapping.ToEntryDto(r.Entry),
                Score: r.Score,
                Tier: EntryMapping.TierName(r.Tier),
                ContentType: EntryMapping.ContentTypeName(r.ContentType),
                Rank: r.Rank);
        }

        var jsonText = JsonSerializer.Serialize(dtos, JsonContext.Default.MemorySearchResultDtoArray);

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

    // Mirrors TS validateOptionalNumber in src-ts/tools/validation.ts. Two
    // flavours — an int-returning one for topK (which must be integral) and
    // a double-returning one for minScore (which is a [0,1] ratio).
    private static int? ReadOptionalInt(JsonElement args, string name, int min, int max)
    {
        var d = ReadOptionalDouble(args, name, min, max);
        return d is null ? null : (int)d.Value;
    }

    private static double? ReadOptionalDouble(JsonElement args, string name, double min, double max)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.Number)
            throw new ArgumentException($"{name} must be a number");
        if (!prop.TryGetDouble(out var d))
            throw new ArgumentException($"{name} must be a number");
        if (d < min || d > max)
            throw new ArgumentException($"{name} must be between {min} and {max}");
        return d;
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return null;
        if (prop.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{name} must be an array of strings");
        var list = new List<string>(prop.GetArrayLength());
        var i = 0;
        foreach (var el in prop.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"{name}[{i}] must be a string");
            list.Add(el.GetString() ?? throw new ArgumentException($"{name}[{i}] must be a string"));
            i++;
        }
        return list;
    }

    private static Tier ParseTier(string s) => s switch
    {
        "hot" => Tier.Hot,
        "warm" => Tier.Warm,
        "cold" => Tier.Cold,
        _ => throw new ArgumentException($"Invalid tier: {s}. Must be hot, warm, or cold"),
    };

    private static ContentType ParseContentType(string s) => s switch
    {
        "memory" => ContentType.Memory,
        "knowledge" => ContentType.Knowledge,
        _ => throw new ArgumentException($"Invalid content type: {s}. Must be memory or knowledge"),
    };

}
