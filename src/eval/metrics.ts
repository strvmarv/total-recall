import type { Database } from "bun:sqlite";
import type { RetrievalEventRow, CompactionLogRow } from "../types.js";
import { writeCandidates, type MissContext } from "./benchmark-candidates.js";

export interface TierMetrics {
  precision: number;
  hitRate: number;
  avgScore: number;
  count: number;
}

export interface ContentTypeMetrics {
  precision: number;
  hitRate: number;
  count: number;
}

export interface MissEntry {
  query: string;
  topScore: number | null;
  timestamp: number;
}

export interface CompactionHealthMetrics {
  totalCompactions: number;
  avgPreservationRatio: number | null;
  entriesWithDrift: number;
}

export interface Metrics {
  precision: number;
  hitRate: number;
  missRate: number;
  mrr: number;
  avgLatencyMs: number;
  totalEvents: number;
  byTier: Record<string, TierMetrics>;
  byContentType: Record<string, ContentTypeMetrics>;
  topMisses: MissEntry[];
  falsePositives: MissEntry[];
  compactionHealth: CompactionHealthMetrics;
}

function computeGroupMetrics(
  events: RetrievalEventRow[],
): { precision: number; hitRate: number; avgScore: number } {
  const withOutcome = events.filter((e) => e.outcome_used !== null);
  const used = withOutcome.filter((e) => e.outcome_used === 1);
  const precision = withOutcome.length > 0 ? used.length / withOutcome.length : 0;

  const hitEvents = events.filter((e) => e.outcome_used !== null && e.outcome_used === 1);
  const eventsWithOutcome = events.filter((e) => e.outcome_used !== null);
  const hitRate =
    eventsWithOutcome.length > 0 ? hitEvents.length / eventsWithOutcome.length : 0;

  const scoresWithValue = events.filter((e) => e.top_score !== null);
  const avgScore =
    scoresWithValue.length > 0
      ? scoresWithValue.reduce((sum, e) => sum + (e.top_score as number), 0) /
        scoresWithValue.length
      : 0;

  return { precision, hitRate, avgScore };
}

export function computeMetrics(
  events: RetrievalEventRow[],
  similarityThreshold: number,
  compactionRows: CompactionLogRow[] = [],
  db?: Database,
): Metrics {
  if (events.length === 0) {
    return {
      precision: 0,
      hitRate: 0,
      missRate: 0,
      mrr: 0,
      avgLatencyMs: 0,
      totalEvents: 0,
      byTier: {},
      byContentType: {},
      topMisses: [],
      falsePositives: [],
      compactionHealth: computeCompactionHealth(compactionRows),
    };
  }

  // Precision: used / total among events with outcome
  const withOutcome = events.filter((e) => e.outcome_used !== null);
  const usedCount = withOutcome.filter((e) => e.outcome_used === 1).length;
  const precision = withOutcome.length > 0 ? usedCount / withOutcome.length : 0;

  // Hit rate: events where outcome_used = 1 / events with outcome
  const hitRate = withOutcome.length > 0 ? usedCount / withOutcome.length : 0;

  // Miss rate: events with top_score < threshold / total events
  const missCount = events.filter(
    (e) => e.top_score === null || e.top_score < similarityThreshold,
  ).length;
  const missRate = missCount / events.length;

  // MRR: simplified — 1.0 for used top results, 0 otherwise
  const mrrSum = withOutcome.reduce((sum, e) => {
    return sum + (e.outcome_used === 1 ? 1.0 : 0);
  }, 0);
  const mrr = withOutcome.length > 0 ? mrrSum / withOutcome.length : 0;

  // Average latency
  const latencies = events.filter((e) => e.latency_ms !== null);
  const avgLatencyMs =
    latencies.length > 0
      ? latencies.reduce((sum, e) => sum + (e.latency_ms as number), 0) / latencies.length
      : 0;

  // Group by tier
  const tierMap = new Map<string, RetrievalEventRow[]>();
  for (const e of events) {
    if (e.top_tier) {
      const group = tierMap.get(e.top_tier) ?? [];
      group.push(e);
      tierMap.set(e.top_tier, group);
    }
  }

  const byTier: Record<string, TierMetrics> = {};
  for (const [tier, group] of tierMap) {
    const { precision: p, hitRate: h, avgScore } = computeGroupMetrics(group);
    byTier[tier] = { precision: p, hitRate: h, avgScore, count: group.length };
  }

  // Group by content type
  const ctMap = new Map<string, RetrievalEventRow[]>();
  for (const e of events) {
    if (e.top_content_type) {
      const group = ctMap.get(e.top_content_type) ?? [];
      group.push(e);
      ctMap.set(e.top_content_type, group);
    }
  }

  const byContentType: Record<string, ContentTypeMetrics> = {};
  for (const [ct, group] of ctMap) {
    const { precision: p, hitRate: h } = computeGroupMetrics(group);
    byContentType[ct] = { precision: p, hitRate: h, count: group.length };
  }

  // Top misses: queries with lowest scores
  const topMisses: MissEntry[] = events
    .filter((e) => e.top_score === null || e.top_score < similarityThreshold)
    .sort((a, b) => (a.top_score ?? -1) - (b.top_score ?? -1))
    .slice(0, 10)
    .map((e) => ({ query: e.query_text, topScore: e.top_score, timestamp: e.timestamp }));

  // Write miss candidates for evolving benchmarks
  if (db && topMisses.length > 0) {
    try {
      const missContexts: MissContext[] = topMisses.map((miss) => {
        const event = events.find((e) => e.query_text === miss.query);
        let topEntryId: string | null = null;
        if (event) {
          const results = JSON.parse(event.results) as Array<{ entry_id: string }>;
          topEntryId = results[0]?.entry_id ?? null;
        }
        return { query: miss.query, topContent: null, topEntryId };
      });
      writeCandidates(db, topMisses, missContexts);
    } catch {
      // Don't fail metrics if candidate write fails
    }
  }

  // False positives: high score but outcome_used = 0
  const falsePositives: MissEntry[] = events
    .filter((e) => e.outcome_used === 0 && e.top_score !== null && e.top_score >= similarityThreshold)
    .sort((a, b) => (b.top_score ?? 0) - (a.top_score ?? 0))
    .slice(0, 10)
    .map((e) => ({ query: e.query_text, topScore: e.top_score, timestamp: e.timestamp }));

  return {
    precision,
    hitRate,
    missRate,
    mrr,
    avgLatencyMs,
    totalEvents: events.length,
    byTier,
    byContentType,
    topMisses,
    falsePositives,
    compactionHealth: computeCompactionHealth(compactionRows),
  };
}

export interface QueryDiffEntry {
  queryText: string;
  beforeOutcome: "used" | "unused" | "missing";
  afterOutcome: "used" | "unused" | "missing";
  beforeScore: number | null;
  afterScore: number | null;
}

export interface ComparisonResult {
  before: Metrics;
  after: Metrics;
  deltas: {
    precision: number;
    hitRate: number;
    mrr: number;
    missRate: number;
    avgLatencyMs: number;
  };
  byTier: Record<string, {
    before: TierMetrics;
    after: TierMetrics;
    deltas: { precision: number; hitRate: number; avgScore: number };
  }>;
  byContentType: Record<string, {
    before: ContentTypeMetrics;
    after: ContentTypeMetrics;
    deltas: { precision: number; hitRate: number };
  }>;
  queryDiff: {
    regressions: QueryDiffEntry[];
    improvements: QueryDiffEntry[];
  };
  warning?: string;
}

export function computeComparisonMetrics(
  eventsBefore: RetrievalEventRow[],
  eventsAfter: RetrievalEventRow[],
  similarityThreshold: number,
): ComparisonResult {
  const before = computeMetrics(eventsBefore, similarityThreshold);
  const after = computeMetrics(eventsAfter, similarityThreshold);

  const deltas = {
    precision: after.precision - before.precision,
    hitRate: after.hitRate - before.hitRate,
    mrr: after.mrr - before.mrr,
    missRate: after.missRate - before.missRate,
    avgLatencyMs: after.avgLatencyMs - before.avgLatencyMs,
  };

  // Per-tier deltas
  const allTiers = new Set([...Object.keys(before.byTier), ...Object.keys(after.byTier)]);
  const byTier: ComparisonResult["byTier"] = {};
  const emptyTier: TierMetrics = { precision: 0, hitRate: 0, avgScore: 0, count: 0 };
  for (const tier of allTiers) {
    const b = before.byTier[tier] ?? emptyTier;
    const a = after.byTier[tier] ?? emptyTier;
    byTier[tier] = {
      before: b,
      after: a,
      deltas: {
        precision: a.precision - b.precision,
        hitRate: a.hitRate - b.hitRate,
        avgScore: a.avgScore - b.avgScore,
      },
    };
  }

  // Per-content-type deltas
  const allTypes = new Set([...Object.keys(before.byContentType), ...Object.keys(after.byContentType)]);
  const byContentType: ComparisonResult["byContentType"] = {};
  const emptyType: ContentTypeMetrics = { precision: 0, hitRate: 0, count: 0 };
  for (const ct of allTypes) {
    const b = before.byContentType[ct] ?? emptyType;
    const a = after.byContentType[ct] ?? emptyType;
    byContentType[ct] = {
      before: b,
      after: a,
      deltas: {
        precision: a.precision - b.precision,
        hitRate: a.hitRate - b.hitRate,
      },
    };
  }

  // Query-level diff
  const beforeByQuery = new Map<string, RetrievalEventRow>();
  for (const e of eventsBefore) beforeByQuery.set(e.query_text, e);
  const afterByQuery = new Map<string, RetrievalEventRow>();
  for (const e of eventsAfter) afterByQuery.set(e.query_text, e);

  const regressions: QueryDiffEntry[] = [];
  const improvements: QueryDiffEntry[] = [];

  const allQueries = new Set([...beforeByQuery.keys(), ...afterByQuery.keys()]);
  for (const q of allQueries) {
    const b = beforeByQuery.get(q);
    const a = afterByQuery.get(q);
    const bOutcome = !b ? "missing" : b.outcome_used === 1 ? "used" : "unused";
    const aOutcome = !a ? "missing" : a.outcome_used === 1 ? "used" : "unused";

    if (bOutcome === aOutcome) continue;

    const entry: QueryDiffEntry = {
      queryText: q,
      beforeOutcome: bOutcome,
      afterOutcome: aOutcome,
      beforeScore: b?.top_score ?? null,
      afterScore: a?.top_score ?? null,
    };

    if (bOutcome === "used" && aOutcome !== "used") regressions.push(entry);
    if (aOutcome === "used" && bOutcome !== "used") improvements.push(entry);
  }

  let warning: string | undefined;
  if (eventsBefore.length === 0 || eventsAfter.length === 0) {
    warning = "Comparison requires retrieval events from both snapshots. One side has no data — metrics may not be meaningful.";
  }

  return { before, after, deltas, byTier, byContentType, queryDiff: { regressions, improvements }, warning };
}

function computeCompactionHealth(rows: CompactionLogRow[]): CompactionHealthMetrics {
  const withRatio = rows.filter((r) => r.preservation_ratio !== null);
  const withDrift = rows.filter((r) => r.semantic_drift !== null && r.semantic_drift > 0.2);

  return {
    totalCompactions: rows.length,
    avgPreservationRatio: withRatio.length > 0
      ? withRatio.reduce((sum, r) => sum + (r.preservation_ratio as number), 0) / withRatio.length
      : null,
    entriesWithDrift: withDrift.length,
  };
}
