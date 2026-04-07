import type { TotalRecallConfig } from "../types.js";

const MS_PER_HOUR = 60 * 60 * 1000;

export const TYPE_WEIGHTS: Record<string, number> = {
  correction: 1.5,
  preference: 1.3,
  decision: 1.0,
  surfaced: 0.8,
  imported: 1.1,
  compacted: 1.0,
  ingested: 0.9,
};

interface DecayInput {
  last_accessed_at: number;
  created_at: number;
  access_count: number;
  type: string;
}

export function calculateDecayScore(
  entry: DecayInput,
  compactionConfig: TotalRecallConfig["compaction"],
  now: number = Date.now(),
): number {
  const hoursSinceAccess = (now - entry.last_accessed_at) / MS_PER_HOUR;
  const timeFactor = Math.exp(-hoursSinceAccess / compactionConfig.decay_half_life_hours);
  const freqFactor = 1 + Math.log2(1 + entry.access_count);
  const typeWeight = TYPE_WEIGHTS[entry.type] ?? 1.0;
  return timeFactor * freqFactor * typeWeight;
}
