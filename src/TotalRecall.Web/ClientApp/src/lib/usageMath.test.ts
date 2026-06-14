import { describe, expect, it } from 'vitest';
import { sumTokens, dailyTotals, weekOverWeek } from './usageMath';
import type { UsageBucket } from './types';

const b = (key: string, input: number, output: number): UsageBucket => ({
  key, session_count: 1, turn_count: 1, input_tokens: input,
  cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: output,
});

describe('usageMath', () => {
  it('sumTokens adds all non-null token types', () => {
    expect(sumTokens({ input_tokens: 10, cache_creation_tokens: 5, cache_read_tokens: 2, output_tokens: 3 })).toBe(20);
    expect(sumTokens({ input_tokens: null, cache_creation_tokens: null, cache_read_tokens: null, output_tokens: 7 })).toBe(7);
  });

  it('dailyTotals returns one total per bucket in input order', () => {
    expect(dailyTotals([b('2026-06-01', 1, 1), b('2026-06-02', 2, 2)])).toEqual([2, 4]);
  });

  it('weekOverWeek splits last 7d vs prior 7d by date and computes % delta', () => {
    const end = Date.UTC(2026, 5, 14);
    const buckets = [b('2026-06-02', 100, 0), b('2026-06-10', 150, 0)];
    const wow = weekOverWeek(buckets, end);
    expect(wow.current).toBe(150);
    expect(wow.previous).toBe(100);
    expect(wow.deltaPercent).toBeCloseTo(50, 5);
  });

  it('weekOverWeek reports null delta when the prior week is empty', () => {
    const end = Date.UTC(2026, 5, 14);
    const wow = weekOverWeek([b('2026-06-10', 150, 0)], end);
    expect(wow.previous).toBe(0);
    expect(wow.deltaPercent).toBeNull();
  });
});
