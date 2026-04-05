import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { handleKbTool } from "./kb-tools.js";
import { createCollection, addDocumentToCollection } from "../ingestion/hierarchical-index.js";
import { getEntry, updateEntry } from "../db/entries.js";
import type { ToolContext } from "./registry.js";
import type { TotalRecallConfig } from "../types.js";

const TEST_CONFIG: TotalRecallConfig = {
  tiers: {
    hot: { max_entries: 100, token_budget: 10000, carry_forward_threshold: 0.7 },
    warm: { max_entries: 500, retrieval_top_k: 10, similarity_threshold: 0.5, cold_decay_days: 30 },
    cold: { chunk_max_tokens: 512, chunk_overlap_tokens: 50, lazy_summary_threshold: 3 },
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
    embed: (text: string) => mockEmbedSemantic(text),
  };
}

function makeCtx(db: Database.Database): ToolContext {
  return {
    db,
    config: TEST_CONFIG,
    embedder: makeMockEmbedder() as unknown as ToolContext["embedder"],
    sessionId: "test-session",
  };
}

describe("kb_summarize", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("stores summary on collection entry", async () => {
    const collId = await createCollection(db, mockEmbedSemantic, {
      name: "test-collection",
      sourcePath: "/tmp/test",
    });

    const result = await handleKbTool("kb_summarize", {
      collection: collId,
      summary: "This collection covers authentication and authorization patterns.",
    }, makeCtx(db));

    expect(result).not.toBeNull();
    const parsed = JSON.parse(result!.content[0]!.text);
    expect(parsed.summarized).toBe(true);

    const entry = getEntry(db, "cold", "knowledge", collId);
    expect(entry?.summary).toBe("This collection covers authentication and authorization patterns.");
  });

  it("returns error for non-existent collection", async () => {
    const result = await handleKbTool("kb_summarize", {
      collection: "nonexistent-id",
      summary: "test",
    }, makeCtx(db));

    const parsed = JSON.parse(result!.content[0]!.text);
    expect(parsed.error).toContain("not found");
  });
});

describe("kb_search needsSummary", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("returns needsSummary true when access threshold reached", async () => {
    const collId = await createCollection(db, mockEmbedSemantic, {
      name: "auth-docs",
      sourcePath: "/tmp/auth",
    });

    await addDocumentToCollection(db, mockEmbedSemantic, {
      collectionId: collId,
      sourcePath: "/tmp/auth/oauth.md",
      chunks: [{ content: "OAuth2 authorization code flow" }],
    });

    // Set access_count just below threshold (threshold is 3 in test config)
    db.prepare(`UPDATE cold_knowledge SET access_count = 2 WHERE id = ?`).run(collId);

    // This search should push access_count to 3 and trigger needsSummary
    const result = await handleKbTool("kb_search", {
      query: "OAuth authentication",
      collection: collId,
    }, makeCtx(db));

    const parsed = JSON.parse(result!.content[0]!.text);
    expect(parsed.needsSummary).toBe(true);
  });

  it("returns needsSummary false when collection has summary", async () => {
    const collId = await createCollection(db, mockEmbedSemantic, {
      name: "api-docs",
      sourcePath: "/tmp/api",
    });

    await addDocumentToCollection(db, mockEmbedSemantic, {
      collectionId: collId,
      sourcePath: "/tmp/api/rest.md",
      chunks: [{ content: "REST API design patterns" }],
    });

    // Set high access count but also set a summary
    db.prepare(`UPDATE cold_knowledge SET access_count = 10 WHERE id = ?`).run(collId);
    updateEntry(db, "cold", "knowledge", collId, { summary: "API documentation collection" });

    const result = await handleKbTool("kb_search", {
      query: "REST API",
      collection: collId,
    }, makeCtx(db));

    const parsed = JSON.parse(result!.content[0]!.text);
    expect(parsed.needsSummary).toBe(false);
  });
});
