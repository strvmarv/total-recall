import type Database from "better-sqlite3";
import { getEntry, updateEntry } from "../db/entries.js";
import { searchByVector } from "../search/vector-search.js";
import type { Tier, ContentType, SearchResult } from "../types.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export interface SearchOptions {
  tiers: Array<{ tier: Tier; content_type: ContentType }>;
  topK: number;
  minScore?: number;
}

export async function searchMemory(
  db: Database.Database,
  embed: EmbedFn,
  query: string,
  opts: SearchOptions,
): Promise<SearchResult[]> {
  const queryVec = await embed(query);
  const merged: SearchResult[] = [];

  for (const { tier, content_type } of opts.tiers) {
    const vectorResults = searchByVector(db, tier, content_type, queryVec, {
      topK: opts.topK,
      minScore: opts.minScore,
    });

    for (const vr of vectorResults) {
      const entry = getEntry(db, tier, content_type, vr.id);
      if (!entry) continue;

      updateEntry(db, tier, content_type, vr.id, { touch: true });

      merged.push({
        entry,
        tier,
        content_type,
        score: vr.score,
        rank: 0,
      });
    }
  }

  merged.sort((a, b) => b.score - a.score);

  const topK = merged.slice(0, opts.topK);
  topK.forEach((r, i) => {
    r.rank = i + 1;
  });

  return topK;
}
