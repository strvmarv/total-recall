// src/TotalRecall.Server/Handlers/SessionStartHandler.cs
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
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    private readonly ISessionLifecycle _sessionLifecycle;
    private readonly TotalRecall.Infrastructure.Sync.PeriodicSync? _periodicSync;
    private readonly TotalRecall.Infrastructure.Sync.SyncService? _syncService;

    public SessionStartHandler(
        ISessionLifecycle sessionLifecycle,
        TotalRecall.Infrastructure.Sync.PeriodicSync? periodicSync = null,
        TotalRecall.Infrastructure.Sync.SyncService? syncService = null)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        _periodicSync = periodicSync;
        _syncService = syncService;
    }

    public string Name => "session_start";

    public string Description =>
        "Initialize a session: sync host tool imports and assemble hot tier context";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        _ = arguments;
        var result = await _sessionLifecycle.EnsureInitializedAsync(ct).ConfigureAwait(false);

        // Startup flush: drain any backlog that survived a prior process.
        // FlushAsync is internally fault-tolerant (CortexUnreachableException
        // marks items failed and returns; per-type drains can't take each
        // other down). Wrap defensively anyway — session_start must not fail
        // because the sync queue couldn't be drained.
        if (_syncService is not null)
        {
            try { await _syncService.FlushAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }

        _periodicSync?.Start();
        var jsonText = JsonSerializer.Serialize(result, JsonContext.Default.SessionInitResult);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }
}
