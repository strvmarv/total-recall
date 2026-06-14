import { useState } from 'react';
import { api } from '../lib/api';
import { useAsync } from '../lib/useAsync';
import { KbCollectionsTable } from '../components/kb/KbCollectionsTable';
import { KbIngest } from '../components/kb/KbIngest';
import { KbSearch } from '../components/kb/KbSearch';
import type { KbListCollectionsResult } from '../lib/types';

export function KnowledgeBase() {
  const [refreshKey, setRefreshKey] = useState(0);
  const refresh = () => setRefreshKey((k) => k + 1);
  const { data, error, loading } = useAsync<KbListCollectionsResult>(() => api.tool<KbListCollectionsResult>('kb_list_collections'), [refreshKey]);

  return (
    <section className="tr-kb" aria-label="Knowledge Base">
      <h1>Knowledge Base</h1>
      <KbSearch />
      <KbIngest onIngested={refresh} />
      <h2 className="tr-kb-heading">Collections</h2>
      {loading && <p className="tr-card-muted">Loading…</p>}
      {error && <p className="tr-card-error" role="alert" title={error}>Couldn't load collections.</p>}
      {!loading && !error && <KbCollectionsTable collections={data?.collections ?? []} onChanged={refresh} />}
    </section>
  );
}
