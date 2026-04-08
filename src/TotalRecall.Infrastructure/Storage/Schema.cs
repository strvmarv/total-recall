using System;
using System.Collections.Generic;
using TotalRecall.Core;
using MsSqliteConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Schema migration runner. Ports <c>src-ts/db/schema.ts</c> — a 3-migration
/// initial schema consisting of:
///   1. Content tables (hot/warm/cold × memory/knowledge = 6) + vec0 virtual
///      tables + per-table indexes + system tables + system indexes.
///   2. <c>_meta</c> key-value store + <c>benchmark_candidates</c>.
///   3. FTS5 virtual tables + sync triggers + backfill.
///
/// Version tracking lives in the <c>_schema_version</c> table. Running
/// <see cref="RunMigrations"/> twice is idempotent: already-applied migrations
/// are skipped based on <c>MAX(version)</c>.
///
/// WAL and <c>foreign_keys</c> pragmas are deliberately NOT applied here —
/// <see cref="SqliteConnection.Open"/> already sets them and SQLite disallows
/// changing them inside a transaction anyway.
/// </summary>
public static class MigrationRunner
{
    /// <summary>
    /// Returns the canonical content-table name for the given
    /// <see cref="Tier"/> / <see cref="ContentType"/> pair. Mirrors
    /// <c>tableName()</c> in <c>src-ts/types.ts</c>.
    /// </summary>
    internal static string TableName(Tier tier, ContentType type)
    {
        string tierStr;
        if (tier.IsHot) tierStr = "hot";
        else if (tier.IsWarm) tierStr = "warm";
        else if (tier.IsCold) tierStr = "cold";
        else throw new ArgumentOutOfRangeException(nameof(tier));

        string typeStr;
        if (type.IsMemory) typeStr = "memories";
        else if (type.IsKnowledge) typeStr = "knowledge";
        else throw new ArgumentOutOfRangeException(nameof(type));

        return $"{tierStr}_{typeStr}";
    }

    internal static string VecTableName(Tier tier, ContentType type) =>
        $"{TableName(tier, type)}_vec";

    internal static string FtsTableName(Tier tier, ContentType type) =>
        $"{TableName(tier, type)}_fts";

    /// <summary>All 6 (tier, type) pairs the schema creates tables for.</summary>
    internal static readonly (Tier Tier, ContentType Type)[] AllTablePairs =
    {
        (Tier.Hot,  ContentType.Memory),
        (Tier.Warm, ContentType.Memory),
        (Tier.Cold, ContentType.Memory),
        (Tier.Hot,  ContentType.Knowledge),
        (Tier.Warm, ContentType.Knowledge),
        (Tier.Cold, ContentType.Knowledge),
    };

    private const string SchemaVersionDdl = """
        CREATE TABLE IF NOT EXISTS _schema_version (
            version    INTEGER NOT NULL,
            applied_at INTEGER NOT NULL
        )
        """;

    private static string ContentTableDdl(string name) => $$"""
        CREATE TABLE IF NOT EXISTS {{name}} (
            id                TEXT PRIMARY KEY NOT NULL,
            content           TEXT NOT NULL,
            summary           TEXT,
            source            TEXT,
            source_tool       TEXT,
            project           TEXT,
            tags              TEXT DEFAULT '[]',
            created_at        INTEGER NOT NULL,
            updated_at        INTEGER NOT NULL,
            last_accessed_at  INTEGER NOT NULL,
            access_count      INTEGER DEFAULT 0,
            decay_score       REAL DEFAULT 1.0,
            parent_id         TEXT,
            collection_id     TEXT,
            metadata          TEXT DEFAULT '{}'
        )
        """;

    private static string[] ContentTableIndexes(string name) => new[]
    {
        $"CREATE INDEX IF NOT EXISTS idx_{name}_project       ON {name}(project)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_decay_score   ON {name}(decay_score)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_last_accessed ON {name}(last_accessed_at)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_parent_id     ON {name}(parent_id)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_collection_id ON {name}(collection_id)",
    };

    private static readonly string[] SystemTableDdls =
    {
        """
        CREATE TABLE IF NOT EXISTS retrieval_events (
            id                       TEXT PRIMARY KEY NOT NULL,
            timestamp                INTEGER NOT NULL,
            session_id               TEXT NOT NULL,
            query_text               TEXT NOT NULL,
            query_source             TEXT NOT NULL,
            query_embedding          BLOB,
            results                  TEXT NOT NULL DEFAULT '[]',
            result_count             INTEGER NOT NULL DEFAULT 0,
            top_score                REAL,
            top_tier                 TEXT,
            top_content_type         TEXT,
            outcome_used             INTEGER,
            outcome_signal           TEXT,
            config_snapshot_id       TEXT NOT NULL,
            latency_ms               INTEGER,
            tiers_searched           TEXT NOT NULL DEFAULT '[]',
            total_candidates_scanned INTEGER
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS compaction_log (
            id                  TEXT PRIMARY KEY NOT NULL,
            timestamp           INTEGER NOT NULL,
            session_id          TEXT,
            source_tier         TEXT NOT NULL,
            target_tier         TEXT,
            source_entry_ids    TEXT NOT NULL DEFAULT '[]',
            target_entry_id     TEXT,
            semantic_drift      REAL,
            facts_preserved     INTEGER,
            facts_in_original   INTEGER,
            preservation_ratio  REAL,
            decay_scores        TEXT NOT NULL DEFAULT '[]',
            reason              TEXT NOT NULL,
            config_snapshot_id  TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS config_snapshots (
            id        TEXT PRIMARY KEY NOT NULL,
            name      TEXT,
            timestamp INTEGER NOT NULL,
            config    TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS import_log (
            id              TEXT PRIMARY KEY NOT NULL,
            timestamp       INTEGER NOT NULL,
            source_tool     TEXT NOT NULL,
            source_path     TEXT NOT NULL,
            content_hash    TEXT NOT NULL,
            target_entry_id TEXT NOT NULL,
            target_tier     TEXT NOT NULL,
            target_type     TEXT NOT NULL
        )
        """,
    };

    private static readonly string[] SystemTableIndexes =
    {
        "CREATE INDEX IF NOT EXISTS idx_retrieval_events_timestamp  ON retrieval_events(timestamp)",
        "CREATE INDEX IF NOT EXISTS idx_retrieval_events_session_id ON retrieval_events(session_id)",
        "CREATE INDEX IF NOT EXISTS idx_compaction_log_timestamp    ON compaction_log(timestamp)",
        "CREATE INDEX IF NOT EXISTS idx_compaction_log_source_tier  ON compaction_log(source_tier)",
        "CREATE INDEX IF NOT EXISTS idx_import_log_content_hash     ON import_log(content_hash)",
        "CREATE INDEX IF NOT EXISTS idx_import_log_source_tool      ON import_log(source_tool)",
    };

    /// <summary>
    /// Ordered list of migrations. Each entry is run inside the outer
    /// transaction opened by <see cref="RunMigrations"/>. To add a new
    /// migration, append to this list — never reorder or remove.
    /// </summary>
    private static readonly Action<MsSqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction>[] Migrations =
    {
        // Migration 1: content tables + vec0 + system tables + indexes
        Migration1_InitialSchema,
        // Migration 2: _meta + benchmark_candidates
        Migration2_MetaAndBenchmark,
        // Migration 3: FTS5 virtual tables + triggers + backfill
        Migration3_Fts5,
    };

    /// <summary>
    /// Apply any pending migrations to <paramref name="conn"/>. Idempotent:
    /// safe to call repeatedly on an already-migrated database.
    /// </summary>
    public static void RunMigrations(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);

        using var tx = conn.BeginTransaction();

        Exec(conn, tx, SchemaVersionDdl);
        var current = GetCurrentVersion(conn, tx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var i = current; i < Migrations.Length; i++)
        {
            Migrations[i](conn, tx);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO _schema_version (version, applied_at) VALUES ($v, $t)";
            cmd.Parameters.AddWithValue("$v", i + 1);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// Caller MUST ensure <c>_schema_version</c> exists first (see <see cref="RunMigrations"/>).
    private static int GetCurrentVersion(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM _schema_version";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    // --- migrations -------------------------------------------------------

    private static void Migration1_InitialSchema(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        foreach (var (tier, type) in AllTablePairs)
        {
            var tbl = TableName(tier, type);
            var vecTbl = VecTableName(tier, type);

            Exec(conn, tx, ContentTableDdl(tbl));
            Exec(conn, tx,
                $"CREATE VIRTUAL TABLE IF NOT EXISTS {vecTbl} USING vec0(embedding float[384])");

            foreach (var idx in ContentTableIndexes(tbl))
                Exec(conn, tx, idx);
        }

        foreach (var ddl in SystemTableDdls)
            Exec(conn, tx, ddl);

        foreach (var idx in SystemTableIndexes)
            Exec(conn, tx, idx);
    }

    private static void Migration2_MetaAndBenchmark(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS _meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """);

        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS benchmark_candidates (
                id                  TEXT PRIMARY KEY,
                query_text          TEXT NOT NULL UNIQUE,
                top_score           REAL NOT NULL,
                top_result_content  TEXT,
                top_result_entry_id TEXT,
                first_seen          INTEGER NOT NULL,
                last_seen           INTEGER NOT NULL,
                times_seen          INTEGER DEFAULT 1,
                status              TEXT DEFAULT 'pending'
            )
            """);

        Exec(conn, tx,
            "CREATE INDEX IF NOT EXISTS idx_benchmark_candidates_status ON benchmark_candidates(status)");
    }

    private static void Migration3_Fts5(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        foreach (var (tier, type) in AllTablePairs)
        {
            var tbl = TableName(tier, type);
            var ftsTbl = FtsTableName(tier, type);

            Exec(conn, tx,
                $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTbl} USING fts5(content, tags, content={tbl}, content_rowid=rowid)");

            Exec(conn, tx, $$"""
                CREATE TRIGGER IF NOT EXISTS {{tbl}}_fts_ai AFTER INSERT ON {{tbl}} BEGIN
                    INSERT INTO {{ftsTbl}}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
                END
                """);

            Exec(conn, tx, $$"""
                CREATE TRIGGER IF NOT EXISTS {{tbl}}_fts_ad AFTER DELETE ON {{tbl}} BEGIN
                    INSERT INTO {{ftsTbl}}({{ftsTbl}}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
                END
                """);

            Exec(conn, tx, $$"""
                CREATE TRIGGER IF NOT EXISTS {{tbl}}_fts_au AFTER UPDATE ON {{tbl}} BEGIN
                    INSERT INTO {{ftsTbl}}({{ftsTbl}}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
                    INSERT INTO {{ftsTbl}}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
                END
                """);

            // Backfill any existing rows (no-op on a fresh DB).
            Exec(conn, tx,
                $"INSERT INTO {ftsTbl}(rowid, content, tags) SELECT rowid, content, tags FROM {tbl}");
        }
    }

    // --- helpers ----------------------------------------------------------

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
