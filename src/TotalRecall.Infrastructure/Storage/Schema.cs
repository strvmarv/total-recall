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
    /// Fingerprint written to <c>_meta</c> on fresh-init schema migration so
    /// <see cref="AutoMigrationGuard"/> can distinguish a brand-new .NET-native
    /// DB from an unmigrated legacy TS DB (both have identical schemas — the
    /// marker is the only positive signal). See Plan 7 Task 7.-1.
    /// </summary>
    public const string MigrationCompleteMarkerKey = "migration_from_ts_complete";

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
        // Migration 4: compaction_log.source column (history tagging)
        Migration4_CompactionLogSource,
        // Migration 5: orphan vec/content row cleanup (0.6.7 hot-fix)
        Migration5_CleanupOrphans,
        // Migration 6: usage telemetry tables (usage_events, usage_daily, usage_watermarks)
        Migration6_UsageTelemetry,
        // Migration 7: persistent sync queue for outbound Cortex buffering
        Migration7_SyncQueue,
        // Migration 8: first-class scope column on all content tables
        Migration8_Scope,
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
        var startingVersion = GetCurrentVersion(conn, tx);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (var i = startingVersion; i < Migrations.Length; i++)
        {
            Migrations[i](conn, tx);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO _schema_version (version, applied_at) VALUES ($v, $t)";
            cmd.Parameters.AddWithValue("$v", i + 1);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        // Fresh-init fingerprint. When this call created the schema from nothing
        // (startingVersion == 0), stamp _meta with the migration-complete marker
        // so AutoMigrationGuard can distinguish this brand-new .NET DB from an
        // unmigrated TS DB on subsequent startups. Without this, the guard would
        // false-positive on fresh .NET installs. DBs being upgraded from an
        // earlier .NET schema version are left untouched.
        if (startingVersion == 0)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT INTO _meta (key, value) VALUES ($k, $v) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", MigrationCompleteMarkerKey);
            cmd.Parameters.AddWithValue(
                "$v",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Migration 4 — add a <c>source</c> column to <c>compaction_log</c> so
    /// the history log can distinguish compaction-driven tier movements
    /// (source = <c>'compaction'</c>) from manual ones (<c>'api'</c>,
    /// <c>'import'</c>, etc.). The column is <c>NOT NULL DEFAULT
    /// 'compaction'</c> so pre-v4 rows are backfilled to the semantically
    /// correct value automatically and existing insert sites that haven't
    /// been updated to pass a source keep producing valid rows.
    /// </summary>
    private static void Migration4_CompactionLogSource(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        Exec(conn, tx,
            "ALTER TABLE compaction_log ADD COLUMN source TEXT NOT NULL DEFAULT 'compaction'");
    }

    /// <summary>
    /// Migration 5 — delete orphan rows across all 6 content/vec table
    /// pairs. One-time hot-fix for DBs built under 0.6.7 and earlier,
    /// where <c>MemoryDeleteHandler</c> called <c>store.Delete</c> before
    /// <c>vec.DeleteEmbedding</c>, producing orphan vec rows, and the
    /// un-transacted <c>memory_store</c> path occasionally produced
    /// orphan content rows when the follow-up vec insert failed. The
    /// corresponding handler bug is fixed in the same release; this
    /// migration cleans up the historical debris left behind.
    /// </summary>
    private static void Migration5_CleanupOrphans(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        CleanupOrphanRowsInTransaction(conn, tx);
    }

    /// <summary>
    /// Delete orphan rows from every <c>(tier, type)</c> content/vec
    /// table pair. An orphan is a vec row whose rowid has no matching
    /// content row, or a content row whose rowid has no matching vec
    /// row. Both directions are cleaned in a single transaction. Safe
    /// to call repeatedly; idempotent. Exposed publicly so maintenance
    /// tooling (and tests) can invoke the sweep directly without
    /// manipulating <c>_schema_version</c>.
    /// </summary>
    public static void CleanupOrphanRows(MsSqliteConnection conn)
    {
        ArgumentNullException.ThrowIfNull(conn);
        using var tx = conn.BeginTransaction();
        CleanupOrphanRowsInTransaction(conn, tx);
        tx.Commit();
    }

    private static void CleanupOrphanRowsInTransaction(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        foreach (var (tier, type) in AllTablePairs)
        {
            var contentTable = TableName(tier, type);
            var vecTable = VecTableName(tier, type);

            // Orphan vec rows: rowids present in vec but not in content.
            Exec(conn, tx,
                $"DELETE FROM {vecTable} " +
                $"WHERE rowid NOT IN (SELECT rowid FROM {contentTable})");

            // Orphan content rows: rowids present in content but not in vec.
            Exec(conn, tx,
                $"DELETE FROM {contentTable} " +
                $"WHERE rowid NOT IN (SELECT rowid FROM {vecTable})");
        }
    }

    private static void Migration6_UsageTelemetry(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        // Raw event log — one row per assistant turn. 30-day retention
        // enforced by UsageDailyRollup. See spec §4.1.
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS usage_events (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                host                    TEXT NOT NULL,
                host_event_id           TEXT NOT NULL,
                session_id              TEXT NOT NULL,
                interaction_id          TEXT,
                turn_index              INTEGER,
                ts                      INTEGER NOT NULL,
                project_path            TEXT,
                project_repo            TEXT,
                project_branch          TEXT,
                project_commit          TEXT,
                model                   TEXT,
                input_tokens            INTEGER,
                cache_creation_5m       INTEGER,
                cache_creation_1h       INTEGER,
                cache_read              INTEGER,
                output_tokens           INTEGER,
                service_tier            TEXT,
                server_tool_use_json    TEXT,
                host_request_id         TEXT,
                UNIQUE (host, host_event_id)
            )
            """);

        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_usage_events_host_ts ON usage_events (host, ts)");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_usage_events_ts      ON usage_events (ts)");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_usage_events_session ON usage_events (host, session_id, turn_index)");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_usage_events_project ON usage_events (project_repo, project_path)");

        // Daily rollup — forever retention, bounded cardinality (~7k rows/year
        // for a heavy user with 5 projects × 2 hosts × 2 models × 365 days).
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS usage_daily (
                day_utc               INTEGER NOT NULL,
                host                  TEXT NOT NULL,
                model                 TEXT,
                project               TEXT,                       -- denormalized COALESCE(project_repo, project_path) from usage_events
                session_count         INTEGER NOT NULL,
                turn_count            INTEGER NOT NULL,
                input_tokens          INTEGER,
                cache_creation_tokens INTEGER,
                cache_read_tokens     INTEGER,
                output_tokens         INTEGER,
                PRIMARY KEY (day_utc, host, model, project)
            )
            """);

        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_usage_daily_host_day ON usage_daily (host, day_utc)");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_usage_daily_day      ON usage_daily (day_utc)");

        // Per-host watermarks — used by UsageIndexer to skip already-scanned
        // events on repeated session_start passes. See spec §4.3.
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS usage_watermarks (
                host                      TEXT PRIMARY KEY,
                last_indexed_ts           INTEGER NOT NULL,
                last_scan_at              INTEGER NOT NULL,
                last_rollup_at            INTEGER                 -- NULL until the first rollup pass runs for this host
            )
            """);
    }

    /// <summary>
    /// Migration 7 — persistent outbound sync queue. Items are enqueued
    /// when RoutingStore writes a local memory and drained by SyncService
    /// to push to Cortex. Survives process crashes so pending items are
    /// retried on next session start. See Task 11.
    /// </summary>
    private static void Migration7_SyncQueue(
        MsSqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS sync_queue (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                entity_type TEXT NOT NULL,
                operation   TEXT NOT NULL,
                entity_id   TEXT,
                payload     TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                attempts    INTEGER DEFAULT 0,
                last_error  TEXT
            )
            """);
    }

    private static void Migration8_Scope(MsSqliteConnection conn, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        foreach (var (tier, type) in AllTablePairs)
        {
            var table = TableName(tier, type);
            using var alter = conn.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN scope TEXT NOT NULL DEFAULT ''";
            alter.ExecuteNonQuery();

            using var idx = conn.CreateCommand();
            idx.Transaction = tx;
            idx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{table}_scope ON {table}(scope)";
            idx.ExecuteNonQuery();
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
