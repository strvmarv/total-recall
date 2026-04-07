import type { Database } from "bun:sqlite";
import { getEntry, updateEntry } from "../db/entries.js";
import { searchByVector } from "../search/vector-search.js";
import { searchByFts } from "../search/fts-search.js";
import type { Tier, ContentType, SearchResult } from "../types.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

export interface SearchOptions {
  tiers: Array<{ tier: Tier; content_type: ContentType }>;
  topK: number;
  minScore?: number;
  ftsWeight?: number;
}

const DEFAULT_FTS_WEIGHT = 0.3;

export async function searchMemory(
  db: Database,
  embed: EmbedFn,
  query: string,
  opts: SearchOptions,
): Promise<SearchResult[]> {
  const queryVec = await embed(query);
  const ftsWeight = opts.ftsWeight ?? DEFAULT_FTS_WEIGHT;
  const oversampledK = opts.topK * 2;

  const scoreMap = new Map<string, {
    vectorScore: number;
    ftsScore: number;
    tier: Tier;
    content_type: ContentType;
  }>();

  for (const { tier, content_type } of opts.tiers) {
    // Vector search
    const vectorResults = searchByVector(db, tier, content_type, queryVec, {
      topK: oversampledK,
      minScore: opts.minScore,
    });

    for (const vr of vectorResults) {
      const existing = scoreMap.get(vr.id);
      if (!existing || vr.score > existing.vectorScore) {
        scoreMap.set(vr.id, {
          vectorScore: vr.score,
          ftsScore: existing?.ftsScore ?? 0,
          tier,
          content_type,
        });
      }
    }

    // FTS5 search
    const ftsResults = searchByFts(db, tier, content_type, query, {
      topK: oversampledK,
    });

    for (const fr of ftsResults) {
      const existing = scoreMap.get(fr.id);
      if (existing) {
        existing.ftsScore = Math.max(existing.ftsScore, fr.score);
      } else {
        scoreMap.set(fr.id, {
          vectorScore: 0,
          ftsScore: fr.score,
          tier,
          content_type,
        });
      }
    }
  }

  // Fuse scores and build results
  const candidates: Array<{
    id: string;
    fusedScore: number;
    tier: Tier;
    content_type: ContentType;
  }> = [];

  for (const [id, scores] of scoreMap) {
    const fusedScore = scores.vectorScore + ftsWeight * scores.ftsScore;
    candidates.push({ id, fusedScore, tier: scores.tier, content_type: scores.content_type });
  }

  candidates.sort((a, b) => b.fusedScore - a.fusedScore);
  const topCandidates = candidates.slice(0, opts.topK);

  // Resolve entries
  const merged: SearchResult[] = [];
  for (const c of topCandidates) {
    const entry = getEntry(db, c.tier, c.content_type, c.id);
    if (!entry) continue;
    updateEntry(db, c.tier, c.content_type, c.id, { touch: true });
    merged.push({
      entry,
      tier: c.tier,
      content_type: c.content_type,
      score: c.fusedScore,
      rank: 0,
    });
  }

  merged.forEach((r, i) => {
    r.rank = i + 1;
  });

  return merged;
}
