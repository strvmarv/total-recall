import { readFileSync } from "node:fs";
import type { Database } from "bun:sqlite";
import { storeMemory } from "../memory/store.js";
import { deleteMemory } from "../memory/delete.js";
import { searchMemory } from "../memory/search.js";
import { loadConfig } from "../config.js";
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
  expected_absent?: string;
}

export interface BenchmarkDetail {
  query: string;
  expectedContains: string;
  topResult: string | null;
  topScore: number;
  matched: boolean;
  fuzzyMatched: boolean;
  hasNegativeAssertion: boolean;
  negativePass: boolean;
}

export interface BenchmarkResult {
  totalQueries: number;
  exactMatchRate: number;
  fuzzyMatchRate: number;
  tierRoutingRate: number;
  negativePassRate: number;
  avgLatencyMs: number;
  details: BenchmarkDetail[];
}

export interface BenchmarkOptions {
  corpusPath: string;
  benchmarkPath: string;
}

export async function runBenchmark(
  db: Database,
  embed: EmbedFn,
  opts: BenchmarkOptions,
): Promise<BenchmarkResult> {
  // Seed corpus into warm tier, tracking IDs for cleanup
  const corpusLines = readFileSync(opts.corpusPath, "utf-8")
    .split("\n")
    .filter((line) => line.trim().length > 0);

  const seededIds: string[] = [];
  for (const line of corpusLines) {
    const entry = JSON.parse(line) as CorpusEntry;
    const id = await storeMemory(db, embed, {
      content: entry.content,
      type: entry.type,
      tier: "warm",
      contentType: "memory",
      tags: entry.tags,
    });
    seededIds.push(id);
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

  const config = loadConfig();

  for (const bq of queries) {
    const start = performance.now();
    const results = await searchMemory(db, embed, bq.query, {
      tiers: [{ tier: "warm", content_type: "memory" }],
      topK: 3,
      ftsWeight: config.search?.fts_weight,
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

    let negativePass = true;
    if (bq.expected_absent && topContent) {
      negativePass = !topContent.toLowerCase().includes(bq.expected_absent.toLowerCase());
    }

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
      hasNegativeAssertion: !!bq.expected_absent,
      negativePass,
    });
  }

  // Clean up seeded corpus entries
  for (const id of seededIds) {
    deleteMemory(db, id);
  }

  const total = queries.length;

  const negativeQueries = details.filter((d) => d.hasNegativeAssertion);
  const negativePassRate =
    negativeQueries.length > 0
      ? negativeQueries.filter((d) => d.negativePass).length / negativeQueries.length
      : 1.0;

  return {
    totalQueries: total,
    exactMatchRate: total > 0 ? exactMatches / total : 0,
    fuzzyMatchRate: total > 0 ? fuzzyMatches / total : 0,
    tierRoutingRate: total > 0 ? tierMatches / total : 0,
    negativePassRate,
    avgLatencyMs: total > 0 ? totalLatencyMs / total : 0,
    details,
  };
}
