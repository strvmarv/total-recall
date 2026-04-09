// src/TotalRecall.Cli/Internal/MemoryComponents.cs
//
// Plan 5 Task 5.4 — shared production-wiring helper for the memory admin
// verbs (promote, demote, inspect). Opens a single long-lived SqliteConnection,
// runs pending migrations, and hands back the (store, vec, embedder) triple
// that promote/demote need. Callers are responsible for disposing the
// returned handle — it owns the connection.
//
// Inspect only needs the store half of this triple and does not incur the
// ~40MB embedder cost; it opens its own connection directly instead of
// pretending to use this helper with a null embedder.

using System;
using TotalRecall.Infrastructure.Config;
using TotalRecall.Infrastructure.Embedding;
using TotalRecall.Infrastructure.Search;
using TotalRecall.Infrastructure.Storage;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using StoragePath = System.IO.Path;

namespace TotalRecall.Cli.Internal;

internal sealed class MemoryComponents : IDisposable
{
    public MsSqliteConnection Connection { get; }
    public ISqliteStore Store { get; }
    public IVectorSearch Vec { get; }
    public IEmbedder Embedder { get; }

    private MemoryComponents(
        MsSqliteConnection conn,
        ISqliteStore store,
        IVectorSearch vec,
        IEmbedder embedder)
    {
        Connection = conn;
        Store = store;
        Vec = vec;
        Embedder = embedder;
    }

    public static MemoryComponents OpenProduction()
    {
        var dbPath = ConfigLoader.GetDbPath();
        var conn = SqliteConnection.Open(dbPath);
        try
        {
            MigrationRunner.RunMigrations(conn);
            var store = new SqliteStore(conn);
            var vec = new VectorSearch(conn);
            var embedder = EmbedderFactory.CreateProduction();
            return new MemoryComponents(conn, store, vec, embedder);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Connection.Dispose();
    }
}
