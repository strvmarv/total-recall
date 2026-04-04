import { randomUUID } from "node:crypto";
import type Database from "better-sqlite3";
import { listEntries } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { Entry } from "../types.js";
import { tableName } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

export interface CheckAndPromoteColdResult {
  promoted: string[];
}

function copyEntryToWarm(db: Database.Database, embed: EmbedFn, entry: Entry): string {
  const newId = randomUUID();
  const now = Date.now();
  const toTable = tableName("warm", "memory");

  db.prepare(`
    INSERT INTO ${toTable}
      (id, content, summary, source, source_tool, project, tags,
       created_at, updated_at, last_accessed_at, access_count,
       decay_score, parent_id, collection_id, metadata)
    VALUES
      (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
  `).run(
    newId,
    entry.content,
    entry.summary,
    entry.source,
    entry.source_tool,
    entry.project,
    JSON.stringify(entry.tags),
    entry.created_at,
    now,
    now,
    entry.access_count,
    entry.decay_score,
    entry.parent_id,
    entry.collection_id,
    JSON.stringify(entry.metadata),
  );

  const embedding = embed(entry.content);
  insertEmbedding(db, "warm", "memory", newId, embedding);

  return newId;
}

export function checkAndPromoteCold(
  db: Database.Database,
  embed: EmbedFn,
  config: { accessThreshold: number; windowDays: number },
): CheckAndPromoteColdResult {
  const entries = listEntries(db, "cold", "memory");
  const now = Date.now();
  const windowMs = config.windowDays * 24 * 60 * 60 * 1000;

  const promoted: string[] = [];

  for (const entry of entries) {
    const withinWindow = now - entry.last_accessed_at <= windowMs;
    if (withinWindow && entry.access_count >= config.accessThreshold) {
      copyEntryToWarm(db, embed, entry);
      promoted.push(entry.id);
    }
  }

  return { promoted };
}
