import type { RetrievalEventRow, CompactionLogRow } from "../types.js";

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
