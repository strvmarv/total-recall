import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../lib/api';
import { NAV_ITEMS } from './nav';
import type { MemorySearchResult, KbSearchResult } from '../lib/types';

const clip = (s: string, n = 80) => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

interface Hit { key: string; label: string; group: string; run: () => void; }

export function CommandPalette() {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const [hits, setHits] = useState<Hit[]>([]);
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setOpen((o) => !o); }
      if (e.key === 'Escape') setOpen(false);
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  useEffect(() => { if (open) { setQ(''); setHits([]); inputRef.current?.focus(); } }, [open]);

  const navCommands = useMemo<Hit[]>(
    () => NAV_ITEMS.map((n) => ({
      key: `nav:${n.path}`, label: n.label, group: 'Pages',
      run: () => { navigate(n.path); setOpen(false); },
    })),
    [navigate],
  );

  useEffect(() => {
    const term = q.trim();
    if (term.length < 2) { setHits([]); return; }
    let alive = true;
    const t = setTimeout(async () => {
      try {
        const [mem, kb] = await Promise.all([
          api.tool<MemorySearchResult>('memory_search', { query: term }),
          api.tool<KbSearchResult>('kb_search', { query: term }),
        ]);
        if (!alive) return;
        const m: Hit[] = (mem ?? []).slice(0, 6).map((h) => ({
          key: `mem:${h.entry.id}`, label: clip(h.entry.content), group: 'Memories',
          run: () => { navigate(`/memory?q=${encodeURIComponent(term)}`); setOpen(false); },
        }));
        const k: Hit[] = (kb?.results ?? []).slice(0, 6).map((h) => ({
          key: `kb:${h.entry.id}`, label: clip(h.entry.content), group: 'Knowledge',
          run: () => { navigate('/kb'); setOpen(false); },
        }));
        setHits([...m, ...k]);
      } catch { if (alive) setHits([]); }
    }, 180);
    return () => { alive = false; clearTimeout(t); };
  }, [q, navigate]);

  if (!open) return null;

  const term = q.trim();
  const list = term.length >= 2 ? hits : navCommands;

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (list[0]) { list[0].run(); return; }
    if (term) { navigate(`/memory?q=${encodeURIComponent(term)}`); setOpen(false); }
  }

  return (
    <div className="tr-cmdk-backdrop" onClick={() => setOpen(false)}>
      <div
        className="tr-cmdk" role="dialog" aria-label="Command palette" aria-modal="true"
        onClick={(e) => e.stopPropagation()}
      >
        <form role="search" onSubmit={onSubmit}>
          <input
            ref={inputRef} role="combobox" aria-expanded="true" aria-controls="tr-cmdk-list"
            className="tr-cmdk-input" placeholder="Jump to a page, or search memories & knowledge…"
            value={q} onChange={(e) => setQ(e.target.value)}
          />
        </form>
        <ul id="tr-cmdk-list" className="tr-cmdk-list" role="listbox">
          {list.length === 0 && <li className="tr-cmdk-empty">No matches.</li>}
          {list.map((h) => (
            <li key={h.key} role="option" aria-selected="false">
              <button type="button" className="tr-cmdk-item" onClick={h.run}>
                <span className="tr-cmdk-group">{h.group}</span>
                <span className="tr-cmdk-label">{h.label}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
