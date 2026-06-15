import { Area, AreaChart, ResponsiveContainer } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { dailyTotals, sumTokens, weekOverWeek } from '../../lib/usageMath';
import { estimatedCost } from '../../lib/usageCost';
import { useChartTheme } from '../../lib/chartTheme';

const fmt = (n: number) => n.toLocaleString('en-US');

export function TokenUsageCard({ refreshKey }: { refreshKey: number }) {
  const theme = useChartTheme();
  // Primary fetch: day buckets — drives loading/empty state, sparkline, WoW delta
  const { data: dayData, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', { window: '30d', group_by: 'day' }),
    [refreshKey],
  );

  // Secondary fetch: model buckets — drives estimated cost and headline token totals
  const { data: modelData } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', { window: '30d', group_by: 'model' }),
    [refreshKey],
  );

  const total = modelData ? sumTokens(modelData.grand_total) : 0;
  const empty = !!dayData && (dayData.buckets.length === 0 || sumTokens(dayData.grand_total) === 0);
  const wow = dayData ? weekOverWeek(dayData.buckets, dayData.query.end_ms) : null;
  const series = dayData ? dailyTotals(dayData.buckets).map((v, i) => ({ i, v })) : [];
  const cost = modelData ? estimatedCost(modelData.buckets) : null;

  return (
    <Card title="Token usage" drillTo="/usage" drillLabel="open Usage →">
      <CardState loading={loading} error={error} empty={empty} emptyText="Usage tracking unavailable for this backend/window.">
        {dayData && (
          <>
            <div className="tr-stat-figure">{fmt(total)}</div>
            <div className="tr-stat-sub">
              in {fmt(modelData?.grand_total.input_tokens ?? 0)} · out {fmt(modelData?.grand_total.output_tokens ?? 0)} tokens
            </div>
            {wow && wow.deltaPercent !== null && (
              <div className={`tr-stat-sub ${wow.deltaPercent >= 0 ? 'tr-delta-up' : 'tr-delta-down'}`}>
                {wow.deltaPercent >= 0 ? '▲' : '▼'} {Math.abs(wow.deltaPercent).toFixed(1)}% vs prior week
              </div>
            )}
            <div aria-hidden="true" style={{ width: '100%', height: 48, marginTop: 'var(--tr-space-3)' }}>
              <ResponsiveContainer>
                <AreaChart data={series} margin={{ top: 4, right: 0, bottom: 0, left: 0 }}>
                  <Area type="monotone" dataKey="v" stroke={theme.accent} fill={theme.accent} fillOpacity={0.1} isAnimationActive={false} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
            {cost && cost.total > 0
              ? <div className="tr-stat-sub">~${cost.total.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} estimated (30d)</div>
              : <p className="tr-stat-sub">Cost shown when priced models are present.</p>}
          </>
        )}
      </CardState>
    </Card>
  );
}
