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
// throws ArgumentException without moving anything. This is intentionally
// asymmetric with MemoryPinHandler, which is idempotent (pinning an already-pinned
// entry succeeds as a no-op): unpinning is an explicit one-way exit from the pinned
// tier, and a non-pinned target almost always signals a caller error rather than a
// benign double-call, so failing loudly is the safer default.

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
    // Intentionally retained but currently unused: pinned tier movements are not synced to
    // Cortex (local-only); kept for ctor-signature stability and future use.
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
        "Unpin a pinned entry: clears its sticky flag so it stays in the hot tier as an earned resident and the normal decay/compaction lifecycle resumes";
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
        var targetType = requestedType ?? fromType;

        // Tier model v2 (Task 5): "pinned" is now the sticky flag on hot. Only a
        // sticky-hot entry may be unpinned; anything else is not pinned.
        if (!fromTier.IsHot || !_store.IsSticky(fromType, id))
            throw new ArgumentException(
                $"entry {id} is not pinned (tier: {TierNames.TierName(fromTier)})");

        // Clear sticky in place — NO tier move. The entry stays in hot as an
        // earned resident and resumes the normal decay lifecycle.
        _store.SetSticky(fromType, id, false);

        // Sticky (unpin) movements are not synced to Cortex (local-only policy).
        if (_compactionLog is not null)
        {
            _compactionLog.LogEvent(new CompactionLogEntry(
                SessionId: "unknown",
                SourceTier: "hot",
                TargetTier: "hot",
                SourceEntryIds: new[] { id },
                TargetEntryId: id,
                DecayScores: new Dictionary<string, double> { [id] = entry.DecayScore },
                Reason: "manual_unpin",
                ConfigSnapshotId: "default"));
        }

        var dto = new MemoryMoveResultDto(
            Id: id,
            FromTier: "hot",
            FromContentType: TierNames.ContentTypeName(fromType),
            ToTier: "hot",
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
