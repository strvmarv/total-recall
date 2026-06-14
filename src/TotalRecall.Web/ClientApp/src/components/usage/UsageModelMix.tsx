import { Cell, Pie, PieChart, ResponsiveContainer } from 'recharts';
import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { usageArgs, type UsageFilterState } from './UsageFilters';

const COLORS = ['var(--tr-tier-pinned)', 'var(--tr-tier-cold)', 'var(--tr-tier-warm)', 'var(--tr-tier-kb)', 'var(--tr-tier-hot)'];
function friendly(model: string): string {
  const m = model.toLowerCase();
  if (m.includes('opus')) return 'Opus';
  if (m.includes('sonnet')) return 'Sonnet';
  if (m.includes('haiku')) return 'Haiku';
  return model;
}
function tokensOf(b: { input_tokens: number | null; cache_creation_tokens: number | null; cache_read_tokens: number | null; output_tokens: number | null }) {
  return (b.input_tokens ?? 0) + (b.cache_creation_tokens ?? 0) + (b.cache_read_tokens ?? 0) + (b.output_tokens ?? 0);
}

export function UsageModelMix({ filters, refreshKey }: { filters: UsageFilterState; refreshKey: number }) {
  const { data, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', usageArgs(filters, 'model')),
    [filters.window, filters.host, filters.project, refreshKey],
  );
  const slices = (data?.buckets ?? []).map((b, i) => ({ id: b.key, name: friendly(b.key), value: tokensOf(b), color: COLORS[i % COLORS.length] })).filter((s) => s.value > 0);

  return (
    <Card title="Model mix (by tokens)">
      <CardState loading={loading} error={error} empty={!!data && slices.length === 0} emptyText="No usage in this window.">
        <div style={{ display: 'flex', gap: 'var(--tr-space-4)', alignItems: 'center' }}>
          <div aria-hidden="true" style={{ width: 120, height: 120 }}>
            <ResponsiveContainer>
              <PieChart><Pie data={slices} dataKey="value" innerRadius={36} outerRadius={56} isAnimationActive={false}>{slices.map((s) => <Cell key={s.id} fill={s.color} />)}</Pie></PieChart>
            </ResponsiveContainer>
          </div>
          <ul className="tr-legend" style={{ flexDirection: 'column', gap: 'var(--tr-space-1)' }}>
            {slices.map((s) => <li className="tr-legend-item" key={s.id}><span className="tr-legend-dot" style={{ background: s.color }} />{s.name} <strong>{s.value.toLocaleString('en-US')}</strong></li>)}
          </ul>
        </div>
      </CardState>
    </Card>
  );
}
