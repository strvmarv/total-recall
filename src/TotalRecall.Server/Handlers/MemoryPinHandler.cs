// src/TotalRecall.Server/Handlers/MemoryPinHandler.cs
//
// Pinned tier (spec 2026-06-09). Moves an entry from any tier into the
// pinned tier — the only entrance. Mirrors MemoryPromoteHandler's structure:
// locate via MoveHelpers sweep, 4-step MoveAndReEmbed, optional compaction
// log + sync queue telemetry, MemoryMoveResultDto response. Throws
// ArgumentException on bad args; ErrorTranslator renders MCP errors.
//
// The `scope` argument maps to the PROJECT column (NULL = global), not the
// identity `scope` column: "global" clears project, "project" sets it to
// the provided `project` arg (or keeps the entry's existing project).
// Already-pinned entries are an idempotent success (scope change still
// applied when requested).

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

public sealed class MemoryPinHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id":      {"type":"string","description":"Entry ID"},
            "scope":   {"type":"string","enum":["project","global"],"description":"Pin visibility: 'project' scopes to a project, 'global' clears the project (visible everywhere). Omitted: keep the entry's current project."},
            "project": {"type":"string","description":"Project name used with scope='project'. Omitted: keep the entry's existing project."},
            "type":    {"type":"string","enum":["memory","knowledge"],"description":"Target content type (default: source type)"}
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
    private readonly int _maxContentChars;

    public MemoryPinHandler(
        IStore store,
        IVectorSearch vec,
        IEmbedder embedder,
        CompactionLog? compactionLog = null,
        SyncQueue? syncQueue = null,
        int maxContentChars = PinnedTierLimits.DefaultMaxContentChars)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vec = vec ?? throw new ArgumentNullException(nameof(vec));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _compactionLog = compactionLog;
        _syncQueue = syncQueue;
        _maxContentChars = maxContentChars;
    }

    public string Name => "memory_pin";
    public string Description =>
        "Pin a memory or knowledge entry: marks it sticky in the hot tier where it is " +
        "always injected into session context and never decays or compacts";
    public JsonElement InputSchema => _inputSchema;

    public Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        if (!arguments.HasValue)
            throw new ArgumentException("memory_pin requires arguments", nameof(arguments));
        var args = arguments.Value;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("memory_pin arguments must be a JSON object", nameof(arguments));

        var id = ReadRequiredString(args, "id");
        if (id.Length == 0)
            throw new ArgumentException("id must be a non-empty string");

        var scope = ReadOptionalString(args, "scope");
        if (scope is not null && scope != "project" && scope != "global")
            throw new ArgumentException($"invalid scope '{scope}' (expected project|global)");
        var project = ReadOptionalString(args, "project");

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

        // Tier model v2 (Task 5): pinning is now the `sticky` flag on hot, not a
        // move to the retired pinned tier. The entry must end up in
        // (Hot, targetType); if it is already exactly there, no move is needed.
        var alreadyInHotTarget = fromTier.IsHot && fromType.Equals(targetType);
        var alreadySticky = alreadyInHotTarget && _store.IsSticky(targetType, id);
        if (!alreadySticky && entry.Content.Length > _maxContentChars)
            throw new ArgumentException(
                PinnedTierLimits.HotContentLimitMessage(_maxContentChars, entry.Content.Length));

        // Resolve scope/project BEFORE moving so a validation failure leaves
        // the entry untouched.
        string? effectiveProject = null;
        var clearProject = scope == "global";
        if (scope == "project")
        {
            effectiveProject = project ?? EntryMapping.OptString(entry.Project);
            if (string.IsNullOrEmpty(effectiveProject))
                throw new ArgumentException(
                    "scope='project' requires a project argument (the entry has no existing project)");
        }

        // Move into hot only when not already resident there (enforces the hot
        // cap via the check above; re-embeds under the hot vec table).
        if (!alreadyInHotTarget)
        {
            MoveHelpers.MoveAndReEmbed(
                _store, _vec, _embedder, entry, fromTier, fromType, Tier.Hot, targetType);
        }

        // Set the sticky flag (skip the write when already sticky).
        if (!alreadySticky)
            _store.SetSticky(targetType, id, true);

        // Post-move update: normalize decay_score to 1.0 on fresh pins and apply
        // the scope choice via the project column. Skip the write entirely when
        // the entry was already sticky and no scope change was requested, so we
        // don't spuriously bump updated_at or fire the FTS _fts_au trigger.
        var needsUpdate = !alreadySticky || scope is not null;
        if (needsUpdate)
        {
            _store.Update(Tier.Hot, targetType, id, new UpdateEntryOpts
            {
                DecayScore = alreadySticky ? (double?)null : 1.0,
                Project = effectiveProject,
                ClearProject = clearProject,
            });
        }

        // Sticky (pin) movements are not synced to Cortex (local-only policy).
        if (!alreadySticky && _compactionLog is not null)
        {
            var fromTierName = TierNames.TierName(fromTier);

            _compactionLog.LogEvent(new CompactionLogEntry(
                SessionId: "unknown",
                SourceTier: fromTierName,
                TargetTier: "hot",
                SourceEntryIds: new[] { id },
                TargetEntryId: id,
                DecayScores: new Dictionary<string, double> { [id] = entry.DecayScore },
                Reason: "manual_pin",
                ConfigSnapshotId: "default"));
        }

        var dto = new MemoryMoveResultDto(
            Id: id,
            FromTier: TierNames.TierName(fromTier),
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
