import { useState } from 'react';
import { api } from '../../lib/api';
import type { KbSearchResult } from '../../lib/types';

const clip = (s: string, n = 200) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

export function KbSearch() {
  const [query, setQuery] = useState('');
  const [result, setResult] = useState<KbSearchResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(e: React.FormEvent) {
    e.preventDefault();
    if (!query.trim()) return;
    setBusy(true); setError(null);
    try { setResult(await api.tool<KbSearchResult>('kb_search', { query: query.trim() })); }
    catch (err) { setError(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  }

  return (
    <div className="tr-kb-panel">
      <form className="tr-kb-form" onSubmit={run}>
        <div className="tr-field" style={{ flex: 1 }}>
          <label htmlFor="tr-kb-search">Search knowledge base</label>
          <input id="tr-kb-search" className="tr-input" type="search" value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search ingested docs…" />
        </div>
        <button type="submit" className="tr-btn tr-btn-primary" disabled={busy || !query.trim()}>Search</button>
      </form>
      {error && <p className="tr-card-error" role="alert">{error}</p>}
      {result && (result.results.length === 0
        ? <p className="tr-card-muted tr-kb-result">No matches.</p>
        : <ul className="tr-kb-results">
            {result.results.map((h) => (
              <li className="tr-kb-hit" key={h.entry.id}>
                <div>{clip(h.entry.content)}</div>
                <div className="tr-kb-hit-meta">score {h.score.toFixed(3)} · {h.content_type}</div>
              </li>
            ))}
          </ul>)}
    </div>
  );
}
