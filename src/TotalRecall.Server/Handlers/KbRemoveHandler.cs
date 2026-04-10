// src/TotalRecall.Server/Handlers/KbRemoveHandler.cs
//
// Plan 6 Task 6.0b — ports `kb remove` (Cli/Commands/Kb/RemoveCommand.cs)
// to MCP. Parity port of src-ts/tools/kb-tools.ts:198-216 (kb_remove).
//
// Always cascades: enumerates all cold_knowledge rows, deletes any whose
// ParentId == id OR CollectionId == id (excluding the target itself), then
// deletes the target. Returns cascaded_count = number of children removed.
// Unlike the CLI version, the MCP handler does not gate behind a --yes
// confirmation: MCP callers are non-interactive by definition and the
// agent layer is responsible for any user-facing confirm prompts.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class KbRemoveHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {"type":"string","description":"Entry id (collection root or chunk)"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly IStore _store;
    private readonly IVectorSearch _vec;

    public KbRemoveHandler(IStore store, IVectorSearch vec)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
    }

    public string Name => "kb_remove";
    public string Description => "Remove a knowledge base entry (cascades to children)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_remove requires arguments object");

        var args = arguments.Value;
        if (!args.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("id is required");
        var id = idEl.GetString();
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        var entry = _store.Get(Tier.Cold, ContentType.Knowledge, id);
        if (entry is null)
            throw new ArgumentException($"Entry not found: {id}");

        // Cascade: identical to RemoveCommand.cs:163-178.
        var all = _store.List(Tier.Cold, ContentType.Knowledge, null);
        var children = new List<Entry>();
        foreach (var e in all)
        {
            if (IsChildOf(e, id)) children.Add(e);
        }
        var cascadeCount = 0;
        foreach (var child in children)
        {
            var childRowid = _store.GetInternalKey(Tier.Cold, ContentType.Knowledge, child.Id);
            if (childRowid is not null)
                _vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, childRowid.Value);
            _store.Delete(Tier.Cold, ContentType.Knowledge, child.Id);
            cascadeCount++;
        }

        var rootRowid = _store.GetInternalKey(Tier.Cold, ContentType.Knowledge, id);
        if (rootRowid is not null)
            _vec.DeleteEmbedding(Tier.Cold, ContentType.Knowledge, rootRowid.Value);
        _store.Delete(Tier.Cold, ContentType.Knowledge, id);

        var dto = new KbRemoveResultDto(Id: id, Removed: true, CascadedCount: cascadeCount);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.KbRemoveResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static bool IsChildOf(Entry e, string id)
    {
        if (e.Id == id) return false;
        if (FSharpOption<string>.get_IsSome(e.ParentId) && e.ParentId.Value == id) return true;
        if (FSharpOption<string>.get_IsSome(e.CollectionId) && e.CollectionId.Value == id) return true;
        return false;
    }
}
