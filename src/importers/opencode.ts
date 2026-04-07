import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { Database } from "bun:sqlite";
import type { HostImporter, ImportResult, EmbedFn } from "./importer.js";
import { contentHash, isAlreadyImported, logImport, parseFrontmatter } from "./import-utils.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

/**
 * OpenCode stores:
 * - Sessions/messages: ~/.local/share/opencode/opencode.db (SQLite with WAL)
 * - Config: ~/.config/opencode/opencode.json, per-project .opencode/opencode.json
 * - Instructions: AGENTS.md (global at ~/.config/opencode/, per-project)
 * - Custom agents: .opencode/agent/*.md (frontmatter: mode, model, tools, color)
 * - Custom commands: .opencode/command/*.md (frontmatter: description, model, subtask)
 *
 * We import: AGENTS.md (as knowledge), custom agents/commands (as cold knowledge).
 * We skip session DB — complex and noisy.
 */
export class OpenCodeImporter implements HostImporter {
  readonly name = "opencode";
  private readonly dataPath: string;
  private readonly configPath: string;

  constructor(dataPath?: string, configPath?: string) {
    this.dataPath = dataPath ?? join(
      process.env["XDG_DATA_HOME"] ?? join(homedir(), ".local", "share"),
      "opencode",
    );
    this.configPath = configPath ?? join(
      process.env["XDG_CONFIG_HOME"] ?? join(homedir(), ".config"),
      "opencode",
    );
  }

  detect(): boolean {
    return existsSync(this.dataPath) || existsSync(this.configPath);
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    let knowledgeFiles = 0;
    let sessionFiles = 0;

    // Global AGENTS.md
    if (existsSync(join(this.configPath, "AGENTS.md"))) knowledgeFiles++;

    // Session DB exists = at least 1 session
    const dbPath = join(this.dataPath, "opencode.db");
    if (existsSync(dbPath)) sessionFiles = 1;

    return { memoryFiles: 0, knowledgeFiles, sessionFiles };
  }

  async importMemories(_db: Database, _embed: EmbedFn, _project?: string): Promise<ImportResult> {
    return { imported: 0, skipped: 0, errors: [] };
  }

  async importKnowledge(db: Database, embed: EmbedFn): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    // 1. Global AGENTS.md
    await this.importAgentsMd(db, embed, result);

    // 2. Discover projects from the DB and import per-project .opencode/ content
    await this.importProjectContent(db, embed, result);

    return result;
  }

  private async importAgentsMd(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    const agentsMdPath = join(this.configPath, "AGENTS.md");
    if (!existsSync(agentsMdPath)) return;

    try {
      const raw = readFileSync(agentsMdPath, "utf8");
      const hash = contentHash(raw);

      if (isAlreadyImported(db, hash)) {
        result.skipped++;
        return;
      }

      const { content } = parseFrontmatter(raw);

      const entryId = insertEntry(db, "warm", "knowledge", {
        content,
        source: agentsMdPath,
        source_tool: "opencode",
        tags: ["agents-md", "global"],
      });

      insertEmbedding(db, "warm", "knowledge", entryId, await embed(content));
      logImport(db, "opencode", agentsMdPath, hash, entryId, "warm", "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(`${agentsMdPath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  private async importProjectContent(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    // Discover project paths from the OpenCode SQLite DB
    const projectPaths = await this.discoverProjects();

    for (const projectPath of projectPaths) {
      const openCodeDir = join(projectPath, ".opencode");
      if (!existsSync(openCodeDir)) continue;

      // Import custom agents
      const agentDir = join(openCodeDir, "agent");
      if (existsSync(agentDir)) {
        await this.importMdDir(db, embed, result, agentDir, ["opencode-agent"]);
      }

      // Import custom commands
      const commandDir = join(openCodeDir, "command");
      if (existsSync(commandDir)) {
        await this.importMdDir(db, embed, result, commandDir, ["opencode-command"]);
      }

      // Per-project AGENTS.md
      const projectAgentsMd = join(projectPath, "AGENTS.md");
      if (existsSync(projectAgentsMd)) {
        await this.importSingleFile(db, embed, result, projectAgentsMd, ["agents-md", "project"]);
      }
    }
  }

  private async discoverProjects(): Promise<string[]> {
    const dbPath = join(this.dataPath, "opencode.db");
    if (!existsSync(dbPath)) return [];

    let ocDb: Database | null = null;
    try {
      ocDb = new Database(dbPath, { readonly: true });
      const rows = ocDb.query("SELECT worktree FROM project").all() as { worktree: string }[];
      return rows.map((r) => r.worktree).filter((p) => existsSync(p));
    } catch {
      return [];
    } finally {
      ocDb?.close();
    }
  }

  private async importMdDir(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
    dir: string,
    tags: string[],
  ): Promise<void> {
    for (const filename of readdirSync(dir)) {
      if (!filename.endsWith(".md")) continue;
      await this.importSingleFile(db, embed, result, join(dir, filename), tags);
    }
  }

  private async importSingleFile(
    db: Database,
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
        source_tool: "opencode",
        tags: frontmatter?.name ? [frontmatter.name, ...tags] : tags,
      });

      insertEmbedding(db, "cold", "knowledge", entryId, await embed(content));
      logImport(db, "opencode", filePath, hash, entryId, "cold", "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
}
