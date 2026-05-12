// src/TotalRecall.Server/Handlers/SkillSearchHandler.cs
//
// MCP handler for the `skill_search` tool. Proxies to ILocalSkillSearch
// (hybrid local ranking) first; on empty result falls through to cortex
// via ISkillClient.SearchAsync. CortexUnreachableException is swallowed.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;
using TotalRecall.Infrastructure.Sync;

namespace TotalRecall.Server.Handlers;

public sealed class SkillSearchHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query": {"type":"string","description":"Search query"},
            "scope": {"type":"string","description":"Restrict to a scope (global/team/user). Informational — cortex filters by caller's visible scopes."},
            "tags":  {"type":"array","items":{"type":"string"}},
            "limit": {"type":"number","description":"1..100, default 10"}
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly ILocalSkillSearch? _local;
    private readonly ISkillClient _client;

    public SkillSearchHandler(ILocalSkillSearch? local, ISkillClient client)
    {
        _local = local;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    // Backwards-compat ctor used by older tests that don't supply a local searcher.
    public SkillSearchHandler(ISkillClient client) : this(null, client) { }

    public string Name => "skill_search";
    public string Description => "Search skills via hybrid local ranking; cortex fallback when configured";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("skill_search requires an object arguments");

        var args = arguments.Value;
        var query = ArgumentParsing.ReadRequiredString(args, "query");
        if (query.Length == 0)
            throw new ArgumentException("query must be a non-empty string");

        var scope = ArgumentParsing.ReadOptionalString(args, "scope");
        var tags = ArgumentParsing.ReadStringArray(args, "tags");
        var limit = ArgumentParsing.ReadOptionalInt(args, "limit", 1, 100) ?? 10;

        IReadOnlyList<SkillSearchHitDto> hits = Array.Empty<SkillSearchHitDto>();
        if (_local is not null)
        {
            hits = await _local.SearchAsync(query, tags, limit, ct).ConfigureAwait(false);
        }
        if (hits.Count == 0)
        {
            try
            {
                hits = await _client.SearchAsync(query, scope, tags, limit, ct).ConfigureAwait(false);
            }
            catch (CortexUnreachableException) { /* swallow */ }
        }

        var text = JsonSerializer.Serialize(hits.ToArray(), JsonContext.Default.SkillSearchHitDtoArray);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }
}
