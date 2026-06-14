import { Area, AreaChart, ResponsiveContainer } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { MemoryHistoryResult } from '../../lib/types';
import { movementsByDay, transitionCounts } from '../../lib/trendsMath';

export function TrendsCard({ refreshKey }: { refreshKey: number }) {
  const { data, error, loading } = useAsync<MemoryHistoryResult>(
    () => api.tool<MemoryHistoryResult>('memory_history', { limit: 1000 }),
    [refreshKey],
  );
  const empty = !!data && data.movements.length === 0;
  const series = data ? movementsByDay(data.movements).map((d, i) => ({ i, v: d.count })) : [];
  const transitions = data ? transitionCounts(data.movements).slice(0, 4) : [];

  return (
    <Card title="Trends">
      <CardState loading={loading} error={error} empty={empty} emptyText="No compaction activity yet.">
        {data && (
          <>
            <div className="tr-stat-figure">{data.movements.length} movements</div>
            <div className="tr-stat-sub">tier movements (compaction log)</div>
            <div style={{ width: '100%', height: 48, marginTop: 'var(--tr-space-3)' }}>
              <ResponsiveContainer>
                <AreaChart data={series} margin={{ top: 4, right: 0, bottom: 0, left: 0 }}>
                  <Area type="monotone" dataKey="v" stroke="var(--tr-tier-warm)" fill="#fde9c8" isAnimationActive={false} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
            <ul className="tr-legend" style={{ flexDirection: 'column', gap: 'var(--tr-space-1)' }}>
              {transitions.map((tr) => (
                <li className="tr-legend-item" key={tr.label} style={{ justifyContent: 'space-between', width: '100%' }}>
                  <span>{tr.label}</span><strong>{tr.count}</strong>
                </li>
              ))}
            </ul>
          </>
        )}
      </CardState>
    </Card>
  );
}
