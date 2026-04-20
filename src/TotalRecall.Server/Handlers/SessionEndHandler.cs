// src/TotalRecall.Server/Handlers/SessionEndHandler.cs
//
// session_end: flush cortex sync queue and compact the hot tier.
//
// Compaction is heuristic (no LLM judgment): recalculate decay scores for
// all hot-memory entries and promote any whose score falls below the
// configured warm_threshold. This runs server-side so it fires whenever
// Claude calls the tool — no dependency on the SessionEnd hook giving
// Claude a response turn.
//
// When _store is null (unit-test construction or legacy callers) the
// handler falls back to the Plan 4 stub shape (zeroed counts) so existing
// tests continue to pass without modification.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Telemetry;

namespace TotalRecall.Server.Handlers;

public sealed class SessionEndHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    private readonly ISessionLifecycle _sessionLifecycle;
    private readonly TotalRecall.Infrastructure.Sync.SyncService? _syncService;
    private readonly IStore? _store;
    private readonly CompactionLog? _compactionLog;
    private readonly double _warmThreshold;
    private readonly double _decayConstantHours;

    public SessionEndHandler(
        ISessionLifecycle sessionLifecycle,
        IStore? store = null,
        CompactionLog? compactionLog = null,
        double warmThreshold = 0.3,
        double decayConstantHours = 168,
        TotalRecall.Infrastructure.Sync.SyncService? syncService = null)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        _store = store;
        _compactionLog = compactionLog;
        _warmThreshold = warmThreshold;
        _decayConstantHours = decayConstantHours;
        _syncService = syncService;
    }

    public string Name => "session_end";

    public string Description =>
        "End a session: compact the hot tier and return compaction results";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        _ = arguments;
        ct.ThrowIfCancellationRequested();

        if (_syncService is not null)
            await _syncService.FlushAsync(ct).ConfigureAwait(false);

        var (carryForward, promoted, discarded) = _store is not null
            ? CompactHotTier(ct)
            : (0, 0, 0);

        var dto = new SessionEndResultDto(
            SessionId: _sessionLifecycle.SessionId,
            CarryForward: carryForward,
            Promoted: promoted,
            Discarded: discarded);

        var jsonText = JsonSerializer.Serialize(dto, JsonContext.Default.SessionEndResultDto);
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }

    // Recalculate decay scores for all hot-memory entries, promote those
    // below warm_threshold to warm tier, and log each movement.
    private (int carryForward, int promoted, int discarded) CompactHotTier(CancellationToken ct)
    {
        var store = _store!;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessionId = _sessionLifecycle.SessionId;

        var hotEntries = store.List(Tier.Hot, ContentType.Memory);
        var promoted = 0;

        foreach (var entry in hotEntries)
        {
            ct.ThrowIfCancellationRequested();

            var score = CalculateDecayScore(entry, nowMs, _decayConstantHours);

            // Persist the refreshed score so the warm sweep at next session_start
            // has accurate ordering even if session_end isn't called again.
            try
            {
                store.Update(Tier.Hot, ContentType.Memory, entry.Id,
                    new UpdateEntryOpts { DecayScore = score });
            }
            catch (InvalidOperationException) { continue; } // concurrently deleted

            if (score >= _warmThreshold) continue;

            try
            {
                store.Move(Tier.Hot, ContentType.Memory, Tier.Warm, ContentType.Memory, entry.Id);
                _compactionLog?.LogEvent(new CompactionLogEntry(
                    SessionId: sessionId,
                    SourceTier: "hot",
                    TargetTier: "warm",
                    SourceEntryIds: new[] { entry.Id },
                    TargetEntryId: null,
                    DecayScores: new Dictionary<string, double> { [entry.Id] = score },
                    Reason: "session_end_decay",
                    ConfigSnapshotId: ""));
                promoted++;
            }
            catch (InvalidOperationException) { } // concurrently deleted — skip
        }

        var carryForward = store.Count(Tier.Hot, ContentType.Memory);
        return (carryForward, promoted, discarded: 0);
    }

    // Inline implementation of the Decay.fs formula for C# interop simplicity.
    // Keep in sync with src/TotalRecall.Core/Decay.fs.
    private static double CalculateDecayScore(Entry entry, long nowMs, double decayConstantHours)
    {
        var hoursSinceAccess = (nowMs - entry.LastAccessedAt) / 3_600_000.0;
        var timeFactor = Math.Exp(-hoursSinceAccess / decayConstantHours);
        var freqFactor = 1.0 + Math.Log(1.0 + entry.AccessCount) / Math.Log(2.0);
        var typeWeight = entry.EntryType switch
        {
            var t when t.IsCorrection => 1.5,
            var t when t.IsPreference => 1.3,
            var t when t.IsImported   => 1.1,
            var t when t.IsIngested   => 0.9,
            var t when t.IsSurfaced   => 0.8,
            _                         => 1.0, // Decision, Compacted
        };
        return timeFactor * freqFactor * typeWeight;
    }
}
