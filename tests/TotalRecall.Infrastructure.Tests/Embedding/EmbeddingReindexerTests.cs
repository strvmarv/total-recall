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
            // The sqlite-vec native handle + Microsoft.Data.Sqlite pooling can
            // keep the file mapped briefly past Dispose on Windows; drain the
            // pool, then delete the db (and WAL/SHM sidecars) best-effort so a
            // transient lock never fails an otherwise-passing test.
            MsSqliteConnection.ClearAllPools();
            foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (IOException) { }
            }
        }
    }
}
