// src/TotalRecall.Server/Handlers/MemoryFeedbackHandler.cs
//
// Task 1.6 — the WRITE side of the retrieval-quality feedback loop. The
// assistant calls memory_feedback with the retrievalId returned by
// memory_search / kb_search to record whether the retrieved memory was
// actually used. This sets outcome_used on the matching retrieval_events row,
// which Metrics.Compute reads to derive real precision / hit-rate.
//
// Design notes:
//   - Assistant-only. Registered in the MCP ToolRegistry (sqlite + cortex
//     composition modes) but deliberately NOT added to the Web ToolAllowlist,
//     so the browser can never reach it.
//   - Idempotent: an unknown retrievalId is a no-op that returns
//     {"updated": false} (no throw). RetrievalEventLog.UpdateOutcome returns
//     the affected-row count (0 / 1), which we map to the `updated` flag.
//   - retrievalId is required; missing/empty throws ArgumentException so
//     ErrorTranslator wraps it into the MCP tool-error shape (matching the
//     other handlers' validation contract).

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the assistant-only <c>memory_feedback</c> tool. Records the
/// outcome of a prior retrieval (referenced by the retrievalId returned from
/// memory_search / kb_search). Idempotent: an unknown id is a no-op.
/// </summary>
public sealed class MemoryFeedbackHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "retrievalId": {"type":"string","description":"Id returned by memory_search/kb_search"},
            "used":        {"type":"boolean","description":"Whether the retrieved memory was used (default true)"},
            "signal":      {"type":"string","description":"Optional free-text outcome signal"}
          },
          "required": ["retrievalId"]
        }
        """).RootElement.Clone();

    private readonly RetrievalEventLog _log;

    public MemoryFeedbackHandler(RetrievalEventLog log)
        => _log = log ?? throw new ArgumentNullException(nameof(log));

    public string Name => "memory_feedback";

    public string Description => "Record whether a retrieved memory (by retrievalId) was actually used";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_feedback requires a JSON object", nameof(arguments));

        var args = arguments.Value;
        if (!args.TryGetProperty("retrievalId", out var idEl)
            || idEl.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(idEl.GetString()))
            throw new ArgumentException("retrievalId is required");

        // `used` defaults to true; only an explicit `false` flips it.
        var used = !args.TryGetProperty("used", out var uEl) || uEl.ValueKind != JsonValueKind.False;
        string? signal = args.TryGetProperty("signal", out var sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString()
            : null;

        ct.ThrowIfCancellationRequested();
        var affected = _log.UpdateOutcome(idEl.GetString()!, new RetrievalOutcome(used, signal));

        var json = JsonSerializer.Serialize(
            new MemoryFeedbackResultDto(affected > 0), JsonContext.Default.MemoryFeedbackResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = json } },
            IsError = false,
        });
    }
}
