using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using TotalRecall.Infrastructure.Memory;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Embedding;

/// <summary>
/// Owns the full lifecycle around <see cref="EmbeddingReindexer.RunBatched"/>:
/// acquires a cross-process lock, drives the batched re-embed from its resume
/// cursor, and on a FULL pass restamps the embedder fingerprint and clears the
/// cursor + lock. Designed to run on a background worker so the slow re-embed
/// stays off the boot/MCP-handshake path.
///
/// Concurrency: a single coordinator instance per process. Cross-process safety
/// rides on the <c>_meta</c> lock key (<see cref="LockKey"/>), stored as a simple
/// delimited <c>"{pid}:{startedAtUnixMs}"</c> string — NOT JSON, because the
/// server compiles NativeAOT and reflection-based serialization is unavailable.
///
/// The <c>_meta</c> lock is a best-effort ADVISORY lock, NOT atomically exclusive.
/// <see cref="TryAcquire"/> is a read-then-write with no DB-level compare-and-swap,
/// so two concurrent callers can both observe "no live lock" and both proceed. This
/// is acceptable: SQLite WAL serializes writers and <see cref="EmbeddingReindexer.RunBatched"/>
/// commits per batch, so concurrent reindexers are merely wasteful, not corrupting.
/// The known residual risk is PID reuse within the <see cref="StaleAfter"/> staleness
/// window (a recycled pid could read as "still alive"); the 30-minute cap is the
/// backstop for that — it is deliberately generous and should NOT be shortened on the
/// mistaken belief it is safer.
/// </summary>
public sealed class ReindexCoordinator
{
    /// <summary>_meta key holding the re-index lock (<c>"{pid}:{startedAtUnixMs}"</c>).</summary>
    internal const string LockKey = "embed.reindex.lock";

    /// <summary>A lock older than this is treated as stale (the holder died without
    /// releasing it). Mirrors <c>scripts/fetch-binary.js</c>'s provisionerAlive cap.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private readonly Func<long> _nowUnixMs;
    private readonly int _pid;

    public ReindexCoordinator(Func<long>? nowUnixMs = null, int? pid = null)
    {
        _nowUnixMs = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _pid = pid ?? Environment.ProcessId;
    }

    /// <summary>
    /// SQLite-only: takes a concrete <see cref="MsSqliteConnection"/>; the Postgres
    /// flow does not use this coordinator.
    ///
    /// Idempotent and resumable. Acquires the lock (skips entirely if a live runner
    /// in another process holds it), runs <see cref="EmbeddingReindexer.RunBatched"/>
    /// from the persisted cursor, and on FULL completion restamps the fingerprint and
    /// clears the cursor + lock. Owns the <see cref="ReindexProgress"/> lifecycle:
    /// <see cref="ReindexProgress.BeginRunning"/> before, <see cref="ReindexProgress.Complete"/>
    /// on success, <see cref="ReindexProgress.Fail"/> on error — the caller need not
    /// have begun it.
    ///
    /// Normal failures are swallowed (progress.Fail + release lock) so a background
    /// worker never crashes the host. <see cref="OperationCanceledException"/> is
    /// rethrown and leaves the resume cursor intact (the caller treats it as
    /// "resume next boot"); the lock is still released either way.
    /// </summary>
    public void Run(MsSqliteConnection conn, IStore store, IVectorSearch vec, IEmbedder embedder,
                    ReindexProgress progress, CancellationToken ct, TextWriter? log)
    {
        var meta = (IMetaStore)store;
        if (!TryAcquire(meta))
        {
            log?.WriteLine("[total-recall] re-index already running elsewhere; skipping.");
            return;
        }

        try
        {
            long total = 0;
            foreach (var (t, ty) in TierNames.AllTablePairs)
                total += store.Count(t, ty);

            progress.BeginRunning(total, embedder.Descriptor.Model, _nowUnixMs());
            EmbeddingReindexer.RunBatched(conn, store, vec, embedder, progress, ct, log);
            EmbedderFingerprint.Restamp(meta, embedder);
            ClearCursor(meta);
            progress.Complete();
            log?.WriteLine("[total-recall] re-index complete; fingerprint updated.");
        }
        catch (OperationCanceledException)
        {
            throw; // leave the cursor intact; resume next boot
        }
        catch (Exception ex)
        {
            progress.Fail(ex.Message);
            log?.WriteLine($"[total-recall] re-index failed: {ex.Message}");
        }
        finally
        {
            ReleaseLock(meta);
        }
    }

    /// <summary>
    /// Take the lock unless a parseable, non-stale lock owned by a still-alive,
    /// DIFFERENT pid is present. On success (re)stamps the lock with our pid + now.
    ///
    /// ADVISORY only: this is a read-then-write with no DB-level compare-and-swap, so
    /// two concurrent callers can both proceed (see the class summary). An unparseable
    /// lock value is treated as absent and taken over.
    /// </summary>
    private bool TryAcquire(IMetaStore meta)
    {
        var nowMs = _nowUnixMs();
        var raw = meta.GetMeta(LockKey);
        if (TryParse(raw, out int pid, out long startedAt)
            && (nowMs - startedAt) <= (long)StaleAfter.TotalMilliseconds
            && pid != _pid
            && IsAlive(pid))
        {
            return false; // someone else holds a live lock
        }

        meta.SetMeta(LockKey, $"{_pid}:{nowMs}");
        return true;
    }

    /// <summary>Release the lock only if WE still hold it (don't clobber a takeover).</summary>
    private void ReleaseLock(IMetaStore meta)
    {
        if (TryParse(meta.GetMeta(LockKey), out int pid, out _) && pid == _pid)
            meta.DeleteMeta(LockKey);
    }

    private static void ClearCursor(IMetaStore meta)
    {
        meta.DeleteMeta(EmbeddingReindexer.CursorTargetKey);
        meta.DeleteMeta(EmbeddingReindexer.CursorPairKey);
        meta.DeleteMeta(EmbeddingReindexer.CursorRowidKey);
    }

    private static bool TryParse(string? raw, out int pid, out long startedAt)
    {
        pid = 0;
        startedAt = 0;
        if (string.IsNullOrEmpty(raw))
            return false;

        // Split into at most 2 so a future format extension (extra ':' segments)
        // degrades safely to "unparseable ⇒ take over" rather than misbehaving.
        var parts = raw.Split(':', 2);
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out startedAt);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            // ArgumentException (no such pid) ⇒ not running; any other failure ⇒
            // treat as dead so a stuck lock never blocks a fresh re-index.
            return false;
        }
    }
}
