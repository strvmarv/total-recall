// src/TotalRecall.Server/Handlers/MemoryDeleteHandler.cs
//
// Plan 4 Task 4.8 — ports the `memory_delete` branch of
// src-ts/tools/memory-tools.ts (plus src-ts/memory/delete.ts) to the
// .NET Server. Locates the row via an AllTablePairs sweep, then deletes
// the metadata row plus the corresponding vec0 embedding row.
//
// Design notes:
//
//   - Constructor takes (ISqliteStore, IVectorSearch) so we can clean up
//     the embedding row too. IVectorSearch.DeleteEmbedding already exists
//     on the interface and is a silent no-op when the vec0 row is missing,
//     so this is safe regardless of whether the row was ever embedded.
//
//   - Response payload matches TS `{deleted: bool}`. Built by hand.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>memory_delete</c> tool. Finds the row by id
/// across all tier/type tables, deletes the metadata row, and clears
/// the matching vec0 embedding row.
/// </summary>
public sealed class MemoryDeleteHandler : IToolHandler
{
    // Mirror of src-ts/tools/memory-tools.ts:95-104.
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
    private readonly IVectorSearch _vectorSearch;

    public MemoryDeleteHandler(ISqliteStore store, IVectorSearch vectorSearch)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vectorSearch = vectorSearch ?? throw new ArgumentNullException(nameof(vectorSearch));
    }

    public string Name => "memory_delete";

    public string Description => "Delete a memory entry by ID";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_delete requires arguments", nameof(arguments));

        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_delete arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        (Tier Tier, ContentType Type)? located = null;
        foreach (var pair in EntryMapping.AllTablePairs)
        {
            var row = _store.Get(pair.Tier, pair.Type, id);
            if (row is not null)
            {
                located = (pair.Tier, pair.Type);
                break;
            }
        }

        if (located is null)
        {
            return Task.FromResult(new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = "{\"deleted\":false}" } },
                IsError = false,
            });
        }

        _store.Delete(located.Value.Tier, located.Value.Type, id);
        _vectorSearch.DeleteEmbedding(located.Value.Tier, located.Value.Type, id);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = "{\"deleted\":true}" } },
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
