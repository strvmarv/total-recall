export type Tier = "hot" | "warm" | "cold";
export type ContentType = "memory" | "knowledge";
export type EntryType =
  | "correction"
  | "preference"
  | "decision"
  | "surfaced"
  | "imported"
  | "compacted"
  | "ingested";
export type QuerySource = "auto" | "explicit" | "session_start" | "mcp_tool";
export type OutcomeSignal = "positive" | "negative" | "neutral";
export type SourceTool = "claude-code" | "copilot-cli" | "opencode" | "manual";

export interface Entry {
  id: string;
  content: string;
  summary: string | null;
  source: string | null;
  source_tool: SourceTool | null;
  project: string | null;
  tags: string[];
  created_at: number;
  updated_at: number;
  last_accessed_at: number;
  access_count: number;
  decay_score: number;
  parent_id: string | null;
  collection_id: string | null;
  metadata: Record<string, unknown>;
}

export interface EntryRow {
  id: string;
  content: string;
  summary: string | null;
  source: string | null;
  source_tool: string | null;
  project: string | null;
  tags: string | null;
  created_at: number;
  updated_at: number;
  last_accessed_at: number;
  access_count: number;
  decay_score: number;
  parent_id: string | null;
  collection_id: string | null;
  metadata: string | null;
}

export interface SearchResult {
  entry: Entry;
  tier: Tier;
  content_type: ContentType;
  score: number;
  rank: number;
}

export interface RetrievalEventRow {
  id: string;
  timestamp: number;
  session_id: string;
  query_text: string;
  query_source: QuerySource;
  query_embedding: Buffer | null;
  results: string;
  result_count: number;
  top_score: number | null;
  top_tier: string | null;
  top_content_type: string | null;
  outcome_used: number | null;
  outcome_signal: string | null;
  config_snapshot_id: string;
  latency_ms: number | null;
  tiers_searched: string;
  total_candidates_scanned: number | null;
}

export interface CompactionLogRow {
  id: string;
  timestamp: number;
  session_id: string | null;
  source_tier: string;
  target_tier: string | null;
  source_entry_ids: string;
  target_entry_id: string | null;
  semantic_drift: number | null;
  facts_preserved: number | null;
  facts_in_original: number | null;
  preservation_ratio: number | null;
  decay_scores: string;
  reason: string;
  config_snapshot_id: string;
}

export interface ConfigSnapshot {
  id: string;
  name: string | null;
  timestamp: number;
  config: string;
}

export interface ImportLogRow {
  id: string;
  timestamp: number;
  source_tool: string;
  source_path: string;
  content_hash: string;
  target_entry_id: string;
  target_tier: string;
  target_type: string;
}

export interface TotalRecallConfig {
  tiers: {
    hot: {
      max_entries: number;
      token_budget: number;
      carry_forward_threshold: number;
    };
    warm: {
      max_entries: number;
      retrieval_top_k: number;
      similarity_threshold: number;
      cold_decay_days: number;
    };
    cold: {
      chunk_max_tokens: number;
      chunk_overlap_tokens: number;
      lazy_summary_threshold: number;
    };
  };
  compaction: {
    decay_half_life_hours: number;
    warm_threshold: number;
    promote_threshold: number;
    warm_sweep_interval_days: number;
  };
  embedding: {
    model: string;
    dimensions: number;
  };
  regression?: {
    miss_rate_delta?: number;
    latency_ratio?: number;
    min_events?: number;
  };
  search?: {
    fts_weight?: number;
  };
}

export function tableName(tier: Tier, type: ContentType): string {
  const typeStr = type === "memory" ? "memories" : "knowledge";
  return `${tier}_${typeStr}`;
}

export function vecTableName(tier: Tier, type: ContentType): string {
  return `${tableName(tier, type)}_vec`;
}

export function ftsTableName(tier: Tier, type: ContentType): string {
  return `${tableName(tier, type)}_fts`;
}

export const ALL_TABLE_PAIRS: Array<{ tier: Tier; type: ContentType }> = [
  { tier: "hot", type: "memory" },
  { tier: "hot", type: "knowledge" },
  { tier: "warm", type: "memory" },
  { tier: "warm", type: "knowledge" },
  { tier: "cold", type: "memory" },
  { tier: "cold", type: "knowledge" },
];
