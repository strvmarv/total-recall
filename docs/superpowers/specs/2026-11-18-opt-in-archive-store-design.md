# Opt-in, the archive store, forgetting and isolation from sync

Parent: #145, Part 1 (docs/superpowers/specs/2026-11-18-transcript-archive-design.md)

Date: 2026-11-18. Status: draft for review. Part 1 of 8 of the transcript archive. It builds the safe foundation: the setting that
turns the archive on, the store the later parts fill, the commands that delete from it, and the guarantees that nothing syncs it and
no assistant tool can switch it on. Nothing in this part reads a host's session files or stores any conversation text.

## Goal

After this part a user can enable the archive from a terminal, see its status, delete what it holds (a session, a project or
everything) and be sure the deleted text is gone from the file; the assistant's tools cannot enable, disable or redirect it; Cortex and
git sync cannot carry it; and three tools exist that answer that the archive is off, or that search is not built yet. Later parts fill
the store (Part 2), search it (Part 3) and so on.

Success: every item of the parent's "Done when" for Part 1 (parent spec, Breakdown), checked by tests that run in the normal suite,
plus the acceptance criteria 1, 12 and the "forgotten stays forgotten" half of 14 of issue #145 as far as this part reaches (the rest of
14 needs ingestion, Part 2).

## Non-goals

- Any adapter, redaction, chunking, ingestion, watermark, vector table, embedding or search behaviour (Parts 2 to 4).
- The live-session marker and any hook (Part 5), the breadcrumb (Parts 6 and 7), retention, prune, rescan, reproject and the re-embed
  (Part 8).
- A node reader of the sentinel (Part 5 reads it; this part fixes its path and meaning).
- A web UI page or an allowlist entry for the new tools.

## Constraints and ground truth

Read from the repository at `origin/main` (1d34a5b) while writing this spec; the builder re-checks anything that looks different.

- `MigrationRunner` (`Storage/Schema.cs`) hard-codes `_schema_version`, a private `Migrations[]` of 18 entries, a single transaction and the
  `_meta` fresh-database marker; `SchemaTests` and `GitSyncSchemaTests` assert the 18 version rows. Appending a main migration would break
  them, and `RunMigrations` cannot be pointed at a file that must not get the memory tables.
- `SqliteConnection.Open(path, pooling)` always loads the sqlite-vec extension and sets WAL, `busy_timeout`, `foreign_keys=ON` and
  `synchronous=NORMAL`; there is no overload without sqlite-vec. Nothing in `src` uses `secure_delete`, `wal_checkpoint`, or the FTS5
  `secure-delete`, `optimize` or `rebuild` commands. The bundled SQLite is 3.49.1 with FTS5 (read from the linux-x64 library only).
- Per-dataDir state is created with plain `Directory.CreateDirectory` and `File.WriteAllText`, so modes follow the umask; there is no
  product helper for 0600/0700. `File.SetUnixFileMode` throws on Windows.
- Config sections that are not in the F# `TotalRecallConfig` (the `[updates]` settings) are read by static fail-safe readers in
  `ConfigLoader` over the merged TOML table; `config get`/`set` work on the raw table, so a key in `Config/defaults.toml` is reachable
  with no other code. `ConfigWriter.SaveUserOverride(path, key, value)` writes `config.toml`; the `SyncSettingsGuard` check lives in its two
  callers (`ConfigSetHandler`, CLI `SetCommand`), not in the writer.
- `SyncSettingsGuard.isProtectedKey` refuses `git_sync` at any depth and `storage.mode`/`storage.connection_string`; its tests are in
  `tests/TotalRecall.Core.Tests/SyncSettingsGuardTests.fs` and the CLI refusal text is asserted in `HiddenCommandTests`.
- The CLI is a hand-rolled two-level dispatcher (`CliApp.BuildRegistry`, `ICliCommand` with `Name`, `Group`, `Description`, `Hidden`,
  `RunAsync`). The only terminal check in the product is `Console.IsInputRedirected` in `Kb/RemoveCommand` (with an injected confirm
  delegate for tests).
- MCP handlers implement `IToolHandler`; `ServerComposition.BuildRegistry` registers the 42 core handlers (an exact-list test), and
  `OpenSqlite` and `OpenCortexCore` add the mode-specific ones (50 and 51 tools today). `catalog.json` must equal `dump-catalog`; a
  Node test (`host-docs.test.js`, Rule 15) ties it to `docs/tools.md`; the web `ToolAllowlist` is a hand-listed set.
- Cortex sync is queue-driven (`sync_queue.entity_type` is one of `memory`, `usage`, `retrieval`, `compaction`); git sync reads only the
  three tiers of memories, knowledge ids and its own state tables. Nothing enumerates tables, so archive tables are excluded unless
  something enqueues or lists them.
- `PRIVACY.md` is checked by Rule 16 (an update-check item with five needles, a Summary that names `api.github.com` and `updates.check`,
  forbidden phrases such as "does not phone home"); every top-level `docs/*.md` must be linked from the README (Rule 13); relative
  links and anchors are checked (Rule 3). A test forbids user-facing documents from naming the hidden sync verbs.

## Approaches considered

My calls, to confirm at sign-off.

1. **Second migration runner.** (a) Generalise `MigrationRunner` to take a list and a version table; (b) add a separate
   `TranscriptMigrationRunner` with its own list, `tx_schema_version`, helpers and the same one-transaction shape. **Chosen: (b).**
   `MigrationRunner` is frozen history (its comment says its table list must not be extended) and (a) risks it for no gain; the price
   of (b) is about thirty duplicated lines. This departs from the parent's "refactor `MigrationRunner`" wording; see "Deviation".
2. **Config shape.** (a) A new F# record field in `TotalRecallConfig`; (b) static readers like `[updates]`. **Chosen: (b).** (a) changes
   a 12-field positional record, its constructor sites (including a test) and the snapshot serializer, and the settings are read by the
   CLI, the tools and the sentinel mirror, which need no snapshotting.
3. **Where the stub tools are registered.** (a) In `BuildRegistry` (all backends, Postgres included); (b) in `OpenSqlite` and
   `OpenCortexCore`. **Chosen: (b).** The parent registers the tools in sqlite and cortex modes only; (a) would also expose them under
   Postgres and break the exact-list test of `BuildRegistry`. Counts become 53 (sqlite) and 54 (cortex).
4. **CLI visibility until Part 3.** (a) Hide the `transcripts` group (as the sync verbs were hidden); (b) show it, with a status line
   that says search is not available yet. **Chosen: (b).** The parent requires `docs/transcripts.md` (which Rule 13 forces into the
   README), so a hidden group would be documented anyway; announcing is a release-time act and the page carries a status banner.

## Design

### 1. Settings

A `[transcripts]` table in `Config/defaults.toml` with comments, and static readers in `ConfigLoader` following `LoadUpdatesNoticeEnabled`
(a missing table, wrong type or broken TOML gives the default; the readers never throw):

- `enabled` (bool, default `false`);
- `db_path` (string, default empty: the archive lives in the main database; when set, it follows the rules of
  `TOTAL_RECALL_DB_PATH`: absolute or starting `~/`, no trailing slash; an invalid value makes the archive unavailable and `status` says
  why, and `enable` refuses it);
- `retention_days` (int, default 0) and `allow_remote_embedding` (bool, default `false`): defined and documented now, read by later
  parts (8 and 3); nothing in this part uses them.

One reader returns a small immutable `TranscriptsSettings` record. The settings are not added to `TotalRecallConfig` or to
`ConfigJsonSerializer` (config snapshots are unchanged).

### 2. The guard

`SyncSettingsGuard.isProtectedKey` protects the whole `transcripts` table and every key under it (`transcripts`, `transcripts.enabled`,
`transcripts.db_path`, ... any depth and case, with the existing trimming), so `config_set` over MCP, the web route and CLI `config set`
refuse them with the existing refusal text. Tests: the protected list in `SyncSettingsGuardTests.fs`, a look-alike that stays
writable (`transcriptsx.enabled`), the MCP handler, the CLI and the web route.

### 3. The sentinel

`<dataDir>/live/enabled`, an empty file whose existence means "the archive is on" (Parts 5 and 6 read it from node and from shell). A
small `TranscriptSentinel` class (Infrastructure) resolves the path from `ConfigLoader.GetDataDir()`, and offers `IsEnabled`, `Write`,
`Remove` and `Mirror(configEnabled)`:

- the engine mirrors `transcripts.enabled` into the sentinel once at startup (create it when the setting is true, delete it when false),
  from the composition of the sqlite and cortex modes; it is one `stat` and at most one small write, never on the MCP handshake path,
  and every failure is swallowed (a stderr line through `ExceptionLogger.LogChain`);
- `transcripts enable` and `disable` write it at once; the tools and the CLI gate on the sentinel per call (a `stat`), so a change applies
  without a restart; a hand edit of `config.toml` reaches the sentinel at the next engine start.

The directory is created 0700 and the file 0600 (decision 4).

### 4. Private files

A small helper in Infrastructure (`PrivateFiles`) creates a directory user-only and a file user-only: on Unix with
`UnixFileMode` (0700/0600, set at creation where the API allows it, else immediately after), and on Windows it does nothing beyond the
default (the user profile's ACL; stated in the documentation). It is used by the sentinel and by the creation of a separate archive
file; the main database file is not touched.

### 5. The archive migrations

A new `Storage/TranscriptSchema.cs` holds `TranscriptMigrationRunner` (internal), whose `Run(conn)` mirrors `RunMigrations` (one
transaction, apply those above `MAX(version)` of `tx_schema_version`, idempotent, no `_meta`, no marker) and a `Migrations[]` with one
entry for now:

- `tx_session`: `id` (INTEGER primary key), `host`, `host_session_id`, `project_key`, `project_key_source` (`remote` | `path`), `cwd`,
  `started_at`, `ended_at`, `kind` (`interactive` | `automated`, CHECK), `title`, `source_path`, `source_size`, `source_mtime`,
  `source_fingerprint`, `ingested_offset`, `exchange_count`; `UNIQUE(host, host_session_id)`.
- `tx_chunk`: `id` (INTEGER primary key, an implicit rowid is required for the text index), `session_id` (references `tx_session`,
  `ON DELETE CASCADE`), `exchange_start`, `exchange_end`, `part`, `chunk_type` (`dialogue` | `compaction-summary`, CHECK), `has_error`,
  `ts`, `redaction_version`, `embedded` (default 0), `content`; `UNIQUE(session_id, exchange_start, part)`.
- `tx_chunk_fts`: an FTS5 table over `content`, `content=tx_chunk, content_rowid=id`, with the three sync triggers written like the
  existing ones (`Migration3_Fts5`), and its `secure-delete` option turned on (section 7).
- `tx_forgotten`: the text-free exclusion records of the parent's decision 7: `id`, `kind` (`session` | `project`, CHECK), `host` and
  `host_session_id` (for `session`), `project_key` and `roots` (for `project`: a JSON array of directory strings), `at`; unique indexes on
  `(host, host_session_id)` and on `project_key`.
- `tx_state`: key/value text (`ingest_not_before` is the one key here, an ISO-8601 UTC time).
- `tx_schema_version`: `version`, `applied_at`.

The vector table is not created here (Part 3's migration). The builder confirms that FTS5 external-content triggers fire for rows
removed through the cascade; `forget` deletes chunks explicitly first, so it does not depend on it.

### 6. `ITranscriptStore`

An interface and a `TranscriptStore` implementation that **owns its connection** (never the main store's): it opens
`SqliteConnection.Open(path, pooling: false)` for the resolved path (the `db_path` file, created user-only first, or the main database
path), sets `PRAGMA secure_delete = ON`, and runs the archive runner. A separate file still loads sqlite-vec (the only open helper there
is); that is accepted and stated. `TranscriptStore.Open(settings, create)` returns null when `create` is false and the archive tables do
not exist, so `status`, `forget` and `unforget` never create tables behind a disabled archive.

Operations (all synchronous, small, fully tested):

- `Status()`: schema version, location (main database or the path), session count, chunk count, counts by `kind`, forgotten sessions,
  forgotten projects, the cutoff time.
- `ForgetSession(host, hostSessionId)`, `ForgetProject(projectKey)`, `ForgetAll(now)`: each runs in one transaction, deletes chunks
  explicitly and then sessions, writes its exclusion (a `session` record; a `project` record whose `roots` are the distinct `cwd`
  values of the sessions it removed, plus the key, which also covers a key the user types for a project that was never archived; for
  everything, `ingest_not_before = max(existing, now)` in `tx_state`), then performs the delete hygiene of section 7. Each returns the
  counts removed.
- `UnforgetSession`, `UnforgetProject`, `ClearCutoff`: remove the record or cutoff; they return whether anything was removed.
- `IsExcluded(host, hostSessionId, projectKey, cwd)`: true when a `session` record matches, or a `project` record matches by key or by
  `cwd` lying under one of its `roots` (a directory-boundary prefix test, case-sensitive except on Windows); and
  `IsBeforeCutoff(timestamp)`. These are the primitives every ingestion path of Part 2 and later calls first; this part defines and
  tests them and nothing calls them yet.

No ingest API is invented here: tests insert rows with SQL.

### 7. Delete hygiene

`secure_delete` alone does not scrub text-index segments or earlier write-ahead-log frames, so a delete does four things, in order:

1. the transaction deleting rows (the text-index triggers remove the terms; the migration turned on the FTS5 `secure-delete` option, a
   persistent index setting, available from SQLite 3.42; if the running SQLite rejects it, the store falls back to an FTS5 `rebuild`
   after each delete and the status output says so);
2. an FTS5 `optimize` of `tx_chunk_fts`, so merged segments replace the old ones;
3. `PRAGMA wal_checkpoint(TRUNCATE)`;
4. if the checkpoint reports busy (the engine or another process holds the database), retry three times 200 ms apart, then return a
   failure that the command prints: the rows are deleted, but the `-wal` file may still hold their text until it is checkpointed, so run
   it again. The command exits 1 and never prints a clean-delete message it did not earn.

The test that backs this reads the raw bytes of the database file and of its `-wal` (and `-shm`) file after a forget and asserts that a
unique marker string put into a chunk, a title and a project root is absent, with a checkpoint-blocked case that asserts the failure is
reported.

### 8. The `transcripts` commands

One group, five commands, each a small `ICliCommand` in `Commands/Transcripts/` with injected seams (settings reader, store factory,
sentinel, output writers, `Func<bool> isInteractive`, `Func<string, bool> confirm`) like `WhatsNewCommand` and `RemoveCommand`:

- `transcripts enable`: needs a terminal and a confirmation that says what will be stored once the later parts land (the dialogue of
  agent sessions, locally, redacted, never synced); validates `db_path`; writes `transcripts.enabled = true` with
  `ConfigWriter.SaveUserOverride` (the writer, not `config set`: the guard is in the callers); creates the sentinel; opens the store with
  `create = true` (creating the tables); prints the state.
- `transcripts disable`: no gate; writes `false`, removes the sentinel; keeps the data.
- `transcripts status`: no gate; prints enabled or off, the location, the counts of section 6 and the line `search: not available in
  this version yet`. While the archive is off it opens nothing and says so.
- `transcripts forget (--session <host>:<id> | --project <key> | --all)`: exactly one selector; needs a terminal; prints how many
  sessions and chunks will go and asks; runs the matching `Forget*`; prints the counts or the section 7 failure. With no archive tables
  there is nothing to forget and it says so.
- `transcripts unforget (--session <host>:<id> | --project <key> | --cutoff)`: needs a terminal; asks; removes the record or cutoff.

A terminal requirement means `Console.IsInputRedirected` is false; otherwise the command prints that it must be run in a terminal and
exits 2, and there is **no override flag** (scripts and the documented hand edit use `config.toml`). Exit codes follow the other
commands (0 ok, 1 failure, 2 usage). The existing `ICliCommand` pattern needs no change to `CliApp` beyond the registration.

### 9. The three tools

`transcript_search`, `transcript_get` and `transcript_sessions`, one handler file each in `Server/Handlers/`, registered in
`OpenSqlite` and `OpenCortexCore`. Input schemas carry the arguments the parent's decision 9 names, all optional except `query` for
search, so a model sees the final interface; descriptions state that the archive is off by default and, for now, not available. The
gate is an injected `Func<bool>` that reads the sentinel per call. Answers:

- off: `{"enabled":false,"hint":"Run `total-recall transcripts enable` in a terminal to turn the archive on."}`;
- on: `{"enabled":true,"available":false,"hint":"Searching the archive is not available in this version yet."}`.

Updating everything that states the tool list or count is part of the work: `catalog.json` (regenerated, 53 tools), `docs/tools.md` (rows
and the `53 in SQLite mode` phrase that Rule 15 reads, plus 54 for Cortex), `AGENTS.md`, `src/TotalRecall.Server/Handlers/AGENTS.md`,
`CONTRIBUTING.md`, `docs/architecture.md`, `DumpCatalogTests` and a negative case in `ToolAllowlistTests` (the new tools are not in the
web allowlist). `ServerCompositionTests` (the `BuildRegistry` exact list) must not change.

### 10. Isolation from sync

Two tests in `tests/TotalRecall.Infrastructure.Tests/` (not `Integration`): a behavioural one that migrates the main schema and the archive
schema into one database, inserts archive rows, runs the git sync scanner and checks `git_sync_entry` and `sync_queue` stay empty and
that the sync backlog reader reports zero; and a static one, modelled on `HiddenCommandTests.ScanTree`, that fails when any file under
`Infrastructure/Sync/`, `Infrastructure/GitSync/` or the `Core` sync modules mentions `tx_`, `Transcript` or `Archive`.

### 11. Documentation

`docs/transcripts.md` (new; linked from the README Documentation table; a status banner that says the archive can be enabled, inspected
and emptied but not searched yet, and that Parts 1 to 3 are announced together; sections for what it is, what is stored later, privacy,
enabling, status, forgetting and unforgetting), the `[transcripts]` block in `docs/configuration.md`, the commands in `docs/commands.md`,
the tool rows (section 9), `PRIVACY.md` (a row in the stored-data table, and under "Configurations you opt in to" the statement that the
archive is never sent to a remote embedding service unless `allow_remote_embedding = true`, keeping every Rule 16 needle and phrase),
`CHANGELOG.md`. The new documents must not contain the phrase pattern that `HiddenCommandTests` forbids for the sync verbs.

## Deviation from the parent design

- The parent's Part 1 scope says to refactor `MigrationRunner` so it can run a named list against a named version table. This part adds a
  separate `TranscriptMigrationRunner` instead (approach 1), which satisfies the parent's intent (its own list and `tx_schema_version`
  against any chosen file, main chain untouched) without editing frozen code.
- The parent's decision 7 says a project exclusion stores "the root directories its sessions had". This part stores the distinct `cwd`
  values of the removed sessions; reducing them to repository roots is the project-identity work of Part 2 and the `reproject` of Part 8,
  and the stored shape (a JSON array of directories) does not change.

## Acceptance (the parent's Done when, mapped)

- Enabling, forgetting and unforgetting refuse without a terminal; disabling does not; enable and disable write the sentinel: CLI tests.
- `config_set` over MCP, the web route and the CLI refuses every key under `transcripts`: guard, handler, CLI and route tests.
- Forget removes rows from the tables and the text index and leaves none of their text in the file, its text-index shadow tables or the
  write-ahead log; a blocked checkpoint is reported: the byte-scan tests of section 7.
- Forget of a session, of a project and of everything each write an exclusion record or cutoff with no text, `unforget` removes them, and
  `IsExcluded`/`IsBeforeCutoff` answer as decision 7 says: store tests.
- The archive tables appear in the main database or in the `db_path` file, and the main `_schema_version` is unchanged (still 18 rows):
  migration tests.
- The three tools answer off, and `available: false` when on: handler tests, plus `catalog.json`, `docs/tools.md` and the counts agree
  (the verify commands and `host-docs.test.js`).
- The sync-isolation tests pass.

## Left to the builder

Exact column types and index names; the interface shape of `ITranscriptStore`; how the sentinel mirror is wired into the two
compositions; the confirmation wording; whether to tune the retry count of the checkpoint; the FTS fallback wording; a spike on whether
FTS5 triggers fire for cascaded deletes; the Windows behaviour of `PrivateFiles` beyond doing nothing.
