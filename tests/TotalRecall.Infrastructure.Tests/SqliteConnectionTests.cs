using System;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="SqliteConnection"/>. These touch the
/// real Microsoft.Data.Sqlite provider and load the native sqlite-vec
/// extension from the output <c>runtimes/</c> folder, so they are marked
/// Integration and require the MSBuild copy step in
/// <c>TotalRecall.Infrastructure.csproj</c> to have run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqliteConnectionTests
{
    [Fact]
    public void Open_InMemory_LoadsVecExtension()
    {
        using var conn = SqliteConnection.Open(":memory:");

        Assert.Equal(System.Data.ConnectionState.Open, conn.State);

        // If sqlite-vec loaded successfully, creating a vec0 virtual
        // table should succeed. If the extension is missing, this throws
        // SqliteException with "no such module: vec0".
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE VIRTUAL TABLE t_vec USING vec0(embedding float[4])";
            cmd.ExecuteNonQuery();
        }

        // Sanity: insert and query a row through the virtual table.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO t_vec(rowid, embedding) VALUES (1, $vec)";
            var bytes = new byte[4 * sizeof(float)];
            Buffer.BlockCopy(new float[] { 1f, 0f, 0f, 0f }, 0, bytes, 0, bytes.Length);
            cmd.Parameters.AddWithValue("$vec", bytes);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM t_vec";
            var count = (long)cmd.ExecuteScalar()!;
            Assert.Equal(1L, count);
        }
    }

    [Fact]
    public void Open_InMemory_AppliesPragmas()
    {
        using var conn = SqliteConnection.Open(":memory:");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys";
        var fk = (long)cmd.ExecuteScalar()!;
        Assert.Equal(1L, fk);

        cmd.CommandText = "PRAGMA synchronous";
        var sync = (long)cmd.ExecuteScalar()!;
        // NORMAL = 1
        Assert.Equal(1L, sync);
    }
}
