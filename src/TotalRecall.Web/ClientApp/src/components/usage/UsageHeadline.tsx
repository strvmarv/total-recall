import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { usageArgs, type UsageFilterState } from './UsageFilters';
import { cacheHitRate, estimatedCost, monthlyRunRate, windowDays } from '../../lib/usageCost';

const n = (x: number) => x.toLocaleString('en-US');
const usd = (x: number) => `$${x.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

export function UsageHeadline({ filters, refreshKey }: { filters: UsageFilterState; refreshKey: number }) {
  const { data, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', usageArgs(filters, 'model')),
    [filters.window, filters.host, filters.project, refreshKey],
  );
  const gt = data?.grand_total;
  const totalTokens = gt ? (gt.input_tokens ?? 0) + (gt.cache_creation_tokens ?? 0) + (gt.cache_read_tokens ?? 0) + (gt.output_tokens ?? 0) : 0;
  const cost = data ? estimatedCost(data.buckets) : { total: 0, pricedModels: 0, unpricedModels: 0 };
  const days = data ? windowDays(data.query.start_ms, data.query.end_ms) : 1;
  const empty = !!data && totalTokens === 0;

  return (
    <Card title="Usage summary">
      <CardState loading={loading} error={error} empty={empty} emptyText="No usage data for this window. (Usage tracking is SQLite/Cortex only.)">
        {gt && (
          <div className="tr-usage-cards">
            <div className="tr-usage-stat"><div className="tr-stat-figure">{n(totalTokens)}</div><div className="tr-stat-sub">total tokens · in {n(gt.input_tokens ?? 0)} / out {n(gt.output_tokens ?? 0)}</div></div>
            <div className="tr-usage-stat"><div className="tr-stat-figure">{Math.round(cacheHitRate(gt) * 100)}%</div><div className="tr-stat-sub">cache hit rate</div></div>
            <div className="tr-usage-stat"><div className="tr-stat-figure">{usd(cost.total)}</div><div className="tr-stat-sub">estimated · ~{usd(monthlyRunRate(cost.total, days))}/mo run-rate</div></div>
            <div className="tr-usage-stat"><div className="tr-stat-figure">{gt.session_count} · {gt.turn_count}</div><div className="tr-stat-sub">sessions · turns ({gt.session_count ? (gt.turn_count / gt.session_count).toFixed(1) : '0'}/session)</div></div>
          </div>
        )}
        {data && cost.unpricedModels > 0 && <p className="tr-stat-sub">{cost.unpricedModels} model(s) unpriced (no default rate). Cost figures are estimates.</p>}
      </CardState>
    </Card>
  );
}
