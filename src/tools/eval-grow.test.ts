import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { writeCandidates } from "../eval/benchmark-candidates.js";
import { handleEvalTool } from "./eval-tools.js";
import { loadConfig } from "../config.js";
import { Embedder } from "../embedding/embedder.js";
import type { Database } from "bun:sqlite";
import type { ToolContext } from "./registry.js";

// Mock fs to prevent writing to the real retrieval.jsonl during tests
vi.mock("node:fs", async (importOriginal) => {
  const actual = await importOriginal<typeof import("node:fs")>();
  return {
    ...actual,
    readFileSync: vi.fn(() => ""),
    writeFileSync: vi.fn(),
  };
});

function makeCtx(db: Database): ToolContext {
  const config = loadConfig();
  return {
    db,
    config,
    embedder: new Embedder(config.embedding),
    sessionId: "test",
    configSnapshotId: "snap1",
    sessionInitialized: true,
    sessionInitResult: null,
    sessionInitPromise: null,
  };
}

describe("eval_grow tool", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
    writeCandidates(db, [
      { query: "q1", topScore: 0.3, timestamp: 1000 },
      { query: "q2", topScore: 0.2, timestamp: 2000 },
    ], [
      { query: "q1", topContent: null, topEntryId: "e1" },
      { query: "q2", topContent: null, topEntryId: "e2" },
    ]);
  });

  afterEach(() => {
    db.close();
  });

  it("list mode returns pending candidates", async () => {
    const result = await handleEvalTool("eval_grow", { action: "list" }, makeCtx(db));
    expect(result).not.toBeNull();
    const data = JSON.parse(result!.content[0]!.text);
    expect(data.candidates).toHaveLength(2);
  });

  it("defaults to list mode when no action provided", async () => {
    const result = await handleEvalTool("eval_grow", {}, makeCtx(db));
    expect(result).not.toBeNull();
    const data = JSON.parse(result!.content[0]!.text);
    expect(data.candidates).toHaveLength(2);
  });

  it("resolve mode accepts and rejects candidates", async () => {
    const listResult = await handleEvalTool("eval_grow", { action: "list" }, makeCtx(db));
    const { candidates } = JSON.parse(listResult!.content[0]!.text);
    const id1 = candidates[0].id;
    const id2 = candidates[1].id;

    const result = await handleEvalTool("eval_grow", {
      action: "resolve",
      accept: [id1],
      reject: [id2],
    }, makeCtx(db));

    const data = JSON.parse(result!.content[0]!.text);
    expect(data.accepted).toBe(1);
    expect(data.rejected).toBe(1);
  });

  it("resolve mode errors when no IDs provided", async () => {
    const result = await handleEvalTool("eval_grow", {
      action: "resolve",
    }, makeCtx(db));
    const data = JSON.parse(result!.content[0]!.text);
    expect(data.error).toBeDefined();
  });
});
