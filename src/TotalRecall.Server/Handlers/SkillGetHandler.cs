// src/TotalRecall.Server/Handlers/SkillGetHandler.cs
//
// Plan 2 Task 10 — MCP handler for the `skill_get` tool. Accepts either an
// `id` (Guid) OR the natural-key triple (`name`, `scope`, `scopeId`), but
// not both. Returns the full SkillBundleDto or JSON `null` when not found.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;

namespace TotalRecall.Server.Handlers;

public sealed class SkillGetHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":      {"type":"string","description":"Skill GUID. Supply either id OR the name+scope+scopeId triple, not both."},
            "name":    {"type":"string","description":"Skill name (required when using natural-key lookup)"},
            "scope":   {"type":"string","description":"Skill scope (required when using natural-key lookup)"},
            "scopeId": {"type":"string","description":"Skill scope ID (required when using natural-key lookup)"}
          }
        }
        """).RootElement.Clone();

    private readonly ISkillClient _client;

    public SkillGetHandler(ISkillClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "skill_get";
    public string Description => "Fetch a single skill by id or by (name, scope, scopeId)";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("skill_get requires an object arguments");

        var args = arguments.Value;
        var idStr = ArgumentParsing.ReadOptionalString(args, "id");
        var name = ArgumentParsing.ReadOptionalString(args, "name");
        var scope = ArgumentParsing.ReadOptionalString(args, "scope");
        var scopeId = ArgumentParsing.ReadOptionalString(args, "scopeId");

        var haveNaturalKey = name is not null || scope is not null || scopeId is not null;

        SkillBundleDto? bundle;
        if (idStr is not null)
        {
            if (haveNaturalKey)
                throw new ArgumentException(
                    "skill_get: supply either id or name+scope+scopeId, not both");
            if (!Guid.TryParse(idStr, out var id))
                throw new ArgumentException("skill_get: id must be a GUID");
            bundle = await _client.GetByIdAsync(id, ct).ConfigureAwait(false);
        }
        else if (name is not null && scope is not null && scopeId is not null)
        {
            bundle = await _client.GetByNaturalKeyAsync(name, scope, scopeId, ct).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException(
                "skill_get: supply either id or the full name+scope+scopeId triple");
        }

        // When bundle is null STJ emits the literal JSON "null" via the
        // non-nullable JsonTypeInfo<SkillBundleDto>; tolerate the nullability
        // mismatch with a bang since Serialize handles null values correctly.
        var text = JsonSerializer.Serialize(bundle!, JsonContext.Default.SkillBundleDto);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }
}
