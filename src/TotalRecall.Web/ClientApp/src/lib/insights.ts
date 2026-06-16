import type { InsightsResult, InsightsThresholdCurve, UsageBucket } from './types';
import { weekOverWeek } from './usageMath';

// The config key the safe-subset writer accepts for the retrieval similarity
// threshold — the same key the Config page exposes as "Similarity threshold".
export const SIMILARITY_THRESHOLD_KEY = 'tiers.warm.similarity_threshold';

export type Impact = 'high' | 'medium' | 'low';

/**
 * A card descriptor produced by the pure mapper. Every variant carries a real
 * action: a navigate target (`gap`/`cost-spike`), a tool-call (`pin` →
 * memory_pin, `near-dup` → memory_delete), or the special threshold render
 * (`threshold` → config_set). React components own the side-effecting tool calls
 * and the destructive two-click confirm flow for `near-dup` (no `destructive`
 * flag on the descriptor — the kind itself is the signal).
 */
export type InsightCard =
  | {
      kind: 'near-dup';
      id: string;
      icon: string;
      title: string;
      impact: Impact;
      /** Newest member (max createdAt) — kept. */
      keepId: string;
      keepPreview: string;
      /** Older members to delete via memory_delete (one call each). */
      deleteIds: string[];
      topScore: number;
      /** Deep-link to review the cluster manually. */
      reviewTo: string;
    }
  | {
      kind: 'pin';
      id: string;
      icon: string;
      title: string;
      impact: Impact;
      entryId: string;
      preview: string;
      accessCount: number;
      tier: string;
    }
  | {
      kind: 'gap';
      id: string;
      icon: string;
      title: string;
      impact: Impact;
      query: string;
      timesSeen: number;
      topScore: number | null;
      to: string;
    }
  | {
      kind: 'threshold';
      id: string;
      icon: string;
      title: string;
      impact: Impact;
      configKey: string;
      current: number;
      suggested: number;
    }
  | {
      kind: 'cost-spike';
      id: string;
      icon: string;
      title: string;
      impact: Impact;
      evidence: string;
      to: string;
    };

/**
 * The threshold curve point with the best MRR. Ties break to the LOWER
 * threshold (more recall). Returns null for an empty curve.
 */
export function suggestedThreshold(curve: InsightsThresholdCurve): number | null {
  if (curve.points.length === 0) return null;
  let best = curve.points[0];
  // Start at the second point — `best` is already seeded with points[0].
  for (const p of curve.points.slice(1)) {
    if (p.mrr > best.mrr || (p.mrr === best.mrr && p.threshold < best.threshold)) {
      best = p;
    }
  }
  return best.threshold;
}

export const fmtScore = (s: number) => s.toFixed(2);

/**
 * Pure mapper: insights payload (+ already-fetched usage for the one client-only
 * cost-spike nudge) → an ordered array of actionable card descriptors. No card is
 * pure-info; every variant carries a navigate target, a tool-call, or the
 * threshold/apply special render. Side effects live in the React components.
 */
export function buildCards(
  insights: InsightsResult,
  usageDaily: UsageBucket[],
  usageEndMs: number,
): InsightCard[] {
  const out: InsightCard[] = [];

  // 1. Near-duplicate clusters → "keep newest, delete the rest".
  insights.nearDuplicates.forEach((g) => {
    if (g.members.length < 2) return;
    // Newest = max(createdAt); the rest are deleted.
    const newest = g.members.reduce((a, b) => (b.createdAt > a.createdAt ? b : a));
    const deleteIds = g.members.filter((m) => m.id !== newest.id).map((m) => m.id);
    if (deleteIds.length === 0) return;
    out.push({
      kind: 'near-dup',
      id: `near-dup-${g.groupId}`,
      icon: '🧬',
      title: 'Near-duplicate cluster',
      impact: deleteIds.length > 1 ? 'medium' : 'low',
      keepId: newest.id,
      keepPreview: newest.preview,
      deleteIds,
      topScore: g.topScore,
      reviewTo: '/memory',
    });
  });

  // 2. Pin candidates → inline "Pin".
  insights.pinCandidates.forEach((p) => {
    out.push({
      kind: 'pin',
      id: `pin-${p.id}`,
      icon: '📌',
      title: 'Frequently accessed — pin it?',
      impact: 'low',
      entryId: p.id,
      preview: p.preview,
      accessCount: p.accessCount,
      tier: p.tier,
    });
  });

  // 3. Retrieval gaps → "Open in Eval".
  insights.retrievalGaps.forEach((g, i) => {
    out.push({
      kind: 'gap',
      id: `gap-${i}`,
      icon: '🎯',
      title: 'Retrieval gap',
      impact: g.timesSeen >= 5 ? 'medium' : 'low',
      query: g.query,
      timesSeen: g.timesSeen,
      topScore: g.topScore,
      to: '/eval',
    });
  });

  // 4. Threshold curve → "Apply" (only when the suggestion differs from current).
  const suggested = suggestedThreshold(insights.thresholdCurve);
  if (suggested !== null && suggested !== insights.thresholdCurve.current) {
    out.push({
      kind: 'threshold',
      id: 'threshold',
      icon: '🎚️',
      title: 'Tune the similarity threshold',
      impact: 'medium',
      configKey: SIMILARITY_THRESHOLD_KEY,
      current: insights.thresholdCurve.current,
      suggested,
    });
  }

  // 5. Cost spike (client-only): week-over-week token growth > 25%.
  const wow = weekOverWeek(usageDaily, usageEndMs);
  if (wow.deltaPercent !== null && wow.deltaPercent > 25) {
    out.push({
      kind: 'cost-spike',
      id: 'cost-spike',
      icon: '💰',
      title: 'Token usage is rising',
      impact: wow.deltaPercent > 50 ? 'high' : 'medium',
      evidence: `Up ${wow.deltaPercent.toFixed(0)}% vs the prior week.`,
      to: '/usage',
    });
  }

  return out;
}
