# Agent & Contributor Guide

This file is the operational handbook for AI agents and human contributors working in this repo. For end-user docs see `README.md`; for the contributor onboarding flow see `CONTRIBUTING.md`. This file documents project-wide rules, the release flow, the plugin system, the session lifecycle, the schema migration framework, and the beta dogfood mechanics.

> **State as of 0.8.x:** total-recall is a .NET 8 NativeAOT plugin (C# imperative shell + F# functional core). The TypeScript implementation that lived in `src/` through 0.7.x was stripped during the 0.7.2 → 0.8.0 cutover. Anything in this file that mentions `dist/`, `bun`, `tsup`, `vitest`, `publish.yml`, `bin/start.cjs`, or `src/db/schema.ts` is either gone or renamed — see the strip series in `CHANGELOG.md` (commits `87975a7` → `7a8c437`).

---

## Build & Release

### .NET 8 NativeAOT, no `dist/`

The build artifact is a per-platform AOT-published binary at `src/TotalRecall.Host/bin/Release/net8.0/<rid>/publish/total-recall` (Unix) or `total-recall.exe` (Windows). There is no `dist/` directory, no `tsup` build, no `npm run build` script. CI publishes binaries via `.github/workflows/release.yml` and ships them inside the npm tarball under `binaries/<rid>/`.

The .NET SDK is pinned by `global.json` at the repo root (`{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`). The pin exists because GitHub-hosted macOS runners ship .NET 10 preview pre-installed and we need every CI matrix leg to use the same SDK regardless of runner pre-installs. .NET 10 SDK builds the `net8.0` target framework cleanly.

### ONNX model is tracked via Git LFS

The embedding model (`models/all-MiniLM-L6-v2/model.onnx`) is stored with Git LFS. Contributors need `git lfs install` before cloning. The model is bundled so plugin users get offline embeddings without a HuggingFace download on first run. If the model is missing at runtime, the .NET embedder has a fallback to download from HuggingFace (see `src/TotalRecall.Infrastructure/Embedding/ModelManager.cs`).

### Version sync — five files, one version (STANDING RULE)

total-recall is a multi-host plugin (Claude Code, Copilot CLI, Cursor, OpenCode, …). Each host reads its own plugin manifest, and every manifest carries its own `version` field. They MUST all match the `package.json` version on every release. Historical drift incidents:

- `.copilot-plugin/plugin.json` was stuck on `0.1.0` for many releases — Copilot CLI users saw `0.1.0` reported even when npm was at 0.7.2
- `.claude-plugin/plugin.json` was stuck on `0.7.2` through the entire TS→.NET cutover (beta.1 → beta.3) until the build agent caught it during the beta.4 audit

**On every release you MUST bump the version in ALL of these to the same value:**

1. `package.json`
2. `package-lock.json` (top-level `version` field AND `packages[""].version` — npm keeps both in sync; the safest edit is a `replace_all` of the old version string)
3. `.claude-plugin/plugin.json`
4. `.copilot-plugin/plugin.json`
5. `.cursor-plugin/plugin.json`

`.opencode/` uses `INSTALL.md` (no versioned manifest) so it is exempt, but any version references in that doc should still be reviewed.

When agents dispatch subagents to bump versions or cut releases, this list MUST be included in the prompt. Never assume "I'll just bump package.json" — every release must sync all five.

A follow-up in `docs/TODO.md` ("Plugin Version Single Source of Truth") tracks adding a pre-commit or CI check to enforce this automatically.

### Release flow

1. Bump version in all five files above to the same value.
2. Update `CHANGELOG.md` with the new version's `### Fixed` / `### Added` / `### Changed` sections.
3. Commit with a message like `release(beta.N): bump to 0.x.y-beta.N; …` or `release: v0.x.y; …`.
4. Tag with `git tag -a vX.Y.Z -m "..."` (annotated tag with a release-note body in the tag message — `gh release` displays it).
5. Push the branch first (`git push origin rewrite/dotnet`), wait for `.github/workflows/dotnet-ci.yml` to go green.
6. Only then push the tag (`git push origin vX.Y.Z`), which fires `.github/workflows/release.yml`. The 4-job matrix builds AOT binaries for `linux-x64`, `linux-arm64`, `osx-arm64`, and `win-x64`, stages them in `binaries/<rid>/`, then the publish job downloads the four artifacts, runs `prepublishOnly` (`scripts/verify-binaries.js`), `npm publish`es with the right dist-tag (`beta`/`rc`/`latest` resolved from the version string by inline shell logic), and attaches per-RID `.tar.gz` archives to a GitHub Release.
7. Verify the publish landed: `npm view @strvmarv/total-recall dist-tags`, then `gh release view vX.Y.Z --json assets`.

The single CI workflow that runs on every push/PR is `.github/workflows/dotnet-ci.yml`. The release workflow only runs on `v*` tag pushes. There is no `publish.yml` — that was the legacy TS publish workflow and was deleted in commit `7a8c437`.

### Code-signing (not yet shipped)

Windows binaries are not yet Authenticode-signed. Defender's mid-extract scan can hold file handles long enough that npm's temp-then-rename install path fails with `EPERM` on Windows hosts (see "Beta dogfood mechanics" below for the workaround). Authenticode signing is tracked in `docs/TODO.md`.

---

## Plugin System

### How marketplace installs work

The marketplace is a separate git repo (`strvmarv/total-recall-marketplace`) containing a `marketplace.json` that lists plugins and their sources. Claude Code clones the marketplace repo into `~/.claude/plugins/marketplaces/<name>/`, reads the `marketplace.json`, and resolves each plugin entry's `source` field to fetch the plugin content. **Source types supported:**

- `source: github` (or `source: url`) → Claude Code does `git clone` into `~/.claude/plugins/cache/<marketplace>/<plugin>/<version>/`. No npm install. No postinstall hooks. Pure file-tree fetch.
- `source: npm` → Claude Code does `npm install <package>@<version>` into a temp dir, then renames the temp dir into the cache. Triggers `postinstall` lifecycle hooks. Hits Windows Defender mid-extract `EPERM` rename failures more often than the github path.

Both paths converge on the same `bin/start.js` launcher because `.mcp.json` always invokes `node ${CLAUDE_PLUGIN_ROOT}/bin/start.js`. The launcher detects the host RID, finds (or downloads via `scripts/fetch-binary.js`) the matching prebuilt binary in `binaries/<rid>/`, and exec's it with stdio passthrough.

### `.mcp.json` invocation

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/bin/start.js"],
      "cwd": "${CLAUDE_PLUGIN_ROOT}"
    }
  }
}
```

`bin/start.js` is ~60 lines of zero-dep Node. It calls `ensureBinary()` from `scripts/fetch-binary.js` which:
1. Detects the host RID via `process.platform` / `process.arch`
2. Checks `binaries/<rid>/total-recall` (or `total-recall.exe`) for existence
3. If missing, downloads `total-recall-<rid>.tar.gz` from the matching GitHub Release (URL computed from `package.json` version) into `os.tmpdir()`, extracts via system `tar` (or `tar.exe` on Windows since 1803), and writes the result into `binaries/<rid>/`
4. Returns the binary path so `bin/start.js` can `child_process.spawn` it

The download fallback exists because the **git-clone install path** fetches the source tree without `binaries/` (we never commit prebuilt binaries to git — the npm tarball ships them, but git-source installs don't go through npm). When a Claude Code marketplace entry uses `source: github`, the installed tree has `bin/start.js` and `scripts/fetch-binary.js` but no `binaries/`, and the download fallback kicks in on first launch.

### Removing a plugin install (for testing)

To fully uninstall for a clean reinstall test, remove all of:

- `~/.claude/plugins/cache/strvmarv-total-recall-marketplace/total-recall/<version>/` (per-version cache for github-source installs)
- `~/.claude/plugins/cache/total-recall/` (flat cache layout for npm-source installs — added in 0.8.x by Claude Code)
- `~/.claude/plugins/marketplaces/strvmarv-total-recall-marketplace/` (marketplace metadata cache, may not exist depending on Claude Code version)
- Entries in `~/.claude/settings.json`: `enabledPlugins["total-recall@..."]` and `extraKnownMarketplaces["strvmarv-..."]`
- `~/.total-recall/` (the SQLite database, models cache, config — only delete this if you want a fresh state, not a fresh plugin install)

---

## Beta dogfood mechanics

Beta tags (`v0.8.0-beta.N`) are published to the npm `@beta` dist-tag, which is **separate from `@latest`** (currently still on `0.7.2` TypeScript through the cutover). Public users who run `/plugin update total-recall` against the upstream marketplace get whatever the upstream marketplace's `marketplace.json` resolves to — and **the upstream marketplace is never pointed at a beta tag**, so public users never get a beta unless they explicitly opt in.

Beta dogfood is per-tester via a **local marketplace override**. Each tester clones the marketplace repo locally, edits the local clone's `marketplace.json` to pin the beta ref, registers the local clone as a marketplace in their personal `~/.claude/settings.json`, and `/plugin update`s. Public users are unaffected.

### Setup (per tester, one-time per machine)

```bash
# 1. Clone the marketplace repo
git clone https://github.com/strvmarv/total-recall-marketplace.git ~/dev/total-recall-marketplace
cd ~/dev/total-recall-marketplace

# 2. Edit .claude-plugin/marketplace.json — replace the total-recall entry's
#    "source" block with the github source pinned to the beta ref:
#
#    "source": {
#      "source": "github",
#      "repo": "strvmarv/total-recall",
#      "ref": "v0.8.0-beta.7"
#    }
#
# Why github source (not npm source): the npm install path on Windows
# trips EPERM on the temp-dir rename when Defender holds a handle on a
# freshly-extracted .exe inside the temp dir. The github source is a
# pure git clone with no rename window, and bin/start.js downloads the
# binary on first launch via fetch-binary.js — the download path doesn't
# trigger the same Defender lock.

# 3. Register the local clone in ~/.claude/settings.json. Find the
#    "extraKnownMarketplaces" entry for strvmarv-total-recall-marketplace
#    and replace its "source" block with a path source:
#
#    "extraKnownMarketplaces": {
#      "strvmarv-total-recall-marketplace": {
#        "source": { "source": "path", "path": "/home/<you>/dev/total-recall-marketplace" }
#      }
#    }
#
# Keep the existing "enabledPlugins" entry unchanged.

# 4. Fully quit and restart Claude Code so it re-reads settings.json.

# 5. In a fresh CC session: /plugin update total-recall

# 6. Verify in a terminal:
total-recall --version
# Expected: total-recall 0.8.0-beta.<N>  (NOT 0.7.2 or 0.1.0)

total-recall status
# Expected: clean tier output, no DllNotFoundException, no TypeInitializationException
```

### Bumping to a new beta

```bash
cd ~/dev/total-recall-marketplace
# Edit .claude-plugin/marketplace.json — bump the "ref" value to the new tag.
# In Claude Code: /plugin update total-recall
# (No need to touch settings.json again — the path source picks up
# marketplace.json edits on every plugin operation.)
```

### Reverting to public stable

Restore the original `extraKnownMarketplaces` entry's source block to:

```json
"source": { "source": "github", "repo": "strvmarv/total-recall-marketplace" }
```

Restart Claude Code, `/plugin update total-recall`. You're back on stable.

### Windows EPERM workaround

If `/plugin update` fails on Windows with `EPERM: operation not permitted, rename '...temp_npm_...' -> '...total-recall'`, the install actually succeeded — only the final rename failed. Recovery from a WSL prompt:

```bash
# Find the leftover temp dir
ls /mnt/c/Users/<you>/.claude/plugins/cache/temp_npm_*

# Rename it manually (WSL bypasses the Win32 file lock)
mv /mnt/c/Users/<you>/.claude/plugins/cache/temp_npm_<id> \
   /mnt/c/Users/<you>/.claude/plugins/cache/total-recall

# Restart Claude Code
```

The root cause is Windows Defender mid-scanning the freshly-extracted `total-recall.exe` at the moment of rename. Switching the marketplace to `source: github` avoids the issue entirely (the github path doesn't have the rename window). Tracked in `docs/TODO.md`.

---

## Session Lifecycle

### What happens on `session_start`

1. **Migration guard** — `AutoMigrationGuard` (`src/TotalRecall.Server/AutoMigrationGuard.cs`) inspects the database file in read-only mode (`InspectDbFormat`) and dispatches via a 5-state state machine: `NotPresent`, `EmptyFile`, `TsFormat`, `PartialNetEmpty`, `PartialNetPopulated`, `NetMigrated`. Handles the partial-state cliff where both `total-recall.db` and `total-recall.db.ts-backup` exist by sidelining the suspect file to `<dbPath>.failed-migration-<utc>` (never deletes anything).
2. **Import sync** — scans Claude Code, Copilot CLI, Cursor, Cline, OpenCode, and Hermes memory dirs via the `IImporter` collection in `src/TotalRecall.Infrastructure/Importers/`. Deduplicates via content hash in `import_log`.
3. **Warm sweep** — if last sweep was more than `warm_sweep_interval_days` ago, moves old unaccessed warm entries to cold. Tracked via `compaction_log` with `reason = 'warm_sweep_decay'`.
4. **Project docs auto-ingest** — detects `README.md`, `CONTRIBUTING.md`, `CLAUDE.md`, `AGENTS.md`, and `docs/` in cwd. Ingests into a `<project>-project-docs` KB collection. Deduplicates via `import_log`.
5. **Smoke test** — if `_meta.smoke_test_version` differs from current package version, runs a 22-query benchmark from `eval/benchmarks/smoke.jsonl`. Pass threshold: `exactMatchRate >= 0.8`. Writes version to `_meta` on completion. Result returned as `smokeTest` field.
6. **Hot tier assembly** — returns current hot entries as injectable context. Enforces token budget by evicting lowest-decay entries to warm.
7. **Tier summary** — counts entries across all tiers and KB collections, returned as `tierSummary` in the response.
8. **Hint generation** — `GenerateHints()` surfaces up to 5 high-value warm memories: corrections and preferences (priority 1), frequently accessed entries with `access_count >= 3` (priority 2), and recently promoted entries (priority 3). Each hint is truncated to 120 chars. No LLM calls — DB queries only.
9. **Session continuity** — `GetLastSessionAge()` returns human-readable relative time since last compaction event (proxy for last session). Returns `null` for first-time users.
10. **Config snapshot** — captures current config as a named snapshot (`"session-start"`), sets `ctx.ConfigSnapshotId` for the session so retrieval events and compaction are tagged to this config state.
11. **Regression detection** — compares current session metrics against previous config snapshot. Alerts if miss rate increased by ≥ `regression.miss_rate_delta` (default: 0.1) or latency increased by ≥ `regression.latency_ratio` (default: 2.0x). Skipped if fewer than 2 snapshots or insufficient events. Result returned as `regressionAlerts` field.

### Config persistence

`config_set` writes to `~/.total-recall/config.toml` via `Tomlyn`. Changes are merged with existing user config and take effect immediately in the current session. Before writing, `config_set` auto-creates a config snapshot named `pre-change:<key>` so retrieval metrics from before and after can be compared with `eval_compare`.

### Eval system

`eval_report` returns: precision, hit rate, miss rate, MRR, latency, breakdowns by tier and content type, top misses (lowest scoring queries), false positives (high score but unused), and compaction health (total compactions, preservation ratio, semantic drift). Data comes from `retrieval_events` and `compaction_log` tables. Accepts optional `config_snapshot` param to filter events by a specific config snapshot ID, and `days` param (default: 7).

`eval_compare` compares retrieval metrics between two config snapshots. Required param: `before` (snapshot name or ID). Optional: `after` (default: `"latest"`), `days` (default: 30). Returns summary deltas, per-tier and per-content-type breakdowns, and query-level diff showing regressions (used→unused) and improvements (unused→used). Warns if either snapshot has no retrieval events.

`eval_snapshot` manually creates a named config snapshot. Returns `{ id, name, created }`. Useful for tagging a baseline before config experiments.

`eval_grow` lists pending benchmark candidates auto-captured from retrieval misses (in `benchmark_candidates`) and lets you accept/reject them. Accepted entries get appended to `eval/benchmarks/retrieval.jsonl`.

### `ToolContext` and the composition root

`ToolContext` (in `src/TotalRecall.Server/`) carries session state through all tool handlers: `Store`, `Config`, `Embedder`, `SessionId`, and `ConfigSnapshotId`. The `ConfigSnapshotId` is set by `session_start` and used by `memory_search` (for retrieval event logging) and the compactor (for compaction logging). New tools that call `LogRetrievalEvent` should pass `ctx.ConfigSnapshotId`.

The composition root in `src/TotalRecall.Host/Program.cs` wires up all dependencies (storage, embedder, importers, MCP server, migration guard) and is the AOT entry point. The 32 MCP handlers live in `src/TotalRecall.Server/Handlers/` — one file per handler.

---

## Database Migrations

Schema changes are handled by a sequential migration framework in `src/TotalRecall.Infrastructure/Storage/Schema.cs`. The `MigrationRunner` runs each migration function inside a transaction, indexed by `_schema_version`. On startup, it checks the current schema version and runs any newer migrations.

Current migrations (as of 0.8.0-beta.7):

1. **Migration 1** — initial schema (entries tables, vec0 virtual tables, FTS, telemetry tables, _meta, _schema_version).
2. **Migration 2** — knowledge tier tables (`hot_knowledge`, `warm_knowledge`, `cold_knowledge` + vec).
3. **Migration 3** — retrieval event log + import log.
4. **Migration 4** — `compaction_log.source TEXT NOT NULL DEFAULT 'compaction'` for distinguishing compactor-originated movements from manual `promote`/`demote`.
5. **Migration 5** — sweeps all 6 content/vec table pairs and deletes orphan rows (added in 0.8.0-beta.6 to clean up state from the parallel-store concurrency bug fixed in the same release).

To add a schema change:

1. Add a new function to the migrations array in `Schema.cs` (do **NOT** modify existing migrations — they're frozen).
2. The function receives the `SqliteConnection` and runs inside a transaction.
3. Use `CREATE TABLE IF NOT EXISTS` and `ALTER TABLE ... ADD COLUMN` as needed.
4. The version number is the array index + 1.
5. Add a test in `tests/TotalRecall.Infrastructure.Tests/SchemaTests.cs` covering the new migration's pre/post-state.

---

## Diagnostics & error reporting

`src/TotalRecall.Infrastructure/Diagnostics/ExceptionLogger.cs` provides `LogChain(prefix, ex)` that walks the entire `InnerException` chain and writes each level with indented `-> <Type>: <Message>` formatting, then the outer stack trace. AOT-safe (uses the first-class `InnerException` property, no reflection).

**Use it at every catch boundary** that can plausibly hit a static-ctor failure or P/Invoke failure (migration guard, server composition, CLI commands that touch the embedder or open the DB). A bare `Console.Error.WriteLine(ex.Message)` at one of these boundaries hides `DllNotFoundException` for missing native libs under a `TypeInitializationException` and produces unactionable error output. The 0.8.0-beta.4 beta tester was blocked for ~30 minutes by this exact failure mode before `ExceptionLogger.LogChain` was retrofitted across 10 boundary catches in 0.8.0-beta.5.

---

## Project-wide standing rules

These are non-negotiable rules that apply to every commit, every PR, every subagent dispatch:

1. **No `Co-Authored-By: Claude ...` (or any AI co-author) trailers** in commit messages. Project history is attributed solely to human authors. When dispatching subagents that include `git commit` instructions, the prompt MUST explicitly say "do NOT add any Co-Authored-By trailer."
2. **Spec and plan documents in `docs/superpowers/specs/` and `docs/superpowers/plans/` are NEVER committed.** They live in the working tree only. The brainstorming and writing-plans skills both default to committing them — that default is overridden on this project. Do not auto-commit them.
3. **Five-file version sync on every release** (see "Version sync" section above).
4. **Never delete anything destructively.** This applies broadly: never `git reset --hard` without confirmation, never `git push --force` without confirmation, never delete user data. The `AutoMigrationGuard` follows this principle: it sidelines suspect database files to `<dbPath>.failed-migration-<utc>` instead of deleting them.

## Deferred Items

See `docs/TODO.md` for the post-cutover follow-up backlog: checksum verification of downloaded binaries, code signing, multi-platform CI matrix, version-sync automation, doc scrubs, and more.
