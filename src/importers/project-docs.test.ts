import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { mkdtempSync, writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { ingestProjectDocs } from "./project-docs.js";

describe("ingestProjectDocs", () => {
  let db: Database.Database;
  let tempDir: string;

  beforeEach(() => {
    db = createTestDb();
    tempDir = mkdtempSync(join(tmpdir(), "tr-project-docs-"));
  });

  afterEach(() => {
    db.close();
    rmSync(tempDir, { recursive: true, force: true });
  });

  it("ingests README.md from project root", async () => {
    writeFileSync(join(tempDir, "README.md"), "# My Project\n\nThis is a test project.");
    const result = await ingestProjectDocs(db, mockEmbedSemantic, tempDir);
    expect(result.filesIngested).toBe(1);
    expect(result.totalChunks).toBeGreaterThan(0);
  });

  it("ingests markdown files from docs/ directory", async () => {
    mkdirSync(join(tempDir, "docs"));
    writeFileSync(join(tempDir, "docs", "guide.md"), "# Guide\n\nSome documentation.");
    writeFileSync(join(tempDir, "docs", "api.md"), "# API\n\nEndpoint docs.");
    const result = await ingestProjectDocs(db, mockEmbedSemantic, tempDir);
    expect(result.filesIngested).toBe(2);
  });

  it("skips already-ingested files on second call", async () => {
    writeFileSync(join(tempDir, "README.md"), "# My Project\n\nTest content.");
    const first = await ingestProjectDocs(db, mockEmbedSemantic, tempDir);
    expect(first.filesIngested).toBe(1);

    const second = await ingestProjectDocs(db, mockEmbedSemantic, tempDir);
    expect(second.filesIngested).toBe(0);
    expect(second.skipped).toBe(1);
  });

  it("returns zero for empty directory", async () => {
    const result = await ingestProjectDocs(db, mockEmbedSemantic, tempDir);
    expect(result.filesIngested).toBe(0);
    expect(result.totalChunks).toBe(0);
  });
});
