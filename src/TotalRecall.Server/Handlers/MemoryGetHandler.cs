// src/TotalRecall.Server/Handlers/MemoryGetHandler.cs
//
// Plan 4 Task 4.8 — ports the `memory_get` branch of
// src-ts/tools/memory-tools.ts (plus src-ts/memory/get.ts) to the .NET
// Server. TS exposes a single `id` argument and searches all tables for
// the first row that matches. The .NET ISqliteStore API is tier-aware,
// so this handler iterates the 6 (tier, type) pairs and returns the
// first hit.
//
// Design notes:
//
//   - Response shape matches TS: `{tier, content_type, entry}` or JSON
//     `null` if no row matches. Serialized via the source-gen
//     MemoryGetResultDto registered in JsonContext.
//
//   - Like MemorySearchHandler, the AllTablePairs set is inlined rather
//     than exposed from Infrastructure.Schema (which keeps it internal).

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>memory_get</c> tool. Iterates every (tier, type)
/// pair looking for the requested entry id and returns the first match.
/// </summary>
public sealed class MemoryGetHandler : IToolHandler
{
    // Mirror of src-ts/tools/memory-tools.ts:69-78.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {"type":"string","description":"Entry ID"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly ISqliteStore _store;

    public MemoryGetHandler(ISqliteStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "memory_get";

    public string Description => "Retrieve a specific memory entry by ID";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_get requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_get arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        string jsonText = "null";
        foreach (var pair in EntryMapping.AllTablePairs)
        {
            var entry = _store.Get(pair.Tier, pair.Type, id);
            if (entry is null) continue;

            var dto = new MemoryGetResultDto(
                Tier: EntryMapping.TierName(pair.Tier),
                ContentType: EntryMapping.ContentTypeName(pair.Type),
                Entry: EntryMapping.ToEntryDto(entry));
            jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryGetResultDto);
            break;
        }

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static string ReadRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required");
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString() ?? throw new ArgumentException($"{name} must be a string");
    }

}
