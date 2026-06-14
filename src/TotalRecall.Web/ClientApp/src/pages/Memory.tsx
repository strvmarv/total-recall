import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../lib/api';
import { useAsync } from '../lib/useAsync';
import type { MemoryFilterState } from '../components/memory/MemoryFilters';
import { MemoryFilters } from '../components/memory/MemoryFilters';
import { MemoryTable } from '../components/memory/MemoryTable';
import { MemoryDetail } from '../components/memory/MemoryDetail';
import type { MemoryListEntry, MemoryListResult, MemorySearchResult } from '../lib/types';

function hitToRow(h: MemorySearchResult[number]): MemoryListEntry {
  return {
    id: h.entry.id, tier: h.tier, content_type: h.content_type, content: h.entry.content,
    summary: h.entry.summary, source_tool: null, project: h.entry.project, tags: h.entry.tags,
    created_at: h.entry.created_at, updated_at: h.entry.updated_at, scope: h.entry.scope,
  };
}

export function Memory() {
  const [params] = useSearchParams();
  const [filters, setFilters] = useState<MemoryFilterState>({ query: params.get('q') ?? '', tier: '', type: '' });
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);
  const refresh = () => setRefreshKey((k) => k + 1);

  useEffect(() => { setFilters((f) => ({ ...f, query: params.get('q') ?? f.query })); }, [params]);

  const q = filters.query.trim();
  const { data, error, loading } = useAsync<MemoryListEntry[]>(async () => {
    if (q) {
      const hits = await api.tool<MemorySearchResult>('memory_search', {
        query: q,
        tiers: filters.tier ? [filters.tier] : undefined,
        contentTypes: filters.type ? [filters.type] : undefined,
      });
      return hits.map(hitToRow);
    }
    const res = await api.tool<MemoryListResult>('memory_list', {
      tier: filters.tier || undefined,
      content_type: filters.type || undefined,
      limit: 100,
    });
    return res.entries;
  }, [q, filters.tier, filters.type, refreshKey]);

  const rows = useMemo(() => data ?? [], [data]);

  return (
    <section className="tr-memory" aria-label="Memory">
      <h1>Memory</h1>
      {q && <p className="tr-card-muted">Search: &quot;{q}&quot;</p>}
      <MemoryFilters value={filters} onChange={setFilters} />
      <div className="tr-memory-body">
        <div className="tr-memory-list">
          {loading && <p className="tr-card-muted">Loading…</p>}
          {error && <p className="tr-card-error" role="alert" title={error}>Couldn't load memories.</p>}
          {!loading && !error && <MemoryTable rows={rows} selectedId={selectedId} onSelect={setSelectedId} />}
        </div>
        {selectedId && (
          <MemoryDetail id={selectedId} onClose={() => setSelectedId(null)} onChanged={() => { setSelectedId(null); refresh(); }} />
        )}
      </div>
    </section>
  );
}
