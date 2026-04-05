# Deferred Items

Features planned in the design spec but not yet implemented. Ordered by impact.

## Compaction Health Fields

Populate `semantic_drift`, `facts_preserved`, `facts_in_original`, `preservation_ratio` in `compaction_log`. Schema columns exist but are always NULL.

**Files:** `src/compaction/compactor.ts`, `src/search/vector-search.ts`
**Blocked by:** Content merging during compaction — fields are trivially 1.0 until entries are actually merged/summarized. Need `getEmbedding()` in vector-search.ts to compute drift.

## True MRR Computation

Replace simplified MRR (binary 1.0 if top result used, 0 otherwise) with rank-aware reciprocal rank. Current approach in `computeMetrics()` doesn't consider which result in the set was actually used.

**Files:** `src/eval/metrics.ts`, `src/eval/event-logger.ts`
**Blocked by:** Per-result outcome tracking — needs `outcome_rank` column in `retrieval_events` schema and updated `updateOutcome()` to accept rank.

## Automatic Outcome Detection

Auto-detect user corrections, preferences, and acknowledgments in conversation flow to populate `outcome_signal` on retrieval events. Currently requires manual `updateOutcome()` calls.

**Files:** `src/eval/event-logger.ts`, skill instructions
**Complexity:** High — requires conversation pattern analysis or LLM classification of user responses relative to retrieved context.

## ~~Benchmark Corpus Expansion~~ [DONE]

Expanded corpus from 20 to 100 entries across 6 categories. Queries expanded from 20 to 139 including synonym, partial match, and negative assertion queries. Added `expected_absent` support to benchmark runner.

## Evolving Benchmarks (--grow)

Harvest real retrieval misses into the benchmark suite. When a search returns no useful results, log the query as a candidate benchmark entry. `/total-recall eval --grow` would review candidates and add confirmed ones to `retrieval.jsonl`.

**Files:** `src/eval/benchmark-runner.ts`, `src/tools/eval-tools.ts`

## ~~A/B Config Comparison~~ [DONE]

Config snapshots auto-created on `session_start` and before `config_set`. `eval_compare` tool provides side-by-side metrics with per-tier/per-content-type breakdowns and query-level diff. `eval_snapshot` tool for manual snapshots.

## Regression Detection

Alert when retrieval metrics drop below thresholds or trend downward. Surface warnings in `/total-recall status` dashboard.

**Files:** `src/tools/system-tools.ts`, `src/eval/metrics.ts`
**Depends on:** Enough retrieval event history to compute meaningful trends.

## PreToolUse Hook (Optional Fallback)

Belt-and-suspenders backup for session initialization. A PreToolUse hook that fires once per session (using a `/tmp` marker keyed on `session_id`) to inject a reminder to call `session_start` if the model hasn't already. Only needed if the server-side `oninitialized` + lazy-init and hook directive prove insufficient.

**Files:** `hooks/hooks.json`, new `hooks/pre-tool-use/run.sh`
**Depends on:** Evidence that the current three-layer approach (server-side init, hook directive, `/using-total-recall` skill) is still unreliable.

## ~~Additional Host Importers~~ [DONE]

Added Cursor, Cline, OpenCode, and Hermes importers. Extracted shared utilities to `import-utils.ts`. Cursor imports project rules (`.cursorrules`, `.cursor/rules/*.mdc`) and global rules from SQLite. Cline imports global rules and task summaries. OpenCode imports `AGENTS.md` files and custom agents/commands. Hermes imports `§`-delimited memory entries, skills, and SOUL.md.

## PDF Parsing

Not supported in the chunker. Would need a PDF-to-text library.

**Files:** `src/ingestion/chunker.ts`

## First-Run Smoke Test

Benchmark runner exists but isn't integrated into startup. Design spec called for a <2s smoke test with 20 queries on first run.

**Files:** `src/tools/session-tools.ts`, `src/eval/benchmark-runner.ts`

## Post-Ingest Validation Queries

After ingestion, auto-generate 3 test queries to verify chunks are retrievable. Report validation pass/fail in the ingest response.

**Files:** `src/ingestion/ingest.ts`, `src/tools/kb-tools.ts`

## ~~Config Snapshot Auto-Creation~~ [DONE]

Snapshots auto-created on `session_start` and before `config_set`. Deduplication via SHA-256 hash of recursively key-sorted config JSON. Snapshot IDs threaded through retrieval events and compaction logging.

## Code Parser AST Analysis

Current code parser uses basic regex splitting for function/class boundaries. Full AST parsing (via TypeScript compiler API, tree-sitter, etc.) would produce more accurate chunks.

**Files:** `src/ingestion/code-parser.ts`

## Benchmark Use Cases

Only manual benchmark runner exists. Design spec described three use cases: CI smoke test (run on every build), first-install validation (verify system works on setup), and config tuning (compare metrics before/after config changes).

**Files:** `src/eval/benchmark-runner.ts`, `.github/workflows/ci.yml`
