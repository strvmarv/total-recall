import { describe, it, expect } from "vitest";
import { computeComparisonMetrics } from "./metrics.js";
import type { RetrievalEventRow } from "../types.js";

function makeEvent(overrides: Partial<RetrievalEventRow> = {}): RetrievalEventRow {
  return {
    id: "e1",
    timestamp: Date.now(),
    session_id: "s1",
    query_text: "test query",
    query_source: "manual",
    query_embedding: null,
    results: "[]",
    result_count: 1,
    top_score: 0.8,
    top_tier: "warm",
    top_content_type: "memory",
    outcome_used: 1,
    outcome_signal: "positive",
    config_snapshot_id: "snap-a",
    latency_ms: 10,
    tiers_searched: '["warm"]',
    total_candidates_scanned: 50,
    ...overrides,
  };
}

describe("computeComparisonMetrics", () => {
  it("computes deltas between two event sets", () => {
    const eventsA = [
      makeEvent({ id: "a1", outcome_used: 1, outcome_signal: "positive", top_score: 0.7 }),
      makeEvent({ id: "a2", outcome_used: 0, outcome_signal: "negative", top_score: 0.3 }),
    ];
    const eventsB = [
      makeEvent({ id: "b1", outcome_used: 1, outcome_signal: "positive", top_score: 0.9 }),
      makeEvent({ id: "b2", outcome_used: 1, outcome_signal: "positive", top_score: 0.8 }),
    ];

    const result = computeComparisonMetrics(eventsA, eventsB, 0.5);

    expect(result.before.precision).toBe(0.5);
    expect(result.after.precision).toBe(1.0);
    expect(result.deltas.precision).toBeCloseTo(0.5);
  });

  it("returns empty comparison when one set is empty", () => {
    const result = computeComparisonMetrics([], [makeEvent()], 0.5);
    expect(result.before.totalEvents).toBe(0);
    expect(result.after.totalEvents).toBe(1);
    expect(result.warning).toBeTruthy();
  });

  it("computes query-level diff for regressions and improvements", () => {
    const eventsA = [
      makeEvent({ id: "a1", query_text: "q1", outcome_used: 1, top_score: 0.8 }),
      makeEvent({ id: "a2", query_text: "q2", outcome_used: 0, top_score: 0.3 }),
    ];
    const eventsB = [
      makeEvent({ id: "b1", query_text: "q1", outcome_used: 0, top_score: 0.4 }),
      makeEvent({ id: "b2", query_text: "q2", outcome_used: 1, top_score: 0.9 }),
    ];

    const result = computeComparisonMetrics(eventsA, eventsB, 0.5);

    expect(result.queryDiff.regressions).toHaveLength(1);
    expect(result.queryDiff.regressions[0].queryText).toBe("q1");
    expect(result.queryDiff.improvements).toHaveLength(1);
    expect(result.queryDiff.improvements[0].queryText).toBe("q2");
  });
});
