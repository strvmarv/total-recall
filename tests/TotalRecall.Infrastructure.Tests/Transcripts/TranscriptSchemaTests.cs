using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using TotalRecall.Infrastructure.Storage;
using Xunit;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Tests.Transcripts;

/// <summary>
/// The transcript archive schema (spec section 5) and its own runner. Not <c>Integration</c>-trait,
/// so the standard verify runs them.
/// </summary>
public sealed class TranscriptSchemaTests
{
    private static MsSqliteConnection Migrated()
    {
        var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        TranscriptMigrationRunner.Run(conn);
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

    private record Column(string Name, string Type, bool NotNull, int Pk, string? Default = null);

    private static List<Column> Columns(MsSqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name, type, \"notnull\", pk, dflt_value FROM pragma_table_info('{table}') ORDER BY cid";
        using var r = cmd.ExecuteReader();
        var list = new List<Column>();
        while (r.Read())
            list.Add(new Column(r.GetString(0), r.GetString(1), r.GetInt64(2) == 1, (int)r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4)));
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

    private static long AddSession(MsSqliteConnection conn, string host = "pi", string hostSessionId = "s1", string kind = "interactive")
    {
        Exec(conn,
            "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind, ingested_offset, exchange_count) " +
            $"VALUES ('{host}', '{hostSessionId}', 'owner/repo', 'remote', '{kind}', 0, 0)");
        return Scalar(conn, "SELECT last_insert_rowid()");
    }

    private static long AddChunk(MsSqliteConnection conn, long sessionId, string content, int start = 0, int part = 0, string type = "dialogue")
    {
        Exec(conn,
            "INSERT INTO tx_chunk (session_id, exchange_start, exchange_end, part, chunk_type, ts, redaction_version, content) " +
            $"VALUES ({sessionId}, {start}, {start}, {part}, '{type}', '2026-01-01T00:00:00Z', 1, '{content}')");
        return Scalar(conn, "SELECT last_insert_rowid()");
    }

    private static long FtsHits(MsSqliteConnection conn, string term) =>
        Scalar(conn, $"SELECT COUNT(*) FROM tx_chunk_fts WHERE tx_chunk_fts MATCH '{term}'");

    [Fact]
    public void FreshDatabase_HasExactlyTheSpecObjects()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[]
            {
                "index:idx_tx_chunk_unembedded",
                "index:idx_tx_forgotten_project", "index:idx_tx_forgotten_session", "index:idx_tx_session_project_kind",
                "table:tx_chunk", "table:tx_chunk_fts", "table:tx_chunk_fts_config", "table:tx_chunk_fts_data",
                "table:tx_chunk_fts_docsize", "table:tx_chunk_fts_idx",
                "table:tx_forgotten", "table:tx_schema_version", "table:tx_session", "table:tx_state",
                "trigger:tx_chunk_fts_ad", "trigger:tx_chunk_fts_ai", "trigger:tx_chunk_fts_au",
                "trigger:tx_chunk_reset_au", "trigger:tx_chunk_vec_ad", "trigger:tx_chunk_vec_au",
                "trigger:tx_session_vec_ku",
            },
            SchemaObjects(conn).Where(o => !o.StartsWith("table:tx_chunk_vec_", StringComparison.Ordinal)
                                           && o != "table:tx_chunk_vec").ToList());
        // The vec0 virtual table and its shadow tables (named tx_chunk_vec_*).
        Assert.Contains("table:tx_chunk_vec", SchemaObjects(conn));
        Assert.Contains(SchemaObjects(conn), o => o.StartsWith("table:tx_chunk_vec_", StringComparison.Ordinal));
    }

    [Fact]
    public void TxSession_HasTheSpecColumns()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[]
            {
                new Column("id", "INTEGER", false, 1),
                new Column("host", "TEXT", true, 0), new Column("host_session_id", "TEXT", true, 0),
                new Column("project_key", "TEXT", true, 0), new Column("project_key_source", "TEXT", true, 0),
                new Column("cwd", "TEXT", false, 0), new Column("started_at", "TEXT", false, 0),
                new Column("ended_at", "TEXT", false, 0), new Column("kind", "TEXT", true, 0),
                new Column("title", "TEXT", false, 0), new Column("source_path", "TEXT", false, 0),
                new Column("source_size", "INTEGER", false, 0), new Column("source_mtime", "INTEGER", false, 0),
                new Column("source_fingerprint", "TEXT", false, 0), new Column("ingested_offset", "INTEGER", true, 0, "0"),
                new Column("exchange_count", "INTEGER", true, 0, "0"),
                new Column("settled_user_turns", "INTEGER", true, 0, "0"),
                new Column("built_under_cutoff", "TEXT", false, 0),
                new Column("project_root", "TEXT", false, 0),
            },
            Columns(conn, "tx_session"));
    }

    [Fact]
    public void TxChunk_HasTheSpecColumns()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[]
            {
                new Column("id", "INTEGER", false, 1),
                new Column("session_id", "INTEGER", true, 0),
                new Column("exchange_start", "INTEGER", true, 0), new Column("exchange_end", "INTEGER", true, 0),
                new Column("part", "INTEGER", true, 0, "0"), new Column("chunk_type", "TEXT", true, 0),
                new Column("has_error", "INTEGER", true, 0, "0"), new Column("ts", "TEXT", false, 0),
                new Column("redaction_version", "INTEGER", true, 0), new Column("embedded", "INTEGER", true, 0, "0"),
                new Column("content", "TEXT", true, 0),
                new Column("embed_attempts", "INTEGER", true, 0, "0"),
            },
            Columns(conn, "tx_chunk"));
    }

    [Fact]
    public void TxForgottenStateAndVersion_HaveTheSpecColumns()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[]
            {
                new Column("id", "INTEGER", false, 1), new Column("kind", "TEXT", true, 0),
                new Column("host", "TEXT", false, 0), new Column("host_session_id", "TEXT", false, 0),
                new Column("project_key", "TEXT", false, 0), new Column("roots", "TEXT", false, 0),
                new Column("at", "TEXT", true, 0),
            },
            Columns(conn, "tx_forgotten"));
        Assert.Equal(
            new[] { new Column("key", "TEXT", false, 1), new Column("value", "TEXT", true, 0) },
            Columns(conn, "tx_state"));
        Assert.Equal(
            new[] { new Column("version", "INTEGER", false, 1), new Column("applied_at", "INTEGER", true, 0) },
            Columns(conn, "tx_schema_version"));
    }

    [Fact]
    public void ChunkSessionId_ReferencesSessionWithCascade()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[] { "tx_session|session_id|id|CASCADE" },
            Rows(conn, "SELECT \"table\", \"from\", \"to\", on_delete FROM pragma_foreign_key_list('tx_chunk')"));
    }

    [Fact]
    public void ForgottenIndexes_AreUniqueOnSessionAndProject()
    {
        using var conn = Migrated();

        Assert.Equal(
            new[] { "idx_tx_forgotten_project|1|project_key", "idx_tx_forgotten_session|1|host,host_session_id" },
            Rows(conn,
                "SELECT i.name, i.\"unique\", (SELECT group_concat(name) FROM (SELECT name FROM pragma_index_info(i.name) ORDER BY seqno)) " +
                "FROM pragma_index_list('tx_forgotten') i WHERE i.origin = 'c' ORDER BY i.name"));

        Exec(conn, "INSERT INTO tx_forgotten (kind, host, host_session_id, at) VALUES ('session', 'pi', 's1', 't')");
        Assert.Throws<SqliteException>(() =>
            Exec(conn, "INSERT INTO tx_forgotten (kind, host, host_session_id, at) VALUES ('session', 'pi', 's1', 't')"));
        Exec(conn, "INSERT INTO tx_forgotten (kind, project_key, roots, at) VALUES ('project', 'o/r', '[]', 't')");
        Assert.Throws<SqliteException>(() =>
            Exec(conn, "INSERT INTO tx_forgotten (kind, project_key, roots, at) VALUES ('project', 'o/r', '[]', 't')"));
        var ex = Assert.Throws<SqliteException>(() =>
            Exec(conn, "INSERT INTO tx_forgotten (kind, at) VALUES ('x', 't')"));
        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public void Run_RecordsVersion3_AndIsIdempotent()
    {
        using var conn = Migrated();
        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(conn));
        Assert.True(TranscriptMigrationRunner.IsMigrated(conn));
        var before = SchemaObjects(conn);
        var versions = Rows(conn, "SELECT version, applied_at FROM tx_schema_version");

        TranscriptMigrationRunner.Run(conn);

        Assert.Equal(before, SchemaObjects(conn));
        Assert.Equal(versions, Rows(conn, "SELECT version, applied_at FROM tx_schema_version"));
        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(conn));
    }

    [Fact]
    public void Run_UpgradesAVersion1Archive_KeepsRows()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        TranscriptMigrationRunner.Run(conn, 1);
        Assert.Equal(1, TranscriptMigrationRunner.CurrentVersion(conn));
        var sid = AddSession(conn);
        AddChunk(conn, sid, "upgradekeeps text");

        TranscriptMigrationRunner.Run(conn);

        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(conn));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_session"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk"));
        Assert.Equal(1, FtsHits(conn, "upgradekeeps"));
        Assert.Equal(0, Scalar(conn, "SELECT settled_user_turns FROM tx_session"));
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM tx_session WHERE built_under_cutoff IS NOT NULL OR project_root IS NOT NULL"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'idx_tx_session_project_kind'"));
    }

    [Fact]
    public void CurrentVersion_IsZeroWithoutTheTable_AndRunCreatesNothingElse()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");

        Assert.Equal(0, TranscriptMigrationRunner.CurrentVersion(conn));
        Assert.False(TranscriptMigrationRunner.IsMigrated(conn));
        Assert.Empty(SchemaObjects(conn));
    }

    [Fact]
    public void Run_DoesNotCreateMeta_OnAnEmptyDatabase()
    {
        using var conn = Migrated();

        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('_meta', '_schema_version')"));
    }

    [Fact]
    public void Run_OnAMainSchemaDatabase_LeavesMainVersionAt18_AndMetaUntouched()
    {
        using var conn = TotalRecall.Infrastructure.Storage.SqliteConnection.Open(":memory:");
        MigrationRunner.RunMigrations(conn);
        var meta = Rows(conn, "SELECT key, value FROM _meta ORDER BY key");

        TranscriptMigrationRunner.Run(conn);

        Assert.Equal(18, Scalar(conn, "SELECT COUNT(*) FROM _schema_version"));
        Assert.Equal(18, Scalar(conn, "SELECT MAX(version) FROM _schema_version"));
        Assert.Equal(meta, Rows(conn, "SELECT key, value FROM _meta ORDER BY key"));
        Assert.Equal(3, TranscriptMigrationRunner.CurrentVersion(conn));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("chunk_type")]
    public void CheckConstraints_RejectUnknownValues(string column)
    {
        using var conn = Migrated();

        SqliteException ex;
        if (column == "kind")
        {
            ex = Assert.Throws<SqliteException>(() => AddSession(conn, kind: "x"));
        }
        else
        {
            var sid = AddSession(conn);
            ex = Assert.Throws<SqliteException>(() => AddChunk(conn, sid, "hello", type: "x"));
        }
        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public void CheckConstraints_AcceptTheSpecValues()
    {
        using var conn = Migrated();
        AddSession(conn, hostSessionId: "a", kind: "interactive");
        var sid = AddSession(conn, hostSessionId: "b", kind: "automated");
        AddChunk(conn, sid, "one", type: "dialogue");
        AddChunk(conn, sid, "two", start: 1, type: "compaction-summary");

        Assert.Equal(2, Scalar(conn, "SELECT COUNT(*) FROM tx_session"));
        Assert.Equal(2, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk"));
    }

    [Fact]
    public void ProjectKeySource_IsChecked()
    {
        using var conn = Migrated();

        var ex = Assert.Throws<SqliteException>(() => Exec(conn,
            "INSERT INTO tx_session (host, host_session_id, project_key, project_key_source, kind) VALUES ('pi', 's', 'k', 'x', 'interactive')"));
        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public void Fts_InsertMakesFindable_DeleteRemoves_UpdateReindexes()
    {
        using var conn = Migrated();
        var sid = AddSession(conn);
        var cid = AddChunk(conn, sid, "zebrafinch migration notes");

        Assert.Equal(1, FtsHits(conn, "zebrafinch"));

        Exec(conn, $"UPDATE tx_chunk SET content = 'quokka migration notes' WHERE id = {cid}");
        Assert.Equal(0, FtsHits(conn, "zebrafinch"));
        Assert.Equal(1, FtsHits(conn, "quokka"));

        Exec(conn, $"DELETE FROM tx_chunk WHERE id = {cid}");
        Assert.Equal(0, FtsHits(conn, "quokka"));
    }

    [Fact]
    public void SecureDeleteOption_IsTurnedOn()
    {
        using var conn = Migrated();

        Assert.Equal(1, Scalar(conn, "SELECT CAST(v AS INTEGER) FROM tx_chunk_fts_config WHERE k = 'secure-delete'"));
    }

    [Fact]
    public void UniqueConstraints_AreEnforced()
    {
        using var conn = Migrated();
        var sid = AddSession(conn);
        Assert.Throws<SqliteException>(() => AddSession(conn));

        AddChunk(conn, sid, "a", start: 0, part: 0);
        Assert.Throws<SqliteException>(() => AddChunk(conn, sid, "b", start: 0, part: 0));
        AddChunk(conn, sid, "c", start: 0, part: 1);
        AddChunk(conn, sid, "d", start: 1, part: 0);
        Assert.Equal(3, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk"));
    }

    [Fact]
    public void ChunkWithUnknownSession_IsRejected()
    {
        using var conn = Migrated();

        Assert.Throws<SqliteException>(() => AddChunk(conn, 999, "orphan"));
    }

    [Fact]
    public void DeletingASession_CascadesToChunks_AndTheFtsDeleteTriggerFires()
    {
        using var conn = Migrated();
        var sid = AddSession(conn);
        AddChunk(conn, sid, "narwhalcascade one");
        AddChunk(conn, sid, "narwhalcascade two", start: 1);
        Assert.Equal(2, FtsHits(conn, "narwhalcascade"));

        Exec(conn, $"DELETE FROM tx_session WHERE id = {sid}");

        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk"));
        // Recorded answer to the spec's open question: the external-content delete trigger fires
        // for rows removed through ON DELETE CASCADE, so the index drops their terms.
        Assert.Equal(0, FtsHits(conn, "narwhalcascade"));
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM tx_chunk_fts_docsize"));
    }
}
