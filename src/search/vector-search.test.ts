import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding, searchByVector } from "./vector-search.js";

describe("vector search", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("inserts an embedding and retrieves it by similarity", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "the cat sat on the mat",
    });
    const embedding = mockEmbedSemantic("the cat sat on the mat");
    insertEmbedding(db, "hot", "memory", id, embedding);

    const query = mockEmbedSemantic("cat mat");
    const results = searchByVector(db, "hot", "memory", query, { topK: 5 });

    expect(results.length).toBeGreaterThan(0);
    expect(results[0]!.id).toBe(id);
    expect(results[0]!.score).toBeGreaterThan(0);
    expect(results[0]!.score).toBeLessThanOrEqual(1);
  });

  it("returns results ordered by similarity score descending", () => {
    const catId = insertEntry(db, "hot", "memory", {
      content: "cats are fluffy and purr",
    });
    const fishId = insertEntry(db, "hot", "memory", {
      content: "salmon tuna cod ocean fish",
    });
    const carsId = insertEntry(db, "hot", "memory", {
      content: "cars trucks engines roads driving",
    });

    insertEmbedding(db, "hot", "memory", catId, mockEmbedSemantic("cats are fluffy and purr"));
    insertEmbedding(db, "hot", "memory", fishId, mockEmbedSemantic("salmon tuna cod ocean fish"));
    insertEmbedding(db, "hot", "memory", carsId, mockEmbedSemantic("cars trucks engines roads driving"));

    const query = mockEmbedSemantic("fluffy cat purring");
    const results = searchByVector(db, "hot", "memory", query, { topK: 3 });

    expect(results.length).toBe(3);
    // Results should be sorted by score descending
    expect(results[0]!.score).toBeGreaterThanOrEqual(results[1]!.score);
    expect(results[1]!.score).toBeGreaterThanOrEqual(results[2]!.score);
    // The cat entry should be closest
    expect(results[0]!.id).toBe(catId);
  });

  it("respects topK limit", () => {
    const texts = [
      "apple fruit orchard",
      "banana tropical yellow",
      "cherry red summer",
      "date palm sweet",
      "elderberry dark purple",
      "fig mediterranean",
      "grape vine wine",
      "honeydew melon",
      "kiwi green fuzzy",
      "lemon citrus sour",
    ];

    for (const text of texts) {
      const id = insertEntry(db, "hot", "memory", { content: text });
      insertEmbedding(db, "hot", "memory", id, mockEmbedSemantic(text));
    }

    const query = mockEmbedSemantic("fruit");
    const results = searchByVector(db, "hot", "memory", query, { topK: 3 });

    expect(results.length).toBe(3);
  });
});
