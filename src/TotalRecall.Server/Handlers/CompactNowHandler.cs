// src/TotalRecall.Server/Handlers/CompactNowHandler.cs
//
// Plan 6 Task 6.0d — thin informational mirror of the CLI CompactCommand
// stub. This handler INTENTIONALLY does not perform any mechanical
// compaction work:
//
//   - The mechanical compaction operations (warm sweep, decay-based
//     eviction, promote/demote scoring) live in the SessionLifecycle
//     stubs carried forward from Plan 5 (the 8 TODOs that were
//     deferred per spec Flow 2). Those stubs must be filled before a
//     real compact_now can be implemented.
//
//   - Per spec Flow 2, compaction is HOST-ORCHESTRATED: the server
//     exposes session_context + memory_promote/demote/store/delete as
//     primitives, and the host tool's LLM layer drives the actual
//     judgment. This handler therefore exists primarily so that
//     tools/list advertises the full MCP surface and probing hosts
//     learn the semantics rather than getting an "unknown tool" error.
//
// If a real compact_now is ever implemented in a future plan, it would:
//   1. Sweep warm tier for entries past cold_decay_days → demote/delete.
//   2. Score hot-tier entries via decay + access_count → promote to
//      warm/cold or merge into a compaction target entry.
//   3. Write a CompactionLog row per movement.
//   4. Return counts via a richer CompactNowResultDto shape.
//
// For now it returns a single-message stub DTO: {compacted: 0, message: ...}

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

public sealed class CompactNowHandler : IToolHandler
{
    private const string StubMessage =
        "Compaction is host-orchestrated. Use session_context to retrieve hot "
        + "tier entries and apply mechanical or LLM-judged decisions via "
        + "memory_promote/demote/store/delete.";

    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{}}
        """).RootElement.Clone();

    public string Name => "compact_now";
    public string Description => "Host-orchestrated compaction trigger (informational stub)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dto = new CompactNowResultDto(Compacted: 0, Message: StubMessage);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.CompactNowResultDto);
        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
