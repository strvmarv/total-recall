import { describe, it, expect } from "vitest";
import { calculateDecayScore, TYPE_WEIGHTS } from "./decay.js";
import type { TotalRecallConfig } from "../types.js";

const defaultCompaction: TotalRecallConfig["compaction"] = {
  decay_half_life_hours: 168,
  warm_threshold: 0.3,
  promote_threshold: 0.7,
  warm_sweep_interval_days: 7,
};

describe("calculateDecayScore", () => {
  it("returns ~1.0 for brand new entry", () => {
    const now = Date.now();
    const score = calculateDecayScore(
      {
        last_accessed_at: now,
        created_at: now,
        access_count: 0,
        type: "decision",
      },
      defaultCompaction,
      now,
    );
    // timeFactor ≈ 1, freqFactor = 1 + log2(1) = 1, typeWeight = 1.0 → score ≈ 1.0
    expect(score).toBeCloseTo(1.0, 2);
  });

  it("decays over time", () => {
    const now = Date.now();
    const oneWeekMs = 7 * 24 * 60 * 60 * 1000;

    const freshScore = calculateDecayScore(
      {
        last_accessed_at: now,
        created_at: now,
        access_count: 0,
        type: "decision",
      },
      defaultCompaction,
      now,
    );

    const oldScore = calculateDecayScore(
      {
        last_accessed_at: now - oneWeekMs,
        created_at: now - oneWeekMs,
        access_count: 0,
        type: "decision",
      },
      defaultCompaction,
      now,
    );

    expect(oldScore).toBeLessThan(freshScore);
    // One week = 168 hours = decay_half_life_hours, so timeFactor = exp(-1)
    // oldScore ≈ exp(-1) * freshScore
    expect(oldScore).toBeCloseTo(freshScore * Math.exp(-1), 2);
  });

  it("boosts with higher access count", () => {
    const now = Date.now();
    const base = {
      last_accessed_at: now,
      created_at: now,
      type: "decision",
    };

    const lowAccess = calculateDecayScore(
      { ...base, access_count: 0 },
      defaultCompaction,
      now,
    );

    const highAccess = calculateDecayScore(
      { ...base, access_count: 10 },
      defaultCompaction,
      now,
    );

    expect(highAccess).toBeGreaterThan(lowAccess);
  });

  it("type weights — corrections decay slower than surfaced", () => {
    const now = Date.now();
    const base = {
      last_accessed_at: now,
      created_at: now,
      access_count: 0,
    };

    const correctionScore = calculateDecayScore(
      { ...base, type: "correction" },
      defaultCompaction,
      now,
    );

    const surfacedScore = calculateDecayScore(
      { ...base, type: "surfaced" },
      defaultCompaction,
      now,
    );

    expect(correctionScore).toBeGreaterThan(surfacedScore);
    // The ratio should match the type weight ratio
    expect(correctionScore / surfacedScore).toBeCloseTo(
      (TYPE_WEIGHTS["correction"] as number) / (TYPE_WEIGHTS["surfaced"] as number),
      5,
    );
  });
});
