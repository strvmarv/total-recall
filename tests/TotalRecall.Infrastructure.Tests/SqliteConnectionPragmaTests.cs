using System;
using System.IO;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Regression tests asserting that every connection opened via
/// <see cref="SqliteConnection.Open"/> has WAL journal mode and a
/// <c>busy_timeout</c> of at least 3 000 ms — both required for safe
/// cross-process access when <c>total-recall ui</c> runs concurrently with
/// the MCP server against the same SQLite file.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqliteConnectionPragmaTests
{
    [Fact]
    public void Open_SetsWalAndBusyTimeout()
    {
        var path = Path.Combine(Path.GetTempPath(), "tr-pragma-" + Guid.NewGuid() + ".db");
        try
        {
            string journalMode;
            int busyTimeout;
            using (var conn = SqliteConnection.Open(path))
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                journalMode = Convert.ToString(cmd.ExecuteScalar())!.ToLowerInvariant();
                cmd.CommandText = "PRAGMA busy_timeout;";
                busyTimeout = Convert.ToInt32(cmd.ExecuteScalar());
            }
            Assert.Equal("wal", journalMode);
            Assert.True(busyTimeout >= 3000);
        }
        finally
        {
            // WAL mode creates -wal and -shm sidecar files; delete all three.
            MsSqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = path + suffix;
                if (File.Exists(f)) File.Delete(f);
            }
        }
    }
}
