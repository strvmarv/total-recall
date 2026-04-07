import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { randomUUID } from "node:crypto";
import { createTestDb } from "../../tests/helpers/db.js";
import { checkRegressions, type RegressionConfig } from "./regression.js";
import { logRetrievalEvent } from "./event-logger.js";
import type { Database } from "bun:sqlite";

const defaultRegressionConfig: RegressionConfig = {
  miss_rate_delta: 0.1,
  latency_ratio: 2.0,
  min_events: 3,
};

let snapshotCounter = 0;

/**
 * Insert a config snapshot directly, bypassing dedup logic in createConfigSnapshot.
 * Uses an incrementing timestamp offset to ensure distinct ordering.
 */
function insertSnapshot(db: Database, name: string): string {
  const id = randomUUID();
  const timestamp = Date.now() + snapshotCounter++;
  db.prepare(
    "INSERT INTO config_snapshots (id, name, timestamp, config) VALUES (?, ?, ?, ?)",
  ).run(id, name, timestamp, JSON.stringify({ name }));
  return id;
}

function seedEvents(
  db: Database,
  snapshotId: string,
  count: number,
  opts: { topScore?: number; latencyMs?: number } = {},
): void {
  for (let i = 0; i < count; i++) {
    logRetrievalEvent(db, {
      sessionId: "s1",
      queryText: `query-${snapshotId}-${i}`,
      querySource: "explicit",
      results: [{ entry_id: `e${i}`, tier: "warm", content_type: "memory", score: opts.topScore ?? 0.8, rank: 0 }],
      tiersSearched: ["warm"],
      configSnapshotId: snapshotId,
      latencyMs: opts.latencyMs ?? 50,
    });
  }
}

describe("checkRegressions", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("returns null when fewer than 2 snapshots", () => {
    insertSnapshot(db, "only-one");
    const alerts = checkRegressions(db, defaultRegressionConfig, 0.65);
    expect(alerts).toBeNull();
  });

  it("returns null when not enough events", () => {
    const s1 = insertSnapshot(db, "s1");
    const s2 = insertSnapshot(db, "s2");
    seedEvents(db, s1, 2);
    seedEvents(db, s2, 2);
    const alerts = checkRegressions(db, defaultRegressionConfig, 0.65);
    expect(alerts).toBeNull();
  });

  it("returns empty array when no regression", () => {
    const s1 = insertSnapshot(db, "s1");
    const s2 = insertSnapshot(db, "s2");
    seedEvents(db, s1, 5, { topScore: 0.8, latencyMs: 50 });
    seedEvents(db, s2, 5, { topScore: 0.8, latencyMs: 50 });
    const alerts = checkRegressions(db, defaultRegressionConfig, 0.65);
    expect(alerts).toEqual([]);
  });

  it("detects miss rate regression", () => {
    const s1 = insertSnapshot(db, "s1");
    const s2 = insertSnapshot(db, "s2");
    seedEvents(db, s1, 5, { topScore: 0.8 });
    seedEvents(db, s2, 5, { topScore: 0.3 });
    const alerts = checkRegressions(db, defaultRegressionConfig, 0.65);
    expect(alerts).not.toBeNull();
    const missAlert = alerts!.find((a) => a.metric === "miss_rate");
    expect(missAlert).toBeDefined();
    expect(missAlert!.current).toBeGreaterThan(missAlert!.previous);
  });

  it("detects latency regression", () => {
    const s1 = insertSnapshot(db, "s1");
    const s2 = insertSnapshot(db, "s2");
    seedEvents(db, s1, 5, { latencyMs: 50 });
    seedEvents(db, s2, 5, { latencyMs: 150 });
    const alerts = checkRegressions(db, defaultRegressionConfig, 0.65);
    expect(alerts).not.toBeNull();
    const latencyAlert = alerts!.find((a) => a.metric === "latency");
    expect(latencyAlert).toBeDefined();
  });
});
