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
4. In Claude Code and pi, the agent is told once per compaction that the archive holds what it just lost.
5. Search reaches back across sessions: this session, this project, everything.
6. A secret typed into a transcript does not reach the store.
7. The user can enable, delete and bound the archive, and nothing the assistant's tools can do enables, redirects or deletes it.
8. First milestone: Claude Code and pi. Storage, tools, chunking and ranking are host-neutral, so a later host is one adapter
   (OpenCode is the first follow-up: its `opencode.db` is already read by an importer).

Observed on the author's machine: the oldest Claude Code session file is 32 days old, which fits a 30-day cleanup (`cleanupPeriodDays`
is not set and the default was not checked), so history older than a month is already gone. Claude Code compaction does **not**
shorten the file (a `compact_boundary` record is appended to the same JSONL and every earlier message stays), so the archive must keep
what the host deletes and must make the live session searchable, because the file holds the pre-compaction text but nothing searches
it.

## Non-goals

- Hosts other than Claude Code and pi. Cursor, Kiro and the editor hosts (Cline, Windsurf, Zed, VS Code) keep history in stores that
  were not investigated.
- Telling the agent about a compaction on a host that has no compaction hook (a hint on a tool response). No host with an adapter in
  this milestone lacks a hook; the first follow-up host that does gets it.
- Syncing the archive between machines (Cortex or git sync).
- A web UI section (the web UI `ToolAllowlist` does not list the new tools).
- Links from a memory back to the transcript span it came from.
- Successful tool output, thinking blocks and subagent-internal messages.
- Postgres mode (like `memory_feedback`, the tools are registered in sqlite and cortex modes).

Anti-goals, which would make this a failure even if it works: an archive that makes `memory_search`, `kb_search` or `session_start`
worse (slower, noisier or heavier at startup); a store that can be filled with secrets by accident or picked up by a sync path by
accident; an assistant tool that can enable, redirect or delete the archive; a silent, always-on collection of conversations the user did
not agree to. An agent with a shell can do whatever the user can (it can edit `config.toml`), as with the git sync settings; the
guarantee is about the MCP tools and `config_set`, and the management commands that enable or delete need a terminal (decision 10).

## Shared decisions

Every part follows these. Each part's own sub-spec adds the detail that belongs to that part only.

### Constraints from the codebase

- `IUsageImporter` / `UsageIndexer` / `usage_watermarks` already have the shape needed: a pure per-host parser streams records, one
  orchestrator writes them, a per-host watermark skips what was read, a failing host never blocks the others, and it runs from
  `session_start`. `ClaudeCodeUsageImporter` already walks `~/.claude/projects/*/*.jsonl`.
- The schema is a sequential migration array in `Storage/Schema.cs` (18 migrations today, frozen once shipped). `MigrationRunner` is
  tied to `_schema_version`, `Migrations[]` and `_meta`, so running a second list against a chosen file needs a refactor (Part 1).
  Vectors are `vec0(embedding float[384])` tables, searched by `VectorSearch` with a global KNN of `k = topK * 2` and a filter
  afterwards. The lockfile pins sqlite-vec 0.1.9. Tests load the real bge-small model (CI fetches it with `scripts/fetch-bge-small.sh`),
  so a test may use real embeddings.
- Embedder changes run `EmbedderMigration`, then `ReindexCoordinator`, then `EmbeddingReindexer.RunBatched` (background, batched,
  resumable). Anything that stores embeddings takes part.
- `SensitiveContentScanner` (`Infrastructure/Eval/`) has a `Token` and a `PrivateKey` detector and returns category reasons, never
  match positions; redaction needs positions (decision 6). Its regexes are interpreted, not compiled (NativeAOT). `SyncSettingsGuard`
  refuses `config_set` for whole tables (`git_sync`, `storage.mode`).
- `.mcp.json` starts the engine with `cwd` set to the plugin directory (`${CLAUDE_PLUGIN_ROOT}`), so under Claude Code the engine's own
  working directory is **not** the user's project, and on a git-sourced install `ProjectResolver` resolves it to the plugin's own
  repository slug. pi registers its server with no `cwd`, so there the engine sees the user's directory.
- Cortex sync is queue-driven (`sync_queue`) and git sync stamps a manifest-listed set of entries (`git_sync_entry`): only what is
  explicitly enqueued or listed leaves the machine, so new tables are excluded by default.
- Storage and parser boundaries log through `ExceptionLogger.LogChain`.
- Hooks today: Claude Code's `hooks/hooks.json` has a `SessionStart` hook for `startup`, `resume`, `clear` and `compact`; pi's extension
  re-injects text on `session_compact`; Copilot CLI's `/compact` fires no hook and Cursor's `preCompact` cannot carry context
  (`docs/hosts/`).

### 1. Storage

One database. Tables with the prefix `tx_` in the main `total-recall.db`, reached only through an `ITranscriptStore`, never through the
memory store classes. The archive has **its own migration list and version table** (`tx_schema_version`), run against whichever file
`ITranscriptStore` opens (my call: it keeps archive changes out of the main chain, so adding an archive column never triggers a
main-database migration, and the escape hatch works with no special case). Each part adds its own migration to that list when it needs
one. Escape hatch: an optional `[transcripts] db_path` (empty by default, meaning the main database); nothing may assume the archive and
the memory tables share a connection or a transaction.

Sketch (names are the builder's to settle). `tx_session`: `id`, `host`, `host_session_id`, `project_key`, `project_key_source`
(`remote` | `path`), `cwd`, `started_at`, `ended_at`, `kind` (`interactive` | `automated`), `title` (first real prompt, truncated),
`source_path`, `source_size`, `source_mtime`, `source_fingerprint` (hash of the first N bytes), `ingested_offset`, `exchange_count`.
`tx_chunk`: `id`, `session_id`, `exchange_start`, `exchange_end`, `part`, `chunk_type` (`dialogue` | `compaction-summary`), `has_error`,
`ts`, `redaction_version`, `embedded` (0 until a vector exists), `content` (header line included), plus an FTS5 table over `content` with
sync triggers, as the existing tables have. The vector table arrives with the search part (decision 9). Idempotency key:
`(host, host_session_id, exchange_start, part)`.

Safety of the file. The archive connection sets `secure_delete` (or the delete commands vacuum), so deleted text does not linger in free
pages, the write-ahead log or the FTS and vector shadow tables; the archive file, when it is separate, and everything under `<dataDir>/live/`
are created user-only (0600, directories 0700).

Growth. The prototype turned 100 Claude Code sessions into 2,352 chunks and 3.3 MB of text; the author's pi history alone is 416
sessions and 8.7 MB. Estimate (not measured): about 8,000 chunks a month for a heavy user, 100k in a year, 1M in ten. What scales with
size: vector search, a scan of many vectors (tens of milliseconds at 100k chunks, about a second at 1M, estimated), kept bounded by the
scoping of decision 9; and a full re-embed after a model swap (about 3 chunks a second was measured for one ingest running alone, so 100k
chunks is about nine hours of background work).

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

Each session is `interactive` or `automated`, decided from the session file alone (the adapter is pure and also runs for historical
sessions). In the author's history 93 of 100 Claude Code sessions are single-prompt reviewer or subagent runs ("Review this change for
security vulnerabilities", `/develop` reviewer rounds). Prototype rule: a session with one real user turn that either matches a known
automation opening or is longer than 1,500 characters is `automated`. For pi the file signal is a header that names a `parentSession`
**and** at most one real user turn (a child gets one task prompt; a `/fork` of a real conversation also names a parent, but carries the
parent's many turns, so it stays `interactive`). A live pi session may carry the authoritative kind in its marker (decision 12). The
rule is a heuristic, the builder refines it, and the kind is stored so it can be re-evaluated. Automated sessions are ingested but
excluded from search unless the caller asks (`include_automated`): a reviewer prompt is a real thing to look for, so down-ranking would
hide it and exclusion with an opt-in does not.

### 5. Chunking

Chunk by exchange (a user turn and the assistant's reply, tool stubs included), up to about 1,800 characters. A longer reply is split on
paragraph boundaries and each piece repeats the question. Every chunk starts with a header line: `<host> session <id8> | project <path> |
<date> | <kind> | <type> | exchanges <a>-<b>`. The header makes a hit self-describing and puts project and date into the embedding.
Compaction summaries are their own chunk type, split by paragraph. The prototype used the KB path, whose chunker split some chunks
further; a dedicated store keeps chunks as built.

### 6. Secrets

Secrets are **redacted in place** before anything is embedded or written: the matched text becomes `[redacted:<category>]` and the rest
of the chunk stays searchable (the owner chose this over skipping the chunk or storing raw). A redacted secret cannot be recovered from
the archive. `SensitiveContentScanner` gets a method that returns spans (or redacts); the existing `Scan` and `ScanSecrets` keep their
behavior. A private key is redacted from its `BEGIN` line to its `END` line. Emails are not redacted (my call: common in development
conversation and worth searching by).

The detector set is the existing token shapes (`ghp_`, `github_pat_`, `glpat-`, `xox*-`, `sk-`, `AKIA`) and private keys **plus a minimum
extra set**, because tool stubs (commands) and error output are where bearer tokens, passwords and connection strings appear:
`Authorization:` and `Bearer` values, credentials in URLs (`scheme://user:pass@host`), and assignments to secret-named variables
(`*_KEY`, `*_TOKEN`, `*_SECRET`, `PASSWORD`, `PASSWD`). Anything beyond that is the builder's to settle; the documentation lists exactly
what is detected and what is not, and states the residual risk.

Redaction runs on the record text **before chunking**, so a token cannot be split across a chunk boundary and missed. Chunks carry
`redaction_version`; when a later release extends the detector set, `transcripts rescan` re-applies it to the stored text in place (the
archive keeps the text, so this works after the host's file is gone), and a chunk written under an older set keeps what it had until a
rescan runs. Dropping successful tool output (decision 3) already removes the largest source of leaked secrets.

### 7. Ingestion

Three triggers, one code path: a **bounded background pass** from `session_start`, per host, in the spirit of `UsageIndexer` and the
background re-embed (never on the MCP boot path; a failing host never blocks another; a per-pass time and chunk budget, the rest on the
next start); **sync-on-read** before a transcript tool runs, ingesting the current session's new tail; and the `transcripts backfill`
command. A source file that disappears keeps its archived chunks.

Resuming without losing text. A session is read incrementally, but chunks are per exchange, so a pass must never replace a complete chunk
with a partial one. The watermark is the byte offset of the start of the **last incomplete exchange**, at a complete line boundary; a
trailing partial JSONL line is never consumed. A pass re-reads from the watermark, and for every exchange it rebuilds it replaces **all
parts** of that exchange as one set (so a changed number of parts leaves no orphan). A fingerprint of the first bytes detects a rewritten
or replaced file, which is re-read from the start. A session grown across two passes must equal the same session ingested once.

**Embedding never blocks a search** (my call: about 3 chunks a second was measured, and a first backfill is hours). A chunk is written
as text first (`embedded = 0`, in the FTS index at once) and embedded in background batches. Sync-on-read embeds the new tail only within
a small budget (about 2 seconds or 8 chunks; the builder tunes it) and leaves the rest pending. Search merges vector candidates with
text candidates, so a chunk without a vector is still found by its words, and the response says how many candidates were text-only
(`pendingEmbedding`). The same path covers an embedder that is not ready (model missing, a re-embed in progress). The embedder
fingerprint is recorded with the archive's vectors; a vector whose fingerprint differs from the running embedder's is ignored by search
and never mixed with vectors of another model.

### 8. Project identity

`project_key` is computed at ingest by `ProjectResolver` from the session's recorded `cwd`. When that directory no longer exists
(deleted worktrees are the common case for historical sessions; the resolver returns null for a missing directory), the path is reduced
to its repository root (a path under `.claude/worktrees/<name>` or `.zoid/worktrees/<name>` maps to the repository that holds it), the
resolver runs on that root if it exists, and otherwise the key is the root's folder name with `project_key_source = path`. `cwd` is
stored with every session, so the key is recomputable (`transcripts reproject`) without a migration when the rule changes. A worktree
whose directory still exists resolves through its `.git` file to the repository's slug (checked in `ProjectResolver`).

### 9. Tools and ranking

Three MCP tools, registered in sqlite and cortex modes (the archive is local in both), and management on the CLI only (`transcripts
enable|disable|backfill|embed|status|prune|forget|rescan|reproject`), never MCP.

- `transcript_search { query, scope: "session"|"project"|"all", project?, session?, since?, include_automated?, top_k }`. Default scope
  `project`. The project and the current session come from, in order: the explicit `project` and `session` arguments; the live marker of
  decision 12 (its recorded `cwd` and session); the engine's working directory **only for a host whose server inherits the user's
  directory** (pi), never under Claude Code, where it is the plugin directory. With none of them the default is `all` (my call: unlike
  pins this is the user's own history, and failing closed would make the tool useless outside a repository). Until the marker exists
  (Parts 1 to 4) a default-scope call from Claude Code therefore searches everything, and the explicit arguments are how the scopes are
  tested.
- `transcript_get { id | session + exchange range, context }`. Expands a hit into its surrounding exchanges, capped to a token budget
  (default about 2,000 tokens) with a `truncated` flag.
- `transcript_sessions { project?, since?, limit, include_automated? }`. Sessions labeled by `title`.

Scoped vector search. A plain `vec0` table is searched by a global nearest-neighbour scan with the filter applied afterwards, so a
`session` or `project` query can come back empty once other sessions fill the neighbour slots, and that is the headline use. The search
part therefore decides, with a spike on sqlite-vec 0.1.9 (believed to support partition keys and metadata columns; unconfirmed), how
the vector table is built: a partition key on the project (and a metadata column for the session), or, if that is not available, an
over-fetch that grows `k` until the scoped result is full or a cap is reached, with the text candidates always merged in. The vector
table is created by that part's own migration, so its shape is free until then. "Text index first, rank only its candidates" is **not**
used for vector retrieval: it would lose paraphrase hits.

Ranking is its own, not the memory ranker: candidates from the vector and text indexes together; scores used **relatively** (raw scores
in the prototype were 0.2 to 0.6, so no absolute threshold applies); a small boost for compaction summaries, a mild one for recency, a
small one for an error chunk when the query looks like an error string; at most two chunks per session in the top results. Weights are
the builder's to tune against the fixtures.

### 10. Consent and defaults (my call, to confirm)

The archive is **opt-in**: off until the user enables it. While off: the three tools stay registered (so `catalog.json`, the handler
count and the announce files do not depend on a setting) and each returns `{enabled: false, hint: "run total-recall transcripts enable"}`;
the `session_start` pass, sync-on-read, the marker writer and the breadcrumb do nothing; nothing is read from any host directory;
`backfill` refuses.

What the assistant's tools cannot do: `config_set` (MCP and CLI `config set`) refuses the **whole `transcripts` table and every key
beneath it** (as `SyncSettingsGuard` does for `git_sync`), so it cannot enable, disable, redirect (`db_path`) or shorten the retention of
the archive; no MCP tool manages it. What it can: an agent with a shell can edit `config.toml` or run the CLI, as with the git sync
settings. So `transcripts enable`, `forget` and `prune` ask for confirmation on a terminal and refuse when standard input is not one, with
no override flag; scripts and the documented hand edit use `config.toml`.

The node hooks and the marker writer must know whether the archive is on without the engine and without reading TOML: the engine mirrors
`transcripts.enabled` into a sentinel file `<dataDir>/live/enabled` at startup, `transcripts enable|disable` writes it too, and **both the
hooks and the engine's tool calls check the sentinel per call** (a cheap stat), so a change applies at once and a hand edit of
`config.toml` reaches both at the next engine start. A hook tests the file in shell before it starts node, so a user with the archive off
pays no extra process on each prompt. A one-line what's-new notice and the README point at the setting.

### 11. The breadcrumb

When a compaction happens, the agent is told once, in a few lines, that the earlier text of this session is archived and searchable and
how (`transcript_search`, scope `session`). The text does **not** promise an exchange count, and the hook does **not** trigger an ingest
(a hook has a tight time budget and embedding is slow): sync-on-read does the catching up when the model searches. It is said only when
the archive is enabled. Delivery: Claude Code through the existing `SessionStart` hook with source `compact` (the source is the signal;
the engine is not involved); pi through the extension's existing after-compaction message. No tool-response hint in this milestone (see
Non-goals).

### 12. Knowing which session is current

The MCP server knows neither the host session id nor the transcript path, and the owner runs several sessions in one repository at once,
so "newest file for this cwd" is wrong exactly when it matters. Proposal, to be confirmed by a spike: **key a marker by the host process,
not the directory.** Each host session runs its own MCP server, spawned by the shim, which the host spawned; the hook or extension that
handles the same session runs under the same host process. The hook (Claude Code) or the extension (pi) writes
`<dataDir>/live/<host>-<host process id>.json` (`{session_id, transcript_path, cwd, kind?, at}`) and the engine finds its own entry by
walking its ancestor process ids (Linux and macOS; Windows needs its own method, which the spike settles or declares unsupported). The
writer is a separate, cheap, pure-node step on `SessionStart` and on every prompt, **independent of the pinned floor** (it works with
`floor_enabled = false` and does not need the engine to be provisioned); stale markers are pruned. The engine accepts a marker's
`transcript_path` only under the host's own transcript root (`~/.claude/projects`, `~/.pi/agent/sessions`), so a marker cannot make it
ingest an arbitrary file. If the match fails, sync-on-read falls back to the most recently modified session file for the project and the
tool response says `currentSession: "guessed"`.

### 13. Fixtures

Tests use a committed corpus of **synthetic** sessions (written for the tests, not taken from anyone's history): a compaction, an error,
an automated session and a planted secret of each detector shape, with a fixture query set modelled on the prototype's queries (a
decision recalled by topic, a question answered by a compaction summary, an exact error string, a how-to). The bar is the right session
in the top three; no recall target is set for v1, and weights tuned on synthetic data are tuned lightly. Part 2 builds the corpus and
the harness (Claude Code shaped sessions), Part 3 adds the query set, Part 7 adds pi sessions; tests use real embeddings. An optional
script runs the same checks against the user's own archive and is not a gate.

### 14. Chores every part owns

Each part that adds or changes a tool regenerates `catalog.json` (the drift guard in `.github/develop.md` fails otherwise), keeps the
handler count in `AGENTS.md` and the docs that state it correct, and updates the announce files that list tools and the host pages it
touches (`docs/hosts/`); each documents the surface it ships. The what's-new notice is written at release time, not by a part.

### 15. Evidence and what was not checked

Observed on the author's machine (2026-11-18) with throwaway scripts that are not committed: the Claude Code and pi record formats
(`compact_boundary`, `isCompactSummary`, `compaction`); the size and tool-result figures above; five conversational queries returning the
right session first through the KB path (raw hybrid scores 0.2 to 0.47); the error-string query result; one ingest rate. Ranking weights,
the 1,800-character chunk size and the session-kind rules are from the prototype and untuned.

Not checked: sqlite-vec 0.1.9 partition and metadata support; vector-search latency at 100k and 1M chunks (estimates); the hook payload
fields of pi and Claude Code at compaction time beyond what the host docs state; Codex, Gemini CLI, Cursor, Kiro, Copilot and editor-host
transcript stores. (The data directory holds `.bak` files beside the database; no code that writes them was found, so they are not
relied on.)

## Breakdown

### Part 1: Opt-in, the archive store, forgetting and isolation from sync
Scope: The `[transcripts]` settings and the `transcripts` commands `enable`, `disable`, `status` and `forget` (by session, project or everything), with enable and forget asking on a terminal and refusing otherwise; the whole `transcripts` table refused by `config_set` over MCP and the CLI (decision 10); the sentinel file mirrored by the engine and written by enable and disable; the refactor that lets `MigrationRunner` run a named migration list against a named version table on a chosen connection; the first archive migration, creating `tx_session`, `tx_chunk` and the text index with its triggers; `ITranscriptStore` with the optional `db_path`; `secure_delete` and the user-only file modes (decision 1); the three tools registered with their off-state answer only; the test that no Cortex or git sync path carries an archive entry type. Leaves to other parts: every adapter, redaction, ingestion, vectors and the search behaviour of the tools.
Depends on: none
Done when: Enabling and disabling work only from a terminal and write the sentinel; `config_set` over MCP and the CLI refuses every key under `transcripts`; with test rows in the store, `forget` removes them from the tables and the text index and leaves none of their text in the file; the archive tables appear in the main database or in the `db_path` file while the main version table is unchanged; the three tools answer that the archive is off until it is enabled; the sync-isolation test passes.

### Part 2: Back up Claude Code sessions, with secrets redacted
Scope: The Claude Code adapter and the common record (decision 2); the kept and dropped rules and compaction markers (decision 3); session kind (decision 4); project identity (decision 8); the chunker (decision 5); span-returning redaction in `SensitiveContentScanner` with the detector set of decision 6, applied before chunking and to tool stubs and error text; per-session watermarks with the resume rule of decision 7; `transcripts backfill` and the counts in `status`; the fixture corpus and harness of decision 13. Chunks are stored as text (`embedded = 0`) and no tool finds them yet. Leaves to other parts: vectors and embedding, every tool's behaviour, the `session_start` pass, the live session, the breadcrumb, pi, prune, rescan, reproject and the re-embed.
Depends on: Part 1
Done when: With the archive enabled, backfill of the fixture sessions (one with a compaction, one with an error, one automated) stores them so that `status` and a direct read of the store show sessions and chunks per project and kind; a second run adds nothing; a session grown across two passes equals the same session ingested once; successful tool output is not stored; a planted secret of each listed shape, also one inside a tool stub and one inside an error, is stored as a redaction marker; deleting a source file after ingest leaves its chunks in the archive; with the archive off, backfill refuses and reads nothing from the host directory.

### Part 3: Search the archive by meaning and by words
Scope: The behaviour of `transcript_search`; the spike and the migration for the vector table (a partition key and a metadata column if sqlite-vec 0.1.9 supports them, else the growing over-fetch; decision 9); the background embedding worker with a per-pass budget, resumable, running on text alone when the embedder is not ready, and the embedder fingerprint of decision 7; its triggers: `transcripts backfill`, a new `transcripts embed` command and the bounded background pass from `session_start` (which also ingests new sessions); the merge of vector and text candidates, `pendingEmbedding`, the ranking and arguments of decision 9 (`include_automated`, `project`, `session`, `since`, the three scopes); embedding progress in `status`; the fixture query set. Default-scope resolution from the live marker is Part 5's. Leaves to other parts: `transcript_get`, `transcript_sessions`, sync-on-read, the breadcrumb, pi, and the re-embed on a model change.
Depends on: Part 2
Done when: After backfill or the `session_start` pass (not by calling the worker directly), a topic query, a query answered only by a compaction summary and an exact error string each return the right fixture session in the top three; a session-scope paraphrase query still finds its chunk with many other sessions present; an automated session appears only with `include_automated`; a chunk without a vector is found by its words and counted in `pendingEmbedding`; with the archive on and populated, `session_start` takes within 10% of its time with the archive off (median of ten runs on the fixture store) and `memory_search` returns identical results.

### Part 4: Read and browse archived sessions
Scope: The `transcript_get` tool (a hit id or a session with an exchange range, a context size, a token budget with a `truncated` flag) and the `transcript_sessions` tool (project, since, limit, `include_automated`, labelled by title), each with the off-state answer. Leaves to other parts: sync-on-read, the current session, and everything not named here.
Depends on: Part 3
Done when: Given a hit from search, `transcript_get` returns the surrounding exchanges within the budget and flags truncation; `transcript_sessions` lists a project's sessions newest first with their titles and hides automated ones unless asked; both answer that the archive is off while it is off.

### Part 5: Make the current Claude Code session searchable while it runs
Scope: The process-keyed marker and its pure-node writer, a second hook entry on `SessionStart` and `UserPromptSubmit` beside the floor's and independent of it, that tests the sentinel in shell before starting node (decisions 10 and 12); finding the current session by ancestor process ids, accepting a transcript path only under the host's transcript root, with the disclosed `currentSession: "guessed"` fallback; sync-on-read for the three tools with the small embed budget (decision 7); default-scope resolution from the marker (decision 9); a spike that confirms the marker approach on Linux and macOS and states what Windows can do. Leaves to other parts: the breadcrumb, pi, and any host other than Claude Code.
Depends on: Part 4
Done when: Text added to a running Claude Code session after the last pass is found by `transcript_search` with no hook running other than the marker writer, including text a compaction removed from the model's context; two parallel sessions in one directory each resolve to their own file on Linux and macOS (simulated host processes in the tests, plus a manual check on a real Claude Code; or the spike's finding for a platform is recorded as a known limit); a marker pointing outside the transcript root is ignored; with the archive on, `session_start` and `memory_search` stay within the bounds of Part 3 once sync-on-read exists.

### Part 6: Tell Claude Code's agent what compaction removed
Scope: The breadcrumb text of decision 11 delivered by the existing `SessionStart` hook when its source is `compact`, once per compaction, checking the sentinel and saying nothing while the archive is off; the instruction text that tells the model what to do with it. No engine change and no tool-response hint. Leaves to other parts: pi.
Depends on: Part 5
Done when: After a compaction in Claude Code the model's next turn carries the breadcrumb exactly once and a `transcript_search` with scope `session` finds text the compaction removed; with the archive off nothing is added; startup, resume and clear add nothing.

### Part 7: Archive pi sessions and tell pi's agent what compaction removed
Scope: The pi adapter (the `compaction` entry, the kept and dropped rules, the session kind rule with `parentSession` and one user turn of decision 4), registered beside the Claude Code adapter; the pi extension's marker writer (with the authoritative kind of a live session) and its existing after-compaction message carrying the breadcrumb; pi sessions in `backfill`, `status`, the `session_start` pass and the three tools; the pi fixture sessions of decision 13. Leaves to other parts: other hosts.
Depends on: Part 5
Done when: A fixture of pi sessions (with a compaction, an error, a subagent child and a fork of a real conversation) backfills and is searchable like the Claude Code fixture, the child as `automated` and the fork as `interactive`; text added to a running pi session is found by search; after a pi compaction the next turn carries the breadcrumb once.

### Part 8: Retention, repair and re-embedding
Scope: `[transcripts] retention_days` (default 0, keep forever: the archive exists to keep what the host deletes) and `transcripts prune --older-than`, asking on a terminal; `transcripts rescan` (re-apply redaction to stored text) and `transcripts reproject`; the archive's vectors in `EmbedderMigration` and `ReindexCoordinator`, memories first so `memory_search` recovers first and transcript search on text alone meanwhile, with `backgroundTasks.transcripts` in `session_start` and `status`; the user documentation for these commands and a feature overview page that links every part's page. Leaves to other parts: everything built by Parts 1 to 7.
Depends on: Part 3
Done when: Prune removes exactly the sessions older than the given age and their chunks and vectors, and the retention setting does the same at start; rescan redacts a planted token that an older detector set missed; reproject recomputes project keys from the stored directories; after an embedder change the archive re-embeds in the background while search keeps working on text; the overview page links every part's page.
