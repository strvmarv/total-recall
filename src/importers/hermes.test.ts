import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { HermesImporter } from "./hermes.js";
import { listEntries } from "../db/entries.js";

describe("HermesImporter", () => {
  let db: Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `hermes-test-${randomUUID()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true });
    db.close();
  });

  it("detects Hermes when memories directory exists", () => {
    mkdirSync(join(tmpDir, "memories"), { recursive: true });
    const importer = new HermesImporter(tmpDir);
    expect(importer.detect()).toBe(true);
  });

  it("does not detect when directory missing", () => {
    const importer = new HermesImporter(join(tmpDir, "nonexistent"));
    expect(importer.detect()).toBe(false);
  });

  it("imports MEMORY.md entries split on § delimiter", async () => {
    const memoriesDir = join(tmpDir, "memories");
    mkdirSync(memoriesDir, { recursive: true });

    writeFileSync(
      join(memoriesDir, "MEMORY.md"),
      "User prefers dark mode\n§\nProject uses vitest for testing\n§\nAlways run typecheck before commit",
    );

    const importer = new HermesImporter(tmpDir);
    const result = await importer.importMemories(db, mockEmbedSemantic);

    expect(result.imported).toBe(3);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "warm", "memory");
    expect(entries).toHaveLength(3);
    expect(entries.some((e) => e.content.includes("dark mode"))).toBe(true);
    expect(entries[0]!.source_tool).toBe("hermes");
    expect(entries[0]!.tags).toContain("hermes-memory");
  });

  it("imports USER.md entries with user-profile tag", async () => {
    const memoriesDir = join(tmpDir, "memories");
    mkdirSync(memoriesDir, { recursive: true });

    writeFileSync(
      join(memoriesDir, "USER.md"),
      "Name: Paul\n§\nRole: Senior engineer",
    );

    const importer = new HermesImporter(tmpDir);
    const result = await importer.importMemories(db, mockEmbedSemantic);

    expect(result.imported).toBe(2);

    const entries = listEntries(db, "warm", "memory");
    expect(entries).toHaveLength(2);
    expect(entries[0]!.tags).toContain("hermes-user");
    expect(entries[0]!.tags).toContain("user-profile");
  });

  it("handles empty memory file", async () => {
    const memoriesDir = join(tmpDir, "memories");
    mkdirSync(memoriesDir, { recursive: true });
    writeFileSync(join(memoriesDir, "MEMORY.md"), "");

    const importer = new HermesImporter(tmpDir);
    const result = await importer.importMemories(db, mockEmbedSemantic);

    expect(result.imported).toBe(0);
    expect(result.errors).toHaveLength(0);
  });

  it("imports SOUL.md as warm knowledge", async () => {
    writeFileSync(
      join(tmpDir, "SOUL.md"),
      "You are a helpful coding assistant who values clarity.",
    );

    const importer = new HermesImporter(tmpDir);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);

    const entries = listEntries(db, "warm", "knowledge");
    expect(entries).toHaveLength(1);
    expect(entries[0]!.tags).toContain("hermes-soul");
    expect(entries[0]!.content).toContain("helpful coding assistant");
  });

  it("imports skill files from skills/<name>/SKILL.md", async () => {
    const skillDir = join(tmpDir, "skills", "code-review");
    mkdirSync(skillDir, { recursive: true });
    writeFileSync(
      join(skillDir, "SKILL.md"),
      "---\nname: code-review\ndescription: Review code for bugs\n---\nCheck for common patterns.",
    );

    const importer = new HermesImporter(tmpDir);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);

    const entries = listEntries(db, "cold", "knowledge");
    expect(entries).toHaveLength(1);
    expect(entries[0]!.tags).toContain("hermes-skill");
    expect(entries[0]!.tags).toContain("code-review");
  });

  it("deduplicates memory entries on re-import", async () => {
    const memoriesDir = join(tmpDir, "memories");
    mkdirSync(memoriesDir, { recursive: true });
    writeFileSync(join(memoriesDir, "MEMORY.md"), "Single fact to remember");

    const importer = new HermesImporter(tmpDir);

    const first = await importer.importMemories(db, mockEmbedSemantic);
    expect(first.imported).toBe(1);

    const second = await importer.importMemories(db, mockEmbedSemantic);
    expect(second.imported).toBe(0);
    expect(second.skipped).toBe(1);

    const entries = listEntries(db, "warm", "memory");
    expect(entries).toHaveLength(1);
  });
});
