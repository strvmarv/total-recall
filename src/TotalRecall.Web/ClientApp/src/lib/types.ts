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
  // Rich fields surfaced on the Eval page (RetrievalQualityCard only reads the
  // top-line subset above, so these additions are safe). All emitted camelCase.
  byTier?: Record<string, { precision: number; hitRate: number; avgScore: number; count: number }>;
  byContentType?: Record<string, { precision: number; hitRate: number; count: number }>;
  topMisses?: { query: string; topScore: number | null; timestamp: number }[];
  falsePositives?: { query: string; topScore: number | null; timestamp: number }[];
  compactionHealth?: { totalCompactions: number; avgPreservationRatio: number | null; entriesWithDrift: number };
}

export interface EvalBenchmarkDetail {
  query: string;
  expectedContains: string;
  topResult: string | null;
  topScore: number;
  matched: boolean;
  fuzzyMatched: boolean;
  hasNegativeAssertion: boolean;
  negativePass: boolean;
}
export interface EvalBenchmarkResult {
  totalQueries: number;
  exactMatchRate: number;
  fuzzyMatchRate: number;
  tierRoutingRate: number;
  negativePassRate: number;
  avgLatencyMs: number;
  details: EvalBenchmarkDetail[];
}

export interface EvalCompareChange {
  queryText: string;
  beforeOutcome: string;
  afterOutcome: string;
  beforeScore: number | null;
  afterScore: number | null;
}
export interface EvalCompareResult {
  beforeId: string;
  afterId: string;
  deltas: { precision: number; hitRate: number; mrr: number; missRate: number; avgLatencyMs: number };
  regressions: EvalCompareChange[];
  improvements: EvalCompareChange[];
  warning: string | null;
}

export interface EvalGrowCandidate {
  id: string;
  queryText: string;
  topScore: number;
  topResultContent: string | null;
  topResultEntryId: string | null;
  firstSeen: number;
  lastSeen: number;
  timesSeen: number;
  status: string;
}
export interface EvalGrowListResult { action: string; candidates: EvalGrowCandidate[]; count: number; }
export interface EvalGrowResolveResult { action: string; accepted: number; rejected: number; corpusEntries: string[]; benchmarkPath: string; }

export interface EvalSnapshotResult { id: string; name: string; deduped: boolean; }

// ── insights tool (entry-level analysis) ──────────────────────────────────────
// Mirrors the server InsightsResultDto (all camelCase as emitted).
export interface InsightsHealthComponent { score: number; max: number; detail: string; }
export interface InsightsHealthBreakdown {
  retrieval: InsightsHealthComponent;
  capture: InsightsHealthComponent;
  pinned: InsightsHealthComponent;
  kb: InsightsHealthComponent;
}
export interface InsightsNearDupMember { id: string; tier: string; preview: string; score: number; createdAt: number; }
export interface InsightsNearDupGroup { groupId: string; topScore: number; members: InsightsNearDupMember[]; }
export interface InsightsPinCandidate { id: string; tier: string; preview: string; accessCount: number; }
export interface InsightsRetrievalGap { query: string; timesSeen: number; topScore: number | null; }
export interface InsightsThresholdPoint { threshold: number; hitRate: number; precision: number; mrr: number; }
export interface InsightsThresholdCurve { current: number; points: InsightsThresholdPoint[]; }
export interface InsightsResult {
  healthScore: number;
  healthBreakdown: InsightsHealthBreakdown;
  nearDuplicates: InsightsNearDupGroup[];
  pinCandidates: InsightsPinCandidate[];
  retrievalGaps: InsightsRetrievalGap[];
  thresholdCurve: InsightsThresholdCurve;
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
  summary: string | null; source_tool: string | null; project: string | null; tags: string[];
  created_at: number; updated_at: number; scope: string;
}
export interface MemoryListResult { entries: MemoryListEntry[]; count: number; total: number; limit: number; offset: number; }

export interface MemoryRecentEntry {
  id: string; tier: string; entry_type: string; project: string | null;
  created_at: number; updated_at: number; last_accessed_at: number; preview: string;
}
export interface MemoryRecentResult { entries: MemoryRecentEntry[]; count: number; order: string; }

export interface EntryDto {
  id: string; content: string; summary: string | null; source: string | null;
  project: string | null; tags: string[]; created_at: number; updated_at: number;
  last_accessed_at: number; access_count: number; decay_score: number; scope: string;
}
export interface MemorySearchHit { entry: EntryDto; score: number; tier: string; content_type: string; rank: number; }
export interface MemorySearchResult { retrievalId: string; results: MemorySearchHit[]; }

export interface MemoryInspectResult {
  id: string; tier: string; content_type: string; content: string; summary: string | null;
  source: string | null; source_tool: string | null; project: string | null; tags: string[];
  created_at: number; updated_at: number; last_accessed_at: number; access_count: number;
  decay_score: number; parent_id: string | null; collection_id: string | null;
  metadata: string; compaction_history: CompactionMovement | null;
}
export interface LineageNode {
  id: string; compaction_log_id: string | null; reason: string | null; timestamp: number | null;
  source_tier: string | null; target_tier: string | null; sources: LineageNode[] | null;
}
export interface MemoryMoveResult { id: string; from_tier: string; from_content_type: string; to_tier: string; to_content_type: string; success: boolean; }
export interface MemoryUpdateResult { updated: boolean; }
export interface MemoryDeleteResult { deleted: boolean; }

export interface KbCollection {
  id: string; name: string; document_count: number; chunk_count: number;
  created_at: number; summary: string | null; source_path: string | null;
}
export interface KbListCollectionsResult { collections: KbCollection[]; count: number; }
export interface KbIngestFileResult { document_id: string; chunk_count: number; validation_passed: boolean; }
export interface KbIngestDirResult { collection_id: string; document_count: number; total_chunks: number; errors: string[]; validation_passed: boolean; validation_failures: string[]; }
export interface KbRefreshResult { collection_id: string; files: number; chunks: number; refreshed: boolean; }
export interface KbRemoveResult { id: string; removed: boolean; cascaded_count: number; }
export interface KbSearchResult { retrievalId: string; results: MemorySearchHit[]; hierarchicalMatch: unknown; needsSummary: boolean; }
