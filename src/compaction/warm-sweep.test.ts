import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { countEntries } from "../db/entries.js";
import { sweepWarmTier } from "./warm-sweep.js";

describe("sweepWarmTier", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("moves old unaccessed warm entries to cold", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "old warm memory",
      tier: "warm",
    });
    const oldTime = Date.now() - 60 * 24 * 60 * 60 * 1000;
    db.prepare(
      "UPDATE warm_memories SET created_at = ?, last_accessed_at = ?, updated_at = ?, access_count = 0 WHERE id = ?",
    ).run(oldTime, oldTime, oldTime, id);

    const result = await sweepWarmTier(db, mockEmbedSemantic, { coldDecayDays: 30 }, "test-session");

    expect(result.demoted).toContain(id);
    expect(countEntries(db, "warm", "memory")).toBe(0);
    expect(countEntries(db, "cold", "memory")).toBe(1);
  });

  it("keeps recently accessed warm entries", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "fresh warm memory",
      tier: "warm",
    });

    const result = await sweepWarmTier(db, mockEmbedSemantic, { coldDecayDays: 30 }, "test-session");

    expect(result.kept).toContain(id);
    expect(countEntries(db, "warm", "memory")).toBe(1);
    expect(countEntries(db, "cold", "memory")).toBe(0);
  });
});
