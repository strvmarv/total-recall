import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { createTestDb } from "../tests/helpers/db.js";
import { mockEmbedSemantic } from "../tests/helpers/embedding.js";
import { storeMemory } from "./memory/store.js";
import { searchMemory } from "./memory/search.js";
import { getMemory } from "./memory/get.js";
import { promoteEntry } from "./memory/promote-demote.js";
import { countEntries } from "./db/entries.js";
import type Database from "better-sqlite3";

const embed = mockEmbedSemantic;

describe("total-recall e2e", () => {
  let db: Database.Database;
  beforeEach(() => {
    db = createTestDb();
  });
  afterEach(() => {
    db.close();
  });

  it("full lifecycle: store -> search -> promote -> search in new tier", () => {
    // Store a correction in hot
    const id = storeMemory(db, embed, {
      content: "always use pnpm, never npm",
      type: "correction",
      project: "my-app",
      tags: ["tooling"],
    });

    // Verify it's in hot
    expect(countEntries(db, "hot", "memory")).toBe(1);
    expect(countEntries(db, "warm", "memory")).toBe(0);

    // Search finds it in hot
    const hotResults = searchMemory(db, embed, "package manager", {
      tiers: [{ tier: "hot", content_type: "memory" }],
      topK: 10,
    });
    const hotFiltered = hotResults.filter(
      (r) => r.entry.project === "my-app",
    );
    expect(hotFiltered.length).toBe(1);
    expect(hotFiltered[0]!.entry.content).toContain("pnpm");

    // Promote to warm
    promoteEntry(db, embed, id, "hot", "memory", "warm", "memory");

    // Verify tier move
    expect(countEntries(db, "hot", "memory")).toBe(0);
    expect(countEntries(db, "warm", "memory")).toBe(1);

    // Search finds it in warm
    const warmResults = searchMemory(db, embed, "package manager", {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 10,
    });
    const warmFiltered = warmResults.filter(
      (r) => r.entry.project === "my-app",
    );
    expect(warmFiltered.length).toBe(1);
    expect(warmFiltered[0]!.tier).toBe("warm");
  });

  it("multi-project isolation", () => {
    storeMemory(db, embed, { content: "project A uses React", project: "a" });
    storeMemory(db, embed, { content: "project B uses Vue", project: "b" });
    storeMemory(db, embed, {
      content: "always use TypeScript",
      project: null,
    });

    // Search all hot memories then filter: project A or global (null project)
    const allResults = searchMemory(db, embed, "frontend framework", {
      tiers: [{ tier: "hot", content_type: "memory" }],
      topK: 10,
    });

    // Filter to project A + global (includeGlobal equivalent)
    const aResults = allResults.filter(
      (r) => r.entry.project === "a" || r.entry.project === null,
    );

    const contents = aResults.map((r) => r.entry.content);
    expect(contents.some((c) => c.includes("React"))).toBe(true);
    expect(contents.some((c) => c.includes("TypeScript"))).toBe(true);
    expect(contents.some((c) => c.includes("Vue"))).toBe(false);
  });

  it("cross-tier ranked search", () => {
    storeMemory(db, embed, { content: "hot auth memory", tier: "hot" });
    storeMemory(db, embed, { content: "warm auth pattern", tier: "warm" });
    storeMemory(db, embed, {
      content: "cold auth docs",
      tier: "cold",
      contentType: "knowledge",
    });

    const results = searchMemory(db, embed, "authentication", {
      tiers: [
        { tier: "hot", content_type: "memory" },
        { tier: "warm", content_type: "memory" },
        { tier: "cold", content_type: "knowledge" },
      ],
      topK: 10,
    });
    expect(results.length).toBe(3);
    // Results should be ranked by score
    for (let i = 1; i < results.length; i++) {
      expect(results[i - 1]!.score).toBeGreaterThanOrEqual(results[i]!.score);
    }
  });
});
