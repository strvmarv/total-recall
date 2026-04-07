import type { Database } from "bun:sqlite";
import type { Entry } from "../types.js";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";

interface RawRow {
  id: string;
  content: string;
  summary: string | null;
  source: string | null;
  source_tool: string | null;
  project: string | null;
  tags: string | null;
  created_at: number;
  updated_at: number;
  last_accessed_at: number;
  access_count: number;
  decay_score: number;
  parent_id: string | null;
  collection_id: string | null;
  metadata: string | null;
}

function rowToEntry(row: RawRow): Entry {
  return {
    id: row.id,
    content: row.content,
    summary: row.summary,
    source: row.source,
    source_tool: row.source_tool as Entry["source_tool"],
    project: row.project,
    tags: row.tags ? (JSON.parse(row.tags) as string[]) : [],
    created_at: row.created_at,
    updated_at: row.updated_at,
    last_accessed_at: row.last_accessed_at,
    access_count: row.access_count,
    decay_score: row.decay_score,
    parent_id: row.parent_id,
    collection_id: row.collection_id,
    metadata: row.metadata ? (JSON.parse(row.metadata) as Record<string, unknown>) : {},
  };
}

export type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export interface CreateCollectionOpts {
  name: string;
  sourcePath: string;
}

export interface ChunkInput {
  content: string;
  headingPath?: string[];
  name?: string;
  kind?: string;
}

export interface AddDocumentOpts {
  collectionId: string;
  sourcePath: string;
  chunks: ChunkInput[];
}

export async function createCollection(
  db: Database,
  embed: EmbedFn,
  opts: CreateCollectionOpts,
): Promise<string> {
  const content = `Collection: ${opts.name}`;
  const id = insertEntry(db, "cold", "knowledge", {
    content,
    source: opts.sourcePath,
    metadata: {
      type: "collection",
      name: opts.name,
      source_path: opts.sourcePath,
    },
  });

  const embedding = await embed(content);
  insertEmbedding(db, "cold", "knowledge", id, embedding);

  return id;
}

export async function addDocumentToCollection(
  db: Database,
  embed: EmbedFn,
  opts: AddDocumentOpts,
): Promise<string> {
  const joined = opts.chunks.map((c) => c.content).join("\n\n");
  const docContent = joined.slice(0, 500);

  const docId = insertEntry(db, "cold", "knowledge", {
    content: docContent,
    source: opts.sourcePath,
    collection_id: opts.collectionId,
    metadata: {
      type: "document",
      source_path: opts.sourcePath,
      chunk_count: opts.chunks.length,
    },
  });

  const docEmbedding = await embed(docContent);
  insertEmbedding(db, "cold", "knowledge", docId, docEmbedding);

  for (const chunk of opts.chunks) {
    const chunkId = insertEntry(db, "cold", "knowledge", {
      content: chunk.content,
      source: opts.sourcePath,
      parent_id: docId,
      collection_id: opts.collectionId,
      metadata: {
        type: "chunk",
        heading_path: chunk.headingPath,
        name: chunk.name,
        kind: chunk.kind,
      },
    });

    const chunkEmbedding = await embed(chunk.content);
    insertEmbedding(db, "cold", "knowledge", chunkId, chunkEmbedding);
  }

  return docId;
}

export function getCollection(
  db: Database,
  id: string,
): (Entry & { name: string }) | null {
  const row = db
    .prepare(`SELECT * FROM cold_knowledge WHERE id = ?`)
    .get(id) as RawRow | undefined;

  if (!row) return null;

  const entry = rowToEntry(row);
  const metadata = entry.metadata as Record<string, unknown>;
  if (metadata["type"] !== "collection") return null;

  return { ...entry, name: metadata["name"] as string };
}

export function listCollections(db: Database): Array<Entry & { name: string }> {
  const rows = db
    .prepare(`SELECT * FROM cold_knowledge WHERE json_extract(metadata, '$.type') = 'collection'`)
    .all() as RawRow[];

  return rows.map((row) => {
    const entry = rowToEntry(row);
    const metadata = entry.metadata as Record<string, unknown>;
    return { ...entry, name: metadata["name"] as string };
  });
}

export function getDocumentChunks(db: Database, docId: string): Entry[] {
  const rows = db
    .prepare(`SELECT * FROM cold_knowledge WHERE parent_id = ?`)
    .all(docId) as RawRow[];

  return rows.map(rowToEntry);
}
