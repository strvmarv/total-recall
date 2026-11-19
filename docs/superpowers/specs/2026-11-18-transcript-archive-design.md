# Transcript archive: searchable session history that survives compaction

Date: 2026-11-18. Status: draft for review. Parent spec: it is delivered as a docs-only pull request, and each part of the Breakdown
becomes a sub-issue that is defined and built on its own. Decisions that every part must follow are under "Shared decisions".

## Goal

total-recall stores what an agent *concluded* (memories) and what a user *ingested* (the knowledge base). It does not keep what was
*said*. A memory keeps the outcome of a conversation and drops the rejected alternatives, the exact error text, the command that
finally worked and the reasoning. A host's own transcript keeps all of it, but the host compacts it out of the model's context and,
eventually, deletes the file.

The owner's reason, in their words: context matters in every session and across sessions; a larger persisted context store bridges
the gap, allows smaller context windows, and lets a session survive compaction better. The archive is therefore an **extension of the
context window**: the window holds what is relevant now, the archive holds everything else, and the agent (or the user) fetches it
back on demand. It is not a log for its own sake.

What success means:

1. The dialogue of every session of the supported hosts is stored in a store that compaction, decay, the warm sweep and the host's
   own retention never touch.
2. It is searchable by meaning and by exact text, through tools of its own, with a ranking of its own.
3. The **current** session is searchable while it runs, including text that compaction removed from the model's context.
4. Where the host allows it, the agent is told once per compaction that the archive holds what it just lost.
5. Search reaches back across sessions: this session, this project, everything.
6. A secret typed into a transcript does not reach the store.
7. First milestone: Claude Code and pi. Storage, tools, chunking and ranking are host-neutral, so a later host is one adapter
   (OpenCode is the first follow-up: its `opencode.db` is already read by an importer).

Observed on the author's machine: the oldest Claude Code session file is 32 days old, which fits a 30-day cleanup (`cleanupPeriodDays`
is not set and the default was not checked), so history older than a month is already gone. Claude Code compaction does **not**
shorten the file (a `compact_boundary` record is appended to the same JSONL and every earlier message stays), so the archive must keep
what the host deletes and must make the live session searchable, because the file holds the pre-compaction text but nothing searches
it.

## Non-goals

- Hosts other than Claude Code and pi. Cursor, Kiro and the editor hosts (Cline, Windsurf, Zed, VS Code) keep history in stores that
  were not investigated.
- Syncing the archive between machines (Cortex or git sync).
- A web UI section (the web UI `ToolAllowlist` does not list the new tools).
- Links from a memory back to the transcript span it came from.
- Successful tool output, thinking blocks and subagent-internal messages.
- Postgres mode (like `memory_feedback`, the tools are registered in sqlite and cortex modes).

Anti-goals, which would make this a failure even if it works: an archive that makes `memory_search`, `kb_search` or `session_start`
worse (slower, noisier or heavier at startup); a store that can be filled with secrets by accident or picked up by a sync path by
accident; an assistant that can delete the archive or turn it on; a silent, always-on collection of conversations the user did not
agree to.

## Shared decisions

Every part follows these. Each part's own sub-spec adds the detail that belongs to that part only.

### Constraints from the codebase

- `IUsageImporter` / `UsageIndexer` / `usage_watermarks` already have the shape needed: a pure per-host parser streams records, one
  orchestrator writes them, a per-host watermark skips what was read, a failing host never blocks the others, and it runs from
  `session_start`. `ClaudeCodeUsageImporter` already walks `~/.claude/projects/*/*.jsonl`.
- The schema is a sequential migration array in `Storage/Schema.cs` (18 migrations today, frozen once shipped): add a migration, never
  edit one. Vectors are `vec0(embedding float[384])` tables. Tests load the real bge-small model (CI fetches it with
  `scripts/fetch-bge-small.sh`), so a test may use real embeddings.
- `.mcp.json` starts the engine with `cwd` set to the plugin directory (`${CLAUDE_PLUGIN_ROOT}`), so the engine's own working directory
  is **not** the user's project under Claude Code (`docs/hosts/copilot-cli.md` records the same fail-closed effect for project scope).
- Embedder changes run `EmbedderMigration`, then `ReindexCoordinator`, then `EmbeddingReindexer.RunBatched` (background, batched,
  resumable). Anything that stores embeddings takes part.
- `SensitiveContentScanner` (`Infrastructure/Eval/`) has a `Token` and a `PrivateKey` detector and returns category reasons, never
  match positions; redaction needs positions, so it is extended (decision 6). Its regexes are interpreted, not compiled (NativeAOT).
- Cortex sync is queue-driven (`sync_queue`) and git sync stamps a manifest-listed set of entries (`git_sync_entry`): only what is
  explicitly enqueued or listed leaves the machine, so new tables are excluded by default.
- Storage and parser boundaries log through `ExceptionLogger.LogChain`.
- Hooks today: Claude Code's `hooks/hooks.json` has a `SessionStart` hook for `startup`, `resume`, `clear` and `compact`; pi's extension
  re-injects text on `session_compact`; Copilot CLI's `/compact` fires no hook and Cursor's `preCompact` cannot carry context
  (`docs/hosts/`).

### 1. Storage

One database. Tables with the prefix `tx_` in the main `total-recall.db`, reached only through an `ITranscriptStore`, never through the
memory store classes. The archive has **its own migration list and version table** (`tx_schema_version`), run by the `MigrationRunner`
against whichever file `ITranscriptStore` opens (my call: it keeps archive changes out of the main chain, so adding an archive column never
triggers a main-database migration or its full-file backup, and it makes the escape hatch work with no special case). Part 1's migration
creates **every** archive table and column named below, the FTS5 table with its triggers and the `vec0` table included, so no later part
needs a migration unless a shape turns out wrong. Escape hatch: an optional `[transcripts] db_path` (empty by default, meaning
the main database); when set, `ITranscriptStore` opens that file. Nothing may assume the archive and the memory tables share a
connection or a transaction.

Sketch (names are the builder's to settle). `tx_session`: `id`, `host`, `host_session_id`, `project_key`, `project_key_source`
(`remote` | `path`), `cwd`, `started_at`, `ended_at`, `kind` (`interactive` | `automated`), `title` (first real prompt, truncated),
`source_path`, `source_size`, `source_mtime`, `source_fingerprint` (hash of the first N bytes), `ingested_offset`, `exchange_count`,
`last_compaction_exchange`. `tx_chunk`: `id`, `session_id`, `exchange_start`, `exchange_end`, `part`, `chunk_type` (`dialogue` |
`compaction-summary`), `has_error`, `ts`, `redaction_version`, `embedded` (0 until a vector exists), `content` (header line included),
plus a `vec0` table (a chunk may exist without a vector) and an FTS5 table over `content` with sync triggers, as the existing tables
have. Idempotency key: `(host, host_session_id, exchange_start, part)`; a re-run replaces and never duplicates.

Growth. The prototype turned 100 Claude Code sessions into 2,352 chunks and 3.3 MB of text; the author's pi history alone is 416
sessions and 8.7 MB. Estimate (not measured): about 8,000 chunks a month for a heavy user, 100k in a year, 1M in ten. What scales with
size: vector search (a scan of every vector: tens of milliseconds at 100k chunks, about a second at 1M, estimated), kept fast by scoping
every search (session, then project), by running the text index first and ranking only its candidates, and later by sqlite-vec
partitioning or quantization; a full re-embed after a model swap (about 3 chunks a second was measured for one ingest running alone,
so 100k chunks is about nine hours of background work); and the `.bak` copies the data directory already holds from earlier tier
migrations (observed; where they are written was not traced), so the builder must find out whether a migration that touches only `tx_`
tables can skip a full-file backup (with the separate chain above it should not apply to archive migrations at all; the builder confirms).

### 2. Adapters and the common record

One adapter per host, pure like `IUsageImporter`: it reads and never writes the database. It streams a host-neutral record: session
identity, project path, timestamp, role, text, tool stub, error text and a compaction marker. Claude Code compaction is a `system`
record with `subtype: compact_boundary` followed by a user record with `isCompactSummary`; pi's is a `compaction` entry with a
`summary`. `Detect()` is cheap and runs on every pass.

### 3. What is kept

Kept: user text, assistant text, one-line tool stubs (`[Bash: <first 160 chars>]`) and the **error output** of a failed tool call
(truncated to 500 characters). Dropped: successful tool output, thinking blocks, `system-reminder` and `local-command` blocks, the
`EXTREMELY_IMPORTANT` startup injections and host-injected context (pi's `total-recall-context` message), and messages flagged `isMeta`
or `isSidechain`.

Why. In the author's Claude Code history 1,441 successful tool results are 4.1 MB (about 2.8 KB each) and 32 errors are 37 KB. The
conversation already says what mattered about a successful output. Both variants were tested: the query "Parse error no such table
schema_version" found nothing relevant without errors and ranked the exact chunk first (0.58) with them; paraphrased queries worked
either way, mostly through compaction summaries. Dropping successful output keeps the archive near 3% of the raw size.

### 4. Session kind

Each session is `interactive` or `automated`. In the author's history 93 of 100 Claude Code sessions are single-prompt reviewer or
subagent runs ("Review this change for security vulnerabilities", `/develop` reviewer rounds). Prototype rule: a session with one real
user turn that either matches a known automation opening or is longer than 1,500 characters is `automated`. For pi, a session whose
header names a `parentSession` that was open in the same process, or that was started with `PI_SUBAGENT_ID`, is a candidate (the pi host
doc describes how the extension tells a child). The rule is a heuristic, the builder refines it, and the kind is stored so it can be
re-evaluated. Automated sessions are ingested but excluded from search unless the caller asks (`include_automated`): a reviewer prompt
is a real thing to look for, so down-ranking would hide it and exclusion with an opt-in does not.

### 5. Chunking

Chunk by exchange (a user turn and the assistant's reply, tool stubs included), up to about 1,800 characters. A longer reply is split on
paragraph boundaries and each piece repeats the question. Every chunk starts with a header line: `<host> session <id8> | project <path> |
<date> | <kind> | <type> | exchanges <a>-<b>`. The header makes a hit self-describing and puts project and date into the embedding.
Compaction summaries are their own chunk type, split by paragraph. The prototype used the KB path, whose chunker split some chunks
further; a dedicated store keeps chunks as built.

### 6. Secrets

Secrets are **redacted in place** before anything is embedded or written: the matched text becomes `[redacted:<category>]` and the rest
of the chunk stays searchable (the owner chose this over skipping the chunk or storing raw). A redacted secret cannot be recovered from
the archive. `SensitiveContentScanner` gets a method that returns spans (or redacts) for the `Token` and `PrivateKey` detectors; the
existing `Scan` and `ScanSecrets` keep their behavior. A private key is redacted from its `BEGIN` line to its `END` line. Emails are not
redacted (my call: common in development conversation and worth searching by).

Redaction runs on the record text **before chunking**, so a token cannot be split across a chunk boundary and missed. Chunks carry
`redaction_version`; when a later release extends the detector set, `transcripts rescan` re-applies it to the stored text in place (the
archive keeps the text, so this works after the host's file is gone), and a chunk written under an older set keeps what it had until a
rescan runs: the docs state that residual risk. The current detectors name token shapes (`ghp_`, `github_pat_`, `glpat-`, `xox*-`, `sk-`,
`AKIA`) and private key headers and miss `password=...`, bearer tokens, connection strings and `.env`-style lines; extending them is in
scope of the part that adds redaction, how far is the builder's to settle, with the residual risk stated. Dropping successful tool
output (decision 3) already removes the largest source of leaked secrets.

### 7. Ingestion

Three triggers, one code path: a **bounded background pass** from `session_start`, per host, in the spirit of `UsageIndexer` and the
background re-embed (never on the MCP boot path; a failing host never blocks another; a per-pass time and chunk budget, the rest on the
next start); **sync-on-read** before a transcript tool runs, ingesting the current session's new tail; and the `transcripts backfill`
command. Per session the watermark is the file size and offset, with a fingerprint of the first bytes so a rewritten or replaced file is
re-read and not appended to. A source file that disappears keeps its archived chunks.

**Embedding never blocks a search** (my call: about 3 chunks a second was measured, and a first backfill is hours). A chunk is written
as text first (`embedded = 0`, in the FTS index at once) and embedded in background batches. Sync-on-read embeds the new tail only within
a small budget (about 2 seconds or 8 chunks; the builder tunes it) and leaves the rest pending. Search merges vector candidates with
text candidates, so a chunk without a vector is still found by its words, and the response says how many candidates were text-only
(`pendingEmbedding`). The same path covers an embedder that is not ready (model missing, a re-embed in progress). The embedder fingerprint is recorded with the
archive's vectors; a vector whose fingerprint differs from the running embedder's is ignored by search (the chunk is found by its words)
and never mixed with vectors of another model, from the moment the embedding exists until the re-embed of decision 14 lands.

### 8. Project identity

`project_key` is computed at ingest by `ProjectResolver` from the session's recorded `cwd`. When that directory no longer exists
(deleted worktrees are the common case for historical sessions), the path is reduced to its repository root (a path under
`.claude/worktrees/<name>` or `.zoid/worktrees/<name>` maps to the repository that holds it), the resolver runs on that root if it
exists, and otherwise the key is the root's folder name with `project_key_source = path`. `cwd` is stored with every session, so the key
is recomputable (`transcripts reproject`) without a migration when the rule changes. Whether `ProjectResolver` handles worktree paths is
unchecked.

### 9. Tools and ranking

Three MCP tools, registered in sqlite and cortex modes (the archive is local in both), and management on the CLI only (`transcripts
enable|disable|backfill|status|prune|forget|rescan|reproject`), never MCP.

- `transcript_search { query, scope: "session"|"project"|"all", project?, session?, since?, include_automated?, top_k }`. Default scope
  `project`. The project and the current session come from, in order: the explicit `project` and `session` arguments; the live marker
  of decision 13 (its recorded `cwd` and session, which is what makes the default right under Claude Code, because the engine's own
  working directory is the plugin directory); the engine's working directory. With none of them the default is `all` (my call: unlike
  pins this is the user's own history, and failing closed would make the tool useless outside a repository). Until the marker exists
  (Parts 1 to 3), a default-scope call from Claude Code therefore searches everything, and the explicit arguments are how the scopes are
  tested. Returns short excerpts, each with `id`, session, project, date, exchange range, chunk type, score and
  about 300 characters of text.
- `transcript_get { id | session + exchange range, context }`. Expands a hit into its surrounding exchanges, capped to a token budget
  (default about 2,000 tokens) with a `truncated` flag.
- `transcript_sessions { project?, since?, limit, include_automated? }`. Sessions labeled by `title`.

Ranking is its own, not the memory ranker: candidates from the vector and text indexes together; scores used **relatively** (raw scores
in the prototype were 0.2 to 0.6, so no absolute threshold applies); a small boost for compaction summaries, a mild one for recency, a
small one for an error chunk when the query looks like an error string; at most two chunks per session in the top results. Weights are
the builder's to tune against the fixtures.

### 10. Consent and defaults (my call, to confirm)

The archive is **opt-in**: off until `transcripts enable` or `[transcripts] enabled = true`. While off: the three tools stay registered
(so `catalog.json`, the handler count and the announce files do not depend on a setting) and each returns `{enabled: false, hint: "run
total-recall transcripts enable"}`; the `session_start` pass, sync-on-read, the marker writer and the breadcrumb do nothing; nothing is
read from any host directory; `backfill` refuses. **The assistant cannot turn it on:** `transcripts.enabled` joins the `config_set`
denylist beside `storage.mode` and the `git_sync` settings (MCP and CLI `config set` both refuse it). The node hooks and the marker writer must know whether the archive is on without the engine and without reading TOML: the engine mirrors
`transcripts.enabled` into a sentinel file `<dataDir>/live/enabled` at startup, `transcripts enable|disable` writes it too, and the
hooks read only that file (my call; a hand edit of `config.toml` reaches the hooks at the next engine start). A one-line what's-new
notice and the README point at it.

### 11. Retention

`[transcripts] retention_days`, default 0 (keep forever): the archive exists to keep what the host deletes, so a default that deletes
would defeat it. `prune --older-than` and the setting remove old sessions. There is no pinned or sticky concept for transcripts in v1.

### 12. The breadcrumb

When a compaction is detected, the agent is told once, in a few lines, that the earlier text of this session is archived and searchable
and how (`transcript_search`, scope `session`). The text does **not** promise an exchange count, and the hook does **not** trigger an
ingest (a hook has a tight time budget and embedding is slow): sync-on-read does the catching up when the model searches. It fires only
when the archive is enabled. Delivery: Claude Code through the existing `SessionStart` hook with source `compact`; pi through the
extension's existing after-compaction message; every host **without** such a hook through a `transcriptHint` field on the next `memory_search` or
`session_refresh` response, once. The hint is never sent to a host that has the hook (Claude Code, pi; the engine knows the host from the
`HostIds` of the what's-new notice), so a compaction is announced by one channel only. `memory_search` and `session_refresh` do no file
reading for this: they only read a flag that the engine's own background tick sets (a cheap tail scan of the current session's file, off
the request path, every 15 to 30 seconds while the archive is on, that also keeps the index fresh) when it sees a compaction marker the
agent was not told about. The hint fires only if the agent calls a tool, which the startup instructions already ask for on every
question; its reliability has not been measured, and in this milestone no host with an adapter lacks a hook, so the path is covered by
unit-level tests only.

### 13. Knowing which session is current

The MCP server knows neither the host session id nor the transcript path, and the owner runs several sessions in one repository at once,
so "newest file for this cwd" is wrong exactly when it matters. Proposal, to be confirmed by a spike: **key a marker by the host process,
not the directory.** Each host session runs its own MCP server, spawned by the shim, which the host spawned; the hook or extension that
handles the same session runs under the same host process. The hook (Claude Code) or the extension (pi) writes
`<dataDir>/live/<host>-<host process id>.json` (`{session_id, transcript_path, cwd, at}`) and the engine finds its own entry by walking
its ancestor process ids (Linux and macOS; Windows needs its own method, which the spike settles or declares unsupported). The writer is
a separate, cheap, pure-node step on `SessionStart` and on every prompt, **independent of the pinned floor** (it works with
`floor_enabled = false` and does not need the engine to be provisioned); stale markers are pruned. If the match fails, sync-on-read
falls back to the most recently modified session file for the project and the tool response says `currentSession: "guessed"`.

### 14. Embedder changes

The archive's vectors take part in `EmbedderMigration` and the `ReindexCoordinator`. The builder decides the order (memories first, so
`memory_search` recovers first) and whether transcript search runs on text alone during the re-embed; `session_start` and `status` show
`backgroundTasks.transcripts` progress.

### 15. Isolation from sync

Nothing adds a `tx_` table to Cortex or git sync. A test pins that: the sync queue and the git sync manifest contain no archive entry
type, and no setting can turn a sync of the archive on.

### 16. Evidence and what was not checked

Observed on the author's machine (2026-11-18) with throwaway scripts that are not committed: the Claude Code and pi record formats
(`compact_boundary`, `isCompactSummary`, `compaction`); the size and tool-result figures above; five conversational queries returning the
right session first through the KB path (raw hybrid scores 0.2 to 0.47); the error-string query result; one ingest rate. Ranking weights,
the 1,800-character chunk size and the session-kind rule are from the prototype and untuned.

Not checked: vector-search latency at 100k and 1M chunks (estimates); that `ProjectResolver` resolves a worktree path or a vanished
directory to the repository's slug; the hook payload fields of pi and Claude Code at compaction time beyond what the host docs state;
where the `.bak` files are written; Codex, Gemini CLI, Cursor, Kiro, Copilot and editor-host transcript stores; how reliably a model acts
on the tool-response hint.

### 17. Fixtures

Tests use a committed corpus of **synthetic** sessions (written for the tests, not taken from anyone's history): a compaction, an error,
an automated session and a planted secret of each detector shape, with a fixture query set modelled on the prototype's queries (a
decision recalled by topic, a question answered by a compaction summary, an exact error string, a how-to). The bar is the right session
in the top three; no recall target is set for v1, and weights tuned on synthetic data are tuned lightly. Part 1 builds the corpus and the harness
(Claude Code shaped sessions), Part 2 adds the query set, Part 6 adds pi sessions; tests use real embeddings. An optional script runs the
same checks against the user's own archive and is not a gate.

### 18. Chores every part owns

Each part that adds or changes a tool regenerates `catalog.json` (the drift guard in `.github/develop.md` fails otherwise), keeps the handler
count in `AGENTS.md` and the docs that state it correct, and updates the announce files that list tools and the host pages it touches
(`docs/hosts/`); each documents the surface it ships. The what's-new notice is written at release time, not by a part.

## Breakdown

### Part 1: Opt-in, archive store, redaction and a text-only Claude Code backfill
Scope: The `[transcripts]` settings and `transcripts enable|disable`, with `transcripts.enabled` refused by `config_set` (decision 10); the migration, `ITranscriptStore` and the optional `db_path` (decision 1); span-returning redaction and the extended detectors (decision 6); the Claude Code adapter, the kept and dropped rules, compaction markers, session kind and the chunker (decisions 2 to 5); project identity (decision 8); per-session watermarks and idempotent ingest; `transcripts backfill` and `status`; the sentinel file of decision 10; the fixture corpus and harness (decision 17). Chunks are stored as text (`embedded = 0`) and are found by neither a tool nor an index yet. Leaves to other parts: every MCP tool, vectors and embedding, the live session, the breadcrumb, pi, prune, forget, rescan, reproject and the re-embed integration.
Depends on: none
Done when: With the archive enabled, backfill ingests a fixture of Claude Code sessions (one with a compaction, one with an error, one automated) so that status (and a direct read of the store in the tests) shows sessions and chunks per project and kind; a second run adds nothing; successful tool output is not stored; a planted token of each detector shape is stored as a redaction marker; with the archive off, backfill refuses, nothing is read from the host directory, and `config_set` refuses to enable it.

### Part 2: Search the archive by meaning and by words
Scope: The `transcript_search` tool and its off-state response; the background embedding of stored chunks (a `vec0` table, a worker with a per-pass budget, resumable, working when the embedder is not ready); the merge of vector and text candidates; the ranking of decision 9 (relative scores, summary and error boosts, two chunks per session, `include_automated`, the `project` and `session` arguments, scope `session`, `project` and `all`, `since`); the embedder fingerprint of decision 7; embedding progress in `status`; `pendingEmbedding` in the response; the fixture query set and its top-three bar. Default-scope resolution from the live marker is Part 4's; here the scopes are exercised with explicit arguments. Leaves to other parts: `transcript_get`, `transcript_sessions`, sync-on-read and the current session, the breadcrumb, pi, and the re-embed on a model change.
Depends on: Part 1
Done when: Against the fixture corpus, a topic query, a query answered by a compaction summary and an exact error string each return the right session in the top three; an automated session appears only with `include_automated`; a chunk without a vector is found by its words and counted in `pendingEmbedding`; with the archive off the tool answers `enabled: false`.

### Part 3: Read and browse archived sessions
Scope: The `transcript_get` tool (a hit id or a session with an exchange range, a context size, a token budget with a `truncated` flag) and the `transcript_sessions` tool (project, since, limit, `include_automated`, labelled by title), each with the off-state response. Leaves to other parts: sync-on-read, the current session, and everything not named here.
Depends on: Part 2
Done when: Given a hit from search, `transcript_get` returns the surrounding exchanges within the budget and flags truncation; `transcript_sessions` lists a project's sessions newest first with their titles and hides automated ones unless asked; both answer `enabled: false` while the archive is off.

### Part 4: Make the current Claude Code session searchable while it runs
Scope: The process-keyed marker and its pure-node writer, a second hook entry on `SessionStart` and `UserPromptSubmit` beside the floor's and independent of it, that reads only the sentinel of decision 10 (decision 13); finding the current session by ancestor process ids, with the disclosed `currentSession: "guessed"` fallback; sync-on-read for the three transcript tools with the small embed budget (decision 7); the bounded background pass from `session_start` and the background tick of decision 12 for the current session; default-scope resolution from the marker; a spike that confirms the marker approach on Linux and macOS and states what Windows can do. Leaves to other parts: the breadcrumb, pi, and anything that is not Claude Code.
Depends on: Part 2, Part 3
Done when: Text added to a running Claude Code session after the last pass is found by `transcript_search` with no hook running other than the marker writer, including text a compaction removed from the model's context; two parallel sessions in one directory each resolve to their own file on Linux and macOS (simulated host processes in the tests, plus a manual check on a real Claude Code; or the spike's finding for a platform is recorded as a known limit); `session_start` returns without waiting for the background pass.

### Part 5: Tell Claude Code's agent what compaction removed
Scope: The compaction detection in the background tick and in sync-on-read; the flag that `memory_search` and `session_refresh` read without touching a file (decision 12); the breadcrumb text (decision 12, no count, no ingest in the hook) delivered once per compaction by the existing Claude Code `SessionStart` hook with source `compact`; the `transcriptHint` on the next `memory_search` or `session_refresh` response for hosts without a usable hook (never for Claude Code or pi); both silent while the archive is off. Leaves to other parts: pi.
Depends on: Part 4
Done when: After a compaction in Claude Code the model's next turn carries the breadcrumb exactly once; in a unit-level test a host with no compaction hook gets `transcriptHint` once on its next tool response and not again for the same compaction, and Claude Code and pi never get it; `memory_search` reads no file; neither appears with the archive off.

### Part 6: Archive pi sessions and tell pi's agent what compaction removed
Scope: The pi adapter (the `compaction` entry, the kept and dropped rules, pi's session kind rule with `parentSession` and `PI_SUBAGENT_ID`), registered beside the Claude Code adapter; the pi extension's marker writer and its existing after-compaction message carrying the breadcrumb; pi sessions in `backfill`, `status` and the three tools. Leaves to other parts: other hosts.
Depends on: Part 4, Part 5
Done when: A fixture of pi sessions (with a compaction, an error and a subagent child) backfills and is searchable like the Claude Code fixture, the child as `automated`; text added to a running pi session is found by search; after a pi compaction the next turn carries the breadcrumb once.

### Part 7: Retention, forgetting, repair and re-embedding
Scope: `[transcripts] retention_days` and `transcripts prune --older-than`; `transcripts forget` by session, project or everything, with confirmation; `transcripts rescan` (re-apply redaction to stored text) and `transcripts reproject`; the archive's vectors in `EmbedderMigration` and `ReindexCoordinator` with `backgroundTasks.transcripts` in `session_start` and `status`; the test that no sync path contains an archive entry type (decision 15); whether a migration that touches only archive tables can skip the full-file backup; the user documentation for its own commands and a feature overview page that lists what exists by then. Leaves to other parts: everything built by Parts 1 to 6.
Depends on: Part 1, Part 2, Part 4
Done when: Prune and forget remove exactly the sessions asked for and their chunks and vectors; rescan redacts a planted token that an older detector set missed; after an embedder change the archive re-embeds in the background while search keeps working on text; the sync-isolation test passes; the documentation covers retention, forgetting, rescan and reproject and the overview page links every part's page.
