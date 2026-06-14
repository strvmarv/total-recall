import { useState } from 'react';
import { api } from '../lib/api';
import { useAsync } from '../lib/useAsync';
import { buildSuggestions, computeHealthScore, type InsightInputs } from '../lib/insights';
import { HealthScore } from '../components/insights/HealthScore';
import { InsightCard } from '../components/insights/InsightCard';
import type { EvalReport, MemoryRecentResult, StatusResult, UsageResult } from '../lib/types';

export function Insights() {
  const [refreshKey] = useState(0);
  const { data, error, loading } = useAsync<InsightInputs>(async () => {
    const [status, evalReport, usage, recent] = await Promise.all([
      api.tool<StatusResult>('status'),
      api.tool<EvalReport>('eval_report').catch(() => null),
      api.tool<UsageResult>('usage_status', { window: '30d', group_by: 'day' }).catch(() => null),
      api.tool<MemoryRecentResult>('memory_recent', { limit: 30, order: 'created' }).catch(() => ({ entries: [], count: 0, order: 'created' } as MemoryRecentResult)),
    ]);
    return {
      status,
      evalReport,
      usageDaily: usage?.buckets ?? [],
      usageEndMs: usage?.query.end_ms ?? 0,
      recent: recent.entries,
    };
  }, [refreshKey]);

  const score = data ? computeHealthScore(data) : 0;
  const suggestions = data ? buildSuggestions(data) : [];

  return (
    <section className="tr-insights" aria-label="Insights">
      <h1>✨ Insights</h1>
      {loading && <p className="tr-card-muted">Loading…</p>}
      {error && <p className="tr-card-error" role="alert" title={error}>Couldn't compute insights.</p>}
      {data && (
        <>
          <HealthScore score={score} />
          {suggestions.length === 0
            ? <p className="tr-card-muted">All clear — no suggestions right now. 🎉</p>
            : <div className="tr-insight-list">{suggestions.map((s) => <InsightCard key={s.id} s={s} />)}</div>}
          <p className="tr-stat-sub">Suggestions are computed from your local data (no LLM). Deeper coaching (near-duplicate merge, threshold tuning) arrives with the insights engine.</p>
        </>
      )}
    </section>
  );
}
