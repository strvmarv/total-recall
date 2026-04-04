import { describe, it, expect } from "vitest";
import type { RetrievalEventRow } from "../types.js";
import { computeMetrics } from "./metrics.js";

function makeEvent(overrides: Partial<RetrievalEventRow> = {}): RetrievalEventRow {
  return {
    id: "evt-" + Math.random().toString(36).slice(2),
    timestamp: Date.now(),
    session_id: "sess-1",
    query_text: "test query",
    query_source: "auto",
    query_embedding: null,
    results: "[]",
    result_count: 1,
    top_score: 0.8,
    top_tier: "warm",
    top_content_type: "memory",
    outcome_used: null,
    outcome_signal: null,
    config_snapshot_id: "default",
    latency_ms: 10,
    tiers_searched: '["warm"]',
    total_candidates_scanned: null,
    ...overrides,
  };
}

describe("computeMetrics", () => {
  it("returns zeroes for empty events", () => {
    const metrics = computeMetrics([], 0.65);
    expect(metrics.precision).toBe(0);
    expect(metrics.hitRate).toBe(0);
    expect(metrics.missRate).toBe(0);
    expect(metrics.mrr).toBe(0);
    expect(metrics.avgLatencyMs).toBe(0);
    expect(metrics.totalEvents).toBe(0);
    expect(metrics.byTier).toEqual({});
    expect(metrics.byContentType).toEqual({});
  });

  it("computes precision, hit rate, miss rate, MRR", () => {
    const events = [
      makeEvent({ outcome_used: 1, top_score: 0.9 }),
      makeEvent({ outcome_used: 1, top_score: 0.85 }),
      makeEvent({ outcome_used: 0, top_score: 0.7 }),
      makeEvent({ outcome_used: null, top_score: 0.3 }),
    ];
    const metrics = computeMetrics(events, 0.65);

    // precision: 2 used / 3 with outcome ≈ 0.667
    expect(metrics.precision).toBeCloseTo(0.67, 1);
    // hitRate: same as precision here (2/3)
    expect(metrics.hitRate).toBeCloseTo(0.67, 1);
    // missRate: 1 event (score 0.3 < 0.65) / 4 total = 0.25
    expect(metrics.missRate).toBe(0.25);
    // MRR: simplified, 2 used / 3 with outcome ≈ 0.667
    expect(metrics.mrr).toBeCloseTo(0.67, 1);
    expect(metrics.totalEvents).toBe(4);
  });

  it("computes average latency", () => {
    const events = [
      makeEvent({ latency_ms: 10 }),
      makeEvent({ latency_ms: 20 }),
      makeEvent({ latency_ms: 30 }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.avgLatencyMs).toBe(20);
  });

  it("handles all misses", () => {
    const events = [
      makeEvent({ top_score: 0.4 }),
      makeEvent({ top_score: 0.5 }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.missRate).toBe(1.0);
  });

  it("handles no misses", () => {
    const events = [
      makeEvent({ top_score: 0.8 }),
      makeEvent({ top_score: 0.9 }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.missRate).toBe(0);
  });

  it("groups by tier correctly", () => {
    const events = [
      makeEvent({ top_tier: "warm", outcome_used: 1 }),
      makeEvent({ top_tier: "warm", outcome_used: 0 }),
      makeEvent({ top_tier: "cold", outcome_used: 1 }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.byTier["warm"]!.count).toBe(2);
    expect(metrics.byTier["warm"]!.precision).toBeCloseTo(0.5);
    expect(metrics.byTier["cold"]!.count).toBe(1);
    expect(metrics.byTier["cold"]!.precision).toBe(1.0);
  });

  it("groups by content type correctly", () => {
    const events = [
      makeEvent({ top_content_type: "memory", outcome_used: 1 }),
      makeEvent({ top_content_type: "memory", outcome_used: 0 }),
      makeEvent({ top_content_type: "knowledge", outcome_used: 1 }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.byContentType["memory"]!.count).toBe(2);
    expect(metrics.byContentType["memory"]!.precision).toBeCloseTo(0.5);
    expect(metrics.byContentType["knowledge"]!.count).toBe(1);
    expect(metrics.byContentType["knowledge"]!.precision).toBe(1.0);
  });

  it("handles null latency gracefully", () => {
    const events = [
      makeEvent({ latency_ms: null }),
      makeEvent({ latency_ms: null }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.avgLatencyMs).toBe(0);
  });

  it("handles events with no outcomes", () => {
    const events = [
      makeEvent({ outcome_used: null }),
      makeEvent({ outcome_used: null }),
    ];
    const metrics = computeMetrics(events, 0.65);
    expect(metrics.precision).toBe(0);
    expect(metrics.hitRate).toBe(0);
    expect(metrics.mrr).toBe(0);
  });
});
