# Opt-in transcript archive store (Part 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the opt-in switch, the archive store, the forget/unforget commands, the three "off / not built yet" tools and the sync-isolation guarantees of Part 1 of the transcript archive (issue #148).

**Architecture:** Static `[transcripts]` settings readers and a sentinel file `<dataDir>/live/enabled` gate everything. A separate `TranscriptMigrationRunner` creates `tx_*` tables (own `tx_schema_version`) in the main database or a `db_path` file, through a `TranscriptStore` that owns its own connection. A `transcripts` CLI group enables, disables, inspects and forgets; three stub MCP tools answer off or "not available yet".

**Tech Stack:** C# 14 / .NET 10 NativeAOT (Infrastructure, Cli, Server, Host), F# (Core guard), Microsoft.Data.Sqlite + sqlite-vec + FTS5, xunit (C#) and Expecto (F#) tests, Node tests for docs.

**Spec:** `docs/superpowers/specs/2026-11-18-opt-in-archive-store-design.md` (parent: `docs/superpowers/specs/2026-11-18-transcript-archive-design.md`). Read the spec section named in each task before starting it.

## Global Constraints

- Work only inside this worktree. No commit trailers of any kind (no `Co-Authored-By`). Commit messages: one plain line.
- NativeAOT: no `RegexOptions.Compiled`, no reflection-based JSON; DTOs go through `Server/JsonContext.cs` only if a handler needs one (the stub handlers return hard-coded strings and need none).
- Never edit `MigrationRunner` or any existing migration; main `_schema_version` stays at 18 rows (`SchemaTests`, `GitSyncSchemaTests` assert it).
- Storage and parser boundary catches log through `ExceptionLogger.LogChain`.
- Sentinel path: `<dataDir>/live/enabled` (empty file; directory 0700, file 0600 on Unix). `dataDir` comes from `ConfigLoader.GetDataDir()`.
- Exit codes: 0 ok, 1 failure, 2 usage/refusal. A terminal means `Console.IsInputRedirected == false`; no override flag.
- Tool counts after this part: 53 in SQLite mode, 54 in Cortex mode; the 42 core handlers and `BuildRegistry`'s exact-list test do not change.
- New user-facing documents must not match the regex `sync\s+(init|join|leave|status)` (`HiddenCommandTests`).
- Tests for this part carry no `Integration` trait, so they run under `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`.
- Verify commands (run at Finish): those in `.github/develop.md` (`npm ci --ignore-scripts`, model fetch guard, `dotnet test ... --filter "Category!=Integration"`, `npm run test:shim`, build + `dump-catalog | diff -u catalog.json -`).

## Review Focus

Inputs and conditions the spec implies that a person will hit first; each has a test in the task that owns the code.

1. A project key or session id containing quotes, `%`, `_` or a colon (Task 4, Task 5): all SQL is parameterised; `--session a:b:c` splits at the first colon; an empty host or id is exit 2.
2. `status`, `forget` and `unforget` on a data directory that has no database yet, or a database that is locked (Task 4, Task 5): they create nothing, print a clear message, and exit 1 only for real failures.
3. `enable` when the data directory is not writable or `HOME` is unset (Task 2, Task 5): the sentinel failure is reported, the setting stays true, no crash.
4. `forget` of a session that does not exist (Task 4): it still writes the exclusion record, reports zero removed, and is idempotent when repeated.
5. A `db_path` spelled differently from the main database path, relative, or pointing at somebody's non-archive database (Task 1, Task 4): treated as main / refused, never written to.

---

### Task 1: Settings readers and the protected-key guard

Spec: sections 1 and 2.

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Config/defaults.toml` (path may be `src/TotalRecall.Infrastructure/Config/Config/defaults.toml`; find the embedded `[updates]` block and add beside it)
- Modify: `src/TotalRecall.Infrastructure/Config/ConfigLoader.cs`
- Modify: `src/TotalRecall.Core/SyncSettingsGuard.fs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Config/ConfigLoaderTranscriptsTests.cs` (new, modelled on `ConfigLoaderUpdatesTests.cs`), `tests/TotalRecall.Core.Tests/SyncSettingsGuardTests.fs`, `tests/TotalRecall.Server.Tests/` (the existing `ConfigSetHandler` tests), `tests/TotalRecall.Cli.Tests/HiddenCommandTests.cs` or the CLI `config set` tests, `tests/TotalRecall.Web.Tests/ConfigSetGuardRouteTests.cs`.

**Interfaces:**
- Produces (namespace `TotalRecall.Infrastructure.Config`):
  `public sealed record TranscriptsSettings(bool Enabled, string DbPath, string? DbPathError, int RetentionDays, bool AllowRemoteEmbedding)`
  `public static TranscriptsSettings ConfigLoader.LoadTranscriptsSettings(string? userConfigPath = null)`
  `DbPath` is empty for "main database". `DbPathError` is non-null when `db_path` is present but not a string, or violates the `TOTAL_RECALL_DB_PATH` rules (absolute or starting `~/`, no trailing slash; reuse the rule code behind `GetDbPath`); then `DbPath` is empty and the caller treats the archive as unavailable.

- [ ] **Step 1: Write the failing tests.** `ConfigLoaderTranscriptsTests`: no file gives `(false, "", null, 0, false)`; `enabled = true` reads true; `enabled = "yes"` (wrong type) reads false; broken TOML gives defaults; `db_path = "/tmp/x.db"` gives `DbPath`; `db_path = 5` gives non-null `DbPathError`; `db_path = "relative.db"` and `db_path = "/tmp/x/"` give a non-null error; `retention_days = -3` reads 0; `allow_remote_embedding = true` reads true. Guard tests (Expecto): `transcripts`, `transcripts.enabled`, `transcripts.db_path`, `Transcripts.Enabled`, ` transcripts . x ` are protected; `transcriptsx.enabled` and `transcript.enabled` are not. Handler/CLI/route tests: `config_set` with key `transcripts.enabled` is refused (`ArgumentException` from the handler, exit 2 from the CLI, the existing refusal text) and nothing is written.
- [ ] **Step 2: Run them to see them fail.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~ConfigLoaderTranscripts"` and `dotnet test tests/TotalRecall.Core.Tests --filter "FullyQualifiedName~SyncSettingsGuard"`. Expected: compile errors / failures.
- [ ] **Step 3: Implement.** Add the `[transcripts]` block to `defaults.toml` with comments (`enabled = false`, `db_path = ""`, `retention_days = 0`, `allow_remote_embedding = false`); implement `LoadTranscriptsSettings` as a fail-safe static reader over `LoadEffectiveTable` (pattern of `LoadUpdatesNoticeEnabled`); add `| "transcripts" -> true` to `isProtectedKey`. Do not touch `TotalRecallConfig` or `ConfigJsonSerializer`.
- [ ] **Step 4: Run the tests and the existing config and guard tests.** Expected: all pass (including `HiddenCommandTests` and `ConfigSetGuardRouteTests`).
- [ ] **Step 5: Commit** `Add the [transcripts] settings and protect them from config set`.

### Task 2: Private files and the sentinel

Spec: sections 3 and 4.

**Files:**
- Create: `src/TotalRecall.Infrastructure/Storage/PrivateFiles.cs`, `src/TotalRecall.Infrastructure/Transcripts/TranscriptSentinel.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptSentinelTests.cs`, `.../PrivateFilesTests.cs` (use `Posix.Mode` from `GitSync/GitTestSupport.cs`)

**Interfaces:**
- Produces (namespace `TotalRecall.Infrastructure.Storage`): `public static class PrivateFiles { public static void EnsureDirectory(string path); public static void CreateEmptyFile(string path); }` (Unix: 0700 / 0600, set at creation where the API allows, else immediately after; Windows: plain create).
- Produces (namespace `TotalRecall.Infrastructure.Transcripts`): `public sealed class TranscriptSentinel { public TranscriptSentinel(string dataDir); public string Path { get; } /* <dataDir>/live/enabled */ public bool IsEnabled(); public void Write(); public void Remove(); public static bool IsActive(bool settingEnabled, string storageMode); /* enabled && mode != "postgres" (case-insensitive) */ public void Mirror(bool settingEnabled, string storageMode); /* Write when active, Remove when not, swallow and log every exception */ }`

- [ ] **Step 1: Failing tests.** `IsEnabled` false on an empty data dir and true after `Write`; `Write` is idempotent; `Remove` on a missing file does not throw; `Write` creates `live/` 0700 and the file 0600 (skip on Windows); `IsActive(true,"postgres")` false, `IsActive(true,"sqlite")`, `("cortex")`, `("git")` true, `IsActive(false,"sqlite")` false; `Mirror` writes when active, removes when not, and with an unwritable data dir (a path under a regular file) neither throws nor leaves a file.
- [ ] **Step 2: Run them; expected: compile failures.**
- [ ] **Step 3: Implement** both classes; the sentinel uses `PrivateFiles`; log through `ExceptionLogger.LogChain("transcripts sentinel", ex)`.
- [ ] **Step 4: Run; expected: pass.**
- [ ] **Step 5: Commit** `Add private file helpers and the transcript archive sentinel`.

### Task 3: The archive migration runner and schema

Spec: section 5 (and the FTS notes in 7).

**Files:**
- Create: `src/TotalRecall.Infrastructure/Storage/TranscriptSchema.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptSchemaTests.cs` (shape of `GitSyncSchemaTests`: helpers `Scalar`, `Rows`, `Columns` via `pragma_table_info`, `SchemaObjects`)

**Interfaces:**
- Produces: `public static class TranscriptMigrationRunner { public static void Run(Microsoft.Data.Sqlite.SqliteConnection conn); public static long CurrentVersion(Microsoft.Data.Sqlite.SqliteConnection conn); public static bool IsMigrated(Microsoft.Data.Sqlite.SqliteConnection conn); }` (one transaction, apply entries above `MAX(version)` of `tx_schema_version`, no `_meta`, no marker; `CurrentVersion` is 0 when the table is absent; `IsMigrated` is `CurrentVersion >= 1`).
- Migration 1 creates exactly the objects of spec section 5: `tx_session`, `tx_chunk` (with the CHECK constraints and `ON DELETE CASCADE`), `tx_chunk_fts` + the three sync triggers + the FTS5 `secure-delete` option turned on, `tx_forgotten`, `tx_state`, `tx_schema_version`.

- [ ] **Step 1: Failing tests.** Running on `:memory:` (opened with `SqliteConnection.Open(":memory:")`, then `Run`) creates the tables with the columns of spec section 5; `CurrentVersion == 1`; running twice changes nothing; a database that already holds the main schema (`MigrationRunner.RunMigrations` first) keeps exactly 18 main `_schema_version` rows after `Run`; `kind = 'x'` and `chunk_type = 'x'` fail with "CHECK constraint failed"; inserting a chunk makes it findable by `MATCH` in `tx_chunk_fts`, deleting it removes it (`SELECT ... WHERE tx_chunk_fts MATCH`), updating `content` re-indexes; `UNIQUE(host, host_session_id)` and `UNIQUE(session_id, exchange_start, part)` are enforced; deleting a `tx_session` row cascades to its chunks (and the test records whether the FTS delete trigger fired for them, as the spec asks); `Run` on a database without `_meta` does not create `_meta`.
- [ ] **Step 2: Run them; expected: compile failures.**
- [ ] **Step 3: Implement** the runner with its own private `Exec` and `GetCurrentVersion` (do not call into `MigrationRunner`), and the migration, following `Migration3_Fts5` for the FTS pattern. Enable the FTS5 option with `INSERT INTO tx_chunk_fts(tx_chunk_fts, rank) VALUES('secure-delete', 1)`; if SQLite rejects it, let the exception fail the migration.
- [ ] **Step 4: Run; expected: pass, and `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~SchemaTests|FullyQualifiedName~GitSyncSchema"` still passes.**
- [ ] **Step 5: Commit** `Add the transcript archive schema and its own migration runner`.

### Task 4: The archive store: opening, status, forget, exclusions, delete hygiene

Spec: sections 6 and 7. Depends on Tasks 1 and 3.

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/ITranscriptStore.cs`, `TranscriptStore.cs`, `TranscriptLocation.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptStoreTests.cs`, `TranscriptForgetTests.cs` (byte scan), `TranscriptOpenTests.cs`

**Interfaces:**
- Consumes: `TranscriptsSettings` (Task 1), `TranscriptMigrationRunner` (Task 3), `PrivateFiles` (Task 2), `LegacyDatabaseProbe.Check` (`Infrastructure/GitSync/LegacyDatabaseProbe.cs`; read its signature and refusal text), `ReadOnlyDatabase.Open`, `MigrationRunner.RunMigrations`, `ConfigLoader.GetDbPath()`.
- Produces (namespace `TotalRecall.Infrastructure.Transcripts`):
  `public sealed record TranscriptLocation(string Path, bool IsMainDatabase)` and `public static TranscriptLocation? ResolveLocation(TranscriptsSettings s, string mainDbPath, out string? error)` (empty `DbPath` or a path equal to `mainDbPath` after full-path normalisation gives main; `s.DbPathError` gives an error).
  `public enum TranscriptOpenStatus { Opened, Absent, Refused, Failed }`, `public sealed record TranscriptOpenResult(TranscriptOpenStatus Status, ITranscriptStore? Store, string? Message)`.
  `public static TranscriptOpenResult TranscriptStore.Open(TranscriptLocation location, bool create)`; `ITranscriptStore : IDisposable` with:
  `TranscriptStatus Status()` where `TranscriptStatus(int SchemaVersion, string Location, long Sessions, long Chunks, IReadOnlyDictionary<string,long> SessionsByKind, IReadOnlyList<ForgottenSession> ForgottenSessions, IReadOnlyList<ForgottenProject> ForgottenProjects, string? IngestNotBefore)` and `ForgottenSession(string Host, string HostSessionId, string At)`, `ForgottenProject(string ProjectKey, IReadOnlyList<string> Roots, string At)`;
  `ForgetResult ForgetSession(string host, string hostSessionId)`, `ForgetResult ForgetProject(string projectKey)`, `ForgetResult ForgetAll(DateTimeOffset now)` with `ForgetResult(long Sessions, long Chunks, bool WalTruncated, string? Warning)`;
  `bool UnforgetSession(string host, string hostSessionId)`, `bool UnforgetProject(string projectKey)`, `bool ClearCutoff()`;
  `bool IsExcluded(string host, string hostSessionId, string? projectKey, string? cwd)` and `bool IsBeforeCutoff(DateTimeOffset timestamp)`.

- [ ] **Step 1: Failing tests (open).** `Open(create:false)` on a missing file returns `Absent` and creates no file or sidecar; on a main database without `tx_schema_version` returns `Absent`; `Open(create:true)` on a missing main file runs the main schema then the archive runner (both version tables present, main has 18 rows, file user-only is not required for main); on a main file that has tables but no `_schema_version` returns `Refused` and does not modify it; with an orphan `<db>.ts-backup` beside a missing main file returns `Refused` with the probe's text and creates no database; a separate `db_path` file is created 0600 (Unix) and holds only archive tables and no `_meta`; an existing non-empty separate file without `tx_schema_version` is `Refused`; `ResolveLocation` treats `~/x/../x/total-recall.db` spelled differently from the main path as main, and an invalid setting as an error.
- [ ] **Step 2: Failing tests (store).** Using raw SQL to insert sessions and chunks (no ingest API): `Status` counts; `ForgetSession` removes the session's chunks and row (and from `tx_chunk_fts`), writes a `session` exclusion, is idempotent, and for a missing session still writes the record and reports 0; `ForgetProject` removes all sessions with that key, writes a `project` record with `Roots` = the distinct `cwd` values and merges roots on a repeat; `ForgetAll` empties both tables and sets `ingest_not_before` to the max of existing and now; `Unforget*` remove records and report whether anything was removed; `IsExcluded` is true for a forgotten session, for a project key match, and for a `cwd` under a stored root on a directory boundary (`/a/b` excludes `/a/b/c` but not `/a/bc`), false otherwise; `IsBeforeCutoff` compares correctly; a project key `x' OR '1'='1` and a host id containing `%`, `_` and `:` are handled as data.
- [ ] **Step 3: Failing tests (delete hygiene, byte scan).** With a file-backed archive: insert a session whose `title`, `cwd`, `source_path` and a chunk's `content` hold unique lowercase alphanumeric markers; assert each marker is found in the raw bytes of the database (checkpoint first or read the `-wal`) before the forget; after `ForgetSession` and after `ForgetAll` none appears in the database, `-wal` or `-shm` bytes (case-insensitive); after `ForgetProject` the content and title markers appear zero times and the `cwd` marker exactly once in the database and `-wal` together; with a second connection holding a read transaction the checkpoint is blocked: the result has `WalTruncated == false` and a warning (retry three times 200 ms apart with a shortened `busy_timeout` set only around the checkpoint). Record in the test whether `optimize` was needed and add it only if the scan fails without it.
- [ ] **Step 4: Run them; expected: compile failures / failures.**
- [ ] **Step 5: Implement** as the spec's sections 6 and 7 say: the store owns a `SqliteConnection.Open(path, pooling: false)`, sets `PRAGMA secure_delete = ON`, runs the archive runner when creating; `Open` follows the "Opening safely" rules (probe first for the main path; no `SqliteConnection.Open` on a missing file when `create` is false; check through `ReadOnlyDatabase`); each forget is one transaction deleting chunks explicitly then sessions then writing the exclusion, followed by the hygiene steps; all SQL parameterised.
- [ ] **Step 6: Run; expected: pass.**
- [ ] **Step 7: Commit** `Add the transcript archive store with forgetting and exclusion records`.

### Task 5: The `transcripts` commands

Spec: section 8. Depends on Tasks 1, 2 and 4.

**Files:**
- Create: `src/TotalRecall.Cli/Commands/Transcripts/` with `TranscriptsCommandBase.cs`, `EnableCommand.cs`, `DisableCommand.cs`, `StatusCommand.cs`, `ForgetCommand.cs`, `UnforgetCommand.cs`
- Modify: `src/TotalRecall.Cli/CliApp.cs` (`BuildRegistry`)
- Test: `tests/TotalRecall.Cli.Tests/Commands/Transcripts/*Tests.cs` (constructor seams like `WhatsNewCommandTests` and `RemoveCommand`'s confirm delegate; the end-to-end ones use `[Collection("ConsoleCapture")]` and the env redirection of `SyncCommandTests`)

**Interfaces:**
- Consumes: Tasks 1, 2 and 4 types; `ConfigWriter.SaveUserOverride(path, key, value)`; storage mode from the loaded config (`storage.mode`).
- Produces: five `ICliCommand` classes with `Group => "transcripts"`, `Hidden => false`, injectable seams `(Func<TranscriptsSettings> settings, Func<TranscriptLocation?> location or the paths, Func<string?> storageMode, TranscriptSentinel sentinel, Func<bool> isInteractive, Func<string,bool> confirm, TextWriter output, TextWriter error, string userConfigPath, string mainDbPath)` plus a parameterless production constructor.

- [ ] **Step 1: Failing tests.** `enable`: with `isInteractive = false` exits 2, prints that it must be run in a terminal, writes nothing; declined confirmation changes nothing; storage mode `postgres` exits 1 with the reason; an invalid `db_path` exits 1; on success the tables exist, `config.toml` has `transcripts.enabled = true`, the sentinel exists, exit 0; if only the sentinel write fails the exit is 1 with a message, the setting stays true; an `Open` refusal (legacy database) exits 1 and changes neither setting nor sentinel. `disable`: needs no terminal, writes false, removes the sentinel, keeps data. `status`: never needs a terminal; with no database prints "off, nothing stored" and creates no file; with data prints the counts, the forgotten sessions and projects with their ids and keys, the cutoff and the line `search: not available in this version yet`; prints a mismatch between setting and sentinel; a locked or unreadable database exits 1 and does not say "nothing stored". `forget`: exactly one selector else exit 2; `--session` splits at the first colon, empty parts exit 2; needs a terminal (exit 2 otherwise); prints counts and asks; prints the warning and exits 1 when `WalTruncated` is false; with no archive tables says nothing is stored and creates nothing. `unforget`: selectors `--session`, `--project`, `--cutoff`; terminal required; reports whether anything was removed. `transcripts` appears in top-level help and group help lists the five verbs.
- [ ] **Step 2: Run them; expected: compile failures.**
- [ ] **Step 3: Implement** the commands and register them in `BuildRegistry` (rebuilt on every invocation, so constructors stay cheap).
- [ ] **Step 4: Run `dotnet test tests/TotalRecall.Cli.Tests`; expected: pass** (including `HiddenCommandTests`).
- [ ] **Step 5: Commit** `Add the transcripts commands: enable, disable, status, forget, unforget`.

### Task 6: Mirror the sentinel at engine start

Spec: section 3. Depends on Tasks 1 and 2.

**Files:**
- Modify: `src/TotalRecall.Host/Program.cs` (`RunServeAsync`, after the composition is opened, beside where the `UpdateChecker` is started)
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptSentinelWiringTests.cs` or the existing Host test project if one exists

**Interfaces:**
- Consumes: `ConfigLoader.LoadTranscriptsSettings`, the configured `storage.mode`, `TranscriptSentinel.Mirror`.
- Produces: one static helper `TranscriptSentinelStartup.Run(string dataDir, TranscriptsSettings settings, string storageMode)` (Infrastructure/Transcripts) that `RunServeAsync` calls; it must not run on `ui`, `dump-catalog` or any other entry.

- [ ] **Step 1: Failing tests** for `TranscriptSentinelStartup.Run`: setting true + `sqlite` writes the file; true + `postgres` removes it; false removes it; a throwing filesystem is swallowed. A source-scan test (same style as `HiddenCommandTests.ScanTree`) asserting `Program.cs` references `TranscriptSentinelStartup` only inside `RunServeAsync` and `ServerComposition.cs` does not reference it.
- [ ] **Step 2: Run; expected failures.**
- [ ] **Step 3: Implement** the helper and call it from `RunServeAsync` as a best-effort step that cannot delay or fail the handshake (fire it on the thread pool or run it inline only if it is one `stat` plus at most one write; either way wrap it in a try/catch).
- [ ] **Step 4: Run; expected: pass.**
- [ ] **Step 5: Commit** `Mirror the archive setting into the sentinel when the engine starts`.

### Task 7: The three archive tools and the tool lists

Spec: section 9. Depends on Task 2.

**Files:**
- Create: `src/TotalRecall.Server/Handlers/TranscriptSearchHandler.cs`, `TranscriptGetHandler.cs`, `TranscriptSessionsHandler.cs`
- Modify: `src/TotalRecall.Server/ServerComposition.cs` (`OpenSqlite` after the existing mode-specific registrations; `OpenCortexCore` likewise), `catalog.json` (regenerate), `docs/tools.md`, `AGENTS.md`, `src/TotalRecall.Server/Handlers/AGENTS.md`, `CONTRIBUTING.md`, `docs/architecture.md`, `tests/TotalRecall.Server.Tests/DumpCatalogTests.cs`, `tests/TotalRecall.Web.Tests/ToolAllowlistTests.cs`
- Test: `tests/TotalRecall.Server.Tests/TranscriptToolHandlerTests.cs`; extend `ServerCompositionTests` for the sqlite and cortex registries

**Interfaces:**
- Consumes: `TranscriptSentinel` (Task 2).
- Produces: three `IToolHandler`s, each constructed with `Func<bool> isActive` (production: `new TranscriptSentinel(ConfigLoader.GetDataDir()).IsEnabled`); `Name`s `transcript_search`, `transcript_get`, `transcript_sessions`; input schemas exactly as spec section 9 (`session` is the string `<host>:<host_session_id>`; no `oneOf`); answers: inactive `{"enabled":false,"hint":"Run `total-recall transcripts enable` in a terminal to turn the archive on."}`, active `{"enabled":true,"available":false,"hint":"Searching the archive is not available in this version yet."}`.

- [ ] **Step 1: Failing tests.** Each handler answers both states with exactly those strings (parse the JSON); the schemas have the property names and types of the spec and `query` required only on search; `OpenSqlite`'s registry contains the three tools and `OpenCortexCore`'s too, `BuildRegistry`'s exact-list test is unchanged, the Postgres registry has none; `ToolAllowlist` does not contain them (negative `InlineData`); `DumpCatalogTests` counts 53.
- [ ] **Step 2: Run; expected failures.**
- [ ] **Step 3: Implement** the handlers (hard-coded JSON strings; one file each) and register them in the two compositions.
- [ ] **Step 4: Regenerate and check the catalog and docs:** `dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q` then `dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog > catalog.json`; update `docs/tools.md` (three rows, the `53 in SQLite mode` phrase and 54 for Cortex), the handler counts in `AGENTS.md`, `Handlers/AGENTS.md`, `CONTRIBUTING.md` and `docs/architecture.md`; run `npm run test:shim` (Rule 15 must pass) and `dotnet test tests/TotalRecall.Server.Tests tests/TotalRecall.Web.Tests`. Expected: pass.
- [ ] **Step 5: Commit** `Add the three archive tools in their off state`.

### Task 8: Isolation from sync

Spec: section 10. Depends on Task 3.

**Files:**
- Create: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptSyncIsolationTests.cs`

**Interfaces:**
- Consumes: `GitSyncScanner`, `SyncDbTestSupport` / `GitSyncScannerTestSupport` fixtures, `SyncBacklogReader`, `SyncQueue`.

- [ ] **Step 1: Write the tests (they should pass once Task 3 exists; they are guards, so first prove they can fail).** Behavioural: set up a joined machine identity as the scanner tests do, migrate the main schema and the archive schema into one database, insert one memory row and several archive rows, enqueue one memory as a positive control, run `Scan()`, assert `Done`, `git_sync_entry` holds exactly that one memory id, `sync_queue` holds exactly one entry, the backlog reader counts one. Static: scan `Infrastructure/Sync/`, `Infrastructure/GitSync/` and `Core/Sync*.fs` for `tx_`, `Transcript`, `Archive` (case-sensitive, excluding `SyncSettingsGuard.fs`) and fail on any hit. For each test, temporarily break the guarantee (e.g. insert the archive row into `warm_memories`, or add the word to a scanned file) and confirm the test fails, then restore.
- [ ] **Step 2: Run; expected: pass.**
- [ ] **Step 3: Commit** `Test that no sync path carries the transcript archive`.

### Task 9: Documentation and the changelog

Spec: section 11. Depends on Tasks 5 and 7.

**Files:**
- Create: `docs/transcripts.md`
- Modify: `README.md` (Documentation table row), `PRIVACY.md` (stored-data row, the remote-embedding statement under "Configurations you opt in to", the deletion paragraph), `docs/configuration.md` (`[transcripts]` block), `docs/commands.md`, `CHANGELOG.md` (unreleased, internal), `src/TotalRecall.Infrastructure/AGENTS.md` (migration section)
- Test: `scripts/host-docs.test.js` already checks links, README reachability, tool counts and PRIVACY rules; run it.

- [ ] **Step 1: Write the documents.** `docs/transcripts.md`: a status banner (the archive can be enabled, inspected and emptied but not searched yet; search will arrive in a later release; no part numbers), then what it is, what will be stored later (locally, redacted, never synced), privacy, enabling, status, forgetting and unforgetting, the `[transcripts]` keys, and that a Unix separate archive file and `live/` are user-only. PRIVACY edits must keep every Rule 16 needle (`api.github.com`, `updates.check`, the update-check item) and avoid the forbidden phrases.
- [ ] **Step 2: Run `npm run test:shim` and `dotnet test src/TotalRecall.sln --filter "FullyQualifiedName~HiddenCommand"`.** Expected: pass.
- [ ] **Step 3: Commit** `Document the transcript archive foundation`.
