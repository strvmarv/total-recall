import type { Database } from "bun:sqlite";
import { ALL_TABLE_PAIRS, tableName, vecTableName } from "../types.js";

const SCHEMA_VERSION = 1;

function contentTableDDL(name: string): string {
  return `
    CREATE TABLE IF NOT EXISTS ${name} (
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
  `;
}

function contentTableIndexes(name: string): string[] {
  return [
    `CREATE INDEX IF NOT EXISTS idx_${name}_project         ON ${name}(project)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_decay_score     ON ${name}(decay_score)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_last_accessed   ON ${name}(last_accessed_at)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_parent_id       ON ${name}(parent_id)`,
    `CREATE INDEX IF NOT EXISTS idx_${name}_collection_id   ON ${name}(collection_id)`,
  ];
}

const SYSTEM_TABLE_DDLS = [
  `CREATE TABLE IF NOT EXISTS retrieval_events (
    id                      TEXT PRIMARY KEY NOT NULL,
    timestamp               INTEGER NOT NULL,
    session_id              TEXT NOT NULL,
    query_text              TEXT NOT NULL,
    query_source            TEXT NOT NULL,
    query_embedding         BLOB,
    results                 TEXT NOT NULL DEFAULT '[]',
    result_count            INTEGER NOT NULL DEFAULT 0,
    top_score               REAL,
    top_tier                TEXT,
    top_content_type        TEXT,
    outcome_used            INTEGER,
    outcome_signal          TEXT,
    config_snapshot_id      TEXT NOT NULL,
    latency_ms              INTEGER,
    tiers_searched          TEXT NOT NULL DEFAULT '[]',
    total_candidates_scanned INTEGER
  )`,

  `CREATE TABLE IF NOT EXISTS compaction_log (
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
  )`,

  `CREATE TABLE IF NOT EXISTS config_snapshots (
    id        TEXT PRIMARY KEY NOT NULL,
    name      TEXT,
    timestamp INTEGER NOT NULL,
    config    TEXT NOT NULL
  )`,

  `CREATE TABLE IF NOT EXISTS import_log (
    id              TEXT PRIMARY KEY NOT NULL,
    timestamp       INTEGER NOT NULL,
    source_tool     TEXT NOT NULL,
    source_path     TEXT NOT NULL,
    content_hash    TEXT NOT NULL,
    target_entry_id TEXT NOT NULL,
    target_tier     TEXT NOT NULL,
    target_type     TEXT NOT NULL
  )`,
];

const SYSTEM_TABLE_INDEXES = [
  `CREATE INDEX IF NOT EXISTS idx_retrieval_events_timestamp   ON retrieval_events(timestamp)`,
  `CREATE INDEX IF NOT EXISTS idx_retrieval_events_session_id  ON retrieval_events(session_id)`,
  `CREATE INDEX IF NOT EXISTS idx_compaction_log_timestamp     ON compaction_log(timestamp)`,
  `CREATE INDEX IF NOT EXISTS idx_compaction_log_source_tier   ON compaction_log(source_tier)`,
  `CREATE INDEX IF NOT EXISTS idx_import_log_content_hash      ON import_log(content_hash)`,
  `CREATE INDEX IF NOT EXISTS idx_import_log_source_tool       ON import_log(source_tool)`,
];

const SCHEMA_VERSION_DDL = `
  CREATE TABLE IF NOT EXISTS _schema_version (
    version    INTEGER NOT NULL,
    applied_at INTEGER NOT NULL
  )
`;

// Each migration runs once, in order. Add new migrations at the end.
// Migration functions receive the db and run inside a transaction.
const MIGRATIONS: Array<(db: Database) => void> = [
  // Migration 1: Initial schema (v1)
  (db) => {
    for (const pair of ALL_TABLE_PAIRS) {
      const tbl = tableName(pair.tier, pair.type);
      const vecTbl = vecTableName(pair.tier, pair.type);
      db.prepare(contentTableDDL(tbl)).run();
      db.prepare(
        `CREATE VIRTUAL TABLE IF NOT EXISTS ${vecTbl} USING vec0(embedding float[384])`,
      ).run();
      for (const idx of contentTableIndexes(tbl)) {
        db.prepare(idx).run();
      }
    }
    for (const ddl of SYSTEM_TABLE_DDLS) {
      db.prepare(ddl).run();
    }
    for (const idx of SYSTEM_TABLE_INDEXES) {
      db.prepare(idx).run();
    }
  },
  // Migration 2: _meta key-value store + benchmark_candidates
  (db) => {
    db.prepare(`
      CREATE TABLE IF NOT EXISTS _meta (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
      )
    `).run();

    db.prepare(`
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
    `).run();

    db.prepare(
      `CREATE INDEX IF NOT EXISTS idx_benchmark_candidates_status ON benchmark_candidates(status)`
    ).run();
  },
  // Migration 3: FTS5 full-text indexes for hybrid search
  (db) => {
    for (const pair of ALL_TABLE_PAIRS) {
      const tbl = tableName(pair.tier, pair.type);
      const ftsTbl = `${tbl}_fts`;

      // Content-synced FTS5 table indexing content and tags columns
      db.prepare(
        `CREATE VIRTUAL TABLE IF NOT EXISTS ${ftsTbl} USING fts5(content, tags, content=${tbl}, content_rowid=rowid)`,
      ).run();

      // Sync triggers: keep FTS5 in sync with content table mutations
      db.prepare(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_ai AFTER INSERT ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
        END
      `).run();

      db.prepare(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_ad AFTER DELETE ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(${ftsTbl}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
        END
      `).run();

      db.prepare(`
        CREATE TRIGGER IF NOT EXISTS ${tbl}_fts_au AFTER UPDATE ON ${tbl} BEGIN
          INSERT INTO ${ftsTbl}(${ftsTbl}, rowid, content, tags) VALUES('delete', old.rowid, old.content, old.tags);
          INSERT INTO ${ftsTbl}(rowid, content, tags) VALUES (new.rowid, new.content, new.tags);
        END
      `).run();

      // Backfill existing data
      db.prepare(
        `INSERT INTO ${ftsTbl}(rowid, content, tags) SELECT rowid, content, tags FROM ${tbl}`,
      ).run();
    }
  },
];

function getCurrentVersion(db: Database): number {
  const hasTable = db
    .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='_schema_version'")
    .get();
  if (!hasTable) return 0;

  const row = db
    .prepare("SELECT MAX(version) as v FROM _schema_version")
    .get() as { v: number | null } | undefined;
  return row?.v ?? 0;
}

export function initSchema(db: Database): void {
  // Enable WAL mode and foreign keys outside the transaction (SQLite requirement)
  db.pragma("journal_mode = WAL");
  db.pragma("foreign_keys = ON");

  const migrate = db.transaction(() => {
    // Ensure version table exists
    db.prepare(SCHEMA_VERSION_DDL).run();

    const currentVersion = getCurrentVersion(db);

    for (let i = currentVersion; i < MIGRATIONS.length; i++) {
      MIGRATIONS[i]!(db);
      db.prepare("INSERT INTO _schema_version (version, applied_at) VALUES (?, ?)").run(
        i + 1,
        Date.now(),
      );
    }
  });

  migrate();
}
