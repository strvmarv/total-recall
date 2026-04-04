import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { ingestFile, ingestDirectory } from "./ingest.js";
import { countEntries } from "../db/entries.js";
import { createCollection } from "./hierarchical-index.js";

describe("ingestion", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `ingest-test-${randomUUID()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("ingests a single markdown file", () => {
    writeFileSync(
      join(tmpDir, "readme.md"),
      "# My Project\n\n## Setup\n\nInstall with pnpm.\n\n## Usage\n\nRun the thing.",
    );

    const result = ingestFile(db, mockEmbedSemantic, join(tmpDir, "readme.md"));

    expect(result.documentId).toBeDefined();
    expect(result.chunkCount).toBeGreaterThan(0);
    // doc + chunks + collection
    expect(countEntries(db, "cold", "knowledge")).toBeGreaterThan(1);
  });

  it("ingests a directory into a collection", () => {
    writeFileSync(join(tmpDir, "auth.md"), "# Auth\n\nAuth docs here.");
    writeFileSync(join(tmpDir, "deploy.md"), "# Deploy\n\nDeploy docs here.");
    mkdirSync(join(tmpDir, "sub"));
    writeFileSync(join(tmpDir, "sub", "nested.md"), "# Nested\n\nNested doc.");

    const result = ingestDirectory(db, mockEmbedSemantic, tmpDir);

    expect(result.collectionId).toBeDefined();
    expect(result.documentCount).toBe(3);
  });

  it("validates ingested content with self-match test", () => {
    writeFileSync(
      join(tmpDir, "api.md"),
      "# API Reference\n\n## Endpoints\n\nGET /users returns a list.",
    );

    const result = ingestFile(db, mockEmbedSemantic, join(tmpDir, "api.md"));

    expect(result.validationPassed).toBe(true);
  });

  it("creates a collection from dirname when no collectionId given", () => {
    writeFileSync(join(tmpDir, "doc.md"), "# Doc\n\nSome content.");

    const result = ingestFile(db, mockEmbedSemantic, join(tmpDir, "doc.md"));

    expect(result.documentId).toBeDefined();
    // Collection entry + document entry + at least 1 chunk
    expect(countEntries(db, "cold", "knowledge")).toBeGreaterThanOrEqual(3);
  });

  it("uses provided collectionId instead of creating a new one", () => {
    const collId = createCollection(db, mockEmbedSemantic, {
      name: "my-coll",
      sourcePath: tmpDir,
    });

    writeFileSync(join(tmpDir, "page.md"), "# Page\n\nPage content.");
    const result = ingestFile(db, mockEmbedSemantic, join(tmpDir, "page.md"), collId);

    expect(result.documentId).toBeDefined();
    // Only 1 collection entry (the one we created)
    const allRows = db
      .prepare(`SELECT * FROM cold_knowledge WHERE json_extract(metadata, '$.type') = 'collection'`)
      .all() as unknown[];
    expect(allRows).toHaveLength(1);
  });

  it("reports totalChunks for directory ingestion", () => {
    writeFileSync(join(tmpDir, "a.md"), "# A\n\n## Section 1\n\nContent here.\n\n## Section 2\n\nMore content.");
    writeFileSync(join(tmpDir, "b.md"), "# B\n\nSingle section.");

    const result = ingestDirectory(db, mockEmbedSemantic, tmpDir);

    expect(result.totalChunks).toBeGreaterThan(0);
  });

  it("skips hidden directories when walking", () => {
    writeFileSync(join(tmpDir, "visible.md"), "# Visible\n\nContent.");
    mkdirSync(join(tmpDir, ".hidden"));
    writeFileSync(join(tmpDir, ".hidden", "secret.md"), "# Secret\n\nContent.");

    const result = ingestDirectory(db, mockEmbedSemantic, tmpDir);

    expect(result.documentCount).toBe(1);
  });
});
