using System;
using System.Collections.Generic;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

public sealed record SearchHit(long Id, string Content, float Score);

public sealed class SqliteStore : IDisposable
{
    private readonly MsSqliteConnection _conn;

    public SqliteStore(string dbPath)
    {
        _conn = SqliteConnection.Open(dbPath);
        MigrationRunner.RunMigrations(_conn);
    }

    public long Insert(string content, float[] embedding)
    {
        throw new NotImplementedException("Replaced by tier-aware CRUD in Plan 3 Task 3.3.");
    }

    public IReadOnlyList<SearchHit> Search(float[] queryEmbedding, int topK)
    {
        throw new NotImplementedException("Replaced by tier-aware CRUD in Plan 3 Task 3.3.");
    }

    public void Dispose() => _conn.Dispose();
}
