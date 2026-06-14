import { describe, expect, it } from 'vitest';
import { timeAgo } from './time';

describe('timeAgo', () => {
  const now = Date.UTC(2026, 5, 14, 12, 0, 0);
  it('formats seconds/minutes/hours/days', () => {
    expect(timeAgo(now - 5_000, now)).toBe('just now');
    expect(timeAgo(now - 55_000, now)).toBe('just now');
    expect(timeAgo(now - 90_000, now)).toBe('1m ago');
    expect(timeAgo(now - 3 * 3_600_000, now)).toBe('3h ago');
    expect(timeAgo(now - 2 * 86_400_000, now)).toBe('2d ago');
  });
});
