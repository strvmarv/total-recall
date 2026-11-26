using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Diagnostics;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Memory;

namespace TotalRecall.Infrastructure.Transcripts;

/// <summary>Brings the current session's new tail into the archive before a transcript tool reads (spec section 3).</summary>
public interface ITranscriptSync
{
    /// <summary>Never throws, except an <see cref="OperationCanceledException"/> from <paramref name="ct"/>.</summary>
    SyncResult SyncCurrent(SyncEmbedPlan? embed, CancellationToken ct);
}

/// <summary>
/// What the embed step of a sync needs (search only, spec section 3 step 5): the search's embedder, the
/// <c>[transcripts]</c> settings, the storage mode, the <c>[embedding]</c> config and the memory re-embed probe.
/// </summary>
public sealed record SyncEmbedPlan(
    IEmbedder Embedder,
    TranscriptsSettings Settings,
    string StorageMode,
    Core.Config.EmbeddingConfig Embedding,
    Func<bool> MemoryReindexRunning);

/// <summary>
/// What one sync did. <paramref name="Complete"/> is false when the catch-up of the current session did not finish
/// (<paramref name="Note"/> then says why, as one short sentence). <paramref name="ProjectKey"/> is set only for a
/// <see cref="CurrentKind.Marker"/> session: the key the archive stored for it, else (when the session was not excluded)
/// the key derived from the head of its file, else null.
/// </summary>
public sealed record SyncResult(CurrentSession Current, bool Complete, string? Note, string? ProjectKey);

/// <summary>
/// Limits and test seams of <see cref="TranscriptSyncService"/>; null means the default: a 2 s ingest budget (also the
/// longest wait for a running sync), <c>EmbedBudget(2 s, 8)</c>, a 30-minute guess window, a 6 s total deadline, a
/// 500 ms busy timeout, this process's ancestors (<see cref="ProcessAncestry.PlatformTimed"/>, kept for the process once a walk ends normally),
/// <see cref="ConfigLoader.GetDataDir"/>, the real process start time, <see cref="TranscriptAdapters.All"/>, the system
/// clock and <see cref="TranscriptStore.Open"/> with <c>create: false</c>. <paramref name="Embedder"/>, when set, replaces
/// the composition's own embedder for the transcript tools (composition tests use it to run without a model).
/// </summary>
public sealed record TranscriptSyncOptions(
    TimeSpan? IngestBudget = null,
    EmbedBudget? EmbedBudget = null,
    TimeSpan? GuessWindow = null,
    TimeSpan? TotalDeadline = null,
    int? BusyTimeoutMs = null,
    Func<IReadOnlyList<int>>? Ancestors = null,
    string? DataDir = null,
    Func<int, DateTimeOffset?>? ProcessStart = null,
    Func<IReadOnlyList<ITranscriptAdapter>>? Adapters = null,
    TimeProvider? Time = null,
    Func<TranscriptLocation, TranscriptOpenResult>? Open = null,
    IEmbedder? Embedder = null);

/// <summary>Where the archive is and the <c>[transcripts]</c> settings, read when a sync runs.</summary>
public sealed record SyncContext(TranscriptLocation Location, TranscriptsSettings Settings);

/// <summary>
/// Sync-on-read (spec section 3, steps 1 to 4): one sync at a time per service (a second call waits for the running one
/// for at most the ingest budget); the current session from its marker (<see cref="LiveSessionFinder"/>) or, without
/// one, the bounded guess (the newest session file written within the guess window, disclosed as
/// <see cref="CurrentKind.Guessed"/>); the archive opened without creating it and with a short busy timeout; and the
/// session's file ingested through <see cref="TranscriptIngester"/> (the same path as the <c>session_start</c> pass, so
/// the exclusions hold by construction) under the ingest budget on the service's clock. No step starts once the total
/// deadline has passed. Every failure is logged to stderr through <see cref="ExceptionLogger"/> and reported as an
/// incomplete sync; only the caller's cancellation is thrown. With an embed plan (search only), step 5 then embeds the
/// session's waiting tail within the embed budget (see <see cref="EmbedTail"/>); a refused or failed embed step never
/// makes the sync incomplete. The single-flight lock is held through the embed step too, so a concurrent caller can wait
/// for an embed as well as an ingest; the server handles tool calls one at a time, so such a waiter is unlikely.
/// </summary>
public sealed class TranscriptSyncService : ITranscriptSync
{
    public static readonly TimeSpan DefaultIngestBudget = TimeSpan.FromSeconds(2);
    public static readonly EmbedBudget DefaultEmbedBudget = new(TimeSpan.FromSeconds(2), 8);
    public static readonly TimeSpan DefaultGuessWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultTotalDeadline = TimeSpan.FromSeconds(6);
    public const int DefaultBusyTimeoutMs = 500;

    private const string LogPrefix = "[total-recall] transcripts sync";
    private const string NotePrefix = "the newest text of the current session may be missing: ";

    internal const string NoteOutOfTime = NotePrefix + "the catch-up ran out of time";
    internal const string NoteWaitRanOut = NotePrefix + "the catch-up ran out of time waiting for another one";
    internal const string NoteUnreadable = NotePrefix + "the session file could not be read";
    internal const string NoteNoFirstLine = NotePrefix + "the session file has no complete first line yet";
    internal const string NoteChanged = NotePrefix + "the session file was replaced or truncated since it was archived";
    internal const string NoteRetry = NotePrefix + "the archive changed during the catch-up";
    internal const string NoteNotOpened = NotePrefix + "the archive could not be opened";
    internal const string NoteNotWritten = NotePrefix + "the archive could not be written (it may be busy)";
    internal const string NoteSettings = NotePrefix + "the archive's settings could not be read";
    /// <summary>The note of a catch-up that failed; the server's own catch-up guard reports the same text.</summary>
    public const string NoteFailed = NotePrefix + "the catch-up failed";

    private readonly Func<SyncContext?> _context;
    private readonly TranscriptSyncOptions _options;
    private readonly TimeSpan _ingestBudget;
    private readonly TimeSpan _guessWindow;
    private readonly TimeSpan _totalDeadline;
    private readonly TimeProvider _time;
    private readonly Func<TranscriptLocation, TranscriptOpenResult> _open;
    private readonly Lazy<LiveSessionFinder> _finder;
    private readonly SemaphoreSlim _single = new(1, 1);

    /// <param name="context">The archive's location and settings; evaluated by each sync, never by the constructor.</param>
    /// <param name="options">Limits and test seams; null for the defaults.</param>
    public TranscriptSyncService(Func<SyncContext?> context, TranscriptSyncOptions? options = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _options = options ?? new TranscriptSyncOptions();
        _ingestBudget = _options.IngestBudget ?? DefaultIngestBudget;
        _guessWindow = _options.GuessWindow ?? DefaultGuessWindow;
        _totalDeadline = _options.TotalDeadline ?? DefaultTotalDeadline;
        _time = _options.Time ?? TimeProvider.System;
        var busy = _options.BusyTimeoutMs ?? DefaultBusyTimeoutMs;
        _open = _options.Open ?? (location => TranscriptStore.Open(location, create: false, busyTimeoutMs: busy));

        // Built by the first sync (no work at boot); a failed build is not kept, so the next sync tries again.
        _finder = new Lazy<LiveSessionFinder>(BuildFinder, LazyThreadSafetyMode.PublicationOnly);
    }

    public SyncResult SyncCurrent(SyncEmbedPlan? embed, CancellationToken ct)
    {
        var current = CurrentSession.None;
        try
        {
            using var deadline = new CancellationTokenSource(_totalDeadline, _time);

            // Step 2 (cheap: the marker files, or one listing per host) comes first so that a call that cannot get
            // its turn still reports the current session.
            var adapters = Adapters();
            var found = Find(adapters);
            current = found.Current;

            // Step 1: single flight. A call that finds one running waits for it, then sees its own work unchanged.
            if (!_single.Wait(_ingestBudget, ct))
                return Incomplete(current, NoteWaitRanOut);
            try
            {
                return SyncLocked(found, embed, deadline.Token, ct);
            }
            finally
            {
                _single.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("the sync", ex);
            return Incomplete(current, NoteFailed);
        }
    }

    private SyncResult SyncLocked(Found found, SyncEmbedPlan? embed, CancellationToken deadline, CancellationToken ct)
    {
        var current = found.Current;
        if (current.Kind == CurrentKind.None || found.Adapter is null)
            return new SyncResult(current, true, null, null);
        if (deadline.IsCancellationRequested)
            return Incomplete(current, NoteOutOfTime);

        SyncContext? context;
        try
        {
            context = _context();
        }
        catch (Exception ex)
        {
            Log("reading the settings", ex);
            return Incomplete(current, NoteSettings);
        }

        // Off by a hand edit while the sentinel is still there, or no archive in this composition: no work.
        if (context is null || !context.Settings.Enabled)
            return new SyncResult(current, true, null, null);
        if (deadline.IsCancellationRequested)
            return Incomplete(current, NoteOutOfTime);

        // Step 3: open without creating.
        TranscriptOpenResult open;
        try
        {
            open = _open(context.Location);
        }
        catch (Exception ex)
        {
            Log("opening the archive", ex);
            return Incomplete(current, NoteNotOpened);
        }

        var adapter = found.Adapter;
        switch (open.Status)
        {
            case TranscriptOpenStatus.Absent:
                // Nothing is archived, so nothing is forgotten: the key may come from the head of the file.
                open.Store?.Dispose();
                return new SyncResult(current, true, null, KeyOf(current, adapter, null, null, archiveAbsent: true, deadline));
            case TranscriptOpenStatus.Refused:
                // Nothing to sync; the read answers as it does today.
                open.Store?.Dispose();
                return new SyncResult(current, true, null, null);
            case TranscriptOpenStatus.Failed:
            case TranscriptOpenStatus.Opened when open.Store is null:
                open.Store?.Dispose();
                LogLine("opening the archive: " + (open.Message ?? "no reason given"));
                return Incomplete(current, NoteNotOpened);
        }

        using var store = open.Store!;
        // The file as it is now, for a guess too: a call that waited for another sync must not use the size and time
        // it saw before the wait.
        var source = Stat(current);
        if (deadline.IsCancellationRequested)
            return Incomplete(current, NoteOutOfTime);
        if (source is null)
            return Incomplete(current, NoteUnreadable);

        // Step 4: ingest under the budget, on the service's clock, and never past the total deadline.
        SessionIngestStatus? status = null;
        string? note;
        using (var budget = new CancellationTokenSource(_ingestBudget, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token, deadline))
        {
            try
            {
                var ingester = new TranscriptIngester(store, adapter, new ProjectResolver(), UserHome());
                status = ingester.IngestSession(source, linked.Token).Status;
                note = NoteFor(status.Value);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                note = NoteOutOfTime;   // the write is one transaction: nothing partial was stored
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log("reading " + source.Path, ex);
                note = NoteUnreadable;
            }
            catch (SqliteException ex)
            {
                Log("writing the archive", ex);
                note = NoteNotWritten;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log("ingesting " + source.Path, ex);
                note = NoteFailed;
            }
        }

        // Step 5 (search only): the new tail gets its vectors within a small budget, or is found by its words.
        if (embed is not null)
            EmbedTail(store, adapter.Host, source.HostSessionId, embed, deadline, ct);

        var key = status == SessionIngestStatus.Excluded ? null : KeyOf(current, adapter, store, source, archiveAbsent: false, deadline);
        return new SyncResult(current, note is null, note, key);
    }

    /// <summary>
    /// Spec section 3 step 5: only when the session itself has chunks waiting (an indexed read; a fully embedded session
    /// takes no lease even when an older backlog waits), under the pass's gate (<c>ServerPass</c>, the plan's re-embed
    /// probe), one run of the Part 3 worker with the embed budget, the session preferred and no run record. A refusing
    /// gate, a held lease, a descriptor that cannot be read or any failure ends the step silently (the worker logs its
    /// own); nothing but the caller's cancellation is thrown, and nothing starts once the total deadline has passed.
    /// </summary>
    private void EmbedTail(
        ITranscriptStore store, string host, string hostSessionId, SyncEmbedPlan embed, CancellationToken deadline, CancellationToken ct)
    {
        if (deadline.IsCancellationRequested) return;
        try
        {
            if (!store.HasWaitingEmbedWork(host, hostSessionId)) return;

            var (gate, current) = TranscriptSearchService.BuildEmbedGate(
                embed.Embedder, embed.Settings, embed.StorageMode, embed.Embedding,
                serverPass: true, embed.MemoryReindexRunning(), store.CheckFingerprint, store.ReadEmbedFingerprint);
            if (current is null || !gate.CanEmbed) return;
            if (deadline.IsCancellationRequested) return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline);
            new TranscriptEmbedWorker(store, embed.Embedder, _time).Run(
                gate, current, _options.EmbedBudget ?? DefaultEmbedBudget, linked.Token,
                preferSession: (host, hostSessionId), recordRun: false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("embedding the tail", ex);
        }
    }

    /// <summary>Section 3 step 4: Retry, Skipped and Changed are incomplete; the other statuses are complete.</summary>
    private static string? NoteFor(SessionIngestStatus status) => status switch
    {
        SessionIngestStatus.Skipped => NoteNoFirstLine,
        SessionIngestStatus.Changed => NoteChanged,
        SessionIngestStatus.Retry => NoteRetry,
        _ => null,
    };

    private static SyncResult Incomplete(CurrentSession current, string note, string? key = null) =>
        new(current, false, note, key);

    // ------------------------------------------------------------------
    // Finding the current session
    // ------------------------------------------------------------------

    /// <summary>The current session and its adapter.</summary>
    private sealed record Found(CurrentSession Current, ITranscriptAdapter? Adapter)
    {
        public static Found None { get; } = new(CurrentSession.None, null);
    }

    private IReadOnlyList<ITranscriptAdapter> Adapters()
    {
        try
        {
            return (_options.Adapters ?? TranscriptAdapters.All)() ?? Array.Empty<ITranscriptAdapter>();
        }
        catch (Exception ex)
        {
            Log("listing the adapters", ex);
            return Array.Empty<ITranscriptAdapter>();
        }
    }

    private Found Find(IReadOnlyList<ITranscriptAdapter> adapters)
    {
        if (adapters.Count == 0) return Found.None;

        try
        {
            var marked = _finder.Value.Find(adapters);
            if (marked is not null)
            {
                foreach (var adapter in adapters)
                {
                    if (string.Equals(adapter.Host, marked.Host, StringComparison.Ordinal))
                        return new Found(marked, adapter);
                }
            }
        }
        catch (Exception ex)
        {
            Log("reading the live session markers", ex);
        }

        return Guess(adapters);
    }

    /// <summary>
    /// The guess (spec section 2): for each host that is detected, the newest session file (by modification time, then
    /// the ordinal path) written within the guess window of the service's clock; across hosts the newest of those.
    /// </summary>
    private Found Guess(IReadOnlyList<ITranscriptAdapter> adapters)
    {
        var floor = (_time.GetUtcNow() - _guessWindow).ToUnixTimeMilliseconds();
        SessionSource? best = null;
        ITranscriptAdapter? bestAdapter = null;
        foreach (var adapter in adapters)
        {
            try
            {
                if (!adapter.Detect()) continue;
                foreach (var source in adapter.EnumerateSessions().Sources)
                {
                    if (source.MtimeMs < floor) continue;
                    if (best is null
                        || source.MtimeMs > best.MtimeMs
                        || (source.MtimeMs == best.MtimeMs && string.CompareOrdinal(source.Path, best.Path) < 0))
                    {
                        best = source;
                        bestAdapter = adapter;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("listing the sessions of a host", ex);
            }
        }

        if (best is null || bestAdapter is null) return Found.None;
        var current = new CurrentSession(CurrentKind.Guessed, bestAdapter.Host, best.HostSessionId, best.Path, null);
        return new Found(current, bestAdapter);
    }

    /// <summary>The session's file as it is now (size and modification time), or null when it is gone or unreadable.</summary>
    private static SessionSource? Stat(CurrentSession current)
    {
        if (current.TranscriptPath is null || current.HostSessionId is null) return null;
        try
        {
            var file = new FileInfo(current.TranscriptPath);
            if (!file.Exists) return null;
            var mtime = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
            return new SessionSource(current.HostSessionId, file.FullName, file.Length, mtime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private LiveSessionFinder BuildFinder()
    {
        // The real walk is kept only when it ended normally (a failed or timed-out ps on macOS is walked again next
        // time); a test seam's non-empty chain is kept as before.
        var ancestry = _options.Ancestors is { } seam
            ? new CachedAncestry(seam)
            : new CachedAncestry(() => ProcessAncestry.Walk(Environment.ProcessId, ProcessAncestry.PlatformTimed()));
        var dataDir = _options.DataDir ?? ConfigLoader.GetDataDir();
        return new LiveSessionFinder(dataDir, ancestry, _options.ProcessStart);
    }

    // ------------------------------------------------------------------
    // The project key
    // ------------------------------------------------------------------

    /// <summary>
    /// For a marker session only: the key stored for it, else the key derived from the head of its file as the ingest
    /// derives it. Never for a session the archive excludes (by id, by project key or root, or by the cutoff): its file
    /// is not read for a key. Without an open store the head is read only when the archive is absent (nothing can be
    /// forgotten then). Null on any failure, and once the total deadline has passed (no read starts after it).
    /// </summary>
    private string? KeyOf(
        CurrentSession current, ITranscriptAdapter adapter, ITranscriptStore? store, SessionSource? source, bool archiveAbsent,
        CancellationToken deadline)
    {
        if (current.Kind != CurrentKind.Marker || current.HostSessionId is null) return null;
        if (store is null && !archiveAbsent) return null;
        if (deadline.IsCancellationRequested) return null;
        try
        {
            var id = current.HostSessionId;
            if (store is not null)
            {
                var stored = store.GetIngestState(adapter.Host, id)?.ProjectKey;
                if (stored is not null) return stored;
                if (store.IsExcluded(adapter.Host, id, null, null)) return null;
            }

            if (deadline.IsCancellationRequested) return null;
            source ??= Stat(current);
            if (source is null) return null;
            if (store?.IngestNotBefore() is { } cutoff
                && DateTimeOffset.FromUnixTimeMilliseconds(source.MtimeMs) <= cutoff.At)
                return null;

            var head = adapter.ReadHead(source);
            if (head.Fingerprint is null) return null;   // the head cannot be read: no project
            var derived = ProjectIdentityRule.Derive(
                head.Cwd, TranscriptIngester.ProjectDirName(source.Path), new ProjectResolver(), UserHome());
            if (store is not null && store.IsExcluded(adapter.Host, id, derived.Key, derived.Cwd)) return null;
            return derived.Key;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log("reading the session's project", ex);
            return null;
        }
    }

    /// <summary>The user's home directory, as <see cref="TranscriptBackfill"/> resolves it.</summary>
    private static string UserHome() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty;

    /// <summary>One line to stderr (never stdout: it is the MCP channel), for a failure that carries no exception.</summary>
    private static void LogLine(string what)
    {
        try { Console.Error.WriteLine(LogPrefix + ": " + what); }
        catch { /* logging must not throw */ }
    }

    private static void Log(string what, Exception ex)
    {
        try { ExceptionLogger.LogChain(LogPrefix + ": " + what, ex); }
        catch { /* logging must not throw */ }
    }
}
