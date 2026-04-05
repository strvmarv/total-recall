import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { storeMemory } from "./store.js";
import { searchMemory } from "./search.js";
import { getMemory } from "./get.js";
import { updateMemory } from "./update.js";
import { deleteMemory } from "./delete.js";
import { promoteEntry, demoteEntry } from "./promote-demote.js";

describe("memory operations", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("stores a memory and retrieves it by semantic search", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "the cat sat on the mat",
      tier: "hot",
    });

    const results = await searchMemory(db, mockEmbedSemantic, "cat sat mat", {
      tiers: [{ tier: "hot", content_type: "memory" }],
      topK: 5,
    });

    expect(results.length).toBeGreaterThan(0);
    const found = results.find((r) => r.entry.id === id);
    expect(found).toBeDefined();
  });

  it("stores to hot tier by default", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "default tier memory",
    });

    const result = getMemory(db, id);
    expect(result).not.toBeNull();
    expect(result!.tier).toBe("hot");
  });

  it("updates memory content and re-embeds", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "original content here",
      tier: "hot",
    });

    await updateMemory(db, mockEmbedSemantic, id, { content: "updated content now" });

    const result = getMemory(db, id);
    expect(result).not.toBeNull();
    expect(result!.entry.content).toBe("updated content now");
  });

  it("deletes a memory", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "to be deleted",
      tier: "hot",
    });

    const deleted = deleteMemory(db, id);
    expect(deleted).toBe(true);

    const result = getMemory(db, id);
    expect(result).toBeNull();
  });

  it("promotes from hot to warm", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "promote me",
      tier: "hot",
    });

    await promoteEntry(db, mockEmbedSemantic, id, "hot", "memory", "warm", "memory");

    const result = getMemory(db, id);
    expect(result).not.toBeNull();
    expect(result!.tier).toBe("warm");
  });

  it("demotes from warm to cold", async () => {
    const id = await storeMemory(db, mockEmbedSemantic, {
      content: "demote me",
      tier: "warm",
    });

    await demoteEntry(db, mockEmbedSemantic, id, "warm", "memory", "cold", "memory");

    const result = getMemory(db, id);
    expect(result).not.toBeNull();
    expect(result!.tier).toBe("cold");
  });
});
