import type Database from "better-sqlite3";
import { getEntry, moveEntry } from "../db/entries.js";
import { deleteEmbedding, insertEmbedding } from "../search/vector-search.js";
import type { Tier, ContentType } from "../types.js";

type EmbedFn = (text: string) => Float32Array;

export function promoteEntry(
  db: Database.Database,
  embed: EmbedFn,
  id: string,
  fromTier: Tier,
  fromType: ContentType,
  toTier: Tier,
  toType: ContentType,
): void {
  const entry = getEntry(db, fromTier, fromType, id);
  if (!entry) {
    throw new Error(`Entry ${id} not found in ${fromTier}/${fromType}`);
  }

  deleteEmbedding(db, fromTier, fromType, id);
  moveEntry(db, fromTier, fromType, toTier, toType, id);
  const newEmbedding = embed(entry.content);
  insertEmbedding(db, toTier, toType, id, newEmbedding);
}

export function demoteEntry(
  db: Database.Database,
  embed: EmbedFn,
  id: string,
  fromTier: Tier,
  fromType: ContentType,
  toTier: Tier,
  toType: ContentType,
): void {
  promoteEntry(db, embed, id, fromTier, fromType, toTier, toType);
}
