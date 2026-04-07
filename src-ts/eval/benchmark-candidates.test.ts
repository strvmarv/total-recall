import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import { writeCandidates, listCandidates, resolveCandidates } from "./benchmark-candidates.js";
import type { Database } from "bun:sqlite";

// Mock fs to prevent writing to the real retrieval.jsonl during tests
vi.mock("node:fs", async (importOriginal) => {
  const actual = await importOriginal<typeof import("node:fs")>();
  return {
    ...actual,
    readFileSync: vi.fn(() => ""),
    writeFileSync: vi.fn(),
  };
});

describe("benchmark-candidates", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("writeCandidates inserts new candidates from misses", () => {
    writeCandidates(db, [
      { query: "test query 1", topScore: 0.3, timestamp: 1000 },
      { query: "test query 2", topScore: 0.1, timestamp: 2000 },
    ], [
      { query: "test query 1", topContent: "some content", topEntryId: "e1" },
      { query: "test query 2", topContent: "other content", topEntryId: "e2" },
    ]);
    const candidates = listCandidates(db);
    expect(candidates).toHaveLength(2);
    const queryTexts = candidates.map((c) => c.query_text);
    expect(queryTexts).toContain("test query 1");
    expect(queryTexts).toContain("test query 2");
  });

  it("writeCandidates deduplicates by query_text", () => {
    writeCandidates(db, [
      { query: "same query", topScore: 0.3, timestamp: 1000 },
    ], [
      { query: "same query", topContent: "content", topEntryId: "e1" },
    ]);
    writeCandidates(db, [
      { query: "same query", topScore: 0.2, timestamp: 2000 },
    ], [
      { query: "same query", topContent: "content", topEntryId: "e1" },
    ]);
    const candidates = listCandidates(db);
    expect(candidates).toHaveLength(1);
    expect(candidates[0]!.times_seen).toBe(2);
    expect(candidates[0]!.last_seen).toBe(2000);
    expect(candidates[0]!.top_score).toBe(0.2);
  });

  it("listCandidates returns only pending, sorted by times_seen desc", () => {
    writeCandidates(db, [
      { query: "rare", topScore: 0.3, timestamp: 1000 },
      { query: "frequent", topScore: 0.2, timestamp: 1000 },
    ], [
      { query: "rare", topContent: "a", topEntryId: "e1" },
      { query: "frequent", topContent: "b", topEntryId: "e2" },
    ]);
    writeCandidates(db, [
      { query: "frequent", topScore: 0.15, timestamp: 2000 },
    ], [
      { query: "frequent", topContent: "b", topEntryId: "e2" },
    ]);

    const candidates = listCandidates(db);
    expect(candidates[0]!.query_text).toBe("frequent");
    expect(candidates[1]!.query_text).toBe("rare");
  });

  it("resolveCandidates marks accepted and rejected", () => {
    writeCandidates(db, [
      { query: "q1", topScore: 0.3, timestamp: 1000 },
      { query: "q2", topScore: 0.2, timestamp: 1000 },
      { query: "q3", topScore: 0.1, timestamp: 1000 },
    ], [
      { query: "q1", topContent: "c1", topEntryId: "e1" },
      { query: "q2", topContent: "c2", topEntryId: "e2" },
      { query: "q3", topContent: "c3", topEntryId: "e3" },
    ]);

    const candidates = listCandidates(db);
    const id1 = candidates.find((c) => c.query_text === "q1")!.id;
    const id2 = candidates.find((c) => c.query_text === "q2")!.id;
    const id3 = candidates.find((c) => c.query_text === "q3")!.id;

    const result = resolveCandidates(db, [id1, id3], [id2]);
    expect(result.accepted).toBe(2);
    expect(result.rejected).toBe(1);

    // Only pending remain
    expect(listCandidates(db)).toHaveLength(0);
  });
});
