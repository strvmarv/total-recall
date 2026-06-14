import { describe, expect, it } from 'vitest';
import { estimatedCost, monthlyRunRate, cacheSavings, cacheHitRate } from './usageCost';
import type { UsageBucket } from './types';

const b = (key: string, input: number, output: number, cacheRead = 0, cacheCreate = 0): UsageBucket => ({
  key, session_count: 1, turn_count: 1, input_tokens: input, cache_creation_tokens: cacheCreate, cache_read_tokens: cacheRead, output_tokens: output,
});

describe('usageCost', () => {
  it('estimatedCost sums priced model buckets, counts unpriced', () => {
    // sonnet: 1M input @3 + 1M output @15 = 18
    const r = estimatedCost([b('claude-sonnet-x', 1_000_000, 1_000_000), b('(unknown)', 1_000_000, 0)]);
    expect(r.total).toBeCloseTo(18, 5);
    expect(r.pricedModels).toBe(1);
    expect(r.unpricedModels).toBe(1);
  });
  it('monthlyRunRate scales window cost to 30 days', () => {
    expect(monthlyRunRate(7, 7)).toBeCloseTo(30, 5);
    expect(monthlyRunRate(5, 0)).toBe(0);
  });
  it('cacheSavings = cache_read × (input − cacheRead) rate', () => {
    // sonnet cache_read 1M: (3 - 0.3) = 2.7
    const s = cacheSavings([b('claude-sonnet-x', 0, 0, 1_000_000)]);
    expect(s.tokens).toBe(1_000_000);
    expect(s.usd).toBeCloseTo(2.7, 5);
  });
  it('cacheHitRate = cache_read / (input + cacheCreate + cacheRead)', () => {
    expect(cacheHitRate({ input_tokens: 30, cache_creation_tokens: 10, cache_read_tokens: 60 })).toBeCloseTo(0.6, 5);
    expect(cacheHitRate({ input_tokens: 0, cache_creation_tokens: 0, cache_read_tokens: 0 })).toBe(0);
  });
});
