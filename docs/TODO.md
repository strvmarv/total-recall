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

### ~~Cortex Connection~~

Hybrid local+remote mode connecting the plugin to Total Recall Cortex. Local SQLite for user memories with bidirectional sync to Cortex API, remote queries for global KB. Async ingest on Cortex side via staging table + Hangfire. Content-only sync (no vectors) — each side re-embeds independently (ONNX 384-dim vs. Cohere v4 1024-dim). Implemented across 0.9.1-0.9.4.

## Backlog

Items below are not urgent. Revisit when real-world usage surfaces a need.

### True MRR Computation

Replace simplified MRR (binary 1.0 if top result used, 0 otherwise) with rank-aware reciprocal rank. Current approach in `Metrics.cs` / `ComparisonMetrics.cs` doesn't consider which result in the set was actually used.

**Files:** `src/TotalRecall.Infrastructure/Eval/Metrics.cs`, `src/TotalRecall.Infrastructure/Telemetry/RetrievalEventLog.cs`
**Blocked by:** Per-result outcome tracking — needs `outcome_rank` column in `retrieval_events` schema and an updated `UpdateOutcome()` signature to accept rank. Also depends on reliable rank reporting from host LLMs.

### Compaction Health Fields

Populate `semantic_drift`, `facts_preserved`, `facts_in_original`, `preservation_ratio` in `compaction_log`. Schema columns exist but are always NULL.

**Files:** `src/TotalRecall.Core/Compaction.fs` (decision logic), `src/TotalRecall.Infrastructure/Search/VectorSearch.cs` (drift measurement), `src/TotalRecall.Infrastructure/Telemetry/CompactionLog.cs` (persistence)
**Blocked by:** Content merging during compaction — fields are trivially 1.0 until entries are actually merged/summarized. Note that compaction is now host-orchestrated via the `session_end` flow (host subagent calls `memory_store` / `memory_delete` / `memory_promote` / `memory_demote`), so the "merging" that would populate these fields happens in the host's LLM judgment step, not in the .NET server.

### Automatic Outcome Detection

Auto-detect user corrections, preferences, and acknowledgments in conversation flow to populate `outcome_signal` on retrieval events. Currently requires manual outcome updates from the host tool.

**Files:** `src/TotalRecall.Infrastructure/Telemetry/RetrievalEventLog.cs`, skill instructions in `skills/commands/SKILL.md`
**Complexity:** High — requires conversation pattern analysis or LLM classification of user responses relative to retrieved context. Per the self-contained-binary principle, LLM judgment for this would live in the host tool's plugin layer, not in the .NET server.

### PreToolUse Hook (Optional Fallback)

Belt-and-suspenders backup for session initialization. A PreToolUse hook that fires once per session (using a `/tmp` marker keyed on `session_id`) to inject a reminder to call `session_start` if the model hasn't already.

**Files:** `hooks/hooks.json`, new `hooks/pre-tool-use/run.sh`
**Depends on:** Evidence that the current three-layer approach (server-side init, hook directive, `/using-total-recall` skill) is still unreliable. No failure reports from the cutover betas suggest it's needed right now.

### PDF Parsing

Not supported in the chunker. Would need a PDF-to-text library callable from F#/C# — e.g., `PdfPig` (AOT-compatible? needs verification) or shell out to `pdftotext` at build/runtime.

**Files:** `src/TotalRecall.Core/Chunker.fs`, `src/TotalRecall.Core/Parsers.fs`, possibly a new `src/TotalRecall.Infrastructure/Ingestion/PdfExtractor.cs` since PDF parsing needs I/O (not pure).

### Code Parser AST Analysis

Current code parser in `Parsers.fs` uses regex splitting for function/class boundaries. Full AST parsing (via Roslyn for C#, FSharp.Compiler.Service for F#, tree-sitter for other languages) would produce more accurate chunks at language-aware boundaries.

**Files:** `src/TotalRecall.Core/Parsers.fs`
**Complexity:** Per-language. Roslyn is the cleanest option for C# and the .NET team maintains an AOT-compatible subset. Tree-sitter bindings for .NET exist (`TreeSitter.Net`) but their AOT story is unverified. Regex is the pragmatic baseline.

### Reranker (Cortex-Side)

Cross-encoder or Cohere Rerank API pass after pgvector retrieval on the Cortex side, improving KB search quality for plugin queries. Plugin-side reranking (bundled ONNX cross-encoder) is a separate future consideration. Address when retrieval eval metrics show top-K precision is a bottleneck.

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

**Five** version locations can drift silently:

1. `package.json` (`version` field)
2. `package-lock.json` (top-level `version` field AND `packages[""].version` — npm keeps both in sync; the safest edit is a `replace_all` of the old version string)
3. `.claude-plugin/plugin.json`
4. `.copilot-plugin/plugin.json`
5. `.cursor-plugin/plugin.json`

Multiple drift incidents were discovered during the 0.7.2 → 0.8.0 cutover:

- `.copilot-plugin/plugin.json` was stuck at `0.1.0` across many releases — Copilot CLI users saw `0.1.0` reported even when the npm package was at `0.7.2`.
- `.claude-plugin/plugin.json` stayed stuck on `0.7.2` through the entire strip series while `package.json` advanced to `0.8.0-beta.3`.
- `.cursor-plugin/plugin.json` was stale at `0.7.2` for the same reason.
- `package-lock.json`'s top-level `version` field was drifting independently of `package.json` through beta.4 until the Mac agent's beta.6 lockfile regeneration synced them.

All five were synced to `0.8.0-beta.4` in the beta.4 commit, and the standing rule is documented in `AGENTS.md` § "Version sync — five files, one version". The AGENTS.md entry was bumped from "four" to "five" in the 2026-04-09 doc audit when the build agent noticed `package-lock.json` had been drifting.

Automate the enforcement with either:

- A pre-commit hook that treats `package.json` as authoritative and derives the other manifests' version fields from it (auto-sync). Note `package-lock.json` is tricky to edit safely in a hook — consider running `npm install --package-lock-only --force --os=darwin --cpu=arm64` after the sync to regenerate the lockfile cross-platform (the `--force --os/--cpu` flags are required on npm 11+ to resolve all optional-dep RID variants; a plain `npm install` without them regenerates a single-platform lockfile and breaks every non-host CI matrix leg — this is exactly how beta.1 shipped a broken lockfile that didn't get caught until beta.6).
- A `dotnet-ci.yml` step that fails if the five versions disagree (safety-net check, no auto-fix).

The pre-commit sync eliminates human error at the source. The CI check catches it if the hook is bypassed or never installed. Ideally both.

**Files:** `package.json`, `package-lock.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json`, `git-hooks/pre-commit` (new), `.github/workflows/dotnet-ci.yml`

### Scrub Stale TS References in .NET Source Comments

89 files under `src/TotalRecall.*/` carry comments like `// ported from src-ts/embedding/tokenizer.ts` or `// mirrors src-ts/db/entries.ts`. These are historical documentation that now point at paths deleted in commit `87975a7`. Not broken, just stale. Replace with a shorter note ("ported from the original TypeScript implementation — see git history before 87975a7 for archaeology") or delete the lineage references entirely. The same pass should also scrub the `bun:sqlite` reference in `src/TotalRecall.Infrastructure/Storage/SqliteConnection.cs:13–15`.

**Files:** 89 `.cs` / `.fs` files across `src/TotalRecall.*/`, plus `src/TotalRecall.Infrastructure/Storage/SqliteConnection.cs`.

### Windows install robustness: retry-on-EPERM upstream in Claude Code

When Claude Code's plugin update path uses `source: npm`, it does `npm install` into a temp dir and then `fs.rename(temp, dest)`. On Windows this reliably trips `EPERM: operation not permitted, rename` when Windows Defender holds a handle on a freshly-extracted `.exe` (mid-scan window is typically 50–500ms). The rename has no retry logic.

This is a one-line fix in Claude Code's plugin installer — wrap the rename in exponential backoff (100ms, 200ms, 400ms, …) on `EPERM` or `EBUSY` codes. Standard pattern used by `rimraf`, `fs-extra`, npm itself.

**Action:** File a GitHub issue against Claude Code with a clean repro (see the beta.6 dogfood trace in this session's history for details). Benefits every npm-distributed plugin on Windows, not just total-recall. Zero cost to us.

**Workaround until fixed:** See `AGENTS.md` § "Windows EPERM workaround" (manual `mv` from WSL). Also documented in the beta dogfood mechanics section.

### Windows install robustness: Authenticode code signing for the AOT binary

Defender skips full scans on binaries signed by trusted Authenticode publishers, which eliminates the mid-extract scan window that triggers the EPERM rename above. Signed binaries also make SmartScreen warnings disappear after a few hundred user installs.

Requirements:
- EV code-signing certificate (~$300–$500/year). EV certs are whitelisted faster than DV.
- Signing infrastructure: HSM-bound key for EV, or hardware token for DV.
- `signtool` + a signing step in `release.yml`'s win-x64 matrix leg before the archive staging.

Deferred until the user count justifies the cost. A good milestone trigger would be "first reported Windows Defender false-positive from a real user on a stable release."

**Files:** `.github/workflows/release.yml`

### Windows install robustness: per-platform npm packages (esbuild model)

Instead of shipping all four RID binaries in a single ~60 MB npm tarball, split into:

- `@strvmarv/total-recall` (main package, ~5 MB: bin/start.js, scripts/, skills/, agents/, hooks/, models/, no platform binaries)
- `@strvmarv/total-recall-linux-x64` (~15 MB: just the linux-x64 binary + siblings)
- `@strvmarv/total-recall-linux-arm64`, `@strvmarv/total-recall-osx-arm64`, `@strvmarv/total-recall-win-x64`

The main package lists the four platform packages as `optionalDependencies` with `os`/`cpu` filters. npm only installs the matching one per host. `bin/start.js` uses `require.resolve('@strvmarv/total-recall-' + rid)` to find the binary at runtime.

This is the industry-standard pattern for npm-distributed native CLIs — used by esbuild, sharp, biome, swc. Benefits:

- Each user install is ~15 MB (one platform) instead of ~60 MB (four platforms)
- Defender only has one binary to scan per install, which reduces the lock window enough to probably avoid the EPERM rename
- Windows install is substantially faster
- The main package extracts cleanly in milliseconds because it has no large .exe files

Tradeoffs:
- Publish 5 npm packages instead of 1 per release; release.yml publish job gets a per-platform-package loop
- Version lockstep across 5 packages instead of 1 (already a version-sync challenge we don't fully automate)
- `bin/start.js` has to do require.resolve instead of direct relative path lookup

Meaningful refactor — probably 1-2 days of work plus a beta cycle to validate. Worth tracking but not urgent until Windows user count grows.

**Files:** `package.json`, `bin/start.js`, `.github/workflows/release.yml`, new per-platform package scaffolding
