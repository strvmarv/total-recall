// src/TotalRecall.Server/Handlers/MemoryHistoryHandler.cs
//
// Plan 6 Task 6.0a — ports `memory history` (HistoryCommand.cs) to MCP.
// Read-only over ICompactionLogReader.GetRecentMovements; no embedder or
// vector search needed. Args: { limit? (default 50, clamped to 1..1000) }.

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryHistoryHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "limit": {"type":"number","description":"Max rows to return (1..1000, default 50)"}
          }
        }
        """).RootElement.Clone();

    private readonly ICompactionLogReader _log;

    public MemoryHistoryHandler(ICompactionLogReader log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "memory_history";
    public string Description => "Show recent compaction movements";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        int limit = 50;
        if (arguments.HasValue && arguments.Value.ValueKind == JsonValueKind.Object)
        {
            if (arguments.Value.TryGetProperty("limit", out var limEl))
            {
                if (limEl.ValueKind != JsonValueKind.Number)
                    throw new ArgumentException("limit must be a number");
                limit = limEl.GetInt32();
            }
        }
        if (limit < 1 || limit > 1000)
            throw new ArgumentException("limit must be between 1 and 1000");

        ct.ThrowIfCancellationRequested();

        var rows = _log.GetRecentMovements(limit);
        var dtoRows = new CompactionMovementDto[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            dtoRows[i] = MemoryInspectHandler.MapMovement(rows[i]);
        }

        var dto = new MemoryHistoryResultDto(Movements: dtoRows, Count: dtoRows.Length);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryHistoryResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }
}
