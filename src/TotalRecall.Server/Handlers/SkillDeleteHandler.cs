// src/TotalRecall.Server/Handlers/SkillDeleteHandler.cs
//
// Plan 2 Task 12 — MCP handler for the `skill_delete` tool. Validates the
// required `id` argument is a Guid, forwards to ISkillClient.DeleteAsync,
// and returns `{"deleted":true}` on success.

using System.Text.Json;
using TotalRecall.Infrastructure.Skills;

namespace TotalRecall.Server.Handlers;

public sealed class SkillDeleteHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {"type":"string","description":"Skill GUID"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly ISkillClient _client;

    public SkillDeleteHandler(ISkillClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "skill_delete";
    public string Description => "Delete a skill by id (subject to cortex scope policy)";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("skill_delete requires an object arguments");

        var args = arguments.Value;
        var idStr = ArgumentParsing.ReadRequiredString(args, "id");
        if (!Guid.TryParse(idStr, out var id))
            throw new ArgumentException("skill_delete: id must be a GUID");

        await _client.DeleteAsync(id, ct).ConfigureAwait(false);

        var wire = new SkillDeleteWireResponse(true);
        var text = JsonSerializer.Serialize(wire, JsonContext.Default.SkillDeleteWireResponse);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }
}
