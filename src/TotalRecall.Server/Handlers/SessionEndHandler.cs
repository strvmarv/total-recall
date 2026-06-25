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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Memory;
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

    // Delegate hot→warm compaction to the shared HotTierCompactor, which routes
    // through the canonical Decay.calculateDecayScore formula in Decay.fs.
    private (int carryForward, int promoted, int discarded) CompactHotTier(CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var r = HotTierCompactor.Compact(
            _store!, _sessionLifecycle.SessionId, nowMs,
            _warmThreshold, _decayConstantHours, _compactionLog,
            reason: "session_end_decay", ct: ct);
        return (r.CarryForward, r.Promoted, r.Discarded);
    }
}
