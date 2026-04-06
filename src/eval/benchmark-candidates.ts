import { randomUUID } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { resolve, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import type Database from "better-sqlite3";
import type { MissEntry } from "./metrics.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const PACKAGE_ROOT = basename(__dirname) === "dist"
  ? resolve(__dirname, "..")
  : resolve(__dirname, "..", "..");

export interface MissContext {
  query: string;
  topContent: string | null;
  topEntryId: string | null;
}

export interface CandidateRow {
  id: string;
  query_text: string;
  top_score: number;
  top_result_content: string | null;
  top_result_entry_id: string | null;
  first_seen: number;
  last_seen: number;
  times_seen: number;
  status: string;
}

export function writeCandidates(
  db: Database.Database,
  misses: MissEntry[],
  contexts: MissContext[],
): void {
  const contextMap = new Map(contexts.map((c) => [c.query, c]));

  const upsert = db.prepare(`
    INSERT INTO benchmark_candidates
      (id, query_text, top_score, top_result_content, top_result_entry_id, first_seen, last_seen, times_seen, status)
    VALUES (?, ?, ?, ?, ?, ?, ?, 1, 'pending')
    ON CONFLICT(query_text) DO UPDATE SET
      top_score = excluded.top_score,
      last_seen = excluded.last_seen,
      times_seen = benchmark_candidates.times_seen + 1
  `);

  for (const miss of misses) {
    const ctx = contextMap.get(miss.query);
    upsert.run(
      randomUUID(),
      miss.query,
      miss.topScore ?? 0,
      ctx?.topContent ?? null,
      ctx?.topEntryId ?? null,
      miss.timestamp,
      miss.timestamp,
    );
  }
}

export function listCandidates(db: Database.Database): CandidateRow[] {
  return db.prepare(`
    SELECT * FROM benchmark_candidates
    WHERE status = 'pending'
    ORDER BY times_seen DESC, top_score ASC
  `).all() as CandidateRow[];
}

export function resolveCandidates(
  db: Database.Database,
  acceptIds: string[],
  rejectIds: string[],
): { accepted: number; rejected: number; corpusEntries: string[] } {
  const corpusEntries: string[] = [];

  const accept = db.prepare("UPDATE benchmark_candidates SET status = 'accepted' WHERE id = ?");
  const reject = db.prepare("UPDATE benchmark_candidates SET status = 'rejected' WHERE id = ?");
  const getById = db.prepare("SELECT * FROM benchmark_candidates WHERE id = ?");

  for (const id of acceptIds) {
    const row = getById.get(id) as CandidateRow | undefined;
    if (!row) continue;
    accept.run(id);

    const entry = JSON.stringify({
      query: row.query_text,
      expected_content_contains: row.top_result_content?.slice(0, 100) ?? "",
      expected_tier: "warm",
      source: "grow",
      added: new Date().toISOString().slice(0, 10),
    });
    corpusEntries.push(entry);
  }

  for (const id of rejectIds) {
    reject.run(id);
  }

  // Append accepted entries to retrieval.jsonl
  if (corpusEntries.length > 0) {
    const benchmarkPath = resolve(PACKAGE_ROOT, "eval", "benchmarks", "retrieval.jsonl");
    const existing = readFileSync(benchmarkPath, "utf-8");
    const trailing = existing.endsWith("\n") ? "" : "\n";
    writeFileSync(benchmarkPath, existing + trailing + corpusEntries.join("\n") + "\n");
  }

  return {
    accepted: acceptIds.length,
    rejected: rejectIds.length,
    corpusEntries,
  };
}
