import { describe, expect, it } from 'vitest';
import { rateForModel } from './pricing';

describe('rateForModel', () => {
  it('matches opus/sonnet/haiku by substring (case-insensitive)', () => {
    expect(rateForModel('claude-opus-4-1')?.input).toBe(15);
    expect(rateForModel('claude-3-5-sonnet-20241022')?.output).toBe(15);
    expect(rateForModel('CLAUDE-HAIKU-4')?.cacheRead).toBe(0.08);
  });
  it('returns null for unknown / subscription models', () => {
    expect(rateForModel('(unknown)')).toBeNull();
    expect(rateForModel('gpt-4o')).toBeNull();
  });
});
