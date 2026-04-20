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

    public SessionStartHandler(
        ISessionLifecycle sessionLifecycle,
        TotalRecall.Infrastructure.Sync.PeriodicSync? periodicSync = null)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        _periodicSync = periodicSync;
    }

    public string Name => "session_start";

    public string Description =>
        "Initialize a session: sync host tool imports and assemble hot tier context";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        _ = arguments;
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
