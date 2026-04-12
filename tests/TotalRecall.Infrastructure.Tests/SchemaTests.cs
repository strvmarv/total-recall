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
        Assert.Equal(7L, count);
        Assert.Equal(7L, maxVersion);
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

    // --- Migration 5 / orphan cleanup -------------------------------------

    private static void InsertContentRow(
        Microsoft.Data.Sqlite.SqliteConnection conn, string table, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {table}
  (id, content, source, source_tool, project, tags,
   created_at, updated_at, last_accessed_at, access_count,
   decay_score, parent_id, collection_id, metadata)
VALUES
  ($id, 'c', NULL, NULL, NULL, '[]',
   1, 1, 1, 0,
   1.0, NULL, NULL, '{{}}')";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void InsertVecRow(
        Microsoft.Data.Sqlite.SqliteConnection conn, string vecTable, long rowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO {vecTable} (rowid, embedding) VALUES ($rowid, zeroblob(1536))";
        cmd.Parameters.AddWithValue("$rowid", rowid);
        cmd.ExecuteNonQuery();
    }

    private static long CountContent(
        Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool VecRowidExists(
        Microsoft.Data.Sqlite.SqliteConnection conn, string vecTable, long rowid)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {vecTable} WHERE rowid = $rowid)";
        cmd.Parameters.AddWithValue("$rowid", rowid);
        return (long)cmd.ExecuteScalar()! == 1;
    }

    [Fact]
    public void CleanupOrphanRows_RemovesOrphanVecRow()
    {
        // Orphan type 1: a vec row whose rowid has no matching content
        // row. Produced in 0.6.7 by the wrong delete order in
        // MemoryDeleteHandler (content deleted before vec, so the vec
        // rowid lookup returned null and left the vec row behind).
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        InsertContentRow(conn, "hot_memories", "valid");
        InsertVecRow(conn, "hot_memories_vec", rowid: 1);
        InsertVecRow(conn, "hot_memories_vec", rowid: 99);

        MigrationRunner.CleanupOrphanRows(conn);

        Assert.True(VecRowidExists(conn, "hot_memories_vec", 1));
        Assert.False(VecRowidExists(conn, "hot_memories_vec", 99));
    }

    [Fact]
    public void CleanupOrphanRows_RemovesOrphanContentRow()
    {
        // Orphan type 2: a content row with no matching vec row.
        // Produced when a memory_store call's vec insert failed (e.g.
        // colliding with a type-1 orphan) while the content insert had
        // already committed, since the two were not wrapped in a
        // transaction. Must also be cleaned up.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        InsertContentRow(conn, "hot_memories", "valid");
        InsertVecRow(conn, "hot_memories_vec", rowid: 1);
        InsertContentRow(conn, "hot_memories", "orphan");

        MigrationRunner.CleanupOrphanRows(conn);

        Assert.Equal(1L, CountContent(conn, "hot_memories"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM hot_memories";
        Assert.Equal("valid", cmd.ExecuteScalar() as string);
    }

    [Fact]
    public void CleanupOrphanRows_CoversAllTierTypePairs()
    {
        // Pin the cleanup sweep to all 6 (tier, type) pairs so a future
        // change can't silently skip one table.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        var pairs = new[]
        {
            ("hot_memories",  "hot_memories_vec"),
            ("warm_memories", "warm_memories_vec"),
            ("cold_memories", "cold_memories_vec"),
            ("hot_knowledge",  "hot_knowledge_vec"),
            ("warm_knowledge", "warm_knowledge_vec"),
            ("cold_knowledge", "cold_knowledge_vec"),
        };
        foreach (var (_, vec) in pairs)
            InsertVecRow(conn, vec, rowid: 42);

        MigrationRunner.CleanupOrphanRows(conn);

        foreach (var (_, vec) in pairs)
            Assert.False(VecRowidExists(conn, vec, 42), $"orphan at {vec}.rowid=42 not cleaned");
    }

    [Fact]
    public void CleanupOrphanRows_PreservesAlignedRows()
    {
        // A fully-aligned row (content + matching vec) must survive
        // cleanup unchanged.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        InsertContentRow(conn, "hot_memories", "keep");
        InsertVecRow(conn, "hot_memories_vec", rowid: 1);

        MigrationRunner.CleanupOrphanRows(conn);

        Assert.Equal(1L, CountContent(conn, "hot_memories"));
        Assert.True(VecRowidExists(conn, "hot_memories_vec", 1));
    }

    [Fact]
    public void RunMigrations_FreshDb_CompactionLogHasSourceColumn()
    {
        // Migration 4 adds a `source` column to compaction_log so history
        // can distinguish compaction-driven movements from manual ones
        // (API promote/demote, importers, etc.). The column is NOT NULL
        // with DEFAULT 'compaction' to preserve existing semantics for
        // pre-v4 rows on upgrade.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(compaction_log)";
        using var reader = cmd.ExecuteReader();

        string? typeName = null;
        int notNull = -1;
        string? defaultValue = null;
        bool found = false;
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (name == "source")
            {
                found = true;
                typeName = reader.GetString(2);
                notNull = reader.GetInt32(3);
                defaultValue = reader.IsDBNull(4) ? null : reader.GetString(4);
                break;
            }
        }

        Assert.True(found, "compaction_log.source column is missing");
        Assert.Equal("TEXT", typeName);
        Assert.Equal(1, notNull);
        Assert.Equal("'compaction'", defaultValue);
    }

    [Fact]
    public void RunMigrations_CompactionLogSource_DefaultsToCompaction()
    {
        // An INSERT that omits the source column must fall back to the
        // DEFAULT so existing call sites (which haven't been updated to
        // pass source yet) continue to produce semantically-correct rows.
        using var conn = SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO compaction_log
                  (id, timestamp, source_tier, reason, config_snapshot_id)
                VALUES
                  ('abc', 1, 'hot', 'test', 'cfg-1')
                """;
            insert.ExecuteNonQuery();
        }

        using var select = conn.CreateCommand();
        select.CommandText = "SELECT source FROM compaction_log WHERE id = 'abc'";
        var value = select.ExecuteScalar() as string;
        Assert.Equal("compaction", value);
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
