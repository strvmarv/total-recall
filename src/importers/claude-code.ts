import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { homedir } from "node:os";
import { createHash } from "node:crypto";
import type Database from "better-sqlite3";
import type { HostImporter, ImportResult, EmbedFn } from "./importer.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { Tier, ContentType } from "../types.js";

interface Frontmatter {
  name?: string;
  description?: string;
  type?: string;
}

function parseFrontmatter(raw: string): { frontmatter: Frontmatter | null; content: string } {
  const match = raw.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!match) return { frontmatter: null, content: raw };

  const frontmatter: Frontmatter = {};
  for (const line of match[1]!.split("\n")) {
    const kv = line.match(/^(\w+):\s*(.*)$/);
    if (kv) {
      const key = kv[1] as keyof Frontmatter;
      frontmatter[key] = kv[2]!.trim();
    }
  }

  return { frontmatter, content: match[2]! };
}

function contentHash(text: string): string {
  return createHash("sha256").update(text).digest("hex");
}

function importLogId(sourceTool: string, sourcePath: string, hash: string): string {
  return createHash("md5").update(`${sourceTool}:${sourcePath}:${hash}`).digest("hex");
}

function isAlreadyImported(db: Database.Database, hash: string): boolean {
  const row = db
    .prepare("SELECT id FROM import_log WHERE content_hash = ?")
    .get(hash) as { id: string } | undefined;
  return row !== undefined;
}

function logImport(
  db: Database.Database,
  sourceTool: string,
  sourcePath: string,
  hash: string,
  entryId: string,
  tier: Tier,
  type: ContentType,
): void {
  const id = importLogId(sourceTool, sourcePath, hash);
  db.prepare(`
    INSERT OR IGNORE INTO import_log
      (id, timestamp, source_tool, source_path, content_hash, target_entry_id, target_tier, target_type)
    VALUES (?, ?, ?, ?, ?, ?, ?, ?)
  `).run(id, Date.now(), sourceTool, sourcePath, hash, entryId, tier, type);
}

export class ClaudeCodeImporter implements HostImporter {
  readonly name = "claude-code";
  private readonly basePath: string;

  constructor(basePath?: string) {
    this.basePath = basePath ?? join(homedir(), ".claude");
  }

  detect(): boolean {
    return existsSync(this.basePath) && existsSync(join(this.basePath, "projects"));
  }

  scan(): { memoryFiles: number; knowledgeFiles: number; sessionFiles: number } {
    let memoryFiles = 0;
    let knowledgeFiles = 0;
    let sessionFiles = 0;

    const projectsDir = join(this.basePath, "projects");
    if (!existsSync(projectsDir)) {
      return { memoryFiles, knowledgeFiles, sessionFiles };
    }

    for (const projectEntry of readdirSync(projectsDir, { withFileTypes: true })) {
      if (!projectEntry.isDirectory()) continue;
      const projectDir = join(projectsDir, projectEntry.name);

      // Count memory .md files
      const memoryDir = join(projectDir, "memory");
      if (existsSync(memoryDir)) {
        for (const f of readdirSync(memoryDir)) {
          if (f.endsWith(".md") && f !== "MEMORY.md") memoryFiles++;
        }
      }

      // Count CLAUDE.md files (knowledge)
      if (existsSync(join(projectDir, "CLAUDE.md"))) knowledgeFiles++;

      // Count .jsonl session files
      for (const f of readdirSync(projectDir)) {
        if (f.endsWith(".jsonl")) sessionFiles++;
      }
    }

    return { memoryFiles, knowledgeFiles, sessionFiles };
  }

  async importMemories(db: Database.Database, embed: EmbedFn, project?: string): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    const projectsDir = join(this.basePath, "projects");
    if (!existsSync(projectsDir)) return result;

    for (const projectEntry of readdirSync(projectsDir, { withFileTypes: true })) {
      if (!projectEntry.isDirectory()) continue;
      const projectDir = join(projectsDir, projectEntry.name);
      const memoryDir = join(projectDir, "memory");

      if (!existsSync(memoryDir)) continue;

      for (const filename of readdirSync(memoryDir)) {
        if (!filename.endsWith(".md") || filename === "MEMORY.md") continue;

        const filePath = join(memoryDir, filename);
        try {
          const raw = readFileSync(filePath, "utf8");
          const hash = contentHash(raw);

          if (isAlreadyImported(db, hash)) {
            result.skipped++;
            continue;
          }

          const { frontmatter, content } = parseFrontmatter(raw);

          // Determine tier/type from frontmatter type field
          let tier: Tier = "warm";
          let type: ContentType = "memory";

          if (frontmatter?.type === "reference") {
            tier = "cold";
            type = "knowledge";
          }

          const entryId = insertEntry(db, tier, type, {
            content,
            summary: frontmatter?.description ?? null,
            source: filePath,
            source_tool: "claude-code",
            project: project ?? null,
            tags: frontmatter?.name ? [frontmatter.name] : [],
          });

          insertEmbedding(db, tier, type, entryId, await embed(content));
          logImport(db, "claude-code", filePath, hash, entryId, tier, type);
          result.imported++;
        } catch (err) {
          result.errors.push(`${filePath}: ${err instanceof Error ? err.message : String(err)}`);
        }
      }
    }

    return result;
  }

  async importKnowledge(db: Database.Database, embed: EmbedFn): Promise<ImportResult> {
    const result: ImportResult = { imported: 0, skipped: 0, errors: [] };

    const claudeMdPath = join(this.basePath, "CLAUDE.md");
    if (!existsSync(claudeMdPath)) return result;

    try {
      const raw = readFileSync(claudeMdPath, "utf8");
      const hash = contentHash(raw);

      if (isAlreadyImported(db, hash)) {
        result.skipped++;
        return result;
      }

      const { content } = parseFrontmatter(raw);

      const entryId = insertEntry(db, "warm", "knowledge", {
        content,
        source: claudeMdPath,
        source_tool: "claude-code",
        tags: ["pinned"],
      });

      insertEmbedding(db, "warm", "knowledge", entryId, await embed(content));
      logImport(db, "claude-code", claudeMdPath, hash, entryId, "warm", "knowledge");
      result.imported++;
    } catch (err) {
      result.errors.push(
        `${claudeMdPath}: ${err instanceof Error ? err.message : String(err)}`,
      );
    }

    return result;
  }
}
