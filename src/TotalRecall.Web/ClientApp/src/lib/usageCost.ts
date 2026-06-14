import type { UsageBucket } from './types';
import { rateForModel } from './pricing';

const PER = 1_000_000;

export interface CostBreakdown { total: number; pricedModels: number; unpricedModels: number; }

/** Estimated USD across model-keyed buckets (group_by=model). Unpriced models contribute 0. */
export function estimatedCost(modelBuckets: UsageBucket[]): CostBreakdown {
  let total = 0, priced = 0, unpriced = 0;
  for (const bk of modelBuckets) {
    const r = rateForModel(bk.key);
    if (!r) { unpriced++; continue; }
    priced++;
    total += ((bk.input_tokens ?? 0) * r.input
      + (bk.cache_creation_tokens ?? 0) * r.cacheWrite
      + (bk.cache_read_tokens ?? 0) * r.cacheRead
      + (bk.output_tokens ?? 0) * r.output) / PER;
  }
  return { total, pricedModels: priced, unpricedModels: unpriced };
}

/** Project window cost to a 30-day run-rate. */
export function monthlyRunRate(windowCost: number, windowDays: number): number {
  return windowDays > 0 ? (windowCost / windowDays) * 30 : 0;
}

/** USD + tokens saved by cache reads vs full input rate. */
export function cacheSavings(modelBuckets: UsageBucket[]): { tokens: number; usd: number } {
  let tokens = 0, usd = 0;
  for (const bk of modelBuckets) {
    const cr = bk.cache_read_tokens ?? 0;
    tokens += cr;
    const r = rateForModel(bk.key);
    if (r) usd += (cr * (r.input - r.cacheRead)) / PER;
  }
  return { tokens, usd };
}

export function cacheHitRate(t: { input_tokens: number | null; cache_creation_tokens: number | null; cache_read_tokens: number | null }): number {
  const inp = t.input_tokens ?? 0, cw = t.cache_creation_tokens ?? 0, cr = t.cache_read_tokens ?? 0;
  const denom = inp + cw + cr;
  return denom > 0 ? cr / denom : 0;
}

/** Days spanned by a usage query window (from echoed start/end ms). */
export function windowDays(startMs: number, endMs: number): number {
  return Math.max(1, Math.round((endMs - startMs) / 86_400_000));
}
