using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class EmbedderMigrationTests
{
    // ---------------------------------------------------------------------
    // EnsureCompatibleSqlite
    // ---------------------------------------------------------------------

    [Fact]
    public void Sqlite_Unstamped_Stamps_NoThrow_VectorsUntouched()
    {
        var dbPath = TempDb("unstamped");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            var e = new ConstantEmbedder(0.5f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), e.Embed("seed"));

            // No fingerprint stamped yet → Unstamped.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
                EmbedderFingerprint.Check(store, e, out _));

            EmbedderMigration.EnsureCompatibleSqlite(conn, store, vec, e, OnModelChange.Auto, log: null);

            // Now stamped → Match.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, e, out _));

            // Vectors untouched: the seed still self-matches at ~1.0.
            var hits = vec.SearchByVector(Tier.Warm, ContentType.Memory, e.Embed("seed"),
                new VectorSearchOpts(TopK: 5));
            Assert.Contains(hits, h => h.Score > 0.999);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Match_NoOp_NoThrow()
    {
        var dbPath = TempDb("match");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            var e = new ConstantEmbedder(0.5f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), e.Embed("seed"));
            EmbedderFingerprint.Restamp(store, e);

            var log = new StringWriter();
            EmbedderMigration.EnsureCompatibleSqlite(conn, store, vec, e, OnModelChange.Auto, log);

            // No-op: nothing logged, still Match.
            Assert.Equal("", log.ToString());
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, e, out _));
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Mismatch_Auto_ReEmbeds_RestampsAndLogs()
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
            EmbedderMigration.EnsureCompatibleSqlite(conn, store, vec, newE, OnModelChange.Auto, log);

            // Re-embedded → fingerprint now Match.
            Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                EmbedderFingerprint.Check(store, newE, out _));

            // Vectors rewritten into the new model's space: self-similarity ~1.0.
            var hits = vec.SearchByVector(Tier.Warm, ContentType.Memory, newE.Embed("anything"),
                new VectorSearchOpts(TopK: 5));
            Assert.Contains(hits, h => h.Score > 0.999);

            // A log line was emitted (re-embedding announcement).
            Assert.Contains("re-embedding the local database", log.ToString());
            Assert.Contains("re-embedded 2 entries", log.ToString());
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public void Sqlite_Mismatch_Warn_NoThrow_FingerprintUnchanged_Logs()
    {
        var dbPath = TempDb("warn");
        try
        {
            using var conn = SqliteConnection.Open(dbPath);
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);

            var oldE = new ConstantEmbedder(1.0f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), oldE.Embed("seed"));
            EmbedderFingerprint.Restamp(store, oldE);

            var newE = new ConstantEmbedder(0.5f);
            var log = new StringWriter();
            EmbedderMigration.EnsureCompatibleSqlite(conn, store, vec, newE, OnModelChange.Warn, log);

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
            var vec = new VectorSearch(conn);

            var oldE = new ConstantEmbedder(1.0f);
            store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                new InsertEntryOpts("seed"), oldE.Embed("seed"));
            EmbedderFingerprint.Restamp(store, oldE);

            var newE = new ConstantEmbedder(0.5f);
            Assert.Throws<EmbedderFingerprintMismatchException>(
                () => EmbedderMigration.EnsureCompatibleSqlite(conn, store, vec, newE, OnModelChange.Block, log: null));
        }
        finally { Cleanup(dbPath); }
    }

    // ---------------------------------------------------------------------
    // EnsureCompatiblePostgres (no live pg — only the _meta surface is touched)
    // ---------------------------------------------------------------------

    [Fact]
    public void Postgres_Unstamped_Stamps()
    {
        var store = new InMemoryMetaOnlyStore();
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Unstamped,
            EmbedderFingerprint.Check(store, e, out _));

        EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Auto, log: null);

        Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
            EmbedderFingerprint.Check(store, e, out _));
    }

    [Fact]
    public void Postgres_Match_NoOp()
    {
        var store = new InMemoryMetaOnlyStore();
        var e = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);
        EmbedderFingerprint.Restamp(store, e);

        var log = new StringWriter();
        EmbedderMigration.EnsureCompatiblePostgres(store, e, OnModelChange.Auto, log);

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
    public void Postgres_Mismatch_Warn_NoThrow_Unchanged_Logs()
    {
        var store = new InMemoryMetaOnlyStore();
        var oldE = new StubEmbedder("onnx", "minilm", "rev1", 384);
        EmbedderFingerprint.Restamp(store, oldE);

        var newE = new StubEmbedder("onnx", "bge-small-en-v1.5", "rev1", 384);
        var log = new StringWriter();
        EmbedderMigration.EnsureCompatiblePostgres(store, newE, OnModelChange.Warn, log);

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
