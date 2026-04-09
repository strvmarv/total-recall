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

## Post-cutover follow-ups (0.8.x .NET)

Items identified during the 0.7.2 TS → 0.8.0 .NET cutover but deferred out of the beta window. Revisit once `main` is on `0.8.x` and the beta has baked cleanly.

### Checksum Verification of Downloaded Binaries

`scripts/fetch-binary.js` currently trusts TLS when fetching from GitHub Releases. Add a sidecar `total-recall-<rid>.sha256` file per RID in the release assets, attach via `release.yml`, and verify the SHA-256 in `ensureBinary()` before the rename-into-place step. Reject and delete the tmp file on mismatch; include expected vs. actual digest in the error message.

**Files:** `scripts/fetch-binary.js`, `.github/workflows/release.yml`

### Signed Releases

Beyond checksums, consider sigstore/cosign signatures or gh-release's built-in provenance so users can verify binaries were built by CI and not tampered with in transit.

**Files:** `.github/workflows/release.yml`, `scripts/fetch-binary.js`
**Depends on:** Checksum verification landing first.

### Binary Cache Survives Plugin Reinstall

`ensureBinary()` currently caches into `${CLAUDE_PLUGIN_ROOT}/binaries/<rid>/`, which gets wiped when the plugin directory is recreated (e.g., `claude /plugin update`). Consider caching into a stable shared location (e.g. `~/.total-recall/binaries/<version>/<rid>/`) and symlinking/copying into the plugin tree. Avoids re-downloading on plugin reinstall when the version hasn't changed.

**Files:** `scripts/fetch-binary.js`

### Intel Mac (osx-x64) Support

Dropped in `v0.8.0-beta.2` to get the release matrix green (the `macos-13` runner was deprecated and the legacy `darwin-x64` RID was invalid). Add back via either a `macos-13-large` / `macos-15-large` Intel runner or cross-compile from `macos-14` arm64. Only do this if demand surfaces — all Apple hardware since Nov 2020 is arm64.

**Files:** `.github/workflows/release.yml`, `scripts/fetch-binary.js`, `bin/start.js`

### npm publish --provenance

Requires adding `id-token: write` to the `publish` job's `permissions` block in `release.yml`, then passing `--provenance` to the `npm publish` step. Gives users an sigstore attestation trail. The old `publish.yml` that this repo deleted during the TS strip had this pattern; restore it on the new `release.yml`.

**Files:** `.github/workflows/release.yml`

### release.yml Dry-Run Step

Add `npm publish --dry-run` before the real publish so tarball shape regressions (missing `files` entries, unexpected sizes) are caught before the publish commits. The dry-run step can also surface the tarball content list for human review in the workflow log.

**Files:** `.github/workflows/release.yml`

### Multi-platform dotnet-ci.yml

`dotnet-ci.yml` currently only runs on `ubuntu-latest`, so Windows and macOS regressions are only caught at real `v*` tag pushes by `release.yml`. Add a matrix (ubuntu-latest, ubuntu-24.04-arm, macos-14, windows-latest) so PR review catches platform-specific breakage without burning a release tag.

**Files:** `.github/workflows/dotnet-ci.yml`

### Node.js 24 Migration in Workflows

All workflows currently use `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/setup-node@v4` which run on Node.js 20. GitHub Actions will force Node.js 24 on June 2nd, 2026 and remove Node.js 20 entirely on September 16th, 2026. Migrate when the action versions publish Node-24-compatible releases, or set `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24=true` to opt in early.

**Files:** `.github/workflows/release.yml`, `.github/workflows/dotnet-ci.yml`

### Plugin Version Single Source of Truth

Four files carry a `version` field and can drift silently: `package.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json`. Multiple drift incidents were discovered during the 0.7.2 → 0.8.0 cutover:

- `.copilot-plugin/plugin.json` was stuck at `0.1.0` across many releases — Copilot CLI users saw `0.1.0` reported even when the npm package was at `0.7.2`.
- `.claude-plugin/plugin.json` stayed stuck on `0.7.2` through the entire strip series while `package.json` advanced to `0.8.0-beta.3`.
- `.cursor-plugin/plugin.json` was stale at `0.7.2` for the same reason.

All four were synced to `0.8.0-beta.4` in the beta.4 commit, and the standing rule is documented in `AGENTS.md` § "Version sync — four files, one version".

Automate the enforcement with either:

- A pre-commit hook that treats `package.json` as authoritative and derives the other three manifests' version fields from it (auto-sync).
- A `dotnet-ci.yml` step that fails if the four versions disagree (safety-net check, no auto-fix).

The pre-commit sync eliminates human error at the source. The CI check catches it if the hook is bypassed or never installed. Ideally both.

**Files:** `package.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json`, `git-hooks/pre-commit` (new), `.github/workflows/dotnet-ci.yml`

### CHANGELOG.md Backfill: 0.6.8 GA, 0.7.0, 0.7.1, 0.7.2

`CHANGELOG.md` has a gap between `0.6.8-beta.5` (documented 2026-04-06) and `0.8.0-beta.1` (documented 2026-04-08). Four TypeScript releases shipped to npm in between without changelog entries:

- `0.6.8` GA (post-beta.5)
- `0.7.0`
- `0.7.1`
- `0.7.2` (current `@latest` on npm)

Backfill by reading `git log v0.6.8-beta.5..v0.7.2 --reverse` and grouping commits by tag. These are all pre-cutover TS commits — the CHANGELOG entries should describe what those releases changed, not be written from scratch. Archaeological work, not urgent, but worth doing before `0.8.0` GA so users looking at the full version history have continuity.

**Files:** `CHANGELOG.md`

### Comprehensive AGENTS.md Rewrite

`AGENTS.md` is a leftover from the TypeScript era and most of its content is stale post-cutover. Known wrong sections (verified 2026-04-09):

- "dist/ is committed" — false, `dist/` was deleted in commit `73ec297`.
- "A pre-commit hook in `git-hooks/pre-commit` auto-rebuilds and stages `dist/`" — stale, no more build step.
- "CI will fail if `dist/` is stale after a clean build" — false, the check was in `ci.yml` which was deleted in commit `7a8c437`.
- "`.mcp.json` uses the bash launcher" — false, it uses `node bin/start.js` now.
- "The launcher resolves the runtime in this order: 1. `~/.total-recall/bun/<version>/bun`..." — entirely stale, bun is gone.
- "The entry point is always `dist/index.js` relative to the script" — false, `dist/` doesn't exist.
- "Bundled Bun" section — entirely stale.
- "Since 0.6.8, `dist/index.js` uses `bun:sqlite` and cannot run under node..." — irrelevant to the .NET binary.
- "Database Migrations... `src/db/schema.ts`" — wrong path, now `src/TotalRecall.Infrastructure/Storage/Schema.cs` and the migration framework lives in `MigrationRunner`.
- The "Release flow" section was surgically fixed in the beta.4 commit but the rest of the file remains stale.

Do a full audit and rewrite — delete bun, dist, TS-specific runtime sections; replace with .NET AOT, sqlite-vec native CDN flow, `release.yml` matrix, and the new `scripts/fetch-binary.js` install model. Keep only sections that still describe current behavior (session lifecycle, eval system, ToolContext — review these too, some may reference TS paths).

**Files:** `AGENTS.md`

### Scrub Stale TS References in .NET Source Comments

89 files under `src/TotalRecall.*/` carry comments like `// ported from src-ts/embedding/tokenizer.ts` or `// mirrors src-ts/db/entries.ts`. These are historical documentation that now point at paths deleted in commit `87975a7`. Not broken, just stale. Replace with a shorter note ("ported from the original TypeScript implementation — see git history before 87975a7 for archaeology") or delete the lineage references entirely.

**Files:** 89 `.cs` / `.fs` files across `src/TotalRecall.*/`.

### Scrub bun / TypeScript References in Top-Level Docs

`README.md`, `INSTALL.md`, `AGENTS.md`, `CHANGELOG.md` all reference bun (the TS runtime) and the TypeScript implementation details that no longer apply. Update to describe the .NET AOT binary distribution model, remove bun install instructions, and describe the current install paths: `npm install @strvmarv/total-recall` or `claude /plugin update` from the marketplace.

**Files:** `README.md`, `INSTALL.md`, `AGENTS.md`, `CHANGELOG.md`

### Scrub bun:sqlite Comment in SqliteConnection.cs

`src/TotalRecall.Infrastructure/Storage/SqliteConnection.cs:13–15` references `src-ts/db/connection.ts` and its `bun:sqlite` usage as a porting crib. Replace with the `Microsoft.Data.Sqlite` rationale or just delete the comment.

**Files:** `src/TotalRecall.Infrastructure/Storage/SqliteConnection.cs`
