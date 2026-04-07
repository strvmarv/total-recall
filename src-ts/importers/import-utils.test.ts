import { describe, it, expect, beforeEach, afterEach } from "vitest";
import type { Database } from "bun:sqlite";
import { createTestDb } from "../../tests-ts/helpers/db.js";
import { parseFrontmatter, contentHash, isAlreadyImported, logImport } from "./import-utils.js";

describe("parseFrontmatter", () => {
  it("extracts name, description, and type from valid YAML frontmatter", () => {
    const raw = "---\nname: my-rule\ndescription: A test rule\ntype: feedback\n---\nThe actual content.";
    const { frontmatter, content } = parseFrontmatter(raw);

    expect(frontmatter).not.toBeNull();
    expect(frontmatter!.name).toBe("my-rule");
    expect(frontmatter!.description).toBe("A test rule");
    expect(frontmatter!.type).toBe("feedback");
    expect(content).toBe("The actual content.");
  });

  it("returns null frontmatter when no delimiters present", () => {
    const raw = "Just plain content with no frontmatter.";
    const { frontmatter, content } = parseFrontmatter(raw);

    expect(frontmatter).toBeNull();
    expect(content).toBe(raw);
  });

  it("handles empty frontmatter block as no-frontmatter", () => {
    // Empty frontmatter (---\n---\n) doesn't match the regex pattern,
    // so it's treated as plain content — this is expected behavior
    const raw = "---\n---\nContent after empty frontmatter.";
    const { frontmatter, content } = parseFrontmatter(raw);

    expect(frontmatter).toBeNull();
    expect(content).toBe(raw);
  });

  it("handles CRLF line endings", () => {
    const raw = "---\r\nname: crlf-test\r\ndescription: Windows file\r\n---\r\nContent here.";
    const { frontmatter, content } = parseFrontmatter(raw);

    expect(frontmatter).not.toBeNull();
    expect(frontmatter!.name).toBe("crlf-test");
    expect(content).toBe("Content here.");
  });
});

describe("contentHash", () => {
  it("returns consistent hash for same input", () => {
    const hash1 = contentHash("hello world");
    const hash2 = contentHash("hello world");
    expect(hash1).toBe(hash2);
    expect(hash1).toMatch(/^[0-9a-f]{64}$/);
  });

  it("returns different hashes for different input", () => {
    const hash1 = contentHash("hello");
    const hash2 = contentHash("world");
    expect(hash1).not.toBe(hash2);
  });
});

describe("isAlreadyImported / logImport", () => {
  let db: Database;

  beforeEach(() => {
    db = createTestDb();
  });

  afterEach(() => {
    db.close();
  });

  it("returns false for unknown hash", () => {
    expect(isAlreadyImported(db, "nonexistent-hash")).toBe(false);
  });

  it("returns true after logImport with same hash", () => {
    const hash = contentHash("test content");
    logImport(db, "test-tool", "/path/to/file", hash, "entry-123", "warm", "memory");

    expect(isAlreadyImported(db, hash)).toBe(true);
  });

  it("is idempotent — re-logging same content does not throw", () => {
    const hash = contentHash("test content");
    logImport(db, "test-tool", "/path/to/file", hash, "entry-123", "warm", "memory");
    expect(() => {
      logImport(db, "test-tool", "/path/to/file", hash, "entry-123", "warm", "memory");
    }).not.toThrow();
  });
});
