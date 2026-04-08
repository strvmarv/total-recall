// src/TotalRecall.Server/Handlers/MemoryInspectHandler.cs
//
// Plan 6 Task 6.0a — ports the `memory inspect` CLI verb
// (src/TotalRecall.Cli/Commands/Memory/InspectCommand.cs) to an MCP tool.
// Sweeps the 6 (tier, type) tables looking for the requested id, then
// returns a full-fat detail DTO. Optional compaction_history is sourced
// from ICompactionLogReader.GetByTargetEntryId and attached as a
// CompactionMovementDto when a matching row exists (mirrors what
// InspectCommand's Plan 5.5 TODO gestured at).

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Collections;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryInspectHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": {"type":"string","description":"Entry ID"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly ISqliteStore _store;
    private readonly ICompactionLogReader _log;

    public MemoryInspectHandler(ISqliteStore store, ICompactionLogReader log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => "memory_inspect";
    public string Description => "Show full details for a memory or knowledge entry";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_inspect requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_inspect arguments must be a JSON object", nameof(arguments));

        if (!args.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            throw new ArgumentException("id is required");
        var id = idProp.GetString() ?? throw new ArgumentException("id must be a string");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        ct.ThrowIfCancellationRequested();

        var located = MoveHelpers.Locate(_store, id);
        if (located is null)
        {
            // Not-found is represented as a JSON null body, matching
            // memory_get's convention for the same condition.
            return Task.FromResult(new ToolCallResult
            {
                Content = new[] { new ToolContent { Type = "text", Text = "null" } },
                IsError = false,
            });
        }

        var (tier, type, e) = located.Value;
        var history = _log.GetByTargetEntryId(id);
        CompactionMovementDto? historyDto = history is null
            ? null
            : MapMovement(history);

        var dto = new MemoryInspectResultDto(
            Id: e.Id,
            Tier: EntryMapping.TierName(tier),
            ContentType: EntryMapping.ContentTypeName(type),
            Content: e.Content,
            Summary: EntryMapping.OptString(e.Summary),
            Source: EntryMapping.OptString(e.Source),
            SourceTool: EntryMapping.SourceToolName(e.SourceTool),
            Project: EntryMapping.OptString(e.Project),
            Tags: ListModule.ToArray(e.Tags),
            CreatedAt: e.CreatedAt,
            UpdatedAt: e.UpdatedAt,
            LastAccessedAt: e.LastAccessedAt,
            AccessCount: e.AccessCount,
            DecayScore: e.DecayScore,
            ParentId: EntryMapping.OptString(e.ParentId),
            CollectionId: EntryMapping.OptString(e.CollectionId),
            Metadata: string.IsNullOrEmpty(e.MetadataJson) ? "{}" : e.MetadataJson,
            CompactionHistory: historyDto);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryInspectResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    internal static CompactionMovementDto MapMovement(CompactionMovementRow row) =>
        new(
            Id: row.Id,
            Timestamp: row.Timestamp,
            SessionId: row.SessionId,
            SourceTier: row.SourceTier,
            TargetTier: row.TargetTier,
            SourceEntryIds: row.SourceEntryIds.ToArray(),
            TargetEntryId: row.TargetEntryId,
            Reason: row.Reason);
}
