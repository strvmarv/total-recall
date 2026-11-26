# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

### Added

- **The foundation of an opt-in transcript archive: off by default, not searchable yet, and not announced (internal).** A `[transcripts]` settings block (`enabled`, `db_path`, `retention_days`, `allow_remote_embedding`; the last two have no effect yet), a store with its own migration runner and `tx_schema_version` (the main `_schema_version` chain is untouched and still 18), and the commands `total-recall transcripts enable|disable|status|forget|unforget`. `enable`, `forget` and `unforget` need a terminal and ask first; `forget` deletes a session, a project or everything, scrubs the deleted text from the file and its write-ahead log, and records an exclusion or a cutoff so it is not archived again. Only the user can change the settings: `config_set`, the web route and `config set` refuse every `transcripts.*` key. The engine mirrors the setting into `<data directory>/live/enabled` for the hooks of later releases, and a separate archive file and `live/` are created user-only on Unix. Three tools, `transcript_search`, `transcript_get` and `transcript_sessions` (SQLite and Cortex modes: 53 and 54 tools), only report whether the archive is on. The archive is kept out of Cortex and git sync (tests pin it). See `docs/transcripts.md`.
- **The transcript archive can now back up Claude Code sessions: `total-recall transcripts backfill` (internal, still not announced; the archive is still not searchable).** It reads `$CLAUDE_CONFIG_DIR/projects` (else `~/.claude/projects`), stores each session's dialogue as chunks (your text, the assistant's text, one-line tool stubs of 160 characters and failed tool errors of 500, compaction summaries; no successful tool output, thinking or host-injected records), and resumes a grown session from the start of its last exchange. Text is redacted before it is chunked: `[redacted:token|private-key|bearer|url-credentials|secret-assignment]`, and the whole text as `[redacted:unprocessable]` when a detector times out; the documentation lists what is not detected. The archive's second migration adds `settled_user_turns`, `built_under_cutoff` and `project_root` to `tx_session`; `forget --project` records each session's repository root instead of its working directory; `status` lists sessions and chunks per project and kind. `enable` now says that nothing is read until `backfill` runs. Nothing runs on its own yet. See `docs/transcripts.md`.
- **The transcript archive can now be embedded and searched: `transcript_search`, `transcripts embed` and a background pass at each session start (internal, still not announced).** Once the archive is on, `session_start` starts a background pass (at most once per ten minutes per process) that ingests the host's session files for up to 15 seconds and then embeds waiting chunks for up to 30 seconds or 256 chunks, on its own thread and with its own two-thread copy of the local model; nothing in `session_start` waits for it. `total-recall transcripts embed [--retry-failed]` embeds the rest in the foreground, `transcripts backfill` does so after its ingest (`--no-embed` skips it), and `transcripts status` shows how many chunks are embedded, the embedder state and whether search uses meaning and words or words only. `transcript_search` merges a search by meaning, a phrase search and a word search by reciprocal rank fusion (constant 60), with multipliers of 1.25 for compaction summaries, 1.20 for error chunks when the query looks like an error and a recency boost of 1 + 0.10 * 2^(-age_days / 180) (the initial values, none changed after the fixture queries), at most two chunks per session except inside one session, a relative score, `pendingEmbedding` and a 40,000-character cap on the answer. The archive's third migration adds the `tx_chunk_vec` vector table, triggers that keep vectors in step with their chunks and the per-chunk `embed_attempts`; it is applied once when an older archive is opened. `allow_remote_embedding` is now enforced: with a remote embedding provider and the setting off, no archive text or query is sent and the archive is searched by words only. **Enabling the archive now means "archive my sessions from now on"**: the `enable` confirmation says that the session files are read in the background at each session start, no longer that nothing is read until `backfill`. `transcript_get` and `transcript_sessions` are still stubs. This supersedes the earlier entries above: `allow_remote_embedding` now has an effect, and the archive is now read on its own at each session start. See `docs/transcripts.md`.
- **The transcript archive can now be read and browsed: `transcript_get` and `transcript_sessions` (internal, still not announced).** `transcript_get` returns the exchanges around a search hit (`id`) or a stated range of a session (`session`, `from_exchange`, `to_exchange`), with `context` stored exchanges on each side (0 to 10, default 1), as readable text within `max_tokens` (200 to 20,000, default 2,000, counted as the sum of `ceil(characters / 4)` per returned exchange). It keeps the requested exchanges and trims the far context first, flags `truncated`, cuts a first exchange that alone is over the budget (marked `[cut: raise max_tokens to read the rest]`), and gives `nextFromExchange` and `nextToExchange` so the rest is read with `context: 0`. A named session or chunk is read whatever its kind, and an unknown or forgotten one is an error result. `transcript_sessions` lists sessions that hold text, newest first by last activity, with their titles, optional `project`, `since` (active since), `before` (to page back), `limit` (1 to 100, default 20) and `include_automated`; it reports `total`, `more` and `automatedHidden`. Both read in one read transaction, need no model and send nothing anywhere, and answer the off text while the archive is off. On a synthetic archive of 100,000 chunks in 2,000 sessions a get by id took a median of 1.3 ms, a range 1.4 ms and a filtered session list 6.8 ms. This supersedes the earlier entries above, which say these two tools are stubs. See `docs/transcripts.md`.
- **The transcript archive now keeps the running Claude Code session up to date: a marker, a sync before the read and defaults from the current session (internal, still not announced).** The plugin's hooks gain a second entry in the `SessionStart` and `UserPromptSubmit` groups, `hooks/live-marker/run.sh`: with the archive off it starts no process; with it on it writes `<data directory>/live/claude-code-<pid>.json` (session id, transcript path, working directory, time; 0600 in a 0700 directory, local, never synced) for the Claude Code process, and on `SessionStart` removes the markers of processes that are gone. The hook never ingests. Before `transcript_search`, `transcript_get` or `transcript_sessions` runs, the engine finds its host's marker through its ancestor processes (nearest first, a marker is refused unless its path is a plain `<project>/<session>.jsonl` under Claude Code's projects directory and it is no older than its process), ingests the session file's new text under a 2-second budget by the same code as the session-start pass (so what you forgot stays out) and, for `transcript_search` only, embeds up to 8 of its chunks within 2 seconds, the session's own chunks first (the embed queue takes an optional session preference); `transcript_get` and `transcript_sessions` bring in text only, so reading still needs no model. Without a marker the newest Claude Code session file written within 30 minutes is synced as a guess, which never chooses a scope. With a marker, `transcript_search` with no `scope`, `session` or `project` now searches the current project (`scopeNote` says so), `scope: "project"` without `project` the current project and `scope: "session"` without `session` the current session; an explicit argument wins, and the project is the key the archive stored for the session. The answer gains `currentSession` (`marker`, `guessed`, `none`), `currentSessionRef` and, only when the catch-up was incomplete, `syncNote`. A failed or late catch-up never fails the read. With the archive off none of the three tools reads a marker or opens the archive, and `session_start` and `memory_search` construct nothing new. The marker is written on Linux (observed with Claude Code 2.1.330) and by the same script on macOS (not observed); on Windows no marker is written and the guess applies. Measured on Linux (Debug build): a sync with nothing new took a median of 1.0 ms on an archive of 2,000 sessions and the guess over 5,000 session files 26 to 29 ms; a call against a locked archive waited about 1.2 seconds, not the 500 ms of the lock timeout, and still answered. A `claude -p` session is automated, so a default project search leaves its own text out (name it with `scope: "session"`). No schema change, no new tool and no new setting. See `docs/transcripts.md`.

### Fixed

- **The startup "Last session: N weeks ago" line is now right on every host, and never counts the running session (#132).** `lastSessionAge` came from `usage_events`, which only the Claude Code and Copilot CLI importers fill and which is pruned after 30 days, and fell back to the newest tier movement in `compaction_log`. On pi, Codex, Cursor and the other hosts that left `usage_events` empty it therefore showed the age of an old one-off migration ("16 weeks ago" a day after the last use). It is now the age of the newest activity of an earlier session, taken from `usage_events`, `retrieval_events` and `compaction_log`, where the reasons `warm_sweep_decay` (legacy TypeScript rows only; the .NET code no longer writes it) and `migration_*` are ignored. Each source counts only rows older than the moment the server process started, so the session that is starting can never make it "just now". This supersedes the usage-first behaviour described under 1.1.4. No schema change. An engine that starts late or is restarted in the middle of one host session (the shim provisions the engine after answering the handshake, and restarts it after a crash) counts that host session's earlier activity as the previous session, and with Postgres storage only the compaction log is available.
- **The Claude Code plugin no longer registers a `SessionEnd` hook, and the docs no longer promise a compaction reminder at session end (#97).** The hook ran `total-recall session-end-hint`, which printed a `systemMessage` asking the user to compact. Claude Code runs a `SessionEnd` hook but never displays what it prints when it exits 0: observed on Claude Code 2.1.325 (Linux, interactive with `/exit` and `claude -p`), a throwaway hook that wrote a marker file and printed a JSON `systemMessage`, plain stdout and stderr ran every time and none of the three appeared, so the reminder has never reached anyone and every session end waited for the hook (its own header comment measures about 1.2 s on Windows). The hook, its wrapper `hooks/session-end/run.sh`, the `session-end-hint` command and its tests are gone. The reminder that does reach the user is the `hot_tier_compaction_suggested` hint that `session_start` returns when the hot tier holds at least `tiers.hot.compaction_hint_threshold` entries (default 5; 0 disables), which the startup instructions now tell the model to always surface at the next session start, plus the once-per-session pinned-floor nudge; both reach the user through the model, not the host. The commands skill, `using-total-recall`, the Kiro steering file, `AGENTS.md` and the Claude Code host page say so. A `SessionEnd` hook that exits 1 or 2 with stderr is shown as a "SessionEnd hook [...] failed" notice, which is not a message and was not used. On the same version, with the plugin loaded from a checkout, an Opus 5.5 session on a store with 6 hot-tier entries gave a startup announcement that suggested running `/total-recall:commands compact` (two runs), and one on a store with 3 gave none; a Haiku 5.5 session answered without calling `session_start`, so the reminder depends on the model following the startup instructions. Exits other than `/exit` (Ctrl+C, Ctrl+D, closing the terminal) were not observed. The same runs showed a `SessionStart` hook's `systemMessage` displayed in an interactive session (`SessionStart:startup says: ...`), which the Claude Code host page and `AGENTS.md` had listed as not observed.
- **The Claude Code prompt and session-end hooks no longer download the engine (#98).** Both hooks ran `node bin/start.js <command>`, and for any command but `serve` that launcher provisions the engine first. On a git-sourced install (the Claude Code marketplace, whose tree has no `binaries/` for a new version) the first prompt after an install or an update therefore downloaded the engine archive (about 100 MB) inside a hook that blocks the prompt, with no lock, possibly while the MCP shim was writing the same files, and a session that ended first did the same inside the session-end hook's 10 second budget. The hooks now ask `hooks/engine-path.js` for the engine (the check the Codex and Copilot hooks and the pi extension use: the binary exists and its model payload passes its size check) and run it directly, or print `{}` until it is provisioned; the MCP shim provisions as before. The check only reads, so it shares the shim's own limit: a binary extracted before `models/registry.json` counts as provisioned, and the engine's failure then gives `{}`. A new test file, `hooks/claude-hooks.test.js`, runs both scripts over a temporary plugin tree whose `bin/start.js` records that it ran. Not observed: Windows, and how long the first prompt after an update blocked before. The session-end hook this also changed is removed in the same release (#97, above), so only the prompt hook remains.

### Changed

- **`total-recall config set` and the `config_set` tool now refuse `storage.mode`, `storage.connection_string` and every `git_sync` setting (#128, part of #93).** These settings decide where the store lives and which repository memories are synced to, so only the user changes them, by hand in `config.toml`; an agent that read a planted memory can no longer redirect them. `config set storage.mode cortex` used to work and is refused now. The web UI already showed both storage keys as read-only.

## 4.8.0 - 2026-11-16

### Added

- **Groundwork for git sync, with no user-visible behaviour yet (#93: #95, #100, #105, #111, #118).** Migration 18 adds four state tables (`git_sync_entry`, `git_sync_review`, `git_sync_issue`, `git_sync_alias`) and an index on `import_log(target_entry_id)` to SQLite databases; the migration only adds tables and an index, and nothing reads or writes them yet. The Postgres runner is unchanged. The code also gains the sync record format, a hybrid clock, record validation and the pure merge decisions, and a `[git_sync]` settings block that is read and normalised but not yet used. A 4.7.0 binary opened on a database migrated to version 18 runs no migration of its own and ignores the new tables (read from its code; not exercised).
- **The engine now tells a host when a newer release exists, from a daily background check (#115, part of #92).** A host that was already told its version gets one more line, once per release and at most one notice per start: `total-recall 4.9.0 is available (you have 4.8.0): <summary> Update: <how> More: <link to the release page>` (kind `available` in `session_start`'s `whatsNew` and in `whats-new --format json`). The MCP server asks GitHub for the newest release of its channel (the published stable release for a stable build, the greatest of the ten newest releases for a prerelease build) no earlier than 60 seconds after it starts, and only when the last answer is older than `updates.check_interval_hours` (default 24, a whole number from 1 to 8760); after a failed attempt it tries again no sooner than an hour later; it runs on its own task, so it never delays startup or `session_start`. The request is unauthenticated and identifies itself only by `User-Agent: total-recall/<version>` (plus an `Accept` header); what it keeps is the version and the first paragraph of the release notes, sanitised, in `<data directory>/whats-new/latest-stable.json` or `latest-pre.json`. The check is on by default; `updates.check = false`, `updates.notice = false` or the environment variable `TOTAL_RECALL_NO_UPDATE_CHECK` (any non-empty value other than `0` or `false`) turns it off, and so does a development build. Codex forwards the variable to its server; Cursor cannot pass it to its plugin server, so use `updates.check = false` there. The `<how>` is a fixed instruction per host, kept in one table (`UpdateInstructions`) that `scripts/host-docs.test.js` checks against the host pages. `PRIVACY.md` now names the check (the earlier statement that the plugin makes no network request of its own no longer holds) and how to turn it off; the Configuration page, the agent docs and the Claude Code and pi pages describe it.
- **Claude Code and pi now show the what's-new notice themselves, and every host's name reaches the engine (#106, part of #92).** A new command, `total-recall whats-new --host <id> [--format text|json|claude-code] [--no-mark]`, prints the notice for one host (the host comes from `--host` only; it ignores the direct stamp and the Hermes rule) and records the version as seen only after the line was printed. The Claude Code `SessionStart` hook (`hooks/session-start/whats-new.js`, Claude Code branch only) shows the line as a `systemMessage` on `startup`, `resume` and `clear`, within one 2.5 second deadline, and on any failure leaves its output exactly as it was; the pi extension shows it through `ctx.ui.notify` on a session start with a screen, without delaying startup, and its MCP entry now sets `TOTAL_RECALL_HOST=pi`. Both write `<data directory>/whats-new/direct-<host>.json` first (and do nothing, running no engine, when the data directory cannot be resolved), and both show the engine's text only if it is one line of at most 1000 characters without control characters, and for 30 days after that stamp the engine no longer asks the model to relay the line for that host. The shim now passes the harness's `initialize` client name as the `host` argument of every `session_start` it proxies, so a host that sends a known client name resolves to it instead of `other`, and Hermes (client name `hermes-mcp-client`) now resolves to `hermes` and gets no notice. The announce text in the commands skill, the `using-total-recall` skill and the Kiro steering and hook files tells the model to relay `whatsNew`. Not observed: whether Claude Code shows a `SessionStart` `systemMessage` (in `claude -p` as well) and the client name it sends (expected `claude-code`); pi's client name `pi` and its `env` handling were read from pi's source. The update-available check was delivered in #115 (above).
- **`/develop` can now define larger work as a parent ticket with sub-tickets (#103).** A change that fails the size check used to end in "define only the first piece". Define now also offers to define it as a **parent**: a ticket whose spec holds the goal, the shared decisions and an ordered breakdown into parts. A parent is never built. It is delivered as a docs-only pull request that says `Refs #<n>` and closes nothing. When that PR merges, `/develop <parent>` posts a new `spec-merged` checkpoint and creates one native sub-issue per part, in order and without duplicating a part on a second run. `/develop <parent>` after that shows each part with its issue, state and readiness, and offers to close the parent when every part is closed, and only on a yes. `/develop <sub-issue>` defines that part against the merged parent spec, asks first if a part it depends on is not done, reviews the sub-spec against the parent spec and pins `parent` and `parentBlob`; Deliver warns, without stopping, when the parent spec has changed since. A ticket with no parent that passes the size check behaves as before. New in `checkpoints.js` and `route.js`: the `parent` track, the `spec-merged` phase, the `parent`, `parentBlob` and `specBlob` fields, the routes `parent-status` and `terminal-pin`, and the subcommands `parts`, `stub`, `stub-body` and `parent-line`. Parent tickets need this version of the skill: an older version rejects a parent pin, and one that sees a parent's `pr-open` posts `closed` when the spec PR merges; minimize that `closed` comment to recover (`docs/develop.md`, "Larger work: parent tickets and sub-tickets"). The sub-issue calls were checked in a throwaway repository; creating a 50-part breakdown in one run was not.
- **The engine now tells the user once per host and version that total-recall is new or was updated (#99).** `session_start` returns a `whatsNew` field, `{kind: "first-seen"|"updated", text, relay}`: one line (the version, the release's one-line summary from `release-notes.json`, and a link to the release or to all releases) and a fixed relay instruction, so the notice reaches the user through the model. The engine remembers what each host was told in `<dataDir>/whats-new/seen-<host>.json` (`{"version":"..."}`) and records it only after the response is built. The host is `TOTAL_RECALL_HOST`, else the optional `host` argument of `session_start` (a client name), normalised to a closed list of ids (`claude-code`, `pi`, `copilot-cli`, `cursor`, `codex`, `gemini-cli`, `kiro`, `opencode`, `hermes`, `cline`, `windsurf`, `zed`, `vscode`, `other`; a known id passes through, `hermes-mcp-client` becomes `hermes`, anything else is `other`). Nothing shows on a dev build (`1.0.0` or `unknown`) or for Hermes; a `session_start` call that takes more than 30 s shows nothing and records nothing, so the next call shows it; a `direct-<host>.json` stamp younger than 30 days (written by a host that shows the notice itself, see #106 above) suppresses the relayed line for that host. `[updates] notice = false` turns it off and records nothing (`docs/configuration.md`). This is the engine and relay part of the startup what's-new notice (tracked in #92): direct display in pi and Claude Code, the `whats-new` command and the shim forwarding the client name were delivered in #106 (above), so a host that sends no host name still resolves to `other`, and the update-available check was delivered in #115 (above). `AGENTS.md` ("Session Lifecycle") describes it.
- **A release's notes are now written once, as the annotated tag message, and reach every place a release is published (#91).** The GitHub release body used to be empty. The tag message now has a required shape: a first line (`total-recall vX.Y.Z`), a blank line, one summary paragraph, then optional `Added` / `Changed` / `Fixed` lists of `- ` bullets (`AGENTS.md`, "Release flow", step 4). The new `scripts/release-notes.js` reads it. The `check-version` job runs `node scripts/release-notes.js <tag> --check` before any build and fails a tag that is not an annotated tag object, and a stable release whose message has no title or summary paragraph (a prerelease only gets a warning); run the same command on a tag before pushing it. The `publish` job then writes `release-notes.json` (`version`, `previous`, `summary`, `sections`, `url`, `releasesUrl`) into `binaries/<rid>/` before `npm publish`, so it ships in the npm package and in all four platform archives, and sets the GitHub release body to the tag message without its title line. The previous release is the highest lower stable version for a stable release and the highest lower version of any kind for a prerelease; the tag `0.5.1`, which is not a `vX.Y.Z` version, is ignored. The summary is cut to one plain line of at most 200 characters (terminal escapes and control characters removed, markdown links reduced to their text); the sanitiser is specified by the shared cases in `scripts/fixtures/summary-sanitiser.json`, which the engine's implementation of the same sanitiser is to be tested against as well. This is the first of three tickets for the startup what's-new notice (tracked in #92); nothing shows the notes to users yet, that is the next ticket. Create the tag with `git tag -a vX.Y.Z --cleanup=whitespace -F <message-file>`: git's default cleanup deletes every line that starts with `#`, so write the headings as the plain words `Added` / `Changed` / `Fixed`. `scripts/release-notes.test.js` and `scripts/release-workflow.test.js` run in `npm run test:shim`.
- **The install script now says what it changed in other tools, and can be told to skip them (#80).** When `scripts/postinstall.js` has finished, it prints one summary on stderr with a line for each file it wrote in another tool (Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config) and for each step that failed, and appends the same lines to `postinstall.log` in the data directory (`TOTAL_RECALL_HOME`, else `~/.total-recall`), so there is a record even when npm hides stderr. The log is only appended to. Setting `TOTAL_RECALL_SKIP_HOST_CONFIG=1` skips every other tool while the engine step still runs. What the script writes is unchanged. A failure of the Hermes plugin copy no longer ends the script before the other tools are configured. `docs/npm-postinstall.md` describes the summary, the log and the switch; the new `scripts/host-config.test.js` is part of `npm run test:shim`.

### Changed

- **New memory imports now get an id derived from the import hash, in every storage mode, and an interrupted memory import is repaired by the next run (#95, part of #93).** Until now every import got a random id, so the same Claude Code memory file or Hermes `MEMORY.md`/`USER.md` section imported on two machines became two entries with different ids, and an import killed between storing the memory and writing its search vector or import-log row left a half-finished entry that the next run neither skipped nor repaired (a retry added a duplicate and left the first copy without a vector). A memory import now derives its id from the same content hash the import log uses (the first 16 bytes of the SHA-256 of that hash text, as a lowercase `8-4-4-4-12` hex string), so identical content gets the same id everywhere, and a retry finds the entry by that id, skips the insert, re-embeds its stored text, and writes the missing log row. Memories imported before this change keep their random ids and are not duplicated; knowledge imports (every other host file, including a Claude Code `type: reference` file) keep random ids. This is groundwork for syncing memories through a private git repository: the SQLite store also gains an all-or-nothing transaction (insert with its vector, move between tiers, delete, edit, pin, record an import), a caller-chosen creation time on insert, and `scope` and `entry_type` updates (the Postgres store throws for all three), plus a tolerant `source_tool` reader; nothing uses them yet. In Cortex mode a repaired import does not enqueue a second upsert.
- **The MCP entries the install script writes are now pinned to the installed version (#83).** Cline, Kiro, Windsurf, Zed, VS Code, Copilot CLI and Gemini CLI entries, and Gemini CLI's per-prompt hook, run `npx --yes @strvmarv/total-recall@<version>` instead of the unversioned form, so an older installed copy can no longer answer instead of the release that was just installed (observed: a global 4.0.4 ran while `latest` was 4.7.0). On each install the script moves an entry in exactly the form it writes (`npx`, `--yes`, the package name, with or without a version) to the new version and shows it in the summary; any other entry is left alone, and `TOTAL_RECALL_SKIP_HOST_CONFIG=1` is how to hold a version by hand. A git checkout, or a package whose version cannot be read, still writes the bare name and refreshes nothing. The static Kiro workspace hooks are unchanged. `docs/npm-postinstall.md` and the host pages describe the new form.
- **The README is now a short front door, and its reference material lives under `docs/` (#77).** The README went from 670 lines to about 150: what total-recall is, how to install it (the prompt to paste into an assistant first, then Claude Code, pi and a table of the other harnesses), how to check that it works, how to use it, and where everything else is. The reference sections moved whole to their own pages: `docs/how-it-works.md`, `docs/platforms.md`, `docs/web-ui.md`, `docs/commands.md`, `docs/configuration.md`, `docs/cortex.md`, `docs/skills.md`, `docs/develop.md`, `docs/tools.md`, `docs/architecture.md`, `docs/verifying-a-release.md` and `docs/building.md`. Corrections made on the way: the tool counts (42 core, 50 in SQLite mode, 51 in Cortex mode) and the missing `insights` tool; the default scope (`"user:local"`) and a configuration key the code does not read (`initial_backfill_days`); the command table (`setup`, `recent`, `update`, `ui`, `compact [--fast]` added, `eval --grow` removed); the hot tier is injected at session start, not into every prompt; the platform table is rebuilt from the harness pages (it called OpenCode a plugin wrapper, Hermes import only and Cline full support); what is imported from each tool is stated as the importers have it; the Cortex page says that ingested knowledge base entries and skill usage are pushed too and that local skills are uploaded only with `[skills] auto_import`; `CONTRIBUTING.md` no longer lists `eval --grow`. `PRIVACY.md` no longer gives the engine download as about 22 MB, says "by default" where it stated that nothing leaves the machine, and names the configurations that change that (Postgres, Cortex, a remote embedder).
- **The documentation check also guards the README.** `scripts/host-docs.test.js` now fails when the README grows past 200 lines, loses or reorders a section, gains a second title, changes its banner or puts anything above it, drops the install prompt, the check prompt or the status line, holds a command that runs an npm install other than the one pi line or mentions `npx`, leaves a page under `docs/` (except `docs/TODO.md`) unlinked, or when a tool in `catalog.json` is missing from `docs/tools.md`. Links and anchors are checked in every page under `docs/` except `docs/TODO.md`, and in `PRIVACY.md`.
- **`INSTALL.md` is now a runbook an AI agent can follow to a verified install (#74).** The old file mixed install commands for every harness with release guidance that had gone stale (it told readers that stable was 0.7.2 and to use `@beta`, and expected 0.8.0). `INSTALL.md` now holds only the procedure and a table of harnesses, at the same URL, so a prompt that was already pasted keeps working. The per-harness instructions moved to one page each under `docs/hosts/` (Claude Code, pi, Codex CLI, Copilot CLI, Cursor, Gemini CLI, Kiro, OpenCode, Cline, Windsurf, Zed, VS Code, Hermes and any other MCP client). Each page separates what the agent runs, what only the user can do and how to check the install, and says what was observed and what was not. An install that runs npm now tells the user first what the install script may change in other tools (including switching Hermes's memory provider and adding a Gemini CLI hook that runs on every prompt), and gives the form that skips it. The detail is in `docs/npm-postinstall.md`. The database-relocation and first-run material moved to `docs/database-location.md` and `docs/first-run.md`, `.opencode/INSTALL.md` is now a pointer to the OpenCode page, and four README lines about installing were corrected.
- **A test keeps the install instructions true.** `scripts/host-docs.test.js` runs in `npm run test:shim` and fails when a harness has no page or the wrong headings, when `INSTALL.md` holds an install command or a version number, when a link or an address points at a missing file or heading, when an install that runs npm lacks its disclosure paragraph, when the example status line no longer matches the commands skill, or when a name cited by a hook script is no longer in the table.
- **The documentation check sees more forms (#79).** `scripts/host-docs.test.js` now also checks links written as `<img src>`, `<a href>` and reference definitions (`[ref]: target`), ignores a link or a heading inside an HTML comment, reads Setext headings (`Title` over `-----`) and `<h1>` to `<h6>` as headings, and counts an npm-running command in the README in any case and when it is continued onto the next line with a backslash. None of these forms occurs in the repository today; the check only closes the gaps.

### Fixed

- **The pi extension now skips pi-subagents children (#117, follows #114).** A subagent that pi-subagents started with its default settings has a session file, so the extension took it for the user's conversation: every one of the 233 persisted sessions with a parent session header on the author's machine (all named like pi-subagents agents, `<type>#<8 hex>`) held the injected `total-recall-context` entry, and a child told to make no tool calls quoted the `session_start` instruction. The extension now keeps a process-wide set of session files that have started and not yet shut down, and treats a session whose header names one of them as its parent as a child: no instructions, no floor, no what's-new notice, and `/total-recall` and `/develop` say they are not active. pi's own `/new`, `/resume`, `/fork` and `/reload` are not affected, because pi shuts the old session down before it builds the new one. A live run with pi 1.5.2 and pi-subagents 0.25.0 showed one `total-recall-context` entry in the child with 4.7.0 and none with this change; `docs/hosts/pi.md` states the limits of the rule.
- **The pi docs now say that a pi-subagents child does get the injected instructions (#114, part of #76).** `docs/hosts/pi.md` and `AGENTS.md` said that subagent children get no instructions, floor or `/total-recall`, and that this was not verified. A child started by pi-subagents with its default settings was observed to quote the `session_start` instruction, the session-start text and the CLI note, because pi-subagents keeps its session in a file and the extension takes any session with a file for the user's conversation. The page now keeps what was observed apart from what was read in code, lists the ways pi-subagents documents to keep a child out (`exclude_extensions`, `persist_session`, `rememberAgents`, `extensions: false`), and says that the extension does not yet skip such children; comments in `pi/` that assumed otherwise were corrected. No behaviour changed.
- **The agent docs now give the right handler, migration and tool counts, and the commands skill documents `eval grow` (#110, part of #76).** `AGENTS.md` said 49 handlers (51 exist: 50 are served in SQLite mode, 51 in Cortex mode) and 16 migrations (17 exist, with the sticky column of migration 17 missing from the Infrastructure guide's table); `CONTRIBUTING.md`, `docs/TODO.md` and the Handlers guide had older counts still, and a comment in the catalog test gave 48 tools. The commands skill had no entry for `eval grow`, so a request to review benchmark candidates had no command to map to; it now lists `eval grow list` and `eval grow resolve`.
- **`npm run test:shim` now runs on Node 24 as well as Node 20 (#89).** The script handed `node --test` six directories, which Node 24 reads as files or globs ("Cannot find module .../bin/shim"), so the command failed before any test ran on a current Node while CI, on Node 20, stayed green. A new zero-dependency runner, `scripts/run-shim-tests.js`, now holds the one list of test targets (`SHIM_TEST_TARGETS`), expands directories to `*.test.js`, `*.test.mjs` and `*.test.cjs` files itself and hands Node explicit relative paths, so it works on every Node from 20 on any operating system; a missing target or an empty directory fails with its name instead of running fewer tests, and extra options after `--` reach Node. Node 20 and Node 24 both run 1242 passing tests and skip 3. The profile's shim verify command is now `npm run test:shim`, so the tests are listed once, and the tests that read the script text read the runner's list.
- **The launcher now retries a first-launch provisioning failure that can clear up, so an install made in a release window heals by itself (#87).** The marketplace entry follows `main`, and `main` names a new version for a few minutes before its release is published; a first launch in that window got a 404 for the release manifest, set `provisioning-failed` and never tried again, while its not-ready answer said to retry in a moment. The shim now retries a retryable failure after 5, 10, 20 and 40 seconds and then every 60 seconds (24 tries, about 20 minutes), starts the engine as soon as a try succeeds, and reports `attempt`, `error` and `retryInSeconds` (counted down at each call) in the not-ready answer with a hint that says it is retrying. A checksum mismatch or a missing archive is not retried; after the last try the answer is `provisioning-failed` with `gaveUp: true` and a hint to restart the MCP host. A progress tick no longer replaces the answer's `phase` with `downloading` or `extracting`. `skills/using-total-recall/SKILL.md`, `AGENTS.md` and `docs/first-run.md` describe the retry.
- **The install script now writes the Cline entry where Cline reads it, and only when Cline is installed (#85).** It used to write `~/.vscode/cline_mcp_settings.json`, a file Cline does not read, whenever the `~/.vscode` directory existed (which is VS Code's own, so any VS Code user got the file). It now finds Cline by its directories and writes `settings/cline_mcp_settings.json` in `~/.cline/data` and in the `saoudrizwan.claude-dev` folder under VS Code's `globalStorage` when they exist (the first is the file the Cline importer reads; the second matches the importer only on Linux), with the same pin and refresh rules as the other tools. `CLINE_HOME` is still an explicit override (the directory that holds the file itself, created if missing). A `~/.vscode/cline_mcp_settings.json` from an earlier install is left in place and can be deleted. `docs/hosts/cline.md`, `docs/npm-postinstall.md` and `docs/platforms.md` describe it, and the documentation check keeps them from claiming otherwise. Cline loading the written file was not observed.
- **The web UI has seven sections, not six (#79).** The commands skill and `AGENTS.md` said six and left out Eval; both now list all seven, as `docs/web-ui.md` does.
- **`PRIVACY.md` no longer ties the first-launch engine download to Claude Code alone (#79).** The Copilot CLI, Codex CLI and Cursor plugin installs download the engine the same way; the disclosure now says "a git-sourced plugin install" and names the four.

## 4.7.0 - 2026-11-14

The first stable release with the release attestation, the engine archive cache and the one-command version bump. It was validated on the prereleases 4.7.0-beta.1 and 4.7.0-beta.2 (npm `beta`); 4.7.0-beta.1's GitHub release has no assets, so use 4.7.0-beta.2 or 4.7.0.

### Added

- **A reinstall of the same version no longer downloads the engine again (#65).** The engine and its model live under the plugin's own `binaries/<rid>/`, so removing and reinstalling the plugin, or a second host installing the same version, downloaded the whole archive again, with every tool call answering "not ready" until it finished and no way to start offline. The archive that was verified against the release manifest is now also kept in `<data dir>/cache/binaries/<version>/` (the engine's data directory: `TOTAL_RECALL_HOME`, else `~/.total-recall`) with a sha256 sidecar, and the shim restores from it before it fetches the manifest, so it works offline. Only archives verified against the manifest are cached, only after they extracted with their binary and passed the model-payload check; the cache keeps one version; `cache/` is owner-only and an entry in a directory another user owns or can write to is ignored; and any cache problem is a miss, never an error. A restore re-hashes the entry, runs the same binary, `chmod` and payload checks as a download, and deletes the entry only when it is proven bad. A release asset re-cut at the same version is not refreshed while its cache entry exists. If the data directory is created by this, it gets the default permissions; only `cache/` is made owner-only.
- **Release archives and the provisioning manifest carry a build attestation (#64).** The shim verified a download against the sha256 in `provisioning.manifest.json`, which shows the download matches the manifest but not that this repository's CI built it, because both are published together. The publish job of `release.yml` now runs `actions/attest@v4` (GitHub artifact attestations, signed through sigstore) over the four platform archives and the manifest, before they are attached to the GitHub Release and with no way to skip it, so a failed attestation stops the release assets from being published. Verify a download with `gh attestation verify <file> --repo strvmarv/total-recall` (README, "Verifying a release"). The shim still checks only the sha256. `scripts/release-workflow.test.js` (in `test:shim`) fails if an attached asset is not attested, if the step moves out of place or can be skipped, or if `attestations: write` is granted to any job but publish. Only releases published after this change carry attestations, and `npm publish` still runs before the archives are staged.
- **A release version bump is one command, and a mismatch fails the release (#62).** The version lived in seven files (eight fields) that a release agent updated by hand, and a missed one shipped a stale manifest. `node scripts/sync-version.js <version>` now sets all of them at once, staging every change before replacing any file (the two `package-lock.json` fields are edited by position, with no `npm install`), and `--check` verifies them against `package.json` and fails on any tracked `plugin.json`/`plugin.yaml` outside the list. The release workflow gained a `check-version` job that runs `--check --expect` against the git tag before any binary is built, so a tag that differs from `package.json` never reaches a build or the npm publish. The version must be `X.Y.Z` or `X.Y.Z-<prerelease>` with a letter-first prerelease, no further `-` or `+build` part, and a first identifier that is not `latest` or a valid version range such as `x` or `v1`, because the publish step derives the npm dist-tag from the text after the last `-`.

### Changed

- **Comments that pointed at the deleted TypeScript implementation were removed or rewritten (#66).** 132 comment lines in 86 files named `src-ts/...` or `bun:sqlite`. Only comment lines changed, and a guard test (`StaleLineageReferenceTests`) fails if either string comes back into a `.cs` or `.fs` file under `src/` or `tests/`.

### Fixed

- **A prerelease could not be attached to a GitHub release (found with 4.7.0-beta.1).** This repository has immutable releases, which lock a release's assets once it is published. A stable release uploads its assets before it is published, but a prerelease is published first by default, so the upload failed with "Cannot upload asset ... to an immutable release" and left `v4.7.0-beta.1` as a published prerelease with no archives and no manifest (npm and the attestation had already succeeded). The release workflow now creates a prerelease as a draft and publishes it, by release id, in a following step; `scripts/release-workflow.test.js` pins both.

## 4.6.0 - 2026-11-12

### Added

- **`session_start` runs a version-gated first-run smoke test (#51).** `smokeTest` was always `null`. The first `session_start` after an install or a version change now runs the 22 queries of `eval/benchmarks/smoke.jsonl` against the bundled corpus in an isolated, ephemeral database, reusing the session's embedder, and reports `{passed, notes}` (pass is an exact-match rate of at least 0.8). Later sessions on the same version skip it and report `null`, as do sessions where the embedder is not the bundled local model, a background re-embed has not finished, or the build has no version. The version is stored in `_meta.smoke_test_version` only after a run completes; a run that throws, cannot find its bundled files or is cancelled after 20 s reports `passed: false` with the reason, records nothing and retries next session. It never fails `session_start`. Postgres keeps the record per machine (`smoke_test_version:<machine name>`), because its `_meta` is shared.
- **`session_start` reports retrieval regressions (#50).** `regressionAlerts` was always `null`. The sqlite and cortex servers now compare the retrieval metrics under this session's config snapshot with those of the nearest older snapshot that has `assistant`-source events, and report an alert when the miss rate rose by at least `regression.miss_rate_delta` (default 0.1) or average latency rose by at least `regression.latency_ratio` times (default 2.0). Each side needs at least `regression.min_events` (default 10) events with a resolved outcome; events still inside the 60-minute feedback window are not counted. An alert is `{kind: "miss_rate"|"latency", message, previous, current, delta, threshold}`. The field stays `null` when there is nothing to compare, no threshold is crossed, or detection fails (a failure never fails `session_start`). A changed config starts a snapshot with no events, so the alert first appears in a later session that reuses it, once it has enough events. Postgres mode reports none.

### Fixed

- **Config snapshot lookups are deterministic when two snapshots share a millisecond.** `latest`, a name lookup and the recent list ordered by timestamp alone, so snapshots created in the same millisecond resolved in arbitrary order; ties now go to the one inserted last. Also stops a flaky CI test.
- **Retrieval and compaction events carry a real config snapshot id (#49).** `session_start` created no snapshot, so every `memory_search`, `kb_search`, promote, demote, pin, unpin and session-end compaction event was logged with the placeholder `"default"` (or `""`), and `eval_compare` and `eval_report`'s `config_snapshot` filter had nothing to separate. The sqlite and cortex servers now snapshot the config they run with (reusing the latest snapshot when the config is unchanged) and tag each event with its id, so periods before and after a config change can be compared. A snapshot only reflects the settings the config serializer writes. Postgres mode, the one-time tier migration and the CLI `compact` path keep the placeholder. Existing events are not rewritten. When a named snapshot (`eval_snapshot <name>`, CLI `config set`) is requested for a config that matches the unnamed session snapshot, the name is now attached to that row.

## 4.5.0 - 2026-11-11

### Changed

- **Retargeted from .NET 8 to .NET 10 (#34).** Every project now builds for `net10.0`, declared once in a
  root `Directory.Build.props`, and the shipped NativeAOT binaries embed the .NET 10 runtime (.NET 8
  support ended on 10 November 2026). No code or behaviour changes. `Microsoft.Extensions.FileProviders.Embedded`
  moved to 10.0.5, the release workflow and `scripts/ui-aot-smoke.sh` read the publish output from
  `bin/Release/net10.0/`, and the docs say .NET 10. Contributors still need the .NET 10 SDK, as before.

### Fixed

- **Warm is the default tier on every ingress path (#41).** `memory_extract`, `memory_import`,
  `total-recall memory import` and the Cortex pull still put new entries into Hot when no tier was given,
  which filled the auto-injected tier with entries that had not earned a place and triggered compaction
  more often. They now land in Warm and reach Hot only by an explicit `tier: "hot"`, by pinning, or by
  earned promotion. `memory_extract` also de-duplicates against Warm as well as Hot. An import entry with
  an unrecognised `tier`, and a pulled entry with an unrecognised or missing one, also falls back to Warm.
  Explicit tiers and `sticky: true` are honoured as before, and `memory_promote` still defaults to Hot.
  The `/total-recall:commands store` default now reads warm.
- **`session_start` reports `project`, `warmSweep` and `projectDocs`.** They were hardcoded to `null`
  although the work behind them already ran. `project` is the detected project key (the lowercase
  `owner/repo` slug, or the folder name when there is no remote; `null` outside a git repo), the same
  value that scopes the injected pins. `warmSweep` is `{demoted}` once the sweep completes (`null` only
  if it failed). `projectDocs` is `{filesIngested, totalChunks}` when project docs were ingested during
  the call, otherwise `null`. `smokeTest` and `regressionAlerts` are still not implemented (#47).

## 4.4.0 - 2026-11-11

### Added

- **`develop` skill.** `/develop` defines a change as a reviewed GitHub issue (brainstorm, spec or
  approach, two reviewer agents, a ticket that meets the ticket standard, pinned by a `defined`
  comment) and delivers it hands-off as a pull request (plan, build with review at every step,
  verify, open the PR, stop). It never merges. Every `gh` and network `git` call is pinned to the
  chosen account on that command only, and a run trusts a `defined` comment by a user with write
  access, not the issue body. Ships as `skills/develop/` with its zero-dependency Node scripts, the
  `adversarial-reviewer` and `counterexample-reviewer` agents, and a `/develop` command for pi
  (`pi/develop.js`). A repository can add a profile at `.github/develop.md` (this one does). The
  skill's tests run in the `test:shim` job.

### Changed

- **Release and CI workflows no longer install clang unconditionally.** The GitHub-hosted Ubuntu
  images already ship clang 18, so `release.yml` installs it only when missing (and prints
  `clang --version`), and `dotnet-ci.yml`, which never publishes, drops the step. A package-mirror
  outage can no longer fail CI or the release.

## 4.3.0 - 2026-11-10

### Added

- **Cursor CLI host support.** Cursor now gets the startup instructions, the pinned-directive
  floor and a working MCP server, not just the skills. New `cursor/hooks.json` + `cursor/hook.js`
  (node hooks: `sessionStart` injects the commands skill as `additional_context`; `postToolUse` runs
  `pinned-floor` through the provisioned engine on the first tool call of each user message, turn-count
  trigger only, because Cursor's CLI fires no per-prompt hook) and `cursor/mcp.json` (`node
  ${CURSOR_PLUGIN_ROOT}/bin/start.js`, no `cwd`, so the server starts in the workspace and project-scoped
  pins work). The old Cursor wiring ran a bare `total-recall` command (not on PATH) and a bash hook whose
  unbraced `$CURSOR_PLUGIN_ROOT` is empty in Cursor's PowerShell. Observed on cursor-agent
  2026.11.10 (Windows, `-p` and interactive); known gaps are in INSTALL.md.

- **Copilot CLI host support.** `copilot plugin install` now gives Copilot CLI the startup
  instructions and the per-turn pinned-directive floor, not just the tools. New
  `copilot/hooks.json` + `copilot/hook.js` (node hooks: `sessionStart` injects the commands
  skill as `additionalContext`; `userPromptSubmitted` runs `pinned-floor` through the
  provisioned engine, turn-count trigger only). Observed working on Copilot CLI 1.0.98-2
  (Windows, `-p` and interactive). The Claude-format hooks never ran under Copilot on
  Windows (the unbraced `$CLAUDE_PLUGIN_ROOT` in their `command` expands to nothing there). Known gaps are in INSTALL.md.

- **Codex CLI harness support.** `codex plugin marketplace add strvmarv/total-recall-marketplace`
  then `codex plugin add total-recall@strvmarv-total-recall-marketplace` installs total-recall into
  [Codex CLI](https://developers.openai.com/codex). A new `.codex-plugin/plugin.json`
  (which takes precedence over `.claude-plugin/plugin.json` for Codex and leaves
  every other host's files untouched) points Codex at `codex/hooks.json` and
  `codex/mcp.json`. `codex/hook.js` is a node (not bash) `SessionStart` /
  `UserPromptSubmit` hook, because Codex runs hook commands through PowerShell on
  Windows where the Claude hooks' `$CLAUDE_PLUGIN_ROOT` expands to nothing: it
  injects the same startup instructions plus a short Codex note and re-asserts
  pinned directives on the turn-count floor trigger by running the provisioned
  engine directly (the token-growth trigger is not used: a Codex session log
  grows several times faster than the conversation). `codex/mcp.json` is
  generated from `codex/bootstrap.cjs` (`node codex/build-mcp.js`): Codex expands
  no variables in a bundled server's command, so it runs a launcher that finds
  the installed plugin in Codex's cache and starts `bin/start.js` with no
  working directory set, and it forwards the environment variables the engine
  needs (Codex otherwise sanitizes the server's environment). Verified end to end
  on Codex 0.162.1 (Windows): hooks, tool calls, floor cadence over a resumed
  session, and store parity between hook and server; on update, Codex replaces
  the old version directory (the shim's re-provisioning after an update was not
  exercised). Plugin hooks need a one-time trust review (`/hooks`) and `codex exec`
  needs the tool-approval policy described in INSTALL.md; neither interactive flow
  was observed, nor macOS/Linux.
- **Version sync now covers seven files and is tested.** `.codex-plugin/plugin.json`
  joins the release sync, and `codex/manifest.test.js` fails when
  `package.json`, both `package-lock.json` fields, the four plugin manifests and
  `hermes-plugin/plugin.yaml` disagree.
- **pi harness support.** `pi install npm:@strvmarv/total-recall` (or
  `pi install git:github.com/strvmarv/total-recall`) installs total-recall into
  [pi](https://pi.dev). A new extension (`pi/extension.js`, declared in
  `package.json`'s `pi` block) registers the MCP server, injects the same
  startup instructions and capture / retrieve / feedback rules Claude Code gets
  (once per session and again after a compaction), and re-asserts pinned
  directives on the turn-count floor trigger by running the already-provisioned
  engine directly. A new `/total-recall <subcommand>` command (alias
  `/total-recall:commands`, with tab completion) asks the model to run any
  command-surface subcommand. Subagent children and `--no-session` runs are
  skipped. See `INSTALL.md` for known gaps (no `session_end`, no token-growth
  trigger, `setup`/`update` n/a, `compact --fast`).
- `pi-package` npm keyword and the `pi/` directory in the published files.

### Changed

- `.cursor-plugin/plugin.json` now declares `hooks` and `mcpServers` paths under `cursor/`, which
  replace Cursor's default discovery of `hooks/hooks.json`; `hooks/hooks-cursor.json` is kept for
  hand-wired setups with its variable braced (still untested).

- The Copilot manifest moved from `.copilot-plugin/plugin.json` (not in Copilot CLI's documented manifest lookup, and not read by 1.0.98-2)
  to `.github/plugin/plugin.json`, which Copilot finds before `.claude-plugin/plugin.json`; its
  `hooks` path replaces `hooks/hooks.json` for Copilot. The release version-sync list still
  has seven files (the path changed).

- `skills/commands/SKILL.md`: the pinned-floor host table gains a `pi` row, the
  growth-trigger sentence is qualified for hosts without a transcript, and
  `compact` says what to do on hosts without the compactor agent.

### Fixed

- **Release workflow: the macOS build runs on `macos-15`.** GitHub no longer serves the `macos-14` image ("The configuration 'macos-14-arm64-us-default' is not supported"), so the first v4.3.0 tag push cancelled the osx-arm64 leg before it started and skipped the npm publish. Nothing was published from that run; v4.3.0 was re-tagged on the fix.

## 4.2.3 - 2026-09-04

### Fixed

- **`SessionEnd` hook reported "Hook cancelled" on every `/exit` on Windows
  (#28).** Claude Code shares a hard-coded 1.5s default budget across all
  `SessionEnd` hooks, raising it only when a hook's `hooks.json` entry sets
  an explicit per-hook `timeout` (capped at 60s). This hook's chain
  (`bash` → `node` → the self-contained .NET engine → SQLite) measures
  ~1.2-1.3s end-to-end on Windows — right at that default ceiling.
  Measuring the chain layer-by-layer (against the real, ~250MB production
  database) attributes the cost almost entirely to process-spawn overhead
  across three cold-starting runtimes; the SQLite migration-check + hot-tier
  count the CLI actually performs costs ~15-30ms, not the bulk of it. Adds
  `"timeout": 10` to the `SessionEnd` entry in `hooks/hooks.json`, matching
  the issue's own third suggested fix direction ("keep the hook's own work
  below whatever grace window Claude Code enforces"), plus a regression
  test (`hooks/hooks.config.test.js`) asserting the timeout stays set and
  within Claude Code's supported range.

## 4.2.2 - 2026-09-02

### Fixed

- **Cortex storage mode uploaded every locally-scanned skill to Cortex on
  every `session_start`, with no way to turn it off short of leaving cortex
  mode entirely.** `~/.claude/skills` and `{project}/.claude/skills` were
  unconditionally scanned and POSTed to Cortex's `/api/me/skills/import`
  endpoint, duplicating skills a host tool already synced down from Cortex
  globally (e.g. a Cortex skill-sync plugin) as separate user-scoped
  entries. Adds `[skills] auto_import` (bool, **defaults to disabled**) to
  gate the upload; local scanning and `skill_get`/`skill_search` are
  unaffected either way. (#27)

## 4.2.1 - 2026-08-28

### Fixed

- **`pinned-floor` UserPromptSubmit hook could emit invalid/truncated JSON,
  surfacing as Claude Code's "Unterminated string" error.** `run.sh` only
  checked that the CLI's captured stdout was non-empty, not that it was
  complete, valid JSON — a killed or interrupted child (hook timeout, slow
  render on a large pinned block, a pipe hiccup) could still flush a partial
  payload, which bash forwarded verbatim. The captured output is now
  validated with `node` (already a hard dependency of the hook, unlike `jq`)
  before forwarding: it must parse as a non-null, non-array JSON object, or
  the hook falls back to `{}` — the same no-op it already used for an empty
  capture. Also forces `Console.InputEncoding`/`OutputEncoding` to UTF-8 on
  the generic CLI dispatch path (used by `pinned-floor`), matching the MCP
  `serve` path's existing UTF-8 stdio contract, to rule out Windows OEM
  code-page corruption of non-ASCII pinned content on either stream. (#24)

## 4.2.0 - 2026-08-17

### Added

- **Five new host integrations: Cline, Windsurf, Gemini CLI, Zed, and VS Code.**
  Each host gets a postinstall auto-install script that detects the host's config
  directory and writes/merges the total-recall MCP server entry (non-clobbering,
  non-fatal). This brings the supported host count from 6 to 11.

  - **Cline** — detects `~/.vscode/`, writes `cline_mcp_settings.json` (`mcpServers`).
  - **Windsurf** — detects `~/.codeium/windsurf/`, writes `mcp_config.json`
    (`mcpServers`) plus a rules file (stripped from the steering doc) for the
    `session_start` instruction.
  - **Gemini CLI** — detects `~/.gemini/`, writes `settings.json` with both
    `mcpServers` and `hooks` entries, plus `GEMINI.md` (marker-based merge that
    preserves user-saved memories from Gemini CLI's `save_memory` tool).
  - **Zed** — detects Zed config dir via `process.platform` (macOS/Windows/Linux),
    writes `settings.json` (`context_servers` key, not `mcpServers`).
  - **VS Code** — detects VS Code user data dir via `process.platform`, writes
    `mcp.json` (`servers` key) plus `~/.copilot/mcp-config.json` (`mcpServers`)
    for Agent Host portability.

- **Gemini CLI full pinned-floor support.** Gemini CLI's `BeforeAgent` hook uses
  `hookSpecificOutput.additionalContext` — the same JSON shape as Claude Code's
  `UserPromptSubmit`, but with `BeforeAgent` as the event name. A new `gemini-cli`
  arm in `PinnedFloorCommand.cs` emits the correct envelope. Gemini CLI's payload
  includes `transcript_path`, so the token-growth throttle is **active** (unlike
  Kiro, which lacks `transcript_path`). This is the most complete host integration
  after Claude Code itself.

- **Shared install helpers extracted** (`scripts/lib/merge-json-config.js`,
  `scripts/lib/is-repo-self.js`) from the Kiro install script for reuse across all
  new host install scripts. The `mergeJsonConfig` helper handles non-clobbering
  merges into JSON config files with different top-level keys (`mcpServers`,
  `context_servers`, `servers`). The `isRepoSelf` helper uses path-based detection
  to distinguish dev installs from end-user `node_modules` installs.

### Changed

- **`install-kiro-hooks.js` refactored** to use the shared helpers. No behavioral
  change — the merge logic and `isRepoSelf` check are identical, just extracted
  for reuse.

- **Docs updated** (AGENTS.md, README.md, SKILL.md) with the 5 new hosts in the
  per-host status lines, host compatibility table, and cross-tool list.

### Known limitations

- **Cline, Windsurf, Zed, VS Code:** MCP-tools-only — no pinned floor (none have
  shell-command hooks for per-turn context injection). Windsurf has a rules file
  for the session-start instruction.
- **Gemini CLI capture:** `AfterAgent` hook is command-type only (no agent prompt
  injection). Capture is encouraged via `GEMINI.md` but not enforced.
- **VS Code multi-profile:** Only writes to the default profile location.
- **No install-script tests:** Merge logic is untested at the JS level (consistent
  with existing codebase patterns).

## 4.1.0 - 2026-08-17

### Added

- **Kiro IDE host support.** Kiro IDE is now a supported host alongside Claude
  Code, Copilot CLI, Cursor, and OpenCode. The integration uses Kiro's v1 hook
  schema (`.kiro/hooks/*.json`) with three hooks:
  - `SessionStart` agent hook — prompts the agent to call `session_start`,
    loading pinned directives and hot-tier context.
  - `UserPromptSubmit` command hook — shells out to
    `total-recall pinned-floor --host kiro` via `npx`, injecting the pinned
    block as plain-text STDOUT (re-asserted on an adaptive turn-count throttle).
  - `UserPromptSubmit` agent hook — nudges the agent to call `memory_search`
    before answering.
  - `Stop` agent hook — captures corrections, preferences, and decisions via
    `memory_store`.
  An `always`-inclusion steering doc (`.kiro/steering/total-recall.md`) ensures
  the `session_start` instruction is always in context. A workspace-level
  `.kiro/settings/mcp.json` registers the MCP server for dev use; end users get
  MCP config via the postinstall script.

### Fixed

- **`PinnedFloorCommand` skip/catch paths emitted `{}` unconditionally.** For
  Kiro, STDOUT is injected verbatim into context, so `{}` polluted the agent's
  context on 5 of every 6 turns (default `floor_every_n_turns=6`). The skip and
  catch paths now emit an empty string for `kiro` (via a `NoOpForHost` helper),
  keeping `{}` for JSON-envelope hosts.

- **`install-kiro-hooks.js` was not actually non-fatal.** The script had zero
  `try/catch`; a `readdirSync`/`cpSync` throw could reject the promise and break
  `npm install`. The entire body is now wrapped in `try/catch`. Additionally,
  `mergeMcpConfig` now bails on a parse failure of an existing `mcp.json`
  instead of resetting it to `{mcpServers:{}}` and clobbering the user's other
  MCP server entries.

- **`isRepoSelf()` used `package.json` name check — blocked all end users.** The
  check compared `name === "@strvmarv/total-recall"`, which matches both the dev
  repo and the published tarball inside `node_modules`. This silently disabled
  MCP config + steering installation for the entire end-user population.
  Replaced with a path-based check (`!ROOT.split(sep).includes("node_modules")`).

### Changed

- **Install script no longer copies hooks to `~/.kiro/hooks/`.** Hooks are
  workspace-level only (ship in `.kiro/hooks/` in the npm tarball, activate
  per-workspace). Copying them user-level caused double-firing with the
  workspace-level copies. Steering copy is guarded by `isRepoSelf()` to avoid
  double-fire for dev installs.

- **`.kiro/` added to `package.json` `files[]`.** Without this, the hooks,
  steering doc, and MCP config were not in the npm tarball.

- **Steering doc terminology aligned with post-4.0 tier model.** Replaced
  "four-tier memory (Pinned/Hot/Warm/Cold)" with "three-tier memory
  (Hot/Warm/Cold with a sticky flag)" throughout, matching AGENTS.md.

### Known limitations

- **Token-growth throttle inactive for Kiro.** Kiro's hook payload has no
  `transcript_path`, so the floor's growth-based trigger cannot estimate
  transcript bytes. The turn-count trigger (`floor_every_n_turns`) still fires
  the floor; only the growth arm is dead. Documented in AGENTS.md.

- **Workspace-level hooks don't reach end-user workspaces.** Kiro scans the
  workspace root's `.kiro/hooks/`, not `node_modules/`. End users get MCP config
  + steering via the install script; the pinned-floor command hook fires via
  `npx`. The session-start/retrieve/capture agent hooks are only active in
  workspaces that vendor `.kiro/hooks/` at their root.

## 4.0.4 - 2026-07-11

### Fixed

- **Corrupt `registry.json` slipped past the 4.0.3 model-integrity guard.**
  `payloadIntact` treated *every* failure reading or parsing
  `models/registry.json` as "no registry → intact", so a registry that was
  present but truncated/unparseable — itself a signature of the interrupted
  extraction 4.0.3 guards against — was waved through and the broken tree
  trusted. Now only a genuinely-absent registry (`ENOENT`) counts as "nothing to
  validate"; a present-but-unreadable or unparseable `registry.json` is a
  not-intact failure, so provisioning re-downloads instead of trusting a partial
  tree. (Follow-up to the 4.0.3 fix, from PR #15 review.)

## 4.0.3 - 2026-07-10

### Fixed

- **Truncated bundled embedding model was trusted forever, breaking all
  embedding.** Provisioning treated the engine binary's mere presence as proof
  the whole extracted tree was intact. If a `tar` extraction was interrupted
  partway (process killed mid-extract) it could leave the binary present but the
  ~133 MB `model.onnx` truncated to a fraction of its size — and the fast path
  trusted that broken tree on every subsequent launch, even stamping a
  `.verified.json` success marker over it. Every embed (`memory_store`,
  `memory_search`, pin's re-embed) then failed with "Model 'bge-small-en-v1.5'
  not found" because `ModelManager` correctly rejected the size-mismatched model
  and fell back to an empty user dir. The archive sha256 gate could not catch
  this: it validates the downloaded tarball, not the extracted tree.

  Provisioning now validates the extracted model payload against the `sizeBytes`
  in the tree's own `models/registry.json` — a cheap `stat`-based size check (no
  re-hash) run both immediately after extraction (a truncated model is now a
  retryable failure that never stamps `.verified.json`) and on the present-tree
  fast path (a previously-broken tree self-heals by re-downloading instead of
  being trusted indefinitely). This also closes the gap on the unverified
  background-provisioner path, which extracted with no checksum at all.

## 4.0.2 - 2026-07-10

### Documentation

- **Docs brought in line with the 4.0 tier model.** README, AGENTS, PRIVACY, and
  the TODO backlog described the retired four-tier Pinned/Hot/Warm/Cold model.
  Updated to the three-tier Hot/Warm/Cold model with the sticky flag:
  warm-default ingress, earned warm→hot promotion (`access_count >= 5` AND
  `decay_score >= 0.7`), the 1,200-char hot cap (sticky exempt), `unpin` leaving
  an entry in hot as an earned resident, the deprecated `[tiers.pinned]` alias +
  new `[tiers.hot] max_content_chars` / `[compaction] promote_min_access` config,
  and the compaction "compact" terminology. Code-only release — no behavior change.

## 4.0.1 - 2026-07-10

### Fixed

- **Pin/promote/demote crash on databases with legacy vec orphans.**
  `memory_pin`, `memory_promote`, `memory_demote`, and host import move an
  entry between tiers and re-embed via `VectorSearch.InsertEmbedding`, which —
  unlike `SqliteStore.InsertWithEmbedding` — did not clear a pre-existing orphan
  vec row at the reused rowid. On a DB carrying orphans (accumulated by the
  pre-4.0 write-time eviction path, or migrated from the legacy TS store), the
  moved entry's recycled rowid collided: `UNIQUE constraint failed on
  <tier>_memories_vec primary key`. 4.0.0 exposed this by routing pins into the
  hot vec table. `InsertEmbedding` now performs the same orphan-delete
  `InsertWithEmbedding` documents; a regression test seeds an orphan at a reused
  rowid and asserts the re-embed succeeds.

## 4.0.0 - 2026-07-10

Tier model v2 — the Pinned tier is merged into Hot as a `sticky` flag, new
memories default to Warm and *earn* their way into Hot by access evidence, and
existing data is migrated automatically on first `session_start`.

### Changed

- **BREAKING: Pinned tier retired → sticky-hot.** `Tier.Pinned` no longer
  exists. Pin/unpin (MCP **and** CLI) and `pinned:true` now set/clear a `sticky`
  flag on Hot. Sticky entries are unbounded, injected first, and never
  auto-demoted, evicted, or compacted. The `pinned_*` tables are dropped by the
  migration.
- **BREAKING: warm-default ingress.** `memory_store` with no tier now defaults
  to **Warm** (was Hot). Coding-agent prose is already in the conversation
  context; keeping it out of Hot avoids double-billing the context budget.
- **Hot per-entry char cap** of **1,200 chars**. Oversized Hot writes are
  rejected/redirected.
- **Compaction terminology.** The compaction hot→warm step "promote" is renamed
  **"compact"** (`CompactionDecision.Compact`, `Result.Compacted`) across code,
  CLI, and the compactor agent prompt. Tier *promotion* keeps the "promote"
  name — it is now the only meaning.

### Added

- **Access-earned promotion.** Warm→Hot now requires `access_count >= 5` **AND**
  `decay_score >= 0.7`, closing the "fresh entry has decay ~1.0" auto-promotion
  trap. `promote_min_access` config (default `5`).
- **One-time data migration** on first `session_start`: SQLite runs an
  app-layer, single-transaction, re-embedding move (old non-sticky hot → warm;
  legacy `pinned_*` → sticky-hot; then drops `pinned_*`), idempotent via a
  `_meta` flag; Postgres does the equivalent in-place tier-column `UPDATE`. All
  columns + embeddings preserved.
- **Compaction fast/deep split.** CLI `compact` defaults to the fast heuristic;
  `--deep` documents the host-orchestrated LLM path. The compactor skips sticky
  rows and guards against oversized entries.

### Migration & back-compat notes

- The migration is **automatic and irreversible** — downgrading after it runs is
  unsafe. Back up `~/.total-recall/total-recall.db` first if you want a
  rollback path.
- `[tiers.pinned]` config still parses (deprecated alias; its `Floor*` fields
  drive the per-turn re-injection hook, now repointed to sticky-hot).
- `Pinned`/`PinnedMemories`/`PinnedKnowledge` JSON field names retained
  (populated from sticky-hot) for host/Web wire compat.
- Legacy `tier:"pinned"` imports map to sticky-hot; current pins round-trip via
  a new `sticky` export field.

## 3.7.0 - 2026-06-25

### Added

- **User-triggered compaction with proactive hints.** The `SessionEnd` hook no longer injects a compaction *directive* — Claude Code's `SessionEnd` schema rejects `hookSpecificOutput`/`additionalContext`, and no hook fires at session end with a following model turn to execute one. Compaction is now user-triggered via the `/total-recall:commands compact` command (model-driven LLM summaries by default, `--fast` for a no-LLM heuristic sweep), and the plugin nudges toward it at three points: `session_start`, throttled mid-session (once per session), and session end.
- **`tiers.hot.compaction_hint_threshold` config** (default `5`). Hot-tier entry count at or above this triggers the compaction hint on all three surfaces; `0` disables it uniformly.
- **`total-recall compact --run`** — real heuristic hot→warm sweep from the CLI, sharing one decay implementation (`Decay.fs`) with the `session_end` MCP tool via the new `HotTierCompactor`.

### Fixed

- **`SessionEnd` hook crash.** `hooks/session-end/run.sh` now emits schema-valid output only (`{}`, or a user-facing `systemMessage` at/above threshold), ending the `Hook JSON output validation failed — (root): Invalid input` error. A golden test asserts the output never carries `hookSpecificOutput`/`additionalContext`.

## 3.6.1 - 2026-06-22

### Fixed

- **Release plumbing.** Re-publish so the GitHub Release carries the per-RID binary archives and `provisioning.manifest.json`. The 3.6.0 release published the npm package and built all four platform binaries, but its GitHub Release was created out-of-band and could not receive the asset uploads. No functional changes from 3.6.0.

## 3.6.0 - 2026-06-22

### Added

- **Project-scoped pinned-directive injection.** Pinned entries can now be scoped to a specific git repository so that repo-level rules (coding conventions, branch policies, secret patterns) inject only when the assistant is working in the right project, while global pins (no `project` tag) continue to inject everywhere. Three injection sites are all consistent: `session_start`, `session_refresh`, and the per-turn pinned-floor hook. Fail-closed: when no git repo is detected in the current working directory, only global pins inject — no cross-project leakage.
- **`tiers.pinned.project_scoping` config flag** (default `true`). Set to `false` to restore the legacy behavior of injecting all pins in every session regardless of repo.
- **`ProjectResolver`** (`src/TotalRecall.Infrastructure/Memory/ProjectResolver.cs`) — pure filesystem detection of the current git repo: walks upward from cwd for `.git`, follows linked-worktree `gitdir:` pointers, and reads `[remote "origin"] url` from the git config file to produce a lowercase `owner/repo` slug (e.g. `acme-org/widget-service`). Handles scp-style, https, and ssh:// remote URLs; strips trailing `.git`; falls back to the bare repo-root folder name when no remote is configured. Results are memoized in-process keyed by normalized absolute cwd; any error yields `null` (treated as "no repo").
- **`ProjectKey.FromRemoteUrl`** (`src/TotalRecall.Infrastructure/Memory/ProjectKey.cs`) — pure URL-to-slug reducer shared by `ProjectResolver` and tests.
- **`PinnedScope.OptsFor`** (`src/TotalRecall.Infrastructure/Memory/PinnedScope.cs`) — single policy function: scoping off → all pins; scoping on + repo detected → repo pins + globals; scoping on + no repo → globals only.

## 3.5.2 - 2026-06-17

### Security

- **The retrieval benchmark no longer touches or surfaces your live data.** `eval_benchmark` (and the CLI `eval benchmark`) ran against your real database — seeding the synthetic corpus into it and hybrid-searching the whole store — so per-query results could surface real memories/KB (internal names, even secret-bearing docs) and the seeded corpus was outranked by your real entries. The benchmark now runs against an isolated, ephemeral database created, used, and deleted per run: your live store is never read or written, and match rates reflect only the corpus.
- **`eval_grow` can no longer leak sensitive content into the public benchmark.** Candidates are captured from real retrieval misses; accepting one appended the query and a snippet of the surfaced content to the committed `eval/benchmarks/retrieval.jsonl`. Accepts are now scanned and blocked when they contain secrets/PII (emails, API tokens, private keys — always on) or a configured internal term; blocked candidates stay pending for review. The CLI table and web UI grow list flag sensitive candidates.

### Added

- **`eval.grow.sensitive_terms` config** (string array, default empty) — caller-supplied internal terms (product names, customers, hostnames) to block from the grow benchmark, on top of the always-on generic secret/PII detection. Matched case-insensitively as substrings and kept in your **local** config, so no internal names ship in the public defaults.

## 3.5.1 - 2026-06-17

### Fixed

- **Web UI "Run benchmark" no longer fails with "Benchmark failed."** `eval_benchmark` (and the CLI `eval benchmark`) defaulted the bundled corpus/benchmark to working-directory-relative paths, so the run threw `FileNotFoundException: corpus not found: eval\corpus\memories.jsonl` whenever the host process (the Web UI server, the MCP host) was launched from anywhere other than the package root. The bundled `eval/` files now resolve relative to the binary location (walking up from `AppContext.BaseDirectory`), the same way the embedder locates `models/`. The shared resolution lives in a new `EvalPaths` helper, which also replaces the two duplicated path-resolvers that `eval_grow` had been carrying.

## 3.5.0 - 2026-06-16

### Added

- **Retrieval-quality feedback loop.** New assistant-only `memory_feedback` MCP tool lets the host confirm whether a retrieval was actually used; `memory_search` and `kb_search` now return a `retrievalId` to correlate the feedback. `eval_report` (and the Insights health score) infer un-acted retrievals as misses after a configurable grace window, scoped to the `assistant` query source. The Dashboard **"Retrieval quality"** card is now driven by this real signal instead of always reading 0%.
- **Web UI loading vocabulary.** Two reusable components — `Skeleton` (page/panel loads) and `OperationProgress` (determinate "X of N" + bar, or indeterminate + live elapsed) — now give consistent feedback across Insights, Eval, Knowledge Base, Memory, and Config. Both respect `prefers-reduced-motion`.
- **Insights retrieval-gap cards explain themselves and hand off to Eval.** A gap card now says why the query matters and deep-links to the Eval → **Grow benchmark** section (`/eval?grow=<query>`), which surfaces a banner and highlights the matching candidate (or explains when it isn't a current candidate).

### Changed

- **`memory_search` response shape is now `{ retrievalId, results }`** (was a bare array); `kb_search` responses gain a top-level `retrievalId`. Retrieval events now record a real `query_source` (`assistant` / `web-ui` / `cli`) instead of a single hardcoded value, so retrieval-quality metrics can be scoped to assistant traffic.

- **Retrieval metrics now exclude still-pending retrievals.** `Metrics.Compute` resolves outcomes at read time and drops recent un-acted retrievals (inside the grace window) from the scored set entirely, so `missRate` is now `misses / scored` rather than `misses / allEvents`. Charts that tracked the old denominator will shift accordingly.

> **Upgrade note:** because un-acted assistant retrievals are now inferred as misses after the grace window, the "Retrieval quality" / health-score metric may *dip* immediately after upgrading and stay lower until the assistant adopts `memory_feedback`. This reflects real measurement (previously these retrievals were silently treated as hits), not a regression.

## 3.4.1 - 2026-06-15

### Fixed

- **Insights "Pin" no longer suggests memories that can't be pinned.** Pin candidates are now filtered by the pinned-tier content cap (`tiers.pinned.max_content_chars`, default 500), so clicking **Pin** no longer fails with "pinned entries are limited to N characters". The candidate query is also bounded so it never scans an entire tier on large stores.
- **Applying an Insights suggestion (or any `config_set`) no longer crashes when your `config.toml` contains an array.** The AOT TOML writer now serializes inline arrays (e.g. `[skills] extra_dirs`), fixing `System.NotSupportedException: ConfigWriter cannot serialize TOML value of type TomlArray`.
- **Deleting a near-duplicate cluster removes the card immediately.** The Insights page now optimistically drops a cluster the moment its deletes succeed instead of waiting for a refetch (which could lag behind the delete under the cortex backend).
- **The left nav rail is now sticky.** Only the page body scrolls; the rail — including the light/dark toggle — stays fixed to the viewport on long pages.

## 3.4.0 - 2026-06-15

### Added

- **Eval page in the web UI** — a retrieval-quality command center for the previously CLI-only eval toolchain. Review a rich `eval_report` (hit/miss/precision/MRR/latency with per-tier and per-content-type breakdowns, top misses, and compaction health), **run the retrieval benchmark** against the local embedder, **grow** the benchmark by accepting/rejecting candidates auto-captured from real retrieval misses, and **compare** two config snapshots (plus create named snapshots). The four execute/write tools (`eval_benchmark`, `eval_compare`, `eval_grow`, `eval_snapshot`) are now reachable from the web UI, token-gated like every other call.
- **Server-side Insights engine (`insights` MCP tool).** Replaces the old client-side heuristics with entry-level analysis of your local store: near-duplicate memory clusters (same-tier cosine over the live set), pin-promotion candidates (high access count, not yet pinned), retrieval gaps, a recall-vs-threshold curve, and a self-explaining health score with a four-part breakdown. Works in every backend mode (user memories are stored locally and locally embedded even under cortex).

### Changed

- **The Insights page is rebuilt on the new engine and every card is now actionable** — no more dead "info" cards. Merge a near-duplicate cluster ("keep newest, delete the rest"), promote a frequently-accessed memory to pinned inline, jump from a retrieval gap straight into the Eval page, and apply a suggested similarity threshold from the recall curve. The health score now expands to show its retrieval / capture / pinned / KB breakdown. The web UI left rail now has **seven** sections (Insights · **Eval** added).

### Docs

- README and the AGENTS guides updated for the new Eval page, the `insights` tool, and the rebuilt Insights page.

## 3.3.1 - 2026-06-15

### Fixed

- **`total-recall ui` (and every other non-`serve` subcommand) no longer floods the console and dies.** Since 3.2.0, `bin/start.js` wrapped _every_ invocation in the MCP stdio shim, which spawns the engine and performs an `initialize` handshake. Only `serve` speaks MCP JSON-RPC on stdout; `ui`, `reindex-embeddings`, `config`, and `dump-catalog` emit human-readable output. Routing those through the shim made it parse their stdout as protocol (`[total-recall:shim] dropping unparseable line`, repeated) and time out the never-answered handshake — killing and restarting the process five times before giving up in degraded mode, which tore down the web server ("it stopped working"). The launcher now dispatches: `serve` (or no args) goes through the shim; every other subcommand provisions then spawns the engine directly with inherited stdio (`bin/shim/direct.js`).
- **The web UI no longer jumps back to the Dashboard after sitting idle on another page.** This was a downstream symptom of the same bug: the shim restarted the web server every ~30s underneath the open tab, minting a fresh per-launch token each time and breaking the loaded page. With the launcher fix the server is a single stable process, so the open page stays put.

### Changed

- **Denser Config page.** The Config sections now tile into a responsive multi-column grid (instead of one tall stacked column) with tighter row and section spacing, so wide screens pack several sections per row.

### Docs

- Added a sanitized Dashboard screenshot to the README **Web UI** section.

## 3.3.0 - 2026-06-15

### Changed

- **The built-in web UI was redesigned with a developer-native _Terminal / Archive_ visual identity.** The generic top-bar layout was replaced with a fixed **left navigation rail**; the type system is now monospace-forward (self-hosted **JetBrains Mono** + **IBM Plex Sans**, bundled by Vite — no CDN, fully offline), over a faint ruled-grid backdrop with an amber phosphor accent. The whole surface is now driven by a CSS custom-property token system.
- **Dark and light themes with a toggle.** The UI ships both themes; the choice persists in `localStorage` and defaults to the OS `prefers-color-scheme` on first visit (a no-FOUC inline script applies it before first paint). Recharts charts read their colors from the theme tokens and recolor live on toggle. Motion (the blinking brand caret, staggered panel reveals) respects `prefers-reduced-motion`.

### Added

- **⌘K / Ctrl-K command palette.** A keyboard-first palette jumps to any page and runs live, debounced search across memories and the knowledge base (via the existing allowlisted `memory_search` / `kb_search` tools), with full arrow-key navigation and ARIA combobox semantics. It replaces the previous inline global-search box.

### Fixed

- **The Dashboard tier-composition bar now renders.** The stacked `layout="vertical"` bar chart was missing its category axis, so it drew zero segments; adding a hidden category `YAxis` restores the proportional pinned/hot/warm/cold/KB bar.

## 3.2.1 - 2026-06-15

### Fixed

- **The built-in web UI (`total-recall ui`) now actually ships in the published binaries.** The release workflow built the engine with `-p:PublishAot=true` but never passed `-p:BuildSpa=true`, so every published binary since the UI landed (3.1.0) served only the Node-free placeholder ("SPA assets were not built"). The `Publish (Unix)` and `Publish (Windows)` steps now pass `-p:BuildSpa=true`, so Vite builds the React SPA and it is embedded (served via `ManifestEmbeddedFileProvider`, AOT-safe) into each released RID. No source changes to the UI itself — it was complete since 3.1.0, just not bundled.

## 3.2.0 - 2026-06-14

### Changed

- **The MCP server is now a thin always-on shim that never drops its connection.** `bin/start.js` was rewritten as a zero-dependency Node MCP shim: it answers `initialize`/`ping`/`tools/list` instantly, returns a structured `not_ready` tool result while the engine binary provisions in the background, then spawns and proxies to the engine — supervising and restarting it on crash with exponential backoff. This eliminates the `MCP error -32000: Connection closed` failure on first launch after a plugin update (the old launcher did a detached background download and then exited, closing the stdio transport). All not-ready states (`provisioning`, `starting-engine`, `engine-restarting`, `engine-failed`) are surfaced in-band as `{status:"not_ready", phase:…}` tool results, so recovery is uniform across every harness — wait briefly and retry; the connection stays up.
- **Engine archives are sha256-verified on download** against a release `provisioning.manifest.json` (generated and attached by the release workflow). Previously there was no checksum on the downloaded archive. A `.verified.json` marker next to the binary keeps steady-state startup instant (no re-hash of the ~90 MB tree).
- **`tools/list` is served from a committed `catalog.json` before the engine is up**, then re-fetched from the authoritative engine via `notifications/tools/list_changed` once the shim is proxying. A CI drift guard keeps `catalog.json` in sync with the engine's local tool surface.

### Added

- **`dump-catalog` engine command** (`dotnet run --project src/TotalRecall.Host -- dump-catalog`) emits the local/sqlite `tools/list` JSON used to (re)generate the committed `catalog.json`.
- **Shim test suite** (`npm run test:shim`) — `node:test`, zero-dependency, runs as a Node-only CI job independent of .NET.

## 3.1.0 - 2026-06-14

### Added

- **Built-in local web UI (`total-recall ui`).** The single NativeAOT binary now serves an embedded React SPA on a local Kestrel server (default port 5577). Six sections: Dashboard (tier composition, retrieval quality, token usage, recent activity, trend sparklines), Memory (browse/search/filter/promote/demote/pin/delete), Knowledge Base (collections, search, ingest, refresh, remove), Usage (token spend by host/project/model/time with client-side cost estimates), ✨ Insights (memory-health score + suggestion cards — cost-spike, capture-mix, pinned pressure, retrieval misses, empty KB — pure heuristics, no LLM), and Config (edit a safe subset of tuning knobs, persisted via `config_set`; storage/embedding read-only). Security model: loopback-only bind by default, ephemeral per-launch bearer token injected directly into the served HTML, Host-header allowlist against DNS-rebinding. The SPA build is opt-in (`-p:BuildSpa=true`); default builds and the full test suite remain Node-free. **v1 deferrals** (tracked in `docs/TODO.md`): server-side `/api/insights` engine, editable/persisted `[pricing]` config section, usage activity heatmap (needs hourly `GroupBy`), per-project trend sparklines, Cortex remote phase, SSE/live push.

## 3.0.4 - 2026-06-13

### Fixed

- **The embedder re-index no longer runs synchronously on the MCP startup path — it can no longer brick the server.** v3.0.2 wired cortex (and sqlite) to re-embed the local vector index on a model change, but that re-embed ran *inside server composition, before the MCP `initialize` handshake*. On a real populated database this was fatal: re-embedding ~25k rows through the bge ONNX model took ~27 minutes — far past Claude Code's hard, no-retry stdio startup timeout — and because the re-embed was a single atomic transaction, the timeout kill rolled it back, so the *next* launch re-ran the identical doomed migration: an unrecoverable boot loop. The re-embed now runs in an **in-process background worker** (its own connection + embedder) that starts only after the server is already answering tool calls; the startup path never blocks on it. The `Program.cs` boot sequence carries an explicit invariant comment so future startup work doesn't regress this.

### Changed

- **Re-index is now batched and resumable.** `EmbeddingReindexer.RunBatched` embeds off the write lock and commits per batch, advancing a `_meta` cursor, so concurrent `memory_store`/search keep working during a re-index and an interrupted run resumes from where it left off instead of restarting from zero. A best-effort advisory lock keeps two runners from doubling up, and the embedder fingerprint is stamped only after a full pass completes. One reusable `ReindexCoordinator` now backs both the startup auto-migration and the `total-recall reindex-embeddings` CLI (the CLI runs it foreground/to-completion, also resumable). The old single-transaction `RunAtomicSqlite` is removed.
- **Background-task progress is surfaced to the user, portably.** `session_start` and the `status` tool now return a `backgroundTasks` block: `reindex` (state + done/total + model) while a re-index runs, plus a one-time `setup` notice when first-run binary provisioning just completed. These ride in tool results — the host-agnostic, MCP-standard channel — and the `using-total-recall` skill surfaces them, so during a re-index you're told local semantic retrieval is degraded until it finishes (FTS still works), in any harness. It self-heals when the pass completes.
- **First-run binary download now reports progress.** The detached provisioner records download bytes/total into its lockfile so `bin/start.js` prints `downloading memory engine — N/M MB (X%)…` on a launch while the download is still in flight, and writes a `binaries/<rid>/.provisioned.json` marker the server consumes once to confirm setup completed.

## 3.0.3 - 2026-06-13

### Fixed

- **First launch on the git/marketplace install path no longer risks bricking the MCP server.** `bin/start.js` used to download the ~90 MB binary archive *synchronously* before it could answer the MCP startup handshake; on a slow connection that exceeded Claude Code's stdio startup timeout (a hard wall-clock with no retry), so the server was killed and the partial download discarded — leaving the binary unprovisioned on every subsequent launch. Now a missing binary triggers a **detached background download** (pid-stamped lockfile guards against duplicates) and `start.js` exits fast with a one-time "downloading in the background, available next launch" message. The next launch finds the binary and starts instantly. npm installs are unaffected (the binary ships in the tarball).
- **Windows binary extraction no longer fails when a GNU `tar` is on `PATH`.** `scripts/fetch-binary.js` extracted the release archive with bare `tar`; if Git for Windows / msys `tar` resolved first, it treated the `C:\...` destination as a remote host and failed (`Cannot connect to C:`). It now pins to the bundled `%SystemRoot%\System32\tar.exe` (bsdtar) on Windows.

## 3.0.2 - 2026-06-13

### Fixed

- **The cortex backend now auto-migrates its local vector index on a model change, like sqlite.** Embedder-fingerprint stamping had only ever been wired into the sqlite and postgres startup paths — never the cortex path — so a cortex database was never stamped. After the bge model swap it ended up *unstamped but populated* with stale vectors, and the local index silently kept producing degraded retrieval (no auto-migration, no warning). The migration is now wired into the cortex composition (`OpenCortexCore`) and re-embeds the **local** vec0 index in place under `embedding.on_model_change = "auto"`; the remote cortex re-embeds independently as before.
- **An unstamped-but-populated index is now treated as a model change instead of "fresh DB".** Previously a database with no fingerprint was assumed brand-new and simply stamped — correct only when the index is empty. If it holds vectors (from an unknown prior model), the startup migration now re-embeds them (`auto`), warns (`warn`), or refuses to start (`block`), the same as a detected fingerprint mismatch. A genuinely empty database is still just stamped. This applies to both the sqlite and cortex local indexes and closes a silent-degradation window for databases that predate fingerprint stamping.

## 3.0.1 - 2026-06-13

### Fixed

- **Release packaging: strip debug symbols so `npm publish` stops exceeding the registry payload limit.** v3.0.0 failed to publish with `npm error 413 Payload Too Large` — the release artifacts had been bundling developer-only debug symbols (`.pdb`/`.dbg`/`.dsym`, roughly half of every tarball since v2.2.0), and stacking the new 133 MB bge model on top pushed the publish payload past npm's ceiling. The release workflow now removes those symbols from each staged publish tree (the runtime never loaded them), which trims both the npm package and the per-RID release tarballs back under the limit, and a pre-publish size guard now fails the build if the packed tarball regresses past a safe ceiling. No code or runtime behavior change; this is the release that actually ships the v3.0.0 changes below.

## 3.0.0 - 2026-06-13

### Breaking Changes

- **The local embedding model changed** (`all-MiniLM-L6-v2` → `bge-small-en-v1.5`), so databases created before this release hold vectors in the old model's space. The upgrade path now depends on backend:
  - **sqlite** (the default) migrates automatically — see the auto-migration entry under _Changed_. No manual step; upgrading no longer blocks startup.
  - **postgres** cannot auto-migrate: under the default `embedding.on_model_change = "auto"` the server refuses to start with an explicit, actionable error. Re-ingest into a fresh database, or set `embedding.on_model_change = "warn"` to run with (temporarily) degraded retrieval.
  - **cortex** users should run `total-recall reindex-embeddings` to re-embed the local vector index — cortex sync is content-only, so the remote re-embeds independently and only the local index is stale.

### Changed

- **Replaced the local embedder with `bge-small-en-v1.5`** (CLS pooling, 384-dim, with an asymmetric query prefix applied to searches), retiring `all-MiniLM-L6-v2`. The model (~133 MB fp32) is now fetched and sha256-verified at release build time from a pinned HuggingFace revision (`scripts/fetch-bge-small.sh`) and bundled into the per-RID release artifact — it is no longer committed to the repo / Git LFS, and there is no runtime HuggingFace download (the runtime validates the bundled model and fails fast if absent).
- **Startup auto-migrates local embeddings on a model change instead of hard-blocking.** When a database's stored embedder fingerprint no longer matches the configured embedder, the **sqlite** startup path re-embeds every local vector in place — atomically (`BEGIN IMMEDIATE` → re-embed → re-stamp → `COMMIT`, so a mid-run failure rolls back and the next launch retries cleanly) — then continues, under the default `embedding.on_model_change = "auto"`. The first launch after an upgrade therefore does a one-time re-embed and logs progress to stderr. The new **`embedding.on_model_change`** setting controls this: `auto` (default) re-embeds in place; `warn` runs with the stale vectors and a recurring warning (no re-stamp); `block` restores the previous fail-fast refusal. On **postgres**, `auto` surfaces an explicit error (in-place migration isn't supported there); `warn`/`block` behave as on sqlite.

### Added

- **`total-recall reindex-embeddings` CLI command** — re-embeds existing local vectors into the new model's vector space after an embedder swap. With `on_model_change = "auto"` (the default) sqlite handles this automatically at startup; the command remains for cortex local-index re-embedding, for `warn`/`block` users who deferred the migration, and for a manual offline re-embed.

## 2.2.0 - 2026-06-13

### Added

- **Pinned-directive floor — a per-turn re-injection of the pinned block near the live edge.** Long sessions push the session-start pinned block far enough up the transcript that the model stops honoring it; the floor re-asserts it. A new `UserPromptSubmit` hook (`hooks/user-prompt-submit/run.sh`) runs before each prompt, backed by a new `total-recall pinned-floor --host <host>` CLI command that reads the host hook payload from stdin, consults an adaptive throttle, and on an "inject" turn renders the pinned block live from the DB and emits host-correct `additionalContext` JSON. The throttle re-injects when EITHER trigger trips since the last injection: `floor_every_n_turns` user turns elapse, OR ~`floor_growth_tokens` of transcript growth (measured from transcript byte size). The first turn of a session seeds state and skips, since `session_start` already injected the block. **Fail-safe: the hook never blocks or rejects a prompt** — any error emits `{}` and exits 0.
- **New `[tiers.pinned]` floor config keys** (defaults in parentheses): `floor_enabled` (`true`), `floor_every_n_turns` (`6`), `floor_growth_tokens` (`6000`). Set `floor_enabled = false` to disable the per-turn floor entirely.
- **`PinnedBlockRenderer` extraction (single source of truth).** The pinned-block rendering previously inlined in `SessionLifecycle.BuildPinnedBlock` now lives in `src/TotalRecall.Infrastructure/Memory/PinnedBlockRenderer.cs`, shared by the Server (session start + `session_refresh`) and the CLI (the `pinned-floor` hook). Pinned content is rendered verbatim under the `## Pinned directives (always follow)` header — no detail levels, no truncation — so every injection path produces an identical block.
- **Per-session throttle state** (`PinnedFloorState`) persisted as a small JSON file under `<dataDir>/floor-state/<session>.json`; reads are fail-safe (any error yields a fresh, unseeded state) and stale files older than 3 days are pruned on each run. The throttle decision itself (`PinnedFloorDecider`) is a pure, I/O-free function.

### Per-host capability matrix

| Host | Per-turn floor | Mechanism |
|------|----------------|-----------|
| Claude Code | Active | `UserPromptSubmit` → `additionalContext` |
| Copilot CLI | Pending upstream fix | `UserPromptSubmit` → `additionalContext` (upstream currently ignores the returned `additionalContext`) |
| Cursor | Layered fallback | session-start injection + skill-guided `session_refresh` (`beforeSubmitPrompt` is block-only and cannot inject context) |

## 2.1.1 - 2026-06-10

### Fixed

- **Sync-queue poison pill that crashed `session_end` and wedged the entire Cortex backlog.** A memory whose content held a raw C0 control character (e.g. a captured code snippet containing a literal U+0001) was written to the sync queue as invalid JSON: the hand-rolled, AOT-safe `SyncPayload` escaper only handled `\ " \n \r \t` and emitted every other control byte unescaped, violating RFC 8259. On drain, `SyncService.FlushAsync` parsed payloads inside a `try` that caught only `CortexUnreachableException`, so the resulting `JsonException` propagated uncaught and aborted the whole flush before any item could be marked completed or failed. Because the corrupt row sat at the head of the memory drain batch, every periodic sync re-threw on it and the backlog grew without bound (all rows stuck at `attempts = 0`); `session_end`, which flushes first, surfaced the raw `JsonReaderException`. Two-part fix: (1) `SyncPayload.Escape` now emits the `\b \f \n \r \t` short forms plus `\uXXXX` for every other C0 control character (RFC 8259-compliant), with a no-allocation fast path when nothing needs escaping; (2) `FlushAsync` parses each drained item defensively and quarantines a permanently-unparseable payload — marking it failed with a backoff window and a diagnostic log line — instead of aborting the batch, so one corrupt row can no longer crash `session_end` or block the rest of the queue. Telemetry payloads that deserialize to `null` are quarantined rather than silently dropped, and a latent `JsonDocument` leak in the memory-upsert parse path is closed.

## 2.1.0 - 2026-06-10

### Added

- **Pinned tier** — fourth memory tier for user-pinned entries: always injected verbatim at session start (ahead of the hot tier), structurally immune to decay, warm-sweep demotion, and compaction. session_refresh re-assembles context with pinned entries included. New `memory_pin` / `memory_unpin` MCP tools and `total-recall memory pin|unpin` CLI verbs; `pinned: true` flag on `memory_store`; `pinned` accepted in search/list/recent/export tier filters; `tierSummary.pinned` count, `status` pinned counts, and a `pinned_budget_pressure` session-start hint. Pinned entries are capped at 500 chars (`tiers.pinned.max_content_chars`) — rejected at the door, never truncated. SQLite Migration 16; no Postgres schema change (new `tier` value).

### Known limitations

- **Pinned is local-only for the Cortex backend**: the remote Cortex service has no pinned-tier support yet, so pinned entries are never pushed, pulled, reconciled, or migrated (`migrate_to_remote` skips them). Pinning an entry that already exists remotely leaves the remote copy untouched; a later re-pull of that remote copy can reintroduce it as a separate non-pinned entry. Requires a companion Cortex server change to lift.

## 2.0.0 - 2026-06-04

Active token optimization, phases 1-3. total-recall moves from passive responder toward active co-pilot: accurate token accounting, mid-session context refresh, usage-driven memory selection, tool-result caching, and host-LLM fact extraction.

### Breaking

- **`session_start` `hints` changed from `string[]` to structured objects.** Each hint is now `{priority, type, entryId, summary, suggestedAction, suggestedArgs}` — priority-1 hints carry a pre-filled `memory_promote` invocation. Consumers parsing hints as plain strings must update (the bundled skill, Hermes provider, and CLI are already updated).
- **Tool validation failures are now MCP tool-level errors, not JSON-RPC protocol errors.** A bad argument (missing/empty/wrong type/invalid enum) returns a result with `isError: true` and an `Invalid arguments: <reason>` message the calling LLM can read and act on, instead of a `-32603 Internal error` protocol error with a stack trace on stderr. Hosts with retry logic keyed on protocol errors for bad arguments will observe the new shape. Structured `model_not_ready` payloads now actually reach the wire through the same path (previously destroyed by the generic catch).

### Added

- **`session_refresh` MCP tool.** Mid-session hot-tier refresh: recalculates decay, runs the warm sweep, re-assembles context with current budget, and reports a change summary plus efficiency stats — hot-tier utilization, token-denominated injection impact, real session duration, retrieval count/latency, tool-cache economics (`hits`/`misses`/`tokensSaved`/`hitRate`), and advisory `recommendations` (`session_refresh` past 30 min; `memory_extract` at 8+ retrievals). Optional `task` argument enables task-aware ranking.
- **Tool-result cache: `cache_check` + `cache_store` MCP tools** (sqlite/cortex modes). New `tool_cache` table (migration 15) keyed by `(tool, argsHash)` with TTL freshness at read, expired purge + LRU cap (default 200) at write, and per-session savings counters surfaced via `session_refresh`. Configurable via the new `[tool_cache]` section (`max_entries`, `default_ttl_seconds`).
- **`kb_resolve` MCP tool.** Resolves a file path to its ingested knowledge-base chunks — a token-efficient alternative to re-reading raw files — reporting `tokenEstimate`, `rawFileTokens`, and clamped `savings`.
- **`memory_extract` MCP tool.** Persists facts/decisions/preferences/corrections/action items the host LLM extracted from conversation text. Validates the whole batch before writing anything, dedups against existing hot-tier content, and maps fact types onto entry types (`fact`/`action_item` → surfaced).
- **Progressive summarization + dynamic detail levels in context assembly.** Hot entries render full → summary → compact as budget allows, with extractive truncation, auto-tagging (keyword extraction + entry type), and a compact-view footer listing IDs for `memory_get` follow-up.
- **BERT-accurate token counting.** `countTokens`/`truncateToTokens` in the Core tokenizer; budget ceiling stays on the fast heuristic, exact counts reported in responses.
- **Per-type decay half-lives.** Corrections 720h, preferences 336h, decisions 168h, surfaced 72h (configurable via `decay_half_life_*` keys), falling back to `decay_half_life_hours`.
- **Injection tracking + dead-weight auto-demotion.** Migration 14 adds `times_injected` to all six content tables; entries injected ≥ `auto_demote_min_injections` times (default 10) but never accessed are demoted to warm during the sweep.
- **Task-aware context selection.** `session_refresh(task: ...)` blends decay with task relevance under the new `tiers.hot.task_weight` config (default 0, pure decay).
- **`ListEntriesOpts.Source` filter** across SQLite, Postgres, and the routing store.

### Fixed

- **MCP `serverInfo.version` no longer hardcoded `0.1.0`.** Resolved from the assembly's `InformationalVersion` (stamped by the release workflow), matching the `--version` behavior fixed previously for the CLI.
- **Plugin manifest version drift repaired.** `.claude-plugin`/`.copilot-plugin`/`.cursor-plugin` manifests had been left at 1.3.0 by the 1.4.0 release, and `package-lock.json` had said 1.0.0 since the 1.0.0 release. All five version sites are synced at 2.0.0.

### Changed

- Handler surface grew to 47 handler files (39 in the core registry + mode-dependent cache/usage/skill registrations); handler docs reconciled.
- A pre-existing `memory_store` defect (caller `entryType` ignored for the `entry_type` column) is documented in `docs/TODO.md` — `memory_extract` writes the mapped type correctly; the siblings will be reconciled in a follow-up.

## 1.4.0 - 2026-06-03

### Added

- **Hermes Agent MemoryProvider plugin.** total-recall now ships a Python plugin (`hermes-plugin/`) that implements Hermes's `MemoryProvider` ABC, giving Hermes Agent deep automatic memory integration:
  - **Hot tier injection** — session context is injected directly into the system prompt at session start (no tool call needed)
  - **Per-turn prefetch** — semantic search fires before each turn, providing relevant memories as fenced context
  - **Per-turn sync** — every exchange is automatically written back to total-recall as a surfaced memory entry
  - **Session lifecycle** — `session_start` runs on agent init (auto-import from Hermes), `session_end` compacts on session close
  - **Built-in memory mirroring** — writes to Hermes's built-in `memory` tool are mirrored into total-recall as corrections and preferences
  - **Drop-in memory tool replacement** — the Hermes `memory` tool schema is preserved but routed through total-recall's warm tier, so the agent's workflow doesn't change
  - **Auto-install on `npm install`** — plugin is deployed to `~/.hermes/plugins/total-recall/` and config is flipped automatically when Hermes is detected
- **`memory_list` MCP tool.** Paginated listing of memory entries with optional filters: `tier` (hot/warm/cold), `content_type` (memory/knowledge), `tags`, `project`, `source_tool`, `limit` (default 50, max 200), `offset`. Returns combined results across all tiers sorted by `created_at DESC`.
- **`memory_get_all` MCP tool.** Full dump of all entries matching filters with no pagination. Capped at 10,000 entries per content type to prevent memory exhaustion. Filters: `tier` (default warm), `source_tool`, `tags`.

### Changed

- **package.json** — `hermes-plugin/` directory added to npm `files` array for inclusion in published tarball.
- **postinstall** — now calls `installHermesPlugin()` to auto-deploy the Hermes plugin when Hermes is detected on the system.

## 1.3.0 - 2026-05-31

### Added

- **`recent` — browse memories newest-first by timestamp.** New `memory_recent` MCP tool, `total-recall memory recent` CLI command, and `/total-recall:commands recent` skill subcommand. Lists memories merged across hot/warm/cold ordered by a selectable timestamp (`--order created|updated|accessed`, default `created`), with optional `--tier`, `--type` (entry type), `--project`, and `--limit` (default 20, max 200) filters. Compact-preview output; full text via `inspect`. Backed by a new `entry_type` filter on the storage `List` seam and a shared `RecentQuery` helper.

## 1.2.2 - 2026-05-13

### Fixed

- **`skill_get` now records invocations on every successful resolution and warms the local cache from remote fetches.** The recording call had been guarded inside the cache-hit-only branch, and the remote-fetch path never wrote the returned bundle back to `skill_cache`. Combined, this meant first fetches (or any fetch against an empty cache) were invisible to usage tracking AND the cache stayed empty forever — so `usage_count` had no row to accumulate against. Recording now fires whenever a bundle is resolved (cache OR remote) and remote bundles are written back via `ISkillCache.UpsertAsync` so subsequent calls hit cache.
- **Telemetry backlog now drains fully each flush instead of being capped at the per-type quota.** The per-type drain quotas introduced in 1.2.1 were an anti-starvation floor implemented as a ceiling — each flush drained at most `UsageDrainLimit=10` usage rows regardless of backlog. A real backlog (e.g. 300+ rows queued during a cortex outage) needed 30+ flushes to clear and ongoing inflow ≥10/flush could grow the queue indefinitely. `FlushAsync` now runs a Phase 2 catch-up loop per telemetry type after the fair-share Phase 1 drain, draining 100 rows at a time until empty (with a safety ceiling of 100 batches per type per flush) and bailing cleanly on `CortexUnreachableException`. Regression test: `FlushAsync_HeavyTelemetryBacklog_DrainsFully`.

### Added

- **Startup flush on `session_start`.** Drains any backlog that survived a prior process (crash, kill -9, network drop during `session_end`). Combined with the new Phase 2 catch-up, one cold start clears any reasonable backlog. Best-effort — `session_start` never fails because of a sync problem.
- **`status` MCP tool now returns a `syncBacklog` field.** Per-type unsynced counts (memory, usage, retrieval, compaction), `skill_usage_events` unsynced count, `retrying` count (rows that have failed at least once — proxy for cortex-reachability problems), and `oldestUnsyncedAt`. Powered by a new `SyncBacklogReader` in `Infrastructure.Sync`. Null when the handler doesn't have a SQLite connection (tests, cortex-only modes).

### Changed

- **Integration test defaults match the standard `total-recall-cortex` docker-compose stack.** Previously defaulted to `http://localhost:5000` with the nonsense PAT `tr_test`. New defaults: `http://localhost:5188` + the auto-seeded dev PAT from `TotalRecall.Cortex.Api/Program.cs:78`. `CORTEX_URL` / `CORTEX_PAT` env vars still override. CI is unaffected — `Category=Integration` is already excluded.

## 1.2.1 - 2026-05-13

### Fixed

- **Sync queue no longer starves telemetry under heavy memory churn.** `SyncQueue.Drain` returned the next 50 unsynced items in a single ordering pass with memory entries prioritized, so during a memory-heavy session all 50 slots filled with memory upserts and usage/retrieval/compaction events sat in the queue indefinitely. `SyncService.FlushAsync` now drains each entity type independently with explicit per-tick quotas (memory 25, usage 10, retrieval 10, compaction 5), so every type makes guaranteed progress on every flush. New `SyncQueue.Drain(string entityType, int limit)` overload backs this; the existing `Drain(int)` overload is preserved for inspection callers. Regression test: `FlushAsync_HeavyMemoryBacklog_StillPushesTelemetry`.

## 1.2.0 - 2026-05-12

### Added

- **Skills are now invokable locally without Cortex.** Previously the `skill_get` and `skill_search` MCP tools were only registered in cortex mode, and `NullSkillClient` silently returned empty results — so users without a configured cortex had no working skill subsystem. `SqliteSkillCache` is now the primary skill store. Scanners (Claude Code + `extra_dirs`) write directly to it with content + hash + a locally-computed embedding. `skill_get`/`skill_search`/`skill_list`/`skill_import_host` handlers are registered in sqlite mode and resolve from the cache; cortex remains a fallback when configured. Local search is hybrid (semantic + keyword) using the same `IEmbedder` the memory store already uses. Migration 12 adds `content`, `frontmatter_json`, `content_hash`, `content_embedding`, `embedder_fingerprint` columns to `skill_cache` plus a `(name, scope, scope_id)` natural-key unique index.

- **Skill usage tracking with decay-weighted ranking.** Each `skill_get` cache hit records an invocation event and bumps a usage counter on the cache row. `LocalSkillSearch` ranking now factors `0.10 * NormalizeDecay(usage_count, last_used_at)` alongside semantic + keyword — frequently-used skills win ties. 30-day half-life, log-saturated at 20 invocations. Migration 13 adds `usage_count`, `last_used_at`, `decay_score` columns plus a new `skill_usage_events` append-only table for telemetry.

- **Cortex add-on (Phase 3, optional).** When Cortex is configured, `SyncService.FlushAsync` drains unsynced `skill_usage_events` rows to a new `POST /api/plugin/sync/skill-usage` endpoint, marking them synced on success. `PullSkillsAsync` widens to user + team + global visible scopes, transports full content + frontmatter + hash + usage fields, and merges with `max(local, cortex)` semantics so multi-machine rollup never drops local edits. On the first round-trip a locally-issued skill GUID is rewritten to cortex's GUID in a single transaction (FK references in `skill_usage_events` are updated too). Cortex-side `SkillSearch` adds a parallel decay term so ranking is consistent on both sides of the wire.

### Fixed

- **MCP `skill_*` tools are now registered in sqlite mode.** They had previously been wired only in cortex mode. Combined with the new local cache this means agents always have a working `skill_search`/`skill_get` regardless of cortex availability.

## 1.1.5 - 2026-04-29

### Changed

- **Sync queue retries forever with exponential backoff instead of dropping items at 10 attempts.** Previously `SyncQueue.Drain` filtered out anything with `attempts >= 10` (a hard "poison pill" cap), so a single extended cortex outage would permanently strand any usage/memory/retrieval/compaction events queued during the window — they sat in `sync_queue` forever, never retried, never delivered. The cap has been replaced with a `next_attempt_at` backoff column (Migration 11). On each failure, the next eligible retry time is set to `now + min(60 * 2^(attempts-1), 3600)` seconds — so a stuck item retries at 60s, 120s, 240s, 480s, 16min, 32min, then every hour thereafter. Items are no longer dropped; they keep retrying until cortex is reachable again. `PendingCount()` now reflects all not-yet-completed items (including those in a backoff window) rather than only "eligible-now" items.

## 1.1.4 - 2026-04-29

### Added

- **Embedder fingerprint guard (#6, PR #7).** Each store now stamps an embedder descriptor (provider + model + dimensions) into a `_meta` row on first write and verifies it on open. Switching the configured embedder against an existing DB without a deliberate migration now fails fast with a clear error instead of silently producing dimension-mismatched vectors. New `EmbedderDescriptor` / `EmbedderFingerprint` types and an `IMetaStore` interface back this; SQLite and Postgres stores both implement it. Bedrock, ONNX, and OpenAI embedders all advertise their descriptor.

### Changed

- **`session_start.lastSessionAge` now reflects real session activity.** Previously this value was sourced from `compaction_log.GetLastTimestampExcludingReason("warm_sweep_decay")`, which only updates when an entry actually moves tiers (hot→warm decay, manual promote/demote). On installs where hot entries stay above the warm threshold and no manual movements occur, the timestamp would freeze and the startup banner would report a stale "X days ago" even when sessions ran daily. `SessionLifecycle` now prefers `MAX(ts)` from `usage_events` (populated on every `session_start` by the usage indexer for Claude Code and Copilot CLI) and falls back to the compaction-log query only when no `UsageQueryService` is wired (pure-Postgres composition path). New `UsageQueryService.GetLastEventTimestampMs()` method backs this. No schema changes.

## 1.1.3 - 2026-04-22

### Fixed

- **Skill frontmatter parsing no longer crashes under NativeAOT.** `FrontmatterParser` used YamlDotNet with `Dictionary<string, object?>` deserialization, which relies on reflection that is trimmed away in NativeAOT builds. The suppression attribute silenced the AOT warning at compile time but the code still failed at runtime with "Exception during deserialization" for every skill. Replaced YamlDotNet with a hand-rolled, reflection-free key:value parser. YamlDotNet dependency removed. Handles flat key:value, single-quoted, double-quoted, and YAML block scalar formats used by skill frontmatter.

## 1.1.2 - 2026-04-21

### Fixed

- **`[skills]` config section now works as documented.** The README documented the skill scan directory as `[skills]` (plural), but the config parser only recognised `[skill]` (singular), silently ignoring `extra_dirs` for any user who followed the docs. The parser now accepts both forms. Added a regression test to prevent recurrence.

## 1.1.1 - 2026-04-21

### Fixed

- **`extra_dirs` skill scan now works without Cortex.** Skills from `extra_dirs` were previously only advertised via the Cortex list response. In SQLite mode (or when Cortex is unavailable), `ScanExtraDirsAsync` now runs locally first and the results are merged into `BuildSkillsBlock` directly — so the `## Available Skills` block appears regardless of Cortex connectivity.

## 1.1.0 - 2026-04-21

### Added

- **`extra_dirs` config for skill scanning.** A new `[skill]` section in `~/.total-recall/config.toml` accepts an `extra_dirs` array of directories to scan for skills on every `session_start`. This lets Cursor users (and anyone who keeps skills outside `~/.claude/skills/`) point total-recall at their custom skill directories: `extra_dirs = ["~/my-skills"]`. Paths use `~/` tilde-slash syntax; each directory is scanned with the same bundle-aware logic as the Claude Code scanner. Skills from extra dirs are imported to Cortex with user scope alongside the standard Claude Code skills.
- **Available skills surfaced in `session_start` context.** After skill import, all skills visible to the current user are fetched and appended to the session context as an `## Available Skills` block (`- name: description` per skill). The agent no longer needs a separate tool call to discover what skills exist at startup.

## 1.0.13 - 2026-04-20

### Fixed

- **Windows MCP stdio no longer garbles non-ASCII characters.** `RunServeAsync` now sets `Console.InputEncoding` and `Console.OutputEncoding` to `Encoding.UTF8` before constructing `McpServer`. On Windows the default OEM code page (CP437/850) was applied to the JSON-RPC stream, corrupting em dashes, arrows, curly quotes, and other Unicode above U+007F in memory content before `JsonSerializer` ever saw them.
- **`memory_store` with duplicate content no longer throws a vec constraint error.** Three complementary fixes: (1) `SqliteStore`'s production constructor now calls `MigrationRunner.CleanupOrphanRows` on every startup (previously only ran once during Migration 5), clearing orphan vec rows left by historical eviction bugs before they collide on the next insert. (2) `InsertWithEmbedding` defensively deletes any orphan vec row at the about-to-be-used rowid inside the insert transaction — a no-op when the DB is clean, self-healing otherwise. (3) `MemoryStoreHandler` checks `IStore.FindByContent` before embedding; identical content in the same tier returns the existing id without re-inserting.
- **AOT crash in `CodeChunkKindToString` (ingestion path).** `FileIngester` called `.ToString()` on an F# discriminated union value (`CodeChunkKind`). Under AOT trimming, F# DU `ToString()` invokes `StructuredPrintfImpl` reflection which is stripped at publish time, causing a `KeyNotFoundException` during code-chunk ingestion. Fixed by replacing the `ToString()` call with an explicit match expression.

## 1.0.12 - 2026-04-21

### Fixed

- **Memory sync to cortex now works end-to-end.** `SyncPayload.Upsert` called `entry.EntryType.ToString()` on an F# discriminated union. In the AOT-compiled binary, F# DU `ToString()` invokes `StructuredPrintfImpl` reflection which is trimmed away, causing a `KeyNotFoundException` at runtime. This exception was silently caught by `RoutingStore.EnqueueUpsert`'s best-effort handler, so every `memory_store` call enqueued nothing and cortex never received new memories. Fixed by replacing the `ToString()` call with `EntryTypeMapping.ToDbValue(entry.EntryType)`, which uses explicit `IsXxx` property checks and is safe under AOT trimming.

## 1.0.11 - 2026-04-20

### Fixed

- **Newly stored memories are now reliably synced to cortex.** `SqliteStore` previously enforced the hot-tier cap via write-time eviction (`EvictHotIfOverLimit`), which called `SqliteStore.Move` directly and bypassed `RoutingStore`. This caused two silent failures: evicted entries were never enqueued for cortex sync, and when the freshly inserted entry itself had the lowest decay score it was immediately moved out of hot, causing `RoutingStore.EnqueueUpsert` to find it gone and bail without queuing it either. Write-time eviction has been removed; hot-tier cap enforcement is now owned exclusively by the session_start warm sweep, which runs through `RoutingStore.Move` and enqueues each moved entry correctly.

## 1.0.10 - 2026-04-20

### Fixed

- **Warm sweep now runs synchronously before hot-entry counting in `session_start`.** Previously `RunBackgroundWarmSweep` was dispatched via `Task.Run` *after* assembling context, so the reported hot count always reflected the pre-sweep state and the sweep raced with `PeriodicSync` on the shared SQLite connection — causing it to silently crash and never demote entries. The sweep now runs inline within the `_initLock` section before hot entries are listed, eliminating the thread-race and ensuring the post-sweep count is what the host sees.
- **`session_end` now performs real heuristic compaction.** Previously the handler returned zeroed stub counts. It now recalculates a fresh decay score for every hot-memory entry (using the `Decay.fs` formula), persists the updated score via `IStore.Update`, and promotes any entry whose score falls below `tiers.hot.carry_forward_threshold` (default 0.3) to the warm tier via `IStore.Move`. Each movement is recorded in `CompactionLog` with reason `session_end_decay`. When no store is injected (legacy or test construction) the stub behaviour is preserved.

## 1.0.9 - 2026-04-20

### Fixed

- **`session_start` no longer blocks for ~40s on cortex mode cold start.** `PeriodicSync.Start()` now fires the first tick immediately (`dueTime = TimeSpan.Zero`) instead of waiting a full sync interval, so the periodic sync path handles cortex pull/flush without blocking session init. The two awaited `PullAsync` + `FlushAsync` calls have been removed from `SessionStartHandler` entirely.
- **`session_start` hot context is now bounded to the documented 4,000-token budget.** `BuildContext` sorts hot entries by `decay_score` descending and stops accumulating once the next entry would exceed `tokenBudget × 4` characters (≈4 chars/token). A `hotContextTruncated` flag in the response indicates when entries were dropped. The `token_budget` value from config (`tiers.hot.token_budget`) is now wired through `ServerComposition` to all storage paths; previously the config field was serialized and advertised but never enforced.
- **Hot tier is now bounded and self-healing.** Two complementary mechanisms cap the hot tier at `tiers.hot.max_entries` (default 50): a background warm sweep fires after `session_start` assembles context to demote the lowest-`decay_score` entries from hot to warm (drains existing backlogs), and write-time eviction in `SqliteStore`/`PostgresStore` moves the lowest-scoring entry to warm after each hot insert that would exceed the limit. The `max_entries` config value is now wired to all construction sites; previously it was serialized but never enforced.

## 1.0.8 - 2026-04-20

### Fixed

- **Skill sync no longer fails with "JWT missing payload segment".** Removed client-side JWT parsing (`JwtCurrentUserId`) that tried to extract the user `sub` claim from the PAT token. Cortex now stamps the authenticated user's identity at ingestion via the Bearer token, so `suggestedScopeId` is left empty by the scanner and filled server-side. This fixes skill import failures when the PAT is not a well-formed JWT.

## 1.0.7 - 2026-04-20

### Fixed

- **`opencode plugin @strvmarv/total-recall` no longer fails with "No plugin targets found".** Added `"main": "bin/start.js"` to `package.json` so OpenCode's plugin installer can detect the MCP server entrypoint. All other integrations (Claude Code, Copilot CLI, Cursor) are unaffected.

## 1.0.6 - 2026-04-20

### Fixed

- **`skill_get` no longer rejected by OpenCode and strict MCP clients.** The input schema used a top-level `oneOf` to express the id-vs-natural-key mutual exclusivity, which violates the MCP schema constraint that disallows `oneOf`/`anyOf`/`allOf`/`enum`/`not` at the root level. The handler already validates the combination at runtime, so the schema was simplified to plain optional properties with descriptive text.

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
