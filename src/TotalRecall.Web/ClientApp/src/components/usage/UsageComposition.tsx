import { Bar, BarChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { usageArgs, type UsageFilterState } from './UsageFilters';
import { useChartTheme } from '../../lib/chartTheme';

export function UsageComposition({ filters, refreshKey }: { filters: UsageFilterState; refreshKey: number }) {
  const theme = useChartTheme();
  const { data, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', usageArgs(filters, 'day')),
    [filters.window, filters.host, filters.project, refreshKey],
  );
  const rows = (data?.buckets ?? []).map((b) => ({
    day: b.key.slice(5), input: b.input_tokens ?? 0, cacheRead: b.cache_read_tokens ?? 0, cacheCreate: b.cache_creation_tokens ?? 0, output: b.output_tokens ?? 0,
  }));

  return (
    <Card title="Token composition (by day)">
      <CardState loading={loading} error={error} empty={!!data && rows.length === 0} emptyText="No usage in this window.">
        <div aria-hidden="true" style={{ width: '100%', height: 220 }}>
          <ResponsiveContainer>
            <BarChart data={rows} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
              <XAxis dataKey="day" fontSize={11} tick={{ fill: theme.tick }} /><YAxis fontSize={11} width={48} tick={{ fill: theme.tick }} /><Tooltip />
              <Bar dataKey="input" stackId="t" fill={theme.tierCold} isAnimationActive={false} />
              <Bar dataKey="cacheRead" stackId="t" fill={theme.tierKb} isAnimationActive={false} />
              <Bar dataKey="cacheCreate" stackId="t" fill={theme.tierWarm} isAnimationActive={false} />
              <Bar dataKey="output" stackId="t" fill={theme.tierPinned} isAnimationActive={false} />
            </BarChart>
          </ResponsiveContainer>
        </div>
        <ul className="tr-legend">
          <li className="tr-legend-item"><span className="tr-legend-dot" style={{ background: 'var(--tr-tier-cold)' }} />input</li>
          <li className="tr-legend-item"><span className="tr-legend-dot" style={{ background: 'var(--tr-tier-kb)' }} />cache-read</li>
          <li className="tr-legend-item"><span className="tr-legend-dot" style={{ background: 'var(--tr-tier-warm)' }} />cache-create</li>
          <li className="tr-legend-item"><span className="tr-legend-dot" style={{ background: 'var(--tr-tier-pinned)' }} />output</li>
        </ul>
      </CardState>
    </Card>
  );
}
