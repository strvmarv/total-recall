using System;
using System.IO;
using System.Threading;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class ReindexCoordinatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MsSqliteConnection _conn;
    private readonly SqliteStore _store;
    private readonly VectorSearch _vec;
    private readonly IEmbedder _newEmbedder = new ConstantEmbedder(0.5f);

    // A fixed clock so stale/live-lock arithmetic in the tests is deterministic.
    private long _now = 1_000_000_000_000L;
    private long Now() => _now;

    public ReindexCoordinatorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "tr-reindexcoord-" + Guid.NewGuid().ToString("N") + ".db");
        _conn = SqliteConnection.Open(_dbPath);
        MigrationRunner.RunMigrations(_conn);
        _store = new SqliteStore(_conn);
        _vec = new VectorSearch(_conn);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Cleanup(_dbPath);
    }

    [Fact]
    public void Run_FullPass_StampsFingerprint_ClearsCursorAndLock()
    {
        var total = ReindexHarness.Seed(_store, _vec, warmMemories: 50, coldKnowledge: 10);
        // Stale (different model) fingerprint so the new embedder differs going in.
        EmbedderFingerprint.Restamp(_store, new FakeEmbedder());
        Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
            EmbedderFingerprint.Check(_store, _newEmbedder, out _));

        var progress = new ReindexProgress();
        var coord = new ReindexCoordinator(nowUnixMs: Now, pid: Environment.ProcessId);
        coord.Run(_conn, _store, _vec, _newEmbedder, progress, CancellationToken.None, log: null);

        // Restamped to the new embedder.
        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(_store, _newEmbedder, out _));

        // Lock + cursor keys all cleared.
        Assert.Null(_store.GetMeta(ReindexCoordinator.LockKey));
        Assert.Null(_store.GetMeta(EmbeddingReindexer.CursorTargetKey));
        Assert.Null(_store.GetMeta(EmbeddingReindexer.CursorPairKey));
        Assert.Null(_store.GetMeta(EmbeddingReindexer.CursorRowidKey));

        // Progress reflects a completed full pass.
        Assert.Equal(ReindexProgress.Phase.Completed, progress.State);
        Assert.Equal(total, progress.Done);
        Assert.Equal(total, progress.Total);
    }

    [Fact]
    public void Run_SkipsWhenLiveLockHeldByAnotherPid()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 20, coldKnowledge: 0);
        EmbedderFingerprint.Restamp(_store, new FakeEmbedder()); // stale fingerprint

        // Seed a live lock owned by THIS process (guaranteed alive), but construct
        // the coordinator with a sentinel pid != ProcessId so it reads as "someone else".
        var alivePid = Environment.ProcessId;
        _store.SetMeta(ReindexCoordinator.LockKey, $"{alivePid}:{_now}");

        var progress = new ReindexProgress();
        var coord = new ReindexCoordinator(nowUnixMs: Now, pid: 999_999);
        var ex = Record.Exception(() =>
            coord.Run(_conn, _store, _vec, _newEmbedder, progress, CancellationToken.None, log: null));

        Assert.Null(ex); // no-op, no throw
        // Fingerprint NOT stamped (still mismatches the new embedder).
        Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
            EmbedderFingerprint.Check(_store, _newEmbedder, out _));
        // Other process's lock untouched; no cursor written.
        Assert.Equal($"{alivePid}:{_now}", _store.GetMeta(ReindexCoordinator.LockKey));
        Assert.Null(_store.GetMeta(EmbeddingReindexer.CursorTargetKey));
        // Progress never started.
        Assert.Equal(ReindexProgress.Phase.Idle, progress.State);
    }

    [Fact]
    public void Run_TakesOverStaleLock()
    {
        var total = ReindexHarness.Seed(_store, _vec, warmMemories: 30, coldKnowledge: 0);
        EmbedderFingerprint.Restamp(_store, new FakeEmbedder()); // stale fingerprint

        // Lock from a (likely dead) pid started 31 minutes ago → stale → taken over.
        var staleStart = _now - (long)TimeSpan.FromMinutes(31).TotalMilliseconds;
        _store.SetMeta(ReindexCoordinator.LockKey, $"12345:{staleStart}");

        var progress = new ReindexProgress();
        var coord = new ReindexCoordinator(nowUnixMs: Now, pid: Environment.ProcessId);
        coord.Run(_conn, _store, _vec, _newEmbedder, progress, CancellationToken.None, log: null);

        // Proceeded to completion: fingerprint stamped, lock/cursor cleared.
        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(_store, _newEmbedder, out _));
        Assert.Null(_store.GetMeta(ReindexCoordinator.LockKey));
        Assert.Equal(ReindexProgress.Phase.Completed, progress.State);
        Assert.Equal(total, progress.Done);
    }

    [Fact]
    public void Run_Cancellation_LeavesResumableCursor_ReleasesLock_NotCompleted()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 1000, coldKnowledge: 0);
        EmbedderFingerprint.Restamp(_store, new FakeEmbedder()); // stale fingerprint

        using var cts = new CancellationTokenSource();
        var cancelling = new CancelAfterEmbedder(_newEmbedder, cts, afterCalls: 150);

        var progress = new ReindexProgress();
        var coord = new ReindexCoordinator(nowUnixMs: Now, pid: Environment.ProcessId);

        // RunBatched throws OperationCanceledException after a committed batch; the
        // coordinator rethrows it (caller resumes next boot).
        Assert.Throws<OperationCanceledException>(() =>
            coord.Run(_conn, _store, _vec, cancelling, progress, cts.Token, log: null));

        // Lock released even on cancellation.
        Assert.Null(_store.GetMeta(ReindexCoordinator.LockKey));
        // Resumable cursor left behind.
        Assert.NotNull(_store.GetMeta(EmbeddingReindexer.CursorTargetKey));
        Assert.NotNull(_store.GetMeta(EmbeddingReindexer.CursorPairKey));
        Assert.NotNull(_store.GetMeta(EmbeddingReindexer.CursorRowidKey));
        // Fingerprint NOT stamped; progress not Completed.
        Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
            EmbedderFingerprint.Check(_store, _newEmbedder, out _));
        Assert.NotEqual(ReindexProgress.Phase.Completed, progress.State);
    }

    [Fact]
    public void Run_EmbedderThrows_SetsFailed_ReleasesLock_DoesNotStamp()
    {
        ReindexHarness.Seed(_store, _vec, warmMemories: 20, coldKnowledge: 0);
        // Stamp a DISTINCT prior fingerprint (model "const-1") so we can prove the
        // failed run does not restamp to the throwing embedder (model "fake").
        var oldE = new ConstantEmbedder(1.0f);
        EmbedderFingerprint.Restamp(_store, oldE);

        // Throws InvalidOperationException on the 2nd embed — a normal failure.
        var throwing = new ThrowingEmbedder(throwOnCall: 2);

        var progress = new ReindexProgress();
        var coord = new ReindexCoordinator(nowUnixMs: Now, pid: Environment.ProcessId);

        // Must NOT throw to the caller (swallowed).
        var ex = Record.Exception(() =>
            coord.Run(_conn, _store, _vec, throwing, progress, CancellationToken.None, log: null));
        Assert.Null(ex);

        // Progress.Fail set; lock released.
        Assert.Equal(ReindexProgress.Phase.Failed, progress.State);
        Assert.NotNull(progress.Error);
        Assert.Null(_store.GetMeta(ReindexCoordinator.LockKey));
        // Fingerprint NOT restamped to the throwing embedder — the OLD one is intact.
        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(_store, oldE, out _));
        Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
            EmbedderFingerprint.Check(_store, throwing, out _));
    }

    // sqlite-vec native handle + Microsoft.Data.Sqlite pooling can keep the file
    // mapped briefly past Dispose on Windows; drain the pool then best-effort
    // delete the db (+ WAL/SHM sidecars) so a transient lock never fails a test.
    private static void Cleanup(string dbPath)
    {
        MsSqliteConnection.ClearAllPools();
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (IOException) { }
        }
    }
}
