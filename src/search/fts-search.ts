import type Database from "better-sqlite3";
import type { Tier, ContentType } from "../types.js";
import { tableName, ftsTableName } from "../types.js";

export interface FtsSearchResult {
  id: string;
  score: number;
}

export interface FtsSearchOpts {
  topK: number;
}

/**
 * Sanitize a query string for FTS5 MATCH syntax.
 * Wraps each word in double quotes to avoid FTS5 syntax errors
 * from special characters like -, *, etc.
 */
function sanitizeFtsQuery(query: string): string {
  const words = query
    .split(/\s+/)
    .filter(Boolean)
    .map((w) => `"${w.replace(/"/g, '""')}"`)
    .join(" ");
  return words;
}

export function searchByFts(
  db: Database.Database,
  tier: Tier,
  type: ContentType,
  query: string,
  opts: FtsSearchOpts,
): FtsSearchResult[] {
  const contentTable = tableName(tier, type);
  const ftsTable = ftsTableName(tier, type);

  // Check if FTS5 table exists (graceful fallback for pre-migration DBs)
  const tableExists = db
    .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name=?")
    .get(ftsTable);
  if (!tableExists) return [];

  const sanitized = sanitizeFtsQuery(query);
  if (!sanitized) return [];

  const rows = db
    .prepare(
      `SELECT c.id, rank as bm25_rank
       FROM ${ftsTable} fts
       INNER JOIN ${contentTable} c ON c.rowid = fts.rowid
       WHERE ${ftsTable} MATCH ?
       ORDER BY rank
       LIMIT ?`,
    )
    .all(sanitized, opts.topK) as Array<{ id: string; bm25_rank: number }>;

  if (rows.length === 0) return [];

  // FTS5 rank is negative BM25 (lower = better match).
  // Normalize to 0-1 range using min-max scaling.
  const rawScores = rows.map((r) => -r.bm25_rank); // flip sign: higher = better
  const maxRaw = Math.max(...rawScores);
  const minRaw = Math.min(...rawScores);
  const range = maxRaw - minRaw;

  return rows.map((r, i) => ({
    id: r.id,
    score: range > 0 ? (rawScores[i]! - minRaw) / range : 1.0,
  }));
}
