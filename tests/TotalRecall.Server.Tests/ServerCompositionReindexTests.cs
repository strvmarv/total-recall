// tests/TotalRecall.Server.Tests/ServerCompositionReindexTests.cs
//
// Task 5 — verify ServerComposition consumes the EnsureCompatibleSqlite decision
// and starts an in-process background re-index worker when (and only when) the
// decision is ReindexInBackground. Drives the internal seam
// ServerComposition.MaybeStartBackgroundReindex directly, injecting a deterministic
// fake embedder via the factory seam so the worker can run to completion WITHOUT
// the bge ONNX model (which is not downloaded in this worktree).

namespace TotalRecall.Server.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Server.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

public sealed class ServerCompositionReindexTests
{
    private static Core.Config.EmbeddingConfig DefaultEmbeddingConfig() =>
        new ConfigLoader().LoadDefaults().Embedding;

    // Scenario 1: Compatible → no worker is started, returns (null, null).
    [Fact]
    public void MaybeStartBackgroundReindex_Compatible_ReturnsNulls()
    {
        var (progress, cts) = ServerComposition.MaybeStartBackgroundReindex(
            EmbedderCompatibility.Compatible,
            resolvedDbPath: ":memory:",
            embCfg: DefaultEmbeddingConfig(),
            embedderFactory: () => throw new InvalidOperationException("factory must NOT be invoked when Compatible"));

        Assert.Null(progress);
        Assert.Null(cts);
    }

    // Warned (the other non-reindex decision) likewise starts no worker.
    [Fact]
    public void MaybeStartBackgroundReindex_Warned_ReturnsNulls()
    {
        var (progress, cts) = ServerComposition.MaybeStartBackgroundReindex(
            EmbedderCompatibility.Warned,
            resolvedDbPath: ":memory:",
            embCfg: DefaultEmbeddingConfig(),
            embedderFactory: () => throw new InvalidOperationException("factory must NOT be invoked when Warned"));

        Assert.Null(progress);
        Assert.Null(cts);
    }

    // Scenario 2: ReindexInBackground → the worker runs to completion against a
    // populated, unstamped temp DB using a FAKE embedder, and stamps the fingerprint.
    [Fact]
    public void MaybeStartBackgroundReindex_ReindexInBackground_RunsToCompletion_StampsFingerprint()
    {
        var dbPath = NewDbPath();
        var newEmbedder = new FixedDescriptorEmbedder("local", "new-model", "", 384);
        try
        {
            SeedPopulatedUnstamped(dbPath);

            var (progress, cts) = ServerComposition.MaybeStartBackgroundReindex(
                EmbedderCompatibility.ReindexInBackground,
                resolvedDbPath: dbPath,
                embCfg: DefaultEmbeddingConfig(),
                embedderFactory: () => newEmbedder);

            using (cts)
            {
                Assert.NotNull(progress);
                Assert.NotNull(cts);

                WaitForState(progress!, ReindexProgress.Phase.Completed, TimeSpan.FromSeconds(10));
                Assert.Equal(ReindexProgress.Phase.Completed, progress!.State);
            }

            // The worker owns its own connection and has now closed it; reopen and
            // assert the fingerprint was restamped to the new embedder.
            MsSqliteConnection.ClearAllPools();
            using var conn = SqliteConnection.Open(dbPath);
            var store = new SqliteStore(conn);
            Assert.Equal(
                EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, newEmbedder, out _));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // Scenario 3: non-blocking — MaybeStartBackgroundReindex returns promptly even
    // when the worker's embedder is slow. We use a fake embedder that sleeps per
    // Embed call so the full pass takes seconds. The call must return (with a live
    // progress object) far faster than the worker could finish — that prompt return
    // IS the non-blocking contract. The Task.Run is genuinely fire-and-forget, so we
    // do NOT assert the worker's state at the instant of return (it may not have been
    // scheduled yet — Idle is legitimate). Instead we prove the worker was actually
    // started by waiting for it to progress past Idle.
    [Fact]
    public void MaybeStartBackgroundReindex_ReturnsPromptly_NonBlocking()
    {
        var dbPath = NewDbPath();
        // Per-embed delay large enough that, across many seeded rows, the full pass
        // cannot complete within the return window.
        var slowEmbedder = new DelayedEmbedder("local", "new-model", "", 384, TimeSpan.FromMilliseconds(50));
        try
        {
            SeedPopulatedUnstamped(dbPath, warmMemories: 40);

            var sw = Stopwatch.StartNew();
            var (progress, cts) = ServerComposition.MaybeStartBackgroundReindex(
                EmbedderCompatibility.ReindexInBackground,
                resolvedDbPath: dbPath,
                embCfg: DefaultEmbeddingConfig(),
                embedderFactory: () => slowEmbedder);
            sw.Stop();

            using (cts)
            {
                Assert.NotNull(progress);
                Assert.NotNull(cts);

                // Fire-and-forget: returns far faster than the worker's total embed
                // time (40 rows * 50ms = ~2s). 1s is a generous, non-flaky ceiling.
                // This is the load-bearing assertion: the MCP handshake is not blocked.
                Assert.True(sw.ElapsedMilliseconds < 1000,
                    $"MaybeStartBackgroundReindex took {sw.ElapsedMilliseconds}ms; expected a prompt return.");

                // The worker was actually started: it eventually leaves Idle. With a
                // 50ms/row embedder it cannot have COMPLETED 40 rows by now, so the
                // observed live state proves the re-embed is running off the boot path.
                WaitForStatePastIdle(progress!, TimeSpan.FromSeconds(5));
                Assert.True(sw.ElapsedMilliseconds < 1000, "return must still be the prompt one above.");

                // Cancel so the slow worker stops promptly and releases the DB.
                cts!.Cancel();
            }

            WaitForWorkerToStop(dbPath, TimeSpan.FromSeconds(10));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // Scenario 4: Dispose cancels — building handles with a progress+cts from a
    // long-running worker and calling Dispose() cancels the token and stops the
    // worker (it does not advance to Completed).
    [Fact]
    public void Handles_Dispose_CancelsBackgroundReindex()
    {
        var dbPath = NewDbPath();
        // Long per-embed delay so the worker is still mid-pass when we dispose.
        var slowEmbedder = new DelayedEmbedder("local", "new-model", "", 384, TimeSpan.FromMilliseconds(200));
        try
        {
            SeedPopulatedUnstamped(dbPath, warmMemories: 40);

            var (progress, cts) = ServerComposition.MaybeStartBackgroundReindex(
                EmbedderCompatibility.ReindexInBackground,
                resolvedDbPath: dbPath,
                embCfg: DefaultEmbeddingConfig(),
                embedderFactory: () => slowEmbedder);

            Assert.NotNull(progress);
            Assert.NotNull(cts);

            // Wrap them in handles using a no-op primary resource so we can exercise
            // the Dispose() cancellation path in isolation. Dispose() never touches
            // the store, so a bare FakeStore is sufficient.
            var handles = new ServerCompositionHandles(
                new NoopDisposable(), new ToolRegistry(), new FakeStore(),
                storageMode: "sqlite", periodicSync: null,
                reindexProgress: progress, reindexCts: cts);

            // The worker is mid-pass (not yet Completed) thanks to the 200ms/row delay.
            Assert.NotEqual(ReindexProgress.Phase.Completed, progress!.State);

            handles.Dispose();

            // Dispose cancelled the cts (and disposed it).
            Assert.Throws<ObjectDisposedException>(() => cts!.Token);

            // The worker stops without reaching Completed — wait until it releases
            // the DB file and confirm it never completed.
            WaitForWorkerToStop(dbPath, TimeSpan.FromSeconds(10));
            Assert.NotEqual(ReindexProgress.Phase.Completed, progress.State);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---- helpers ---------------------------------------------------------------

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "tr-srvreindex-" + Guid.NewGuid().ToString("N") + ".db");

    // Seed a populated local index with NO embedder fingerprint (Unstamped +
    // populated) so EnsureCompatibleSqlite would route to ReindexInBackground under
    // auto. The worker re-embeds these rows.
    private static void SeedPopulatedUnstamped(string dbPath, int warmMemories = 8)
    {
        var oldE = new FixedDescriptorEmbedder("local", "old-model", "", 384);
        using var conn = SqliteConnection.Open(dbPath);
        MigrationRunner.RunMigrations(conn);
        var store = new SqliteStore(conn);
        for (var i = 0; i < warmMemories; i++)
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts($"seed {i}"), oldE.Embed($"seed {i}"));
        // No EmbedderFingerprint.Restamp → Unstamped.
        MsSqliteConnection.ClearAllPools();
    }

    private static void WaitForState(ReindexProgress progress, ReindexProgress.Phase target, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var s = progress.State;
            if (s == target) return;
            if (s == ReindexProgress.Phase.Failed)
                throw new Xunit.Sdk.XunitException($"re-index failed: {progress.Error}");
            Thread.Sleep(25);
        }
        throw new Xunit.Sdk.XunitException(
            $"re-index did not reach {target} within {timeout.TotalSeconds:0}s (state={progress.State}).");
    }

    private static void WaitForStatePastIdle(ReindexProgress progress, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (progress.State != ReindexProgress.Phase.Idle) return;
            Thread.Sleep(10);
        }
        throw new Xunit.Sdk.XunitException(
            $"background worker never left Idle within {timeout.TotalSeconds:0}s — it was not started.");
    }

    // The worker owns its own sqlite connection; once cancelled it disposes it. Poll
    // until the DB file can be exclusively reopened (proxy for "worker released it").
    private static void WaitForWorkerToStop(string dbPath, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            MsSqliteConnection.ClearAllPools();
            try
            {
                using var conn = SqliteConnection.Open(dbPath);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1;";
                cmd.ExecuteScalar();
                return; // reopened cleanly → worker has let go
            }
            catch
            {
                Thread.Sleep(25);
            }
        }
        // Don't fail the test on a slow release; the cancellation assertions above
        // are the real contract. Cleanup() tolerates locked files.
    }

    private static void Cleanup(string dbPath)
    {
        MsSqliteConnection.ClearAllPools();
        foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (IOException) { }
        }
    }

    /// <summary>IEmbedder with a controllable descriptor; Embed returns a normalized
    /// constant vector (valid for the vec0 table).</summary>
    private sealed class FixedDescriptorEmbedder : IEmbedder
    {
        public FixedDescriptorEmbedder(string provider, string model, string revision, int dims)
            => Descriptor = new EmbedderDescriptor(provider, model, revision, dims);

        public EmbedderDescriptor Descriptor { get; }

        public float[] Embed(string text)
        {
            var a = new float[Descriptor.Dimensions];
            for (var i = 0; i < a.Length; i++) a[i] = 1.0f;
            var n = (float)Math.Sqrt(a.Length);
            for (var i = 0; i < a.Length; i++) a[i] /= n;
            return a;
        }
    }

    /// <summary>Like FixedDescriptorEmbedder but sleeps per Embed call so the worker
    /// is observably slow (used to prove non-blocking return + Dispose cancellation).</summary>
    private sealed class DelayedEmbedder : IEmbedder
    {
        private readonly TimeSpan _delay;

        public DelayedEmbedder(string provider, string model, string revision, int dims, TimeSpan delay)
        {
            Descriptor = new EmbedderDescriptor(provider, model, revision, dims);
            _delay = delay;
        }

        public EmbedderDescriptor Descriptor { get; }

        public float[] Embed(string text)
        {
            Thread.Sleep(_delay);
            var a = new float[Descriptor.Dimensions];
            for (var i = 0; i < a.Length; i++) a[i] = 1.0f;
            var n = (float)Math.Sqrt(a.Length);
            for (var i = 0; i < a.Length; i++) a[i] /= n;
            return a;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
