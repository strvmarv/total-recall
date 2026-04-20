# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## 1.0.5 - 2026-04-20

### Fixed

- **Pulled memories now land in the correct local tier.** `SyncService.PullAsync` previously always inserted new memories from Cortex into the local hot tier. It now reads the `tier` field from the pull response and inserts into hot/warm/cold accordingly, so a warm entry on Cortex arrives as warm locally rather than inflating hot. Falls back to hot when the server omits tier (old server compatibility).

## 1.0.4 - 2026-04-20

### Fixed

- **Warm-tier memories no longer silently land in hot on Cortex.** `SyncPayload.Upsert` now includes a `tier` field in the push payload, `SyncEntry` carries it across the wire, and `PluginSyncIngestJob` (cortex-side) routes new entries to the correct tier table and migrates existing entries when their tier has changed. Previously every synced memory—whether created directly in warm or moved there by the local compactor—was ingested into the hot tier on Cortex, defeating compaction and inflating hot counts.
- **Memory upserts no longer starve behind a telemetry backlog.** `SyncQueue.Drain` now orders `memory` items before `usage`, `retrieval`, and `compaction` rows. With a large pre-existing telemetry backlog (e.g. 331 rows observed in the wild), memory writes would wait up to ~35 minutes for the backlog to drain under the old FIFO ordering.

## 1.0.3 - 2026-04-20

### Fixed

- **`memory_store` no longer fails with `KeyNotFoundException` on every call in cortex mode.** The 1.0.2 "best-effort" catch block in `RoutingStore.EnqueueUpsert` logged its own diagnostic via `$"...tier={tier} type={type}..."`, which triggers F# reflection-based DU formatting (`StructuredPrintfImpl`) that isn't AOT-safe and throws its own `KeyNotFoundException` on top of whatever it was trying to log. Swapped the interpolation for the AOT-safe `TierNames.TierName` / `ContentTypeName` helpers. Memory writes now succeed cleanly; any underlying sync-enqueue failure is logged to stderr without re-throwing.
- **AOT-unsafe JSON in remote embedders.** `BedrockEmbedder` and `OpenAiEmbedder` used `JsonSerializer.Serialize`/`Deserialize` with reflection-based options, which emit IL2026/IL3050 warnings and fail at runtime under AOT trimming in the exact same way the sync enqueue did. Routed both through a new `EmbeddingJsonContext` source-gen partial with explicit DTOs (`BedrockEmbedRequest`/`Response`, `OpenAiEmbedRequest`). Build warnings drop from 6 to 0.

## 1.0.2 - 2026-04-20

### Added

- **Skills as a first-class Cortex resource.** Five new MCP tools wired in cortex mode: `skill_search`, `skill_get` (by id or natural key), `skill_list` (with base64 skip cursor), `skill_delete` (owner-scoped), and `skill_import_host` (scans local `~/.claude/skills/` and `<project>/.claude/skills/`, POSTs bundles to cortex). `session_start` now folds per-adapter skill counts (`skillsImported`, `skillsUpdated`, `skillsUnchanged`, `skillsOrphaned`, `skillsErrors`) into the returned `importSummary`, with a 5-second soft timeout and a synthetic error row on failure so session init is never blocked. Plugin-cache paths are intentionally not scanned.
- **Full exception stack traces on tool dispatch failure.** `McpServer` now logs the entire exception chain (type, message, stack, inner) to stderr whenever a tool throws. The wire-level MCP error stays terse, so clients are unaffected, but operators can diagnose throw sites from server logs.

### Fixed

- **`memory_store` no longer reports a write error when the local commit succeeded.** `RoutingStore.EnqueueUpsert` now catches any exception after the local write has committed, logs it to stderr, and returns the committed id to the caller. Previously, a failure in the post-commit sync-queue path (re-reading the row, building the payload, or queuing it) would surface as an MCP error even though the memory was safely persisted locally. The enqueue failure only means the memory won't be pushed to cortex until the next successful write touches the queue.

## 1.0.0 - 2026-04-16

### Added

- **Multi-scope memory and knowledge entries.** New `[scope]` config section with a configurable `default` (defaults to `user:local`). All content tables gained a `scope` column (migration 8) indexed for fast lookup. `memory_store` and `kb_ingest_*` accept an optional `scope` parameter; `memory_search` and `kb_search` accept a `scopes` array to broaden the query (e.g. `["user:paul","global:jira"]`). Scope flows end-to-end through MCP handlers, the SQLite/Postgres stores, the `EntryDto` wire format, sync push/pull, and the CortexClient so plugin-mode and cortex-mode both honor it. The remote `kb_search` path forwards scopes to Cortex as a comma-separated query param.

## 0.9.7 - 2026-04-13

### Added

- **Periodic background sync in cortex mode.** Pull + flush runs every 300 seconds between `session_start` and `session_end`, keeping multi-device memories fresh mid-session. Configurable via `sync_interval_seconds` in `[cortex]` config; set to `0` to disable.
- **Storage mode in `session_start` response.** New `storage` field reports the effective backend (`sqlite`, `postgres`, `cortex`) and flags fallbacks (e.g. `sqlite (cortex failed)`).

### Fixed

- **Cortex/Postgres startup crash on bad config.** `OpenCortex` and `OpenPostgres` now validate config before accessing F# option values, throwing a clear `InvalidOperationException` instead of `NullReferenceException`. Cortex/Postgres failures fall back to SQLite gracefully.
- **Flaky `OverlappingTicks_AreSkipped` test.** Widened timing tolerances and switched to `Interlocked` counter.

### Changed

- Skill prompts (`using-total-recall`, `commands`) updated to display storage mode in the session status line.

## 0.9.6 - 2026-04-13

### Fixed

- **Tags coercion in MCP handlers.** `memory_store` and `memory_update` now accept tags as a native array, a JSON-encoded array string, or a comma-separated string. Fixes failures when MCP clients serialize arrays as strings.

### Changed

- Extracted shared `ArgumentParsing.ReadTags` helper, replacing duplicate private methods in `MemoryStoreHandler` and `MemoryUpdateHandler`.

## 0.9.4 - 2026-04-12

### Fixed

- **AOT-safe `CortexClient`.** Replaced reflection-based `System.Net.Http.Json` calls with source-generated `SyncJsonContext` serialization, fixing NativeAOT publish.

### Added

- **`SyncService` wired into session lifecycle.** `session_start` pulls from Cortex and flushes the pending sync queue; `session_end` flushes remaining queued items.
- **KB search routes to Cortex remote backend** in cortex mode, with graceful degradation when the Cortex endpoint is offline.
- **Knowledge search and status endpoints** added to the Cortex plugin sync controller.
- **Cortex ingest job retries failed items** with a 24-hour stale threshold; tags returned as array in KB search results.

## 0.9.3 - 2026-04-12

### Fixed

- **AOT serialization crash in `RoutingStore`.** Replaced `JsonSerializer.Serialize(new { ... })` with manual JSON builders (`SyncPayload`) and source-generated `SyncJsonContext`, eliminating reflection at runtime.

### Changed

- **Version synced across all 5 manifest files** (`package.json`, `package-lock.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json`).

## 0.9.2 - 2026-04-12

### Fixed

- **`.mcp.json` MCP server registration.** Restored the server entry that was emptied in `b933ed1`, which broke plugin MCP server discovery for all users.

## 0.9.1 - 2026-04-12

**Cortex Connection.** Hybrid local+remote storage mode connecting the plugin to Total Recall Cortex. Three storage modes (`local`, `postgres`, `cortex`) selected via `[storage] mode` config.

### Added

- **`RoutingStore` — local-first `IStore` wrapper** that syncs user memories to Cortex. Reads and writes hit local SQLite for speed; mutations are enqueued for background push to Cortex and periodic pulls merge remote changes back.

- **`CortexClient` — HTTP client for Cortex REST API** with PAT authentication. Handles memory CRUD, global knowledge queries, telemetry push, and health checks against the Cortex backend.

- **`SyncQueue` — persistent SQLite-backed outbound queue** surviving crashes. Outbound mutations (store, update, delete) are written to a local `sync_queue` table and drained in order by `SyncService`. Guarantees at-least-once delivery even across unclean shutdowns.

- **`SyncService` — orchestrates pull (session start), periodic flush, and telemetry push.** Pulls remote changes on `session_start`, flushes the outbound queue on a configurable interval, and pushes usage events, retrieval events, and compaction log entries to Cortex for unified dashboards.

- **Content-only sync — no vectors cross the wire.** Each side re-embeds independently: the plugin uses the bundled ONNX model (all-MiniLM-L6-v2, 384 dimensions) while Cortex uses Cohere Embed v4 (1024 dimensions). Only text content and metadata are synced.

- **Graceful degradation — Cortex unavailable = local-only.** If the Cortex endpoint is unreachable, the plugin continues working against local SQLite with no user-visible impact. The `SyncQueue` buffers outbound changes and `SyncService` catches up automatically when connectivity is restored.

- **Sync payload: user memories (bidirectional), usage events, retrieval events, compaction log (push-only).** User memories sync both ways for seamless multi-device and team sharing. Telemetry data flows one-way to Cortex for centralized analytics.

- **New `[cortex]` config section** with `url` and `pat` fields. Environment variable overrides: `TOTAL_RECALL_CORTEX_URL` and `TOTAL_RECALL_CORTEX_PAT`.

- **Migration #7 — `sync_queue` table.** New SQLite table `sync_queue` with columns for operation type, payload, status, retry count, and timestamps. Supports the persistent outbound queue for crash-resilient sync.

## 0.9.0 - 2026-04-12

**Token usage tracking (Phases 1 + 2).** Host-neutral telemetry pipeline that ingests coding-assistant transcripts, records per-turn token usage, aggregates old data into a daily rollup, and exposes the result via a `usage` CLI verb and a `usage_status` MCP tool. Supports Claude Code and Copilot CLI. Additive-only: usage ingestion is best-effort and never blocks `session_start`. Token columns are nullable end-to-end to preserve fidelity differences between hosts (Claude Code full vs. Copilot CLI output-only).

### Added

- **Migration #6 — usage telemetry schema.** New tables `usage_events` (raw per-turn events, 30-day retention via future rollup), `usage_daily` (forever-aggregated rollups), and `usage_watermarks` (per-host scan state). All token columns nullable — `null` means "we don't know," distinct from `0`.

- **`UsageEvent` record + `IUsageImporter` interface.** Host-neutral event shape and pure streaming adapter contract. Host-specific importers translate transcripts into the common shape; writer path is centralized in `UsageEventLog`.

- **`UsageEventLog` writer/reader.** `INSERT OR IGNORE` on `(host, host_event_id)` UNIQUE key for idempotent re-scans. Mirrors the existing `RetrievalEventLog` / `CompactionLog` pattern.

- **`UsageWatermarkStore`.** Per-host `(last_indexed_ts, last_rollup_at)` watermark tracking for incremental scans. Unknown hosts return 0; the indexer then applies the config-driven `initial_backfill_days` bound.

- **`ClaudeCodeUsageImporter`.** Pure streaming parser for `~/.claude/projects` transcripts. Emits only assistant turns with `message.usage` present; skips user messages, malformed lines, and tool-only records. Populates all Anthropic usage fields including the 5-minute / 1-hour cache-creation split.

- **`UsageIndexer` orchestrator.** Iterates registered `IUsageImporter`s, streams events to `UsageEventLog`, advances watermarks on success. Per-host failure isolation: a failing importer is logged via `ExceptionLogger.LogChain` and skipped without blocking other hosts or advancing its own watermark. Matches the existing `SessionLifecycle` resilience policy.

- **`UsageQueryService` read layer.** Single read path for CLI, MCP tools, and future quota-nudge composer. Supports group-by `host` / `project` / `day` / `model` / `session`, host and project filters, `TopN`, and coverage counts (full vs. partial token data). Null handling honors "we don't know" ≠ "zero."

- **`total-recall usage` CLI verb.** `usage [--last 5h|1d|7d|30d|90d|all] [--by host|project|day|model|session] [--host H] [--project P] [--top N] [--json]`. Fixed-width table output (no Spectre.Console dependency for testability), em-dash for null token columns, `tracked at token granularity` footer. `--json` emits a stable machine-readable shape `{query, buckets, grand_total, coverage}` via the shared `JsonWriter` emitter — null token fields serialize as JSON `null`, never `0`.

- **`CopilotCliUsageImporter`.** Streaming parser for `~/.copilot/session-state/<session>/events.jsonl`. Emits one `UsageEvent` per `assistant.message`. Handles mid-session repo / branch switches via `session.context_changed`. Model attribution comes from the most recent `tool.execution_complete`. Copilot CLI does not expose Anthropic-style input or cache tokens, so those fields stay null per the unified-schema / optional-fields design.

- **`UsageDailyRollup`.** Rolling aggregation that, once per 24h, compacts `usage_events` older than the retention cutoff (default 30 days) into `usage_daily` and deletes the source rows. The full operation runs inside a single `IMMEDIATE` transaction so the reserved lock is taken before the initial `SELECT COUNT(*)` — concurrent writers cannot slip an event past the aggregation window and get wiped by the trailing `DELETE`. Idempotent via `INSERT OR REPLACE` on the composite primary key `(day_utc, host, model, project)`.

- **`UsageQueryService` raw + daily union.** The read layer now queries a `WITH unioned AS (...)` CTE that UNION ALLs `usage_events` with `usage_daily`, so long-window queries (e.g., `--last 90d`) see the full history across the rollup boundary. Grand total and per-bucket aggregates read from the union. Coverage counts intentionally stay on raw events only, with a documented caveat: `usage_daily` rows have `session_id = NULL` so `COUNT(DISTINCT session_id)` would undercount for rolled-up periods.

- **`usage_status` MCP tool.** Agents can now query their own token burn mid-session. Input schema mirrors the CLI flags (`window`, `group_by`, `host`, `project`, `top`) so LLM and human usage stay consistent. Emits the same JSON shape as `total-recall usage --json` via a shared `UsageJsonRenderer` used by both code paths.

- **`UsageJsonRenderer`.** New shared JSON emitter in `TotalRecall.Infrastructure.Usage` used by both `UsageCommand` and `UsageStatusHandler`. Delegates escaping and number formatting to the existing `TotalRecall.Infrastructure.Json.JsonWriter` helper (matches the pattern used by ~10 other CLI commands), so the JSON output is byte-identical across the CLI and MCP paths.

### Changed

- **`SessionLifecycle.RunInit` runs the usage indexer before the existing importer sweep.** Failures are caught and logged but never block `session_start` — usage tracking is additive and must not appear in the critical path.

- **`ServerComposition.OpenProduction` constructs the indexer, rollup, and query service** with `ClaudeCodeUsageImporter` + `CopilotCliUsageImporter` and passes them to `SessionLifecycle`. Registers the `usage_status` MCP tool handler. `CliApp` registers `UsageCommand` so `total-recall usage` is reachable from the CLI entry point.

- **`ClaudeCodeUsageImporter` uses per-record `cwd` / `gitBranch` for project attribution.** Previously derived the project path from the encoded directory name (`-Users-strvmarv-source-total-recall`) via a reverse `-` → `/` map, which destroyed real hyphens in folder names (`total-recall` decoded to `total/recall`). The JSONL records themselves carry authoritative `cwd` and `gitBranch` fields on nearly every line type — the importer now maintains a running `lastKnownCwd` / `lastKnownGitBranch` updated from every record that carries them. `DecodeProjectDirName` stays as the fallback when no cwd-carrying record appears before the first assistant turn.

- **`ClaudeCodeUsageImporter` skips `model == "<synthetic>"` records.** Claude Code emits synthetic marker messages for internal protocol events (compaction boundaries, session metadata). These are not real LLM usage and were polluting the `--by model` report with a ghost bucket of 0 / 0 / 0 tokens. Filtered out alongside the existing missing-usage check. Real events with unknown model attribution (null `model`) are still counted — they represent genuine usage with missing metadata, not synthetic markers.

- **`UsageQueryService` and `UsageDailyRollup` preserve null `cache_creation_tokens`.** Both previously ran `COALESCE(cache_creation_5m, 0) + COALESCE(cache_creation_1h, 0)` at the per-row level, which erased the "we don't know" signal when an entire bucket came from a host that never populates those fields. Both now use a `CASE WHEN cache_creation_5m IS NULL AND cache_creation_1h IS NULL THEN NULL ELSE ... END` wrapper so the outer `SUM` correctly returns null for all-null buckets.

### Notes

- Historical (pre-installation) transcripts are picked up on the first `session_start` after upgrade, bounded by `initial_backfill_days`.
- Phase 3 of the spec (quota nudging — plan registry, evaluator, and `session_start` two-line nudge composer) is designed but not yet implemented.

## 0.8.1 - 2026-04-10

**Enterprise backend support.** The same binary now supports either local SQLite + bundled ONNX (default, unchanged) or remote Postgres/pgvector + remote HTTP embedder, selected by configuration. Adds multi-user ownership and visibility primitives, a one-time migration tool, and three embedding provider options.

### Added

- **Postgres/pgvector backend.** Set `[storage] connection_string` in `config.toml` to switch from SQLite to Postgres. Uses a two-table schema (`memories` + `knowledge`) with tier as a column, HNSW vector indexes for cosine similarity, generated `tsvector` columns with GIN indexes for full-text search, and `owner_id`/`visibility` columns for multi-user scoping. Schema migrations run automatically on first connection.

- **Remote embedding providers.** Set `[embedding] provider` to `"openai"` or `"bedrock"` to use a remote text embedder instead of the bundled ONNX model. OpenAI-compatible endpoints (`/v1/embeddings` contract — covers OpenAI, Azure OpenAI, vLLM, TEI) and Amazon Bedrock (Cohere Embed v4 via raw HTTP, no AWS SDK) are supported. API key resolves from config or `TOTAL_RECALL_EMBEDDING_API_KEY` env var. Configurable `dimensions` field (default 384) controls vector size for all backends.

- **Multi-user ownership and visibility.** Postgres entries carry `owner_id` (injected from `[user] user_id` config or `TOTAL_RECALL_USER_ID` env var) and `visibility` (`private`, `team`, `public`). New `scope` parameter on `memory_search` and `kb_search` (`mine`, `team`, `all`) controls query scoping. New `visibility` parameter on `memory_store` sets entry visibility. SQLite path is unaffected — single-user, all entries local.

- **`migrate_to_remote` MCP tool.** One-time migration from local SQLite to a configured remote Postgres. Re-embeds every entry with the configured remote embedder (since dimensions/model typically differ). Supports `dry_run`, `include_knowledge` (skip KB chunks if team will re-ingest from source), and `visibility` (share entries on migration). Idempotent — skips entries whose id already exists in the target. Also available as `total-recall migrate` CLI command.

- **`Pgvector` and `Npgsql` dependencies** added to `TotalRecall.Infrastructure` for the Postgres path.

### Changed

- **`ISqliteStore` renamed to `IStore`.** The storage interface is now backend-neutral. `GetRowid` renamed to `GetInternalKey`. All 33 MCP tool handlers compile against `IStore` — no handler changes required to switch backends. `SqliteStore` and the new `PostgresStore` both implement `IStore`.

- **`EmbedderFactory` gains `CreateFromConfig`.** Selects `OnnxEmbedder` (local), `OpenAiEmbedder`, or `BedrockEmbedder` based on the `[embedding]` config section. The existing `CreateProduction()` method is unchanged and used as the local fallback.

- **`ServerComposition.OpenProduction` now selects backend from config.** Connection string present → Postgres path. Absent → SQLite path (identical to 0.8.0 behavior). The composition root is the single decision point; all downstream code is backend-agnostic.

- **`HierarchicalIndex` and `IngestValidator` no longer take `MsSqliteConnection`.** Both classes now work through `IStore` and `IVectorSearch`, making KB ingestion functional against both SQLite and Postgres backends. `ListEntriesOpts` gains a `ParentId` filter to support the `GetDocumentChunks` query without raw SQL.

- **`FakeSqliteStore` test double renamed to `FakeStore`** to match the `IStore` interface rename.

- **Config types extended.** `EmbeddingConfig` gains optional `Provider`, `Endpoint`, `BedrockRegion`, `BedrockModel`, `ModelName`, `ApiKey` fields. New `StorageConfig` (optional `ConnectionString`) and `UserConfig` (optional `UserId`) sections. All new fields are optional — existing configs work without changes.

### New files

| File | Purpose |
|------|---------|
| `PostgresMigrationRunner.cs` | Postgres schema migrations (two-table layout, pgvector, FTS) |
| `PostgresStore.cs` | `IStore` implementation for Postgres |
| `PgvectorSearch.cs` | `IVectorSearch` implementation using pgvector `<=>` operator |
| `PostgresFtsSearch.cs` | `IFtsSearch` implementation using tsvector/tsquery |
| `OpenAiEmbedder.cs` | `IEmbedder` for OpenAI-compatible `/v1/embeddings` APIs |
| `BedrockEmbedder.cs` | `IEmbedder` for Amazon Bedrock (Cohere Embed) |
| `PostgresCompactionLog.cs` | Compaction log backed by Postgres |
| `PostgresImportLog.cs` | Import log backed by Postgres (with `IImportLog` interface) |
| `MigrateToRemoteHandler.cs` | MCP tool for SQLite → Postgres migration |

## 0.8.0 - 2026-04-09

**General availability of the .NET-native rewrite.** 0.8.0 is the cutover from the TypeScript/bun runtime (0.7.x and earlier) to a single .NET 8 AOT binary per platform, distributed via the existing `@strvmarv/total-recall` npm package and the `strvmarv/total-recall-marketplace` plugin marketplace. The end-user install command is unchanged; the plugin manifest, MCP tool surface, skill, and hook contracts are preserved so upgrading is a drop-in replacement. The 9 beta cuts (`0.8.0-beta.1` through `0.8.0-beta.7`) document the full shape of the cutover — this GA entry summarizes the highlights and folds in one GA-blocking fix that was found during pre-release dogfood.

### Changed

- **Runtime: TypeScript on bun → .NET 8 AOT binary.** Every `src-ts/`, `bin-ts/`, `dist/`, and `tests-ts/` path was deleted in `0.8.0-beta.1`. The MCP server, CLI, embedding pipeline, sqlite-vec bridge, and migration tooling now live under `src/TotalRecall.{Cli,Core,Host,Infrastructure,Server}/` and ship as a pre-built single-file AOT executable per RID (`linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`). The `bun:sqlite` runtime dependency is gone; `Microsoft.Data.Sqlite` + a bundled `sqlite-vec` native per RID replaces it. `package.json` is reduced to a minimal `bin`/`files`/`scripts` shell whose `bin/start.js` launcher dispatches to the per-platform executable via `child_process.spawn`.

- **Distribution: bun-runtime download → prebuilt binaries in the npm tarball.** The 0.6.8-era `scripts/postinstall.js` that downloaded a ~150MB bun runtime on every `npm install` is gone. The new `scripts/postinstall.js` (~20 lines) calls `ensureBinary()` once at install time, which resolves the host RID and either (a) extracts the matching pre-staged binary from the npm tarball or (b) downloads the matching per-RID GitHub Release asset (`total-recall-<rid>.tar.gz`) on first launch. Install and first-run work under `--ignore-scripts`, offline, and corporate-firewall conditions — the launcher's `ensureBinary()` retry at first spawn is the safety net.

- **Release pipeline: `ci.yml` + `publish.yml` → `release.yml` matrix.** A single 5-leg matrix (`ubuntu-latest` x64, `ubuntu-24.04-arm`, `macos-14`, `windows-latest`) builds the AOT binary per RID, stages per-RID `.tar.gz` archives, attaches them to the GitHub Release, and publishes the npm tarball. Prerelease tags (`v<x>-beta.N`, `v<x>-rc.N`) publish to the matching npm dist-tag instead of `@latest`; stable tags publish to `@latest`. The workflow runs entirely on GitHub-hosted runners — no self-hosted toolchain, no cross-compile dance.

- **Migration guard: stateless marker check → explicit 5-state machine.** `src/TotalRecall.Server/AutoMigrationGuard.cs` classifies the on-disk state into one of five DbFormats (`NotPresent`, `EmptyFile`, `TsFormat`, `PartialNet*`, `NetMigrated`), drives the TS→.NET migration from the `.ts-backup` source, and handles resume-from-partial-state scenarios without ever deleting user data. Every retried-failure path renames suspect files to `<dbPath>.failed-migration-<utc>` so unique data is always recoverable by hand even when the guard auto-resumes.

### Fixed

- **Guard self-heals fully-migrated .NET DBs missing the Plan 7 marker.** Pre-Plan-7-Task-7.-1 builds (0.6.7-era) produced real .NET DBs with `_schema_version` populated and all 6 content tables present but never stamped the `migration_from_ts_complete` marker in `_meta`. Every 0.8.0 beta through `beta.7` hard-failed on first open against these DBs, printing a manual `sqlite3 INSERT` recovery recipe. Two dogfood users hit this on two different machines. The guard now distinguishes "complete .NET schema missing only the marker" from "truly partial schema mid-migration" via a new `IsCompleteNetSchema` read-only check: when all 6 canonical content tables exist and `_schema_version` has at least one row, the guard stamps the marker in place and returns `AlreadyMigrated` instead of bailing. The manual-recipe bail still fires for the narrow "truly partial schema" case (some content tables missing, typically a rolled-back init from a build without the outer BEGIN wrapper). Two new `AutoMigrationGuardTests` cases exercise the self-heal path end-to-end using the real `MigrationRunner.RunMigrations` as the seed factory. Test count: 946 (up from 944 in beta.7).

### Reference

- Full per-beta detail in the `0.8.0-beta.1` through `0.8.0-beta.7` entries below — known-issue triage, state-machine transition table, `release.yml` matrix evolution, `sqlite-vec` per-RID lockfile fix, archive format unification (`.tar.gz` across all RIDs), and per-commit credit notes.

## 0.8.0-beta.7 - 2026-04-09

### Fixed

- **Migration guard cliff: `total-recall.db` and `total-recall.db.ts-backup` both present.** Beta tester upgrading through 0.8.0-beta.3..6 hit a hard dead-end on beta.6 startup: `migration failed: could not rename old database: The file '~/.total-recall/total-recall.db.ts-backup' already exists.` Reproduction: an earlier beta run (likely beta.3 or beta.4 before the AOT crash classes were fixed) made it past the rename phase, leaving a 5.4 MB `total-recall.db.ts-backup` with the user's real TS-era data. A subsequent step then created a fresh ~12 KB `total-recall.db` (either a partial init from a rolled-back transaction, or an empty SQLite shell from a failed `MigrationRunner.RunMigrations`). On the next startup the guard saw both files and the bare `File.Move(dbPath, backupPath)` threw `IOException` because the target already existed. The user was stuck — recovery required hand-editing files in `~/.total-recall/`.

  Root cause: the previous `CheckAndMigrateAsync` state machine modeled only two pre-migration states (fresh-TS-needs-migration vs. already-migrated). The real state space is **five**: `NotPresent`, `EmptyFile`, `TsFormat`, `PartialNetEmpty`, `PartialNetPopulated`, plus the steady-state `NetMigrated`. The guard had no concept of "the previous attempt got partway and left both files behind" and treated any unrecognized state as a fatal collision.

  Fix: refactor `AutoMigrationGuard.CheckAndMigrateAsync` into an explicit state machine driven by a new read-only `InspectDbFormat()` helper. The full transition table lives in the class xmldoc; key invariants:

  - **Never delete anything.** Suspect files are renamed to `<dbPath>.failed-migration-<utc>` so unique data is recoverable by hand even after the guard auto-resumes.
  - **The `.ts-backup` is treated as authoritative.** The guard never creates a backup except by renaming `dbPath`, so its existence is *proof* that `dbPath` was once renamed there. If a fresh `dbPath` shows up alongside an existing backup, the backup wins.
  - **Inspection is read-only.** The previous `TryReadMarker` silently `CREATE TABLE IF NOT EXISTS _meta`-d on every peek, mutating TS-era DBs even on no-op runs. The new `InspectDbFormat` opens with `Mode=ReadOnly` so the file is never touched until the state machine decides to act.

  6 new test cases extend the existing 6 to cover all 5 transitions (`Resume_TsAtDb_BackupExists_*`, `Resume_PartialNetEmpty_*`, `Resume_PartialNetPopulated_*`, `Resume_NoDb_BackupOnly_*`, `EmptyShellAtDb_BackupExists_*`). Each test asserts the right `GuardResult`, the right migrator invocation, the marker presence, and that any sidelined file is still on disk via a new `FailedMigrationSidelineCount()` helper. Total project test count: 944 (up from 938).

- **`total-recall-win-x64.zip` was actually a POSIX tar archive.** Verified after the v0.8.0-beta.6 release pipeline went green: `file release-assets/total-recall-win-x64.zip` reported `POSIX tar archive (GNU)`. Root cause: `release.yml`'s win-x64 staging step used `tar -C binaries/win-x64 -a -cf release-assets/total-recall-win-x64.zip .` and asserted in a comment that `bsdtar -a auto-detects the format from the extension, producing a standard .zip`. That's true on macOS where `tar` is bsdtar/libarchive — but the publish job runs on `ubuntu-latest` where `tar` is **GNU tar**, whose `-a`/`--auto-compress` selects a *compression program* from the suffix (gzip/bzip2/xz), not an *archive format*. For `.zip` it falls through to plain uncompressed tar. Result: a tar file with a misleading `.zip` extension. Windows users using Explorer's built-in zip handling, 7-Zip without auto-detection, etc. would fail with "not a zip file"; only Windows tar.exe (bsdtar) handled the misnamed file successfully via libarchive's format auto-detect.

  Fix: switch the win-x64 leg to `.tar.gz` like every other RID. `release.yml`'s `Stage per-RID release assets` step now produces `total-recall-win-x64.tar.gz` via `tar -C binaries/win-x64 -czf release-assets/total-recall-win-x64.tar.gz .`, and the `Attach archives to GitHub release` files list updated accordingly. `scripts/fetch-binary.js`'s `getArchiveName()` now always returns `.tar.gz` regardless of RID, and `extractArchive()` collapses to a single `tar -xzf` invocation — no more isZip branch, no more `Expand-Archive` PowerShell fallback. Windows 10+ ships `tar.exe` (bsdtar/libarchive) since build 17063 / 1803 (April 2018), which handles `.tar.gz` natively. Single archive format, single extraction code path, simpler than what we had.

### Credits

- Migration guard fix delivered by the Mac dogfood agent (commit `7ce54db`). The fix was triggered by the user's actual recovery experience — they had to manually `mv ~/.total-recall/total-recall.db ~/.total-recall/total-recall.db.failed-migration-cruft && mv ~/.total-recall/total-recall.db.ts-backup ~/.total-recall/total-recall.db` to unblock beta.6. This commit makes that recovery automatic.
- `total-recall-win-x64.zip` archive bug discovered by the build agent during post-tag verification of v0.8.0-beta.6 (downloading and inspecting the live release asset via `file`). Mac agent's commit `b5564a3` introduced the bug; tested locally on macOS where bsdtar is the default `tar` and didn't surface the GNU tar / bsdtar divergence. Fixed in this commit by unifying on `.tar.gz` for all RIDs.
- Build agent verified on linux-x64 before bumping: `dotnet build` 0/0, `dotnet test` 944 passing (up from 938 with the new guard tests), happy-path AOT publish produces `runtimes/vec0.so`, tar round-trip on the new win-x64 path produces real `gzip compressed data` (verified via `file`) that extracts cleanly with `runtimes/vec0.so` in place.

## 0.8.0-beta.6 - 2026-04-09

### Fixed

- **`sqlite-vec` native library missing from non-linux-x64 publish trees.** Beta tester on macOS reported `sqlite-vec native library not found at .../runtimes/vec0.dylib` on first DB open, even though the v0.8.0-beta.5 archive download and extraction (Fix A from beta.5) worked correctly. Root cause: `package-lock.json` was originally regenerated on a linux-x64 host (in the `0.8.0-beta.1` Task 3 strip commit `4394138`) using a plain `npm install` that only resolved `sqlite-vec`'s `optionalDependencies` for the host platform. The other four RID variants (`sqlite-vec-linux-arm64`, `sqlite-vec-darwin-x64`, `sqlite-vec-darwin-arm64`, `sqlite-vec-windows-x64`) appeared as bare names in the parent's `optionalDependencies` block but had no resolution / integrity entries of their own. Result: `npm ci` on any non-linux-x64 CI matrix leg never installed the matching variant, the Infrastructure csproj's `<Content Include="..." Condition="Exists(...)">` copy step silently no-op'd, and the publish tree shipped without `runtimes/vec0.<ext>`. This is the textbook **npm optional-dependency platform-locked lockfile** footgun and had been latent on every CI matrix leg other than linux-x64 since beta.1; it only became visible at runtime now that beta.5's archive download/extract path was working correctly enough to expose what was missing inside the archive.

  Fix: regenerated `package-lock.json` with `npm install --package-lock-only --force --os=darwin --cpu=arm64`. The `--force` flag tells npm 11 to fully resolve every optional dep variant in one pass. The new lockfile contains per-RID entries for all 5 variants with correct os/cpu metadata, integrity hashes, and resolved URLs. `npm ci` on each platform still installs only the matching variant (verified locally on linux-x64: installs exactly `sqlite-vec` + `sqlite-vec-linux-x64`). Side effect: regeneration also pruned 299 orphan TypeScript-era lockfile entries (`@babel/*`, `@types/*`, `esbuild`, `vitest`, `@modelcontextprotocol/sdk`, etc.) that survived the original 0.8.0-beta.1 strip. Lockfile shrunk from 3995 lines to 107.

- **Silent failures when sqlite-vec is missing from a publish tree.** New `<Target Name="VerifyVecExtensionPublished" AfterTargets="Publish">` in `src/TotalRecall.Host/TotalRecall.Host.csproj` checks for `runtimes/vec0.{so,dylib,dll}` after publish completes and emits an MSBuild `<Error>` if none are found. The error message names the active RuntimeIdentifier and includes explicit fix instructions (`run npm ci, verify package-lock.json contains a per-RID entry for your target`). Verified locally on linux-x64: happy-path publish with `vec0.so` present exits 0; negative-path publish with `node_modules/sqlite-vec-linux-x64` hidden exits 1 with the new diagnostic. If beta.5's CI run had this target in place, the build agent would have seen a clear failure at the publish step instead of shipping a tarball that crashed on first DB open.

### Known limitations

- Fix A regenerated the lockfile from the Mac agent's machine, which had npm 11.11.0 installed. Older npm versions may not honor `--force` the same way, and contributors regenerating the lockfile in the future need to either use npm 11+ or pass `--os` / `--cpu` flags to manually populate each RID entry. A `docs/TODO.md` follow-up should add a `scripts/regenerate-lockfile.sh` wrapper that captures the right invocation, plus a CI check that verifies the lockfile still has all 5 RID entries.
- Fix B (an explicit `npm install --no-save sqlite-vec-<rid>` step in each release.yml matrix leg as a belt-and-suspenders safety net) was intentionally skipped — Fix A removes the root cause and Fix C catches any regression strictly more generally. If Fix A ever regresses (someone commits a single-platform lockfile), Fix C will fail the publish step with a clear error and the bisect points straight at the regressing commit.

### Credits

- Diagnosis and fixes delivered by the Mac dogfood agent (commit `0386443`). Build agent verified on linux-x64: `dotnet build` 0/0, `dotnet test` 938 passing, `dotnet publish -r linux-x64` happy-path produces `runtimes/vec0.so`, negative-path test (with `sqlite-vec-linux-x64` hidden from node_modules) correctly fails with exit code 1 and the new diagnostic message naming the missing RID. Lockfile structure verified: 5 sqlite-vec entries with correct os/cpu metadata, `npm ci` installs only the host-matching variant.

## 0.8.0-beta.5 - 2026-04-09

### Fixed

- **AOT publish tree shipped as compressed archive instead of bare executable.** v0.8.0-beta.4 attached only the executable to the GitHub Release per RID, but the .NET AOT binary P/Invokes into sibling native libraries (`libonnxruntime.{dylib,so,dll}` via `Microsoft.ML.OnnxRuntime` and `vec0.*` via sqlite-vec) that live next to it in the publish tree. Every fresh `claude /plugin update` install on a `source: github` marketplace entry crashed at first DB open with `TypeInitializationException` -> `DllNotFoundException: libonnxruntime.dylib`. The npm tarball install path was unaffected because the tarball already shipped the full tree. `release.yml` now stages each per-RID publish tree into `total-recall-<rid>.tar.gz` (Unix) or `total-recall-<rid>.zip` (Windows) and attaches the archives as Release assets. `scripts/fetch-binary.js` downloads the matching archive into `os.tmpdir()`, extracts it via system `tar` (or `tar.exe` / `Expand-Archive` on Windows), verifies the expected executable, and restores the +x bit.
- **Opaque exception messages at boundary catches.** Beta tester sessions were blocked for ~30 minutes by `migration guard threw: A type initializer threw an exception` because Program.cs's migration-guard catch wrote only `ex.Message` — the real `DllNotFoundException` naming the missing library was buried in `ex.InnerException`. New `src/TotalRecall.Infrastructure/Diagnostics/ExceptionLogger.cs` provides `LogChain(prefix, ex)` that writes the outer exception type+message, walks the entire `InnerException` chain with indented `-> <Type>: <Message>` lines, then the outer stack trace. AOT-safe (uses the first-class `InnerException` property, not reflection). Retrofitted 10 boundary catches across `Program.cs` (migration guard + composition) and the CLI commands (`StatusCommand`, `Memory/{History,Inspect,Export,Lineage}Command`, `Kb/{List,Refresh,Remove}Command`).
- **`total-recall --version` reported `0.1.0` regardless of build version.** `CliApp.cs` had `private const string AppVersion = "0.1.0"` baked in at compile time. New `ResolveAppVersion()` walks `Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()` at runtime, strips any SourceLink `+<sha>` suffix, falls back to `Assembly.Version.ToString(3)`, then `"unknown"`. AOT-safe — assembly attributes are metadata and survive trimming. `release.yml`'s `Publish (Unix/Windows)` steps now pass `-p:Version=${REF_NAME#v}` and `-p:InformationalVersion=${REF_NAME#v}` to `dotnet publish` so CI builds carry the tag string. `REF_NAME` is passed via `env:` and dereferenced as `"$REF_NAME"` per GitHub Actions script-injection guidance.
- **Stale `git-hooks/pre-commit` ran `npm run build` on src/ commits.** Leftover from the TypeScript era when `tsup` built `dist/index.js` from `src/`. After the 0.8.0 strip, `npm run build` no longer exists in `package.json`, so any commit touching `src/TotalRecall.*.cs` would fail the hook. Replaced with a no-op + comment explaining why (and how to run `dotnet build` manually for a local fast-feedback loop).

### Known limitations

- `src/TotalRecall.Server/McpServer.cs` still has `private const string ServerVersion = "0.1.0"`, reported in the MCP `initialize` response (`serverInfo.version`). Moving it to the same dynamic lookup as `CliApp.AppVersion` requires updating `tests/TotalRecall.Server.Tests/McpServerTests.cs:76` which asserts the exact string. Deferred to a follow-up PR.
- Five additional catch sites in `Memory/{Promote,Demote,Import}Command`, `MigrateCommand`, `ImportHostCommand` were not retrofitted with `ExceptionLogger.LogChain` because no concrete failure was observed there. Same pattern applies; can be propagated later if a real failure surfaces.

### Credits

- Diagnosis and fixes A/B/C delivered by the Mac dogfood agent. Build agent verified `dotnet build` (0/0), `dotnet test` (938 passing), `dotnet publish -r linux-x64 --aot` with the new `-p:Version` flag (binary reports correct version, sibling natives load), and the tar round-trip shape before tagging.

## 0.8.0-beta.4 - 2026-04-09

### Fixed

- **Install-path gap for git-clone installs.** When a host tool fetches total-recall via `git clone` rather than `npm install` (e.g. Claude Code `/plugin update` against a marketplace entry whose `source` is `github`), the installed tree has no `binaries/` because prebuilt binaries are never committed to git. `bin/start.js` then failed with "Prebuilt binary not found for &lt;rid&gt;". Diagnosis confirmed against authoritative Claude Code docs.
- **Per-RID GitHub Release asset naming.** `softprops/action-gh-release` attached files by basename, so four binaries all named `total-recall` collided down to ~2 assets on the GitHub Release (observed on v0.8.0-beta.3 — only 2 of 4 platforms attached). `release.yml` now stages copies into `release-assets/` with per-RID names (`total-recall-linux-x64`, `total-recall-linux-arm64`, `total-recall-osx-arm64`, `total-recall-win-x64.exe`) before attaching. `fail_on_unmatched_files: true` turns a missing asset into a hard error.
- **Prerelease badge on GitHub Releases.** Previously every release was published with `prerelease: false`, so beta tags appeared as stable releases. `release.yml` now sets `prerelease: ${{ contains(github.ref_name, '-') }}` so any tag with a hyphen (e.g. `v0.8.0-beta.4`) is marked prerelease automatically.
- **Stale `.opencode/INSTALL.md`.** The OpenCode install doc told users to invoke `node /path/to/total-recall/dist/index.js`, which stopped working the moment `dist/` was deleted in the 0.8.0 TypeScript strip. Rewritten to document three supported install options (global npm, `npx`, source checkout), all routing through `bin/start.js`.
- **Multi-host plugin manifest version drift.** Four files carry a `version` field — `package.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json` — and they were all out of sync: Copilot's was stuck at `0.1.0` since day one, Claude's and Cursor's were stuck at `0.7.2` through the entire TS strip series. All four synced to `0.8.0-beta.4`. `AGENTS.md` § "Version sync — four files, one version" now documents this as a standing rule.

### Added

- **`scripts/fetch-binary.js`** — shared zero-dep Node downloader. Detects host RID, reads version from `package.json`, fetches the matching per-RID GitHub Release asset, and writes to `binaries/&lt;rid&gt;/`. Used by both `scripts/postinstall.js` (fast path at npm install time) and `bin/start.js` (safety net at first launch for git-clone installs and `--ignore-scripts` users).
- **`scripts/postinstall.js`** — resurrected with entirely new content. The old 0.6.8-era `postinstall.js` downloaded a ~150MB bun runtime (deleted as dead code in 0.8.0-beta.3). The new one is ~20 lines and calls `ensureBinary()` once at install time. Failures are intentionally non-fatal (exit 0) so `--ignore-scripts` / offline / corporate-firewall installs still succeed; `bin/start.js` retries on first launch.
- **Per-RID GitHub Release assets** so `scripts/fetch-binary.js` has a stable download URL pattern: `https://github.com/strvmarv/total-recall/releases/download/v&lt;version&gt;/total-recall-&lt;rid&gt;[.exe]`.

### Changed

- **`bin/start.js` refactored to use `ensureBinary()` as the single entry.** Previously duplicated RID detection and the "binary not found" error path. Now imports from `scripts/fetch-binary.js` — single source of truth for RID detection, error messages, and the download fallback. Fast path (binary present) has no added overhead.
- **`AGENTS.md` "Release flow" section rewritten** to enumerate all four version files as a standing rule and to remove references to the deleted `dist/`, `publish.yml`, and the bun launcher. The rest of `AGENTS.md` remains stale post-cutover; comprehensive rewrite tracked in `docs/TODO.md`.
- **`docs/TODO.md`** backfilled with 13 post-cutover follow-ups: checksum verification, signed releases, shared binary cache location, Intel Mac support, `--provenance` on npm publish, `release.yml` dry-run step, multi-platform `dotnet-ci.yml`, Node 24 migration, plugin version single-source-of-truth automation, stale `src-ts/` comment scrub, top-level doc scrub, `SqliteConnection.cs` bun:sqlite comment scrub, and a comprehensive `AGENTS.md` rewrite.

## 0.8.0-beta.3 - 2026-04-09

### Fixed

- **`scripts/verify-binaries.js` stuck on the old 5-RID layout.** The `prepublishOnly` safety-check script was still checking for `binaries/darwin-x64/` and `binaries/darwin-arm64/` directories that no longer existed (dropped and renamed in beta.2). It correctly refused to publish — working as designed — but the refusal blocked the npm publish step of the v0.8.0-beta.2 release. Updated to the new 4-RID list.
- **`bin/start.js` `detectRid()` returned stale `darwin-x64` / `darwin-arm64` values.** Even if the beta.2 publish had succeeded, macOS users would have hit "Prebuilt binary not found for darwin-arm64" because the tarball stages `osx-arm64`. Fixed `detectRid()` to return `osx-arm64` for macOS arm64 and dropped the `darwin-x64` branch entirely. Error message now distinguishes Intel Mac as a "not shipped in this release" case.
- **`tests-ts/` directory was never deleted in the original strip pass.** The plan's Task 0 verification only checked for `src-ts/`, `bin-ts/`, and the top-level TS tooling configs. `tests-ts/` held `dist-smoke.test.ts`, `helpers/{db,bun-sqlite-shim,embedding}.ts`, a `fixtures/` dir, and a `manual/` test doc — all pure dead weight post-cutover. Now deleted.
- **`scripts/postinstall.js` was downloading a ~150MB bun runtime** on every user `npm install` and doing nothing else useful. The .NET AOT binary doesn't execute through bun; `bin/start.js` uses `child_process.spawn` to run the native binary directly. Deleted entirely. (The file name was resurrected in beta.4 with entirely new content — a binary downloader.)
- **`.npmignore` was stale and redundant.** Listed `src-ts/`, `tests-ts/`, `tsconfig.json`, `tsup.config.ts`, `vitest.config.ts` — all already deleted. Also superseded by the explicit `files` allow-list in `package.json`. Removed.

## 0.8.0-beta.2 - 2026-04-09

### Fixed

- **Invalid .NET RIDs in `release.yml` matrix.** The matrix used `darwin-arm64` and `darwin-x64`, which are not canonical .NET runtime identifiers — .NET uses `osx-arm64` / `osx-x64` for macOS. The bug was dormant since Plan 6 because `release.yml` had never actually run before v0.8.0-beta.1; the first firing on macos-14 errored out with `NETSDK1083: The specified RuntimeIdentifier 'darwin-arm64' is not recognized`. Renamed matrix-wide: cascades through matrix entries, artifact names, staging paths, publish-job download paths, verification script, chmod script, and the GitHub Release files list.
- **Deprecated `macos-13` runner image.** GitHub retired `macos-13` runner images; the darwin-x64 matrix leg failed instantly with "macos-13-us-default is not supported". Dropped `osx-x64` from the matrix entirely. All modern Apple hardware is Apple Silicon since Nov 2020 and Intel Mac support is deferred (see `docs/TODO.md`).
- **`linux-arm64` cross-compile apt sources hardcoded `jammy` (Ubuntu 22.04).** `ubuntu-latest` runners are now `noble` (24.04), so the `/etc/apt/sources.list.d/arm64.list` patch hit 404 on every arm64 package index fetch. Rewrote the entire leg to use GitHub's native `ubuntu-24.04-arm` runner instead — eliminates the cross-compile toolchain dance (apt sources patching, `gcc-aarch64-linux-gnu`, `-p:CppCompilerAndLinker=clang -p:SysRoot=... -p:LinkerFlavor=lld -p:ObjCopyName=...`). Single unified `Publish (Unix)` step now handles all three Unix RIDs.

### Added

- **`/global.json`** pinning .NET 10 SDK with `rollForward: latestFeature`. Prevents runner pre-install drift — macos-14 already has .NET 10.0.201 preinstalled, and pinning explicitly means every matrix leg uses the same SDK regardless of what each runner image happens to ship. .NET 10 SDK builds `net8.0` targets cleanly.
- **`release.yml` npm publish step now handles prerelease dist-tags.** Logic ported from the deleted `publish.yml`: if `package.json` version contains a dash (e.g. `0.8.0-beta.N`), publish under the matching dist-tag (`beta`, `rc`, `alpha`) instead of clobbering `latest`. Only stable versions publish to `latest`. Implementation uses bash parameter expansion (no `sed`), reads `VERSION` from the committed `package.json` — no untrusted `github.event.*` input.

### Changed

- **`dotnet-ci.yml` Setup .NET step** now uses `global-json-file: global.json` instead of `dotnet-version: 8.0.x` for consistency with `release.yml`.
- **Version bump to `0.8.0-beta.2`.** The `v0.8.0-beta.1` tag stays on origin as a historical marker of the first failed matrix attempt.

### Known limitations

- `release.yml` only grants `contents: write` at the workflow level, not `id-token: write`, so `--provenance` cannot be passed to `npm publish` without a follow-up commit. Tracked in `docs/TODO.md`.

## 0.8.0-beta.1 - 2026-04-08

### Removed

- **All TypeScript source, tests, and build tooling.** The .NET rewrite on the `rewrite/dotnet` branch had been running in parallel to the TypeScript tree for weeks, with `src-ts/` and `bin-ts/` holding the parked TS implementation alongside the active `src/TotalRecall.{Cli,Core,Host,Infrastructure,Server}/` .NET tree. This release strips all remaining TS:
  - `src-ts/` — the entire parked TS source tree (114 files)
  - `bin-ts/` — the previous-generation launcher scripts (`start.cjs`, `total-recall.cmd`, `total-recall.sh`)
  - `dist/` — committed `tsup` output (`index.js`, `defaults.toml`, `eval/ci-smoke.js`, `eval/defaults.toml`)
  - `tsconfig.json`, `tsup.config.ts`, `vitest.config.ts`, `vitest.dist.config.ts` — top-level TS tooling
  - `.github/workflows/ci.yml` — pure TS CI (vitest on node, bun matrix, tsup build, dist-committed check)
  - `.github/workflows/publish.yml` — legacy TS npm publish workflow that triggered on the same `v*` tag as `release.yml` (latent double-publish bug)
  - All TS dependencies and devDependencies from `package.json` except `sqlite-vec`, which is kept in `devDependencies` as a per-platform native-binary source via its npm optionalDependencies (`sqlite-vec-linux-x64`, etc.). `package-lock.json` regenerated from the stripped manifest.

### Changed

- **Distribution model.** `package.json` reduced to a minimal `bin`/`files`/`scripts` shell pointing at `bin/start.js` as the Node launcher. Prebuilt .NET AOT binaries are staged into `binaries/<rid>/` by the release matrix and shipped inside the npm tarball via the `files` allow-list. End users install with `npm install @strvmarv/total-recall` (or `@beta` for prerelease); the Node launcher dispatches to the per-platform binary via `child_process.spawn`.
- **Version bump to `0.8.0-beta.1`.** Main branch stays at `0.7.2` (TypeScript) — beta tags ship via `release.yml` matrix to the `beta` dist-tag without touching `latest`.

### Known issues (all fixed in later betas)

- The `release.yml` matrix failed on three independent latent bugs that had never been exercised before this first tag push: invalid `darwin-*` RIDs (fixed in beta.2), stale `jammy` apt sources for linux-arm64 cross-compile (fixed in beta.2), and deprecated `macos-13` runner (fixed in beta.2).
- `scripts/verify-binaries.js` and `bin/start.js` had the old 5-RID list baked in (fixed in beta.3).
- `tests-ts/` was missed in the strip pass (fixed in beta.3).
- `scripts/postinstall.js` was downloading a bun runtime that nothing used (fixed in beta.3).
- The install-path gap for git-clone-sourced plugin installs was not recognized until beta.3 dogfood (fixed in beta.4 via the `scripts/fetch-binary.js` download bootstrap).
- The `darwin-x64` RID was dropped in beta.2, so Intel Mac is not shipped in the 0.8.x line.

## 0.7.2 - 2026-04-08

### Fixed
- **`SessionStart` hook missed `resume` source.** `hooks/hooks.json` and `hooks/hooks-cursor.json` matched only `startup|clear|compact`, so resuming a prior session never fired `session-start/run.sh` — hot context wasn't injected and the announce flow was skipped on resume. Added `resume` to both matchers.
- **Cursor and Copilot CLI bundled manifests shipped a known-broken `npx` invocation.** `.cursor-plugin/plugin.json` and `.copilot-plugin/plugin.json` declared `"command": "npx", "args": ["-y", "@strvmarv/total-recall"]`, but `AGENTS.md` and `README.md` already document that `npx` cannot resolve scoped-package binaries for this package. Replaced with `"command": "total-recall"` to match the documented install path in `INSTALL.md`.
- **Drifted version pins in host-specific manifests.** `.cursor-plugin/plugin.json` and `.copilot-plugin/plugin.json` were stuck at `0.1.0` while everything else was on the 0.7.x train. Synced both to the current release.
- **`async: false` removed from hook entries.** Not part of the Claude Code hook schema (only `type`, `command`, `timeout` are recognized). Was silently ignored, but it's noise that could mask a real config error later. Cleared from both `hooks/hooks.json` and `hooks/hooks-cursor.json`.
- **`package-lock.json` was stale at `0.6.8-beta.7`** even though `package.json` had moved through 0.7.0 and 0.7.1. Resynced as part of the 0.7.2 bump via `npm version`.

## 0.7.1 - 2026-04-08

### Fixed
- **CI was red on the 0.7.0 tag.** `src/e2e-phase3.test.ts` hardcoded `skills = ["total-recall"]` and asserted `skills/total-recall/SKILL.md` exists, which broke the moment the skill was renamed to `commands` in 0.7.0. Updated the test to cover both `commands` and `using-total-recall`. The `Publish to npm` workflow tied to `v0.7.0` never ran to completion because of this; 0.7.1 is cut from the fixed commit so the tarball actually lands on npm.

### Docs
- Finished the `/total-recall` → `/total-recall:commands` rename in the remaining docs that 0.7.0 missed:
  - `CONTRIBUTING.md` eval workflow examples and the PR benchmark-regression checklist.
  - `tests/manual/model-bootstrap.md` Scenario 3 manual-test script.
  - `hooks/session-end/run.sh` header comment that still pointed at the old `skills/total-recall/session-end.md` path.

## 0.7.0 - 2026-04-08

### Breaking
- **Skill rename: `total-recall` → `commands`.** The skill previously invoked as `/total-recall <subcommand>` is now `/total-recall:commands <subcommand>`. Rationale: the skill was named identically to the plugin, so the plugin:skill qualified form rendered as the awkward `total-recall:total-recall`. Renaming yields the cleaner `total-recall:commands` namespace and frees up the plain `total-recall` name for other meanings. The old shorthand `/total-recall` no longer resolves — update any scripts, aliases, or muscle memory accordingly. All README and INSTALL examples have been updated.

### Fixed
- **Cursor SessionStart hook resolved to an invalid path.** `hooks/hooks-cursor.json` referenced `$CLAUDE_PLUGIN_ROOT`, which is unset under Cursor; the hook command expanded to `/hooks/session-start/run.sh` and silently failed. Switched to `$CURSOR_PLUGIN_ROOT`, which the `run.sh` script already branches on for platform-aware JSON output.
- **SessionEnd hook directive used `<IMPORTANT>` XML tags** that the companion SessionStart hook explicitly warns against ("some hosts strip XML-like tags from hook output"). Rewrote the payload as plain text framing matching the session-start style. The directive is already injected inside a `system-reminder` block by the host, so nested markup is redundant and cross-host inconsistent.
- **MCP tool names were hardcoded with the `mcp__total-recall__*` prefix** throughout `skills/total-recall/SKILL.md`, `skills/using-total-recall/SKILL.md`, and `hooks/session-end/run.sh`. Claude Code exposes plugin MCP tools as `mcp__plugin_<plugin>_<server>__<tool>` (e.g. `mcp__plugin_total-recall_total-recall__session_start`), while Cursor / OpenCode / Copilot CLI use shorter forms. No single literal prefix works across hosts. Replaced with functional references (e.g. "call the total-recall `session_start` MCP tool") which the model resolves to whatever is on its toolbelt.

### Changed
- **Single source of truth for the SessionEnd directive.** Extracted the session-end instructions from the hardcoded bash string in `hooks/session-end/run.sh` into `skills/commands/session-end.md`. The hook now reads and injects this fragment at runtime, symmetric with how `session-start/run.sh` already injects the full SKILL.md. Eliminates the drift risk between the hook directive and the SKILL.md prose. `SKILL.md`'s Session End section is now a pointer to the fragment.
- **Standardized the compactor agent reference** on the fully qualified `total-recall:compactor` form throughout SKILL.md, using-total-recall SKILL.md, and `hooks/session-end/run.sh`. Previously some references were qualified and others bare, which could fail to resolve under strict namespacing.
- **Shortened the `commands` SKILL.md description frontmatter.** It previously enumerated all 18 subcommands inline (effectively a trigger list, not a when-to-use description). Replaced with a concise trigger phrase; the full command table remains in the body.
- **Documented Cursor's lack of a SessionEnd hook** in the README Supported Platforms table. Cursor 1.7 only exposes a `stop` event that fires after every agent turn, not at actual session close, so auto-compaction is unavailable on Cursor — users must run `/total-recall:commands compact` manually.
- **README now clarifies** that `/total-recall:commands` is implemented as a Claude Code skill (`skills/commands/SKILL.md`), not as a slash-command file under a `commands/` directory. The repo intentionally has no `commands/` dir.

## 0.6.8-beta.7 - 2026-04-08

### Added
- **`TOTAL_RECALL_DB_PATH` env var for relocating only the SQLite database file.** Set to an absolute path (e.g. `/Users/you/Dropbox/memories.db`) or a `~/`-prefixed path to move `total-recall.db` out from under `<TOTAL_RECALL_HOME>`. `config.toml`, the embedding model cache, and export directories stay anchored to `TOTAL_RECALL_HOME`. Enables cloud-synced memories (Dropbox, iCloud) and shared-database workflows across multiple Claude Code workspaces — the existing `project` field on memories filters per-workspace views on top of a shared store. Validation runs once at MCP server startup: invalid values (relative paths, trailing separators, bare `~`) cause `src/index.ts` to print a single stderr line via `SqliteDbPathError` and `process.exit(1)` BEFORE loadConfig, bootstrapSqlite, the embedding model, or the MCP transport bind — no partial DB is ever created. `src/db/connection.ts` now calls `mkdirSync(dirname(dbPath), { recursive: true })` so deep custom paths whose parent directories don't exist yet get created on first run. `status` tool reports the resolved path (not the default literal) so "which DB am I actually talking to?" is always answerable. See INSTALL.md's new "Relocating the database" section for cloud-sync caveats (sqlite.org/howtocorrupt link, Dropbox WAL/SHM warnings), concurrent-writer semantics on shared workspaces, and a manual migration recipe.
- **Smoke test passes 2 and 3.** `scripts/mcp-smoke-test.mjs` now runs three sequential passes under real bun on every CI matrix leg. Pass 2 spawns the MCP server with a `TOTAL_RECALL_DB_PATH` pointing at a nested path whose parent directory doesn't exist pre-spawn, and asserts that `status` reports the override, the DB file lands at exactly the configured location, the parent directory is auto-created, and vector search still hits the relocated DB. Pass 3 spawns the server with `TOTAL_RECALL_DB_PATH="./relative.db"` (invalid) and locks in the fail-fast contract: non-zero exit within a 5-second watchdog, expected `SqliteDbPathError` message on stderr including the raw bad value. Pass 3 uses `child_process.spawn` directly with a `close` event await and an `error` handler because the MCP SDK's transport init would crash during the child's early exit, masking the real assertion.

### Changed
- **Startup log line format.** `src/index.ts` now reports the resolved DB path via `getDbPath()` — previously it used `getDataDir()/total-recall.db`, which lied when `TOTAL_RECALL_DB_PATH` was set. The log line is otherwise unchanged: `total-recall: MCP server starting (db: <path>)`. If you have a log-scraping tool parsing the old literal format, it will still match the prefix but the tail will differ when a custom path is in use.

## 0.6.8-beta.6 - 2026-04-08

### Fixed
- **MCP server failed at first query on macOS** with `This build of sqlite3 does not support dynamic extension loading`. `bun:sqlite` on darwin dlopens `/usr/lib/libsqlite3.dylib`, which Apple ships without `SQLITE_ENABLE_LOAD_EXTENSION`, so `sqlite-vec.load()` at `src/db/connection.ts:18` aborted every request. Root cause is a long-standing bun quirk (oven-sh/bun#5756). Added `src/db/sqlite-bootstrap.ts` which calls `Database.setCustomSQLite()` before any Database construction, pointing at Homebrew's keg-only libsqlite3 at `/opt/homebrew/opt/sqlite/lib/libsqlite3.dylib` (Apple Silicon) or `/usr/local/opt/sqlite/lib/libsqlite3.dylib` (Intel). If neither is present, throws a `SqliteExtensionError` with a clear `brew install sqlite` remediation instead of surfacing the cryptic bun error later. Idempotent; no-op on linux and windows (their system libsqlite3 supports extension loading natively). Wired into both `getDb()` and the vitest test helper.
- **CI was green on macOS despite this bug** because `vitest.config.ts` aliases `bun:sqlite` to a `better-sqlite3` shim in `tests/helpers/bun-sqlite-shim.ts`, and better-sqlite3 bundles its own SQLite with extension loading enabled. Production paths running under real bun never touched the shim, so the darwin regression slipped past all three CI matrix legs. The shim now also exposes a static `setCustomSQLite` no-op so the bootstrap logic can be exercised by tests on any platform.
- **`scripts/postinstall.js` now warns at install time** when running on darwin without an extension-capable libsqlite3, surfacing the same `brew install sqlite` instruction during `npm install` rather than at first MCP request. Non-fatal — install still succeeds.
- **`INSTALL.md` documents the macOS prerequisite** under a new "Prerequisites" section at the top, so AI assistants walking users through setup won't hit the bug blind.
- **`getDataDir()` on Windows**: `src/config.ts:13` fell back to `join(process.env.HOME ?? "~", ".total-recall")`. `HOME` is undefined on Windows (Node uses `USERPROFILE`), so every Windows install that didn't set `TOTAL_RECALL_HOME` resolved its data dir to the literal string `~/.total-recall` — stored under whatever the current working directory was. Replaced with `os.homedir()`, which resolves correctly on every platform. Same class of bug as the `detectProject` Windows fix in 0.6.8-beta.5.

### Added
- **`scripts/mcp-smoke-test.mjs` + `npm run smoke`**: end-to-end MCP smoke test that launches the built `dist/index.js` under real bun (via `bin/start.cjs`) and drives it over stdio using the `@modelcontextprotocol/sdk` client. Runs `tools/list`, `status`, `memory_store`, `memory_search` (critical vector-query path), and `memory_delete`, asserting every step. Uses `TOTAL_RECALL_HOME=<mkdtemp>` so runs never touch the user's real database. Wired into the `test-bun` CI job on all three matrix legs (ubuntu, macos, windows) right after `bun run build`. This is the gap that let the darwin extension-loading bug ship undetected: vitest aliases `bun:sqlite` to a `better-sqlite3` shim, so the 304 unit tests exercise better-sqlite3 — not the real runtime. Any future regression in the bun:sqlite → sqlite-vec → embeddings → vector-search pipeline will now fail CI before reaching users.

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
