import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import type Database from "better-sqlite3";
import type { ToolContext } from "./registry.js";
import { loadConfig } from "../config.js";
import { Embedder } from "../embedding/embedder.js";

describe("session_start config snapshot", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("creates a config snapshot and sets it on context", async () => {
    const { handleSessionTool } = await import("./session-tools.js");
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

    await handleSessionTool("session_start", {}, ctx);

    // Should have updated context
    expect(ctx.configSnapshotId).not.toBe("default");

    // Should have a row in config_snapshots
    const row = db.prepare("SELECT * FROM config_snapshots WHERE id = ?").get(ctx.configSnapshotId) as any;
    expect(row).toBeTruthy();
    expect(row.name).toBe("session-start");
  });
});
