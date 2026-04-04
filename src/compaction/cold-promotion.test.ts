import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { countEntries, updateEntry } from "../db/entries.js";
import { checkAndPromoteCold } from "./cold-promotion.js";

describe("checkAndPromoteCold", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("promotes cold entries accessed 3+ times in 7 days", () => {
    const id = storeMemory(db, mockEmbedSemantic, {
      content: "frequently accessed cold memory",
      tier: "cold",
    });

    // Simulate 3 accesses within the last 7 days
    db.prepare(
      "UPDATE cold_memories SET access_count = 3, last_accessed_at = ? WHERE id = ?",
    ).run(Date.now(), id);

    const result = checkAndPromoteCold(db, mockEmbedSemantic, {
      accessThreshold: 3,
      windowDays: 7,
    });

    expect(result.promoted).toContain(id);
    // Original stays in cold
    expect(countEntries(db, "cold", "memory")).toBe(1);
    // Copy added to warm
    expect(countEntries(db, "warm", "memory")).toBe(1);
  });

  it("does not promote entries below threshold", () => {
    const id = storeMemory(db, mockEmbedSemantic, {
      content: "rarely accessed cold memory",
      tier: "cold",
    });

    // Only 1 access
    db.prepare(
      "UPDATE cold_memories SET access_count = 1, last_accessed_at = ? WHERE id = ?",
    ).run(Date.now(), id);

    const result = checkAndPromoteCold(db, mockEmbedSemantic, {
      accessThreshold: 3,
      windowDays: 7,
    });

    expect(result.promoted).not.toContain(id);
    expect(countEntries(db, "cold", "memory")).toBe(1);
    expect(countEntries(db, "warm", "memory")).toBe(0);
  });
});
