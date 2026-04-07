import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import { insertEntry } from "../db/entries.js";
import { searchByFts } from "./fts-search.js";

describe("FTS5 search", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("finds an entry by exact keyword match", () => {
    insertEntry(db, "warm", "memory", { content: "Use pnpm over npm for package management" });

    const results = searchByFts(db, "warm", "memory", "pnpm", { topK: 5 });
    expect(results.length).toBe(1);
    expect(results[0]!.score).toBeGreaterThan(0);
  });

  it("finds entries by partial phrase match", () => {
    insertEntry(db, "warm", "memory", { content: "Config file format is TOML" });
    insertEntry(db, "warm", "memory", { content: "Deploy to staging first" });

    const results = searchByFts(db, "warm", "memory", "TOML config", { topK: 5 });
    expect(results.length).toBe(1);
    expect(results[0]!.score).toBeGreaterThan(0);
  });

  it("returns results ordered by BM25 relevance", () => {
    insertEntry(db, "warm", "memory", { content: "TOML is used for configuration files in this project" });
    insertEntry(db, "warm", "memory", { content: "Config file format is TOML" });
    insertEntry(db, "warm", "memory", { content: "Deploy uses Docker containers" });

    const results = searchByFts(db, "warm", "memory", "TOML", { topK: 5 });
    expect(results.length).toBe(2);
    expect(results[0]!.score).toBeGreaterThanOrEqual(results[1]!.score);
  });

  it("respects topK limit", () => {
    for (let i = 0; i < 10; i++) {
      insertEntry(db, "warm", "memory", { content: `Entry ${i} about testing` });
    }

    const results = searchByFts(db, "warm", "memory", "testing", { topK: 3 });
    expect(results.length).toBe(3);
  });

  it("returns empty array when no match", () => {
    insertEntry(db, "warm", "memory", { content: "Hello world" });

    const results = searchByFts(db, "warm", "memory", "zzzznotaword", { topK: 5 });
    expect(results.length).toBe(0);
  });

  it("searches tags column too", () => {
    insertEntry(db, "warm", "memory", {
      content: "Some unrelated content",
      tags: ["sqlite", "database"],
    });

    const results = searchByFts(db, "warm", "memory", "sqlite", { topK: 5 });
    expect(results.length).toBe(1);
  });

  it("handles FTS5 special characters in query gracefully", () => {
    insertEntry(db, "warm", "memory", { content: "Use sqlite-vec for vectors" });

    const results = searchByFts(db, "warm", "memory", "sqlite-vec", { topK: 5 });
    expect(results.length).toBe(1);
  });
});
