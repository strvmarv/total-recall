// src/TotalRecall.Server/Handlers/SessionRefreshHandler.cs
//
// Phase 1 Step 4 — MCP handler for the session_refresh tool. Accepts an
// optional task description, delegates to ISessionLifecycle.RefreshAsync,
// and returns updated context, change summary, and efficiency stats.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TotalRecall.Server.Handlers;

public sealed class SessionRefreshHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {
                "task": {
                    "type": "string",
                    "description": "Optional: current task description for task-aware ranking (Phase 2)"
                }
            },
            "required": []
        }
        """).RootElement.Clone();

    private readonly ISessionLifecycle _sessionLifecycle;

    public SessionRefreshHandler(ISessionLifecycle sessionLifecycle)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
    }

    public string Name => "session_refresh";

    public string Description =>
        "Refresh hot-tier context mid-session. Recalculates decay scores, runs warm sweep, promotes/demotes entries, and returns updated context with efficiency stats.";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        string? task = null;
        if (arguments is { } args && args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("task", out var taskEl)
                && taskEl.ValueKind == JsonValueKind.String)
            {
                task = taskEl.GetString();
            }
        }

        var result = await _sessionLifecycle.RefreshAsync(task, ct).ConfigureAwait(false);

        var jsonText = JsonSerializer.Serialize(result, JsonContext.Default.RefreshResult);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }
}
