import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { mkdtempSync, rmSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";

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
