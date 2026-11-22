using System;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Migration runner for the opt-in transcript archive database. It has its own
/// <c>tx_schema_version</c> table and never touches <see cref="MigrationRunner"/>'s
/// <c>_schema_version</c> or <c>_meta</c>, so it can run on a connection that also holds
/// the main schema without changing it. Idempotent: entries at or below
/// <c>MAX(version)</c> are skipped.
/// </summary>
internal static class TranscriptMigrationRunner
{
    private delegate void Migration(MsSqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx);

    private static readonly Migration[] Migrations =
    {
        // Migration 1: sessions, chunks, text index, exclusion records, state
        Migration1_Archive,
        // Migration 2: ingest state (settled turns, cutoff the row was built under, project root) and the status index
        Migration2_IngestState,
        // Migration 3: the vector table, the triggers that keep it in step, the embed attempt count
        Migration3_Vectors,
    };

    private const string VersionDdl = """
        CREATE TABLE IF NOT EXISTS tx_schema_version (
            version    INTEGER PRIMARY KEY,
            applied_at INTEGER NOT NULL
        )
        """;

    /// <summary>Apply any pending archive migrations to <paramref name="conn"/> in one transaction.</summary>
    public static void Run(MsSqliteConnection conn) => Run(conn, Migrations.Length);

    /// <summary>Apply pending archive migrations up to and including <paramref name="targetVersion"/> (tests build an older archive with it).</summary>
    internal static void Run(MsSqliteConnection conn, int targetVersion)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetVersion, Migrations.Length);

        // Read the version first, outside any transaction: an archive that is already current must not take the
        // write lock (BEGIN IMMEDIATE would queue every search and status behind an ingest). The version table may
        // not exist yet, so CurrentVersion probes for it and returns 0.
        if (CurrentVersion(conn) >= targetVersion) return;

        using var tx = conn.BeginTransaction();

        Exec(conn, tx, VersionDdl);
        // Re-read under the write lock: another process may have migrated between the check above and BEGIN.
        var starting = (int)ReadVersion(conn, tx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var i = starting; i < targetVersion; i++)
        {
            Migrations[i](conn, tx);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO tx_schema_version (version, applied_at) VALUES ($v, $t)";
            cmd.Parameters.AddWithValue("$v", i + 1);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>The highest applied archive migration, or 0 when <c>tx_schema_version</c> does not exist.</summary>
    public static long CurrentVersion(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);

        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'tx_schema_version'";
            if (probe.ExecuteScalar() is null) return 0;
        }
        return ReadVersion(conn, null);
    }

    /// <summary>True when migration 1 (the archive schema) has been applied.</summary>
    public static bool IsMigrated(MsSqliteConnection conn) => CurrentVersion(conn) >= 1;

    private static long ReadVersion(MsSqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM tx_schema_version";
        return cmd.ExecuteScalar() is long l ? l : 0;
    }

    private static void Migration1_Archive(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS tx_session (
                id                 INTEGER PRIMARY KEY,
                host               TEXT NOT NULL,
                host_session_id    TEXT NOT NULL,
                project_key        TEXT NOT NULL,
                project_key_source TEXT NOT NULL CHECK (project_key_source IN ('remote', 'path')),
                cwd                TEXT,
                started_at         TEXT,
                ended_at           TEXT,
                kind               TEXT NOT NULL CHECK (kind IN ('interactive', 'automated')),
                title              TEXT,
                source_path        TEXT,
                source_size        INTEGER,
                source_mtime       INTEGER,
                source_fingerprint TEXT,
                ingested_offset    INTEGER NOT NULL DEFAULT 0,
                exchange_count     INTEGER NOT NULL DEFAULT 0,
                UNIQUE (host, host_session_id)
            )
            """);

        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS tx_chunk (
                id                INTEGER PRIMARY KEY,
                session_id        INTEGER NOT NULL REFERENCES tx_session(id) ON DELETE CASCADE,
                exchange_start    INTEGER NOT NULL,
                exchange_end      INTEGER NOT NULL,
                part              INTEGER NOT NULL DEFAULT 0,
                chunk_type        TEXT NOT NULL CHECK (chunk_type IN ('dialogue', 'compaction-summary')),
                has_error         INTEGER NOT NULL DEFAULT 0,
                ts                TEXT,
                redaction_version INTEGER NOT NULL,
                embedded          INTEGER NOT NULL DEFAULT 0,
                content           TEXT NOT NULL,
                UNIQUE (session_id, exchange_start, part)
            )
            """);

        Exec(conn, tx,
            "CREATE VIRTUAL TABLE IF NOT EXISTS tx_chunk_fts USING fts5(content, content=tx_chunk, content_rowid=id)");

        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_chunk_fts_ai AFTER INSERT ON tx_chunk BEGIN
                INSERT INTO tx_chunk_fts(rowid, content) VALUES (new.id, new.content);
            END
            """);

        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_chunk_fts_ad AFTER DELETE ON tx_chunk BEGIN
                INSERT INTO tx_chunk_fts(tx_chunk_fts, rowid, content) VALUES('delete', old.id, old.content);
            END
            """);

        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_chunk_fts_au AFTER UPDATE ON tx_chunk BEGIN
                INSERT INTO tx_chunk_fts(tx_chunk_fts, rowid, content) VALUES('delete', old.id, old.content);
                INSERT INTO tx_chunk_fts(rowid, content) VALUES (new.id, new.content);
            END
            """);

        // Persistent FTS5 index option (SQLite 3.42+): a rejection fails the migration on purpose.
        Exec(conn, tx, "INSERT INTO tx_chunk_fts(tx_chunk_fts, rank) VALUES('secure-delete', 1)");

        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS tx_forgotten (
                id              INTEGER PRIMARY KEY,
                kind            TEXT NOT NULL CHECK (kind IN ('session', 'project')),
                host            TEXT,
                host_session_id TEXT,
                project_key     TEXT,
                roots           TEXT,
                at              TEXT NOT NULL
            )
            """);

        Exec(conn, tx,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_tx_forgotten_session ON tx_forgotten(host, host_session_id)");
        Exec(conn, tx,
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_tx_forgotten_project ON tx_forgotten(project_key)");

        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS tx_state (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);
    }

    private static void Migration2_IngestState(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        Exec(conn, tx, "ALTER TABLE tx_session ADD COLUMN settled_user_turns INTEGER NOT NULL DEFAULT 0");
        Exec(conn, tx, "ALTER TABLE tx_session ADD COLUMN built_under_cutoff TEXT");
        Exec(conn, tx, "ALTER TABLE tx_session ADD COLUMN project_root TEXT");
        Exec(conn, tx,
            "CREATE INDEX IF NOT EXISTS idx_tx_session_project_kind ON tx_session(project_key, kind)");
    }

    private static void Migration3_Vectors(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        // The vector row of a chunk (rowid = tx_chunk.id) exists only while tx_chunk.embedded = 1. vec0 is loaded by
        // every archive connection (SqliteConnection.Open); a connection without it cannot write tx_chunk or tx_session.
        Exec(conn, tx, """
            CREATE VIRTUAL TABLE IF NOT EXISTS tx_chunk_vec USING vec0(
                session_id integer,
                project text,
                kind text,
                ts integer,
                embedding float[384] distance_metric=cosine
            )
            """);

        Exec(conn, tx, "ALTER TABLE tx_chunk ADD COLUMN embed_attempts INTEGER NOT NULL DEFAULT 0");

        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_chunk_vec_ad AFTER DELETE ON tx_chunk BEGIN
                DELETE FROM tx_chunk_vec WHERE rowid = old.id;
            END
            """);

        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_chunk_vec_au AFTER UPDATE OF content ON tx_chunk BEGIN
                DELETE FROM tx_chunk_vec WHERE rowid = old.id;
            END
            """);

        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_session_vec_ku AFTER UPDATE OF kind, project_key ON tx_session
            WHEN old.kind IS NOT new.kind OR old.project_key IS NOT new.project_key BEGIN
                UPDATE tx_chunk_vec SET kind = new.kind, project = new.project_key
                WHERE rowid IN (SELECT id FROM tx_chunk WHERE session_id = new.id);
            END
            """);

        // Narrow the text-index update trigger to content changes: marking a chunk embedded must not delete and
        // re-insert its text-index entry. Same body as migration 1.
        Exec(conn, tx, "DROP TRIGGER IF EXISTS tx_chunk_fts_au");
        Exec(conn, tx, """
            CREATE TRIGGER tx_chunk_fts_au AFTER UPDATE OF content ON tx_chunk BEGIN
                INSERT INTO tx_chunk_fts(tx_chunk_fts, rowid, content) VALUES('delete', old.id, old.content);
                INSERT INTO tx_chunk_fts(rowid, content) VALUES (new.id, new.content);
            END
            """);

        // A changed chunk gets a fresh embed state. The inner UPDATE does not set content, so it cannot fire again.
        Exec(conn, tx, """
            CREATE TRIGGER IF NOT EXISTS tx_chunk_reset_au AFTER UPDATE OF content ON tx_chunk BEGIN
                UPDATE tx_chunk SET embedded = 0, embed_attempts = 0 WHERE id = new.id;
            END
            """);

        Exec(conn, tx, """
            CREATE INDEX IF NOT EXISTS idx_tx_chunk_unembedded ON tx_chunk(id)
            WHERE embedded = 0 AND embed_attempts < 3
            """);

        // No vector exists yet, so an embedded flag of 1 (written by the earlier parts' tests) is wrong.
        Exec(conn, tx, "UPDATE tx_chunk SET embedded = 0 WHERE embedded <> 0");
    }

    private static void Exec(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
