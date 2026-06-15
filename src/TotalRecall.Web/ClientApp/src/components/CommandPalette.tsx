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
  const [active, setActive] = useState(0);
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);
  const openRef = useRef(open);
  const prevFocusRef = useRef<HTMLElement | null>(null);

  useEffect(() => { openRef.current = open; }, [open]);

  // Global open/close hotkeys (Esc only acts when open).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setOpen((o) => !o); }
      else if (e.key === 'Escape' && openRef.current) { setOpen(false); }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  // Focus the input on open; restore previously-focused element on close.
  useEffect(() => {
    if (open) {
      prevFocusRef.current = document.activeElement as HTMLElement | null;
      setQ(''); setHits([]); setActive(0);
      inputRef.current?.focus();
    } else {
      prevFocusRef.current?.focus();
      prevFocusRef.current = null;
    }
  }, [open]);

  const navCommands = useMemo<Hit[]>(
    () => NAV_ITEMS.map((n) => ({
      key: `nav:${n.path}`, label: n.label, group: 'Pages',
      run: () => { navigate(n.path); setOpen(false); },
    })),
    [navigate],
  );

  // Debounced live search for queries >= 2 chars (independent per-tool results).
  useEffect(() => {
    const term = q.trim();
    if (!open || term.length < 2) { setHits([]); return; }
    let alive = true;
    const t = setTimeout(async () => {
      const [memR, kbR] = await Promise.allSettled([
        api.tool<MemorySearchResult>('memory_search', { query: term }),
        api.tool<KbSearchResult>('kb_search', { query: term }),
      ]);
      if (!alive) return;
      const mem = memR.status === 'fulfilled' ? (memR.value ?? []) : [];
      const kb = kbR.status === 'fulfilled' ? (kbR.value?.results ?? []) : [];
      const m: Hit[] = mem.slice(0, 6).map((h) => ({
        key: `mem:${h.entry.id}`, label: clip(h.entry.content), group: 'Memories',
        run: () => { navigate(`/memory?q=${encodeURIComponent(term)}`); setOpen(false); },
      }));
      const k: Hit[] = kb.slice(0, 6).map((h) => ({
        key: `kb:${h.entry.id}`, label: clip(h.entry.content), group: 'Knowledge',
        run: () => { navigate('/kb'); setOpen(false); },
      }));
      setHits([...m, ...k]);
      setActive(0);
    }, 180);
    return () => { alive = false; clearTimeout(t); };
  }, [q, open, navigate]);

  if (!open) return null;

  const term = q.trim();
  const list = term.length >= 2 ? hits : navCommands;
  const activeIdx = list.length ? Math.min(active, list.length - 1) : -1;

  function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (activeIdx >= 0 && list[activeIdx]) { list[activeIdx].run(); return; }
    if (term) { navigate(`/memory?q=${encodeURIComponent(term)}`); setOpen(false); }
  }

  function onInputKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive((i) => (list.length ? Math.min(i + 1, list.length - 1) : 0)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((i) => Math.max(i - 1, 0)); }
    else if (e.key === 'Tab') { e.preventDefault(); } // keep focus within the palette
  }

  return (
    <div className="tr-cmdk-backdrop" onClick={() => setOpen(false)}>
      <div className="tr-cmdk" role="dialog" aria-label="Command palette" aria-modal="true" onClick={(e) => e.stopPropagation()}>
        <form role="search" onSubmit={onSubmit}>
          <input
            ref={inputRef} role="combobox" aria-expanded="true" aria-controls="tr-cmdk-list"
            aria-activedescendant={activeIdx >= 0 ? `tr-cmdk-opt-${activeIdx}` : undefined}
            className="tr-cmdk-input" placeholder="Jump to a page, or search memories & knowledge…"
            value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={onInputKeyDown}
          />
        </form>
        <ul id="tr-cmdk-list" className="tr-cmdk-list" role="listbox">
          {list.length === 0 && <li className="tr-cmdk-empty">No matches.</li>}
          {list.map((h, i) => (
            <li key={h.key} id={`tr-cmdk-opt-${i}`} role="option" aria-selected={i === activeIdx}>
              <button
                type="button" tabIndex={-1}
                className={i === activeIdx ? 'tr-cmdk-item is-active' : 'tr-cmdk-item'}
                onClick={h.run} onMouseMove={() => setActive(i)}
              >
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
