import type { MemoryListEntry } from '../../lib/types';

const clip = (s: string, n = 120) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

export function MemoryTable({ rows, selectedId, onSelect }: {
  rows: MemoryListEntry[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  if (rows.length === 0) return <p className="tr-card-muted">No entries match these filters.</p>;
  return (
    <table className="tr-table">
      <thead><tr><th>Tier</th><th>Type</th><th>Content</th><th>Project</th></tr></thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.id} className={r.id === selectedId ? 'is-selected' : undefined}
            onClick={() => onSelect(r.id)} tabIndex={0}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(r.id); } }}>
            <td><span className={`tr-tier-badge tr-tier-${r.tier}`}>{r.tier}</span></td>
            <td>{r.content_type}</td>
            <td className="tr-table-content" title={r.content}>{clip(r.content)}</td>
            <td>{r.project ?? '—'}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
