using System.IO;
using TotalRecall.Core;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using TotalRecall.Infrastructure.Tests.TestSupport;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Embedding;

public sealed class EmbeddingReindexerTests
{
    [Fact]
    public void Reindex_RewritesVectorsForAllEntries_AndReturnsCount()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "tr-reindex-" + System.Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = SqliteConnection.Open(dbPath))
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                var vec = new VectorSearch(conn);

                var oldE = new ConstantEmbedder(1.0f);
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("first fact"), oldE.Embed("first fact"));
                store.InsertWithEmbedding(Tier.Cold, ContentType.Memory,
                    new InsertEntryOpts("second fact"), oldE.Embed("second fact"));

                var newE = new ConstantEmbedder(0.5f);
                var reindexer = new EmbeddingReindexer(store, vec, newE);
                int n = reindexer.Reindex(progress: null);

                Assert.Equal(2, n);
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void RunAtomicSqlite_RewritesVectors_RestampsFingerprint_ReturnsCount()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "tr-atomic-" + System.Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = SqliteConnection.Open(dbPath))
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                var vec = new VectorSearch(conn);

                // Seed rows across DIFFERENT tier pairs with the OLD embedder's
                // content-dependent vectors, then stamp the OLD fingerprint —
                // exactly the "model changed under an existing DB" scenario.
                var oldE = new FakeEmbedder();
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("alpha"), oldE.Embed("alpha"));
                store.InsertWithEmbedding(Tier.Cold, ContentType.Memory,
                    new InsertEntryOpts("beta"), oldE.Embed("beta"));
                store.InsertWithEmbedding(Tier.Cold, ContentType.Knowledge,
                    new InsertEntryOpts("gamma"), oldE.Embed("gamma"));
                EmbedderFingerprint.Restamp(store, oldE);

                // New (mismatched) embedder. ConstantEmbedder produces a
                // unit-uniform vector independent of content, so self-similarity
                // against newE.Embed(...) is ~1.0 once a row is re-embedded.
                var newE = new ConstantEmbedder(0.5f);
                Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
                    EmbedderFingerprint.Check(store, newE, out _));

                int n = EmbeddingReindexer.RunAtomicSqlite(conn, store, vec, newE, progress: null);

                Assert.Equal(3, n);

                // Fingerprint now restamped to the new embedder.
                Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                    EmbedderFingerprint.Check(store, newE, out _));

                // Vectors rewritten: a search with newE's vector self-matches at ~1.0.
                var q = newE.Embed("anything");
                var hits = vec.SearchByVector(Tier.Warm, ContentType.Memory, q,
                    new VectorSearchOpts(TopK: 5));
                Assert.Contains(hits, h => h.Score > 0.999);
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void RunAtomicSqlite_MidRunThrow_RollsBack_FingerprintUnchanged()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "tr-atomic-rb-" + System.Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = SqliteConnection.Open(dbPath))
            {
                MigrationRunner.RunMigrations(conn);
                var store = new SqliteStore(conn);
                var vec = new VectorSearch(conn);

                // Seed rows + stamp an OLD fingerprint (ConstantEmbedder => "const-1").
                var oldE = new ConstantEmbedder(1.0f);
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("one"), oldE.Embed("one"));
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("two"), oldE.Embed("two"));
                store.InsertWithEmbedding(Tier.Warm, ContentType.Memory,
                    new InsertEntryOpts("three"), oldE.Embed("three"));
                EmbedderFingerprint.Restamp(store, oldE);

                // A (different) embedder that throws partway through the reindex.
                var throwing = new ThrowingEmbedder(throwOnCall: 2);
                Assert.Throws<System.InvalidOperationException>(
                    () => EmbeddingReindexer.RunAtomicSqlite(conn, store, vec, throwing, progress: null));

                // Transaction rolled back: the OLD fingerprint is intact (un-restamped).
                Assert.Equal(EmbedderFingerprint.FingerprintState.Match,
                    EmbedderFingerprint.Check(store, oldE, out _));
                // ...and it was never restamped to the throwing embedder.
                Assert.Equal(EmbedderFingerprint.FingerprintState.Mismatch,
                    EmbedderFingerprint.Check(store, throwing, out _));
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // The sqlite-vec native handle + Microsoft.Data.Sqlite pooling can keep the
    // file mapped briefly past Dispose on Windows; drain the pool, then delete
    // the db (and WAL/SHM sidecars) best-effort so a transient lock never fails
    // an otherwise-passing test.
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
