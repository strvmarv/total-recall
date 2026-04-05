import { readFileSync } from "node:fs";
import type Database from "better-sqlite3";
import { storeMemory } from "../memory/store.js";
import { searchMemory } from "../memory/search.js";
import { countEntries } from "../db/entries.js";
import type { Tier, EntryType } from "../types.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

interface CorpusEntry {
  content: string;
  type: EntryType;
  tags: string[];
}

interface BenchmarkQuery {
  query: string;
  expected_content_contains: string;
  expected_tier: Tier;
}

export interface BenchmarkDetail {
  query: string;
  expectedContains: string;
  topResult: string | null;
  topScore: number;
  matched: boolean;
  fuzzyMatched: boolean;
}

export interface BenchmarkResult {
  totalQueries: number;
  exactMatchRate: number;
  fuzzyMatchRate: number;
  tierRoutingRate: number;
  avgLatencyMs: number;
  details: BenchmarkDetail[];
}

export interface BenchmarkOptions {
  corpusPath: string;
  benchmarkPath: string;
}

export async function runBenchmark(
  db: Database.Database,
  embed: EmbedFn,
  opts: BenchmarkOptions,
): Promise<BenchmarkResult> {
  // Seed corpus into warm tier (skip if already seeded)
  const corpusLines = readFileSync(opts.corpusPath, "utf-8")
    .split("\n")
    .filter((line) => line.trim().length > 0);

  const existingWarmCount = countEntries(db, "warm", "memory");
  if (existingWarmCount < corpusLines.length) {
    for (const line of corpusLines) {
      const entry = JSON.parse(line) as CorpusEntry;
      await storeMemory(db, embed, {
        content: entry.content,
        type: entry.type,
        tier: "warm",
        contentType: "memory",
        tags: entry.tags,
      });
    }
  }

  // Load benchmark queries
  const benchmarkLines = readFileSync(opts.benchmarkPath, "utf-8")
    .split("\n")
    .filter((line) => line.trim().length > 0);

  const queries = benchmarkLines.map((line) => JSON.parse(line) as BenchmarkQuery);

  const details: BenchmarkDetail[] = [];
  let exactMatches = 0;
  let fuzzyMatches = 0;
  let tierMatches = 0;
  let totalLatencyMs = 0;

  for (const bq of queries) {
    const start = performance.now();
    const results = await searchMemory(db, embed, bq.query, {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 3,
    });
    const latencyMs = performance.now() - start;
    totalLatencyMs += latencyMs;

    const topResult = results[0] ?? null;
    const topContent = topResult?.entry.content ?? null;
    const topScore = topResult?.score ?? 0;
    const topTier = topResult?.tier ?? null;

    const matched =
      topContent !== null && topContent.includes(bq.expected_content_contains);

    const fuzzyMatched =
      matched ||
      results
        .slice(1)
        .some((r) => r.entry.content.includes(bq.expected_content_contains));

    const tierRouted = topTier === bq.expected_tier;

    if (matched) exactMatches++;
    if (fuzzyMatched) fuzzyMatches++;
    if (tierRouted) tierMatches++;

    details.push({
      query: bq.query,
      expectedContains: bq.expected_content_contains,
      topResult: topContent,
      topScore,
      matched,
      fuzzyMatched,
    });
  }

  const total = queries.length;

  return {
    totalQueries: total,
    exactMatchRate: total > 0 ? exactMatches / total : 0,
    fuzzyMatchRate: total > 0 ? fuzzyMatches / total : 0,
    tierRoutingRate: total > 0 ? tierMatches / total : 0,
    avgLatencyMs: total > 0 ? totalLatencyMs / total : 0,
    details,
  };
}
