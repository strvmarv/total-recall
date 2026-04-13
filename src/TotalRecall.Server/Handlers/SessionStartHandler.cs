// src/TotalRecall.Server/Handlers/SessionStartHandler.cs
//
// Plan 4 Task 4.10 — ports the `session_start` branch of
// src-ts/tools/session-tools.ts to the .NET Server. Thin wrapper around
// ISessionLifecycle.EnsureInitializedAsync (Task 4.3): runs the importer
// sweep + context assembly on first invocation, returns the cached
// SessionInitResult on every call thereafter.
//
// Design notes:
//
//   - Constructor depends on ISessionLifecycle (not the concrete type) so
//     unit tests can swap in a recording fake without wiring the full
//     importer + store + compaction-log graph.
//
//   - The TS handler catches ModelNotReadyError locally; in .NET that
//     translation is centralized in ErrorTranslator (Task 4.5) and wired
//     at the McpServer boundary. This handler deliberately lets any
//     exception bubble up.
//
//   - InputSchema is empty-object: session_start takes no arguments. The
//     handler therefore accepts null/absent `arguments` and does not
//     throw if the field is missing.
//
//   - Response is the serialized SessionInitResult (via source-gen) rather
//     than TS's `{sessionId, ...result}` splat. SessionInitResult already
//     carries the sessionId field (see SessionLifecycle.cs) so the wire
//     shape matches one-for-one.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>session_start</c> tool. Runs (or returns the
/// cached result of) session initialization and returns the assembled
/// context, hints, and tier summary.
/// </summary>
public sealed class SessionStartHandler : IToolHandler
{
    // Mirror of src-ts/tools/session-tools.ts:106-111 — session_start takes
    // no inputs.
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    private readonly ISessionLifecycle _sessionLifecycle;
    private readonly TotalRecall.Infrastructure.Sync.SyncService? _syncService;
    private readonly TotalRecall.Infrastructure.Sync.PeriodicSync? _periodicSync;

    public SessionStartHandler(
        ISessionLifecycle sessionLifecycle,
        TotalRecall.Infrastructure.Sync.SyncService? syncService = null,
        TotalRecall.Infrastructure.Sync.PeriodicSync? periodicSync = null)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        _syncService = syncService;
        _periodicSync = periodicSync;
    }

    public string Name => "session_start";

    public string Description =>
        "Initialize a session: sync host tool imports and assemble hot tier context";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        // session_start accepts no arguments; `arguments` may legitimately
        // be null or an empty object. Ignore either form.
        _ = arguments;

        // In cortex mode, pull newer user memories from Cortex before init
        if (_syncService is not null)
        {
            await _syncService.PullAsync(ct).ConfigureAwait(false);
            await _syncService.FlushAsync(ct).ConfigureAwait(false); // drain any pending from prior crash
        }

        var result = await _sessionLifecycle.EnsureInitializedAsync(ct).ConfigureAwait(false);

        _periodicSync?.Start();

        var jsonText = JsonSerializer.Serialize(result, JsonContext.Default.SessionInitResult);

        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }
}
