// src/TotalRecall.Server/Handlers/KbSummarizeHandler.cs
//
// Plan 6 Task 6.0b — parity port of src-ts/tools/kb-tools.ts:298-309
// (kb_summarize). This tool was never ported during Plan 5; the SKILL.md
// `kb search` `needsSummary: true` conditional has been pointing at a
// missing tool until now. Plan 6 closes the gap.
//
// Behavior: look up a cold/knowledge entry by id and update its `summary`
// field. On not-found, returns the TS-compatible error envelope
// `{"error": "Collection not found: <id>"}` (NOT a thrown exception —
// matches the TS shape exactly so MCP clients keying off the error
// payload keep working).

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;

namespace TotalRecall.Server.Handlers;

public sealed class KbSummarizeHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "collection": {"type":"string","description":"Collection id"},
            "summary":    {"type":"string","description":"Summary text to write"}
          },
          "required": ["collection","summary"]
        }
        """).RootElement.Clone();

    private readonly IStore _store;

    public KbSummarizeHandler(IStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Name => "kb_summarize";
    public string Description => "Set the summary field on a knowledge base collection";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("kb_summarize requires arguments object");

        var args = arguments.Value;
        if (!args.TryGetProperty("collection", out var cEl) || cEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("collection is required");
        var collectionId = cEl.GetString();
        if (string.IsNullOrEmpty(collectionId))
            throw new ArgumentException("collection must be a non-empty string");

        if (!args.TryGetProperty("summary", out var sEl) || sEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("summary is required");
        var summary = sEl.GetString();
        if (summary is null)
            throw new ArgumentException("summary must be a string");

        ct.ThrowIfCancellationRequested();

        var entry = _store.Get(Tier.Cold, ContentType.Knowledge, collectionId);
        if (entry is null)
        {
            // TS-compatible not-found shape — NOT a throw. The agent layer
            // keys off the `error` field per kb-tools.ts:304.
            var errDto = new KbErrorDto(Error: $"Collection not found: {collectionId}");
            var errText = JsonSerializer.Serialize(errDto, JsonContext.Default.KbErrorDto);
            return Task.FromResult(new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = errText } },
                IsError = false,
            });
        }

        _store.Update(
            Tier.Cold,
            ContentType.Knowledge,
            collectionId,
            new UpdateEntryOpts { Summary = summary });

        var dto = new KbSummarizeResultDto(Collection: collectionId, Summarized: true);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.KbSummarizeResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
