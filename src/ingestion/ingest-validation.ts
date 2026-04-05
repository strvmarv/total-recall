import type Database from "better-sqlite3";
import { searchByVector } from "../search/vector-search.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

const PROBE_MIN_SCORE = 0.5;
const PROBE_TOP_K = 3;

export interface ProbeResult {
  chunkIndex: number;
  score: number;
  passed: boolean;
}

export interface ValidationResult {
  passed: boolean;
  probes: ProbeResult[];
}

export function selectProbeIndices(totalChunks: number): number[] {
  if (totalChunks <= 3) {
    return Array.from({ length: totalChunks }, (_, i) => i);
  }
  return [
    0,
    Math.floor(totalChunks / 3),
    Math.floor((2 * totalChunks) / 3),
  ];
}

export async function validateChunks(
  db: Database.Database,
  embed: EmbedFn,
  chunks: Array<{ content: string }>,
  collectionId: string,
): Promise<ValidationResult> {
  const indices = selectProbeIndices(chunks.length);
  const probes: ProbeResult[] = [];

  for (const idx of indices) {
    const chunk = chunks[idx]!;
    const queryVec = await embed(chunk.content);
    const results = searchByVector(db, "cold", "knowledge", queryVec, {
      topK: PROBE_TOP_K * 3,
      minScore: 0,
    });

    // Filter to same collection
    const scoped = results.filter((r) => {
      const entry = db.prepare(
        "SELECT collection_id, parent_id FROM cold_knowledge WHERE id = ?"
      ).get(r.id) as { collection_id: string | null; parent_id: string | null } | undefined;
      return entry?.collection_id === collectionId || entry?.parent_id === collectionId;
    });

    const bestScore = scoped.length > 0 ? Math.max(...scoped.map((r) => r.score)) : 0;
    probes.push({
      chunkIndex: idx,
      score: bestScore,
      passed: bestScore > PROBE_MIN_SCORE,
    });
  }

  return {
    passed: probes.every((p) => p.passed),
    probes,
  };
}
