import { Bar, BarChart, ResponsiveContainer, XAxis } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { StatusResult } from '../../lib/types';
import { useChartTheme, type ChartTheme } from '../../lib/chartTheme';

interface Seg { key: string; label: string; value: number; color: string; }

function segments(s: StatusResult, theme: ChartTheme): Seg[] {
  const t = s.tierSizes;
  return [
    { key: 'pinned', label: 'Pinned', value: t.pinned_memories + t.pinned_knowledge, color: theme.tierPinned },
    { key: 'hot', label: 'Hot', value: t.hot_memories + t.hot_knowledge, color: theme.tierHot },
    { key: 'warm', label: 'Warm', value: t.warm_memories + t.warm_knowledge, color: theme.tierWarm },
    { key: 'cold', label: 'Cold', value: t.cold_memories + t.cold_knowledge, color: theme.tierCold },
    { key: 'kb', label: 'KB', value: s.knowledgeBase.totalChunks, color: theme.tierKb },
  ];
}

export function TierCompositionCard({ refreshKey }: { refreshKey: number }) {
  const theme = useChartTheme();
  const { data, error, loading } = useAsync<StatusResult>(() => api.tool<StatusResult>('status'), [refreshKey]);
  const segs = data ? segments(data, theme) : [];
  const total = segs.reduce((a, s) => a + s.value, 0);
  const collections = data?.knowledgeBase.collections.length ?? 0;
  const row = Object.fromEntries(segs.map((s) => [s.key, s.value])) as Record<string, number>;

  return (
    <Card title="Tier composition">
      <CardState loading={loading} error={error} empty={!!data && total === 0} emptyText="No memories yet.">
        {data && (
          <>
            <div aria-hidden="true" style={{ width: '100%', height: 56 }}>
              <ResponsiveContainer>
                <BarChart layout="vertical" data={[{ name: 'tiers', ...row }]} stackOffset="expand" margin={{ top: 0, right: 0, bottom: 0, left: 0 }}>
                  <XAxis type="number" hide domain={[0, 1]} />
                  {segs.map((s) => (
                    <Bar key={s.key} dataKey={s.key} stackId="t" fill={s.color} isAnimationActive={false} />
                  ))}
                </BarChart>
              </ResponsiveContainer>
            </div>
            <ul className="tr-legend">
              {segs.map((s) => (
                <li className="tr-legend-item" key={s.key}>
                  <span className="tr-legend-dot" style={{ background: s.color }} />
                  {s.label} <strong>{s.value}</strong>
                </li>
              ))}
            </ul>
            <p className="tr-stat-sub">{collections} collections</p>
          </>
        )}
      </CardState>
    </Card>
  );
}
