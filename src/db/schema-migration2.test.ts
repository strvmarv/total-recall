import { describe, it, expect } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";

describe("migration 2: _meta and benchmark_candidates", () => {
  it("creates _meta table", () => {
    const db = createTestDb();
    const row = db.prepare(
      "SELECT name FROM sqlite_master WHERE type='table' AND name='_meta'"
    ).get() as { name: string } | undefined;
    expect(row?.name).toBe("_meta");
    db.close();
  });

  it("creates benchmark_candidates table", () => {
    const db = createTestDb();
    const row = db.prepare(
      "SELECT name FROM sqlite_master WHERE type='table' AND name='benchmark_candidates'"
    ).get() as { name: string } | undefined;
    expect(row?.name).toBe("benchmark_candidates");
    db.close();
  });

  it("allows insert and query on _meta", () => {
    const db = createTestDb();
    db.prepare("INSERT INTO _meta (key, value) VALUES (?, ?)").run("test_key", "test_value");
    const row = db.prepare("SELECT value FROM _meta WHERE key = ?").get("test_key") as { value: string };
    expect(row.value).toBe("test_value");
    db.close();
  });

  it("enforces unique query_text on benchmark_candidates", () => {
    const db = createTestDb();
    db.prepare(`
      INSERT INTO benchmark_candidates (id, query_text, top_score, first_seen, last_seen)
      VALUES ('id1', 'test query', 0.3, 1000, 1000)
    `).run();
    expect(() => {
      db.prepare(`
        INSERT INTO benchmark_candidates (id, query_text, top_score, first_seen, last_seen)
        VALUES ('id2', 'test query', 0.4, 2000, 2000)
      `).run();
    }).toThrow();
    db.close();
  });
});
