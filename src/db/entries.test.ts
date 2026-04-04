import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import {
  insertEntry,
  getEntry,
  updateEntry,
  deleteEntry,
  listEntries,
  countEntries,
  moveEntry,
} from "./entries.js";

describe("entries CRUD", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("inserts and retrieves an entry", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "test content",
      source: "test-source",
      project: "my-project",
      tags: ["alpha", "beta"],
    });

    expect(typeof id).toBe("string");
    expect(id.length).toBeGreaterThan(0);

    const entry = getEntry(db, "hot", "memory", id);
    expect(entry).not.toBeNull();
    expect(entry!.id).toBe(id);
    expect(entry!.content).toBe("test content");
    expect(entry!.source).toBe("test-source");
    expect(entry!.project).toBe("my-project");
    expect(entry!.tags).toEqual(["alpha", "beta"]);
    expect(entry!.access_count).toBe(0);
    expect(entry!.decay_score).toBe(1.0);
    expect(entry!.metadata).toEqual({});
    expect(typeof entry!.created_at).toBe("number");
    expect(typeof entry!.updated_at).toBe("number");
    expect(typeof entry!.last_accessed_at).toBe("number");
  });

  it("updates an entry", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "original content",
      tags: ["old"],
    });

    const before = getEntry(db, "hot", "memory", id)!;

    updateEntry(db, "hot", "memory", id, {
      content: "updated content",
      tags: ["new", "tags"],
    });

    const after = getEntry(db, "hot", "memory", id)!;
    expect(after.content).toBe("updated content");
    expect(after.tags).toEqual(["new", "tags"]);
    expect(after.updated_at).toBeGreaterThanOrEqual(before.created_at);
  });

  it("soft deletes an entry", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "to be deleted",
    });

    const before = getEntry(db, "hot", "memory", id);
    expect(before).not.toBeNull();

    deleteEntry(db, "hot", "memory", id);

    const after = getEntry(db, "hot", "memory", id);
    expect(after).toBeNull();
  });

  it("lists entries with optional project filter", () => {
    insertEntry(db, "hot", "memory", { content: "no project" });
    insertEntry(db, "hot", "memory", { content: "project a", project: "a" });
    insertEntry(db, "hot", "memory", { content: "project b", project: "b" });

    const all = listEntries(db, "hot", "memory");
    expect(all.length).toBe(3);

    const projA = listEntries(db, "hot", "memory", { project: "a" });
    expect(projA.length).toBe(1);
    expect(projA[0]!.project).toBe("a");

    const projAWithGlobal = listEntries(db, "hot", "memory", {
      project: "a",
      includeGlobal: true,
    });
    expect(projAWithGlobal.length).toBe(2);
  });

  it("increments access count and updates last_accessed_at", () => {
    const id = insertEntry(db, "hot", "memory", {
      content: "tracked entry",
    });

    const before = getEntry(db, "hot", "memory", id)!;
    expect(before.access_count).toBe(0);

    updateEntry(db, "hot", "memory", id, { touch: true });

    const after = getEntry(db, "hot", "memory", id)!;
    expect(after.access_count).toBe(1);
    expect(after.last_accessed_at).toBeGreaterThanOrEqual(before.last_accessed_at);
  });
});
