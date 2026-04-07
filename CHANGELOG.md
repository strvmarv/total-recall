# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 0.6.8-beta.5 - 2026-04-06

### Fixed
- **Cursor importer on Windows**: `CursorImporter.importKnowledge` silently skipped every workspace-discovered project on Windows. Root cause: `src/importers/cursor.ts` parsed the `folder` field from Cursor's `workspace.json` with `new URL(folder).pathname`, which returns `/C:/Users/...` on Windows — not a valid filesystem path, so the subsequent `existsSync(join(projectPath, ".cursorrules"))` always failed. Replaced with `fileURLToPath` from `node:url` (via a `safeFileURLToPath` helper that swallows malformed URLs), which produces the correct `C:\Users\...` on Windows and `/Users/...` on POSIX. Affects all Windows users who had Cursor rules to import since 0.6.0-ish.
- **`detectProject` on Windows**: `src/utils/project-detect.ts` used `process.env.HOME` to recognize the home directory and hardcoded `"/"` as the filesystem root. Both are POSIX-only — `HOME` is undefined on Windows (Node uses `USERPROFILE`), and the root is `C:\` (or whatever drive). Switched to `os.homedir()` for the home check and `path.parse(cwd).root` for the root check. Previously, `detectProject(someWindowsHomeDir)` would return the home folder's basename instead of null.
- **CI was failing on `test-bun (windows-latest)`** since 0.6.8-beta.1 because of the two bugs above plus one test bug (`cursor.test.ts` constructed `folder: \`file://\${projectDir}\`` which is malformed on Windows — two slashes, backslashes in the path; fixed to use `pathToFileURL(projectDir).href`). The NPM publish workflow is separate from the CI workflow, so beta.1 through beta.4 all published despite red CI. This beta is the first one where the full matrix (ubuntu/macos/windows × bun) is green.

## 0.6.8-beta.4 - 2026-04-06

### Fixed
- MCP server failed to start on Claude Code (and any host launching via `.mcp.json`) after the 0.6.8 migration to `bun:sqlite`. `bin/start.cjs` still re-exec'd `dist/index.js` under `node`, which cannot resolve the `bun:` URL scheme and crashed immediately with `ERR_UNSUPPORTED_ESM_URL_SCHEME`. The launcher now locates the bundled bun binary at `~/.total-recall/bun/<version>/bun` (installed by `scripts/postinstall.js`), falls back to system `bun` on PATH, and fails fast with a clear remediation message if neither is present.
- `bin/start.cjs` no longer uses the stale `node_modules/better-sqlite3/lib` canary — that dependency was removed from production installs in 0.6.8, so every launch was triggering a spurious `npm install`. The bootstrap now trusts that `npm install` ran postinstall correctly and only checks for `dist/index.js` and a bun runtime.
- `serverInfo.version` in the MCP `initialize` response is no longer hardcoded to `0.5.9` — it's read from `package.json` at startup via the existing `pkgPath` helper, so the value stays in sync with the actual release.
- `bin/total-recall.sh` and `bin/total-recall.cmd` no longer fall back to `node` when bun is missing. That branch was dead code after the `bun:sqlite` migration — `dist/index.js` cannot be parsed by node's ESM loader (`ERR_UNSUPPORTED_ESM_URL_SCHEME`), so the "warning: falling back to node" path only produced a louder crash. Both launchers now fail fast with a clear remediation message pointing at `npm install`.

## 0.6.8 - 2026-04-07

### Changed

- Replaced `better-sqlite3` native addon with `bun:sqlite` (built into Bun runtime). Eliminates native addon ABI mismatches when host tools (OpenCode, Claude Code) bundle their own Node.js version.
- Plugin now downloads a pinned Bun binary (v1.2.10) to `~/.total-recall/bun/` on `npm install`. Requires internet access on first install (~60MB). Subsequent installs use the cached binary.
- Launcher (`bin/total-recall.sh`, `bin/total-recall.cmd`) updated to prefer bundled Bun, fall back to system Bun, then system Node with a warning.
- CI matrix expanded to test on ubuntu, macos, and windows with Bun.
- `better-sqlite3` moved to devDependency only (used by vitest shim for Node-based testing).

## 0.6.7 - 2026-04-06

### Fixed
- `session_start` no longer fails with `ENOENT ... models/registry.json` on marketplace installs. Root cause: `src/embedding/registry.ts` computed the registry path with `../../models/registry.json` relative to `import.meta.url`, which was correct for the source tree but wrong after `tsup` bundles the server to a single `dist/index.js` — the resolver walked one level too many and escaped the version-scoped plugin directory. Replaced with a `pkgPath()` helper that walks up to `package.json` and works identically in source, bundled, and sub-bundled layouts.

### Added
- New `tests/dist-smoke.test.ts` regression test: spawns the built `dist/index.js` as a real MCP subprocess and calls `session_start` against it, so bundler-layout regressions of this kind fail loudly before publish. Wired into `prepublishOnly` via a new `test:dist` npm script. Unit tests previously exercised only the source tree, which is why 0.6.6 shipped broken.

## 0.6.6 - 2026-04-06

### Fixed
- `/total-recall update` now detects the plugin install mode. For marketplace tarball installs (the common case), it routes users through Claude Code's `/plugin` updater instead of attempting a `git pull` against a `.git`-less cache directory. Git-checkout installs still use `git pull origin main`.

## 0.6.5 - 2026-04-06

### Security
- Bump `vite` dev dependency from 8.0.3 to 8.0.5 to address three advisories: `server.fs.deny` bypass via queries (GHSA-v2wj-q39q-566r, high), arbitrary file read via dev-server WebSocket (GHSA-p9ff-h696-f583, high), and path traversal in optimized deps `.map` handling (GHSA-4w7w-66w2-5vf9, moderate). Vite is dev-only and not shipped to end users; advisories were not reachable from the published package.

### Fixed
- Sync `.claude-plugin/plugin.json` version (was stuck at 0.6.3 — missed during the 0.6.4 release).

## 0.6.4 - 2026-04-06

### Fixed
- **Model bundle resilience**: `session_start` no longer crashes with "Protobuf parsing failed" when the bundled `model.onnx` is a Git LFS pointer file. The embedding model loader now performs structural validation (file size match) and SHA-256 checksum verification, falling back to download from HuggingFace when the bundled file is corrupt or missing.

### Added
- `ModelBootstrap` state machine (`src/embedding/bootstrap.ts`) coordinates model validation, downloads, and cross-process locking. Uses `proper-lockfile` to prevent concurrent processes from racing on the same download.
- `ModelNotReadyError` (`src/embedding/errors.ts`) carries structured `reason` ("missing" | "downloading" | "failed" | "corrupted") and a user-facing `hint` with manual install commands.
- Atomic, retrying `downloadModel` with progress callbacks, exponential backoff, and SHA-256 verification on completion.
- `.verified` sidecar file that caches the model's checksum so subsequent loads skip the expensive hash computation.
- Model registry (`models/registry.json`) is now the single source of truth for model metadata (sha256, size, file URLs, revision).
- The MCP tool dispatcher (`src/tools/registry.ts`) translates `ModelNotReadyError` into a structured `model_not_ready` response so the using-total-recall skill can drive recovery.
- The `using-total-recall` skill now contains a recovery table for the four `model_not_ready` reasons.
- Manual smoke-test checklist at `tests/manual/model-bootstrap.md`.

## 0.6.3 - 2026-04-06

### Fixed
- Sync `.claude-plugin/plugin.json` version with `package.json` so the Claude Code marketplace reports the correct installed version. Previously v0.6.2 left `plugin.json` at `0.6.1`, causing `/plugin` to report stale version info.

## 0.6.2 - 2026-04-06

### Added
- `bin/start.cjs`: CJS bootstrap wrapper that detects missing `node_modules` and runs `npm install` before launching the MCP server, resolving version-gated startup failures after fresh plugin installs or `git pull`.
- Updated `.mcp.json` to launch via `bin/start.cjs` instead of `dist/index.js` directly.

### Changed
- Replaced `@iarna/toml` with `smol-toml` (pure ESM, no CJS stream dependency) to fix bundling failures under `noExternal` tsup config.
- Added `platform: node` to tsup config to fix CJS interop in the ESM bundle.

## 0.6.1 - 2026-04-06

### Added
- `help` subcommand added to the `total-recall` skill.

### Fixed
- Bundle `@iarna/toml`, `sqlite-vec`, and the MCP SDK into `dist/` to fix startup failures on marketplace installs where `node_modules` is absent.
- Cross-platform compatibility pass (Windows `cmd.exe` path fixes, macOS node-direct MCP launcher).

## [0.5.9](https://github.com/strvmarv/total-recall/compare/v0.5.8...v0.5.9) (2026-04-05)

### Bug Fixes

* coerce JSON-stringified arrays in MCP tool parameters — fixes `tags must be an array` error when MCP clients serialize array params as strings instead of native arrays
* apply coercion to all array-typed tool parameters: `tags`, `tiers`, `contentTypes`, `content_types`, `accept`, `reject`

## [0.5.8](https://github.com/strvmarv/total-recall/compare/v0.5.7...v0.5.8) (2026-04-05)

### Bug Fixes

* use absolute path via CLAUDE_PLUGIN_ROOT in .mcp.json to fix MCP server launch failure when cwd is not the plugin directory

## [0.5.7](https://github.com/strvmarv/total-recall/compare/v0.5.6...v0.5.7) (2026-04-05)

### Features

* add CI smoke benchmark script ([595e8f7](https://github.com/strvmarv/total-recall/commit/595e8f7))

### Bug Fixes

* resolve CodeQL security alerts ([7b2905e](https://github.com/strvmarv/total-recall/commit/7b2905e))
* handle empty changelog in publish pipeline ([dc80828](https://github.com/strvmarv/total-recall/commit/dc80828))

### Chores

* bump esbuild in the npm_and_yarn group ([eccaec4](https://github.com/strvmarv/total-recall/commit/eccaec4))
* add CI smoke benchmark step to CI pipeline ([58cee2d](https://github.com/strvmarv/total-recall/commit/58cee2d))
* bump testTimeout to 20s for vitest 4 compatibility ([d8af19e](https://github.com/strvmarv/total-recall/commit/d8af19e))

## [0.5.6](https://github.com/strvmarv/total-recall/compare/v0.5.5...v0.5.6) (2026-04-05)

### Chores

* add conventional-changelog-cli dev dependency ([ed38ce8](https://github.com/strvmarv/total-recall/commit/ed38ce8))
* backfill CHANGELOG.md from existing git history ([293d9bb](https://github.com/strvmarv/total-recall/commit/293d9bb))
* add GitHub Release creation and CHANGELOG.md update to publish pipeline ([b104186](https://github.com/strvmarv/total-recall/commit/b104186))
* audit fixes for README, AGENTS.md, and skill visibility ([f55d74e](https://github.com/strvmarv/total-recall/commit/f55d74e))

## [0.5.5](https://github.com/strvmarv/total-recall/compare/v0.5.4...v0.5.5) (2026-04-05)

### Bug Fixes

* recalibrate negative assertion benchmarks for contrastive corpus entries ([ab0ff71](https://github.com/strvmarv/total-recall/commit/ab0ff717a63e3a36a0b121689ecde3cffd84486e))

## [0.5.4](https://github.com/strvmarv/total-recall/compare/v0.5.2...v0.5.4) (2026-04-05)

### Features

* add FTS5 schema migration with sync triggers and backfill ([2478457](https://github.com/strvmarv/total-recall/commit/2478457ecf72e5ade6a3cfc6a4e985d687b8ddc0))
* add FTS5 score fusion to searchMemory for hybrid search ([11905fc](https://github.com/strvmarv/total-recall/commit/11905fc8af58c1a61a8e453e005aee8bd74d23d5))
* add FTS5 search with BM25 scoring and query sanitization ([cdf8e43](https://github.com/strvmarv/total-recall/commit/cdf8e43f8e60a929cb163e0a34a7820255910e5f))
* add search.fts_weight config with 0.3 default ([aa997ff](https://github.com/strvmarv/total-recall/commit/aa997ff7520d635fa1f588e65b3f113d477ccacc))
* add WordPiece tokenizer with BertNormalizer and BertPreTokenizer ([172aa7b](https://github.com/strvmarv/total-recall/commit/172aa7b9ab4a22776704a8347282006920085d82))
* wire WordPiece tokenizer into Embedder, replacing whitespace-only tokenize() ([adfeed7](https://github.com/strvmarv/total-recall/commit/adfeed76bf54110139bfed7ca60857ef1ac25d27))

### Bug Fixes

* tokenizer prototype pollution causing BigInt crash on session_start ([6e0f5e8](https://github.com/strvmarv/total-recall/commit/6e0f5e8d91a7fd6c7deae31d86967247f88320eb))

## [0.5.2](https://github.com/strvmarv/total-recall/compare/0.5.1...v0.5.2) (2026-04-05)

### Features

* add benchmark candidate capture and resolution ([1d212e1](https://github.com/strvmarv/total-recall/commit/1d212e12951657d2fe283ffa343418f368694f2b))
* add eval_grow tool for evolving benchmarks ([b0b9705](https://github.com/strvmarv/total-recall/commit/b0b970560f21dc4ed6982dd1a715605f976d495a))
* add migration 2 — _meta and benchmark_candidates tables ([c6a8882](https://github.com/strvmarv/total-recall/commit/c6a8882d0ccacd2e40189cac817e6fae536b9b97))
* add regression detection with configurable thresholds ([08848db](https://github.com/strvmarv/total-recall/commit/08848dbd44e17359252871224340013458576455))
* add smoke test runner with version-gated execution ([80aa734](https://github.com/strvmarv/total-recall/commit/80aa734dd81e5d4f68907c31139eb6527e5d3552))
* capture miss candidates during eval_report ([d3292b0](https://github.com/strvmarv/total-recall/commit/d3292b09080527d8689b7e0bcfaa481f100f23a1))
* replace single-chunk ingest validation with 3-probe approach ([6a88e36](https://github.com/strvmarv/total-recall/commit/6a88e3660fb377ada8b2b25c16062eb3d4e59616))
* wire regression detection into session_start ([7f5499a](https://github.com/strvmarv/total-recall/commit/7f5499a3c6fbfff51a726b3ef87f8cb549fb14fc))
* wire smoke test into session_start ([b1251ab](https://github.com/strvmarv/total-recall/commit/b1251ab5fd57c23580b828695080ce299e9769b8))

### Bug Fixes

* rebuild dist for v0.5.1 ([c5a4f00](https://github.com/strvmarv/total-recall/commit/c5a4f007e14c79bd790b9a292b67be4bf482d138))

## [0.5.1](https://github.com/strvmarv/total-recall/compare/v0.5.0...0.5.1) (2026-04-05)

### Features

* add Cursor, Cline, OpenCode, and Hermes host importers ([c4d2686](https://github.com/strvmarv/total-recall/commit/c4d2686b38d67902d22eb986ed04af2893aec7e6))

## [0.5.0](https://github.com/strvmarv/total-recall/compare/v0.4.0...v0.5.0) (2026-04-05)

### Features

* add generateHints and getLastSessionAge helpers ([0377646](https://github.com/strvmarv/total-recall/commit/0377646d1b64f9071927c32ad3a309f3ae5646a5))
* add listEntriesByMetadata query helper for metadata-based filtering ([9ca8f59](https://github.com/strvmarv/total-recall/commit/9ca8f5975d0769c55287698ed7846687a2360ba7))
* wire tierSummary, hints, and lastSessionAge into session_start response ([b92ba93](https://github.com/strvmarv/total-recall/commit/b92ba93cb3c6699940329fec88d89018d754d4d4))

### Bug Fixes

* handle singular and zero cases in getLastSessionAge ([60accf3](https://github.com/strvmarv/total-recall/commit/60accf31a502860f6c94ab0963e3c853c99ae7a3))
* pre-release fixes for v0.5.0 ([9b58cad](https://github.com/strvmarv/total-recall/commit/9b58cad979084befcab6720a73ba555fe07933c1))
* validate metadata keys and reject empty filter in listEntriesByMetadata ([e85ca9f](https://github.com/strvmarv/total-recall/commit/e85ca9f0d4c10195aeaaa2965c8e7e8745824a8f))

## [0.4.0](https://github.com/strvmarv/total-recall/compare/v0.3.3...v0.4.0) (2026-04-05)

### Features

* add computeComparisonMetrics for A/B config comparison ([b7dcf5f](https://github.com/strvmarv/total-recall/commit/b7dcf5fce76dca75d9b262e0647d46250aa89178))
* add configSnapshotId to ToolContext ([72f2fc7](https://github.com/strvmarv/total-recall/commit/72f2fc7c0f6f4aef31d76fe6831066080ab86d15))
* add createConfigSnapshot with deduplication ([14fa5e0](https://github.com/strvmarv/total-recall/commit/14fa5e079bc08bbcc20f89ab6ae20ffacccaf2cb))
* add eval_compare and eval_snapshot MCP tools ([eb9b3df](https://github.com/strvmarv/total-recall/commit/eb9b3df58986abfd4a0c438ddc0a5a8c807c8915))
* add eval_compare and eval_snapshot MCP tools, fix e2e test for expanded corpus ([7723f88](https://github.com/strvmarv/total-recall/commit/7723f888238cd8b396a1585df0475f5a4801f6a2))
* add expected_absent negative assertion to benchmarks ([3adabd8](https://github.com/strvmarv/total-recall/commit/3adabd82bde339adb881e8db433424d52c306200))
* create config snapshot on session_start ([ec1644d](https://github.com/strvmarv/total-recall/commit/ec1644da18f456a748a6bc1fd6256115970147ea))
* expand benchmark corpus to ~100 entries and ~139 queries ([06b205f](https://github.com/strvmarv/total-recall/commit/06b205f3e44773d01ad4bb730f2a9b0c605f6f73))
* log retrieval events with config snapshot ID on memory_search ([07ee872](https://github.com/strvmarv/total-recall/commit/07ee8723a13be75c63ab29f07fa4ba193d4b543e))
* snapshot config before config_set writes ([8197ec2](https://github.com/strvmarv/total-recall/commit/8197ec2d0344353cacbf8570ac2b7f08083aa79b))
* thread config snapshot ID into compaction calls ([1ef6f10](https://github.com/strvmarv/total-recall/commit/1ef6f10ee91657cac44e20cd5c6777a0d1aef24e))

### Bug Fixes

* correct QuerySource type and non-null assertions in comparison test ([b631b25](https://github.com/strvmarv/total-recall/commit/b631b255d0487793aaf0eeaf5dee169de9a1c0f4))

## [0.3.3](https://github.com/strvmarv/total-recall/compare/v0.3.2...v0.3.3) (2026-04-05)

## [0.3.2](https://github.com/strvmarv/total-recall/compare/v0.3.1...v0.3.2) (2026-04-05)

### Bug Fixes

* resolve eval corpus path correctly when running from dist/ bundle ([62dedb0](https://github.com/strvmarv/total-recall/commit/62dedb03580b1db818a150ac57f9b93442ef98da))

## [0.3.1](https://github.com/strvmarv/total-recall/compare/v0.3.0...v0.3.1) (2026-04-05)

## [0.3.0](https://github.com/strvmarv/total-recall/compare/v0.2.6...v0.3.0) (2026-04-05)

### Bug Fixes

* revert skill name to total-recall so /total-recall works as shorthand ([d1299a3](https://github.com/strvmarv/total-recall/commit/d1299a3654c180d3e0849ef3ebc6ed7ebd64dfe6))

## [0.2.6](https://github.com/strvmarv/total-recall/compare/v0.2.5...v0.2.6) (2026-04-05)

## [0.2.5](https://github.com/strvmarv/total-recall/compare/v0.2.4...v0.2.5) (2026-04-05)

### Features

* add /total-recall update subcommand ([67ea185](https://github.com/strvmarv/total-recall/commit/67ea185576270e62c23a6c079785f9ed0a007490))

## [0.2.4](https://github.com/strvmarv/total-recall/compare/v0.2.3...v0.2.4) (2026-04-05)

## [0.2.3](https://github.com/strvmarv/total-recall/compare/v0.2.2...v0.2.3) (2026-04-05)

## [0.2.2](https://github.com/strvmarv/total-recall/compare/v0.2.1...v0.2.2) (2026-04-05)

## [0.2.1](https://github.com/strvmarv/total-recall/compare/v0.2.0...v0.2.1) (2026-04-05)

### Features

* bundle ONNX model via Git LFS for offline plugin installs ([ffb5566](https://github.com/strvmarv/total-recall/commit/ffb55660beb6575a4f0d78dd75887ee81d40f8da))

### Bug Fixes

* launcher falls back to npx when dist/ not present (git clone installs) ([edfad6e](https://github.com/strvmarv/total-recall/commit/edfad6e2c64bd4fe55e9750605069b8b1a2dfb2a))
* track dist/ in git so plugin installs work without npx ([ba47f4e](https://github.com/strvmarv/total-recall/commit/ba47f4e4abe6a845507e9be2f895da8d0505be2d))
* use npx in .mcp.json for plugin MCP server discovery ([78dc47f](https://github.com/strvmarv/total-recall/commit/78dc47f30962a97fbc0f5c038e324d05ca7c778a))

## [0.2.0](https://github.com/strvmarv/total-recall/compare/v0.1.5...v0.2.0) (2026-04-05)

### Features

* bundle ONNX model for offline use, check bundled path before downloading ([ab42055](https://github.com/strvmarv/total-recall/commit/ab42055d92c4b4ac90d8b1c0dcb766af37b7da39))

### Bug Fixes

* add node-finding launcher script for MCP server, works with nvm/fnm/volta/brew ([9115a9b](https://github.com/strvmarv/total-recall/commit/9115a9b2f03b2caa1e2b2823dd1b3e111f812cfa))
* correct HuggingFace model download paths and auto-copy defaults.toml to dist ([69a4a3c](https://github.com/strvmarv/total-recall/commit/69a4a3c4054473bc670067b9ddb60a284d2aa377))
* correct plugin.json schema and add .mcp.json for MCP server discovery ([02a5765](https://github.com/strvmarv/total-recall/commit/02a5765af3dbea6a00bbd377967d6c9e3c920b04))
* restore portable .mcp.json with bash launcher ([db204ec](https://github.com/strvmarv/total-recall/commit/db204ec21c3ff0e972b9bc0130ae93ad3b016d8f))

## [0.1.5](https://github.com/strvmarv/total-recall/compare/v0.1.4...v0.1.5) (2026-04-05)

## [0.1.4](https://github.com/strvmarv/total-recall/compare/v0.1.3...v0.1.4) (2026-04-05)

## [0.1.3](https://github.com/strvmarv/total-recall/compare/v0.1.2...v0.1.3) (2026-04-05)

## [0.1.2](https://github.com/strvmarv/total-recall/compare/v0.1.1...v0.1.2) (2026-04-05)

## [0.1.1](https://github.com/strvmarv/total-recall/compare/dc4460315bc36d553d5bee8dd5230396005b5370...v0.1.1) (2026-04-05)

### Features

* add Claude Code importer with frontmatter parsing and dedup ([8f1263e](https://github.com/strvmarv/total-recall/commit/8f1263ef974930dd67fd26067f9198d4f0cf78da))
* add code-aware chunking parser with multi-language support ([f6a0d58](https://github.com/strvmarv/total-recall/commit/f6a0d5830a9bcdb0a081529e14331a27211121cb))
* add compact, inspect, history, lineage, export, and import MCP tools ([059ddab](https://github.com/strvmarv/total-recall/commit/059ddabbf1bbbde4c4515c0b1bd351e30da76d10))
* add compactor subagent for intelligent hot-to-warm compaction ([3776657](https://github.com/strvmarv/total-recall/commit/37766573acdcb64dae292ccbd59a911af27040b4))
* add Copilot CLI importer for plan.md files ([297ac96](https://github.com/strvmarv/total-recall/commit/297ac9688f566cec679f9aadeaafd436b9a37da7))
* add core memory skill for always-on behavior ([84c1098](https://github.com/strvmarv/total-recall/commit/84c1098e8dec0b4a5c7b1796638eb505e3a2493b))
* add decay score calculation with time, frequency, and type weighting ([0d9f58a](https://github.com/strvmarv/total-recall/commit/0d9f58a082085c2cab180391373990f84f8d6282))
* add entry CRUD operations with project scoping and access tracking ([7af9916](https://github.com/strvmarv/total-recall/commit/7af9916d871462172c2cf95fb4461298aaf50cf3))
* add file and directory ingestion with validation ([e36daa3](https://github.com/strvmarv/total-recall/commit/e36daa30a4955e2b77f508dfc1b5721658c0b46d))
* add hierarchical knowledge base index (collection/document/chunk) ([75db7ee](https://github.com/strvmarv/total-recall/commit/75db7ee8f6034a76bfe90620512fdd67a661a085))
* add hot->warm compaction with decay scoring and event logging ([b1c0326](https://github.com/strvmarv/total-recall/commit/b1c0326d4ab10e89e65802ea22d5689e66e6b603))
* add lazy-loading ONNX embedding engine with model download ([614f7b5](https://github.com/strvmarv/total-recall/commit/614f7b58a98a3dd1c7ef18d892a9f48fc33c3e1e))
* add markdown-aware semantic chunking parser ([24fe9ba](https://github.com/strvmarv/total-recall/commit/24fe9baa7bba81d9fd95f3b364b3f62588fe1529))
* add MCP server with memory and system tool handlers ([71e9320](https://github.com/strvmarv/total-recall/commit/71e932091a6b55a30e14d7f2ddfe8b40fb5bc412))
* add memory store, search, get, update, delete, promote/demote ([e6ac427](https://github.com/strvmarv/total-recall/commit/e6ac427a5723e7e5ded13b868f957543f049542d))
* add npm publishing support and self-install instructions ([7a6b68a](https://github.com/strvmarv/total-recall/commit/7a6b68ab8fe17fe7b020767fc2e056a0e8bd804f))
* add platform manifests for Claude Code, Copilot CLI, Cursor, and OpenCode ([7f41e19](https://github.com/strvmarv/total-recall/commit/7f41e19673c9402a650fe1c7b23a3a52e3b164c5))
* add retrieval event logger with outcome tracking ([a75d199](https://github.com/strvmarv/total-recall/commit/a75d19978989fb444fba1049aba0cdf6921e0726))
* add retrieval metrics computation (precision, hit rate, MRR) ([af11999](https://github.com/strvmarv/total-recall/commit/af11999b599e83aa36a2c1937ba8ca239242492a))
* add search, ingest, status, and forget skills ([3396c67](https://github.com/strvmarv/total-recall/commit/3396c67d7be81c495979582441770a2b8c2e233b))
* add SessionStart/End hooks for Claude Code and Cursor ([6257a58](https://github.com/strvmarv/total-recall/commit/6257a58df512ec6a4aa3d6e16a26013cb7733fd8))
* add shared type definitions for entries, tiers, config ([dc44603](https://github.com/strvmarv/total-recall/commit/dc4460315bc36d553d5bee8dd5230396005b5370))
* add SQLite schema with 6 content tables, vector tables, and system tables ([17da662](https://github.com/strvmarv/total-recall/commit/17da6620b45769b1afd491c10d41a63f4133c34f))
* add synthetic benchmark runner with seed corpus and 20 query pairs ([0b4a4db](https://github.com/strvmarv/total-recall/commit/0b4a4db194cf180a8abcc2192f814437c7547c17))
* add TOML config loading with user override support ([756abd7](https://github.com/strvmarv/total-recall/commit/756abd753e37881b4d0aaa6b901d11765b0451ee))
* add TUI dashboard formatters for status and eval ([83604bd](https://github.com/strvmarv/total-recall/commit/83604bdf29dbac56c079f516963e5e2ce3f534f7))
* add unified chunker with format detection and dispatch ([71f3172](https://github.com/strvmarv/total-recall/commit/71f31725cd92fd0750b29e23d8745155fbcd4abd))
* add vector similarity search with sqlite-vec, multi-tier support ([5924f11](https://github.com/strvmarv/total-recall/commit/5924f111d5c70f9ea46a7fd7cfc8d78bd691c337))
* add warm->cold decay sweep and cold->warm promotion ([4fbf610](https://github.com/strvmarv/total-recall/commit/4fbf610f496c5d9d81988def399b0911be1e9c92))
* register KB, eval, import, and session MCP tools ([837efa5](https://github.com/strvmarv/total-recall/commit/837efa59c69d5e9979f2f371a8cc51cb5f1293f1))

### Bug Fixes

* add integrity verification for downloaded model files ([52f97fe](https://github.com/strvmarv/total-recall/commit/52f97fed7a64f0cbbcf14ec148689c9107a6efe9))
* add MCP input validation and path traversal protection ([1773708](https://github.com/strvmarv/total-recall/commit/1773708dfb7e125c3c67261be4e5a815906ae6ce))
* add missing warm_sweep_interval_days to phase2 e2e compaction config ([e6ff48f](https://github.com/strvmarv/total-recall/commit/e6ff48f10ea43d49b46dacde0398bedede1fdafb))
* delete embedding when deleting memory to prevent orphaned vectors ([57b4bd4](https://github.com/strvmarv/total-recall/commit/57b4bd45c03e479f2c24d14e5da6be1d5ebb03a2))
* harden config traversal, benchmark dedup, error reporting in ingestion ([d731f86](https://github.com/strvmarv/total-recall/commit/d731f8684190abfcc0bf8b5b52821a5f451e6d53))
* make embed function optional in updateMemory when content unchanged ([a31cf7c](https://github.com/strvmarv/total-recall/commit/a31cf7c3b905022966e707aa44a3bec7563edb43))
* prevent ReDoS by replacing regex glob conversion with safe string matching ([11e3e40](https://github.com/strvmarv/total-recall/commit/11e3e403e763d505e4b6501a7f888408adefcf56))
* prevent SQL injection by whitelisting orderBy columns ([6be555f](https://github.com/strvmarv/total-recall/commit/6be555fc8eaf02e15b594fbbc6c02fd4128231ac))
* replace hash-based sync embedding with real async ONNX embeddings ([e66c957](https://github.com/strvmarv/total-recall/commit/e66c9571685ebf81a7e52cfb75c36c19fb810681))
* resolve noUncheckedIndexedAccess errors in e2e tests ([2f018fe](https://github.com/strvmarv/total-recall/commit/2f018fe867a30f2405585c2bd6488545379d0cc6))
