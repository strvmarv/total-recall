import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import type Database from "better-sqlite3";
import type { HostImporter, ImportResult, EmbedFn } from "./importer.js";
import { contentHash, isAlreadyImported, logImport, parseFrontmatter } from "./import-utils.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

/**
 * Cursor stores:
 * - Project rules: .cursorrules (legacy) and .cursor/rules/*.mdc (current)
 * - Global rules: ~/.config/Cursor/User/globalStorage/state.vscdb SQLite DB
 *   key 'aicontext.personalContext' in ItemTable
 * - Conversations: state.vscdb cursorDiskKV table (skipped — too large/noisy)
 */
export class CursorImporter implements HostImporter {
  readonly name = "cursor";
  private readonly configPath: string;
  private readonly extensionPath: string;

  constructor(configPath?: string, extensionPath?: string) {
    this.configPath = configPath ?? join(homedir(), ".config", "Cursor");
    this.extensionPath = extensionPath ?? join(homedir(), ".cursor");
  }

  detect(): boolean {
    return existsSync(this.configPath) || existsSync(this.extensionPath);
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    let knowledgeFiles = 0;

    // Count global rules DB
    const globalDb = join(this.configPath, "User", "globalStorage", "state.vscdb");
    if (existsSync(globalDb)) knowledgeFiles++;

    // Count project .cursorrules and .cursor/rules/*.mdc via workspace mapping
    const workspaceDir = join(this.configPath, "User", "workspaceStorage");
    if (existsSync(workspaceDir)) {
      for (const entry of readdirSync(workspaceDir, { withFileTypes: true })) {
        if (!entry.isDirectory()) continue;
        const wsJson = join(workspaceDir, entry.name, "workspace.json");
        if (!existsSync(wsJson)) continue;

        try {
          const ws = JSON.parse(readFileSync(wsJson, "utf8"));
          const projectPath = ws.folder
            ? decodeURIComponent(new URL(ws.folder).pathname)
            : ws.workspace
              ? decodeURIComponent(new URL(ws.workspace).pathname)
              : null;
          if (!projectPath) continue;

          if (existsSync(join(projectPath, ".cursorrules"))) knowledgeFiles++;

          const rulesDir = join(projectPath, ".cursor", "rules");
          if (existsSync(rulesDir)) {
            for (const f of readdirSync(rulesDir)) {
              if (f.endsWith(".mdc")) knowledgeFiles++;
            }
          }
        } catch {
          // skip unreadable workspace entries
        }
      }
    }

    return { memoryFiles: 0, knowledgeFiles, sessionFiles: 0 };
  }

  async importMemories(_db: Database.Database, _embed: EmbedFn, _project?: string): Promise<ImportResult> {
    // Cursor doesn't have a structured memory system
    return { imported: 0, skipped: 0, errors: [] };
  }

  async importKnowledge(db: Database.Database, embed: EmbedFn): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    // 1. Import global rules from SQLite
    await this.importGlobalRules(db, embed, result);

    // 2. Import project rules from workspaces
    await this.importProjectRules(db, embed, result);

    return result;
  }

  private async importGlobalRules(
    db: Database.Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    const dbPath = join(this.configPath, "User", "globalStorage", "state.vscdb");
    if (!existsSync(dbPath)) return;

    let cursorDb: Database.Database | null = null;
    try {
      // Dynamic import to avoid hard dependency at module level
      const BetterSqlite3 = (await import("better-sqlite3")).default;
      cursorDb = new BetterSqlite3(dbPath, { readonly: true });

      const row = cursorDb
        .prepare("SELECT value FROM ItemTable WHERE key = 'aicontext.personalContext'")
        .get() as { value: string } | undefined;

      if (!row?.value) return;

      const content = row.value;
      const hash = contentHash(content);

      if (isAlreadyImported(db, hash)) {
        result.skipped++;
        return;
      }

      const entryId = insertEntry(db, "warm", "knowledge", {
        content,
        source: dbPath,
        source_tool: "cursor",
        tags: ["global-rules"],
      });

      insertEmbedding(db, "warm", "knowledge", entryId, await embed(content));
      logImport(db, "cursor", dbPath, hash, entryId, "warm", "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(`cursor global rules: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      cursorDb?.close();
    }
  }

  private async importProjectRules(
    db: Database.Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    const workspaceDir = join(this.configPath, "User", "workspaceStorage");
    if (!existsSync(workspaceDir)) return;

    const projectPaths = new Set<string>();

    for (const entry of readdirSync(workspaceDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const wsJson = join(workspaceDir, entry.name, "workspace.json");
      if (!existsSync(wsJson)) continue;

      try {
        const ws = JSON.parse(readFileSync(wsJson, "utf8"));
        const projectPath = ws.folder
          ? new URL(ws.folder).pathname
          : ws.workspace
            ? new URL(ws.workspace).pathname
            : null;
        if (projectPath) projectPaths.add(projectPath);
      } catch {
        // skip
      }
    }

    for (const projectPath of projectPaths) {
      // Legacy .cursorrules
      const legacyPath = join(projectPath, ".cursorrules");
      if (existsSync(legacyPath)) {
        await this.importRuleFile(db, embed, result, legacyPath, ["cursorrules", "legacy"]);
      }

      // Current .cursor/rules/*.mdc
      const rulesDir = join(projectPath, ".cursor", "rules");
      if (existsSync(rulesDir)) {
        for (const filename of readdirSync(rulesDir)) {
          if (!filename.endsWith(".mdc")) continue;
          await this.importRuleFile(db, embed, result, join(rulesDir, filename), ["cursor-rule"]);
        }
      }
    }
  }

  private async importRuleFile(
    db: Database.Database,
    embed: EmbedFn,
    result: ImportResult,
    filePath: string,
    tags: string[],
  ): Promise<void> {
    try {
      const raw = readFileSync(filePath, "utf8");
      const hash = contentHash(raw);

      if (isAlreadyImported(db, hash)) {
        result.skipped++;
        return;
      }

      const { frontmatter, content } = parseFrontmatter(raw);

      const entryId = insertEntry(db, "cold", "knowledge", {
        content,
        summary: frontmatter?.description ?? null,
        source: filePath,
        source_tool: "cursor",
        tags: frontmatter?.name ? [frontmatter.name, ...tags] : tags,
      });

      insertEmbedding(db, "cold", "knowledge", entryId, await embed(content));
      logImport(db, "cursor", filePath, hash, entryId, "cold", "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
}
