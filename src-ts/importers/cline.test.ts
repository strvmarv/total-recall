import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import { mockEmbedSemantic } from "../../tests-ts/helpers/embedding.js";
import { ClineImporter } from "./cline.js";
import { listEntries } from "../db/entries.js";

describe("ClineImporter", () => {
  let db: Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `cline-test-${randomUUID()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true });
    db.close();
  });

  it("detects Cline when data path exists", () => {
    const importer = new ClineImporter(tmpDir);
    expect(importer.detect()).toBe(true);
  });

  it("does not detect when directory missing", () => {
    const importer = new ClineImporter(join(tmpDir, "nonexistent"));
    expect(importer.detect()).toBe(false);
  });

  it("imports global rule files", async () => {
    // Create rules in the data path (acting as globalRulesPath replacement)
    // The importer uses ~/Documents/Cline/Rules/ by default, but we test
    // by placing rules where the importer will look
    const rulesDir = join(tmpDir, "rules");
    mkdirSync(rulesDir, { recursive: true });

    writeFileSync(join(rulesDir, "coding.md"), "Always write tests first.");
    writeFileSync(join(rulesDir, "style.txt"), "Use 2-space indentation.");
    writeFileSync(join(rulesDir, "ignore.json"), "This should be skipped.");

    // Create importer with custom paths — use a nonexistent data path
    // but set globalRulesPath to our test dir
    const importer = new (class extends ClineImporter {
      constructor() {
        super(tmpDir);
        // Override globalRulesPath via prototype trick
        Object.defineProperty(this, "globalRulesPath", { value: rulesDir });
      }
    })();

    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(2);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "warm", "knowledge");
    expect(entries).toHaveLength(2);
    expect(entries[0]!.source_tool).toBe("cline");
    expect(entries[0]!.tags).toContain("cline-rule");
  });

  it("imports task summaries from taskHistory.json", async () => {
    const stateDir = join(tmpDir, "state");
    mkdirSync(stateDir, { recursive: true });

    const tasks = [
      { id: "task-1", task: "Fix the login bug", modelId: "claude-3-opus", totalCost: 0.05, ts: 1700000000000 },
      { id: "task-2", task: "Add dark mode support", totalCost: 0.12, ts: 1700100000000 },
    ];

    writeFileSync(join(stateDir, "taskHistory.json"), JSON.stringify(tasks));

    const importer = new ClineImporter(tmpDir);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(2);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "cold", "knowledge");
    expect(entries).toHaveLength(2);
    expect(entries.some((e) => e.content.includes("Fix the login bug"))).toBe(true);
    expect(entries.some((e) => e.content.includes("claude-3-opus"))).toBe(true);
    expect(entries[0]!.tags).toContain("cline-task");
  });

  it("skips tasks with missing task or id fields", async () => {
    const stateDir = join(tmpDir, "state");
    mkdirSync(stateDir, { recursive: true });

    const tasks = [
      { id: "valid", task: "A real task" },
      { id: "", task: "No ID" },
      { id: "no-task", task: "" },
      { task: "Missing ID field" },
    ];

    writeFileSync(join(stateDir, "taskHistory.json"), JSON.stringify(tasks));

    const importer = new ClineImporter(tmpDir);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);
  });

  it("handles non-array taskHistory.json gracefully", async () => {
    const stateDir = join(tmpDir, "state");
    mkdirSync(stateDir, { recursive: true });

    writeFileSync(join(stateDir, "taskHistory.json"), JSON.stringify({ not: "an array" }));

    const importer = new ClineImporter(tmpDir);
    const result = await importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(0);
    expect(result.errors).toHaveLength(0);
  });

  it("deduplicates on re-import", async () => {
    const stateDir = join(tmpDir, "state");
    mkdirSync(stateDir, { recursive: true });

    writeFileSync(
      join(stateDir, "taskHistory.json"),
      JSON.stringify([{ id: "t1", task: "Build the feature" }]),
    );

    const importer = new ClineImporter(tmpDir);

    const first = await importer.importKnowledge(db, mockEmbedSemantic);
    expect(first.imported).toBe(1);

    const second = await importer.importKnowledge(db, mockEmbedSemantic);
    expect(second.imported).toBe(0);
    expect(second.skipped).toBe(1);
  });

  it("importMemories returns zero counts", async () => {
    const importer = new ClineImporter(tmpDir);
    const result = await importer.importMemories(db, mockEmbedSemantic);

    expect(result.imported).toBe(0);
    expect(result.skipped).toBe(0);
  });
});
