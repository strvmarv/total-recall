# Agent & Contributor Guide

## Build & Release

### dist/ is committed

`dist/` is tracked in git (not gitignored). This is intentional — the Claude Code plugin marketplace clones from git, not npm, so `dist/index.js` must be present in the repo for the MCP server to start.

- Always run `npm run build` after changing `src/` and commit the updated `dist/`
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

`.mcp.json` invokes `bash bin/total-recall.sh` instead of `npx`. The launcher resolves the server in this order:

1. `dist/index.js` relative to the script (works for git clones and npm installs)
2. `total-recall` global binary in PATH (works for `npm install -g`)
3. Entry point in global `node_modules` via `npm root -g` (edge case fallback)

### Removing a plugin install (for testing)

To fully uninstall for a clean reinstall test, remove all three:
- `~/.claude/plugins/cache/strvmarv-total-recall-marketplace/`
- `~/.claude/plugins/marketplaces/strvmarv-total-recall-marketplace/`
- Entries in `~/.claude/settings.json`: `enabledPlugins["total-recall@..."]` and `extraKnownMarketplaces["strvmarv-..."]`

### Node discovery

The launcher (`bin/total-recall.sh`) finds `node` across common install methods: PATH, nvm, fnm, Homebrew (Linux and macOS), and Volta. If adding a new node version manager, add its lookup to the `find_node()` function.

## Session Lifecycle

### What happens on session_start

1. Import sync — scans Claude Code and Copilot CLI memory dirs, deduplicates via content hash in `import_log`
2. Warm sweep — if last sweep was more than `warm_sweep_interval_days` ago, moves old unaccessed warm entries to cold. Tracked via `compaction_log` with `reason = 'warm_sweep_decay'`.
3. Project docs auto-ingest — detects README.md, CONTRIBUTING.md, CLAUDE.md, AGENTS.md, and docs/ in cwd. Ingests into a `{project}-project-docs` KB collection. Deduplicates via `import_log`.
4. Hot tier assembly — returns current hot entries as injectable context.

### Config persistence

`config_set` writes to `~/.total-recall/config.toml` using `@iarna/toml` stringify. Changes are merged with existing user config and take effect immediately in the current session.

### Eval system

`eval_report` returns: precision, hit rate, miss rate, MRR, latency, breakdowns by tier and content type, top misses (lowest scoring queries), false positives (high score but unused), and compaction health (total compactions, preservation ratio, semantic drift). Data comes from `retrieval_events` and `compaction_log` tables.

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
