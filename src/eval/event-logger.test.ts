import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { logRetrievalEvent, updateOutcome, getRetrievalEvents } from "./event-logger.js";

const minimalOpts = {
  sessionId: "sess-1",
  queryText: "auth middleware",
  querySource: "auto" as const,
  results: [{ entry_id: "e1", tier: "warm", content_type: "memory", score: 0.89, rank: 0 }],
  tiersSearched: ["warm"],
  configSnapshotId: "default",
  latencyMs: 5,
};

describe("event-logger", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("logs a retrieval event and retrieves it", () => {
    const id = logRetrievalEvent(db, minimalOpts);
    expect(typeof id).toBe("string");
    expect(id.length).toBeGreaterThan(0);

    const events = getRetrievalEvents(db, { sessionId: "sess-1" });
    expect(events).toHaveLength(1);
    expect(events[0]!.query_text).toBe("auth middleware");
    expect(events[0]!.top_score).toBe(0.89);
    expect(events[0]!.top_tier).toBe("warm");
    expect(events[0]!.top_content_type).toBe("memory");
    expect(events[0]!.result_count).toBe(1);
    expect(events[0]!.session_id).toBe("sess-1");
    expect(events[0]!.config_snapshot_id).toBe("default");
    expect(events[0]!.latency_ms).toBe(5);
  });

  it("updates outcome after the fact", () => {
    const id = logRetrievalEvent(db, minimalOpts);
    updateOutcome(db, id, { used: true, signal: "positive" });

    const events = getRetrievalEvents(db, { sessionId: "sess-1" });
    expect(events).toHaveLength(1);
    expect(events[0]!.outcome_used).toBe(1);
    expect(events[0]!.outcome_signal).toBe("positive");
  });

  it("returns empty array when no events match filter", () => {
    logRetrievalEvent(db, minimalOpts);
    const events = getRetrievalEvents(db, { sessionId: "other-session" });
    expect(events).toHaveLength(0);
  });

  it("filters by configSnapshotId", () => {
    logRetrievalEvent(db, { ...minimalOpts, configSnapshotId: "snap-a" });
    logRetrievalEvent(db, { ...minimalOpts, configSnapshotId: "snap-b" });

    const events = getRetrievalEvents(db, { configSnapshotId: "snap-a" });
    expect(events).toHaveLength(1);
    expect(events[0]!.config_snapshot_id).toBe("snap-a");
  });

  it("respects limit option", () => {
    logRetrievalEvent(db, minimalOpts);
    logRetrievalEvent(db, minimalOpts);
    logRetrievalEvent(db, minimalOpts);

    const events = getRetrievalEvents(db, { sessionId: "sess-1", limit: 2 });
    expect(events).toHaveLength(2);
  });

  it("handles events with no results", () => {
    const id = logRetrievalEvent(db, {
      ...minimalOpts,
      results: [],
      tiersSearched: ["warm", "cold"],
    });

    const events = getRetrievalEvents(db, { sessionId: "sess-1" });
    expect(events).toHaveLength(1);
    expect(events[0]!.result_count).toBe(0);
    expect(events[0]!.top_score).toBeNull();
    expect(events[0]!.top_tier).toBeNull();
    expect(events[0]!.top_content_type).toBeNull();
  });

  it("stores and retrieves results as JSON string", () => {
    logRetrievalEvent(db, minimalOpts);
    const events = getRetrievalEvents(db, { sessionId: "sess-1" });
    const parsed = JSON.parse(events[0]!.results);
    expect(parsed).toHaveLength(1);
    expect(parsed[0].entry_id).toBe("e1");
    expect(parsed[0].score).toBe(0.89);
  });
});
