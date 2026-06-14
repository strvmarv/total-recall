import { useState } from 'react';
import { api } from '../../lib/api';
import { timeAgo } from '../../lib/time';
import { ConfirmDialog } from '../ConfirmDialog';
import type { KbCollection } from '../../lib/types';

export function KbCollectionsTable({ collections, onChanged }: {
  collections: KbCollection[];
  onChanged: () => void;
}) {
  const [pending, setPending] = useState<{ title: string; body?: string; confirmLabel: string; danger?: boolean; run: () => Promise<unknown> } | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function confirmRun() {
    if (!pending) return;
    setBusy(true); setError(null);
    try { await pending.run(); setPending(null); onChanged(); }
    catch (e) { setError(e instanceof Error ? e.message : String(e)); setPending(null); }
    finally { setBusy(false); }
  }

  if (collections.length === 0) return <p className="tr-card-muted">No collections yet. Ingest a file or directory to begin.</p>;

  return (
    <>
      {error && <p className="tr-card-error" role="alert">{error}</p>}
      <table className="tr-table">
        <thead><tr><th>Name</th><th>Documents</th><th>Chunks</th><th>Source</th><th>Created</th><th>Actions</th></tr></thead>
        <tbody>
          {collections.map((c) => (
            <tr key={c.id}>
              <td>{c.name}</td>
              <td>{c.document_count}</td>
              <td>{c.chunk_count}</td>
              <td className="tr-table-content" title={c.source_path ?? ''}>{c.source_path ?? '—'}</td>
              <td>{timeAgo(c.created_at)}</td>
              <td>
                {!pending && (
                  <>
                    <button type="button" className="tr-btn" disabled={busy} onClick={() => { setError(null); setPending({ title: `Refresh "${c.name}"?`, body: 'Re-ingests from the source path.', confirmLabel: 'Refresh', run: () => api.tool('kb_refresh', { collection: c.id }) }); }}>Refresh</button>{' '}
                    <button type="button" className="tr-btn tr-btn-danger" disabled={busy} onClick={() => { setError(null); setPending({ title: `Remove "${c.name}"?`, body: 'Deletes the collection and all its chunks. This cannot be undone.', confirmLabel: 'Remove', danger: true, run: () => api.tool('kb_remove', { id: c.id }) }); }}>Remove</button>
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {pending && <ConfirmDialog title={pending.title} body={pending.body} confirmLabel={pending.confirmLabel} danger={pending.danger} onConfirm={confirmRun} onCancel={() => setPending(null)} />}
    </>
  );
}
