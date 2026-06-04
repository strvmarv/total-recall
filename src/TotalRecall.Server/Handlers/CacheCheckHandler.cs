// src/TotalRecall.Server/Handlers/CacheCheckHandler.cs
//
// Phase 3 idea 2c — MCP handler for the cache_check tool. Queries the
// tool-result cache by (tool, argsHash). Interception (calling this before
// host tool calls) is a host-plugin concern, not total-recall's.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class CacheCheckHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "tool":          {"type":"string","description":"Tool name the result was cached under"},
            "argsHash":      {"type":"string","description":"Canonical hash of the tool arguments"},
            "maxAgeSeconds": {"type":"number","description":"Optional freshness ceiling in seconds (narrower than the stored TTL)"}
          },
          "required": ["tool", "argsHash"]
        }
        """).RootElement.Clone();

    private readonly ToolCacheStore _cache;

    public CacheCheckHandler(ToolCacheStore cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public string Name => "cache_check";

    public string Description =>
        "Check the tool-result cache for a previously stored result (token-saving replay of identical tool calls)";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("cache_check requires a JSON object argument", nameof(arguments));

        var args = arguments.Value;
        var tool = ReadRequiredString(args, "tool");
        var argsHash = ReadRequiredString(args, "argsHash");
        int? maxAge = null;
        if (args.TryGetProperty("maxAgeSeconds", out var maxAgeEl)
            && maxAgeEl.ValueKind == JsonValueKind.Number)
        {
            maxAge = maxAgeEl.GetInt32();
        }

        var hit = _cache.Check(tool, argsHash, maxAge);

        var dto = hit is null
            ? new CacheCheckResultDto { Hit = false }
            : new CacheCheckResultDto
            {
                Hit = true,
                Content = hit.Content,
                CachedAt = DateTimeOffset.FromUnixTimeMilliseconds(hit.StoredAtMs)
                    .ToString("o"),
                TokenSavings = hit.TokenEstimate,
            };

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.CacheCheckResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static string ReadRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} is required and must be a string");
        var v = prop.GetString();
        if (string.IsNullOrEmpty(v))
            throw new ArgumentException($"{name} must be a non-empty string");
        return v;
    }
}
