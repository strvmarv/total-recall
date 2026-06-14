using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Embedding;

/// <summary>
/// <see cref="EmbedderMigration"/> is a DECISION-ONLY seam now: it classifies the
/// startup situation and returns an <see cref="EmbedderCompatibility"/> without
/// re-embedding inline. The only mutation it makes is stamping a fresh
/// (unstamped + empty) DB. These tests assert that decision contract — they
/// deliberately do NOT assert any vector rewrite, because the re-embed is driven
/// by the caller (a background worker) in a later task.
/// </summary>
public sealed class EmbedderMigrationTests
{
    // ---------------------------------------------------------------------
    // EnsureCompatibleSqlite
    // ---------------------------------------------------------------------

    [Fact]
    public void Sqlite_Match_ReturnsCompatible_NoStampChange()
    {
        var dbPath = TempDb("match");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);

            var e = new ConstantEmbedder(0.5f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), e.Embed("seed"));
            EmbedderFingerprint.Restamp(store, e);

            var log = new StringWriter();
            var decision = EmbedderMigration.EnsureCompatibleSqlite(store, e, OnModelChange.Auto, log);

            Assert.Equal(EmbedderCompatibility.Compatible, decision);
            // No-op: nothing logged, still Match.
            Assert.Equal("", log.ToString());
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, e, out _));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Unstamped_Empty_ReturnsCompatible_Stamps()
    {
        var dbPath = TempDb("unstamped-empty");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);

            var e = new ConstantEmbedder(0.5f);

            // No fingerprint stamped yet, and the index is EMPTY → Unstamped.
            // This is a fresh DB; the policy should stamp without re-embedding.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
                EmbedderFingerprint.Check(store, e, out _));

            var log = new StringWriter();
            var decision = EmbedderMigration.EnsureCompatibleSqlite(store, e, OnModelChange.Auto, log);

            Assert.Equal(EmbedderCompatibility.Compatible, decision);
            // Now stamped → Match.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, e, out _));
            // Empty index → pure stamp, nothing logged.
            Assert.Equal("", log.ToString());
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Unstamped_Populated_Auto_ReturnsReindexInBackground_NoStamp_NoVectorChange()
    {
        var dbPath = TempDb("unstamped-pop-auto");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            // Seed two rows with content-dependent vectors from an UNKNOWN prior
            // model, but DO NOT stamp a fingerprint → Unstamped + populated.
            var oldE = new FakeEmbedder();
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("alpha"), oldE.Embed("alpha"));
            store.InsertWithEmbedding(Tier.Cold, ContentType.Knowledge,
                new InsertEntryOpts("beta"), oldE.Embed("beta"));

            var newE = new ConstantEmbedder(0.5f);
            Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
                EmbedderFingerprint.Check(store, newE, out _));

            // Snapshot the OLD vector for the warm row so we can prove it is untouched.
            var beforeHits = vec.SearchByVector(Tier.Warm, ContentType.Memory, oldE.Embed("alpha"),
                new VectorSearchOpts(TopK: 5));
            Assert.Contains(beforeHits, h => h.Score > 0.999);

            var log = new StringWriter();
            var decision = EmbedderMigration.EnsureCompatibleSqlite(store, newE, OnModelChange.Auto, log);

            Assert.Equal(EmbedderCompatibility.ReindexInBackground, decision);
            // Decision-only: no stamp, no logging here (the caller logs when it starts the worker).
            Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
                EmbedderFingerprint.Check(store, newE, out _));
            Assert.Equal("", log.ToString());

            // Vectors NOT rewritten: the old-model vector still self-matches, the
            // new-model query does not.
            var afterOld = vec.SearchByVector(Tier.Warm, ContentType.Memory, oldE.Embed("alpha"),
                new VectorSearchOpts(TopK: 5));
            Assert.Contains(afterOld, h => h.Score > 0.999);
            var afterNew = vec.SearchByVector(Tier.Warm, ContentType.Memory, newE.Embed("anything"),
                new VectorSearchOpts(TopK: 5));
            Assert.DoesNotContain(afterNew, h => h.Score > 0.999);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Unstamped_Populated_Warn_ReturnsWarned_NoStamp_Logs()
    {
        var dbPath = TempDb("unstamped-pop-warn");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);

            var oldE = new ConstantEmbedder(1.0f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), oldE.Embed("seed"));
            // No stamp → Unstamped + populated.

            var newE = new ConstantEmbedder(0.5f);
            var log = new StringWriter();
            var decision = EmbedderMigration.EnsureCompatibleSqlite(store, newE, OnModelChange.Warn, log);

            Assert.Equal(EmbedderCompatibility.Warned, decision);
            // No restamp: still Unstamped on the next Check (the nag persists).
            Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
                EmbedderFingerprint.Check(store, newE, out _));

            // Degraded-retrieval warning was logged.
            Assert.Contains("on_model_change=warn", log.ToString());
            Assert.Contains("retrieval quality", log.ToString());
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Unstamped_Populated_Block_Throws()
    {
        var dbPath = TempDb("unstamped-pop-block");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);

            var oldE = new ConstantEmbedder(1.0f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), oldE.Embed("seed"));
            // No stamp → Unstamped + populated.

            var newE = new ConstantEmbedder(0.5f);
            Assert.Throws<EmbedderFingerprintMismatchException>(
                () => EmbedderMigration.EnsureCompatibleSqlite(store, newE, OnModelChange.Block, log: null));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Mismatch_Auto_ReturnsReindexInBackground_NoStamp_NoVectorChange()
    {
        var dbPath = TempDb("auto");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            // Seed with OLD content-dependent vectors + stamp OLD fingerprint.
            var oldE = new FakeEmbedder();
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("alpha"), oldE.Embed("alpha"));
            store.InsertWithEmbedding(Tier.Cold, ContentType.Knowledge,
                new InsertEntryOpts("beta"), oldE.Embed("beta"));
            EmbedderFingerprint.Restamp(store, oldE);

            var newE = new ConstantEmbedder(0.5f);
            Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
                EmbedderFingerprint.Check(store, newE, out _));

            var log = new StringWriter();
            var decision = EmbedderMigration.EnsureCompatibleSqlite(store, newE, OnModelChange.Auto, log);

            Assert.Equal(EmbedderCompatibility.ReindexInBackground, decision);
            // Decision-only: the OLD fingerprint is untouched and nothing was logged here.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, oldE, out _));
            Assert.Equal("", log.ToString());

            // Vectors NOT rewritten: the new-model query does not self-match.
            var afterNew = vec.SearchByVector(Tier.Warm, ContentType.Memory, newE.Embed("anything"),
                new VectorSearchOpts(TopK: 5));
            Assert.DoesNotContain(afterNew, h => h.Score > 0.999);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Mismatch_Warn_ReturnsWarned_FingerprintUnchanged_Logs()
    {
        var dbPath = TempDb("warn");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);

            var oldE = new ConstantEmbedder(1.0f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), oldE.Embed("seed"));
            EmbedderFingerprint.Restamp(store, oldE);

            var newE = new ConstantEmbedder(0.5f);
            var log = new StringWriter();
            var decision = EmbedderMigration.EnsureCompatibleSqlite(store, newE, OnModelChange.Warn, log);

            Assert.Equal(EmbedderCompatibility.Warned, decision);
            // No restamp: still Mismatch on the next Check (the nag persists).
            Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
                EmbedderFingerprint.Check(store, newE, out _));
            // Old fingerprint still intact.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, oldE, out _));

            Assert.Contains("on_model_change=warn", log.ToString());
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Mismatch_Block_Throws()
    {
        var dbPath = TempDb("block");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);

            var oldE = new ConstantEmbedder(1.0f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), oldE.Embed("seed"));
            EmbedderFingerprint.Restamp(store, oldE);

            var newE = new ConstantEmbedder(0.5f);
            Assert.Throws<EmbedderFingerprintMismatchException>(
                () => EmbedderMigration.EnsureCompatibleSqlite(store, newE, OnModelChange.Block, log: null));
        }
        finally { Cleanup(dbPath); }
    }

    // ---------------------------------------------------------------------
    // EnsureCompatiblePostgres (no live pg — only the _meta surface is touched)
    // ---------------------------------------------------------------------

    [Fact]
    public void Postgres_Unstamped_Empty_ReturnsCompatible_Stamps()
    {
        var store = new InMemoryMetaOnlyStore();
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
            EmbedderFingerprint.Check(store, e, out _));

        var decision = EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Auto, log: null);

        Assert.Equal(EmbedderCompatibility.Compatible, decision);
        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(store, e, out _));
    }

    [Fact]
    public void Postgres_Match_ReturnsCompatible_NoOp()
    {
        var store = new InMemoryMetaOnlyStore();
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);
        EmbedderFingerprint.Restamp(store, e);

        var log = new StringWriter();
        var decision = EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Auto, log);

        Assert.Equal(EmbedderCompatibility.Compatible, decision);
        Assert.Equal("", log.ToString());
        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(store, e, out _));
    }

    [Fact]
    public void Postgres_Mismatch_Block_Throws()
    {
        var store = new InMemoryMetaOnlyStore();
        var oldE = new StubEmbedder("onnx", "minilm", "rev1", 384);
        EmbedderFingerprint.Restamp(store, oldE);

        var newE = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);
        Assert.Throws<EmbedderFingerprintMismatchException>(
            () => EmbedderMigration.EnsureCompatiblePostgres(store, newE, OnModelChange.Block, log: null));
    }

    [Fact]
    public void Postgres_Mismatch_Warn_ReturnsWarned_Unchanged_Logs()
    {
        var store = new InMemoryMetaOnlyStore();
        var oldE = new StubEmbedder("onnx", "minilm", "rev1", 384);
        EmbedderFingerprint.Restamp(store, oldE);

        var newE = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);
        var log = new StringWriter();
        var decision = EmbedderMigration.EnsureCompatiblePostgres(store, newE, OnModelChange.Warn, log);

        Assert.Equal(EmbedderCompatibility.Warned, decision);
        // No restamp: still Mismatch; old fingerprint intact.
        Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
            EmbedderFingerprint.Check(store, newE, out _));
        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(store, oldE, out _));
        Assert.Contains("on_model_change=warn", log.ToString());
    }

    [Fact]
    public void Postgres_Mismatch_Auto_Throws_MentionsPostgresBackend()
    {
        var store = new InMemoryMetaOnlyStore();
        var oldE = new StubEmbedder("onnx", "minilm", "rev1", 384);
        EmbedderFingerprint.Restamp(store, oldE);

        var newE = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => EmbedderMigration.EnsureCompatiblePostgres(store, newE, OnModelChange.Auto, log: null));
        Assert.Contains("postgres backend", ex.Message);
    }

    // --- Unstamped + populated postgres cases (uses a content-returning double) ---

    [Fact]
    public void Postgres_Unstamped_Populated_Auto_Throws_MentionsPostgresBackend()
    {
        // Populated (Count returns ≥1 row) but no fingerprint stamped → must route
        // into the same dispatch as Mismatch; auto on postgres is unsupported.
        var store = new InMemoryMetaContentStore(rowCount: 1);
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
            EmbedderFingerprint.Check(store, e, out _));

        var ex = Assert.Throws<System.InvalidOperationException>(
            () => EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Auto, log: null));
        Assert.Contains("postgres backend", ex.Message);

        // No restamp on the throw path → still Unstamped.
        Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
            EmbedderFingerprint.Check(store, e, out _));
    }

    [Fact]
    public void Postgres_Unstamped_Populated_Warn_ReturnsWarned_Logs()
    {
        var store = new InMemoryMetaContentStore(rowCount: 1);
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);

        var log = new StringWriter();
        var decision = EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Warn, log);

        Assert.Equal(EmbedderCompatibility.Warned, decision);
        // No restamp: still Unstamped (the nag persists across restarts).
        Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
            EmbedderFingerprint.Check(store, e, out _));
        Assert.Contains("on_model_change=warn", log.ToString());
        // Distinct unstamped wording (not the "X -> Y" Mismatch message) — guards
        // against a future copy-paste of the Mismatch branch into this path.
        Assert.Contains("no embedder fingerprint", log.ToString());
    }

    [Fact]
    public void Postgres_Unstamped_Populated_Block_Throws()
    {
        var store = new InMemoryMetaContentStore(rowCount: 1);
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);

        Assert.Throws<EmbedderFingerprintMismatchException>(
            () => EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Block, log: null));
    }

    // ---------------------------------------------------------------------
    private static string TempDb(string tag) =>
        Path.Combine(Path.GetTempPath(), $"tr-embmig-{tag}-{System.Guid.NewGuid():N}.db");

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
