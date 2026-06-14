import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { usageArgs, type UsageFilterState } from './UsageFilters';

const n = (x: number) => x.toLocaleString('en-US');

export function UsageBreakdown({ filters, refreshKey }: { filters: UsageFilterState; refreshKey: number }) {
  const { data, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', usageArgs(filters, 'project')),
    [filters.window, filters.host, filters.project, refreshKey],
  );
  const rows = data?.buckets ?? [];

  return (
    <Card title="By project">
      <CardState loading={loading} error={error} empty={!!data && rows.length === 0} emptyText="No usage in this window.">
        <table className="tr-usage-table">
          <thead><tr><th>Project</th><th className="num">Input</th><th className="num">Cache-read</th><th className="num">Output</th><th className="num">Turns</th></tr></thead>
          <tbody>
            {rows.map((b) => (
              <tr key={b.key}>
                <td>{b.key}</td>
                <td className="num">{n(b.input_tokens ?? 0)}</td>
                <td className="num">{n(b.cache_read_tokens ?? 0)}</td>
                <td className="num">{n(b.output_tokens ?? 0)}</td>
                <td className="num">{n(b.turn_count)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </CardState>
    </Card>
  );
}
