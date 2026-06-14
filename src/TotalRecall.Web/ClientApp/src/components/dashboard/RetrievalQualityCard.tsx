import { Cell, Pie, PieChart, ResponsiveContainer } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { EvalReport } from '../../lib/types';

const pct = (x: number) => `${Math.round(x * 100)}%`;

export function RetrievalQualityCard({ refreshKey }: { refreshKey: number }) {
  const { data, error, loading } = useAsync<EvalReport>(() => api.tool<EvalReport>('eval_report'), [refreshKey]);
  const empty = !!data && data.totalEvents === 0;
  const pie = data ? [
    { name: 'hit', value: data.hitRate, color: 'var(--tr-tier-kb)' },
    { name: 'miss', value: Math.max(0, 1 - data.hitRate), color: 'var(--tr-border)' },
  ] : [];

  return (
    <Card title="Retrieval quality">
      <CardState loading={loading} error={error} empty={empty} emptyText="No retrieval events in the last 7 days.">
        {data && (
          <div style={{ display: 'flex', alignItems: 'center', gap: 'var(--tr-space-5)' }}>
            <div style={{ width: 96, height: 96, position: 'relative' }}>
              <div aria-hidden="true" style={{ width: '100%', height: '100%' }}>
                <ResponsiveContainer>
                  <PieChart>
                    <Pie data={pie} dataKey="value" innerRadius={32} outerRadius={46} startAngle={90} endAngle={-270} isAnimationActive={false}>
                      {pie.map((s) => <Cell key={s.name} fill={s.color} />)}
                    </Pie>
                  </PieChart>
                </ResponsiveContainer>
              </div>
              <div style={{ position: 'absolute', inset: 0, display: 'grid', placeItems: 'center', fontWeight: 700 }}>{pct(data.hitRate)}</div>
            </div>
            <div>
              <div className="tr-stat-sub">hit rate</div>
              <div className="tr-stat-sub">{Math.round(data.avgLatencyMs)} ms avg latency</div>
              <div className="tr-stat-sub">{pct(data.missRate)} miss rate</div>
              <div className="tr-stat-sub">{data.totalEvents} events</div>
            </div>
          </div>
        )}
      </CardState>
    </Card>
  );
}
