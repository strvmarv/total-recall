using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests.Storage;

public sealed class UsageTelemetrySchemaTests
{
    [Fact]
    public void Migration6_CreatesUsageTables()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var tables = QueryTableNames(conn);
        Assert.Contains("usage_events", tables);
        Assert.Contains("usage_daily", tables);
        Assert.Contains("usage_watermarks", tables);
    }

    [Fact]
    public void Migration6_UsageEventsHasRequiredColumns()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var cols = QueryColumnNames(conn, "usage_events");
        var required = new[] {
            "id", "host", "host_event_id", "session_id", "ts",
            "project_path", "project_repo", "model",
            "input_tokens", "cache_creation_5m", "cache_creation_1h",
            "cache_read", "output_tokens", "host_request_id"
        };
        foreach (var c in required)
            Assert.Contains(c, cols);
    }

    [Fact]
    public void Migration6_UsageEventsUniqueOnHostAndHostEventId()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        using (var insert1 = conn.CreateCommand())
        {
            insert1.CommandText =
                "INSERT INTO usage_events (host, host_event_id, session_id, ts) " +
                "VALUES ('claude-code', 'abc', 'sess1', 100)";
            insert1.ExecuteNonQuery();
        }

        using var insert2 = conn.CreateCommand();
        insert2.CommandText =
            "INSERT OR IGNORE INTO usage_events (host, host_event_id, session_id, ts) " +
            "VALUES ('claude-code', 'abc', 'sess1', 200)";
        var affected = insert2.ExecuteNonQuery();
        Assert.Equal(0, affected); // duplicate ignored
    }

    [Fact]
    public void Migration6_UsageDailyPrimaryKey()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        // First insertion
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO usage_daily (day_utc, host, model, project, session_count, turn_count) " +
                "VALUES (1000, 'claude-code', 'opus', 'foo', 1, 5)";
            insert.ExecuteNonQuery();
        }

        // INSERT OR REPLACE on the same composite key must overwrite, not add.
        using (var replace = conn.CreateCommand())
        {
            replace.CommandText =
                "INSERT OR REPLACE INTO usage_daily (day_utc, host, model, project, session_count, turn_count) " +
                "VALUES (1000, 'claude-code', 'opus', 'foo', 2, 10)";
            replace.ExecuteNonQuery();
        }

        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM usage_daily";
            Assert.Equal(1L, (long)count.ExecuteScalar()!);
        }

        // A row differing only on `model` must NOT collide with the existing
        // row — proves the PK spans all four key columns, not just day_utc+host.
        using (var differentModel = conn.CreateCommand())
        {
            differentModel.CommandText =
                "INSERT INTO usage_daily (day_utc, host, model, project, session_count, turn_count) " +
                "VALUES (1000, 'claude-code', 'sonnet', 'foo', 3, 15)";
            differentModel.ExecuteNonQuery();
        }

        // And a row differing only on `project` must also NOT collide.
        using (var differentProject = conn.CreateCommand())
        {
            differentProject.CommandText =
                "INSERT INTO usage_daily (day_utc, host, model, project, session_count, turn_count) " +
                "VALUES (1000, 'claude-code', 'opus', 'bar', 4, 20)";
            differentProject.ExecuteNonQuery();
        }

        using (var count2 = conn.CreateCommand())
        {
            count2.CommandText = "SELECT COUNT(*) FROM usage_daily";
            Assert.Equal(3L, (long)count2.ExecuteScalar()!);
        }
    }

    private static System.Collections.Generic.HashSet<string> QueryTableNames(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private static System.Collections.Generic.HashSet<string> QueryColumnNames(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        var names = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(1));
        return names;
    }
}
