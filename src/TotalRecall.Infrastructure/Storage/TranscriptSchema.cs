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
public static class TranscriptMigrationRunner
{
    private delegate void Migration(MsSqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx);

    private static readonly Migration[] Migrations =
    {
        // Migration 1: sessions, chunks, text index, exclusion records, state
        Migration1_Archive,
    };

    private const string VersionDdl = """
        CREATE TABLE IF NOT EXISTS tx_schema_version (
            version    INTEGER PRIMARY KEY,
            applied_at INTEGER NOT NULL
        )
        """;

    /// <summary>Apply any pending archive migrations to <paramref name="conn"/> in one transaction.</summary>
    public static void Run(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);

        using var tx = conn.BeginTransaction();

        Exec(conn, tx, VersionDdl);
        var starting = (int)ReadVersion(conn, tx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var i = starting; i < Migrations.Length; i++)
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
