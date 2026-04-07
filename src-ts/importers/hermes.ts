import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import type { Database } from "bun:sqlite";
import type { HostImporter, ImportResult, EmbedFn } from "./importer.js";
import { contentHash, isAlreadyImported, logImport, parseFrontmatter } from "./import-utils.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

/**
 * Hermes agent (NousResearch/hermes-agent) stores:
 * - Config: ~/.hermes/config.yaml
 * - Sessions/messages: ~/.hermes/state.db (SQLite, WAL, FTS5)
 * - Memories: ~/.hermes/memories/MEMORY.md and USER.md (§-delimited entries)
 * - Skills: ~/.hermes/skills/<name>/SKILL.md
 * - Soul: ~/.hermes/SOUL.md (agent personality)
 * - Override base path via HERMES_HOME env var
 *
 * We import: memories (as warm), skills + SOUL.md (as cold knowledge).
 * We skip session DB conversations — too large/noisy.
 */
export class HermesImporter implements HostImporter {
  readonly name = "hermes";
  private readonly basePath: string;

  constructor(basePath?: string) {
    this.basePath = basePath ?? (process.env["HERMES_HOME"] || join(homedir(), ".hermes"));
  }

  detect(): boolean {
    return existsSync(this.basePath) && (
      existsSync(join(this.basePath, "state.db")) ||
      existsSync(join(this.basePath, "memories")) ||
      existsSync(join(this.basePath, "config.yaml"))
    );
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    let memoryFiles = 0;
    let knowledgeFiles = 0;
    let sessionFiles = 0;

    // Memory files
    const memoriesDir = join(this.basePath, "memories");
    if (existsSync(memoriesDir)) {
      if (existsSync(join(memoriesDir, "MEMORY.md"))) memoryFiles++;
      if (existsSync(join(memoriesDir, "USER.md"))) memoryFiles++;
    }

    // Skills
    const skillsDir = join(this.basePath, "skills");
    if (existsSync(skillsDir)) {
      for (const entry of readdirSync(skillsDir, { withFileTypes: true })) {
        if (entry.isDirectory() && existsSync(join(skillsDir, entry.name, "SKILL.md"))) {
          knowledgeFiles++;
        }
      }
    }

    // SOUL.md
    if (existsSync(join(this.basePath, "SOUL.md"))) knowledgeFiles++;

    // Session DB
    if (existsSync(join(this.basePath, "state.db"))) sessionFiles = 1;

    return { memoryFiles, knowledgeFiles, sessionFiles };
  }

  async importMemories(db: Database, embed: EmbedFn, _project?: string): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    // Import MEMORY.md entries (§-delimited)
    await this.importMemoryFile(
      db, embed, result,
      join(this.basePath, "memories", "MEMORY.md"),
      ["hermes-memory"],
    );

    // Import USER.md entries (§-delimited)
    await this.importMemoryFile(
      db, embed, result,
      join(this.basePath, "memories", "USER.md"),
      ["hermes-user", "user-profile"],
    );

    return result;
  }

  async importKnowledge(db: Database, embed: EmbedFn): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    // 1. Import SOUL.md
    const soulPath = join(this.basePath, "SOUL.md");
    if (existsSync(soulPath)) {
      await this.importSingleFile(db, embed, result, soulPath, "warm", ["hermes-soul"]);
    }

    // 2. Import skills
    await this.importSkills(db, embed, result);

    return result;
  }

  private async importMemoryFile(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
    filePath: string,
    tags: string[],
  ): Promise<void> {
    if (!existsSync(filePath)) return;

    try {
      const raw = readFileSync(filePath, "utf8");

      // Split on § delimiter (Hermes convention)
      const entries = raw.split(/\n§\n/).map((e) => e.trim()).filter(Boolean);

      for (const entry of entries) {
        const hash = contentHash(entry);

        if (isAlreadyImported(db, hash)) {
          result.skipped++;
          continue;
        }

        const entryId = insertEntry(db, "warm", "memory", {
          content: entry,
          summary: entry.slice(0, 200),
          source: filePath,
          source_tool: "hermes",
          tags,
        });

        insertEmbedding(db, "warm", "memory", entryId, await embed(entry));
        logImport(db, "hermes", filePath, hash, entryId, "warm", "memory");
        result.imported++;
      }
    } catch (err) {
      result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  private async importSkills(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
  ): Promise<void> {
    const skillsDir = join(this.basePath, "skills");
    if (!existsSync(skillsDir)) return;

    for (const entry of readdirSync(skillsDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;
      const skillPath = join(skillsDir, entry.name, "SKILL.md");
      if (!existsSync(skillPath)) continue;

      await this.importSingleFile(db, embed, result, skillPath, "cold", ["hermes-skill", entry.name]);
    }
  }

  private async importSingleFile(
    db: Database,
    embed: EmbedFn,
    result: ImportResult,
    filePath: string,
    tier: "warm" | "cold",
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

      const entryId = insertEntry(db, tier, "knowledge", {
        content,
        summary: frontmatter?.description ?? null,
        source: filePath,
        source_tool: "hermes",
        tags: frontmatter?.name ? [frontmatter.name, ...tags] : tags,
      });

      insertEmbedding(db, tier, "knowledge", entryId, await embed(content));
      logImport(db, "hermes", filePath, hash, entryId, tier, "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
}
