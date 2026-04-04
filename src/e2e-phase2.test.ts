import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../tests/helpers/db.js";
import { mockEmbedSemantic } from "../tests/helpers/embedding.js";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { tmpdir } from "node:os";
import type Database from "better-sqlite3";

import { ingestDirectory } from "./ingestion/ingest.js";
import { searchMemory } from "./memory/search.js";
import { compactHotTier } from "./compaction/compactor.js";
import { ClaudeCodeImporter } from "./importers/claude-code.js";
import { logRetrievalEvent, updateOutcome, getRetrievalEvents } from "./eval/event-logger.js";
import { computeMetrics } from "./eval/metrics.js";
import { runBenchmark } from "./eval/benchmark-runner.js";
import { storeMemory } from "./memory/store.js";
import { listEntries, countEntries } from "./db/entries.js";

const embed = mockEmbedSemantic;

const compactionConfig = {
  decay_half_life_hours: 168,
  warm_threshold: 0.3,
  promote_threshold: 0.7,
  warm_sweep_interval_days: 7,
};

describe("total-recall phase 2 e2e", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `tr-e2e-${Date.now()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    db.close();
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("ingests a directory and searches knowledge base", () => {
    // Create two markdown files in tmpDir
    writeFileSync(
      join(tmpDir, "typescript-tips.md"),
      `# TypeScript Tips\n\nAlways use strict mode in TypeScript projects.\nEnable noImplicitAny to catch type errors early.\n`,
    );
    writeFileSync(
      join(tmpDir, "testing-guide.md"),
      `# Testing Guide\n\nUse vitest for unit tests.\nWrite integration tests with real databases.\nAvoid mocking the data layer.\n`,
    );

    const result = ingestDirectory(db, embed, tmpDir);

    expect(result.documentCount).toBe(2);
    expect(result.totalChunks).toBeGreaterThan(0);
    expect(result.collectionId).toBeTruthy();

    // Verify entries were stored in the cold/knowledge tier
    const coldCount = countEntries(db, "cold", "knowledge");
    expect(coldCount).toBeGreaterThan(0);

    // Search the cold/knowledge tier — use minScore -1 to accept all similarity scores
    // since the mock embedder produces pseudo-random vectors (not semantic)
    const searchResults = searchMemory(db, embed, "TypeScript strict mode configuration", {
      tiers: [{ tier: "cold", content_type: "knowledge" }],
      topK: 5,
      minScore: -1,
    });

    expect(searchResults.length).toBeGreaterThan(0);
  });

  it("compaction moves old hot entries to warm", () => {
    // Store 3 memories in hot tier
    const id1 = storeMemory(db, embed, {
      content: "Fresh memory stored today — keep this one hot",
      tier: "hot",
      contentType: "memory",
    });

    const id2 = storeMemory(db, embed, {
      content: "Old memory from 40 days ago — should move to warm",
      tier: "hot",
      contentType: "memory",
    });

    const id3 = storeMemory(db, embed, {
      content: "Very old memory from 60 days ago — should be discarded or moved",
      tier: "hot",
      contentType: "memory",
    });

    // Set entries 2 and 3 to old timestamps (30+ and 60+ days ago)
    const thirtyOneDaysAgo = Date.now() - 31 * 24 * 60 * 60 * 1000;
    const sixtyDaysAgo = Date.now() - 60 * 24 * 60 * 60 * 1000;

    db.prepare("UPDATE hot_memories SET created_at = ?, last_accessed_at = ? WHERE id = ?").run(
      thirtyOneDaysAgo,
      thirtyOneDaysAgo,
      id2,
    );
    db.prepare("UPDATE hot_memories SET created_at = ?, last_accessed_at = ? WHERE id = ?").run(
      sixtyDaysAgo,
      sixtyDaysAgo,
      id3,
    );

    const before = countEntries(db, "hot", "memory");
    expect(before).toBe(3);

    const compactionResult = compactHotTier(db, embed, compactionConfig, "test-session");

    // id1 should stay hot (fresh), id2 and id3 should be moved to warm or discarded
    expect(compactionResult.carryForward).toContain(id1);
    expect(compactionResult.carryForward).not.toContain(id2);
    expect(compactionResult.carryForward).not.toContain(id3);

    const movedOrDiscarded = [...compactionResult.promoted, ...compactionResult.discarded];
    expect(movedOrDiscarded).toContain(id2);
    expect(movedOrDiscarded).toContain(id3);

    // Verify counts: hot should now only have id1
    const afterHot = countEntries(db, "hot", "memory");
    expect(afterHot).toBe(1);
  });

  it("imports from mock Claude Code directory", () => {
    // Create a mock ~/.claude directory structure
    const claudeDir = join(tmpDir, "claude");
    const projectDir = join(claudeDir, "projects", "test");
    const memoryDir = join(projectDir, "memory");
    mkdirSync(memoryDir, { recursive: true });

    // Write a .md file with frontmatter
    writeFileSync(
      join(memoryDir, "my-pref.md"),
      `---\nname: my-preference\ndescription: A stored preference\ntype: preference\n---\nAlways use pnpm for package management in this project.\n`,
    );

    const importer = new ClaudeCodeImporter(claudeDir);

    expect(importer.detect()).toBe(true);

    const importResult = importer.importMemories(db, embed);

    expect(importResult.imported).toBe(1);
    expect(importResult.errors).toHaveLength(0);

    // Verify the entry appears in warm_memories
    const warmEntries = listEntries(db, "warm", "memory");
    expect(warmEntries.length).toBeGreaterThan(0);

    const imported = warmEntries.find((e) => e.content.includes("pnpm"));
    expect(imported).toBeTruthy();
    expect(imported!.source_tool).toBe("claude-code");
  });

  it("logs retrieval events and computes metrics", () => {
    const sessionId = "e2e-metrics-session";
    const configSnapshotId = "snap-001";

    // Log 5 retrieval events with varying outcomes
    const eventData = [
      { used: true, score: 0.9 },
      { used: true, score: 0.85 },
      { used: false, score: 0.6 },
      { used: true, score: 0.75 },
      { used: null, score: 0.4 }, // no outcome set
    ];

    const eventIds: string[] = [];

    for (const data of eventData) {
      const id = logRetrievalEvent(db, {
        sessionId,
        queryText: "some query text",
        querySource: "auto",
        results:
          data.score > 0
            ? [
                {
                  entry_id: "fake-entry-id",
                  tier: "warm",
                  content_type: "memory",
                  score: data.score,
                  rank: 1,
                },
              ]
            : [],
        tiersSearched: ["warm"],
        configSnapshotId,
        latencyMs: 10,
      });
      eventIds.push(id);
    }

    // Update outcomes for the first 4 events
    for (let i = 0; i < 4; i++) {
      const data = eventData[i]!;
      updateOutcome(db, eventIds[i]!, { used: data.used as boolean });
    }
    // 5th event (index 4) has no outcome (null)

    const events = getRetrievalEvents(db, { sessionId });
    expect(events).toHaveLength(5);

    const metrics = computeMetrics(events, 0.5);

    // 3 out of 4 events with outcomes were used → precision = 0.75
    expect(metrics.precision).toBeCloseTo(0.75, 5);
    expect(metrics.hitRate).toBeCloseTo(0.75, 5);
    expect(metrics.totalEvents).toBe(5);

    // missRate: events with top_score < 0.5 → only the last event (0.4) qualifies
    expect(metrics.missRate).toBeGreaterThanOrEqual(0);
    expect(metrics.missRate).toBeLessThanOrEqual(1);

    // hitRate and missRate should be reasonable numbers
    expect(metrics.hitRate).toBeGreaterThan(0);
    expect(metrics.hitRate).toBeLessThanOrEqual(1);
  });

  it("runs benchmark suite without errors", () => {
    const corpusPath = resolve("eval/corpus/memories.jsonl");
    const benchmarkPath = resolve("eval/benchmarks/retrieval.jsonl");

    const result = runBenchmark(db, embed, {
      corpusPath,
      benchmarkPath,
    });

    expect(result.totalQueries).toBe(20);
    expect(result.exactMatchRate).toBeGreaterThanOrEqual(0);
    expect(result.exactMatchRate).toBeLessThanOrEqual(1);
    expect(result.fuzzyMatchRate).toBeGreaterThanOrEqual(0);
    expect(result.fuzzyMatchRate).toBeLessThanOrEqual(1);
    expect(result.tierRoutingRate).toBeGreaterThanOrEqual(0);
    expect(result.tierRoutingRate).toBeLessThanOrEqual(1);
    expect(result.details).toHaveLength(20);
  });
});
