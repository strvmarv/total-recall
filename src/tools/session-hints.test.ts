import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { insertEntry, updateEntry } from "../db/entries.js";
import { generateHints, getLastSessionAge } from "./session-tools.js";

describe("generateHints", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("returns corrections and preferences first", () => {
    insertEntry(db, "warm", "memory", {
      content: "Always use snake_case for variables",
      metadata: { entry_type: "preference" },
    });
    insertEntry(db, "warm", "memory", {
      content: "Do not mock the database in integration tests",
      metadata: { entry_type: "correction" },
    });
    insertEntry(db, "warm", "memory", {
      content: "Regular memory with no type",
    });

    const hints = generateHints(db, []);

    expect(hints.length).toBeGreaterThanOrEqual(2);
    expect(hints[0]).toContain("Do not mock");
    expect(hints[1]).toContain("snake_case");
  });

  it("includes frequently accessed memories", () => {
    const id = insertEntry(db, "warm", "memory", {
      content: "TODO list is at docs/TODO.md",
    });
    // Simulate high access count
    updateEntry(db, "warm", "memory", id, { touch: true });
    updateEntry(db, "warm", "memory", id, { touch: true });
    updateEntry(db, "warm", "memory", id, { touch: true });

    const hints = generateHints(db, []);

    expect(hints.some((h) => h.includes("TODO list"))).toBe(true);
  });

  it("includes recently promoted entries", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "Promoted from last session context",
    });

    const hints = generateHints(db, [id]);

    expect(hints.some((h) => h.includes("Promoted from last session"))).toBe(true);
  });

  it("caps at 5 hints", () => {
    for (let i = 0; i < 10; i++) {
      insertEntry(db, "warm", "memory", {
        content: `Correction number ${i}`,
        metadata: { entry_type: "correction" },
      });
    }

    const hints = generateHints(db, []);

    expect(hints.length).toBeLessThanOrEqual(5);
  });

  it("truncates long content to 120 chars", () => {
    const longContent = "A".repeat(200);
    insertEntry(db, "warm", "memory", {
      content: longContent,
      metadata: { entry_type: "correction" },
    });

    const hints = generateHints(db, []);

    expect(hints[0]!.length).toBeLessThanOrEqual(123); // 120 + "..."
  });

  it("deduplicates across priority sources", () => {
    const id = insertEntry(db, "warm", "memory", {
      content: "Deduplicated entry",
      metadata: { entry_type: "correction" },
    });
    // Also high access count — should not appear twice
    updateEntry(db, "warm", "memory", id, { touch: true });
    updateEntry(db, "warm", "memory", id, { touch: true });
    updateEntry(db, "warm", "memory", id, { touch: true });

    const hints = generateHints(db, []);

    const matches = hints.filter((h) => h.includes("Deduplicated"));
    expect(matches).toHaveLength(1);
  });

  it("returns empty array when no entries exist", () => {
    const hints = generateHints(db, []);
    expect(hints).toEqual([]);
  });
});

describe("getLastSessionAge", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("returns null when no compaction log entries exist", () => {
    expect(getLastSessionAge(db)).toBeNull();
  });

  it("returns relative time for recent compaction", () => {
    const twoHoursAgo = Date.now() - 2 * 60 * 60 * 1000;
    db.prepare(
      `INSERT INTO compaction_log (id, timestamp, session_id, source_tier, source_entry_ids, decay_scores, reason, config_snapshot_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-id", twoHoursAgo, "sess-1", "hot", "[]", "{}", "decay_score_below_warm_threshold", "snap-1");

    const age = getLastSessionAge(db);

    expect(age).toBe("2 hours ago");
  });

  it("returns minutes for very recent sessions", () => {
    const fiveMinAgo = Date.now() - 5 * 60 * 1000;
    db.prepare(
      `INSERT INTO compaction_log (id, timestamp, session_id, source_tier, source_entry_ids, decay_scores, reason, config_snapshot_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-id", fiveMinAgo, "sess-1", "hot", "[]", "{}", "decay_score_below_warm_threshold", "snap-1");

    const age = getLastSessionAge(db);

    expect(age).toBe("5 minutes ago");
  });

  it("returns days for older sessions", () => {
    const threeDaysAgo = Date.now() - 3 * 24 * 60 * 60 * 1000;
    db.prepare(
      `INSERT INTO compaction_log (id, timestamp, session_id, source_tier, source_entry_ids, decay_scores, reason, config_snapshot_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-id", threeDaysAgo, "sess-1", "hot", "[]", "{}", "decay_score_below_warm_threshold", "snap-1");

    const age = getLastSessionAge(db);

    expect(age).toBe("3 days ago");
  });

  it("returns 'just now' for very recent sessions", () => {
    const tenSecsAgo = Date.now() - 10 * 1000;
    db.prepare(
      `INSERT INTO compaction_log (id, timestamp, session_id, source_tier, source_entry_ids, decay_scores, reason, config_snapshot_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-id", tenSecsAgo, "sess-1", "hot", "[]", "{}", "decay_score_below_warm_threshold", "snap-1");

    expect(getLastSessionAge(db)).toBe("just now");
  });

  it("uses singular form for 1 unit", () => {
    const oneHourAgo = Date.now() - 1 * 60 * 60 * 1000;
    db.prepare(
      `INSERT INTO compaction_log (id, timestamp, session_id, source_tier, source_entry_ids, decay_scores, reason, config_snapshot_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-id", oneHourAgo, "sess-1", "hot", "[]", "{}", "decay_score_below_warm_threshold", "snap-1");

    expect(getLastSessionAge(db)).toBe("1 hour ago");
  });

  it("returns weeks for very old sessions", () => {
    const threeWeeksAgo = Date.now() - 21 * 24 * 60 * 60 * 1000;
    db.prepare(
      `INSERT INTO compaction_log (id, timestamp, session_id, source_tier, source_entry_ids, decay_scores, reason, config_snapshot_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run("test-id", threeWeeksAgo, "sess-1", "hot", "[]", "{}", "decay_score_below_warm_threshold", "snap-1");

    const age = getLastSessionAge(db);

    expect(age).toBe("3 weeks ago");
  });
});
