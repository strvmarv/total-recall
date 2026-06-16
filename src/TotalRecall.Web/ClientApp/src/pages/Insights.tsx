import { useState } from 'react';
import { api } from '../lib/api';
import { useAsync } from '../lib/useAsync';
import { buildCards, type InsightCard as Card } from '../lib/insights';
import { HealthScore } from '../components/insights/HealthScore';
import { InsightCard } from '../components/insights/InsightCard';
import type { InsightsResult, UsageResult } from '../lib/types';

interface InsightsData {
  insights: InsightsResult;
  usage: UsageResult | null;
}

export function Insights() {
  const [refreshKey, setRefreshKey] = useState(0);
  const refresh = () => setRefreshKey((k) => k + 1);

  const { data, error, loading } = useAsync<InsightsData>(async () => {
    const [insights, usage] = await Promise.all([
      api.tool<InsightsResult>('insights'),
      api.tool<UsageResult>('usage_status', { window: '30d', group_by: 'day' }).catch(() => null),
    ]);
    return { insights, usage };
  }, [refreshKey]);

  const cards: Card[] = data
    ? buildCards(data.insights, data.usage?.buckets ?? [], data.usage?.query.end_ms ?? 0)
    : [];

  return (
    <section className="tr-insights" aria-label="Insights">
      <h1>✨ Insights</h1>
      {loading && <p className="tr-card-muted">Loading…</p>}
      {error && <p className="tr-card-error" role="alert" title={error}>Couldn't compute insights.</p>}
      {data && (
        <>
          <HealthScore score={data.insights.healthScore} breakdown={data.insights.healthBreakdown} />
          {cards.length === 0 ? (
            <p className="tr-card-muted">All clear — no suggestions right now. 🎉</p>
          ) : (
            <div className="tr-insight-list">
              {cards.map((card) => (
                <InsightCard
                  key={card.id}
                  card={card}
                  actions={{
                    curvePoints: data.insights.thresholdCurve.points,
                    onDeleteCluster: async (c) => {
                      const results = await Promise.allSettled(c.deleteIds.map((id) => api.tool('memory_delete', { id })));
                      // Always refetch so the card re-derives from server state — even on
                      // partial failure the successful deletes must be reflected.
                      refresh();
                      const failed = results.filter((r) => r.status === 'rejected');
                      if (failed.length > 0) throw new Error(`${failed.length} delete(s) failed`);
                    },
                    onPin: async (c) => {
                      await api.tool('memory_pin', { id: c.entryId });
                      refresh();
                    },
                    onApplyThreshold: async (c) => {
                      await api.tool('config_set', { key: c.configKey, value: c.suggested });
                      refresh();
                    },
                  }}
                />
              ))}
            </div>
          )}
          <p className="tr-stat-sub">Suggestions are computed server-side from your local store (still no LLM).</p>
        </>
      )}
    </section>
  );
}
