import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { ClaudeCodeImporter } from "./claude-code.js";
import { listEntries } from "../db/entries.js";

describe("ClaudeCodeImporter", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `claude-code-test-${randomUUID()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("detects Claude Code installation", () => {
    mkdirSync(join(tmpDir, "projects"), { recursive: true });
    const importer = new ClaudeCodeImporter(tmpDir);
    expect(importer.detect()).toBe(true);
  });

  it("does not detect when directory missing", () => {
    const importer = new ClaudeCodeImporter(join(tmpDir, "nonexistent"));
    expect(importer.detect()).toBe(false);
  });

  it("imports memory files with YAML frontmatter", () => {
    const projectDir = join(tmpDir, "projects", "my-project");
    const memoryDir = join(projectDir, "memory");
    mkdirSync(memoryDir, { recursive: true });

    writeFileSync(
      join(memoryDir, "pref1.md"),
      "---\nname: dark-mode\ndescription: User prefers dark mode\ntype: user\n---\nAlways use dark mode themes.",
    );
    writeFileSync(
      join(memoryDir, "pref2.md"),
      "---\nname: ts-strict\ndescription: Use strict TypeScript\ntype: feedback\n---\nEnable strict mode in tsconfig.",
    );
    // MEMORY.md should be skipped
    writeFileSync(join(memoryDir, "MEMORY.md"), "This is the MEMORY.md file, skip it.");

    const importer = new ClaudeCodeImporter(tmpDir);
    const result = importer.importMemories(db, mockEmbedSemantic);

    expect(result.imported).toBe(2);
    expect(result.skipped).toBe(0);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "warm", "memory");
    expect(entries).toHaveLength(2);
  });

  it("imports CLAUDE.md as pinned knowledge", () => {
    mkdirSync(join(tmpDir, "projects"), { recursive: true });
    writeFileSync(
      join(tmpDir, "CLAUDE.md"),
      "# Global Instructions\n\nAlways write tests.\nNever skip type checks.",
    );

    const importer = new ClaudeCodeImporter(tmpDir);
    const result = importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);
    expect(result.skipped).toBe(0);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "warm", "knowledge");
    expect(entries).toHaveLength(1);
    expect(entries[0]!.tags).toContain("pinned");
    expect(entries[0]!.source_tool).toBe("claude-code");
  });

  it("deduplicates on re-import", () => {
    const projectDir = join(tmpDir, "projects", "proj");
    const memoryDir = join(projectDir, "memory");
    mkdirSync(memoryDir, { recursive: true });

    writeFileSync(
      join(memoryDir, "note.md"),
      "---\nname: test-note\ntype: user\n---\nRemember this fact.",
    );

    const importer = new ClaudeCodeImporter(tmpDir);

    const first = importer.importMemories(db, mockEmbedSemantic);
    expect(first.imported).toBe(1);
    expect(first.skipped).toBe(0);

    const second = importer.importMemories(db, mockEmbedSemantic);
    expect(second.imported).toBe(0);
    expect(second.skipped).toBe(1);

    const entries = listEntries(db, "warm", "memory");
    expect(entries).toHaveLength(1);
  });
});
