using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.GitSync;

/// <summary>
/// Migration 18: the git sync state tables and the import_log(target_entry_id) index. Not
/// <c>Integration</c>-trait, so the standard verify runs them.
/// </summary>
public sealed class GitSyncSchemaTests
{
    private static MsSqliteConnection Migrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        return conn;
    }

    private static long Scalar(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Exec(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private record Column(string Name, string Type, bool NotNull, int Pk);

    private static List<Column> Columns(MsSqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name, type, \"notnull\", pk FROM pragma_table_info('{table}') ORDER BY cid";
        using var r = cmd.ExecuteReader();
        var list = new List<Column>();
        while (r.Read())
            list.Add(new Column(r.GetString(0), r.GetString(1), r.GetInt64(2) == 1, (int)r.GetInt64(3)));
        return list;
    }

    private static List<string> Rows(MsSqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
        {
            var parts = new string[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
                parts[i] = r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            list.Add(string.Join("|", parts));
        }
        return list;
    }

    private static List<string> SchemaObjects(MsSqliteConnection conn) =>
        Rows(conn, "SELECT type || ':' || name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name");

    [Fact]
    public void FreshDatabase_HasTheFourTablesWithTheDesignsColumns()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[]
            {
                new Column("entry_id", "TEXT", true, 1), new Column("state", "TEXT", true, 0),
                new Column("base_json", "TEXT", false, 0), new Column("scanned_content_hash", "TEXT", false, 0),
                new Column("scanned_tags_hash", "TEXT", false, 0), new Column("local_sticky", "INTEGER", false, 0),
                new Column("local_tier", "TEXT", false, 0), new Column("content_stamp", "TEXT", false, 0),
                new Column("sticky_stamp", "TEXT", false, 0), new Column("tier_stamp", "TEXT", false, 0),
                new Column("repush_groups", "TEXT", false, 0), new Column("pushing_stamps", "TEXT", false, 0),
                new Column("blocked_hash", "TEXT", false, 0), new Column("blocked_reason", "TEXT", false, 0),
                new Column("allowed_hash", "TEXT", false, 0),
            },
            Columns(conn, "git_sync_entry"));

        Assert.Equal(
            new[]
            {
                new Column("entry_id", "TEXT", true, 1), new Column("kind", "TEXT", true, 2),
                new Column("state", "TEXT", true, 0), new Column("record_hash", "TEXT", false, 0),
                new Column("direction", "TEXT", false, 0), new Column("payload_json", "TEXT", false, 0),
                new Column("machine", "TEXT", false, 0), new Column("arrived_at", "INTEGER", true, 0),
            },
            Columns(conn, "git_sync_review"));

        Assert.Equal(
            new[]
            {
                new Column("entry_id", "TEXT", true, 1), new Column("kind", "TEXT", true, 2),
                new Column("record_hash", "TEXT", false, 0), new Column("attempts", "INTEGER", true, 0),
                new Column("detail", "TEXT", false, 0),
            },
            Columns(conn, "git_sync_issue"));

        Assert.Equal(
            new[]
            {
                new Column("canonical_id", "TEXT", true, 1), new Column("other_id", "TEXT", true, 2),
                new Column("origin_hash", "TEXT", false, 0), new Column("state", "TEXT", true, 0),
            },
            Columns(conn, "git_sync_alias"));
    }

    [Fact]
    public void FreshDatabase_HasTheImportLogIndex()
    {
        using var conn = Migrated();
        var cols = Rows(conn, "SELECT name FROM pragma_index_info('idx_import_log_target_entry_id')");
        Assert.Equal(new[] { "target_entry_id" }, cols);
        Assert.Equal(
            1L,
            Scalar(conn, "SELECT COUNT(*) FROM pragma_index_list('import_log') WHERE name = 'idx_import_log_target_entry_id'"));
    }

    [Fact]
    public void SchemaVersion_IsEighteen()
    {
        using var conn = Migrated();
        Assert.Equal(18L, Scalar(conn, "SELECT MAX(version) FROM _schema_version"));
        Assert.Equal(18L, Scalar(conn, "SELECT COUNT(*) FROM _schema_version"));
    }

    [Fact]
    public void RunningTwice_ChangesNothing()
    {
        using var conn = Migrated();
        var objects = SchemaObjects(conn);
        var versions = Rows(conn, "SELECT version FROM _schema_version ORDER BY version");

        MigrationRunner.RunMigrations(conn);

        Assert.Equal(objects, SchemaObjects(conn));
        Assert.Equal(versions, Rows(conn, "SELECT version FROM _schema_version ORDER BY version"));
    }

    [Fact]
    public void IfNotExists_IsExercised_WhenTheVersionRowIsMissingButTheTablesExist()
    {
        using var conn = Migrated();
        Exec(conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('keep-me', 'active')");
        Exec(conn, "DELETE FROM _schema_version WHERE version = 18");

        MigrationRunner.RunMigrations(conn);

        Assert.Equal(18L, Scalar(conn, "SELECT MAX(version) FROM _schema_version"));
        Assert.Equal(new[] { "keep-me|active" }, Rows(conn, "SELECT entry_id, state FROM git_sync_entry"));
    }

    [Fact]
    public void UpgradeFromSeventeen_KeepsEveryExistingRow()
    {
        using var conn = Migrated();
        Exec(conn, "INSERT INTO _meta (key, value) VALUES ('x', 'y')");
        Exec(conn,
            "INSERT INTO import_log (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type) " +
            "VALUES ('i1', 1700000000000, 'claude-code', '/p/f.md', 'abc123', 'e1', 'warm', 'memory')");
        Exec(conn,
            "INSERT INTO hot_memories (id, content, created_at, updated_at, last_accessed_at) " +
            "VALUES ('e1', 'remember this', 1700000000000, 1700000000001, 1700000000002)");
        var before = new[]
        {
            Rows(conn, "SELECT * FROM _meta ORDER BY key"),
            Rows(conn, "SELECT * FROM import_log ORDER BY id"),
            Rows(conn, "SELECT * FROM hot_memories ORDER BY id"),
        };

        // Put the database back at version 17: no sync objects, no version-18 row.
        foreach (var t in new[] { "git_sync_entry", "git_sync_review", "git_sync_issue", "git_sync_alias" })
            Exec(conn, $"DROP TABLE {t}");
        Exec(conn, "DROP INDEX idx_import_log_target_entry_id");
        Exec(conn, "DELETE FROM _schema_version WHERE version = 18");
        Assert.Equal(17L, Scalar(conn, "SELECT MAX(version) FROM _schema_version"));

        MigrationRunner.RunMigrations(conn);

        Assert.Equal(18L, Scalar(conn, "SELECT MAX(version) FROM _schema_version"));
        foreach (var t in new[] { "git_sync_entry", "git_sync_review", "git_sync_issue", "git_sync_alias" })
            Assert.Equal(1L, Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{t}'"));
        Assert.Equal(
            1L,
            Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_import_log_target_entry_id'"));
        Assert.Equal(before[0], Rows(conn, "SELECT * FROM _meta ORDER BY key"));
        Assert.Equal(before[1], Rows(conn, "SELECT * FROM import_log ORDER BY id"));
        Assert.Equal(before[2], Rows(conn, "SELECT * FROM hot_memories ORDER BY id"));
    }

    private static void AssertCheckRejects(MsSqliteConnection conn, string sql)
    {
        var ex = Assert.Throws<SqliteException>(() => Exec(conn, sql));
        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    private static void AssertRefused(MsSqliteConnection conn, string sql, string kind)
    {
        var ex = Assert.Throws<SqliteException>(() => Exec(conn, sql));
        Assert.Contains(kind, ex.Message);
    }

    [Fact]
    public void Constraints_EntryState()
    {
        using var conn = Migrated();
        var n = 0;
        foreach (var ok in new[] { "active", "forgotten", "left" })
            Exec(conn, $"INSERT INTO git_sync_entry (entry_id, state) VALUES ('e{n++}', '{ok}')");
        AssertCheckRejects(conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('bad', 'deleted')");
    }

    [Fact]
    public void Constraints_ReviewKindStateAndDirection()
    {
        using var conn = Migrated();
        var n = 0;
        string Insert(string kind, string state, string direction) =>
            $"INSERT INTO git_sync_review (entry_id, kind, state, direction, arrived_at) VALUES ('r{n++}', '{kind}', '{state}', {direction}, 1)";

        foreach (var kind in new[] { "new-pin", "pin-edit", "conflict", "held-delete" })
            Exec(conn, Insert(kind, "open", "NULL"));
        foreach (var state in new[] { "open", "unseen", "approved", "declined", "resolved" })
            Exec(conn, Insert("conflict", state, "NULL"));
        foreach (var dir in new[] { "'remote'", "'local'", "NULL" })
            Exec(conn, Insert("held-delete", "open", dir));

        AssertCheckRejects(conn, Insert("new_pin", "open", "NULL"));
        AssertCheckRejects(conn, Insert("conflict", "closed", "NULL"));
        AssertCheckRejects(conn, Insert("held-delete", "open", "'both'"));
    }

    [Fact]
    public void Constraints_IssueKind()
    {
        using var conn = Migrated();
        var n = 0;
        foreach (var ok in new[] { "apply-failed", "rejected-record", "ineligible-id" })
            Exec(conn, $"INSERT INTO git_sync_issue (entry_id, kind) VALUES ('i{n++}', '{ok}')");
        AssertCheckRejects(conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('bad', 'other')");
    }

    [Fact]
    public void Constraints_AliasState()
    {
        using var conn = Migrated();
        var n = 0;
        foreach (var ok in new[] { "open", "converged", "resolved", "abandoned" })
            Exec(conn, $"INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('c{n}', 'o{n++}', '{ok}')");
        AssertCheckRejects(conn, "INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('c', 'o', 'done')");
    }

    [Fact]
    public void PrimaryKeys_RefuseDuplicates()
    {
        using var conn = Migrated();

        Exec(conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('e', 'active')");
        AssertRefused(conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('e', 'left')", "UNIQUE constraint failed");

        Exec(conn, "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e', 'conflict', 'open', 1)");
        AssertRefused(conn, "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e', 'conflict', 'resolved', 2)", "UNIQUE constraint failed");
        Exec(conn, "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e', 'pin-edit', 'open', 1)"); // another kind: fine

        Exec(conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('e', 'apply-failed')");
        AssertRefused(conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('e', 'apply-failed')", "UNIQUE constraint failed");
        Exec(conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('e', 'rejected-record')");

        Exec(conn, "INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('a', 'b', 'open')");
        AssertRefused(conn, "INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('a', 'b', 'resolved')", "UNIQUE constraint failed");
        Exec(conn, "INSERT INTO git_sync_alias (canonical_id, other_id, state) VALUES ('b', 'a', 'open')"); // the reversed pair: fine
    }

    [Fact]
    public void NullabilityAndDefaults()
    {
        using var conn = Migrated();
        AssertRefused(
            conn,
            "INSERT INTO git_sync_review (entry_id, kind, state, arrived_at) VALUES ('e', 'conflict', 'open', NULL)",
            "NOT NULL constraint failed");

        Exec(conn, "INSERT INTO git_sync_issue (entry_id, kind) VALUES ('e', 'apply-failed')");
        Assert.Equal(0L, Scalar(conn, "SELECT attempts FROM git_sync_issue WHERE entry_id = 'e'"));

        Exec(conn, "INSERT INTO git_sync_entry (entry_id, state) VALUES ('only-two', 'active')");
        Assert.Equal(1L, Scalar(conn, "SELECT COUNT(*) FROM git_sync_entry WHERE base_json IS NULL AND local_sticky IS NULL"));
    }
}
