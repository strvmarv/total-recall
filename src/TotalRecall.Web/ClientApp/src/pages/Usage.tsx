import { useState } from 'react';
import { UsageFilters, type UsageFilterState } from '../components/usage/UsageFilters';
import { UsageHeadline } from '../components/usage/UsageHeadline';
import { UsageComposition } from '../components/usage/UsageComposition';
import { UsageModelMix } from '../components/usage/UsageModelMix';
import { UsageBreakdown } from '../components/usage/UsageBreakdown';
import { UsageTopSessions } from '../components/usage/UsageTopSessions';

export function Usage() {
  const [filters, setFilters] = useState<UsageFilterState>({ window: '7d', host: '', project: '' });
  const [refreshKey] = useState(0);
  return (
    <section className="tr-usage" aria-label="Usage">
      <h1>Usage</h1>
      <UsageFilters value={filters} onChange={setFilters} />
      <UsageHeadline filters={filters} refreshKey={refreshKey} />
      <div className="tr-card-grid">
        <UsageComposition filters={filters} refreshKey={refreshKey} />
        <UsageModelMix filters={filters} refreshKey={refreshKey} />
      </div>
      <UsageBreakdown filters={filters} refreshKey={refreshKey} />
      <UsageTopSessions filters={filters} refreshKey={refreshKey} />
    </section>
  );
}
