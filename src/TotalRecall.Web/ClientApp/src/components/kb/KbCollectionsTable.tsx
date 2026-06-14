import { timeAgo } from '../../lib/time';
import type { KbCollection } from '../../lib/types';

export function KbCollectionsTable({ collections, onChanged }: {
  collections: KbCollection[];
  onChanged: () => void;
}) {
  void onChanged;
  if (collections.length === 0) return <p className="tr-card-muted">No collections yet. Ingest a file or directory to begin.</p>;
  return (
    <table className="tr-table">
      <thead><tr><th>Name</th><th>Documents</th><th>Chunks</th><th>Source</th><th>Created</th></tr></thead>
      <tbody>
        {collections.map((c) => (
          <tr key={c.id}>
            <td>{c.name}</td>
            <td>{c.document_count}</td>
            <td>{c.chunk_count}</td>
            <td className="tr-table-content" title={c.source_path ?? ''}>{c.source_path ?? '—'}</td>
            <td>{timeAgo(c.created_at)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
