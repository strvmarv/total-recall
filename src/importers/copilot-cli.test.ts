import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { writeFileSync, mkdirSync, rmSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import { CopilotCliImporter } from "./copilot-cli.js";
import { listEntries } from "../db/entries.js";

describe("CopilotCliImporter", () => {
  let db: Database.Database;
  let tmpDir: string;

  beforeEach(() => {
    db = createTestDb();
    tmpDir = join(tmpdir(), `copilot-cli-test-${randomUUID()}`);
    mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    rmSync(tmpDir, { recursive: true, force: true });
  });

  it("detects Copilot CLI installation", () => {
    mkdirSync(join(tmpDir, "session-state"), { recursive: true });
    const importer = new CopilotCliImporter(tmpDir);
    expect(importer.detect()).toBe(true);
  });

  it("does not detect when directory missing", () => {
    const importer = new CopilotCliImporter(join(tmpDir, "nonexistent"));
    expect(importer.detect()).toBe(false);
  });

  it("imports plan.md files as cold knowledge", () => {
    const sessionDir = join(tmpDir, "session-state", "session-abc123");
    mkdirSync(sessionDir, { recursive: true });

    writeFileSync(
      join(sessionDir, "plan.md"),
      "# Plan\n\n## Step 1\nRefactor the auth module.\n\n## Step 2\nWrite integration tests.",
    );

    const importer = new CopilotCliImporter(tmpDir);
    const result = importer.importKnowledge(db, mockEmbedSemantic);

    expect(result.imported).toBe(1);
    expect(result.skipped).toBe(0);
    expect(result.errors).toHaveLength(0);

    const entries = listEntries(db, "cold", "knowledge");
    expect(entries).toHaveLength(1);
    expect(entries[0]!.source_tool).toBe("copilot-cli");
  });
});
