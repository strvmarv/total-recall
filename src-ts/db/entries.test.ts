import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import {
  insertEntry,
  getEntry,
  updateEntry,
  deleteEntry,
  listEntries,
  countEntries,
  moveEntry,
  listEntriesByMetadata,
} from "./entries.js";

describe("entries CRUD", () => {
  let db: Database;

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

describe("listEntriesByMetadata", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("filters entries by metadata key-value pair", () => {
    insertEntry(db, "warm", "memory", {
      content: "correction entry",
      metadata: { entry_type: "correction" },
    });
    insertEntry(db, "warm", "memory", {
      content: "preference entry",
      metadata: { entry_type: "preference" },
    });
    insertEntry(db, "warm", "memory", {
      content: "no type entry",
    });

    const results = listEntriesByMetadata(db, "warm", "memory", {
      entry_type: "correction",
    });

    expect(results).toHaveLength(1);
    expect(results[0]!.content).toBe("correction entry");
  });

  it("filters by multiple metadata keys", () => {
    insertEntry(db, "warm", "memory", {
      content: "match both",
      metadata: { entry_type: "correction", source_context: "session" },
    });
    insertEntry(db, "warm", "memory", {
      content: "match one",
      metadata: { entry_type: "correction" },
    });

    const results = listEntriesByMetadata(db, "warm", "memory", {
      entry_type: "correction",
      source_context: "session",
    });

    expect(results).toHaveLength(1);
    expect(results[0]!.content).toBe("match both");
  });

  it("respects orderBy and limit options", () => {
    insertEntry(db, "warm", "memory", {
      content: "first",
      metadata: { entry_type: "correction" },
    });
    insertEntry(db, "warm", "memory", {
      content: "second",
      metadata: { entry_type: "correction" },
    });
    insertEntry(db, "warm", "memory", {
      content: "third",
      metadata: { entry_type: "correction" },
    });

    const results = listEntriesByMetadata(
      db, "warm", "memory",
      { entry_type: "correction" },
      { orderBy: "created_at ASC", limit: 2 },
    );

    expect(results).toHaveLength(2);
    expect(results[0]!.content).toBe("first");
    expect(results[1]!.content).toBe("second");
  });

  it("returns empty array when no entries match", () => {
    insertEntry(db, "warm", "memory", {
      content: "no metadata",
    });

    const results = listEntriesByMetadata(db, "warm", "memory", {
      entry_type: "correction",
    });

    expect(results).toHaveLength(0);
  });

  it("throws on empty metadataFilter", () => {
    expect(() =>
      listEntriesByMetadata(db, "warm", "memory", {}),
    ).toThrow("metadataFilter must contain at least one key-value pair");
  });

  it("throws on invalid metadata key", () => {
    expect(() =>
      listEntriesByMetadata(db, "warm", "memory", { "bad'; DROP TABLE--": "val" }),
    ).toThrow("Invalid metadata key");
  });
});
