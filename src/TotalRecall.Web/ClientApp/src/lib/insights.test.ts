import { describe, expect, it } from 'vitest';
import { buildCards, suggestedThreshold, type InsightCard } from './insights';
import type { InsightsResult, UsageBucket } from './types';

// ── fixtures ──────────────────────────────────────────────────────────────────
function base(over: Partial<InsightsResult> = {}): InsightsResult {
  return {
    healthScore: 80,
    healthBreakdown: {
      retrieval: { score: 30, max: 35, detail: 'hit rate 86%' },
      capture: { score: 20, max: 25, detail: '4 of 10 recent entries are curated' },
      pinned: { score: 20, max: 20, detail: '3 pinned — within budget' },
      kb: { score: 10, max: 20, detail: 'no knowledge base ingested' },
    },
    nearDuplicates: [],
    pinCandidates: [],
    retrievalGaps: [],
    thresholdCurve: { current: 0.7, points: [
      { threshold: 0.5, hitRate: 0.5, precision: 0.5, mrr: 0.40 },
      { threshold: 0.6, hitRate: 0.6, precision: 0.6, mrr: 0.55 },
      { threshold: 0.7, hitRate: 0.7, precision: 0.7, mrr: 0.60 },
      { threshold: 0.8, hitRate: 0.6, precision: 0.8, mrr: 0.65 },
      { threshold: 0.9, hitRate: 0.4, precision: 0.9, mrr: 0.65 },
    ] },
    ...over,
  };
}
const day = (key: string, input: number): UsageBucket => ({ key, session_count: 1, turn_count: 1, input_tokens: input, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 });

// Every produced card must carry a real action — no pure-info cards.
function assertActionable(cards: InsightCard[]) {
  for (const c of cards) {
    if (c.kind === 'near-dup') {
      expect(c.deleteIds.length).toBeGreaterThan(0);
      expect(c.reviewTo).toBeTruthy();
    } else if (c.kind === 'pin') {
      expect(c.entryId).toBeTruthy();
    } else if (c.kind === 'gap') {
      expect(c.to).toBeTruthy();
    } else if (c.kind === 'threshold') {
      expect(c.suggested).not.toBeNull();
    } else if (c.kind === 'cost-spike') {
      expect(c.to).toBeTruthy();
    } else {
      throw new Error(`card ${(c as InsightCard).id} has no recognized action`);
    }
  }
}

describe('suggestedThreshold', () => {
  it('picks the point with the best MRR', () => {
    const curve = base().thresholdCurve;
    // mrr peaks at 0.8 and 0.9 (both 0.65) → tie-break to the LOWER threshold 0.8
    expect(suggestedThreshold(curve)).toBe(0.8);
  });
  it('tie-breaks to the lower threshold', () => {
    const curve = { current: 0.7, points: [
      { threshold: 0.6, hitRate: 0.6, precision: 0.6, mrr: 0.7 },
      { threshold: 0.7, hitRate: 0.6, precision: 0.6, mrr: 0.7 },
    ] };
    expect(suggestedThreshold(curve)).toBe(0.6);
  });
  it('returns null for an empty curve', () => {
    expect(suggestedThreshold({ current: 0.7, points: [] })).toBeNull();
  });
});

describe('buildCards', () => {
  it('maps a near-duplicate cluster to a keep-newest card targeting the older members', () => {
    const insights = base({ nearDuplicates: [{
      groupId: 'a', topScore: 0.95,
      members: [
        { id: 'old1', tier: 'warm', preview: 'dup A', score: 0.95, createdAt: 1000 },
        { id: 'newest', tier: 'hot', preview: 'dup A newest', score: 0.95, createdAt: 3000 },
        { id: 'old2', tier: 'warm', preview: 'dup A', score: 0.9, createdAt: 2000 },
      ],
    }] });
    const cards = buildCards(insights, [], 0);
    const dup = cards.find((c) => c.kind === 'near-dup');
    expect(dup).toBeTruthy();
    if (dup?.kind !== 'near-dup') throw new Error('expected near-dup');
    // newest is the max(createdAt) member → kept; the other two are deleted.
    expect(dup.keepId).toBe('newest');
    expect(dup.keepPreview).toBe('dup A newest');
    expect([...dup.deleteIds].sort()).toEqual(['old1', 'old2']);
    expect(dup.topScore).toBe(0.95);
    assertActionable([dup]);
  });

  it('maps each pin candidate to a Pin action card', () => {
    const insights = base({ pinCandidates: [
      { id: 'p1', tier: 'warm', preview: 'pin me', accessCount: 12 },
    ] });
    const cards = buildCards(insights, [], 0);
    const pin = cards.find((c) => c.kind === 'pin');
    if (pin?.kind !== 'pin') throw new Error('expected pin');
    expect(pin.entryId).toBe('p1');
    expect(pin.accessCount).toBe(12);
    expect(pin.preview).toBe('pin me');
  });

  it('maps each retrieval gap to a Track-in-Eval card with an encoded grow param', () => {
    const insights = base({ retrievalGaps: [{ query: 'how to deploy', timesSeen: 7, topScore: 0.4 }] });
    const cards = buildCards(insights, [], 0);
    const gap = cards.find((c) => c.kind === 'gap');
    if (gap?.kind !== 'gap') throw new Error('expected gap');
    expect(gap.to).toBe(`/eval?grow=${encodeURIComponent('how to deploy')}`);
    expect(gap.query).toBe('how to deploy');
    expect(gap.timesSeen).toBe(7);
  });

  it('emits a threshold-apply card with the suggested value when it differs from current', () => {
    const cards = buildCards(base(), [], 0); // current 0.7, suggested 0.8
    const thr = cards.find((c) => c.kind === 'threshold');
    if (thr?.kind !== 'threshold') throw new Error('expected threshold');
    expect(thr.current).toBe(0.7);
    expect(thr.suggested).toBe(0.8);
    expect(thr.configKey).toBe('tiers.warm.similarity_threshold');
  });

  it('omits the threshold card when the suggested value equals current', () => {
    const insights = base({ thresholdCurve: { current: 0.7, points: [
      { threshold: 0.6, hitRate: 0.5, precision: 0.5, mrr: 0.5 },
      { threshold: 0.7, hitRate: 0.6, precision: 0.6, mrr: 0.9 }, // best mrr at current → no suggestion
    ] } });
    const cards = buildCards(insights, [], 0);
    expect(cards.find((c) => c.kind === 'threshold')).toBeUndefined();
  });

  it('still fires a cost-spike card from week-over-week usage growth', () => {
    const end = Date.UTC(2026, 5, 14);
    const usageDaily = [day('2026-06-02', 100), day('2026-06-10', 300)]; // +200%
    const cards = buildCards(base(), usageDaily, end);
    const spike = cards.find((c) => c.kind === 'cost-spike');
    if (spike?.kind !== 'cost-spike') throw new Error('expected cost-spike');
    expect(spike.to).toBe('/usage');
  });

  it('does not fire a cost-spike card without week-over-week growth', () => {
    const cards = buildCards(base(), [], 0);
    expect(cards.find((c) => c.kind === 'cost-spike')).toBeUndefined();
  });

  it('produces only actionable cards (no pure-info cards)', () => {
    const insights = base({
      nearDuplicates: [{ groupId: 'g', topScore: 0.9, members: [
        { id: 'x', tier: 'warm', preview: 'a', score: 0.9, createdAt: 1 },
        { id: 'y', tier: 'warm', preview: 'b', score: 0.9, createdAt: 2 },
      ] }],
      pinCandidates: [{ id: 'p', tier: 'hot', preview: 'p', accessCount: 9 }],
      retrievalGaps: [{ query: 'q', timesSeen: 3, topScore: null }],
    });
    const end = Date.UTC(2026, 5, 14);
    const usageDaily = [day('2026-06-02', 100), day('2026-06-10', 300)];
    const cards = buildCards(insights, usageDaily, end);
    expect(cards.length).toBeGreaterThan(0);
    assertActionable(cards);
  });

  it('returns no cards for a clean payload', () => {
    const cards = buildCards(base(), [], 0);
    // base has suggested 0.8 != current 0.7, so a threshold card appears; with an
    // equal-suggestion curve and no findings there should be nothing.
    const clean = buildCards(base({ thresholdCurve: { current: 0.7, points: [
      { threshold: 0.7, hitRate: 0.6, precision: 0.6, mrr: 0.9 },
    ] } }), [], 0);
    expect(cards.length).toBe(1); // threshold only
    expect(clean.length).toBe(0);
  });
});
