import { describe, it, expect } from "vitest";
import { formatEvalReport } from "./eval-formatter.js";

describe("formatEvalReport", () => {
  it("formats metrics into a readable report", () => {
    const output = formatEvalReport({
      precision: 0.72,
      hitRate: 0.88,
      missRate: 0.12,
      mrr: 0.81,
      avgLatencyMs: 6,
      totalEvents: 847,
      byTier: {
        warm: { precision: 0.68, hitRate: 0.84, avgScore: 0.79, count: 389 },
      },
      byContentType: {
        memory: { precision: 0.76, hitRate: 0.89, count: 534 },
      },
      topMisses: [{ query: "missing query", topScore: 0.2, timestamp: Date.now() }],
      falsePositives: [{ query: "false positive", topScore: 0.9, timestamp: Date.now() }],
      compactionHealth: { totalCompactions: 5, avgPreservationRatio: 0.85, entriesWithDrift: 1 },
    });
    expect(output).toContain("Precision");
    expect(output).toContain("0.72");
    expect(output).toContain("warm");
  });
});
