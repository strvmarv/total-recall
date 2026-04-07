import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { mkdtempSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { createTestDb } from "../../tests/helpers/db.js";
import type { Database } from "bun:sqlite";
import type { ToolContext } from "./registry.js";
import { loadConfig } from "../config.js";
import { Embedder } from "../embedding/embedder.js";

describe("config_set snapshot", () => {
  let db: Database;
  let tempDir: string;
  const originalHome = process.env.TOTAL_RECALL_HOME;

  beforeEach(() => {
    db = createTestDb();
    tempDir = mkdtempSync(join(tmpdir(), "tr-config-set-test-"));
    process.env.TOTAL_RECALL_HOME = tempDir;
  });

  afterEach(() => {
    db.close();
    process.env.TOTAL_RECALL_HOME = originalHome;
    rmSync(tempDir, { recursive: true, force: true });
  });

  it("snapshots the old config before writing new value", async () => {
    const { handleSystemTool } = await import("./system-tools.js");
    const config = loadConfig();
    const ctx: ToolContext = {
      db,
      config,
      embedder: new Embedder(config.embedding),
      sessionId: "test-session",
      configSnapshotId: "default",
      sessionInitialized: false,
      sessionInitResult: null,
      sessionInitPromise: null,
    };

    handleSystemTool("config_set", { key: "tiers.warm.similarity_threshold", value: 0.9 }, ctx);

    const rows = db.prepare("SELECT * FROM config_snapshots ORDER BY timestamp ASC").all() as any[];
    expect(rows.length).toBeGreaterThanOrEqual(1);
    // The snapshot should contain the OLD config (before the change)
    const snapshotConfig = JSON.parse(rows[0].config);
    expect(snapshotConfig.tiers.warm.similarity_threshold).not.toBe(0.9);
    expect(rows[0].name).toContain("pre-change:");
  });
});
