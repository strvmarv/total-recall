// src/TotalRecall.Server/Handlers/MemoryLineageHandler.cs
//
// Plan 6 Task 6.0a — ports `memory lineage` (LineageCommand.cs) to MCP.
// Builds the same ancestry tree by recursing on
// ICompactionLogReader.GetByTargetEntryId, depth-capped at 10 with a
// visited-set to break cycles (mirrors TS extra-tools.ts:129-164 and the
// CLI verb's BuildLineage). Emits a nested LineageNodeDto tree.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryLineageHandler : IToolHandler
{
    // Matches LineageCommand.MaxDepth — keep the two in sync if TS bumps.
    internal const int MaxDepth = 10;

    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {"type":"string","description":"Entry ID"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly ICompactionLogReader _log;

    public MemoryLineageHandler(ICompactionLogReader log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "memory_lineage";
    public string Description => "Show compaction ancestry for an entry";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_lineage requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_lineage arguments must be a JSON object", nameof(arguments));
        if (!args.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            throw new ArgumentException("id is required");
        var id = idEl.GetString() ?? throw new ArgumentException("id must be a string");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        var tree = BuildLineage(_log, id, 0, new HashSet<string>(StringComparer.Ordinal));
        var jsonText = JsonSerializer.Serialize(tree, JsonContext.Default.LineageNodeDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    internal static LineageNodeDto BuildLineage(
        ICompactionLogReader reader,
        string id,
        int depth,
        HashSet<string> visited)
    {
        if (depth >= MaxDepth)
        {
            return new LineageNodeDto(
                Id: id, CompactionLogId: null, Reason: null, Timestamp: null,
                SourceTier: null, TargetTier: null, Sources: Array.Empty<LineageNodeDto>());
        }
        if (!visited.Add(id))
        {
            return new LineageNodeDto(
                Id: id, CompactionLogId: null, Reason: null, Timestamp: null,
                SourceTier: null, TargetTier: null, Sources: null);
        }

        var row = reader.GetByTargetEntryId(id);
        if (row is null)
        {
            return new LineageNodeDto(
                Id: id, CompactionLogId: null, Reason: null, Timestamp: null,
                SourceTier: null, TargetTier: null, Sources: null);
        }

        var sources = new LineageNodeDto[row.SourceEntryIds.Count];
        for (int i = 0; i < row.SourceEntryIds.Count; i++)
        {
            sources[i] = BuildLineage(reader, row.SourceEntryIds[i], depth + 1, visited);
        }

        return new LineageNodeDto(
            Id: id,
            CompactionLogId: row.Id,
            Reason: row.Reason,
            Timestamp: row.Timestamp,
            SourceTier: row.SourceTier,
            TargetTier: row.TargetTier,
            Sources: sources);
    }
}
