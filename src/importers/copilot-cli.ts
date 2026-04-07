import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import type { Database } from "bun:sqlite";
import type { HostImporter, ImportResult, EmbedFn } from "./importer.js";
import { contentHash, isAlreadyImported, logImport } from "./import-utils.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { Tier, ContentType } from "../types.js";

export class CopilotCliImporter implements HostImporter {
  readonly name = "copilot-cli";
  private readonly basePath: string;

  constructor(basePath?: string) {
    this.basePath = basePath ?? join(homedir(), ".copilot");
  }

  detect(): boolean {
    return existsSync(this.basePath) && existsSync(join(this.basePath, "session-state"));
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    let knowledgeFiles = 0;
    let sessionFiles = 0;

    const sessionStateDir = join(this.basePath, "session-state");
    if (!existsSync(sessionStateDir)) {
      return { memoryFiles: 0, knowledgeFiles, sessionFiles };
    }

    for (const entry of readdirSync(sessionStateDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const sessionDir = join(sessionStateDir, entry.name);

      if (existsSync(join(sessionDir, "plan.md"))) knowledgeFiles++;

      for (const f of readdirSync(sessionDir)) {
        if (f.endsWith(".jsonl")) sessionFiles++;
      }
    }

    return { memoryFiles: 0, knowledgeFiles, sessionFiles };
  }

  async importMemories(_db: Database, _embed: EmbedFn, _project?: string): Promise<ImportResult> {
    return { imported: 0, skipped: 0, errors: [] };
  }

  async importKnowledge(db: Database, embed: EmbedFn): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    const sessionStateDir = join(this.basePath, "session-state");
    if (!existsSync(sessionStateDir)) return result;

    for (const entry of readdirSync(sessionStateDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const planPath = join(sessionStateDir, entry.name, "plan.md");

      if (!existsSync(planPath)) continue;

      try {
        const raw = readFileSync(planPath, "utf8");
        const hash = contentHash(raw);

        if (isAlreadyImported(db, hash)) {
          result.skipped++;
          continue;
        }

        const entryId = insertEntry(db, "cold", "knowledge", {
          content: raw,
          source: planPath,
          source_tool: "copilot-cli",
        });

        insertEmbedding(db, "cold", "knowledge", entryId, await embed(raw));
        logImport(db, "copilot-cli", planPath, hash, entryId, "cold", "knowledge");
        result.imported++;
      } catch (err) {
        result.errors.push(`${planPath}: ${err instanceof Error ? err.message : String(err)}`);
      }
    }

    return result;
  }
}
