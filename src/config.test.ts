import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { mkdtempSync, rmSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { createTestDb } from "../tests/helpers/db.js";
import type { Database } from "bun:sqlite";

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
  let db: Database;

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

describe("getDbPath", () => {
  const ORIGINAL_DB_PATH = process.env.TOTAL_RECALL_DB_PATH;
  const ORIGINAL_HOME = process.env.TOTAL_RECALL_HOME;
  let fakeHomeDir: string;

  beforeEach(() => {
    // Pin TOTAL_RECALL_HOME so the "default" path is predictable.
    fakeHomeDir = mkdtempSync(join(tmpdir(), "tr-getdbpath-home-"));
    process.env.TOTAL_RECALL_HOME = fakeHomeDir;
  });

  afterEach(() => {
    if (ORIGINAL_DB_PATH === undefined) delete process.env.TOTAL_RECALL_DB_PATH;
    else process.env.TOTAL_RECALL_DB_PATH = ORIGINAL_DB_PATH;
    if (ORIGINAL_HOME === undefined) delete process.env.TOTAL_RECALL_HOME;
    else process.env.TOTAL_RECALL_HOME = ORIGINAL_HOME;
    rmSync(fakeHomeDir, { recursive: true, force: true });
  });

  it("returns the default path when TOTAL_RECALL_DB_PATH is unset", async () => {
    const { getDbPath } = await import("./config.js");
    delete process.env.TOTAL_RECALL_DB_PATH;
    expect(getDbPath()).toBe(join(fakeHomeDir, "total-recall.db"));
  });

  it("returns the default path when TOTAL_RECALL_DB_PATH is empty string", async () => {
    const { getDbPath } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "";
    expect(getDbPath()).toBe(join(fakeHomeDir, "total-recall.db"));
  });

  it("returns the default path when TOTAL_RECALL_DB_PATH is whitespace only", async () => {
    const { getDbPath } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "   ";
    expect(getDbPath()).toBe(join(fakeHomeDir, "total-recall.db"));
  });

  it("returns an absolute POSIX path unchanged", async () => {
    const { getDbPath } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "/tmp/custom.db";
    expect(getDbPath()).toBe("/tmp/custom.db");
  });

  it("expands ~/ prefix to the user home directory", async () => {
    const { getDbPath } = await import("./config.js");
    const { homedir } = await import("node:os");
    process.env.TOTAL_RECALL_DB_PATH = "~/custom.db";
    expect(getDbPath()).toBe(join(homedir(), "custom.db"));
  });

  it("expands multi-segment ~/ paths", async () => {
    const { getDbPath } = await import("./config.js");
    const { homedir } = await import("node:os");
    process.env.TOTAL_RECALL_DB_PATH = "~/a/b/c.db";
    expect(getDbPath()).toBe(join(homedir(), "a/b/c.db"));
  });

  it("rejects bare ~ (no filename)", async () => {
    const { getDbPath, SqliteDbPathError } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "~";
    expect(() => getDbPath()).toThrow(SqliteDbPathError);
    expect(() => getDbPath()).toThrow(/must be a file path, not a directory/);
    expect(() => getDbPath()).toThrow(/"~"/);
  });

  it("rejects a relative path starting with ./", async () => {
    const { getDbPath, SqliteDbPathError } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "./rel.db";
    expect(() => getDbPath()).toThrow(SqliteDbPathError);
    expect(() => getDbPath()).toThrow(/must be absolute or start with ~\//);
    expect(() => getDbPath()).toThrow(/"\.\/rel\.db"/);
  });

  it("rejects a bare relative filename", async () => {
    const { getDbPath, SqliteDbPathError } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "rel.db";
    expect(() => getDbPath()).toThrow(SqliteDbPathError);
    expect(() => getDbPath()).toThrow(/must be absolute or start with ~\//);
    expect(() => getDbPath()).toThrow(/"rel\.db"/);
  });

  it("rejects a POSIX trailing slash", async () => {
    const { getDbPath, SqliteDbPathError } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "/tmp/dir/";
    expect(() => getDbPath()).toThrow(SqliteDbPathError);
    expect(() => getDbPath()).toThrow(/must be a file path, not a directory/);
    expect(() => getDbPath()).toThrow(/"\/tmp\/dir\/"/);
  });

  it("rejects a trailing backslash even on POSIX (Windows path pasted on Linux/Mac)", async () => {
    const { getDbPath, SqliteDbPathError } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "C:\\Data\\tr.db\\";
    expect(() => getDbPath()).toThrow(SqliteDbPathError);
    expect(() => getDbPath()).toThrow(/must be a file path, not a directory/);
  });

  it.runIf(process.platform === "win32")(
    "accepts a Windows absolute path with drive letter",
    async () => {
      const { getDbPath } = await import("./config.js");
      process.env.TOTAL_RECALL_DB_PATH = "C:\\Data\\tr.db";
      expect(getDbPath()).toBe("C:\\Data\\tr.db");
    },
  );

  it("is idempotent: repeat calls with env var unchanged return the same value", async () => {
    const { getDbPath } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "/tmp/stable.db";
    const first = getDbPath();
    const second = getDbPath();
    const third = getDbPath();
    expect(first).toBe("/tmp/stable.db");
    expect(second).toBe(first);
    expect(third).toBe(first);
  });

  it("error messages echo the raw env value for debuggability", async () => {
    const { getDbPath } = await import("./config.js");
    process.env.TOTAL_RECALL_DB_PATH = "not/absolute/path.db";
    try {
      getDbPath();
      expect.fail("expected SqliteDbPathError");
    } catch (e) {
      expect((e as Error).message).toContain("not/absolute/path.db");
    }
  });
});
