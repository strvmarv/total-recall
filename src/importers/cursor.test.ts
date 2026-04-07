import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import { Database } from "bun:sqlite";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { CursorImporter } from "./cursor.js";
import { listEntries } from "../db/entries.js";

describe("CursorImporter", () => {
  let db: Database;
  let tmpConfig: string;
  let tmpExt: string;

  beforeEach(() => {
    db = createTestDb();
    tmpConfig = join(tmpdir(), `cursor-config-test-${randomUUID()}`);
    tmpExt = join(tmpdir(), `cursor-ext-test-${randomUUID()}`);
    mkdirSync(tmpConfig, { recursive: true });
    mkdirSync(tmpExt, { recursive: true });
  });

  afterEach(() => {
    rmSync(tmpConfig, { recursive: true, force: true });
    rmSync(tmpExt, { recursive: true, force: true });
    db.close();
  });

  it("detects Cursor when config path exists", () => {
    const importer = new CursorImporter(tmpConfig, tmpExt);
    expect(importer.detect()).toBe(true);
  });

  it("does not detect when both paths missing", () => {
    const importer = new CursorImporter(
      join(tmpConfig, "nonexistent"),
      join(tmpExt, "nonexistent"),
    );
    expect(importer.detect()).toBe(false);
  });

  it("imports .cursorrules from workspace-discovered project", async () => {
    // Create a project dir with .cursorrules
    const projectDir = join(tmpdir(), `cursor-proj-${randomUUID()}`);
    mkdirSync(projectDir, { recursive: true });
    writeFileSync(
      join(projectDir, ".cursorrules"),
      "Always use TypeScript strict mode.\nPrefer const over let.",
    );

    // Create workspace.json pointing to that project
    const wsDir = join(tmpConfig, "User", "workspaceStorage", "abc123");
    mkdirSync(wsDir, { recursive: true });
    writeFileSync(
      join(wsDir, "workspace.json"),
      JSON.stringify({ folder: `file://${projectDir}` }),
    );

    const importer = new CursorImporter(tmpConfig, tmpExt);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "cold", "knowledge");
    expect(entries).toHaveLength(1);
    expect(entries[0]!.source_tool).toBe("cursor");
    expect(entries[0]!.tags).toContain("cursorrules");
    expect(entries[0]!.content).toContain("TypeScript strict mode");

    rmSync(projectDir, { recursive: true, force: true });
  });

  it("imports .cursor/rules/*.mdc files with frontmatter", async () => {
    const projectDir = join(tmpdir(), `cursor-proj-${randomUUID()}`);
    const rulesDir = join(projectDir, ".cursor", "rules");
    mkdirSync(rulesDir, { recursive: true });

    writeFileSync(
      join(rulesDir, "testing.mdc"),
      "---\ndescription: Testing conventions\n---\nUse vitest. Write integration tests.",
    );
    writeFileSync(
      join(rulesDir, "style.mdc"),
      "---\ndescription: Style guide\n---\nPrefer functional patterns.",
    );
    // Non-mdc file should be skipped
    writeFileSync(join(rulesDir, "notes.txt"), "This should be ignored.");

    const wsDir = join(tmpConfig, "User", "workspaceStorage", "def456");
    mkdirSync(wsDir, { recursive: true });
    writeFileSync(
      join(wsDir, "workspace.json"),
      JSON.stringify({ folder: `file://${projectDir}` }),
    );

    const importer = new CursorImporter(tmpConfig, tmpExt);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(2);

    const entries = listEntries(db, "cold", "knowledge");
    expect(entries).toHaveLength(2);
    expect(entries.every((e) => e.tags?.includes("cursor-rule"))).toBe(true);

    rmSync(projectDir, { recursive: true, force: true });
  });

  it("imports global rules from SQLite state.vscdb", async () => {
    const globalDir = join(tmpConfig, "User", "globalStorage");
    mkdirSync(globalDir, { recursive: true });

    const vscdb = new Database(join(globalDir, "state.vscdb"));
    vscdb.run("CREATE TABLE IF NOT EXISTS ItemTable (key TEXT PRIMARY KEY, value TEXT)");
    vscdb.query("INSERT INTO ItemTable (key, value) VALUES (?, ?)").run(
      "aicontext.personalContext",
      "Always explain your reasoning step by step.",
    );
    vscdb.close();

    const importer = new CursorImporter(tmpConfig, tmpExt);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);

    const entries = listEntries(db, "warm", "knowledge");
    expect(entries).toHaveLength(1);
    expect(entries[0]!.tags).toContain("global-rules");
    expect(entries[0]!.content).toContain("step by step");
  });

  it("handles malformed workspace.json gracefully", async () => {
    const wsDir = join(tmpConfig, "User", "workspaceStorage", "bad");
    mkdirSync(wsDir, { recursive: true });
    writeFileSync(join(wsDir, "workspace.json"), "not valid json{{{");

    const importer = new CursorImporter(tmpConfig, tmpExt);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(0);
    expect(result.errors).toHaveLength(0);
  });

  it("deduplicates on re-import", async () => {
    const globalDir = join(tmpConfig, "User", "globalStorage");
    mkdirSync(globalDir, { recursive: true });

    const vscdb = new Database(join(globalDir, "state.vscdb"));
    vscdb.run("CREATE TABLE IF NOT EXISTS ItemTable (key TEXT PRIMARY KEY, value TEXT)");
    vscdb.query("INSERT INTO ItemTable (key, value) VALUES (?, ?)").run(
      "aicontext.personalContext",
      "Be concise.",
    );
    vscdb.close();

    const importer = new CursorImporter(tmpConfig, tmpExt);

    const first = await importer.importKnowledge(db, mockEmbedSemantic);
    expect(first.imported).toBe(1);

    const second = await importer.importKnowledge(db, mockEmbedSemantic);
    expect(second.imported).toBe(0);
    expect(second.skipped).toBe(1);
  });

  it("importMemories returns zero counts", async () => {
    const importer = new CursorImporter(tmpConfig, tmpExt);
    const result = await importer.importMemories(db, mockEmbedSemantic);

    expect(result.imported).toBe(0);
    expect(result.skipped).toBe(0);
  });
});
