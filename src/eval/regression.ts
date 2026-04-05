import type Database from "better-sqlite3";
import { getRetrievalEvents } from "./event-logger.js";
import { computeMetrics } from "./metrics.js";

export interface RegressionConfig {
  miss_rate_delta: number;
  latency_ratio: number;
  min_events: number;
}

export interface RegressionAlert {
  metric: "miss_rate" | "latency";
  previous: number;
  current: number;
  delta: number;
  threshold: number;
}

export function checkRegressions(
  db: Database.Database,
  config: RegressionConfig,
  similarityThreshold: number,
): RegressionAlert[] | null {
  // Need at least 2 config snapshots
  const snapshots = db.prepare(
    "SELECT id FROM config_snapshots ORDER BY timestamp DESC LIMIT 2"
  ).all() as Array<{ id: string }>;

  if (snapshots.length < 2) return null;

  const currentSnapshotId = snapshots[0]!.id;
  const previousSnapshotId = snapshots[1]!.id;

  const currentEvents = getRetrievalEvents(db, { configSnapshotId: currentSnapshotId });
  const previousEvents = getRetrievalEvents(db, { configSnapshotId: previousSnapshotId });

  if (currentEvents.length < config.min_events || previousEvents.length < config.min_events) {
    return null;
  }

  const currentMetrics = computeMetrics(currentEvents, similarityThreshold);
  const previousMetrics = computeMetrics(previousEvents, similarityThreshold);

  const alerts: RegressionAlert[] = [];

  // Check miss rate
  const missRateDelta = currentMetrics.missRate - previousMetrics.missRate;
  if (missRateDelta >= config.miss_rate_delta) {
    alerts.push({
      metric: "miss_rate",
      previous: previousMetrics.missRate,
      current: currentMetrics.missRate,
      delta: missRateDelta,
      threshold: config.miss_rate_delta,
    });
  }

  // Check latency
  if (previousMetrics.avgLatencyMs > 0) {
    const latencyRatio = currentMetrics.avgLatencyMs / previousMetrics.avgLatencyMs;
    if (latencyRatio >= config.latency_ratio) {
      alerts.push({
        metric: "latency",
        previous: previousMetrics.avgLatencyMs,
        current: currentMetrics.avgLatencyMs,
        delta: latencyRatio,
        threshold: config.latency_ratio,
      });
    }
  }

  return alerts;
}
