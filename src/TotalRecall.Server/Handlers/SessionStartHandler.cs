// src/TotalRecall.Server/Handlers/SessionStartHandler.cs
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Transcripts;
using TotalRecall.Infrastructure.WhatsNew;

namespace TotalRecall.Server.Handlers;

/// <summary>
/// MCP handler for the <c>session_start</c> tool. Runs (or returns the
/// cached result of) session initialization and returns the assembled
/// context, hints, and tier summary.
/// </summary>
public sealed class SessionStartHandler : IToolHandler
{
    private static readonly JsonElement _inputSchema = JsonDocument.Parse("""
        {"type":"object","properties":{},"required":[]}
        """).RootElement.Clone();

    private readonly ISessionLifecycle _sessionLifecycle;
    private readonly TotalRecall.Infrastructure.Sync.PeriodicSync? _periodicSync;
    private readonly TotalRecall.Infrastructure.Sync.SyncService? _syncService;
    private readonly WhatsNewService? _whatsNew;
    private readonly TimeProvider _time;
    private readonly IArchivePass? _archivePass;

    /// <summary>A call that took longer than this gets no notice: the model's
    /// own tool timeout is near, and a notice that is not shown must not be recorded.</summary>
    private static readonly TimeSpan NoticeDeadline = TimeSpan.FromSeconds(30);

    public SessionStartHandler(
        ISessionLifecycle sessionLifecycle,
        TotalRecall.Infrastructure.Sync.PeriodicSync? periodicSync = null,
        TotalRecall.Infrastructure.Sync.SyncService? syncService = null,
        WhatsNewService? whatsNew = null,
        TimeProvider? timeProvider = null,
        IArchivePass? archivePass = null)
    {
        _sessionLifecycle = sessionLifecycle
            ?? throw new ArgumentNullException(nameof(sessionLifecycle));
        _periodicSync = periodicSync;
        _syncService = syncService;
        _whatsNew = whatsNew;
        _time = timeProvider ?? TimeProvider.System;
        _archivePass = archivePass;
    }

    public string Name => "session_start";

    public string Description =>
        "Initialize a session: sync host tool imports and assemble hot tier context";

    public JsonElement InputSchema => _inputSchema;

    public async Task<ToolCallResult> ExecuteAsync(JsonElement? arguments, CancellationToken ct)
    {
        var started = _time.GetTimestamp();
        var result = await _sessionLifecycle.EnsureInitializedAsync(ct).ConfigureAwait(false);

        // Startup flush: drain any backlog that survived a prior process.
        // FlushAsync is internally fault-tolerant (CortexUnreachableException
        // marks items failed and returns; per-type drains can't take each
        // other down). Wrap defensively anyway — session_start must not fail
        // because the sync queue couldn't be drained.
        if (_syncService is not null)
        {
            try { await _syncService.FlushAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { /* best-effort */ }
        }

        _periodicSync?.Start();

        // The notice is built per call, outside the cached init result. It is
        // recorded as seen only after the response is built and only when the
        // call was quick enough for the model to still show it. Nothing here may
        // fail session_start.
        PendingNotice? pending = null;
        try
        {
            if (_whatsNew is not null)
            {
                pending = _whatsNew.Check(ReadHost(arguments));
                if (pending is not null && _time.GetElapsedTime(started) > NoticeDeadline)
                    pending = null;
                if (pending is not null)
                    result = result with { WhatsNew = new WhatsNewDto(pending.Kind, pending.Text, pending.Relay) };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { pending = null; }

        var jsonText = JsonSerializer.Serialize(result, JsonContext.Default.SessionInitResult);
        if (pending is not null)
        {
            try { pending.MarkSeen(); }
            catch { /* best-effort: an unrecorded notice is simply offered again */ }
        }

        // The transcript archive's pass, after the response is built. Kick returns at once (it reads one
        // sentinel file and may start a background task); nothing here waits for it or may fail session_start.
        if (_archivePass is not null)
        {
            try { _archivePass.Kick(); }
            catch (Exception ex)
            {
                try { ExceptionLogger.LogChain("[total-recall] transcripts pass: kick", ex); } catch { /* logging must not throw */ }
            }
        }
        return new ToolCallResult
        {
            Content = new[] { new ToolContent { Type = "text", Text = jsonText } },
            IsError = false,
        };
    }

    // The optional string argument `host` (the calling client's name). Every
    // other shape of arguments (absent, null, non-object, non-string value) is
    // read as "no host".
    private static string? ReadHost(JsonElement? arguments)
    {
        if (arguments is { } args
            && args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("host", out var hostEl)
            && hostEl.ValueKind == JsonValueKind.String)
        {
            return hostEl.GetString();
        }
        return null;
    }
}
