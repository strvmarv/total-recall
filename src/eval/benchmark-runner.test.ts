import { describe, it, expect } from "vitest";
import { resolve } from "node:path";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { runBenchmark } from "./benchmark-runner.js";

describe("runBenchmark", () => {
  it("loads corpus, seeds DB, runs queries, and produces report", () => {
    const db = createTestDb();
    const result = runBenchmark(db, mockEmbedSemantic, {
      corpusPath: resolve("eval/corpus/memories.jsonl"),
      benchmarkPath: resolve("eval/benchmarks/retrieval.jsonl"),
    });
    expect(result.totalQueries).toBe(20);
    expect(result.fuzzyMatchRate).toBeGreaterThanOrEqual(0);
    expect(result.fuzzyMatchRate).toBeLessThanOrEqual(1);
    expect(result.avgLatencyMs).toBeGreaterThanOrEqual(0);
  });

  it("returns correct shape for all detail entries", () => {
    const db = createTestDb();
    const result = runBenchmark(db, mockEmbedSemantic, {
      corpusPath: resolve("eval/corpus/memories.jsonl"),
      benchmarkPath: resolve("eval/benchmarks/retrieval.jsonl"),
    });
    expect(result.details).toHaveLength(20);
    for (const detail of result.details) {
      expect(typeof detail.query).toBe("string");
      expect(typeof detail.expectedContains).toBe("string");
      expect(typeof detail.topScore).toBe("number");
      expect(typeof detail.matched).toBe("boolean");
      expect(typeof detail.fuzzyMatched).toBe("boolean");
    }
  });

  it("exactMatchRate and tierRoutingRate are within [0, 1]", () => {
    const db = createTestDb();
    const result = runBenchmark(db, mockEmbedSemantic, {
      corpusPath: resolve("eval/corpus/memories.jsonl"),
      benchmarkPath: resolve("eval/benchmarks/retrieval.jsonl"),
    });
    expect(result.exactMatchRate).toBeGreaterThanOrEqual(0);
    expect(result.exactMatchRate).toBeLessThanOrEqual(1);
    expect(result.tierRoutingRate).toBeGreaterThanOrEqual(0);
    expect(result.tierRoutingRate).toBeLessThanOrEqual(1);
  });
});
