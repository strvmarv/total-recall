import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { countEntries } from "../db/entries.js";
import { compactHotTier } from "./compactor.js";
import type { TotalRecallConfig } from "../types.js";

const testConfig: TotalRecallConfig["compaction"] = {
  decay_half_life_hours: 168,
  warm_threshold: 0.3,
  promote_threshold: 0.7,
  warm_sweep_interval_days: 7,
};

describe("compactHotTier", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("moves low-decay entries from hot to warm", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, { content: "old memory" });
    const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
    db.prepare(
      "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
    ).run(oldTime, oldTime, oldTime, id);

    const before = countEntries(db, "hot", "memory");
    await compactHotTier(db, mockEmbedSemantic, testConfig, "test-session");
    const hotAfter = countEntries(db, "hot", "memory");
    const warmAfter = countEntries(db, "warm", "memory");

    // Entry should have moved or been discarded — no longer in hot
    expect(hotAfter).toBeLessThan(before);
    // Depending on score it went to warm or was discarded
    expect(hotAfter + warmAfter).toBeLessThanOrEqual(before);
  });

  it("keeps fresh high-score entries in hot tier", async () => {
    await storeMemory(db, mockEmbedSemantic, { content: "just added", type: "correction" });
    const before = countEntries(db, "hot", "memory");
    await compactHotTier(db, mockEmbedSemantic, testConfig, "test-session");
    expect(countEntries(db, "hot", "memory")).toBe(before);
  });

  it("logs compaction events", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, { content: "old memory for logging" });
    const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
    db.prepare(
      "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
    ).run(oldTime, oldTime, oldTime, id);

    await compactHotTier(db, mockEmbedSemantic, testConfig, "test-session");

    const rows = db
      .prepare("SELECT * FROM compaction_log WHERE session_id = ?")
      .all("test-session") as Array<{ source_tier: string }>;

    expect(rows.length).toBeGreaterThan(0);
    expect(rows[0]!.source_tier).toBe("hot");
  });
});
