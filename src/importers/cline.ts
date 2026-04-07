import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import type { Database } from "bun:sqlite";
import type { HostImporter, ImportResult, EmbedFn } from "./importer.js";
import { contentHash, isAlreadyImported, logImport } from "./import-utils.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

interface ClineTaskHistoryItem {
  id: string;
  task: string;
  tokensIn?: number;
  tokensOut?: number;
  totalCost?: number;
  ts?: number;
  modelId?: string;
}

/**
 * Cline stores:
 * - New shared storage: ~/.cline/data/ (v3.x+)
 * - Legacy VS Code: ~/.config/Code/User/globalStorage/saoudrizwan.claude-dev/
 * - Rules: .clinerules/ per-project, ~/Documents/Cline/Rules/ global
 * - Task history: state/taskHistory.json (index), tasks/<id>/ (conversations)
 *
 * We import: rules (as knowledge) and task summaries (as cold knowledge).
 * We skip full conversation logs — they're too large and noisy.
 */
export class ClineImporter implements HostImporter {
  readonly name = "cline";
  private readonly dataPath: string;
  private readonly legacyPath: string;
  private readonly globalRulesPath: string;

  constructor(dataPath?: string, legacyPath?: string) {
    this.dataPath = dataPath ?? join(homedir(), ".cline", "data");
    this.legacyPath = legacyPath ?? join(
      homedir(), ".config", "Code", "User", "globalStorage", "saoudrizwan.claude-dev",
    );
    this.globalRulesPath = join(homedir(), "Documents", "Cline", "Rules");
  }

  detect(): boolean {
    return existsSync(this.dataPath) || existsSync(this.legacyPath);
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    let knowledgeFiles = 0;
    let sessionFiles = 0;

    // Global rules (primary + fallback paths)
    const ruleDirs = [this.globalRulesPath, join(homedir(), "Cline", "Rules")];
    for (const dir of ruleDirs) {
      if (existsSync(dir)) knowledgeFiles += countFiles(dir, [".md", ".txt"]);
    }

    // Task history
    const stateDir = this.resolveStateDir();
    if (stateDir) {
      const historyPath = join(stateDir, "taskHistory.json");
      if (existsSync(historyPath)) {
        try {
          const items = JSON.parse(readFileSync(historyPath, "utf8")) as ClineTaskHistoryItem[];
          sessionFiles = items.length;
        } catch {
          // ignore
        }
      }
    }

    // Count MCP settings as knowledge
    const mcpSettings = join(this.resolveDataDir() ?? "", "settings", "cline_mcp_settings.json");
    if (existsSync(mcpSettings)) knowledgeFiles++;

    return { memoryFiles: 0, knowledgeFiles, sessionFiles };
  }

  async importMemories(_db: Database, _embed: EmbedFn, _project?: string): Promise<ImportResult> {
    // Cline doesn't have a structured memory system
    return { imported: 0, skipped: 0, errors: [] };
  }

  async importKnowledge(db: Database, embed: EmbedFn): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    // 1. Import global rules
    await this.importGlobalRules(db, embed, result);

    // 2. Import task summaries (task prompt + metadata, not full conversations)
    await this.importTaskSummaries(db, embed, result);

    return result;
  }

  private resolveDataDir(): string | null {
    if (existsSync(this.dataPath)) return this.dataPath;
    if (existsSync(this.legacyPath)) return this.legacyPath;
    return null;
  }

  private resolveStateDir(): string | null {
    const dataDir = this.resolveDataDir();
    if (!dataDir) return null;

    // New layout: ~/.cline/data/state/
    const newState = join(dataDir, "state");
    if (existsSync(newState)) return newState;

    // Legacy layout: files directly in globalStorage dir
    return dataDir;
  }

  private async importGlobalRules(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    // ~/Documents/Cline/Rules/ and fallback ~/Cline/Rules/
    const ruleDirs = [
      this.globalRulesPath,
      join(homedir(), "Cline", "Rules"),
    ];

    for (const dir of ruleDirs) {
      if (!existsSync(dir)) continue;

      for (const filename of readdirSync(dir)) {
        if (!filename.endsWith(".md") && !filename.endsWith(".txt")) continue;

        const filePath = join(dir, filename);
        try {
          const raw = readFileSync(filePath, "utf8");
          const hash = contentHash(raw);

          if (isAlreadyImported(db, hash)) {
            result.skipped++;
            continue;
          }

          const entryId = insertEntry(db, "warm", "knowledge", {
            content: raw,
            source: filePath,
            source_tool: "cline",
            tags: ["cline-rule", "global"],
          });

          insertEmbedding(db, "warm", "knowledge", entryId, await embed(raw));
          logImport(db, "cline", filePath, hash, entryId, "warm", "knowledge");
          result.imported++;
        } catch (err) {
          result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
    }
  }

  private async importTaskSummaries(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    const stateDir = this.resolveStateDir();
    if (!stateDir) return;

    const historyPath = join(stateDir, "taskHistory.json");
    if (!existsSync(historyPath)) return;

    let items: ClineTaskHistoryItem[];
    try {
      items = JSON.parse(readFileSync(historyPath, "utf8"));
    } catch {
      return;
    }

    if (!Array.isArray(items)) return;

    for (const item of items) {
      if (!item.task || !item.id) continue;

      // Build a summary from the task prompt + metadata
      const parts = [`Task: ${item.task}`];
      if (item.modelId) parts.push(`Model: ${item.modelId}`);
      if (item.totalCost) parts.push(`Cost: $${item.totalCost.toFixed(4)}`);
      if (item.ts) parts.push(`Date: ${new Date(item.ts).toISOString().slice(0, 10)}`);

      const content = parts.join("\n");
      const hash = contentHash(content);

      if (isAlreadyImported(db, hash)) {
        result.skipped++;
        continue;
      }

      try {
        const entryId = insertEntry(db, "cold", "knowledge", {
          content,
          summary: item.task.slice(0, 200),
          source: `cline:task:${item.id}`,
          source_tool: "cline",
          tags: ["cline-task"],
        });

        insertEmbedding(db, "cold", "knowledge", entryId, await embed(content));
        logImport(db, "cline", `task:${item.id}`, hash, entryId, "cold", "knowledge");
        result.imported++;
      } catch (err) {
        result.errors.push(`cline task ${item.id}: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
  }
}

function countFiles(dir: string, extensions: string[]): number {
  let count = 0;
  if (!existsSync(dir)) return count;
  for (const f of readdirSync(dir)) {
    if (extensions.some((ext) => f.endsWith(ext))) count++;
  }
  return count;
}
