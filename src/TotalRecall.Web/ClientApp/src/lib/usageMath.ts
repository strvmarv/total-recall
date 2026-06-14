import type { UsageBucket } from './types';

type Tokens = Pick<UsageBucket, 'input_tokens' | 'cache_creation_tokens' | 'cache_read_tokens' | 'output_tokens'>;

export function sumTokens(t: Tokens): number {
  return (t.input_tokens ?? 0) + (t.cache_creation_tokens ?? 0) + (t.cache_read_tokens ?? 0) + (t.output_tokens ?? 0);
}

export function dailyTotals(buckets: UsageBucket[]): number[] {
  return buckets.map(sumTokens);
}

const DAY = 86_400_000;

export interface WeekOverWeek { current: number; previous: number; deltaPercent: number | null; }

/** day-bucket keys are ISO dates ("YYYY-MM-DD"); window is [endMs-7d, endMs] vs [endMs-14d, endMs-7d). */
export function weekOverWeek(buckets: UsageBucket[], endMs: number): WeekOverWeek {
  const endDay = endMs - (endMs % DAY); // normalize to UTC midnight so date-keyed buckets compare correctly
  const curStart = endDay - 7 * DAY;
  const prevStart = endDay - 14 * DAY;
  let current = 0;
  let previous = 0;
  for (const bk of buckets) {
    const ms = Date.parse(bk.key);
    if (Number.isNaN(ms)) continue;
    const tok = sumTokens(bk);
    if (ms >= curStart) current += tok;
    else if (ms >= prevStart) previous += tok;
  }
  const deltaPercent = previous > 0 ? ((current - previous) / previous) * 100 : null;
  return { current, previous, deltaPercent };
}
