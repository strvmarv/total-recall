import type Database from "better-sqlite3";
import { deleteEntry } from "../db/entries.js";
import { deleteEmbedding } from "../search/vector-search.js";
import { getMemory } from "./get.js";

export function deleteMemory(db: Database.Database, id: string): boolean {
  const location = getMemory(db, id);
  if (!location) return false;

  deleteEmbedding(db, location.tier, location.content_type, id);
  deleteEntry(db, location.tier, location.content_type, id);
  return true;
}
