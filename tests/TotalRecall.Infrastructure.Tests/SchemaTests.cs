using System.Collections.Generic;
using TotalRecall.Infrastructure.Storage;
using Xunit;

namespace TotalRecall.Infrastructure.Tests;

/// <summary>
/// Integration tests for <see cref="MigrationRunner"/>. These exercise the
/// full 3-migration schema against a fresh <c>:memory:</c> database and
/// assert the resulting table topology matches the TS reference
/// (<c>src-ts/db/schema.ts</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaTests
{
    private static readonly string[] ExpectedContentTables =
    {
        "hot_memories", "warm_memories", "cold_memories",
        "hot_knowledge", "warm_knowledge", "cold_knowledge",
    };

    private static readonly string[] ExpectedVecTables =
    {
        "hot_memories_vec", "warm_memories_vec", "cold_memories_vec",
        "hot_knowledge_vec", "warm_knowledge_vec", "cold_knowledge_vec",
    };

    private static readonly string[] ExpectedFtsTables =
    {
        "hot_memories_fts", "warm_memories_fts", "cold_memories_fts",
        "hot_knowledge_fts", "warm_knowledge_fts", "cold_knowledge_fts",
    };

    private static readonly string[] ExpectedSystemTables =
    {
        "retrieval_events", "compaction_log", "config_snapshots", "import_log",
        "_meta", "benchmark_candidates", "_schema_version",
    };

    private static HashSet<string> ListTables(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var names = new HashSet<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void RunMigrations_FreshDb_CreatesAllContentTables()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var tables = ListTables(conn);
        foreach (var expected in ExpectedContentTables)
            Assert.Contains(expected, tables);
    }

    [Fact]
    public void RunMigrations_FreshDb_CreatesAllVecTables()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        // vec0 virtual tables appear in sqlite_master as type='table'.
        var tables = ListTables(conn);
        foreach (var expected in ExpectedVecTables)
            Assert.Contains(expected, tables);
    }

    [Fact]
    public void RunMigrations_FreshDb_CreatesAllFtsTables()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var tables = ListTables(conn);
        foreach (var expected in ExpectedFtsTables)
            Assert.Contains(expected, tables);
    }

    [Fact]
    public void RunMigrations_FreshDb_CreatesSystemTables()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var tables = ListTables(conn);
        foreach (var expected in ExpectedSystemTables)
            Assert.Contains(expected, tables);
    }

    [Fact]
    public void RunMigrations_FreshDb_CreatesAllFtsSyncTriggers()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var triggerNames = new HashSet<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' ORDER BY name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) triggerNames.Add(reader.GetString(0));

        // Each of the 6 base content tables has 3 FTS sync triggers:
        //   <base>_fts_ai (after insert), <base>_fts_ad (after delete), <base>_fts_au (after update)
        var expected = new HashSet<string>();
        foreach (var (tier, type) in new[] {
            ("hot", "memories"), ("hot", "knowledge"),
            ("warm", "memories"), ("warm", "knowledge"),
            ("cold", "memories"), ("cold", "knowledge"),
        })
        {
            var b = $"{tier}_{type}";
            expected.Add($"{b}_fts_ai");
            expected.Add($"{b}_fts_ad");
            expected.Add($"{b}_fts_au");
        }

        Assert.Equal(18, expected.Count);
        Assert.Superset(expected, triggerNames);
    }

    [Fact]
    public void RunMigrations_RunTwice_Idempotent()
    {
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var tablesAfterFirst = ListTables(conn);

        // Second run should be a no-op: no errors, no duplicate schema,
        // no extra _schema_version rows beyond the initial 3.
        MigrationRunner.RunMigrations(conn);
        var tablesAfterSecond = ListTables(conn);

        Assert.Equal(tablesAfterFirst, tablesAfterSecond);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(version) FROM _schema_version";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var count = reader.GetInt64(0);
        var maxVersion = reader.GetInt64(1);
        Assert.Equal(3L, count);
        Assert.Equal(3L, maxVersion);
    }

    [Fact]
    public void RunMigrations_FreshInit_WritesMigrationCompleteMarker()
    {
        // Plan 7 Task 7.-1: a brand-new .NET DB must carry the marker so
        // AutoMigrationGuard can distinguish it from an unmigrated TS DB.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", MigrationRunner.MigrationCompleteMarkerKey);
        var value = cmd.ExecuteScalar() as string;
        Assert.False(string.IsNullOrEmpty(value));
    }

    [Fact]
    public void RunMigrations_ReRun_DoesNotOverwriteExistingMarker()
    {
        // Calling RunMigrations twice on the same DB must not touch the marker
        // that was stamped on the first (fresh-init) run. We prove this by
        // pinning a sentinel value and asserting it survives a second call.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText =
                "UPDATE _meta SET value = 'sentinel' WHERE key = $k";
            setCmd.Parameters.AddWithValue("$k", MigrationRunner.MigrationCompleteMarkerKey);
            setCmd.ExecuteNonQuery();
        }

        MigrationRunner.RunMigrations(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", MigrationRunner.MigrationCompleteMarkerKey);
        Assert.Equal("sentinel", cmd.ExecuteScalar() as string);
    }
}
