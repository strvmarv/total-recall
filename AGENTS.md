# Agent & Contributor Guide

## Build & Release

### dist/ is committed

`dist/` is tracked in git (not gitignored). This is intentional — the Claude Code plugin marketplace clones from git, not npm, so `dist/index.js` must be present in the repo for the MCP server to start.

- A pre-commit hook in `git-hooks/pre-commit` auto-rebuilds and stages `dist/` when `src/` files are committed. Activated by `npm install` (via the `prepare` script setting `core.hooksPath`).
- CI will fail if `dist/` is stale after a clean build (`git diff --exit-code dist/`)
- `.gitattributes` marks `dist/**` as `linguist-generated` so GitHub collapses diffs in PRs

### ONNX model is tracked via Git LFS

The embedding model (`models/**/*.onnx`) is stored with Git LFS. Contributors need `git lfs install` before cloning. The model is bundled so plugin users get offline embeddings without a HuggingFace download on first run.

If the model is missing at runtime, the code auto-downloads from HuggingFace as a fallback (see `src/embedding/model-manager.ts`).

### Release flow

1. Bump version in both `package.json` and `.claude-plugin/plugin.json` (keep them in sync)
2. Run `npm run build` and commit the updated `dist/`
3. Commit with message like `0.x.y`
4. Tag with `git tag v0.x.y`
5. Push both: `git push && git push origin v0.x.y`
6. The `publish.yml` workflow triggers on `v*` tags — runs tests, builds, publishes to npm

## Plugin System

### How marketplace installs work

The marketplace repo (`strvmarv/total-recall-marketplace`) points Claude Code at the source repo via a git URL. Claude Code clones the source repo into `~/.claude/plugins/cache/`. It reads `.mcp.json` to start the MCP server.

### npx does not work for scoped packages

`npx` cannot resolve binaries for scoped packages where the `bin` name differs from the package scope. `npx -y @strvmarv/total-recall` fails with "command not found" because npx looks for a binary matching the scope, not the `bin` field (`total-recall`). This is a known npm bug. Never use `npx` in `.mcp.json` for this package.

### .mcp.json uses the bash launcher

`.mcp.json` invokes `bash bin/total-recall.sh` instead of `npx`. The launcher resolves the runtime in this order:

1. `~/.total-recall/bun/<version>/bun` — bundled Bun binary (downloaded by `postinstall`)
2. System `bun` in PATH — warns if version differs
3. System `node` via PATH, nvm, fnm, Homebrew, Volta — warns that native addon ABI issues may occur

The entry point is always `dist/index.js` relative to the script.

### Removing a plugin install (for testing)

To fully uninstall for a clean reinstall test, remove all three:
- `~/.claude/plugins/cache/strvmarv-total-recall-marketplace/`
- `~/.claude/plugins/marketplaces/strvmarv-total-recall-marketplace/`
- Entries in `~/.claude/settings.json`: `enabledPlugins["total-recall@..."]` and `extraKnownMarketplaces["strvmarv-..."]`

### Bundled Bun

`npm install` runs `scripts/postinstall.js` which downloads a pinned Bun binary to `~/.total-recall/bun/<BUN_VERSION>/`. The pinned version is the `BUN_VERSION` constant in three places that must stay in sync when upgrading:

- `scripts/postinstall.js`
- `bin/total-recall.sh` and `bin/total-recall.cmd`
- `bin/start.cjs`

To clear the cached binary: `rm -rf ~/.total-recall/bun/`.

Since 0.6.8, `dist/index.js` uses `bun:sqlite` and cannot run under node — the launchers intentionally do not fall back to node (it crashes with `ERR_UNSUPPORTED_ESM_URL_SCHEME`). If bun is missing, `npm install` is the only remediation. If you ever need node-version-manager discovery logic again (nvm/fnm/volta/homebrew/macports/nvm4w), resurrect `find_node()` from commit `9115a9b`.

## Session Lifecycle

### What happens on session_start

1. Import sync — scans Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes memory dirs, deduplicates via content hash in `import_log`
2. Warm sweep — if last sweep was more than `warm_sweep_interval_days` ago, moves old unaccessed warm entries to cold. Tracked via `compaction_log` with `reason = 'warm_sweep_decay'`.
3. Project docs auto-ingest — detects README.md, CONTRIBUTING.md, CLAUDE.md, AGENTS.md, and docs/ in cwd. Ingests into a `{project}-project-docs` KB collection. Deduplicates via `import_log`.
4. Smoke test — if `_meta.smoke_test_version` differs from current package version, runs a 22-query benchmark from `eval/benchmarks/smoke.jsonl`. Pass threshold: exactMatchRate >= 0.8. Writes version to `_meta` on completion. Result returned as `smokeTest` field.
5. Hot tier assembly — returns current hot entries as injectable context. Enforces token budget by evicting lowest-decay entries to warm.
6. Tier summary — counts entries across all tiers and KB collections, returned as `tierSummary` in the response.
7. Hint generation — `generateHints()` surfaces up to 5 high-value warm memories: corrections and preferences (priority 1), frequently accessed entries with `access_count >= 3` (priority 2), and recently promoted entries (priority 3). Each hint is truncated to 120 chars. No LLM calls — DB queries only.
8. Session continuity — `getLastSessionAge()` returns human-readable relative time since last compaction event (proxy for last session). Returns `null` for first-time users.
9. Config snapshot — captures current config as a named snapshot (`"session-start"`), sets `ctx.configSnapshotId` for the session so retrieval events and compaction are tagged to this config state.
10. Regression detection — compares current session metrics against previous config snapshot. Alerts if miss rate increased by ≥ `regression.miss_rate_delta` (default: 0.1) or latency increased by ≥ `regression.latency_ratio` (default: 2.0x). Skipped if fewer than 2 snapshots or insufficient events. Result returned as `regressionAlerts` field.

### Config persistence

`config_set` writes to `~/.total-recall/config.toml` using `@iarna/toml` stringify. Changes are merged with existing user config and take effect immediately in the current session. Before writing, `config_set` auto-creates a config snapshot named `pre-change:<key>` so retrieval metrics from before and after can be compared with `eval_compare`.

### Eval system

`eval_report` returns: precision, hit rate, miss rate, MRR, latency, breakdowns by tier and content type, top misses (lowest scoring queries), false positives (high score but unused), and compaction health (total compactions, preservation ratio, semantic drift). Data comes from `retrieval_events` and `compaction_log` tables. Accepts optional `config_snapshot` param to filter events by a specific config snapshot ID, and `days` param (default: 7).

`eval_compare` compares retrieval metrics between two config snapshots. Required param: `before` (snapshot name or ID). Optional: `after` (default: `"latest"`), `days` (default: 30). Returns summary deltas, per-tier and per-content-type breakdowns, and query-level diff showing regressions (used→unused) and improvements (unused→used). Warns if either snapshot has no retrieval events.

`eval_snapshot` manually creates a named config snapshot. Returns `{ id, name, created }`. Useful for tagging a baseline before config experiments.

### ToolContext

`ToolContext` (in `src/tools/registry.ts`) carries session state through all tool handlers: `db`, `config`, `embedder`, `sessionId`, and `configSnapshotId`. The `configSnapshotId` is set by `session_start` and used by `memory_search` (for retrieval event logging) and `compactHotTier` (for compaction logging). New tools that call `logRetrievalEvent` should pass `ctx.configSnapshotId`.

## Database Migrations

Schema changes are handled by a sequential migration framework in `src/db/schema.ts`. The `MIGRATIONS` array contains one function per version. On startup, `initSchema()` checks `_schema_version` for the current version and runs any newer migrations.

To add a schema change:
1. Add a new function to the `MIGRATIONS` array (do NOT modify existing migrations)
2. The function receives the `db` and runs inside a transaction
3. Use `CREATE TABLE IF NOT EXISTS` and `ALTER TABLE ... ADD COLUMN` as needed
4. The version number is the array index + 1

Existing v1 databases (created before the migration framework) are handled correctly — they have `_schema_version.version = 1`, so migration 0 is skipped.

## Deferred Items

See `docs/TODO.md` for features planned in the design spec but not yet implemented, ordered by impact.
