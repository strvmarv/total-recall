using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>The trigger <c>session_start</c> calls (implemented by <see cref="ArchivePass"/>); returns at once.</summary>
public interface IArchivePass
{
    void Kick();
}

/// <summary>
/// Overrides of the pass's limits and of the embedder inspection (tests). Null means the default:
/// <see cref="ArchivePass.DefaultIngestBudget"/>, <see cref="ArchivePass.DefaultEmbedBudget"/>,
/// <see cref="ArchivePass.DefaultMinInterval"/> and the context's own <c>Inspect</c>.
/// </summary>
public sealed record ArchivePassOptions(
    TimeSpan? IngestBudget = null,
    EmbedBudget? EmbedBudget = null,
    TimeSpan? MinInterval = null,
    Func<Core.Config.EmbeddingConfig, int?, EmbedderInspectionResult>? Inspect = null);

/// <summary>
/// What one pass reads, built when the pass runs (never in <see cref="ArchivePass.Kick"/>): the <c>[transcripts]</c>
/// settings, the archive's location (null when it cannot be resolved), the storage mode, the effective
/// <c>[embedding]</c> config, how to build and inspect an embedder (with an intra-op thread count), whether the memory
/// store's re-embed is running, and the adapters to ingest.
/// </summary>
public sealed record ArchivePassContext(
    TranscriptsSettings Settings,
    TranscriptLocation? Location,
    string StorageMode,
    Core.Config.EmbeddingConfig Embedding,
    Func<Core.Config.EmbeddingConfig, int?, EmbedderInspectionResult> Inspect,
    Func<bool> MemoryReindexRunning,
    Func<IReadOnlyList<ITranscriptAdapter>> Adapters);

/// <summary>
/// What the last pass did. <paramref name="At"/> is when it started; <paramref name="Outcome"/> is one of
/// <see cref="ArchivePass.OutcomeDone"/>, <see cref="ArchivePass.OutcomeOff"/>, <see cref="ArchivePass.OutcomePostgres"/>,
/// <see cref="ArchivePass.OutcomeAbsent"/>, <see cref="ArchivePass.OutcomeUnavailable"/>,
/// <see cref="ArchivePass.OutcomeCancelled"/> or <see cref="ArchivePass.OutcomeFailed"/>;
/// <paramref name="SessionsIngested"/> counts new and updated sessions; <paramref name="Embed"/> is null when the
/// embed step was not reached.
/// </summary>
public sealed record ArchivePassResult(DateTimeOffset At, string Outcome, int SessionsIngested, EmbedRun? Embed);

/// <summary>
/// The <c>session_start</c> trigger of the archive (search the archive, spec section 4). <see cref="Kick"/> returns at
/// once: it does nothing when the archive is off (one <c>File.Exists</c> through <c>isEnabled</c>), when a pass runs in
/// this process or when the last one started less than the minimum interval ago; otherwise it starts the pass with
/// <see cref="Task.Run(Func{Task})"/>. The pass re-checks the settings and the storage mode, opens the archive without
/// creating it, ingests each adapter's sessions under one time budget for the whole pass, then evaluates the
/// <see cref="EmbedGate"/> (rules 1 and 2 from config, before any embedder is built) and runs the
/// <see cref="TranscriptEmbedWorker"/> under the embed budget on a dedicated background thread at below-normal priority
/// with its own embedder (2 intra-op threads), disposed when the step ends. Each step logs its failures through
/// <see cref="ExceptionLogger"/> (stderr, never stdout) and nothing it does can fail the caller.
/// </summary>
public sealed class ArchivePass : IArchivePass, IDisposable
{
    public static readonly TimeSpan DefaultIngestBudget = TimeSpan.FromSeconds(15);
    public static readonly EmbedBudget DefaultEmbedBudget = new(TimeSpan.FromSeconds(30), 256);
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMinutes(10);

    /// <summary>The intra-op threads of the pass's own local embedder.</summary>
    public const int PassIntraOpThreads = 2;

    public const string OutcomeDone = "done";
    public const string OutcomeOff = "off";
    public const string OutcomePostgres = "postgres";
    public const string OutcomeAbsent = "absent";
    public const string OutcomeUnavailable = "unavailable";
    public const string OutcomeCancelled = "cancelled";
    public const string OutcomeFailed = "failed";

    private const string LogPrefix = "[total-recall] transcripts pass";

    private readonly Func<ArchivePassContext?> _context;
    private readonly Func<bool> _isEnabled;
    private readonly TimeSpan _ingestBudget;
    private readonly EmbedBudget _embedBudget;
    private readonly TimeSpan _minInterval;
    private readonly Func<Core.Config.EmbeddingConfig, int?, EmbedderInspectionResult>? _inspect;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private int _running;
    private int _disposed;
    private DateTimeOffset? _lastStart;
    private Task? _current;
    private ArchivePassResult? _last;

    /// <param name="context">Builds the pass's context; evaluated by the pass, never by <see cref="Kick"/>.</param>
    /// <param name="isEnabled">The cheap gate <see cref="Kick"/> checks (the sentinel's <c>File.Exists</c>).</param>
    /// <param name="options">Limits and the inspection override; null for the defaults.</param>
    /// <param name="time">The clock of the interval, the result's time and the embed worker.</param>
    public ArchivePass(
        Func<ArchivePassContext?> context,
        Func<bool> isEnabled,
        ArchivePassOptions? options = null,
        TimeProvider? time = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
        _ingestBudget = options?.IngestBudget ?? DefaultIngestBudget;
        _embedBudget = options?.EmbedBudget ?? DefaultEmbedBudget;
        _minInterval = options?.MinInterval ?? DefaultMinInterval;
        _inspect = options?.Inspect;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The result of the last finished pass; null before one finished.</summary>
    public ArchivePassResult? Last => Volatile.Read(ref _last);

    /// <summary>Starts a pass when one is due; returns at once and never throws.</summary>
    public void Kick()
    {
        var claimed = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _running) != 0)
                return;

            var now = _time.GetUtcNow();
            if (TooSoon(now))
                return;

            if (!_isEnabled())
                return;

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return;
            claimed = true;

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || TooSoonLocked(now))
                {
                    Volatile.Write(ref _running, 0);
                    return;
                }

                var ct = _cts.Token;
                _current = Task.Run(() => RunAsync(now, ct));
                _lastStart = now;
                claimed = false; // the pass owns the flag now and clears it when it ends
            }
        }
        catch (Exception ex)
        {
            if (claimed)
                Volatile.Write(ref _running, 0);
            Log("starting a pass", ex);
        }
    }

    /// <summary>Completes when no pass is running (never faults).</summary>
    public Task WaitIdleAsync()
    {
        lock (_gate)
            return _current ?? Task.CompletedTask;
    }

    /// <summary>Cancels a running pass (it stops between sessions or batches) and starts none again.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Task? running;
        lock (_gate)
        {
            try { _cts.Cancel(); } catch (Exception) { /* best-effort */ }
            running = _current;
        }

        // The running pass still holds the token: the source is disposed after it ends.
        if (running is null || running.IsCompleted)
            _cts.Dispose();
        else
            running.ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private bool TooSoon(DateTimeOffset now)
    {
        lock (_gate)
            return TooSoonLocked(now);
    }

    /// <summary>
    /// Within the minimum interval of the last start. A clock that moved backwards (<c>now</c> before the last start)
    /// counts as due, so setting the clock back cannot block passes until it catches up again.
    /// </summary>
    private bool TooSoonLocked(DateTimeOffset now) =>
        _lastStart is { } last && now - last >= TimeSpan.Zero && now - last < _minInterval;

    // ------------------------------------------------------------------
    // The pass
    // ------------------------------------------------------------------

    private async Task RunAsync(DateTimeOffset at, CancellationToken ct)
    {
        var result = new ArchivePassResult(at, OutcomeFailed, 0, null);
        try
        {
            result = await PassAsync(at, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("the pass", ex);
        }
        finally
        {
            Volatile.Write(ref _last, result);
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ArchivePassResult> PassAsync(DateTimeOffset at, CancellationToken ct)
    {
        // Step 1: the settings, the storage mode and the archive, re-checked as `transcripts backfill` does.
        ArchivePassContext? context;
        try
        {
            context = _context();
        }
        catch (Exception ex)
        {
            Log("reading the settings", ex);
            return new ArchivePassResult(at, OutcomeUnavailable, 0, null);
        }

        if (context is null)
            return new ArchivePassResult(at, OutcomeUnavailable, 0, null);
        if (!context.Settings.Enabled)
            return new ArchivePassResult(at, OutcomeOff, 0, null);
        if (IsPostgres(context.StorageMode))
            return new ArchivePassResult(at, OutcomePostgres, 0, null);
        if (context.Location is null)
            return new ArchivePassResult(at, OutcomeUnavailable, 0, null);
        if (ct.IsCancellationRequested)
            return new ArchivePassResult(at, OutcomeCancelled, 0, null);

        // Open logs its own failures; a refused or absent archive ends the pass without a word.
        var open = TranscriptStore.Open(context.Location, create: false);
        if (open.Status == TranscriptOpenStatus.Absent)
        {
            open.Store?.Dispose();
            return new ArchivePassResult(at, OutcomeAbsent, 0, null);
        }
        if (open.Status != TranscriptOpenStatus.Opened || open.Store is null)
        {
            open.Store?.Dispose();
            return new ArchivePassResult(at, OutcomeUnavailable, 0, null);
        }

        using var store = open.Store;

        // Step 2: ingest.
        var ingested = Ingest(context, store, ct);
        if (ct.IsCancellationRequested)
            return new ArchivePassResult(at, OutcomeCancelled, ingested, null);

        // Step 3: embed.
        EmbedRun? embed = null;
        try
        {
            embed = await EmbedAsync(context, store, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("embedding", ex);
        }

        return new ArchivePassResult(at, ct.IsCancellationRequested ? OutcomeCancelled : OutcomeDone, ingested, embed);
    }

    /// <summary>Each adapter's sessions under one token that cancels after the ingest budget; returns new plus updated.</summary>
    private int Ingest(ArchivePassContext context, ITranscriptStore store, CancellationToken ct)
    {
        IReadOnlyList<ITranscriptAdapter> adapters;
        try
        {
            adapters = context.Adapters();
        }
        catch (Exception ex)
        {
            Log("listing the adapters", ex);
            return 0;
        }

        // The budget runs on the pass's clock, so a test clock (and only it) decides when it is spent.
        using var timeout = new CancellationTokenSource(_ingestBudget, _time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var sessions = 0;
        foreach (var adapter in adapters)
        {
            if (budget.IsCancellationRequested)
                break;

            var host = "unknown host";
            try
            {
                host = adapter.Host;
                // Run checks Detect() first and reads nothing more when the host's directory is missing.
                var summary = new TranscriptBackfill(store, adapter).Run(budget.Token);
                sessions += summary.New + summary.Updated;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log("ingesting " + host, ex); // a failing adapter never blocks the next one
            }
        }

        return sessions;
    }

    /// <summary>
    /// Rules 1 and 2 from config first, so a remote provider without the opt-in is never built; then the rest on a
    /// dedicated background thread at below-normal priority.
    /// </summary>
    private async Task<EmbedRun?> EmbedAsync(ArchivePassContext context, ITranscriptStore store, CancellationToken ct)
    {
        var provider = ProviderOf(context.Embedding);
        var allowRemote = context.Settings.AllowRemoteEmbedding;

        var preliminary = EmbedGate.Evaluate(new EmbedGateInput(
            ArchiveActive: true, Postgres: false, Provider: provider, AllowRemoteEmbedding: allowRemote,
            Dimensions: null, Fingerprint: null, Stored: null, Current: null,
            Readiness: new EmbedderReadiness(true, null), MemoryReindexRunning: false, ServerPass: true));
        if (!preliminary.CanEmbed)
            return new EmbedRun(0, Waiting(store), 0, preliminary, EmbedStopReason.Gate);

        var done = new TaskCompletionSource<EmbedRun?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(EmbedHere(context, store, provider, allowRemote, ct));
            }
            catch (Exception ex)
            {
                Log("embedding", ex);
                done.TrySetResult(null);
            }
        })
        {
            IsBackground = true, // never keeps the process alive after the host closes it
            Name = "total-recall archive embed",
        };
        try { thread.Priority = ThreadPriority.BelowNormal; }
        catch (Exception ex) { Log("lowering the embed thread's priority", ex); }
        thread.Start();

        return await done.Task.ConfigureAwait(false);
    }

    private EmbedRun EmbedHere(
        ArchivePassContext context, ITranscriptStore store, string provider, bool allowRemote, CancellationToken ct)
    {
        EmbedderInspectionResult? inspected = null;
        try
        {
            var inspect = _inspect ?? context.Inspect;
            try
            {
                inspected = inspect(context.Embedding, PassIntraOpThreads);
            }
            catch (Exception ex)
            {
                Log("building the embedder", ex);
                inspected = new EmbedderInspectionResult(null, null, new EmbedderReadiness(false, ex.Message), ex.Message);
            }

            inspected ??= new EmbedderInspectionResult(null, null, new EmbedderReadiness(false, "no embedder"), "no embedder");
            var current = inspected.Current;

            FingerprintState? state = null;
            ArchiveFingerprint? stored = null;
            if (current is not null)
            {
                try
                {
                    state = store.CheckFingerprint(current);
                    if (state == FingerprintState.Mismatch)
                        stored = store.ReadEmbedFingerprint();
                }
                catch (Exception ex)
                {
                    Log("reading the fingerprint", ex);
                    return new EmbedRun(0, Waiting(store), 0, EmbedGate.Failed(ex.Message), EmbedStopReason.Failed);
                }
            }

            var reindex = false;
            try
            {
                reindex = context.MemoryReindexRunning();
            }
            catch (Exception ex)
            {
                Log("reading the memory re-embed state", ex);
            }

            var gate = EmbedGate.Evaluate(new EmbedGateInput(
                ArchiveActive: true,
                Postgres: false,
                Provider: provider,
                AllowRemoteEmbedding: allowRemote,
                Dimensions: current?.Dimensions,
                Fingerprint: state,
                Stored: stored,
                Current: current,
                Readiness: inspected.Readiness,
                MemoryReindexRunning: reindex,
                ServerPass: true));

            if (!gate.CanEmbed)
                return new EmbedRun(0, Waiting(store), 0, gate, EmbedStopReason.Gate);
            if (inspected.Embedder is null || current is null)
                return new EmbedRun(0, Waiting(store), 0, EmbedGate.NotReady("no embedder"), EmbedStopReason.Gate);

            return new TranscriptEmbedWorker(store, inspected.Embedder, _time).Run(gate, current, _embedBudget, ct);
        }
        finally
        {
            try { (inspected?.Embedder as IDisposable)?.Dispose(); }
            catch (Exception ex) { Log("disposing the embedder", ex); }
        }
    }

    private static int Waiting(ITranscriptStore store)
    {
        try
        {
            return (int)Math.Min(int.MaxValue, store.CountEmbedding().Waiting);
        }
        catch (Exception ex)
        {
            Log("counting the waiting chunks", ex);
            return 0;
        }
    }

    private static string ProviderOf(Core.Config.EmbeddingConfig cfg) =>
        FSharpOption<string>.get_IsSome(cfg.Provider) ? cfg.Provider.Value : "local";

    /// <summary>
    /// The pass refuses only the exact storage label <c>postgres</c> (any case). A fallback label such as
    /// <c>sqlite (postgres failed)</c> is not refused here; such a boot relies on the boot-time sentinel mirror
    /// (<c>Program</c> calls <see cref="TranscriptSentinel.Mirror"/> with the configured mode, by
    /// <see cref="TranscriptSentinel.EffectiveStorageMode"/>), which removes the <c>live/enabled</c> marker when Postgres
    /// is configured, so <see cref="Kick"/>'s <c>isEnabled</c> check stops the pass first.
    /// </summary>
    private static bool IsPostgres(string? storageMode) =>
        string.Equals(storageMode, "postgres", StringComparison.OrdinalIgnoreCase);

    private static void Log(string what, Exception ex)
    {
        try { ExceptionLogger.LogChain(LogPrefix + ": " + what, ex); }
        catch { /* logging must not throw */ }
    }
}
