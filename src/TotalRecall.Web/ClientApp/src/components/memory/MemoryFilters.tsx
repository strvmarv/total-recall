export interface MemoryFilterState { query: string; tier: string; type: string; }

export function MemoryFilters({ value, onChange }: {
  value: MemoryFilterState;
  onChange: (next: MemoryFilterState) => void;
}) {
  return (
    <div className="tr-mem-filters">
      <input className="tr-input" type="search" placeholder="Search memories…" aria-label="Search memories"
        value={value.query} onChange={(e) => onChange({ ...value, query: e.target.value })} />
      <select aria-label="Tier filter" value={value.tier} onChange={(e) => onChange({ ...value, tier: e.target.value })}>
        <option value="">All tiers</option><option value="hot">Hot</option><option value="warm">Warm</option><option value="cold">Cold</option>
      </select>
      <select aria-label="Type filter" value={value.type} onChange={(e) => onChange({ ...value, type: e.target.value })}>
        <option value="">All types</option><option value="memory">Memory</option><option value="knowledge">Knowledge</option>
      </select>
    </div>
  );
}
