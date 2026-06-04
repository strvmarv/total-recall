// src/TotalRecall.Server/Handlers/CacheStoreHandler.cs
//
// Phase 3 idea 2c — MCP handler for the cache_store tool. Persists a tool
// result keyed by (tool, argsHash) with a TTL; ToolCacheStore enforces
// expiry purge + LRU capacity on every write.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class CacheStoreHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "tool":       {"type":"string","description":"Tool name to cache the result under"},
            "argsHash":   {"type":"string","description":"Canonical hash of the tool arguments"},
            "content":    {"type":"string","description":"The tool result content to cache"},
            "ttlSeconds": {"type":"number","description":"Time-to-live in seconds (default 600)"}
          },
          "required": ["tool", "argsHash", "content"]
        }
        """).RootElement.Clone();

    private readonly ToolCacheStore _cache;

    public CacheStoreHandler(ToolCacheStore cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string Name => "cache_store";

    public string Description =>
        "Store a tool result in the tool-result cache for later cache_check replay";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("cache_store requires a JSON object argument", nameof(arguments));

        var args = arguments.Value;
        var tool = ArgumentParsing.ReadRequiredString(args, "tool");
        if (tool.Length == 0)
            throw new ArgumentException("tool must be a non-empty string");
        var argsHash = ArgumentParsing.ReadRequiredString(args, "argsHash");
        if (argsHash.Length == 0)
            throw new ArgumentException("argsHash must be a non-empty string");
        var content = ArgumentParsing.ReadRequiredString(args, "content");
        if (content.Length == 0)
            throw new ArgumentException("content must be a non-empty string");

        int? ttl = null;
        if (args.TryGetProperty("ttlSeconds", out var ttlEl)
            && ttlEl.ValueKind == JsonValueKind.Number
            && ttlEl.TryGetInt32(out var ttlVal))
        {
            ttl = ttlVal;
        }

        var tokenEstimate = _cache.StoreResult(tool, argsHash, content, ttl);

        var dto = new CacheStoreResultDto { Stored = true, TokenEstimate = tokenEstimate };
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.CacheStoreResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

}
