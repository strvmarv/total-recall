import type Database from "better-sqlite3";
import { getEntry } from "../db/entries.js";
import type { Tier, ContentType, Entry } from "../types.js";
import { ALL_TABLE_PAIRS } from "../types.js";

export interface MemoryLocation {
  entry: Entry;
  tier: Tier;
  content_type: ContentType;
}

export function getMemory(db: Database.Database, id: string): MemoryLocation | null {
  for (const { tier, type } of ALL_TABLE_PAIRS) {
    const entry = getEntry(db, tier, type, id);
    if (entry) {
      return { entry, tier, content_type: type };
    }
  }
  return null;
}
