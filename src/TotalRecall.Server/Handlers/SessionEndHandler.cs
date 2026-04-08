// src/TotalRecall.Server/Handlers/SessionEndHandler.cs
//
// Plan 4 Task 4.10 — ports the `session_end` branch of
// src-ts/tools/session-tools.ts to the .NET Server. In TS this handler
// drives compactHotTier (warm-tier rollover, semantic promotion, etc.);
// that pipeline has NOT been ported to .NET Infrastructure as of Plan 4,
// so this handler is a no-op bookkeeping stub.
//
// TODO(Plan 5+): port compactHotTier from src-ts/compaction/compactor.ts
// and wire it here. For Plan 4 the .NET server does not perform
// server-side compaction — the host's compactor subagent handles that
// responsibility per the spec's Flow 2. Returning a deterministic stub
// keeps the wire protocol honest so the host can still complete its
// compaction workflow against the .NET server.
//
// Design notes:
//
//   - ISessionLifecycle now exposes SessionId directly (Task 4.10
//     backfill), so we do not have to force EnsureInitializedAsync to run
//     just to echo the id back.
//
//   - Response shape is SessionEndResultDto (JsonContext source-gen).
//     Fields mirror TS session-tools.ts:355-368 minus `details`, which
//     would need to carry the full compactHotTier result object; we omit
//     it until Plan 5+ lands the real implementation.
//
//   - The handler never throws — it is safe for the host to call even
//     when the .NET server has nothing to compact.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>session_end</c> tool. Plan 4 stub: returns a
/// zeroed compaction summary tagged with the current session id.
/// </summary>
public sealed class SessionEndHandler : IToolHandler
{
    // Mirror of src-ts/tools/session-tools.ts:113-118 — session_end takes
    // no inputs.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    private readonly ISessionLifecycle _sessionLifecycle;

    public SessionEndHandler(ISessionLifecycle sessionLifecycle)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
    }

    public string Name => "session_end";

    public string Description =>
        "End a session: compact the hot tier and return compaction results";

    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        _ = arguments; // session_end takes no inputs.

        ct.ThrowIfCancellationRequested();

        // TODO(Plan 5+): replace with real compactHotTier(...) result.
        var dto = new SessionEndResultDto(
            SessionId: _sessionLifecycle.SessionId,
            CarryForward: 0,
            Promoted: 0,
            Discarded: 0);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.SessionEndResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
