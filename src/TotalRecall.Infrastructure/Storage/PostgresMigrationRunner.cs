using System;
using Npgsql;

namespace TotalRecall.Infrastructure.Storage;

/// <summary>
/// Schema migration runner for Postgres/pgvector. Creates a two-table layout
/// (<c>memories</c> and <c>knowledge</c>) where tier is a column rather than
/// part of the table name, unlike the SQLite six-table layout in
/// <see cref="MigrationRunner"/>.
///
/// Each content table has: all content columns, <c>owner_id</c>,
/// <c>visibility</c>, <c>internal_key BIGSERIAL</c>,
/// <c>embedding vector({dimensions})</c>, and a generated <c>fts tsvector</c>
/// column. System tables mirror the SQLite schema.
///
/// Running <see cref="RunMigrations"/> is idempotent: if
/// <c>_schema_version</c> already contains version 1 the method returns
/// immediately without executing any DDL.
/// </summary>
public static class PostgresMigrationRunner
{
    private const int SchemaVersion = 2;

    private const string SchemaVersionDdl = """
        CREATE TABLE IF NOT EXISTS _schema_version (
            version    INTEGER NOT NULL,
            applied_at BIGINT  NOT NULL
        )
        """;

    /// <summary>
    /// The two content table names in the Postgres schema.
    /// </summary>
    internal static readonly string[] ContentTableNames = { "memories", "knowledge" };

    private static string ContentTableDdl(string name, int dimensions) => $$"""
        CREATE TABLE IF NOT EXISTS {{name}} (
            id                TEXT    PRIMARY KEY NOT NULL,
            tier              TEXT    NOT NULL,
            content           TEXT    NOT NULL,
            summary           TEXT,
            source            TEXT,
            source_tool       TEXT,
            project           TEXT,
            tags              JSONB   NOT NULL DEFAULT '[]'::jsonb,
            created_at        BIGINT  NOT NULL,
            updated_at        BIGINT  NOT NULL,
            last_accessed_at  BIGINT  NOT NULL,
            access_count      INTEGER NOT NULL DEFAULT 0,
            decay_score       REAL    NOT NULL DEFAULT 1.0,
            parent_id         TEXT,
            collection_id     TEXT,
            metadata          JSONB   NOT NULL DEFAULT '{}'::jsonb,
            entry_type        TEXT    NOT NULL DEFAULT 'Preference',
            owner_id          TEXT    NOT NULL DEFAULT 'local',
            visibility        TEXT    NOT NULL DEFAULT 'private',
            internal_key      BIGSERIAL NOT NULL UNIQUE,
            embedding         vector({{dimensions}})
        )
        """;

    /// <summary>
    /// ALTER-column DDL for existing tables upgraded from SchemaVersion 1 to 2.
    /// Adds the <c>entry_type</c> column required by the outbound Cortex sync
    /// DTO (Phase 1 of the cortex-sync bug hunt). Pre-v2 rows default to
    /// <c>'Preference'</c> so historical data keeps a valid value.
    /// </summary>
    private static string EntryTypeColumnDdl(string name) => $$"""
        ALTER TABLE {{name}}
            ADD COLUMN IF NOT EXISTS entry_type TEXT NOT NULL DEFAULT 'Preference'
        """;

    private static string FtsColumnDdl(string name) => $$"""
        ALTER TABLE {{name}} ADD COLUMN IF NOT EXISTS fts tsvector
            GENERATED ALWAYS AS (
                to_tsvector('english', coalesce(content, '') || ' ' || coalesce(tags::text, ''))
            ) STORED
        """;

    private static string[] ContentTableIndexes(string name) =>
    [
        $"CREATE INDEX IF NOT EXISTS idx_{name}_tier       ON {name}(tier)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_owner      ON {name}(owner_id)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_visibility ON {name}(owner_id, visibility)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_project    ON {name}(project)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_decay      ON {name}(decay_score)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_collection ON {name}(collection_id)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_parent     ON {name}(parent_id)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_fts        ON {name} USING GIN(fts)",
        $"CREATE INDEX IF NOT EXISTS idx_{name}_embedding  ON {name} USING hnsw (embedding vector_cosine_ops)",
    ];

    private static readonly string[] SystemTableDdls =
    [
        """
        CREATE TABLE IF NOT EXISTS _meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS compaction_log (
            id                  TEXT   PRIMARY KEY NOT NULL,
            timestamp           BIGINT NOT NULL,
            session_id          TEXT,
            source_tier         TEXT   NOT NULL,
            source              TEXT   NOT NULL DEFAULT 'compaction',
            target_tier         TEXT,
            source_entry_ids    JSONB  NOT NULL DEFAULT '[]'::jsonb,
            target_entry_id     TEXT,
            semantic_drift      REAL,
            facts_preserved     INTEGER,
            facts_in_original   INTEGER,
            preservation_ratio  REAL,
            decay_scores        JSONB  NOT NULL DEFAULT '[]'::jsonb,
            reason              TEXT   NOT NULL,
            config_snapshot_id  TEXT   NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS retrieval_events (
            id                       TEXT   PRIMARY KEY NOT NULL,
            timestamp                BIGINT NOT NULL,
            session_id               TEXT   NOT NULL,
            query_text               TEXT   NOT NULL,
            query_source             TEXT   NOT NULL,
            query_embedding          BYTEA,
            results                  JSONB  NOT NULL DEFAULT '[]'::jsonb,
            result_count             INTEGER NOT NULL DEFAULT 0,
            top_score                REAL,
            top_tier                 TEXT,
            top_content_type         TEXT,
            outcome_used             BOOLEAN,
            outcome_signal           TEXT,
            config_snapshot_id       TEXT   NOT NULL,
            latency_ms               INTEGER,
            tiers_searched           JSONB  NOT NULL DEFAULT '[]'::jsonb,
            total_candidates_scanned INTEGER
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS config_snapshots (
            id        TEXT   PRIMARY KEY NOT NULL,
            name      TEXT,
            timestamp BIGINT NOT NULL,
            config    TEXT   NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS import_log (
            id              TEXT   PRIMARY KEY NOT NULL,
            timestamp       BIGINT NOT NULL,
            source_tool     TEXT   NOT NULL,
            source_path     TEXT   NOT NULL,
            content_hash    TEXT   NOT NULL,
            target_entry_id TEXT   NOT NULL,
            target_tier     TEXT   NOT NULL,
            target_type     TEXT   NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS benchmark_candidates (
            id                  TEXT   PRIMARY KEY NOT NULL,
            query_text          TEXT   NOT NULL UNIQUE,
            top_score           REAL   NOT NULL,
            top_result_content  TEXT,
            top_result_entry_id TEXT,
            first_seen          BIGINT NOT NULL,
            last_seen           BIGINT NOT NULL,
            times_seen          INTEGER NOT NULL DEFAULT 1,
            status              TEXT    NOT NULL DEFAULT 'pending'
        )
        """,
    ];

    private static readonly string[] SystemTableIndexes =
    [
        "CREATE INDEX IF NOT EXISTS idx_retrieval_events_timestamp  ON retrieval_events(timestamp)",
        "CREATE INDEX IF NOT EXISTS idx_retrieval_events_session_id ON retrieval_events(session_id)",
        "CREATE INDEX IF NOT EXISTS idx_compaction_log_timestamp    ON compaction_log(timestamp)",
        "CREATE INDEX IF NOT EXISTS idx_compaction_log_source_tier  ON compaction_log(source_tier)",
        "CREATE INDEX IF NOT EXISTS idx_import_log_content_hash     ON import_log(content_hash)",
        "CREATE INDEX IF NOT EXISTS idx_import_log_source_tool      ON import_log(source_tool)",
        "CREATE INDEX IF NOT EXISTS idx_benchmark_candidates_status ON benchmark_candidates(status)",
    ];

    /// <summary>
    /// Apply the Postgres schema to the database reachable via
    /// <paramref name="dataSource"/>. Idempotent: safe to call on an
    /// already-migrated database.
    /// </summary>
    /// <param name="dataSource">Open Npgsql data source.</param>
    /// <param name="dimensions">
    /// Embedding vector dimensionality (e.g. 384 for all-MiniLM-L6-v2).
    /// Must be &gt; 0 and must match the model in use.
    /// </param>
    public static void RunMigrations(NpgsqlDataSource dataSource, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions,
                "Embedding dimensions must be greater than zero.");

        using var conn = dataSource.OpenConnection();

        // Ensure version-tracking table exists outside the main transaction so
        // the idempotency check is always safe.
        Exec(conn, null, SchemaVersionDdl);

        var currentVersion = GetCurrentVersion(conn);
        if (currentVersion >= SchemaVersion)
            return;

        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (currentVersion < 1)
        {
            // 1. Enable pgvector extension.
            Exec(conn, tx, "CREATE EXTENSION IF NOT EXISTS vector");

            // 2 & 3. Create content tables + FTS column + indexes.
            foreach (var name in ContentTableNames)
            {
                Exec(conn, tx, ContentTableDdl(name, dimensions));
                Exec(conn, tx, FtsColumnDdl(name));

                foreach (var idx in ContentTableIndexes(name))
                    Exec(conn, tx, idx);
            }

            // 4. Create system tables.
            foreach (var ddl in SystemTableDdls)
                Exec(conn, tx, ddl);

            // 5. Create system table indexes.
            foreach (var idx in SystemTableIndexes)
                Exec(conn, tx, idx);

            RecordSchemaVersion(conn, tx, 1, now);
        }

        if (currentVersion < 2)
        {
            // v2: add entry_type column to content tables. Guarded with IF NOT
            // EXISTS so a v1→v2 upgrade AND a fresh install (where the CREATE
            // TABLE in the v1 block already included the column) both work.
            foreach (var name in ContentTableNames)
                Exec(conn, tx, EntryTypeColumnDdl(name));

            RecordSchemaVersion(conn, tx, 2, now);
        }

        tx.Commit();
    }

    private static void RecordSchemaVersion(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        int version,
        long appliedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO _schema_version (version, applied_at) VALUES ($v, $t)";
        cmd.Parameters.AddWithValue("$v", version);
        cmd.Parameters.AddWithValue("$t", appliedAt);
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int GetCurrentVersion(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM _schema_version";
        var result = cmd.ExecuteScalar();
        return result is int i ? i : result is long l ? (int)l : 0;
    }

    private static void Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
