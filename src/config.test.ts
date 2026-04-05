import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { mkdtempSync, rmSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { createTestDb } from "../tests/helpers/db.js";
import type Database from "better-sqlite3";

describe("config persistence", () => {
  let tempDir: string;
  const originalHome = process.env.TOTAL_RECALL_HOME;

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), "tr-config-test-"));
    process.env.TOTAL_RECALL_HOME = tempDir;
  });

  afterEach(() => {
    process.env.TOTAL_RECALL_HOME = originalHome;
    rmSync(tempDir, { recursive: true, force: true });
  });

  it("setNestedKey creates nested structure from dot notation", async () => {
    const { setNestedKey } = await import("./config.js");
    const result = setNestedKey({}, "tiers.warm.similarity_threshold", 0.7);
    expect(result).toEqual({
      tiers: { warm: { similarity_threshold: 0.7 } },
    });
  });

  it("setNestedKey merges with existing keys", async () => {
    const { setNestedKey } = await import("./config.js");
    const existing = { tiers: { warm: { max_entries: 100 } } };
    const result = setNestedKey(existing, "tiers.warm.similarity_threshold", 0.7);
    expect(result).toEqual({
      tiers: { warm: { max_entries: 100, similarity_threshold: 0.7 } },
    });
  });

  it("saveUserConfig writes valid TOML that round-trips through loadConfig", async () => {
    const { saveUserConfig, loadConfig } = await import("./config.js");

    saveUserConfig({ tiers: { warm: { similarity_threshold: 0.8 } } });

    const configPath = join(tempDir, "config.toml");
    const raw = readFileSync(configPath, "utf-8");
    expect(raw).toContain("similarity_threshold");

    const loaded = loadConfig();
    expect(loaded.tiers.warm.similarity_threshold).toBe(0.8);
  });

  it("saveUserConfig merges with existing user config", async () => {
    const { saveUserConfig, loadConfig } = await import("./config.js");

    saveUserConfig({ tiers: { warm: { similarity_threshold: 0.6 } } });
    saveUserConfig({ tiers: { warm: { retrieval_top_k: 10 } } });

    const loaded = loadConfig();
    expect(loaded.tiers.warm.similarity_threshold).toBe(0.6);
    expect(loaded.tiers.warm.retrieval_top_k).toBe(10);
  });
});

describe("createConfigSnapshot", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("creates a snapshot and returns its ID", async () => {
    const { createConfigSnapshot } = await import("./config.js");
    const config = { tiers: { warm: { similarity_threshold: 0.5 } } };
    const id = createConfigSnapshot(db, config, "test-snap");
    expect(id).toBeTruthy();
    expect(typeof id).toBe("string");

    const row = db.prepare("SELECT * FROM config_snapshots WHERE id = ?").get(id) as any;
    expect(row.name).toBe("test-snap");
    expect(JSON.parse(row.config)).toEqual(config);
    expect(row.timestamp).toBeGreaterThan(0);
  });

  it("deduplicates when config has not changed", async () => {
    const { createConfigSnapshot } = await import("./config.js");
    const config = { tiers: { warm: { similarity_threshold: 0.5 } } };
    const id1 = createConfigSnapshot(db, config, "snap-1");
    const id2 = createConfigSnapshot(db, config, "snap-2");
    expect(id2).toBe(id1);

    const rows = db.prepare("SELECT * FROM config_snapshots").all();
    expect(rows.length).toBe(1);
  });

  it("creates new snapshot when config changes", async () => {
    const { createConfigSnapshot } = await import("./config.js");
    const id1 = createConfigSnapshot(db, { a: 1 }, "snap-1");
    const id2 = createConfigSnapshot(db, { a: 2 }, "snap-2");
    expect(id2).not.toBe(id1);

    const rows = db.prepare("SELECT * FROM config_snapshots").all();
    expect(rows.length).toBe(2);
  });
});
