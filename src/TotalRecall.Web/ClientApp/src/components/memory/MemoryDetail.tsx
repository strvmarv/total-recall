import { useState } from 'react';
import { api } from '../../lib/api';
import { useAsync } from '../../lib/useAsync';
import { timeAgo } from '../../lib/time';
import type { MemoryInspectResult, LineageNode } from '../../lib/types';
import { ConfirmDialog } from '../ConfirmDialog';

function LineageTree({ node }: { node: LineageNode }) {
  return (
    <ul className="tr-lineage">
      <li>
        <code>{node.id.slice(0, 8)}</code>
        {node.reason ? <span className="tr-card-muted"> · {node.reason}</span> : null}
        {node.sources && node.sources.length > 0 && node.sources.map((s) => <LineageTree key={s.id} node={s} />)}
      </li>
    </ul>
  );
}

export function MemoryDetail({ id, onClose, onChanged }: {
  id: string;
  onClose: () => void;
  onChanged: () => void;
}) {
  const inspect = useAsync<MemoryInspectResult | null>(() => api.tool<MemoryInspectResult | null>('memory_inspect', { id }), [id]);
  const lineage = useAsync<LineageNode | null>(() => api.tool<LineageNode | null>('memory_lineage', { id }), [id]);
  const d = inspect.data;

  const [pending, setPending] = useState<{ title: string; body?: string; confirmLabel: string; danger?: boolean; run: () => Promise<unknown> } | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');

  async function confirmRun() {
    if (!pending) return;
    setBusy(true); setActionError(null);
    try { await pending.run(); setPending(null); onChanged(); }
    catch (e) { setActionError(e instanceof Error ? e.message : String(e)); setPending(null); }
    finally { setBusy(false); }
  }

  return (
    <aside className="tr-memory-detail" aria-label="Entry detail">
      <div className="tr-detail-head">
        <strong>Entry</strong>
        <button type="button" className="tr-refresh" onClick={onClose} aria-label="Close detail">✕</button>
      </div>
      {inspect.loading && <p className="tr-card-muted">Loading…</p>}
      {inspect.error && <p className="tr-card-error" role="alert" title={inspect.error}>Couldn't load this entry.</p>}
      {d && (
        <>
          {editing ? (
            <form className="tr-edit-form" onSubmit={async (e) => {
              e.preventDefault();
              setBusy(true); setActionError(null);
              try { await api.tool('memory_update', { id, content: draft }); setEditing(false); onChanged(); }
              catch (err) { setActionError(err instanceof Error ? err.message : String(err)); }
              finally { setBusy(false); }
            }}>
              <label htmlFor="tr-edit-content">Content (saving re-embeds the entry)</label>
              <textarea id="tr-edit-content" value={draft} onChange={(e) => setDraft(e.target.value)} rows={6} />
              <div className="tr-modal-actions">
                <button type="button" className="tr-btn" onClick={() => setEditing(false)} disabled={busy}>Cancel</button>
                <button type="submit" className="tr-btn tr-btn-primary" disabled={busy || !draft.trim()}>Save</button>
              </div>
            </form>
          ) : (
            <p className="tr-detail-content">{d.content}</p>
          )}
          <dl className="tr-detail-meta">
            <div><dt>Tier</dt><dd>{d.tier}</dd></div>
            <div><dt>Type</dt><dd>{d.content_type}</dd></div>
            <div><dt>Project</dt><dd>{d.project ?? '—'}</dd></div>
            <div><dt>Tags</dt><dd>{d.tags.length ? d.tags.join(', ') : '—'}</dd></div>
            <div><dt>Access count</dt><dd>{d.access_count}</dd></div>
            <div><dt>Decay score</dt><dd>{d.decay_score.toFixed(3)}</dd></div>
            <div><dt>Updated</dt><dd>{timeAgo(d.updated_at)}</dd></div>
            <div><dt>Source tool</dt><dd>{d.source_tool ?? '—'}</dd></div>
          </dl>
          <div className="tr-detail-section">
            <h3>Lineage</h3>
            {lineage.loading && <p className="tr-card-muted">Loading…</p>}
            {lineage.error && <p className="tr-card-error" role="alert" title={lineage.error}>Couldn't load lineage.</p>}
            {!lineage.loading && !lineage.error && (lineage.data ? <LineageTree node={lineage.data} /> : <p className="tr-card-muted">No lineage.</p>)}
          </div>
          {!pending && !editing && (
            <div className="tr-detail-actions">
              {d.tier === 'pinned'
                ? <button type="button" className="tr-btn" disabled={busy} onClick={() => setPending({ title: 'Unpin this entry?', confirmLabel: 'Unpin', run: () => api.tool('memory_unpin', { id }) })}>Unpin</button>
                : <button type="button" className="tr-btn" disabled={busy} onClick={() => setPending({ title: 'Pin this entry?', confirmLabel: 'Pin', run: () => api.tool('memory_pin', { id }) })}>Pin</button>}
              <button type="button" className="tr-btn" disabled={busy} onClick={() => setPending({ title: 'Promote this entry?', confirmLabel: 'Promote', run: () => api.tool('memory_promote', { id }) })}>Promote</button>
              <button type="button" className="tr-btn" disabled={busy} onClick={() => setPending({ title: 'Demote this entry?', confirmLabel: 'Demote', run: () => api.tool('memory_demote', { id }) })}>Demote</button>
              <button type="button" className="tr-btn" disabled={busy} onClick={() => { setDraft(d.content); setEditing(true); }}>Edit</button>
              <button type="button" className="tr-btn tr-btn-danger" disabled={busy} onClick={() => setPending({ title: 'Delete this entry?', body: 'This cannot be undone.', confirmLabel: 'Delete', danger: true, run: () => api.tool('memory_delete', { id }) })}>Delete</button>
            </div>
          )}
          {actionError && <p className="tr-card-error" role="alert">{actionError}</p>}
          {pending && <ConfirmDialog title={pending.title} body={pending.body} confirmLabel={pending.confirmLabel} danger={pending.danger} onConfirm={confirmRun} onCancel={() => setPending(null)} />}
        </>
      )}
    </aside>
  );
}
