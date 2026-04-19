// src/TotalRecall.Server/Handlers/MemoryDemoteHandler.cs
//
// Plan 6 Task 6.0a — symmetric demote counterpart to MemoryPromoteHandler.
// Delegates the 4-step move to Infrastructure.Memory.MoveHelpers; only
// the direction gate flips (target must be strictly colder than source).

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

public sealed class MemoryDemoteHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":   {"type":"string","description":"Entry ID"},
            "tier": {"type":"string","enum":["warm","cold"],"description":"Target tier (default: cold)"},
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

    public MemoryDemoteHandler(
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

    public string Name => "memory_demote";
    public string Description => "Demote a memory or knowledge entry to a colder tier (re-embeds)";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_demote requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_demote arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        var tierStr = ReadOptionalString(args, "tier") ?? "cold";
        var toTier = TierNames.ParseTier(tierStr)
            ?? throw new ArgumentException($"invalid tier '{tierStr}' (expected warm|cold)");
        if (toTier.IsHot)
            throw new ArgumentException("cannot demote to hot (use memory_promote instead)");

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
        var targetType = requestedType ?? fromType;

        if (TierNames.WarmthRank(toTier) >= TierNames.WarmthRank(fromTier))
            throw new ArgumentException(
                $"cannot demote {TierNames.TierName(fromTier)} -> {TierNames.TierName(toTier)} (target must be colder)");

        MoveHelpers.MoveAndReEmbed(_store, _vec, _embedder, entry, fromTier, fromType, toTier, targetType);

        // Phase 6: compaction telemetry. Log locally and enqueue for cortex
        // push. Both sinks are optional — compositions that do not wire
        // them leave both null and skip this block entirely.
        if (_compactionLog is not null || _syncQueue is not null)
        {
            var fromTierName = TierNames.TierName(fromTier);
            var toTierName = TierNames.TierName(toTier);
            var nowUtc = DateTime.UtcNow;

            _compactionLog?.LogEvent(new CompactionLogEntry(
                SessionId: "unknown",
                SourceTier: fromTierName,
                TargetTier: toTierName,
                SourceEntryIds: new[] { id },
                TargetEntryId: id,
                DecayScores: new Dictionary<string, double> { [id] = entry.DecayScore },
                Reason: "manual_demote",
                ConfigSnapshotId: "default"));

            _syncQueue?.Enqueue("compaction", "push", null,
                CompactionSyncPayload.Event(
                    entryId: id,
                    fromTier: fromTierName,
                    toTier: toTierName,
                    action: "demote",
                    semanticDrift: null,
                    decayScore: entry.DecayScore,
                    timestampUtc: nowUtc));
        }

        var dto = new MemoryMoveResultDto(
            Id: id,
            FromTier: TierNames.TierName(fromTier),
            FromContentType: TierNames.ContentTypeName(fromType),
            ToTier: TierNames.TierName(toTier),
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
