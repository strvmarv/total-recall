// src/TotalRecall.Server/Handlers/MemoryUnpinHandler.cs
//
// Pinned tier (spec 2026-06-09). Moves an entry from the pinned tier back to
// warm, resuming the normal decay/compaction lifecycle. The inverse of
// MemoryPinHandler. Mirrors MemoryPinHandler's structure: locate via
// MoveHelpers sweep, 4-step MoveAndReEmbed, optional compaction log + sync
// queue telemetry, MemoryMoveResultDto response. Throws ArgumentException on
// bad args; ErrorTranslator renders MCP errors.
//
// Only pinned entries may be unpinned — attempting to unpin a non-pinned entry
// throws ArgumentException without moving anything.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Sync;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

public sealed class MemoryUnpinHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":   {"type":"string","description":"Entry ID"},
            "type": {"type":"string","enum":["memory","knowledge"],"description":"Target content type (default: source type)"}
          },
          "required": ["id"]
        }
        """).RootElement.Clone();

    private readonly IStore _store;
    private readonly IVectorSearch _vec;
    private readonly IEmbedder _embedder;
    private readonly CompactionLog? _compactionLog;
    private readonly SyncQueue? _syncQueue;

    public MemoryUnpinHandler(
        IStore store,
        IVectorSearch vec,
        IEmbedder embedder,
        CompactionLog? compactionLog = null,
        SyncQueue? syncQueue = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _compactionLog = compactionLog;
        _syncQueue = syncQueue;
    }

    public string Name => "memory_unpin";
    public string Description =>
        "Unpin a pinned entry: moves it to the warm tier where the normal decay/compaction lifecycle resumes";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_unpin requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_unpin arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        ContentType? requestedType = null;
        var typeStr = ReadOptionalString(args, "type");
        if (typeStr is not null)
        {
            requestedType = TierNames.ParseContentType(typeStr)
                ?? throw new ArgumentException($"invalid type '{typeStr}' (expected memory|knowledge)");
        }

        ct.ThrowIfCancellationRequested();

        var located = MoveHelpers.Locate(_store, id)
            ?? throw new ArgumentException($"entry {id} not found");
        var (fromTier, fromType, entry) = located;
        if (!fromTier.IsPinned)
            throw new ArgumentException(
                $"entry {id} is not pinned (tier: {TierNames.TierName(fromTier)})");
        var targetType = requestedType ?? fromType;

        MoveHelpers.MoveAndReEmbed(
            _store, _vec, _embedder, entry, Tier.Pinned, fromType, Tier.Warm, targetType);

        if (_compactionLog is not null || _syncQueue is not null)
        {
            var nowUtc = DateTime.UtcNow;

            _compactionLog?.LogEvent(new CompactionLogEntry(
                SessionId: "unknown",
                SourceTier: "pinned",
                TargetTier: "warm",
                SourceEntryIds: new[] { id },
                TargetEntryId: id,
                DecayScores: new Dictionary<string, double> { [id] = entry.DecayScore },
                Reason: "manual_unpin",
                ConfigSnapshotId: "default"));

            _syncQueue?.Enqueue("compaction", "push", null,
                CompactionSyncPayload.Event(
                    entryId: id,
                    fromTier: "pinned",
                    toTier: "warm",
                    action: "unpin",
                    semanticDrift: null,
                    decayScore: entry.DecayScore,
                    timestampUtc: nowUtc));
        }

        var dto = new MemoryMoveResultDto(
            Id: id,
            FromTier: "pinned",
            FromContentType: TierNames.ContentTypeName(fromType),
            ToTier: "warm",
            ToContentType: TierNames.ContentTypeName(targetType),
            Success: true);
        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.MemoryMoveResultDto);

        return Task.FromResult(new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        });
    }

    private static string ReadRequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop))
            throw new ArgumentException($"{name} is required");
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString() ?? throw new ArgumentException($"{name} must be a string");
    }

    private static string? ReadOptionalString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Null) return null;
        if (prop.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{name} must be a string");
        return prop.GetString();
    }
}
