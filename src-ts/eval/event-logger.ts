import { randomUUID } from "node:crypto";
import type { Database } from "bun:sqlite";
import type { RetrievalEventRow, QuerySource } from "../types.js";

export interface ResultItem {
  entry_id: string;
  tier: string;
  content_type: string;
  score: number;
  rank: number;
}

export interface LogRetrievalEventOpts {
  sessionId: string;
  queryText: string;
  querySource: QuerySource;
  queryEmbedding?: Buffer | null;
  results: ResultItem[];
  tiersSearched: string[];
  configSnapshotId: string;
  latencyMs?: number | null;
  totalCandidatesScanned?: number | null;
}

export function logRetrievalEvent(
  db: Database,
  opts: LogRetrievalEventOpts,
): string {
  const id = randomUUID();
  const timestamp = Date.now();

  const top = opts.results[0] ?? null;
  const result_count = opts.results.length;
  const top_score = top?.score ?? null;
  const top_tier = top?.tier ?? null;
  const top_content_type = top?.content_type ?? null;

  db.prepare(`
    INSERT INTO retrieval_events (
      id, timestamp, session_id, query_text, query_source, query_embedding,
      results, result_count, top_score, top_tier, top_content_type,
      config_snapshot_id, latency_ms, tiers_searched, total_candidates_scanned
    ) VALUES (
      ?, ?, ?, ?, ?, ?,
      ?, ?, ?, ?, ?,
      ?, ?, ?, ?
    )
  `).run(
    id,
    timestamp,
    opts.sessionId,
    opts.queryText,
    opts.querySource,
    opts.queryEmbedding ?? null,
    JSON.stringify(opts.results),
    result_count,
    top_score,
    top_tier,
    top_content_type,
    opts.configSnapshotId,
    opts.latencyMs ?? null,
    JSON.stringify(opts.tiersSearched),
    opts.totalCandidatesScanned ?? null,
  );

  return id;
}

export function updateOutcome(
  db: Database,
  eventId: string,
  outcome: { used: boolean; signal?: string },
): void {
  db.prepare(`
    UPDATE retrieval_events
    SET outcome_used = ?, outcome_signal = ?
    WHERE id = ?
  `).run(
    outcome.used ? 1 : 0,
    outcome.signal ?? null,
    eventId,
  );
}

export interface GetRetrievalEventsOpts {
  sessionId?: string;
  configSnapshotId?: string;
  days?: number;
  limit?: number;
}

export function getRetrievalEvents(
  db: Database,
  opts: GetRetrievalEventsOpts = {},
): RetrievalEventRow[] {
  const conditions: string[] = [];
  const params: (string | number)[] = [];

  if (opts.sessionId !== undefined) {
    conditions.push("session_id = ?");
    params.push(opts.sessionId);
  }

  if (opts.configSnapshotId !== undefined) {
    conditions.push("config_snapshot_id = ?");
    params.push(opts.configSnapshotId);
  }

  if (opts.days !== undefined) {
    const cutoff = Date.now() - opts.days * 24 * 60 * 60 * 1000;
    conditions.push("timestamp >= ?");
    params.push(cutoff);
  }

  let sql = "SELECT * FROM retrieval_events";
  if (conditions.length > 0) {
    sql += " WHERE " + conditions.join(" AND ");
  }
  sql += " ORDER BY timestamp DESC";

  if (opts.limit !== undefined) {
    sql += " LIMIT ?";
    params.push(opts.limit);
  }

  return db.prepare(sql).all(...params) as RetrievalEventRow[];
}
