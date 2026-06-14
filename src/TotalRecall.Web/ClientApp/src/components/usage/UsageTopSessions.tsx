import { Card, CardState } from '../Card';
import { useAsync } from '../../lib/useAsync';
import { api } from '../../lib/api';
import type { UsageResult } from '../../lib/types';
import { usageArgs, type UsageFilterState } from './UsageFilters';
import { cacheSavings } from '../../lib/usageCost';

const n = (x: number) => x.toLocaleString('en-US');
const usd = (x: number) => `$${x.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
// session granularity is only retained for <=30d
const SESSION_WINDOWS = new Set(['5h', '1d', '7d', '30d']);

export function UsageTopSessions({ filters, refreshKey }: { filters: UsageFilterState; refreshKey: number }) {
  const win = SESSION_WINDOWS.has(filters.window) ? filters.window : '30d';
  const { data, error, loading } = useAsync<UsageResult>(
    () => api.tool<UsageResult>('usage_status', usageArgs({ ...filters, window: win }, 'session', { top: 10 })),
    [win, filters.host, filters.project, refreshKey],
  );
  const rows = data?.buckets ?? [];
  const savings = data ? cacheSavings(data.buckets) : { tokens: 0, usd: 0 };
  const cov = data?.coverage;

  return (
    <Card title="Top sessions (last 30d)">
      <CardState loading={loading} error={error} empty={!!data && rows.length === 0} emptyText="No session-level usage in this window.">
        {data && (
          <>
            <p className="tr-stat-sub">Cache reads saved ~{n(savings.tokens)} tokens (~{usd(savings.usd)} estimated).</p>
            {cov && <p className="tr-stat-sub">{cov.sessions_with_full_token_data} of {cov.sessions_with_full_token_data + cov.sessions_with_partial_token_data} sessions have full token data ({Math.round(cov.fidelity_percent)}%).</p>}
            <table className="tr-usage-table">
              <thead><tr><th>Session</th><th className="num">Input</th><th className="num">Cache-read</th><th className="num">Output</th><th className="num">Turns</th></tr></thead>
              <tbody>
                {rows.map((b) => (
                  <tr key={b.key}>
                    <td title={b.key}>{b.key.length > 16 ? `${b.key.slice(0, 15)}…` : b.key}</td>
                    <td className="num">{n(b.input_tokens ?? 0)}</td>
                    <td className="num">{n(b.cache_read_tokens ?? 0)}</td>
                    <td className="num">{n(b.output_tokens ?? 0)}</td>
                    <td className="num">{n(b.turn_count)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </>
        )}
      </CardState>
    </Card>
  );
}
