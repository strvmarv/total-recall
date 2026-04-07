import type { Database } from "bun:sqlite";
import { updateEntry } from "../db/entries.js";
import { deleteEmbedding, insertEmbedding } from "../search/vector-search.js";
import type { UpdateEntryOpts } from "../db/entries.js";
import { getMemory } from "./get.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export async function updateMemory(
  db: Database,
  embed: EmbedFn | undefined,
  id: string,
  opts: UpdateEntryOpts,
): Promise<boolean> {
  const location = getMemory(db, id);
  if (!location) return false;

  const { tier, content_type } = location;

  updateEntry(db, tier, content_type, id, opts);

  if (opts.content !== undefined && embed !== undefined) {
    deleteEmbedding(db, tier, content_type, id);
    const newEmbedding = await embed(opts.content);
    insertEmbedding(db, tier, content_type, id, newEmbedding);
  }

  return true;
}
