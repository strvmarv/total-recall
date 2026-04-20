// src/TotalRecall.Server/Handlers/SkillListHandler.cs
//
// Plan 2 Task 11 — MCP handler for the `skill_list` tool. Proxies to
// ISkillClient.ListAsync and wraps the response in an MCP-local
// SkillListWireResponse that exposes a base64-encoded `nextCursor`
// pointing at the next page's skip offset.

using System.Globalization;
using System.Text;
using System.Text.Json;
using TotalRecall.Infrastructure.Skills;

namespace TotalRecall.Server.Handlers;

public sealed class SkillListHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "scope":  {"type":"string","description":"Restrict to a scope"},
            "tags":   {"type":"array","items":{"type":"string"}},
            "cursor": {"type":"string","description":"Opaque base64 cursor from a previous page"},
            "limit":  {"type":"number","description":"1..200, default 50"}
          }
        }
        """).RootElement.Clone();

    private readonly ISkillClient _client;

    public SkillListHandler(ISkillClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => "skill_list";
    public string Description => "List skills visible to the caller with base64 skip-cursor paging";
    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        string? scope = null;
        IReadOnlyList<string>? tags = null;
        string? cursor = null;
        var limit = 50;

        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            var args = arguments.Value;
            scope = ArgumentParsing.ReadOptionalString(args, "scope");
            tags = ArgumentParsing.ReadStringArray(args, "tags");
            cursor = ArgumentParsing.ReadOptionalString(args, "cursor");
            limit = ArgumentParsing.ReadOptionalInt(args, "limit", 1, 200) ?? 50;
        }

        var skip = 0;
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                skip = int.Parse(
                    Encoding.UTF8.GetString(Convert.FromBase64String(cursor)),
                    CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                throw new ArgumentException("skill_list: invalid cursor");
            }
            if (skip < 0)
                throw new ArgumentException("skill_list: cursor decoded to negative skip");
        }

        var resp = await _client.ListAsync(scope, tags, skip, limit, ct).ConfigureAwait(false);

        string? nextCursor = null;
        if (resp.Skip + resp.Items.Count < resp.Total)
        {
            var next = resp.Skip + resp.Items.Count;
            nextCursor = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture)));
        }

        var wire = new SkillListWireResponse(resp.Items, nextCursor);
        var text = JsonSerializer.Serialize(wire, JsonContext.Default.SkillListWireResponse);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = text } },
            IsError = false,
        };
    }
}
