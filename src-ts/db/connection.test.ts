import { describe, it, expect, afterEach } from "vitest";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import type { Database } from "bun:sqlite";

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
