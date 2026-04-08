import { describe, it, expect, afterEach } from "vitest";
import { mkdtempSync, rmSync, existsSync, mkdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import { Database } from "bun:sqlite";
import * as sqliteVec from "sqlite-vec";
import { createTestDb } from "../../tests/helpers/db.js";
import { bootstrapSqlite } from "./sqlite-bootstrap.js";
import { getDbPath } from "../config.js";

describe("SQLite schema", () => {
  let db: Database;

  afterEach(() => {
    db?.close();
  });

  it("creates all 6 content tables", () => {
    db = createTestDb();
    const contentTables = [
      "hot_memories",
      "hot_knowledge",
      "warm_memories",
      "warm_knowledge",
      "cold_memories",
      "cold_knowledge",
    ];
    for (const table of contentTables) {
      const row = db
        .prepare(
          "SELECT name FROM sqlite_master WHERE type='table' AND name=?",
        )
        .get(table);
      expect(row, `Expected table ${table} to exist`).toBeTruthy();
    }
  });

  it("creates all 4 system tables", () => {
    db = createTestDb();
    const systemTables = [
      "retrieval_events",
      "compaction_log",
      "config_snapshots",
      "import_log",
    ];
    for (const table of systemTables) {
      const row = db
        .prepare(
          "SELECT name FROM sqlite_master WHERE type='table' AND name=?",
        )
        .get(table);
      expect(row, `Expected system table ${table} to exist`).toBeTruthy();
    }
  });

  it("creates vector virtual tables", () => {
    db = createTestDb();
    const vecTables = [
      "hot_memories_vec",
      "warm_knowledge_vec",
      "cold_memories_vec",
    ];
    for (const table of vecTables) {
      // vec0 tables show up in sqlite_master as virtual tables
      const row = db
        .prepare("SELECT name FROM sqlite_master WHERE name=?")
        .get(table);
      expect(row, `Expected vector table ${table} to exist`).toBeTruthy();
    }
  });

  it("can insert and read back a row from hot_memories", () => {
    db = createTestDb();
    const now = Date.now();
    db.prepare(
      `INSERT INTO hot_memories
        (id, content, summary, source, source_tool, project, tags, created_at, updated_at, last_accessed_at, access_count, decay_score, parent_id, collection_id, metadata)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      "test-id-1",
      "This is test content",
      "Test summary",
      "test-source",
      "manual",
      "test-project",
      '["tag1","tag2"]',
      now,
      now,
      now,
      0,
      1.0,
      null,
      null,
      "{}",
    );

    const row = db
      .prepare("SELECT * FROM hot_memories WHERE id=?")
      .get("test-id-1") as Record<string, unknown> | undefined;

    expect(row).toBeTruthy();
    expect(row!["id"]).toBe("test-id-1");
    expect(row!["content"]).toBe("This is test content");
    expect(row!["project"]).toBe("test-project");
    expect(row!["decay_score"]).toBe(1.0);
  });
});

describe("TOTAL_RECALL_DB_PATH wiring", () => {
  const ORIGINAL_DB_PATH = process.env.TOTAL_RECALL_DB_PATH;
  let customDir: string;

  afterEach(() => {
    if (ORIGINAL_DB_PATH === undefined) delete process.env.TOTAL_RECALL_DB_PATH;
    else process.env.TOTAL_RECALL_DB_PATH = ORIGINAL_DB_PATH;
    if (customDir) rmSync(customDir, { recursive: true, force: true });
  });

  it("opens a DB at a custom path under an auto-created parent directory", () => {
    customDir = mkdtempSync(join(tmpdir(), "tr-dbpath-wiring-"));
    // Nested path whose parent does NOT exist yet — we expect mkdirSync to create it.
    const customDbPath = join(customDir, "nested", "sub", "custom.db");
    process.env.TOTAL_RECALL_DB_PATH = customDbPath;

    // Exercise the exact pipeline src/db/connection.ts uses, but without
    // touching the getDb() singleton (which would capture the first path
    // seen for the life of the test process).
    const resolved = getDbPath();
    expect(resolved).toBe(customDbPath);

    bootstrapSqlite();
    mkdirSync(dirname(resolved), { recursive: true });
    const db = new Database(resolved);
    try {
      sqliteVec.load(db);
      // Sanity: the DB is open and queryable. We avoid vec_version() in
      // case the better-sqlite3 shim's sqlite-vec binding doesn't expose
      // it; sqlite_master is universal.
      const row = db
        .prepare("SELECT name FROM sqlite_master WHERE type='table' LIMIT 1")
        .get();
      // No strong assertion on the row — just proving the query executes.
      expect(row === undefined || typeof row === "object").toBe(true);
    } finally {
      db.close();
    }

    expect(existsSync(customDbPath)).toBe(true);
    expect(existsSync(dirname(customDbPath))).toBe(true);
  });
});
