import type Database from "better-sqlite3";
import { updateEntry } from "../db/entries.js";
import { deleteEmbedding, insertEmbedding } from "../search/vector-search.js";
import type { UpdateEntryOpts } from "../db/entries.js";
import { getMemory } from "./get.js";

type EmbedFn = (text: string) => Float32Array;

export function updateMemory(
  db: Database.Database,
  embed: EmbedFn,
  id: string,
  opts: UpdateEntryOpts,
): boolean {
  const location = getMemory(db, id);
  if (!location) return false;

  const { tier, content_type } = location;

  updateEntry(db, tier, content_type, id, opts);

  if (opts.content !== undefined) {
    deleteEmbedding(db, tier, content_type, id);
    const newEmbedding = embed(opts.content);
    insertEmbedding(db, tier, content_type, id, newEmbedding);
  }

  return true;
}
