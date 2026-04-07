import type { Database } from "bun:sqlite";
import type { Tier, ContentType } from "../types.js";
import { tableName, vecTableName } from "../types.js";

export function insertEmbedding(
  db: Database,
  tier: Tier,
  type: ContentType,
  entryId: string,
  embedding: Float32Array,
): void {
  const contentTable = tableName(tier, type);
  const vecTable = vecTableName(tier, type);

  const row = db
    .prepare(`SELECT rowid FROM ${contentTable} WHERE id = ?`)
    .get(entryId) as { rowid: number } | undefined;

  if (!row) {
    throw new Error(`Entry ${entryId} not found in ${contentTable}`);
  }

  db.prepare(`INSERT INTO ${vecTable} (rowid, embedding) VALUES (?, ?)`).run(
    BigInt(row.rowid),
    Buffer.from(embedding.buffer),
  );
}

export function deleteEmbedding(
  db: Database,
  tier: Tier,
  type: ContentType,
  entryId: string,
): void {
  const contentTable = tableName(tier, type);
  const vecTable = vecTableName(tier, type);

  const row = db
    .prepare(`SELECT rowid FROM ${contentTable} WHERE id = ?`)
    .get(entryId) as { rowid: number } | undefined;

  if (!row) return;

  db.prepare(`DELETE FROM ${vecTable} WHERE rowid = ?`).run(BigInt(row.rowid));
}

export interface VectorSearchResult {
  id: string;
  score: number;
}

export interface VectorSearchOpts {
  topK: number;
  minScore?: number;
}

export function searchByVector(
  db: Database,
  tier: Tier,
  type: ContentType,
  queryVec: Float32Array,
  opts: VectorSearchOpts,
): VectorSearchResult[] {
  const contentTable = tableName(tier, type);
  const vecTable = vecTableName(tier, type);
  const oversample = opts.topK * 2;

  const rows = db
    .prepare(
      `SELECT c.id, v.distance as dist
       FROM ${vecTable} v
       INNER JOIN ${contentTable} c ON c.rowid = v.rowid
       WHERE v.embedding MATCH ? AND k = ?
       ORDER BY v.distance ASC`,
    )
    .all(Buffer.from(queryVec.buffer), oversample) as Array<{ id: string; dist: number }>;

  let results: VectorSearchResult[] = rows.map((r) => ({
    id: r.id,
    score: 1 - r.dist,
  }));

  if (opts.minScore !== undefined) {
    results = results.filter((r) => r.score >= opts.minScore!);
  }

  return results.slice(0, opts.topK);
}

export function searchMultipleTiers(
  db: Database,
  tiers: Array<{ tier: Tier; type: ContentType }>,
  queryVec: Float32Array,
  opts: VectorSearchOpts,
): VectorSearchResult[] {
  const allResults: VectorSearchResult[] = [];

  for (const { tier, type } of tiers) {
    const results = searchByVector(db, tier, type, queryVec, opts);
    allResults.push(...results);
  }

  allResults.sort((a, b) => b.score - a.score);

  return allResults.slice(0, opts.topK);
}
