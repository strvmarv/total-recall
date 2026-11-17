# TotalRecall.Infrastructure — Agent Guide

The infrastructure layer owns all side-effects: storage, embedding, importers, ingestion,
telemetry, search, and Cortex sync. Pure logic lives in `TotalRecall.Core` (F#).

**AOT requirement**: `IsAotCompatible=true` and `InvariantGlobalization=true` are set in the csproj.
No reflection, no dynamic code generation. All JSON via source-generated `JsonContext`.

---

## Subsystem Map

| Directory | Interfaces / Key Types | Purpose |
|-----------|----------------------|---------|
| `Storage/` | `IStore`, `SqliteStore`, `PostgresStore`, `RoutingStore` | CRUD for memory/knowledge entries across tiers |
| `Embedding/` | `IEmbedder`, `OnnxEmbedder`, `RemoteEmbedder`, `EmbedderFactory` | Embedding generation (local ONNX or remote API) |
| `Search/` | `IVectorSearch`, `IHybridSearch`, `IFtsSearch`, `VectorSearch`, `HybridSearch` | Vector + FTS hybrid retrieval |
| `Importers/` | `IImporter`, `ClaudeCodeImporter`, `CopilotCliImporter`, … | One-way import from host tool memory dirs |
| `Ingestion/` | `IFileIngester`, `FileIngester`, `HierarchicalIndex`, `IngestValidator` | File/dir ingestion into cold KB tier |
| `Telemetry/` | `CompactionLog`, `UsageEventLog`, `UsageDailyRollup`, `UsageWatermarkStore` | Retrieval events, compaction history, usage telemetry |
| `Usage/` | `IUsageImporter`, `UsageIndexer`, `UsageQueryService` | Token usage tracking and query |
| `Sync/` | `CortexClient`, `SyncQueue`, `SyncService`, `PeriodicSync`, `RoutingStore` | Bidirectional Cortex sync |
| `GitSync/` | `RecordReader`, `ParsedRecord`, `RecordWriter`, `RecordPatcher` | Git sync record codec (not wired to any sync yet): strict reader (1 MiB cap, duplicate-key and encoding pre-pass), hand-written canonical writer (byte-stable, unknown content kept), patcher. Wire names are lower-case, not the `EntryTypeMapping` DB strings |
| `GitSync/` (git client) | `GitProcess`, `GitClient` (+ `GitClient.Write.cs`), `GitSyncPaths`, `GitOutcome`, `SyncLock` | Git sync transport (used by the hidden `sync` commands; the cycle is milestone 1b): see "Git sync transport" below |
| `GitSync/` (setup) | `SyncSetupService` (partial: `.cs`, `.LeaveStatus.cs`, `.Init.cs`, `.Record.cs`, `.Join.cs`), `GitSyncStateStore`, `ReadOnlyDatabase`, `ReadOnlyMeta`, `LegacyDatabaseProbe`, `CloneSideline`, `UserConfigFile` | The hidden `sync init`/`join`/`leave`/`status` commands' service (`Cli/Commands/Sync/`): see "Git sync setup commands" below |
| `GitSync/` (pull) | `GitSyncFetcher`, `PullTree`, `GitSyncApplier` (partial: `.cs`, `.Entry.cs`, `.Issues.cs`, `.Pull.cs`), `LocalEntryReader`, `BaseRenderer`, `GitSyncIssues` | The pull: fetch the repository and apply other machines' memories (see "Git sync pull" below). Nothing calls it yet (the cycle of milestone 1b-i-3 will) |
| `GitSync/` (scan) | `GitSyncRow` + `GitSyncRows` (typed `git_sync_entry` access: `ReadAll`, `Write` upsert), `GitSyncScanner` (partial: `.cs`, `.Plan.cs`, `.Write.cs`) | The scan: stamps local memory changes into `git_sync_entry` (see "Git sync scan" below). Nothing calls it yet (the cycle of milestone 1b-i-3 will) |
| `GitSync/` (guards) | `ManifestFile`, `HostIdentity`, `MachineIdReader`, `GitSyncIdentityStore`, `PublicRepoProbe`, `RepoGuard` (+ `RepoGuard.Public.cs`) | Git sync guards (nothing starts a cycle yet): manifest codec, host fingerprint, the stored identity, the anonymous GitHub probe and `RepoGuard` (`EvaluateIdentityAsync`, `ReadManifestAsync`, `CheckPublicAsync`). See "Git sync guards" below. |
| `Config/` | `ConfigLoader`, `TomlConfig` | TOML config loading + `~/.total-recall/config.toml` |
| `Diagnostics/` | `ExceptionLogger` | AOT-safe exception chain logging |
| `Migration/` | `MigrationRunner`, `PostgresMigrationRunner` | Sequential schema migration framework |
| `Json/` | `JsonContext` (Infrastructure-level) | Source-generated JSON for Infrastructure DTOs |
| `Memory/` | Memory-specific helpers | Memory operation utilities |
| `Eval/` | Eval query services, `RegressionDetector` / `RegressionOptions` | Retrieval quality metrics; session-start regression detection (see "Regression detection" in the root `AGENTS.md`) |

---

## Storage Layer

### IStore

```csharp
interface IStore
{
    Task<Entry?> GetAsync(string id, ...);
    Task<IReadOnlyList<Entry>> ListAsync(Tier tier, ContentType type, ...);
    Task StoreAsync(Entry entry, ...);
    Task UpdateAsync(string id, ...);
    Task DeleteAsync(string id, ...);
    Task MoveAsync(string id, Tier targetTier, ...);  // for promote/demote
}
```

**All-or-nothing transaction (git sync groundwork).** `SqliteStore.BeginTransaction()` returns a
`SqliteStoreTransaction` (not on `IStore`) offering insert with its vector, move between tiers, delete,
update, `SetSticky` (hot only) and an `import_log` write as one unit: `Commit()` makes them all take effect,
`Dispose()` without `Commit()` rolls everything back. Embeddings are computed by the caller before it opens.
It runs on the store's single connection, so while one is open no other code may use that connection
(Microsoft.Data.Sqlite fails a stray command with `InvalidOperationException`), and a second
`BeginTransaction()` on the same store throws. The statements live in `Storage/SqlRows.cs` (`SqlRows`,
`VecRows`), shared with `SqliteStore`'s own methods. An internal `BeforeStep` seam lets tests inject a
failure at a named step. Nothing in production uses it yet (git sync does, in later milestones).

**Git sync pull (`GitSync/GitSyncFetcher.cs`, `PullTree.cs`, `GitSyncApplier*.cs`, `LocalEntryReader.cs`, `BaseRenderer.cs`, `GitSyncIssues.cs`; rules in `Core/SyncApply.fs` and `Core/SyncMerge.fs`).** Two classes, called in order by the cycle (milestone 1b-i-3; nothing calls them yet, and a `git grep` for `GitSyncFetcher` and `GitSyncApplier` outside them and the tests is empty).
- **`GitSyncFetcher.FetchAsync`** (cycle step 2) returns `FetchOutcome`: `Fetched` / `NothingNew` (a `FetchResult(Head, Last, Proven, NothingNew)`), `Refused(reason)` or `Failed(kind, message)`. It never writes the store. Order: read `L` (`git_sync_last_sha`, only when 40 or 64 lowercase hex), the identity half of the guard (join state and host identity, no clone and no network), `ls-remote`, then either `NothingNew` (the head equals `L` **and** the clone holds `L`: no fetch and no reset, the full guard runs at `L`) or `fetch`, `R` from `rev-parse` of the tracking ref, the full guard at `R`, the proof state, and only then `reset --hard R`. **Proven** means there is an `L`, `L` is an ancestor of `R` (or equal) and `refs/total-recall/last-synced` equals `L`; a ref that is a strict ancestor of `L` is repaired in place (`UpdateLastSyncedAsync`); a `Tri.No`, `Tri.Unknown`, missing or elsewhere ref is just unproven; a git call that gives no answer is `Failed`. A manifest that could not be read because a git call failed or was cancelled is `Failed`, never `Refused`.
- **`PullTree.ReadAsync`** reads the tree at `R` as git objects (`ls-tree`, `diff-tree`), never the working tree: the listing alone decides what is present and what is rejected (`SyncTree.classifyRecordEntry`; a rejection names an id only for the exact path `memories/<shard>/<id>.json`); the diff, only when proven, gives the ids whose record changed.
- **`GitSyncApplier.ApplyAsync(fetch, scan, ct)`** returns `ApplyOutcome`: `Done(ApplySummary)` or `Failed(reason)` (the listing, the rows or the `L` writes failed). It builds the visit set (`SyncApply.visitSet`, united with the scan's skipped ids), decides each id in ordinal order with `SyncMerge.decide`, and applies each in **one `BEGIN IMMEDIATE` transaction** (`ApplyOne`): re-read of the row and the entry (any difference from the decision's inputs, including an edit the scan has not stamped, is a **deferral**), the store operations, the `git_sync_entry` row (comparison columns from the stored entry, so a scan right after stamps nothing), the `import_log` row for an insert (`git-sync`, `sync:<id>`) and the removal of the entry's `apply-failed` row. A busy database defers and **stops** the apply (the rest is counted `NotAttempted`). A decision that needs a later part (a deletion outcome, a review request, a re-push, a `forgotten` row) is **postponed**: nothing is written and `L` is held back. `L` (`git_sync_last_sha`, then `refs/total-recall/last-synced`) advances only for a complete pull (every id processed, nothing postponed or deferred). `apply-failed` rows record a failed entry (attempts, blob id, a one-line detail) and also `scan-skipped:` / `unreadable:` entries that were not applied (no attempt counted; never overwrite a real failure); `rejected-record` rows are keyed by `sanitizeText(path)` and swept at the end of a pull that processed every id. A rejected path is never read as a deletion. Test seams: `BeforeEntry`, `BeforeTransaction`, `BeforeEntryStep(id, step)` (the store transaction's own steps plus `reread`, `row`, `issue`, `commit`, `write-issue`), `TransactionCount`. **Store groundwork the apply uses:** `SqliteStore.BeginImmediateTransaction`, `SqliteStoreTransaction.ReplaceEmbedding`, and `UpdateEntryOpts.SourceTool` / `ClearSourceTool` / `CreatedAt` (Postgres refuses them). A null vector array must stay "no vector" (`Memory(...)`): the implicit array-to-memory conversion would make it an empty one.

**Git sync scan (`GitSync/GitSyncScanner*.cs`, `GitSyncRows.cs`; rules in `Core/SyncScan.fs`).** `new GitSyncScanner(connection, nowMs).Scan()` returns `ScanOutcome`: `Done(ScanSummary)`, `NotJoined` (no valid `git_sync_machine`) or `Failed(reason)`. It reads inside **one** read transaction (raw `BEGIN`, first `SELECT` starts the WAL snapshot: the machine id, the clock and warning keys, every `git_sync_entry` row, the `ineligible-id` issues, the hot, warm and cold memory tables, the knowledge ids), decides per id with `SyncScan.decide` in ordinal id order, and writes in **one** `BEGIN IMMEDIATE` transaction (rows, issues, clock, warning). A scan with nothing to write opens no write transaction; two scans in a row with no edit change nothing. It takes no lock, runs no `git`, uses no network and writes only `git_sync_entry`, `git_sync_issue` and the `_meta` keys `git_sync_hlc` and `git_sync_clock_warning`, never a memory, knowledge, vector, `import_log` or `compaction_log` row. Per group it stamps content (text, project, scope, entry type, source tool, `created_at`), sticky (a pin or unpin) and tier (only a warmer tier; demotion just updates `local_tier`); a tags change updates the tags hash and issues no stamp. Stamps come from `Clock.issue` with the greater of the group's base stamp (read from `base_json`) and its pending stamp as the floor; `Clock.guardStartup` runs once per scan. An id in two tiers, an unreadable entry and an ineligible id are skipped (listed in `Skipped` or `Ineligible`, and never treated as deleted); a group whose clock is exhausted is skipped and retried next scan. An active row whose memory exists nowhere is only **reported** in `LocalDeletions` (with has-base and has-pushing-stamps flags); the scan never drops, forgets or deletes it (1b-ii acts on it); a retype to knowledge moves the row to `left`. Internal test seams: `AfterFirstTableRead`, `BeforeWrite(step)`, `WriteTransactionCount`. What the scan requires of the later parts: the apply sets `scanned_content_hash`, `scanned_tags_hash`, `local_sticky` and `local_tier` from the stored entry after its own write (or the next scan reads the pulled change as a local edit), reaches rows with no base and no entry whose id is absent from the repository, and exempts groups the scan skipped; the push and the apply clear the pending stamps. A stamp column that does not parse reads as none and is written back as NULL when the row is rewritten.

**Git sync guards (`GitSync/RepoGuard*.cs`, `ManifestFile.cs`, `HostIdentity.cs`, `GitSyncIdentityStore.cs`, `PublicRepoProbe.cs`).**
`RepoGuard.EvaluateIdentityAsync(revision = Head)` reads the settings, the stored identity (`_meta` keys `git_sync_remote`,
`git_sync_repo_id`, `git_sync_host`), the host fingerprint and the repository's manifest from the **local clone** at the given
revision (the cycle passes `refs/remotes/origin/<branch>` after its fetch, when `HEAD` is still the old commit) and returns a
`GuardVerdict`: the `Core.SyncIdentity.JoinState`, the `ManifestRead` outcome (`NoClone` = no `.git` under the clone directory,
`NoManifest`, `Invalid`, `Unreadable` for any git failure or a blob over 64 KiB, `Newer`, `Ok`) and `HostUnverified`.
`GuardVerdict.CanSync` is true only when joined, the host fingerprint was available and the manifest is `Ok` or there is no
clone yet; a caller reports a newer format before `not-joined`. It runs no network command (`Clone`, `LsRemote`, `Fetch`,
`Push`). `ManifestFile.Parse` lets a newer `format` win over every refusal except non-JSON and invalid UTF-8 (a strict UTF-8
check first, then a permissive look for the first root `format`), then applies the 4 KiB cap and the `v1` rules, and never
throws on hostile input. `HostIdentity` hashes the OS machine id (Linux `/etc/machine-id`, macOS `ioreg`, Windows `MachineGuid`)
with the canonical database path (links resolved one hop at a time, `\\?\` stripped, lower-cased on Windows and macOS);
a source that exists but fails makes the fingerprint unavailable (`null`) instead of reading as a moved directory.
`RepoGuard.CheckPublicAsync` asks `GET https://api.github.com/repos/<owner>/<repo>` with no credentials, no cookies, at most 3
redirects and a 5-second limit (only an answer from the base address's host counts): 200 is public (refused), 404 private (cached
24 hours in `_meta.git_sync_public_check`; nothing else is cached, and a store fault on the cache is a miss), anything else is
unknown (one warning per guard); a non-GitHub remote is skipped with one notice. There is no in-process memo of the verdict.
`ServerComposition.OpenGit` (the `git` storage mode) validates the remote, the branch and that a `git` executable is on the
`PATH` (`GitExecutable.TryFind`, no process) and otherwise opens the plain local store with the label `git`; a failure falls back
to `sqlite (git failed)`.

**Git sync transport (`GitSync/GitProcess.cs`, `GitClient*.cs`, `GitSyncPaths.cs`, `SyncLock.cs`).** Every git call is one
of the closed `Core.SyncGit.GitCommand` cases (no case can force, delete or mirror; a recording `IGitProcess` plus
`ForceFlags.AssertNone` checks every argument list the tests produce). `GitProcess` is the only place a process starts:
no shell, argument list from `SyncGit.build`, the `GIT_*` environment scrubbed to an allow-list, a hard kill of the
process tree at the call's limit or cancellation, bounded output, and standard error redacted as it is read (a
credential or a UTF-8 character split across reads is never redacted in pieces). `GitClient` types the operations
(version, init, clone, ls-remote, fetch, reset, clean, ancestry, rev-parse, the protective ref, stale locks, ls-tree,
diff-tree, cat-file, stage, commit, push; `stage` is `add -A -f` so a remote's `.gitignore` cannot drop a record); a clone-scoped command carries `--git-dir`/`--work-tree` so a damaged clone
can never make git act on an enclosing repository, and every call sets `GIT_CEILING_DIRECTORIES` to the database's
directory (a database directory whose path contains the path-list separator, `:` on Unix, therefore fails every call but
the version check). The user's ssh command (the process's `GIT_SSH_COMMAND`, else `core.sshCommand` as git resolves it:
system, global and XDG files with includes, read from the sync directory under the ceiling; once per client) is passed on as `GIT_SSH_COMMAND` with
`-o BatchMode=yes -o ConnectTimeout=8` appended (`-batch` for a plink or putty program; the variable is left alone when
only `GIT_SSH` is set, since such a program takes no OpenSSH options). The layout is
`<directory of the database file>/sync/<dbKey>/{lock, no-hooks, empty, <cloneKey>/repo}` where `dbKey` is the first 12 hex of
the SHA-256 of the database file's *name* (lower-cased on Windows and macOS), so two databases in one directory never
share a clone, a protective ref or a lock. `SyncLock` is a file opened with `FileShare.None` (released by the operating
system if the holder dies; the file is never deleted and holds no text); it throws `NotSupportedException` when .NET's
file locking is disabled (`DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is `1` or `true`, or the `System.IO.DisableFileLocking`
switch is set) and tells "busy" (a sharing violation, or EAGAIN) from a real error. .NET ignores a flock failure other than
"would block", so on a file system without flock support the lock excludes no one. The tests that need `git` are
`[GitFact]`, the ones that need `sh` are `[ShFact]`, the ones that need both are `[GitShFact]` and the one that needs
`flock(1)` is `[FlockFact]`: they are ordinary tests (not `Integration`), so the standard verify runs them and a missing
tool shows as skips. Windows runs the `[GitFact]` tests and skips the `sh`, `git`+`sh` and `flock` ones.

Known limits, left to later milestones: a failed or interrupted `CloneAsync` can leave a partial clone directory that the
caller must move aside; a remote can send a very large blob or pack (only the call's timeout bounds clone and fetch); and
a remote's `.gitattributes` can select a filter driver the user's own configuration defines (the repository is the
user's own, and the filter is theirs).

**Git sync setup commands (`GitSync/SyncSetupService*.cs`, `Cli/Commands/Sync/`).** `total-recall sync init --url <repo> [--branch <b>]`, `sync join <repo> [--branch <b>]`, `sync leave` and `sync status` are **hidden** until milestone 1b-iii (`ICliCommand.Hidden`: the top-level help leaves them out and no document, help text or `config set` refusal names them; `HiddenCommandTests` scans for that). The pure rules are `Core/SyncSetup.fs`; the service does the I/O. `init` and `join` refuse on Cortex or Postgres (the user sets `storage.mode = "local"` by hand first) and on an address holding a credential. Recording is ordered so that a crash is repaired by running the command again: place the clone (a new one, or the earlier one moved aside), then ONE database transaction (identity and `_meta` keys; `ClearForNewRepository` only when the repository differs), then the `config.toml` write (`storage.mode = "git"` and `[git_sync]`; `config.toml` is rewritten **without comments**, as `config set` does). A clone is never deleted: `CloneSideline.Move` renames it to `repo.<label>-<time>` (`unpushed`, `broken`, `left`, `superseded`) and the commands print where it went. A `join` that finds a clone refreshes it (fetch, then `reset --hard` to the remote branch, never `clean`); a clone git cannot read is sidelined as `broken`, and a fetch failure of kind `Other` is probed locally first (Interpretation 13 of the plan). `init` and `join` open the database with `SqliteConnection.Open(path, pooling:false)` plus `RunMigrations` and a borrowed `SqliteStore`; `LegacyDatabaseProbe` refuses a database of an older format first. `status` is read-only and creates nothing: it opens the database read-only (`ReadOnlyDatabase`, `ReadOnlyMeta`: an immutable URI when no `-wal`/`-shm` sidecar exists, `Mode=ReadOnly` when both do, and nothing when exactly one does), reads the manifest only when the `no-hooks` directory and `empty` file already exist, and never touches the network. Results go to standard output, notices and git progress to standard error, then `refused: <name>: <text>` and `note:` lines; exit 0 done, 1 refused or failed, 2 usage. Tests: `SyncSetup*Tests` (Infrastructure, `SetupRig` and `BareRemote` in `SyncSetupTestSupport.cs`) and `SyncCommandTests` (Cli, end to end with git and the data directory redirected).

**Borrowed-connection pattern**: `SqliteStore` accepts a `SqliteConnection` and does NOT dispose it.
The process-lifetime connection is owned by `ServerComposition.OpenSqlite()` → `ServerCompositionHandles`.

### Backend Selection

`ServerComposition.OpenProduction()` reads `[storage] mode` from config:
- `"local"` (default) → `SqliteStore` + `VectorSearch` (sqlite-vec)
- `"postgres"` → `PostgresStore` + `PgvectorSearch`
- `"cortex"` → `RoutingStore` wrapping `SqliteStore` + `CortexClient` for bidirectional sync

**Fallback**: If Cortex/Postgres fails to open, the composition root catches the exception and
falls back to SQLite, setting `StorageMode` to `"sqlite (cortex failed)"`.

### Schema

18 sequential migrations in `Storage/Schema.cs`. **Never modify existing migrations** — only append.

| Migration | What it adds |
|-----------|-------------|
| 1 | 6 content tables (hot/warm/cold × memory/knowledge) + vec0 virtual tables + indexes + system tables |
| 2 | `_meta` KV store + `benchmark_candidates` |
| 3 | FTS5 virtual tables + insert/delete/update triggers + backfill |
| 4 | `compaction_log.source` column (default `'compaction'`) |
| 5 | Orphan row cleanup (one-time hot-fix for 0.6.7) |
| 6 | Usage telemetry: `usage_events`, `usage_daily`, `usage_watermarks` |
| 7 | `sync_queue` for Cortex outbound buffering |
| 8 | `scope TEXT NOT NULL DEFAULT ''` on all 6 content tables + indexes |
| 9 | `entry_type` column on all content tables |
| 10 | `skill_cache` table for local skill metadata sync |
| 11 | `sync_queue.next_attempt_at` column (exponential backoff retry) |
| 12 | `skill_cache` content + frontmatter + content_hash + embedding + fingerprint columns |
| 13 | Skill usage counter columns + `skill_usage_events` table |
| 14 | `times_injected` column on all 6 content tables |
| 15 | `tool_cache` table |
| 16 | Pinned tier: `pinned_memories` / `pinned_knowledge` with the full current column set + vec0 + FTS5 + triggers |
| 17 | `sticky INTEGER NOT NULL DEFAULT 0` column and an `idx_<table>_sticky` index on `hot_memories` / `hot_knowledge` (tier model v2) |
| 18 | Git sync state: `git_sync_entry`, `git_sync_review`, `git_sync_issue`, `git_sync_alias` and `idx_import_log_target_entry_id`. SQLite only; `GitSyncStateStore` clears them for a different repository (`sync join`); `GitSyncScanner` (milestone 1b-i-1) and `GitSyncApplier` (1b-i-2) write `git_sync_entry` and `git_sync_issue`; the cycle that fills the rest is milestone 1b. The tests are in `GitSync/GitSyncSchemaTests.cs`, which is not `Integration`-trait, so the standard verify runs them |

Table naming: `{tier}_{type}` (e.g., `hot_memories`, `cold_knowledge`, `pinned_memories`). Vec tables: `{table}_vec`. FTS tables: `{table}_fts`.
`MigrationRunner.TableName(tier, type)` is the canonical helper — use it, don't inline strings.

**Three table-pair arrays — know which includes pinned:**

- `TierNames.AllTablePairs` (`Memory/TierNames.cs`) and `EntryMapping.AllTablePairs` (`Server/Handlers/EntryMapping.cs`) — **8 pairs** (pinned included). Use these for runtime enumeration.
- `MigrationRunner.AllTablePairs` (`Storage/Schema.cs`) — **deliberately frozen at the 6 historical pairs**. Migrations 1–15 predate the pinned tier and must keep producing the same schema; Migration 16 creates the pinned tables with the full current column set instead of replaying the historical column evolution.

**Postgres:** no schema change for pinned — tier is a column on the `memories` / `knowledge` tables, so pinned is just a new `tier='pinned'` value (`PostgresMigrationRunner` is untouched).

---

## Embedding Layer

### IEmbedder

```csharp
interface IEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    int Dimensions { get; }  // 384 for local bge-small-en-v1.5
}
```

**Factory**: `EmbedderFactory.CreateFromConfig(cfg.Embedding)` selects the implementation:
- `provider = "local"` (default) → `OnnxEmbedder` using bundled `models/bge-small-en-v1.5/model.onnx`
- `provider = "openai"` → `RemoteEmbedder` with OpenAI-compatible endpoint
- `provider = "bedrock"` → `RemoteEmbedder` with Amazon Bedrock

**Model path**: `ModelManager.cs` handles model discovery. It validates the bundled
`models/bge-small-en-v1.5/model.onnx` and throws a clear error if it is absent — it does **not**
download from HuggingFace. The model is fetched + sha256-verified at release build time
(`scripts/fetch-bge-small.sh`, pinned HF revision) and bundled into the per-RID release artifact;
for a git-source build, run `sh scripts/fetch-bge-small.sh` once to fetch it (it is not committed
to the repo / not in Git LFS).

`bge-small-en-v1.5` uses **CLS pooling** (not mean pooling) and an asymmetric query prefix:
search queries are embedded with the `bge` retrieval-instruction prefix while stored documents are
not. After swapping the local embedder the startup guard (`EmbedderMigration.EnsureCompatibleSqlite`,
called from `ServerComposition.OpenSqlite`/`OpenPostgres`/`OpenCortexCore`) detects a fingerprint
**mismatch** — or an **unstamped-but-populated** index (vectors present but no fingerprint, e.g. a
cortex DB created before stamping was wired into that path) — and **returns a decision** (it does no
re-embedding itself) per `embedding.on_model_change` (`OnModelChange`, default `auto`):
- `auto` → returns `ReindexInBackground`. Composition then starts an **in-process background worker**
  (its own SQLite connection + its own embedder) that re-embeds the sqlite / cortex **local** vec0
  index via `ReindexCoordinator` → `EmbeddingReindexer.RunBatched`. This is **never on the MCP boot
  path** — a synchronous re-embed of a large DB (~27 min for ~25k rows) would blow the stdio startup
  timeout into an atomic-rollback boot loop (the v3.0.2 regression this design fixes). `RunBatched`
  embeds OFF the write lock and commits per batch, advancing a `_meta` cursor (`embed.reindex.*`) so an
  interrupted run **resumes** instead of restarting; the coordinator takes a best-effort advisory lock
  (`embed.reindex.lock`) and stamps the fingerprint only after a full pass. While it runs, local
  retrieval is degraded; `session_start`/`status` surface `backgroundTasks.reindex` + a hint.
- `warn` → returns `Warned`: continues without re-stamping (recurring nag).
- `block` → throws `EmbedderFingerprintMismatchException`.
Postgres `auto` throws an explicit unsupported error (no local vec index to background-reindex). An
**empty** unstamped DB just stamps (genuinely fresh — nothing to migrate). For cortex only the local
index is migrated; the remote re-embeds independently via content-only sync. `total-recall
reindex-embeddings` runs the **same** `ReindexCoordinator` foreground (run-to-completion, also
resumable) — for `warn`/`block` deferrals and manual offline re-embeds.

**Known limitation — tokenizer accent handling**: the bundled F# WordPiece tokenizer lowercases but
does **not** strip accents (canonical BERT also strips accents). Accented words that don't match a
vocab entry therefore fall through to `[UNK]`. Impact is low for the English/code-heavy content this
memory system embeds; the behavior is pinned by a tokenizer test so it can't regress silently.

---

## Search Layer

`HybridSearch` combines two sources:
1. **Vector search** (`VectorSearch` / `PgvectorSearch`) — cosine similarity via sqlite-vec / pgvector
2. **FTS search** (`FtsSearch` / `PostgresFtsSearch`) — BM25-ranked full-text via FTS5 / tsvector

Results are merged and re-ranked by `TotalRecall.Core.Ranking` (F# pure function).
`similarity_threshold` from config filters low-score vector results before merge.

`KbSearchHandler` can additionally query a `IRemoteBackend` (Cortex) for global team KB.

---

## Importers

### IImporter

```csharp
interface IImporter
{
    string SourceTool { get; }  // e.g., "claude-code"
    Task ImportAsync(CancellationToken ct);
}
```

Each importer:
1. Detects the host tool's memory directory (platform-specific paths)
2. Reads the host's memory format (markdown, JSON, etc.)
3. Deduplicates via `ImportLog` (content hash lookup in `import_log` table)
4. Calls `IStore.StoreAsync()` + `IVectorSearch.UpsertAsync()` for new entries

Importers run during `session_start` step 2. Order in `ServerComposition` matches the registration order.

**Memory import ids and crash repair.** An import that inserts a **memory** (today: Claude Code memory
files via `ImportUtils.ImportMarkdownFile` with `ContentType.Memory`, and the Hermes `MEMORY.md`/`USER.md`
sections) goes through `ImportUtils.InsertOrRepairMemory` (internal to the assembly). It derives the entry id from the import hash
(`ImportIds.FromHash`: first 16 bytes of `SHA-256(hash)`, lowercase `8-4-4-4-12`), so the same host memory is
the same entry on every machine (git sync relies on this), looks for an existing entry with that id in the
target tier and then the other tiers, and if one exists skips the insert and repairs it: it re-embeds the
**stored** text, inserts the vector (which replaces an orphan row) and writes the missing `import_log` row.
A kill between the insert, the embedding and the log row is therefore repaired by the next run instead of
leaving a duplicate. It is a repair on retry and not a transaction because in cortex mode importers write
through `RoutingStore`, which cannot share one. Knowledge imports keep random ids; memories imported before
this was added keep their random ids.

**Adding a new importer**: implement `IImporter`, add to the `importers` list in both `OpenSqlite`
and `OpenPostgres` / `OpenCortexCore` in `ServerComposition.cs`.

Current importers: `ClaudeCode`, `CopilotCli`, `Cursor`, `Cline`, `OpenCode`, `Hermes`, `ProjectDocs`.

---

## Ingestion

`FileIngester` → `HierarchicalIndex` → `TotalRecall.Core.Chunker` (F# pure) → `IStore` + `IVectorSearch`.

Flow:
1. `IngestValidator` checks for duplicate collections (by source path hash)
2. `Chunker.chunk()` splits file content into semantic chunks (Markdown heading-aware or regex code parser)
3. Each chunk is embedded and stored as a `cold_knowledge` entry with `collection_id` linking siblings
4. `HierarchicalIndex` tracks parent/child relationships via `parent_id`

**Supported parsers** (in `TotalRecall.Core/Parsers.fs`): Markdown, code (regex-based).

---

## Telemetry

| Class | Table | Purpose |
|-------|-------|---------|
| `CompactionLog` | `compaction_log` | Records tier movements (promote/demote/compact) with semantic drift and preservation ratio |
| `UsageEventLog` | `usage_events` | Raw token usage events (one row per assistant turn) |
| `UsageDailyRollup` | `usage_daily` | Aggregated daily token counts; 30-day raw retention |
| `UsageWatermarkStore` | `usage_watermarks` | Per-host scan watermark for incremental indexing |
| `RetrievalEventLog` | `retrieval_events` | Per-search retrieval events with score, tier, and outcome signal |

**Retrieval event logging**: `memory_search` and `kb_search` handlers call `LogRetrievalEvent()`
after each search. They pass `SessionConfigSnapshot.Resolve()` (pinned by `session_start`, resolved
lazily otherwise) so events are tagged to the session's config snapshot. Each event records a `query_source` (`assistant` / `web-ui` / `cli`)
so metrics can be scoped to assistant traffic; the search handlers return the logged event's
`retrievalId`, and `memory_feedback` calls `UpdateOutcome()` to mark whether it was used.

**Read-time outcome inference** (`Eval/Metrics.cs` → `Compute(events, threshold, nowMs, graceWindowMs, …)`):
outcomes are resolved when metrics are computed, not stored. `OutcomeUsed == true` is a hit;
`false` is a miss; `NULL` is a miss only once the event is older than `nowMs - graceWindowMs`
(otherwise it is pending and excluded). `eval_report`, the Insights health score, and the CLI
`eval report` all pass a grace window (default 60 min) so un-acted assistant retrievals age into
misses rather than silently counting as hits.

---

## Cortex Sync

`RoutingStore` wraps `SqliteStore`:
- Reads go to local SQLite (fast, offline-safe)
- Writes enqueue items to `sync_queue` AND write locally
- `SyncService` drains `sync_queue` → `CortexClient` HTTP calls
- `PeriodicSync` calls `SyncService` every N seconds (default 300s) on a background thread

**Offline resilience**: if Cortex is unreachable, local reads/writes continue. The queue
is durable (SQLite) and survives process restarts.

**Pinned is local-only (deliberate)**: the remote Cortex backend has no pinned-tier support
yet, so the pinned tier is excluded from every sync path:

- `RoutingStore` never enqueues pinned upserts or deletes to `sync_queue`
- `SyncService` pull/reconcile excludes pinned — remote updates and tombstones can never
  mutate a local pin
- `migrate_to_remote` silently skips pinned entries

Pinning an entry that already exists remotely leaves the remote copy alone (accepted edge:
a later re-pull can reintroduce it as a separate non-pinned entry). Lifting these gates
requires a companion Cortex server change — tracked in `docs/TODO.md`.

---

## Config

`ConfigLoader.LoadEffectiveConfig()` merges:
1. Embedded defaults (`Config/defaults.toml` compiled as an embedded resource)
2. User config at `~/.total-recall/config.toml` (TOML via Tomlyn)
3. Environment variable overrides (e.g., `TOTAL_RECALL_DB_PATH`, `TOTAL_RECALL_CORTEX_URL`)

`ConfigLoader.GetDbPath()` → `~/.total-recall/total-recall.db` (or `TOTAL_RECALL_DB_PATH`).
`ConfigLoader.GetDataDir()` → `~/.total-recall/`.

---

## Diagnostics

`ExceptionLogger.LogChain(prefix, ex)` walks the entire `InnerException` chain and writes to
`Console.Error` with indented `-> <Type>: <Message>` formatting. AOT-safe — no reflection.

**Use at every catch boundary** that can hit a static-constructor failure or P/Invoke failure:
- Migration guard
- Server composition (host startup)
- CLI commands that touch the embedder or open the DB

A bare `Console.Error.WriteLine(ex.Message)` at these boundaries hides `DllNotFoundException`
under `TypeInitializationException` and produces unactionable output.

---

## AOT Checklist (for new Infrastructure code)

- [ ] No `Type.GetType()`, `Activator.CreateInstance()`, or `Assembly.Load()`
- [ ] All JSON serialization uses source-generated `JsonContext` attributes
- [ ] No `dynamic` types
- [ ] P/Invoke signatures (sqlite-vec, ONNX) are declared as `[DllImport]` or via `NativeLibrary.Load`
- [ ] `IsAotCompatible=true` in the csproj — verify with `dotnet publish -p:PublishAot=true -r win-x64` producing 0 trim warnings
