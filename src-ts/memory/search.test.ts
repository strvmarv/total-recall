import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import { mockEmbedSemantic } from "../../tests-ts/helpers/embedding.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import { searchMemory } from "./search.js";

describe("searchMemory hybrid", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("boosts results that match both vector and FTS5", async () => {
    const tomlId = insertEntry(db, "warm", "memory", {
      content: "Config file format is TOML",
    });
    insertEmbedding(db, "warm", "memory", tomlId, mockEmbedSemantic("Config file format is TOML"));

    const otherId = insertEntry(db, "warm", "memory", {
      content: "Deploy to staging first via GitHub Actions",
    });
    insertEmbedding(db, "warm", "memory", otherId, mockEmbedSemantic("Deploy to staging first via GitHub Actions"));

    const results = await searchMemory(db, mockEmbedSemantic, "TOML", {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 5,
    });

    expect(results.length).toBeGreaterThan(0);
    expect(results[0]!.entry.id).toBe(tomlId);
  });

  it("still returns results when FTS5 has no matches", async () => {
    const id = insertEntry(db, "warm", "memory", {
      content: "the cat sat on the mat",
    });
    insertEmbedding(db, "warm", "memory", id, mockEmbedSemantic("the cat sat on the mat"));

    const results = await searchMemory(db, mockEmbedSemantic, "feline sitting", {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 5,
    });

    expect(results.length).toBeGreaterThan(0);
  });

  it("returns results from FTS5 even when vector score is low", async () => {
    const id = insertEntry(db, "warm", "memory", {
      content: "Config file format is TOML",
    });
    insertEmbedding(db, "warm", "memory", id, mockEmbedSemantic("completely unrelated fish banana"));

    const results = await searchMemory(db, mockEmbedSemantic, "TOML", {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 5,
    });

    expect(results.length).toBeGreaterThan(0);
    expect(results[0]!.entry.id).toBe(id);
  });

  it("searches across multiple tiers with fusion", async () => {
    const hotId = insertEntry(db, "hot", "memory", {
      content: "Hot tier TOML entry",
    });
    insertEmbedding(db, "hot", "memory", hotId, mockEmbedSemantic("Hot tier TOML entry"));

    const warmId = insertEntry(db, "warm", "memory", {
      content: "Warm tier TOML entry",
    });
    insertEmbedding(db, "warm", "memory", warmId, mockEmbedSemantic("Warm tier TOML entry"));

    const results = await searchMemory(db, mockEmbedSemantic, "TOML", {
      tiers: [
        { tier: "hot", content_type: "memory" },
        { tier: "warm", content_type: "memory" },
      ],
      topK: 5,
    });

    expect(results.length).toBe(2);
  });
});
