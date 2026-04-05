import { describe, it, expect, beforeEach } from "vitest";
import type Database from "better-sqlite3";
import { createTestDb } from "../../tests/helpers/db.js";
import { mockEmbedSemantic } from "../../tests/helpers/embedding.js";
import {
  createCollection,
  addDocumentToCollection,
  getCollection,
  listCollections,
  getDocumentChunks,
} from "./hierarchical-index.js";

describe("hierarchical index", () => {
  let db: Database.Database;

  beforeEach(() => {
    db = createTestDb();
  });

  it("creates a collection and adds documents with chunks", async () => {
    const collId = await createCollection(db, mockEmbedSemantic, {
      name: "auth-docs",
      sourcePath: "docs/auth/",
    });

    const docId = await addDocumentToCollection(db, mockEmbedSemantic, {
      collectionId: collId,
      sourcePath: "docs/auth/oauth-flow.md",
      chunks: [
        { content: "OAuth2 flow description", headingPath: ["Auth", "OAuth Flow"] },
        { content: "Token refresh mechanism", headingPath: ["Auth", "Token Refresh"] },
      ],
    });

    const collection = getCollection(db, collId);
    expect(collection).not.toBeNull();
    expect(collection!.name).toBe("auth-docs");

    const chunks = getDocumentChunks(db, docId);
    expect(chunks).toHaveLength(2);
  });

  it("lists all collections", async () => {
    await createCollection(db, mockEmbedSemantic, { name: "auth", sourcePath: "docs/auth/" });
    await createCollection(db, mockEmbedSemantic, { name: "deploy", sourcePath: "docs/deploy/" });
    expect(listCollections(db)).toHaveLength(2);
  });

  it("returns null for non-existent collection id", () => {
    const result = getCollection(db, "00000000-0000-0000-0000-000000000000");
    expect(result).toBeNull();
  });

  it("stores chunk metadata correctly", async () => {
    const collId = await createCollection(db, mockEmbedSemantic, {
      name: "code-docs",
      sourcePath: "src/",
    });

    const docId = await addDocumentToCollection(db, mockEmbedSemantic, {
      collectionId: collId,
      sourcePath: "src/utils.ts",
      chunks: [
        { content: "function helper() {}", name: "helper", kind: "function" },
      ],
    });

    const chunks = getDocumentChunks(db, docId);
    expect(chunks).toHaveLength(1);
    expect(chunks[0]!.metadata["name"]).toBe("helper");
    expect(chunks[0]!.metadata["kind"]).toBe("function");
  });

  it("associates chunks with collection_id", async () => {
    const collId = await createCollection(db, mockEmbedSemantic, {
      name: "my-collection",
      sourcePath: "docs/",
    });

    const docId = await addDocumentToCollection(db, mockEmbedSemantic, {
      collectionId: collId,
      sourcePath: "docs/intro.md",
      chunks: [{ content: "Introduction content here." }],
    });

    const chunks = getDocumentChunks(db, docId);
    expect(chunks[0]!.collection_id).toBe(collId);
  });
});
