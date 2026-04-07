import { createHash } from "node:crypto";
import type { Database } from "bun:sqlite";
import type { Tier, ContentType } from "../types.js";

export interface Frontmatter {
  name?: string;
  description?: string;
  type?: string;
}

export function parseFrontmatter(raw: string): { frontmatter: Frontmatter | null; content: string } {
  const normalised = raw.replace(/\r\n/g, "\n");
  const match = normalised.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (!match) return { frontmatter: null, content: normalised };

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

export function contentHash(text: string): string {
  return createHash("sha256").update(text).digest("hex");
}

function importLogId(sourceTool: string, sourcePath: string, hash: string): string {
  return createHash("md5").update(`${sourceTool}:${sourcePath}:${hash}`).digest("hex");
}

export function isAlreadyImported(db: Database, hash: string): boolean {
  const row = db
    .prepare("SELECT id FROM import_log WHERE content_hash = ?")
    .get(hash) as { id: string } | undefined;
  return row !== undefined;
}

export function logImport(
  db: Database,
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
