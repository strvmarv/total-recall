# Deferred Items

Features planned in the design spec but not yet implemented.

## Completed

### ~~Benchmark Corpus Expansion~~

Expanded corpus from 20 to 100 entries across 6 categories. Queries expanded from 20 to 139 including synonym, partial match, and negative assertion queries. Added `expected_absent` support to benchmark runner.

### ~~Evolving Benchmarks (--grow)~~

Retrieval misses from `eval_report` auto-captured as candidates in `benchmark_candidates` table. `eval_grow` tool supports list mode (pending candidates sorted by frequency) and resolve mode (accept/reject, with accepted entries appended to `retrieval.jsonl`).

### ~~A/B Config Comparison~~

Config snapshots auto-created on `session_start` and before `config_set`. `eval_compare` tool provides side-by-side metrics with per-tier/per-content-type breakdowns and query-level diff. `eval_snapshot` tool for manual snapshots.

### ~~Regression Detection~~

Compares retrieval metrics between config snapshots at session_start. Alerts when miss rate increases by ≥ `regression.miss_rate_delta` or latency increases by ≥ `regression.latency_ratio`. Configurable thresholds with sensible defaults. Skipped when insufficient data.

### ~~Additional Host Importers~~

Added Cursor, Cline, OpenCode, and Hermes importers. Extracted shared utilities to `import-utils.ts`. Cursor imports project rules (`.cursorrules`, `.cursor/rules/*.mdc`) and global rules from SQLite. Cline imports global rules and task summaries. OpenCode imports `AGENTS.md` files and custom agents/commands. Hermes imports `§`-delimited memory entries, skills, and SOUL.md.

### ~~First-Run Smoke Test~~

Version-gated smoke test runs on first install and after upgrades. 22-query corpus in `eval/benchmarks/smoke.jsonl` exercises semantic similarity, tier routing, and negative assertions. Results surfaced in `session_start` response as `smokeTest` field. Version tracked in `_meta` table.

### ~~Post-Ingest Validation Queries~~

Replaced single-chunk identity check with 3-probe validation. Probes sample chunks at 0%, 33%, and 66% through the document via scoped vector search. `IngestFileResult` now includes per-probe details. `IngestDirectoryResult` tracks per-file validation failures.

### ~~Config Snapshot Auto-Creation~~

Snapshots auto-created on `session_start` and before `config_set`. Deduplication via SHA-256 hash of recursively key-sorted config JSON. Snapshot IDs threaded through retrieval events and compaction logging.

### ~~Benchmark Use Cases~~

CI smoke test added — runs 22-query smoke benchmark with real ONNX embedder after build, fails the pipeline if exact match rate drops below 80%. Entry point: `src/eval/ci-smoke.ts`, built as second tsup entry to `dist/eval/ci-smoke.js`, invoked via `npm run benchmark`. First-install validation covered by existing `runSmokeTest()` at session_start. Config tuning covered by `eval_compare` tool.

## Backlog

Items below are not urgent. Revisit when real-world usage surfaces a need.

### True MRR Computation

Replace simplified MRR (binary 1.0 if top result used, 0 otherwise) with rank-aware reciprocal rank. Current approach in `computeMetrics()` doesn't consider which result in the set was actually used.

**Files:** `src/eval/metrics.ts`, `src/eval/event-logger.ts`
**Blocked by:** Per-result outcome tracking — needs `outcome_rank` column in `retrieval_events` schema and updated `updateOutcome()` to accept rank. Also depends on reliable rank reporting from host LLMs.

### Compaction Health Fields

Populate `semantic_drift`, `facts_preserved`, `facts_in_original`, `preservation_ratio` in `compaction_log`. Schema columns exist but are always NULL.

**Files:** `src/compaction/compactor.ts`, `src/search/vector-search.ts`
**Blocked by:** Content merging during compaction — fields are trivially 1.0 until entries are actually merged/summarized.

### Automatic Outcome Detection

Auto-detect user corrections, preferences, and acknowledgments in conversation flow to populate `outcome_signal` on retrieval events. Currently requires manual `updateOutcome()` calls.

**Files:** `src/eval/event-logger.ts`, skill instructions
**Complexity:** High — requires conversation pattern analysis or LLM classification of user responses relative to retrieved context.

### PreToolUse Hook (Optional Fallback)

Belt-and-suspenders backup for session initialization. A PreToolUse hook that fires once per session (using a `/tmp` marker keyed on `session_id`) to inject a reminder to call `session_start` if the model hasn't already.

**Files:** `hooks/hooks.json`, new `hooks/pre-tool-use/run.sh`
**Depends on:** Evidence that the current three-layer approach (server-side init, hook directive, `/using-total-recall` skill) is still unreliable.

### PDF Parsing

Not supported in the chunker. Would need a PDF-to-text library.

**Files:** `src/ingestion/chunker.ts`

### Code Parser AST Analysis

Current code parser uses basic regex splitting for function/class boundaries. Full AST parsing (via TypeScript compiler API, tree-sitter, etc.) would produce more accurate chunks.

**Files:** `src/ingestion/code-parser.ts`
