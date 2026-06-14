// Typed shapes for the tool-dispatch JSON the Dashboard consumes.
// Field names mirror the server DTOs exactly (snake_case where the tool emits it).

export interface StatusResult {
  tierSizes: {
    hot_memories: number; hot_knowledge: number;
    warm_memories: number; warm_knowledge: number;
    cold_memories: number; cold_knowledge: number;
    pinned_memories: number; pinned_knowledge: number;
  };
  knowledgeBase: { collections: { id: string; name: string | null }[]; totalChunks: number };
  db: { path: string; sizeBytes: number | null; sessionId: string };
  embedding: { model: string; dimensions: number };
}

export interface UsageBucket {
  key: string;
  session_count: number;
  turn_count: number;
  input_tokens: number | null;
  cache_creation_tokens: number | null;
  cache_read_tokens: number | null;
  output_tokens: number | null;
}
export interface UsageResult {
  query: { start_ms: number; end_ms: number; group_by: string };
  buckets: UsageBucket[];
  grand_total: Omit<UsageBucket, 'key'>;
  coverage: { sessions_with_full_token_data: number; sessions_with_partial_token_data: number; fidelity_percent: number };
}

export interface EvalReport {
  precision: number;
  hitRate: number;
  missRate: number;
  mrr: number;
  avgLatencyMs: number;
  totalEvents: number;
}

export interface CompactionMovement {
  id: string;
  timestamp: number;
  session_id: string | null;
  source_tier: string;
  target_tier: string | null;
  source_entry_ids: string[];
  target_entry_id: string | null;
  reason: string;
}
export interface MemoryHistoryResult { movements: CompactionMovement[]; count: number; }

export interface MemoryListEntry {
  id: string; tier: string; content_type: string; content: string;
  summary: string | null; project: string | null; tags: string[];
  created_at: number; updated_at: number; scope: string;
}
export interface MemoryListResult { entries: MemoryListEntry[]; count: number; total: number; limit: number; offset: number; }

export interface MemoryRecentEntry {
  id: string; tier: string; entry_type: string; project: string | null;
  created_at: number; updated_at: number; last_accessed_at: number; preview: string;
}
export interface MemoryRecentResult { entries: MemoryRecentEntry[]; count: number; order: string; }
