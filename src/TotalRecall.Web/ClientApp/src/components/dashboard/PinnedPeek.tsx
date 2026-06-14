import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { MemoryListResult } from '../../lib/types';

const clip = (s: string, n = 96) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

export function PinnedPeek({ refreshKey }: { refreshKey: number }) {
  const { data, error, loading } = useAsync<MemoryListResult>(
    () => api.tool<MemoryListResult>('memory_list', { tier: 'pinned', limit: 8 }),
    [refreshKey],
  );
  const empty = !!data && data.entries.length === 0;

  return (
    <Card title="📌 Pinned directives" drillTo="/memory" drillLabel={data ? `${data.total} pinned →` : 'view all →'}>
      <CardState loading={loading} error={error} empty={empty} emptyText="No pinned directives.">
        {data && (
          <ul className="tr-peek-list">
            {data.entries.map((e) => (
              <li className="tr-peek-row" key={e.id}>
                <span className="tr-peek-text" title={e.content}>{clip(e.content)}</span>
              </li>
            ))}
          </ul>
        )}
      </CardState>
    </Card>
  );
}
