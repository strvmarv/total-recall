import type { StatusResult, EvalReport, UsageBucket, MemoryRecentEntry } from './types';
import { weekOverWeek } from './usageMath';

export interface InsightInputs {
  status: StatusResult;
  evalReport: EvalReport | null;
  usageDaily: UsageBucket[];
  usageEndMs: number;
  recent: MemoryRecentEntry[];
}
export type SuggestionAction = { kind: 'navigate'; to: string; label: string } | { kind: 'info' };
export interface Suggestion { id: string; icon: string; title: string; impact: 'high' | 'medium' | 'low'; evidence: string; action: SuggestionAction; }

const PINNED_SOFT_CAP = 15;
const CURATED = new Set(['correction', 'preference', 'decision']);

function pinnedTotal(s: StatusResult): number { return s.tierSizes.pinned_memories + s.tierSizes.pinned_knowledge; }
function captureMix(recent: MemoryRecentEntry[]): { curated: number; total: number; ratio: number } {
  if (recent.length === 0) return { curated: 0, total: 0, ratio: 1 };
  const curated = recent.filter((e) => CURATED.has(e.entry_type)).length;
  return { curated, total: recent.length, ratio: curated / recent.length };
}

/** Memory-health score 0..100: retrieval(0-35) + capture mix(0-25) + pinned discipline(0-20) + KB presence(10/20). */
export function computeHealthScore(i: InsightInputs): number {
  const retrieval = i.evalReport && i.evalReport.totalEvents > 0 ? Math.round(i.evalReport.hitRate * 35) : 25;
  const mix = captureMix(i.recent);
  const capture = Math.round(Math.min(1, mix.ratio / 0.3) * 25); // 30%+ curated → full marks
  const pinned = pinnedTotal(i.status);
  const pinnedScore = pinned <= PINNED_SOFT_CAP ? 20 : Math.max(0, 20 - (pinned - PINNED_SOFT_CAP));
  const kb = i.status.knowledgeBase.totalChunks > 0 ? 20 : 10;
  return Math.max(0, Math.min(100, retrieval + capture + pinnedScore + kb));
}

export function buildSuggestions(i: InsightInputs): Suggestion[] {
  const out: Suggestion[] = [];

  const wow = weekOverWeek(i.usageDaily, i.usageEndMs);
  if (wow.deltaPercent !== null && wow.deltaPercent > 25) {
    out.push({ id: 'cost-spike', icon: '💰', title: 'Token usage is rising', impact: wow.deltaPercent > 50 ? 'high' : 'medium',
      evidence: `Up ${wow.deltaPercent.toFixed(0)}% vs the prior week.`, action: { kind: 'navigate', to: '/usage', label: 'Open Usage' } });
  }

  const mix = captureMix(i.recent);
  if (mix.total >= 5 && mix.ratio < 0.2) {
    out.push({ id: 'capture-mix', icon: '📊', title: 'Mostly auto-captured memories', impact: 'low',
      evidence: `Only ${mix.curated} of ${mix.total} recent entries are corrections, preferences, or decisions.`, action: { kind: 'info' } });
  }

  const pinned = pinnedTotal(i.status);
  if (pinned > PINNED_SOFT_CAP) {
    out.push({ id: 'pinned-pressure', icon: '📌', title: 'Many pinned directives', impact: pinned > 25 ? 'high' : 'medium',
      evidence: `${pinned} pinned entries — trim to keep the context budget lean.`, action: { kind: 'navigate', to: '/memory', label: 'Review pinned' } });
  }

  if (i.evalReport && i.evalReport.totalEvents > 0 && i.evalReport.missRate > 0.4) {
    out.push({ id: 'retrieval-misses', icon: '🎯', title: 'Retrieval miss rate is high', impact: 'medium',
      evidence: `${Math.round(i.evalReport.missRate * 100)}% of recent retrievals missed — add memories or lower the similarity threshold.`, action: { kind: 'navigate', to: '/usage', label: 'See details' } });
  }

  if (i.status.knowledgeBase.totalChunks === 0) {
    out.push({ id: 'empty-kb', icon: '🔎', title: 'Knowledge base is empty', impact: 'low',
      evidence: 'Ingest docs to ground retrieval in your own reference material.', action: { kind: 'navigate', to: '/kb', label: 'Ingest docs' } });
  }

  return out;
}
