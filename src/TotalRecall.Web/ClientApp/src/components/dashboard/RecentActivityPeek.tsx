import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import { timeAgo } from '../../lib/time';
import type { MemoryRecentResult } from '../../lib/types';

const KNOWN = new Set(['correction', 'preference', 'decision', 'surfaced', 'imported', 'compacted', 'ingested']);
function tagStyle(type: string): React.CSSProperties {
  const t = KNOWN.has(type) ? type : 'surfaced';
  return { background: `var(--tr-tag-${t})`, color: `var(--tr-tag-${t}-fg)` };
}

export function RecentActivityPeek({ refreshKey }: { refreshKey: number }) {
  const { data, error, loading } = useAsync<MemoryRecentResult>(
    () => api.tool<MemoryRecentResult>('memory_recent', { limit: 8, order: 'created' }),
    [refreshKey],
  );
  const empty = !!data && data.entries.length === 0;

  return (
    <Card title="🕒 Recent activity" drillTo="/memory" drillLabel="view all →">
      <CardState loading={loading} error={error} empty={empty} emptyText="No recent activity.">
        {data && (
          <ul className="tr-peek-list">
            {data.entries.map((e) => (
              <li className="tr-peek-row" key={e.id}>
                <span className="tr-tag" style={tagStyle(e.entry_type)}>{e.entry_type}</span>
                <span className="tr-peek-text" title={e.preview}>{e.preview}</span>
                <span className="tr-peek-time">{timeAgo(e.created_at)}</span>
              </li>
            ))}
          </ul>
        )}
      </CardState>
    </Card>
  );
}
