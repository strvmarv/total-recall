import { Area, AreaChart, ResponsiveContainer } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { dailyTotals, sumTokens, weekOverWeek } from '../../lib/usageMath';

const fmt = (n: number) => n.toLocaleString('en-US');

export function TokenUsageCard({ refreshKey }: { refreshKey: number }) {
  const { data, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', { window: '30d', group_by: 'day' }),
    [refreshKey],
  );

  const total = data ? sumTokens(data.grand_total) : 0;
  const empty = !!data && (data.buckets.length === 0 || total === 0);
  const wow = data ? weekOverWeek(data.buckets, data.query.end_ms) : null;
  const series = data ? dailyTotals(data.buckets).map((v, i) => ({ i, v })) : [];

  return (
    <Card title="Token usage" drillTo="/usage" drillLabel="open Usage →">
      <CardState loading={loading} error={error} empty={empty} emptyText="Usage tracking unavailable for this backend/window.">
        {data && (
          <>
            <div className="tr-stat-figure">{fmt(total)}</div>
            <div className="tr-stat-sub">
              in {fmt(data.grand_total.input_tokens ?? 0)} · out {fmt(data.grand_total.output_tokens ?? 0)} tokens
            </div>
            {wow && wow.deltaPercent !== null && (
              <div className={`tr-stat-sub ${wow.deltaPercent >= 0 ? 'tr-delta-up' : 'tr-delta-down'}`}>
                {wow.deltaPercent >= 0 ? '▲' : '▼'} {Math.abs(wow.deltaPercent).toFixed(1)}% vs prior week
              </div>
            )}
            <div aria-hidden="true" style={{ width: '100%', height: 48, marginTop: 'var(--tr-space-3)' }}>
              <ResponsiveContainer>
                <AreaChart data={series} margin={{ top: 4, right: 0, bottom: 0, left: 0 }}>
                  <Area type="monotone" dataKey="v" stroke="var(--tr-accent)" fill="var(--tr-accent-weak)" isAnimationActive={false} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
            <p className="tr-stat-sub">Cost estimates arrive in the Usage tab.</p>
          </>
        )}
      </CardState>
    </Card>
  );
}
