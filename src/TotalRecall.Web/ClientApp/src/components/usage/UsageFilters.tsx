export interface UsageFilterState { window: string; host: string; project: string; }

export function UsageFilters({ value, onChange }: { value: UsageFilterState; onChange: (n: UsageFilterState) => void }) {
  return (
    <div className="tr-usage-filters">
      <select aria-label="Time window" value={value.window} onChange={(e) => onChange({ ...value, window: e.target.value })}>
        <option value="7d">Last 7 days</option>
        <option value="30d">Last 30 days</option>
        <option value="90d">Last 90 days</option>
        <option value="all">All time</option>
      </select>
      <input className="tr-input" placeholder="Host (e.g. claude-code)" aria-label="Host filter" value={value.host} onChange={(e) => onChange({ ...value, host: e.target.value })} />
      <input className="tr-input" placeholder="Project" aria-label="Project filter" value={value.project} onChange={(e) => onChange({ ...value, project: e.target.value })} />
    </div>
  );
}

/** Build the usage_status args from filters (host/project omitted when blank). */
export function usageArgs(f: UsageFilterState, groupBy: string, extra?: Record<string, unknown>) {
  return { window: f.window, group_by: groupBy, host: f.host.trim() || undefined, project: f.project.trim() || undefined, ...extra };
}
