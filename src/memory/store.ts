import type { Database } from "bun:sqlite";
import { insertEntry } from "../db/entries.js";
import { insertEmbedding } from "../search/vector-search.js";
import type { Tier, ContentType, EntryType, SourceTool } from "../types.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

interface StoreOptions {
  content: string;
  type?: EntryType;
  tier?: Tier;
  contentType?: ContentType;
  project?: string | null;
  tags?: string[];
  source?: string;
  source_tool?: SourceTool;
  parent_id?: string;
  collection_id?: string;
}

export async function storeMemory(db: Database, embed: EmbedFn, opts: StoreOptions): Promise<string> {
  const tier = opts.tier ?? "hot";
  const contentType = opts.contentType ?? "memory";
  const id = insertEntry(db, tier, contentType, {
    content: opts.content,
    source: opts.source ?? null,
    source_tool: opts.source_tool ?? "manual",
    project: opts.project ?? null,
    tags: opts.tags ?? [],
    parent_id: opts.parent_id,
    collection_id: opts.collection_id,
    metadata: opts.type ? { entry_type: opts.type } : {},
  });
  const embedding = await embed(opts.content);
  insertEmbedding(db, tier, contentType, id, embedding);
  return id;
}
