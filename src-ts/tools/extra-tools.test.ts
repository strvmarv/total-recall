import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { existsSync, readFileSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import { mockEmbedSemantic } from "../../tests-ts/helpers/embedding.js";
import { storeMemory } from "../memory/store.js";
import { handleExtraTool, type ExportEntry } from "./extra-tools.js";
import type { ToolContext } from "./registry.js";
import type { TotalRecallConfig } from "../types.js";

const TEST_CONFIG: TotalRecallConfig = {
  tiers: {
    hot: { max_entries: 100, token_budget: 10000, carry_forward_threshold: 0.7 },
    warm: { max_entries: 500, retrieval_top_k: 10, similarity_threshold: 0.7, cold_decay_days: 30 },
    cold: { chunk_max_tokens: 512, chunk_overlap_tokens: 50, lazy_summary_threshold: 0.6 },
  },
  compaction: {
    decay_half_life_hours: 168,
    warm_threshold: 0.3,
    promote_threshold: 0.7,
    warm_sweep_interval_days: 7,
  },
  embedding: { model: "test", dimensions: 384 },
};

function makeMockEmbedder() {
  return {
    ensureLoaded: async () => {},
    embed: async (text: string): Promise<Float32Array> => mockEmbedSemantic(text),
    isLoaded: () => true,
    unload: () => {},
  };
}

function makeCtx(db: Database): ToolContext {
  return {
    db,
    config: TEST_CONFIG,
    embedder: makeMockEmbedder() as unknown as ToolContext["embedder"],
    sessionId: "test-session-" + randomUUID(),
    configSnapshotId: "default",
    sessionInitialized: false,
    sessionInitResult: null,
    sessionInitPromise: null,
  };
}

function parseResult(result: { content: Array<{ type: string; text: string }> }) {
  return JSON.parse(result.content[0]!.text) as Record<string, unknown>;
}

describe("extra-tools", () => {
  let db: Database;
  let ctx: ToolContext;

  beforeEach(() => {
    db = createTestDb();
    ctx = makeCtx(db);
  });

  afterEach(() => {
    db.close();
  });

  describe("compact_now", () => {
    it("returns compaction result counts for empty hot tier", async () => {
      const result = await handleExtraTool("compact_now", {}, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("carryForward");
      expect(data).toHaveProperty("promoted");
      expect(data).toHaveProperty("discarded");
      expect(typeof data.carryForward).toBe("number");
      expect(typeof data.promoted).toBe("number");
      expect(typeof data.discarded).toBe("number");
    });

    it("compacts old entries and returns non-zero results", async () => {
      const id = await storeMemory(db, mockEmbedSemantic, { content: "old entry for compaction" });
      // Make the entry old so it will be processed
      const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
      db.prepare(
        "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
      ).run(oldTime, oldTime, oldTime, id);

      const result = await handleExtraTool("compact_now", {}, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      const total = (data.carryForward as number) + (data.promoted as number) + (data.discarded as number);
      expect(total).toBe(1);
    });

    it("returns carryForwardIds, promotedIds, discardedIds arrays", async () => {
      const result = await handleExtraTool("compact_now", {}, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(Array.isArray(data.carryForwardIds)).toBe(true);
      expect(Array.isArray(data.promotedIds)).toBe(true);
      expect(Array.isArray(data.discardedIds)).toBe(true);
    });
  });

  describe("memory_inspect", () => {
    it("returns entry details and empty compaction history for new entry", async () => {
      const id = await storeMemory(db, mockEmbedSemantic, { content: "inspect me" });
      const result = await handleExtraTool("memory_inspect", { id }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("entry");
      expect(data).toHaveProperty("compaction_history");
      const entry = data.entry as Record<string, unknown>;
      expect(entry).not.toBeNull();
      expect(entry.tier).toBe("hot");
      expect(Array.isArray(data.compaction_history)).toBe(true);
      expect((data.compaction_history as unknown[]).length).toBe(0);
    });

    it("returns null entry for unknown id", async () => {
      const result = await handleExtraTool("memory_inspect", { id: "nonexistent-id" }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data.entry).toBeNull();
      expect(Array.isArray(data.compaction_history)).toBe(true);
    });

    it("includes compaction history after compaction", async () => {
      const id = await storeMemory(db, mockEmbedSemantic, { content: "compaction subject" });
      const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
      db.prepare(
        "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
      ).run(oldTime, oldTime, oldTime, id);

      await handleExtraTool("compact_now", {}, ctx);

      const result = await handleExtraTool("memory_inspect", { id }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(Array.isArray(data.compaction_history)).toBe(true);
      expect((data.compaction_history as unknown[]).length).toBeGreaterThan(0);
    });

    it("throws on missing id", async () => {
      await expect(handleExtraTool("memory_inspect", {}, ctx)).rejects.toThrow();
    });
  });

  describe("memory_history", () => {
    it("returns recent movements list", async () => {
      const result = await handleExtraTool("memory_history", {}, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("movements");
      expect(data).toHaveProperty("count");
      expect(Array.isArray(data.movements)).toBe(true);
    });

    it("returns movements after compaction", async () => {
      const id = await storeMemory(db, mockEmbedSemantic, { content: "movement history test" });
      const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
      db.prepare(
        "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
      ).run(oldTime, oldTime, oldTime, id);

      await handleExtraTool("compact_now", {}, ctx);

      const result = await handleExtraTool("memory_history", {}, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect((data.count as number)).toBeGreaterThan(0);
      const first = (data.movements as Array<Record<string, unknown>>)[0]!;
      expect(first).toHaveProperty("source_tier");
      expect(first).toHaveProperty("reason");
      expect(first).toHaveProperty("timestamp");
    });

    it("respects custom limit", async () => {
      // Insert 5 old entries and compact to generate 5 log entries
      for (let i = 0; i < 5; i++) {
        const id = await storeMemory(db, mockEmbedSemantic, { content: `old entry ${i} history limit` });
        const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
        db.prepare(
          "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
        ).run(oldTime, oldTime, oldTime, id);
      }
      await handleExtraTool("compact_now", {}, ctx);

      const result = await handleExtraTool("memory_history", { limit: 2 }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect((data.movements as unknown[]).length).toBeLessThanOrEqual(2);
    });
  });

  describe("memory_lineage", () => {
    it("returns lineage with just the id for an entry with no compaction history", async () => {
      const id = await storeMemory(db, mockEmbedSemantic, { content: "lineage test entry" });
      const result = await handleExtraTool("memory_lineage", { id }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("lineage");
      const lineage = data.lineage as Record<string, unknown>;
      expect(lineage.id).toBe(id);
      expect(lineage.sources).toBeUndefined();
    });

    it("throws on missing id", async () => {
      await expect(handleExtraTool("memory_lineage", {}, ctx)).rejects.toThrow();
    });

    it("returns lineage after compaction", async () => {
      const id = await storeMemory(db, mockEmbedSemantic, { content: "lineage after compaction" });
      const oldTime = Date.now() - 30 * 24 * 60 * 60 * 1000;
      db.prepare(
        "UPDATE hot_memories SET created_at = ?, last_accessed_at = ?, updated_at = ? WHERE id = ?",
      ).run(oldTime, oldTime, oldTime, id);

      await handleExtraTool("compact_now", {}, ctx);

      // Entry may have moved; lineage should still be queryable for the original id
      const result = await handleExtraTool("memory_lineage", { id }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("lineage");
    });
  });

  describe("memory_export and memory_import", () => {
    let exportDir: string;

    beforeEach(() => {
      exportDir = join(tmpdir(), "total-recall-test-" + randomUUID());
      mkdirSync(exportDir, { recursive: true });
      // Override TOTAL_RECALL_HOME to use temp dir
      process.env.TOTAL_RECALL_HOME = exportDir;
    });

    afterEach(() => {
      delete process.env.TOTAL_RECALL_HOME;
    });

    it("exports entries to a JSON file and returns path + count", async () => {
      await storeMemory(db, mockEmbedSemantic, { content: "export test entry 1" });
      await storeMemory(db, mockEmbedSemantic, { content: "export test entry 2", tier: "warm" });

      const result = await handleExtraTool("memory_export", {}, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);

      expect(data).toHaveProperty("path");
      expect(data).toHaveProperty("entry_count");
      expect(data).toHaveProperty("size_bytes");
      expect((data.entry_count as number)).toBeGreaterThanOrEqual(2);

      const exportPath = data.path as string;
      expect(existsSync(exportPath)).toBe(true);

      const contents = JSON.parse(readFileSync(exportPath, "utf-8")) as { version: number; entries: ExportEntry[] };
      expect(contents.version).toBe(1);
      expect(Array.isArray(contents.entries)).toBe(true);
      expect(contents.entries.length).toBe(data.entry_count);
    });

    it("exports only filtered tiers when tiers filter provided", async () => {
      await storeMemory(db, mockEmbedSemantic, { content: "hot entry for filter test" });
      await storeMemory(db, mockEmbedSemantic, { content: "warm entry for filter test", tier: "warm" });

      const result = await handleExtraTool("memory_export", { tiers: ["hot"] }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);

      const exportPath = data.path as string;
      const contents = JSON.parse(readFileSync(exportPath, "utf-8")) as { entries: ExportEntry[] };
      const tiers = new Set(contents.entries.map((e) => e.tier));
      expect(tiers.has("warm")).toBe(false);
      expect(tiers.has("hot")).toBe(true);
    });

    it("imports from an export file and skips duplicates", async () => {
      // First export
      const id1 = await storeMemory(db, mockEmbedSemantic, { content: "import test entry A" });
      const id2 = await storeMemory(db, mockEmbedSemantic, { content: "import test entry B" });

      const exportResult = await handleExtraTool("memory_export", {}, ctx);
      expect(exportResult).not.toBeNull();
      const exportData = parseResult(exportResult!);
      const exportPath = exportData.path as string;

      // Now create a fresh db to import into
      const importDb = createTestDb();
      const importCtx = makeCtx(importDb);

      const importResult = await handleExtraTool("memory_import", { path: exportPath }, importCtx);
      expect(importResult).not.toBeNull();
      const importData = parseResult(importResult!);

      expect(importData).toHaveProperty("imported");
      expect(importData).toHaveProperty("skipped");
      expect((importData.imported as number)).toBeGreaterThanOrEqual(2);

      // Import again — all should be skipped (same ids already in db)
      const reimportResult = await handleExtraTool("memory_import", { path: exportPath }, importCtx);
      expect(reimportResult).not.toBeNull();
      const reimportData = parseResult(reimportResult!);
      expect((reimportData.skipped as number)).toBeGreaterThanOrEqual(2);
      expect((reimportData.imported as number)).toBe(0);

      importDb.close();
      void id1;
      void id2;
    });

    it("returns error for missing file", async () => {
      const result = await handleExtraTool(
        "memory_import",
        { path: join(exportDir, "nonexistent.json") },
        ctx,
      );
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("error");
    });

    it("returns error for invalid JSON", async () => {
      const badPath = join(exportDir, "bad.json");
      writeFileSync(badPath, "not json", "utf-8");
      const result = await handleExtraTool("memory_import", { path: badPath }, ctx);
      expect(result).not.toBeNull();
      const data = parseResult(result!);
      expect(data).toHaveProperty("error");
    });
  });

  describe("unknown tool", () => {
    it("returns null for unknown tool name", async () => {
      const result = await handleExtraTool("nonexistent_tool", {}, ctx);
      expect(result).toBeNull();
    });
  });
});
