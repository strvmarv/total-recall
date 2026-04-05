import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { runSmokeTest, getMetaValue, setMetaValue } from "./smoke-test.js";
import type Database from "better-sqlite3";

describe("smoke-test", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("runs on first invocation (no version in _meta)", async () => {
    const result = await runSmokeTest(db, mockEmbedSemantic, "0.5.1");
    expect(result).not.toBeNull();
    expect(result!.passed).toBeDefined();
    expect(result!.exactMatchRate).toBeGreaterThanOrEqual(0);
    expect(result!.avgLatencyMs).toBeGreaterThanOrEqual(0);
  });

  it("skips when version matches", async () => {
    setMetaValue(db, "smoke_test_version", "0.5.1");
    const result = await runSmokeTest(db, mockEmbedSemantic, "0.5.1");
    expect(result).toBeNull();
  });

  it("runs when version differs", async () => {
    setMetaValue(db, "smoke_test_version", "0.5.0");
    const result = await runSmokeTest(db, mockEmbedSemantic, "0.5.1");
    expect(result).not.toBeNull();
  });

  it("writes version to _meta after running", async () => {
    await runSmokeTest(db, mockEmbedSemantic, "0.5.1");
    const version = getMetaValue(db, "smoke_test_version");
    expect(version).toBe("0.5.1");
  });

  it("sets passed=true when exactMatchRate >= 0.8", async () => {
    const result = await runSmokeTest(db, mockEmbedSemantic, "0.5.1");
    if (result) {
      expect(result.passed).toBe(result.exactMatchRate >= 0.8);
    }
  });
});

describe("_meta helpers", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("getMetaValue returns null for missing key", () => {
    expect(getMetaValue(db, "nonexistent")).toBeNull();
  });

  it("setMetaValue upserts", () => {
    setMetaValue(db, "key1", "val1");
    expect(getMetaValue(db, "key1")).toBe("val1");
    setMetaValue(db, "key1", "val2");
    expect(getMetaValue(db, "key1")).toBe("val2");
  });
});
