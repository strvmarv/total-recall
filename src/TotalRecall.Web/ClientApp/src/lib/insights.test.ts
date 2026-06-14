import { describe, expect, it } from 'vitest';
import { computeHealthScore, buildSuggestions, type InsightInputs } from './insights';
import type { StatusResult, EvalReport, MemoryRecentEntry, UsageBucket } from './types';

function status(pinned = 2, chunks = 100): StatusResult {
  return {
    tierSizes: { hot_memories: 3, hot_knowledge: 0, warm_memories: 50, warm_knowledge: 0, cold_memories: 200, cold_knowledge: 0, pinned_memories: pinned, pinned_knowledge: 0 },
    knowledgeBase: { collections: [], totalChunks: chunks },
    db: { path: 'x', sizeBytes: 1, sessionId: 's' }, embedding: { model: 'b', dimensions: 1 },
  };
}
const evalGood: EvalReport = { precision: 0.9, hitRate: 0.9, missRate: 0.1, mrr: 0.8, avgLatencyMs: 20, totalEvents: 100 };
const recent = (types: string[]): MemoryRecentEntry[] => types.map((t, i) => ({ id: String(i), tier: 'warm', entry_type: t, project: null, created_at: 1, updated_at: 1, last_accessed_at: 1, preview: 'x' }));
const day = (key: string, input: number): UsageBucket => ({ key, session_count: 1, turn_count: 1, input_tokens: input, cache_creation_tokens: 0, cache_read_tokens: 0, output_tokens: 0 });

describe('computeHealthScore', () => {
  it('is high for good retrieval, curated mix, few pins, KB present', () => {
    const i: InsightInputs = { status: status(2, 100), evalReport: evalGood, usageDaily: [], usageEndMs: 0, recent: recent(['correction', 'preference', 'decision']) };
    expect(computeHealthScore(i)).toBeGreaterThanOrEqual(90);
  });
  it('is lower with poor retrieval, no curation, empty KB', () => {
    const i: InsightInputs = { status: status(2, 0), evalReport: { ...evalGood, hitRate: 0.1, missRate: 0.9 }, usageDaily: [], usageEndMs: 0, recent: recent(['surfaced', 'surfaced', 'surfaced']) };
    expect(computeHealthScore(i)).toBeLessThan(60);
  });
  it('clamps to 0..100', () => {
    const i: InsightInputs = { status: status(0, 0), evalReport: null, usageDaily: [], usageEndMs: 0, recent: [] };
    const s = computeHealthScore(i);
    expect(s).toBeGreaterThanOrEqual(0); expect(s).toBeLessThanOrEqual(100);
  });
});

describe('buildSuggestions', () => {
  it('flags a cost spike from weekly token growth', () => {
    const end = Date.UTC(2026, 5, 14);
    const usageDaily = [day('2026-06-02', 100), day('2026-06-10', 300)]; // prior 100, current 300 → +200%
    const s = buildSuggestions({ status: status(), evalReport: evalGood, usageDaily, usageEndMs: end, recent: recent(['correction']) });
    expect(s.find((x) => x.id === 'cost-spike')).toBeTruthy();
  });
  it('flags pinned pressure over the soft cap', () => {
    const s = buildSuggestions({ status: status(20, 100), evalReport: evalGood, usageDaily: [], usageEndMs: 0, recent: recent(['correction']) });
    expect(s.find((x) => x.id === 'pinned-pressure')?.impact).toBe('medium');
  });
  it('flags empty KB and high miss rate', () => {
    const s = buildSuggestions({ status: status(2, 0), evalReport: { ...evalGood, missRate: 0.6, totalEvents: 50 }, usageDaily: [], usageEndMs: 0, recent: recent(['surfaced', 'surfaced', 'surfaced', 'surfaced', 'surfaced']) });
    expect(s.find((x) => x.id === 'empty-kb')).toBeTruthy();
    expect(s.find((x) => x.id === 'retrieval-misses')).toBeTruthy();
    expect(s.find((x) => x.id === 'capture-mix')).toBeTruthy();
  });
  it('returns nothing for a healthy system', () => {
    const s = buildSuggestions({ status: status(2, 100), evalReport: evalGood, usageDaily: [], usageEndMs: 0, recent: recent(['correction', 'preference']) });
    expect(s).toHaveLength(0);
  });
});
