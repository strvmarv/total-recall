export interface ConfigField { key: string; label: string; type: 'int' | 'float' | 'bool' | 'string'; min?: number; max?: number; }
export interface ConfigSectionDef { title: string; fields: ConfigField[]; }

export const EDITABLE_SECTIONS: ConfigSectionDef[] = [
  { title: 'Hot tier', fields: [
    { key: 'tiers.hot.max_entries', label: 'Max entries', type: 'int', min: 1 },
    { key: 'tiers.hot.token_budget', label: 'Token budget', type: 'int', min: 1 },
    { key: 'tiers.hot.carry_forward_threshold', label: 'Carry-forward threshold', type: 'float', min: 0, max: 1 },
  ] },
  { title: 'Warm tier', fields: [
    { key: 'tiers.warm.max_entries', label: 'Max entries', type: 'int', min: 1 },
    { key: 'tiers.warm.retrieval_top_k', label: 'Retrieval top-K', type: 'int', min: 1, max: 100 },
    { key: 'tiers.warm.similarity_threshold', label: 'Similarity threshold', type: 'float', min: 0, max: 1 },
    { key: 'tiers.warm.cold_decay_days', label: 'Cold decay (days)', type: 'int', min: 1 },
  ] },
  { title: 'Cold tier', fields: [
    { key: 'tiers.cold.chunk_max_tokens', label: 'Chunk max tokens', type: 'int', min: 1 },
    { key: 'tiers.cold.chunk_overlap_tokens', label: 'Chunk overlap tokens', type: 'int', min: 0 },
    { key: 'tiers.cold.lazy_summary_threshold', label: 'Lazy summary threshold', type: 'int', min: 1 },
  ] },
  { title: 'Pinned tier', fields: [
    { key: 'tiers.pinned.max_content_chars', label: 'Max content chars', type: 'int', min: 1 },
    { key: 'tiers.pinned.floor_enabled', label: 'Floor enabled', type: 'bool' },
    { key: 'tiers.pinned.floor_every_n_turns', label: 'Floor every N turns', type: 'int', min: 1 },
    { key: 'tiers.pinned.floor_growth_tokens', label: 'Floor growth tokens', type: 'int', min: 0 },
  ] },
  { title: 'Compaction', fields: [
    { key: 'compaction.decay_half_life_hours', label: 'Decay half-life (hours)', type: 'float', min: 1 },
    { key: 'compaction.warm_threshold', label: 'Warm threshold', type: 'float', min: 0, max: 1 },
    { key: 'compaction.promote_threshold', label: 'Promote threshold', type: 'float', min: 0, max: 1 },
    { key: 'compaction.warm_sweep_interval_days', label: 'Warm sweep interval (days)', type: 'int', min: 1 },
    { key: 'compaction.auto_demote_min_injections', label: 'Auto-demote min injections', type: 'int', min: 1 },
  ] },
  { title: 'Search', fields: [
    { key: 'search.fts_weight', label: 'FTS weight', type: 'float', min: 0, max: 1 },
  ] },
  { title: 'Regression alerts', fields: [
    { key: 'regression.miss_rate_delta', label: 'Miss-rate delta', type: 'float', min: 0, max: 1 },
    { key: 'regression.latency_ratio', label: 'Latency ratio', type: 'float', min: 1 },
    { key: 'regression.min_events', label: 'Min events', type: 'int', min: 1 },
  ] },
  { title: 'Tool cache', fields: [
    { key: 'tool_cache.max_entries', label: 'Max entries', type: 'int', min: 1 },
    { key: 'tool_cache.default_ttl_seconds', label: 'Default TTL (seconds)', type: 'int', min: 1 },
  ] },
  { title: 'Scope', fields: [
    { key: 'scope.default', label: 'Default scope', type: 'string' },
  ] },
];

// Shown read-only — changing these can break the running instance.
export const READONLY_KEYS: { title: string; keys: string[] }[] = [
  { title: 'Embedding (read-only)', keys: ['embedding.model', 'embedding.dimensions', 'embedding.provider'] },
  { title: 'Storage (read-only)', keys: ['storage.mode', 'storage.connection_string'] },
];

export function getByPath(obj: unknown, path: string): unknown {
  return path.split('.').reduce<unknown>((acc, k) => (acc && typeof acc === 'object' ? (acc as Record<string, unknown>)[k] : undefined), obj);
}

export type FieldValue = number | boolean | string;
export function validateField(field: ConfigField, raw: string | boolean): { value: FieldValue } | { error: string } {
  if (field.type === 'bool') return { value: Boolean(raw) };
  if (field.type === 'string') return { value: String(raw) };
  if (raw === '' ) return { error: 'Enter a value.' };
  const num = Number(raw);
  if (Number.isNaN(num)) return { error: 'Enter a number.' };
  if (field.type === 'int' && !Number.isInteger(num)) return { error: 'Must be a whole number.' };
  if (field.min !== undefined && num < field.min) return { error: `Must be ≥ ${field.min}.` };
  if (field.max !== undefined && num > field.max) return { error: `Must be ≤ ${field.max}.` };
  return { value: num };
}
