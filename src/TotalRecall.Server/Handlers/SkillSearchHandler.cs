// src/TotalRecall.Server/Handlers/SkillSearchHandler.cs
//
// Plan 2 Task 9 — MCP handler for the `skill_search` tool. Proxies to
// ISkillClient.SearchAsync (cortex /api/me/skills/search) and returns the
// hybrid-ranked hit array as JSON.
//
// Argument validation mirrors MemorySearchHandler so ErrorTranslator wraps
// failures uniformly.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;

namespace TotalRecall.Server.Handlers;

public sealed class SkillSearchHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "query": {"type":"string","description":"Search query"},
            "scope": {"type":"string","description":"Restrict to a scope (global/team/user). Currently informational — cortex filters by caller's visible scopes."},
            "tags":  {"type":"array","items":{"type":"string"}},
            "limit": {"type":"number","description":"1..100, default 10"}
          },
          "required": ["query"]
        }
        """).RootElement.Clone();

    private readonly ISkillClient _client;

    public SkillSearchHandler(ISkillClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "skill_search";
    public string Description => "Search skills across caller-visible scopes via hybrid ranking";
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

        var hits = await _client.SearchAsync(query, scope, tags, limit, ct).ConfigureAwait(false);

        var text = JsonSerializer.Serialize(hits, JsonContext.Default.SkillSearchHitDtoArray);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }
}
