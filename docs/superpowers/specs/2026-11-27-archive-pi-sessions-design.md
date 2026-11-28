# Archive pi sessions and tell pi's agent what compaction removed (Part 7 of the transcript archive): design

Parent: #145, Part 7 (docs/superpowers/specs/2026-11-18-transcript-archive-design.md)

Date: 2026-11-27. Status: draft for review. This is the sub-spec of Part 7 of the transcript archive. It follows the parent's
"Shared decisions" (above all decisions 2, 3, 4, 8, 9, 11, 12 and 13) and adds the detail that belongs to this part. Parts 1 to 6 are
merged: the store and `forget` (1), `transcripts backfill` with the Claude Code adapter (2), `transcript_search` (3),
`transcript_get` and `transcript_sessions` (4), the live marker, sync-on-read and the defaults from the current session (5), and the
compaction breadcrumb for Claude Code (6).

## Goal

pi sessions are archived and searched exactly like Claude Code sessions, and pi's agent is told once per compaction what happened:

- A **pi adapter** reads pi's session files (`<sessions>/<encoded cwd>/<timestamp>_<session id>.jsonl`) into the common record, beside
  the Claude Code adapter. `transcripts backfill`, the `session_start` pass, `transcripts status`, the sync-on-read and the three
  tools then handle pi sessions without further change.
- A pi session is `interactive` or `automated` from its file alone: a child built by pi-subagents is `automated`, a `/fork` of a real
  conversation stays `interactive` (parent decision 4).
- The **pi extension writes the live marker** for the user's session (never for an in-process child), so `transcript_search` finds
  text added to a running pi session and defaults from it (parent decision 12).
- Where there is **no marker** and the host is pi, `transcript_search` defaults to the project of the engine's working directory
  (parent decision 9), which for pi is the user's directory.
- pi's existing after-compaction message carries **the same breadcrumb** as Claude Code's, once per compaction, only while the
  archive is on (parent decision 11).

Success is the parent's "Done when": a fixture of pi sessions (with a compaction, an error, a subagent child and a fork of a real
conversation) backfills and is searchable like the Claude Code fixture, the child as `automated` and the fork as `interactive`; text
added to a running pi session is found by search; with a user session and an in-process child both live, only the user's session has a
marker and search from either resolves to it; after a pi compaction the next turn carries the breadcrumb once.

## What was observed

Observed on pi 1.6.1 (Linux), with a throwaway extension and a throwaway MCP server in a temporary directory, with `--session-dir`
pointing at a temporary directory (the author's real sessions, `~/.pi` and `~/.total-recall` were not written by the spike), and in the
author's own pi history (821 session files at the last count, read only; the counts below are from that survey). The spike's scripts are not committed.

| What | Observed |
|---|---|
| The extension's process and the MCP server | the extension's `process.pid` is the **direct parent** of an MCP server it registers (chain: server, then `pi`, then `timeout`/`bash`/the outer shell). The engine's chain under the plugin is engine, shim (`node`), the pi process, so the extension's pid is an ancestor of the engine, as the marker design needs |
| What an extension can read | at `session_start` and `before_agent_start`: `ctx.sessionManager.getSessionId()` (the uuid), `getSessionFile()` (`<dir>/<timestamp>_<uuid>.jsonl`), `getHeader()` (`{type: "session", version: 3, id, timestamp, cwd}`; a root session has no `parentSession`) and `ctx.cwd` |
| Compaction | `-p` (print mode) does not run `/compact` (the model answers that it is a harness command); in `--mode rpc` the `compact` command ran: `session_compact` fired (`reason: "manual"`), the file got a `compaction` entry, and a hidden message returned from the **next** `before_agent_start` was stored **once**, as a `custom_message` with `display: false`, after the user message of that turn. A session too small to compact answers "Nothing to compact"; `ctx.compact()` at `agent_end` in `-p` is cancelled by the shutdown |
| The author's history | 802 of 821 files have a `parentSession` (pi-subagents children, and forks); 792 of them hold **one** user turn, 9 hold **two** (a task prompt and one follow-up), 1 holds more; the 19 files without a parent are the author's own conversations (1 to many turns). Entry types seen: `message` (roles `user`, `assistant`, `toolResult`, `system`), `compaction` (28), `custom_message` (`total-recall-context` hidden, `subagent-notification`), `custom`, `usage`, `model_change`, `thinking_level_change`, `session_info`. Tool calls are blocks `{type: "toolCall", name, arguments}` in assistant messages; the names seen are `bash`, `read`, `edit`, `write`, `Agent`, `codemode`, `grep` and others |
| The file | an append-only tree: entries carry `id` and `parentId`; `/tree` navigation adds entries on another branch of the same file, so file order is not one conversation |

Not observed: macOS and Windows, a real `/fork` file and a real `branch_summary` entry (their shapes are from pi's documentation,
`docs/session-format.md`), a `bashExecution` message (documentation only), pi started with `--session-dir` or the `sessionDir`
setting pointing elsewhere, and an automatic (threshold or overflow) compaction.

## Calls made here (the person can overrule them at sign-off)

1. **The session id is pi's own id.** `pi:<uuid>`, the id pi shows, not the whole file name. The adapter parses it out of
   `<timestamp>_<id>.jsonl`, so the marker, the `session` argument and the results all say `pi:<uuid>`.
2. **A pi session is `automated` when it is a child: its header names a `parentSession`, it carries no copied history, and it holds at
   most two real user turns.** The parent says "at most one"; in the author's history that would file 9 of 801 children as
   `interactive` (a child that got a task prompt and one follow-up). `/fork` and `/clone` copy the entries of the parent's branch into
   the new file, with their **old timestamps**, so a session whose first entry after the header is older than the header's own
   `timestamp` is a fork or clone, never a child, whatever its turn count (checked on the author's files: the one real fork has 119
   copied entries, all older than its header; none of the 801 other files with a parent has one). Every real pi file starts with a setup entry (`model_change` or `thinking_level_change`), and a fork at any message copies at least
   that, so a `/fork` or `/clone` always has copied history. The recorded limits: a **real conversation created with
   `newSession({parentSession})`** (the handoff example, RPC `new_session`) names a parent, copies nothing and is `automated` until it
   passes two turns; and a pi-subagents child **reopened after the user forked** (pi-subagents reopens it with pi's `forkFrom`, which
   writes a header naming the old child file and copies its entries) has copied history, so it is `interactive`, and the extension,
   which no longer lists the old child as live, may write a marker for it until the user's next turn rewrites it. No pi session is marked
   `automated` by its wording: the long-prompt and opening-phrase rules of the Claude Code rule do not apply.
3. **The whole file is archived in file order, abandoned branches included.** The archive is a record of what was said, and the text
   of a branch the user left is still something said. A `branch_summary` entry is kept as a compaction summary (it shares the `compaction-summary` chunk type and its ranking).
   `context_edit` entries change only what the model sees and are ignored: the text they hide stays archived. A fork or clone **carries a
   copy of its parent's entries, and the copy is archived under the fork's own session** (it is the fork's record; no entry ids are
   compared across files): a search can find the same words in both sessions (the two-chunks-per-session limit and the ranking are
   unchanged), and forgetting the parent leaves its text searchable through the fork. This is documented in `docs/transcripts.md` and
   in the `forget` text, and the `fork` fixture's parent is part of the test corpus.
4. **Where pi's sessions are, and their layout.** `PI_CODING_AGENT_SESSION_DIR` (pi writes sessions **flat** in it:
   `<dir>/<timestamp>_<id>.jsonl`), else `PI_CODING_AGENT_DIR/sessions`, else `<home>/.pi/agent/sessions` (both nest one folder per
   working directory: `<root>/--<encoded cwd>--/<file>`). A leading `~` or `~/` is expanded against the home directory as pi does;
   any other relative value, or no home, means pi is not detected. The adapter exposes the layout as `SessionPathDepth` (1 flat, 2
   nested), used by the enumeration and by the finder's path rule. The `--session-dir` flag and the `sessionDir` setting are not
   visible to the engine: sessions stored there are not archived and a marker for them is refused (a recorded limit), as are files
   opened with `pi --session <any path>` whose name does not fit section 1. When `PI_CODING_AGENT_SESSION_DIR` is set, the earlier
   sessions under `<agent dir>/sessions` are not enumerated either (the environment directory is the only root): also a recorded limit.
5. **The marker is written by the extension, with the code of the Claude Code writer.** `hooks/live-marker/write-marker.js` takes the
   host as a parameter (default `claude-code`), and the extension calls its `buildMarker`, `writeMarker` and `prune` for host `pi`
   with `process.pid`, so there is one writer and one set of rules (shape, private modes, atomic write, pruning).
6. **One note, said in the same words.** The breadcrumb text is the one printed by `hooks/compaction-breadcrumb/run.sh`; the
   extension holds the same text and a test fails if the two differ.
7. **The host reaches the transcript tools from `session_start`.** The engine already resolves the host there (`TOTAL_RECALL_HOST`,
   else the client name the shim passes). It is kept in one process-wide value that the sync reads, so the default project from the
   working directory applies to pi, including a hand-configured `mcp.json` entry that sets no `TOTAL_RECALL_HOST`. Before any
   `session_start` the host is `TOTAL_RECALL_HOST` or unknown, and unknown means no default project.
8. **The working directory is a project only inside a git repository.** Parent decision 9 says the engine's working directory; a
   directory with no repository would give a project key that is only a folder name and searches nothing. There the default stays
   `all`, as it is with no information.
9. **`transcripts backfill` without `--host` archives every host that is detected**, and `--host pi` or `--host claude-code` limits it.
   Part 2 made `claude-code` the default because it was the only host.
10. **The guess spans hosts.** Part 5's guess is the newest session file of any detected host written within 30 minutes. It only
    decides what is synced and never chooses a scope, so a pi-subagents child being the newest file costs nothing but a sync of it.
11. **No marker on Windows**, as for Claude Code: the engine cannot walk its ancestors there, so a marker would never be read.

## 1. The pi adapter

`PiAdapter` implements `ITranscriptAdapter` (`Host` is `pi`) beside `ClaudeCodeAdapter`, registered in `TranscriptAdapters.All()`.
It reads and never writes.

**Enumeration.** `<root>/<project dir>/<file>.jsonl` (nested layout) or `<root>/<file>.jsonl` (flat layout, call 4), links skipped, as for Claude Code. The file name is
`<timestamp>_<id>.jsonl`: the timestamp is `YYYY-MM-DDTHH-MM-SS-mmmZ` (24 characters), then `_`, then the id, which is 1 to 128
characters of `A-Z a-z 0-9 . _ -`. A name that does not fit is counted in `Skipped`. The `HostSessionId` is the id (call 1); the
header's own `id` is not compared, the name decides. Two files with the same id (`pi --session-id work` in two directories, an
imported file) cannot both be archived: one is counted as skipped, as for Claude Code, and this is a recorded limit; an id over 128
characters is skipped the same way. The `SessionSource` path, size and mtime are as for Claude Code.

**Head.** The first complete line must be a `{"type": "session", "version": 3, ...}` header; its SHA-256 is the fingerprint, `cwd` and
`timestamp` come from it, and two new members are added to `SessionHead`: `ParentSession` (a non-empty string `parentSession`) and
`CopiedHistory` (true when the next complete line is an entry whose `timestamp` is earlier than the header's: call 2; the head reads
the first two lines, the second only for this). A first line that is not a version 3 session header, or is over the line limit, gives
no fingerprint, so the session is skipped and counted, as for Claude Code. Files of versions 1 and 2 are skipped the same way: pi
rewrites such a file in place, header and entries, when it opens it, which would change its fingerprint and shift every offset after
it was archived (the author's files are all version 3). When the header has no usable `cwd`, the project is unknown: the pi adapter
has no project-folder-name fallback, because the `--<path>--` folder name does not decode safely: the adapter gets a `UsesProjectFolderFallback` member (false for pi) that `TranscriptIngester` honours when it derives the project (no key, so the session has no project).

**Records.** `IsLineStart` and the byte-exact line reading are the same machinery as for Claude Code (`JsonlLineReader`); a line that
is not JSON, is too long, or nests deeper than the limit counts as skipped; a blank line is not counted. `Ts` is the entry's ISO
`timestamp` on every record (the cutoff of `forget --all` needs it).

## 2. What is kept from a pi file

Each line is one entry; `PiRecordMapper` turns it into records (parent decision 3: kept and dropped are the same rules, in pi's words).

| Entry | Result |
|---|---|
| `message`, role `user` | `UserText`: the string, or the `text` blocks joined with a blank line; image blocks are ignored; an empty text is nothing |
| `message`, role `assistant` | `AssistantText` for the `text` blocks (joined); a `ToolStub` per `toolCall` block; `thinking` blocks dropped. A message that stopped on `error` or `aborted` is kept as it is |
| `message`, role `toolResult` | `isError: true`: `ToolError` with the text blocks (cut to 500 characters by the builder); otherwise dropped |
| `message`, role `bashExecution` | a `ToolStub` named `bash` with the `command`; the output is dropped (documentation only, not observed) |
| `message`, role `system` and any other role | nothing |
| `compaction` | `CompactionBoundary`, then `CompactionSummary` with the `summary` |
| `branch_summary` | `CompactionSummary` with the `summary` (call 3) |
| `custom_message` (every `customType`: the hidden `total-recall-context`, `subagent-notification` and any other extension's text) | nothing: it is injected by an extension, not said by the user or the model |
| `session`, `usage`, `custom`, `model_change`, `thinking_level_change`, `session_info`, `label`, `context_edit`, and any unknown type | nothing, and not counted as skipped |

**The stub** is `[<name>: <detail>]` as the builder renders it: the tool's name, and the detail is the first of these arguments that
is a string, in this order: `command`, `path`, `file_path`, `pattern`, `url`, `query`, `description`, `question`, then the first string
property of the arguments; none gives no detail. The builder cuts it to 160 characters after redaction, as for Claude Code.

## 3. Session kind

`SessionKindRule.Decide` takes one more input, `isChild`, a nullable boolean the adapter fills from the head: **null** for a host that
has no such signal (Claude Code: its rule is unchanged, including the wording rules), **true** when the header names a `parentSession`
and the head has no `CopiedHistory`, **false** otherwise (pi). For pi the result is a function of that flag and the number of real user
turns alone: a child with **at most 2** turns is `automated`; every other pi session, including a fork or clone of any size, a root
session and a child with more turns, is `interactive`. The turn count is the sum of the settled and the current pass's real user turns,
so a child that grows from 2 turns to 3 flips to `interactive` through the existing rewrite path (a changed kind re-reads the file from
the start). The kind is stored and can be re-evaluated, as the parent says.

## 4. The current session on pi

**Engine side.** `LiveSessionFinder` looks for `<live>/pi-<pid>.json` for each ancestor as it does for `claude-code`; the marker's
`host` must be `pi`. Two things in it are Claude-specific and are moved to the adapter: the session id check
(`ClaudeCodeAdapter.IsValidId`) becomes `ITranscriptAdapter.IsValidSessionId`, and "the file is named `<id>.jsonl`" becomes
`ITranscriptAdapter.IsSessionFileName(fileName, id)` (pi: the name is `<timestamp>_<id>.jsonl` with the timestamp of section 1). Every
other refusal stays (size, `v`, `host_pid` equal to the name, an absolute `cwd`, a path with no `.` or `..` under the adapter's root
with exactly `SessionPathDepth` components below it, neither a link (the check covers the components below the root; the root itself is resolved as for Claude Code), a process start check). `TranscriptSyncService`, `ArchivePass` and
`TranscriptBackfill` already work per adapter and need no change for this.

**Extension side.** A new module of the pi package writes the marker for the user's session:

- **When.** At `session_start` (every reason, so `/new`, `/resume` and `/fork` rewrite it for the new session), `before_agent_start`,
  `tool_call` and `turn_end`, only when the extension's own `hookable` rule holds (so never for a child, for a `PI_SUBAGENT_ID`
  process or for a session with no session file) and the platform is not Windows. The marker names a session only **once its file
  exists on disk**: pi creates the file when the first message is persisted, and in pi's order `before_agent_start` and `turn_start`
  run before that, so a marker written earlier would be refused by the finder ("the session file does not exist"). `tool_call`
  runs after the assistant message that makes the call, which is after the file exists, and before the tool runs, so the marker is
  there for the first transcript tool call of a new session. When the session file does not exist yet, `session_start` and
  `before_agent_start` **remove this process's marker** (`remove` is added to the shared writer), so that after `/new` the previous
  session's marker is never read for the new one; the finder then falls back to the guess and the working-directory default. The finder walks every ancestor across both hosts: a pi started
  under a Claude Code process that has a live `claude-code-<pid>.json` (for example from Claude's shell tool) and has no pi marker of its own
  resolves to that Claude Code session (a recorded limit, in `docs/hosts/pi.md`). The marker's `session_id` is the id parsed from the session file's **name** with the rule of section 1 (so a re-imported file
  `<timestamp>_<id>-1.jsonl` is named `<id>-1`, as the adapter archives it; a name that does not parse writes no marker). A call
  that finds the marker file on disk with every field equal except `at`, and an `at` not earlier than this process's start, skips the
  write; any other state, including a leftover file of a crashed process whose pid was reused, is rewritten (the comparison is with
  the file, never with a memory of the last write). At `session_start` it also prunes the markers of ended pi processes, as the Claude
  Code hook does at `SessionStart`. The builder checks the event order against pi's code and the manual check observes that the marker
  exists before the first transcript tool call.
- **Which archive.** The data directory is resolved as the engine does (`resolveDataDir` over the environment and, when pi follows a
  hand-configured `total-recall` server, that server's `TOTAL_RECALL_*` values; a server that cannot be followed means no marker). With
  the archive off (no `<data directory>/live/enabled`) the module writes nothing.
- **What.** `<data directory>/live/pi-<process.pid>.json` with `v`, `host` `pi`, `host_pid`, `session_id` (pi's id), `transcript_path`
  (the session file), `cwd` (`ctx.cwd`) and `at`, through the shared writer (call 5): private modes, atomic rename.
- **Never a failure.** Every step is inside a try, writes nothing to stdout or stderr, and takes no await: the marker is a small file
  written synchronously.
- **Children.** (Recorded limit: with `pi --no-session` the user's session has no file, a pi-subagents child is saved with no
  `parentSession`, is not recognised as a child by the extension, and may write a marker and be archived `interactive`.) pi-subagents builds a child in the same process, so it would share the pid and overwrite the user's marker; it is
  never written for. A child's own tool calls therefore resolve to the **user's** session through the same pid (parent decision 12,
  accepted: children do not search the archive).

**The breadcrumb.** `session_compact` already queues the instructions for the next prompt. It now appends the note when the archive is
on (the same sentinel test as the marker), so the hidden message of the next `before_agent_start` carries both, once. The note is
owed from a compaction until a `total-recall-context` message follows it, so there is **one rule wherever the queue is filled** (every
`session_start` reason including `/resume`, `session_tree`, `session_compact`): append the note when the archive is on and the active
branch's last `compaction` entry has no `total-recall-context` message after it (stateless, read from the session). A failed or cancelled compaction fires no `session_compact`
and adds nothing. An automatic compaction inside a run queues the note, which reaches the model with the next user prompt (as the
existing instructions do), not the next model turn within the run; `docs/hosts/pi.md` says so. The note is the text of the Claude Code hook (call 6), and `total-recall-context` messages are dropped by the
adapter, so the note is never archived.

## 5. The default project from the working directory

When no marker is found and the host (call 7) is `pi`, the sync derives the project of the engine's working directory
(`ProjectResolver`, as the ingest does) and, if it lies inside a git repository (call 8), returns it as the project of the current
call without naming a session: `currentSession` stays `guessed` or `none`. `SyncResult.ProjectKey` is then set for this case too (today only for a marker), and `TranscriptSearchService.Plan` uses it whenever it is set, not only when `Current.Kind` is `marker`; `transcript_search` then defaults, with no `scope`, to that
project (`scopeNote` is the same `defaulted to the current project (<key>)`); `scope: "project"` without `project` uses it; `scope:
"session"` without `session` is still the argument error of Part 5, because only a marker names the session. An explicit argument
always wins. Under Claude Code, where the working directory is the plugin directory, the host is `claude-code` and nothing changes.

## 6. `backfill`, `status`, the pass and the texts

- `transcripts backfill` runs the adapters that detect their directory, one report per host (the counts of Part 2, per host);
  `--host claude-code|pi` limits it; any other value is a usage error naming both hosts. No sessions found for a host says so for that host.
- The `session_start` pass and the sync already loop over the adapters.
- `transcripts status` adds the number of sessions and chunks per host to its existing counts by project and kind.
- The confirmation text of `transcripts enable` and the text printed after it name both hosts ("your Claude Code and pi sessions").
- The kind and the project are stored per session as before; `transcripts rescan` and `reproject` (Part 8) will work for pi through
  the adapter, with no pi-specific code.

## 7. Fixtures and tests

**Fixtures** (`tests/fixtures/transcripts/pi/`, synthetic, written for the tests, with the placeholders of the Claude Code fixtures):
`compaction` (a compaction entry with a unique summary word), `error` (a failing `toolResult`), `child` (a `parentSession` and one user
turn), `child-followup` (a parent and two turns), `fork` (a parent and four turns), `branch` (an abandoned branch and a
`branch_summary`), `injected` (hidden and visible `custom_message` entries, `thinking`, a `system` message, `usage`, `custom`,
`model_change`, a `bashExecution`) and `secrets` (a planted secret of each detector shape in user text, a stub and an error). A
`PiFixtureHome` in `Support/` places them as `<root>/--<encoded cwd>--/<timestamp>_<id>.jsonl` (public API only, compiled into the
Server and Cli test projects like `ClaudeFixtureHome`).

**Tests.**

1. `PiAdapterTests`: root resolution (each variable, `~` and `~/` expansion, other relative and empty cases), both layouts (a flat
   `PI_CODING_AGENT_SESSION_DIR` directory is enumerated and its markers accepted; the nested default too), enumeration (names, ids,
   skipped names, links, an empty file), head (fingerprint, cwd, parent, copied history, a version 1 or 2 file skipped and counted, a
   first line that is not a header, a header with no `cwd` gives a head with no cwd; and at ingest the session then has no project and no folder-name key), line starts, bad lines.
2. `PiRecordMapperTests`: every row of section 2 on a line written for it, the stub detail order, the 500-character error cut after
   redaction, string and array user content, an unknown entry type.
3. Kind: `SessionKindRule` with `isChild` (null keeps every Claude Code case as before; true with 1, 2, 3 turns; false with a long
   first prompt stays `interactive`); an ingest where a child grows from 2 to 3 turns and flips; a fork made after one turn (one copied
   user turn plus the edited one), a clone, and a fork at the first message (only setup entries copied) stay `interactive`; a session
   that names a parent and copies nothing (the `newSession({parentSession})` limit) is `automated`; a copy of a child file reopened by
   pi-subagents (copied history) is `interactive` (the limit, pinned so a change shows).
4. The corpus: `backfill` of the pi fixtures stores the sessions and chunks per project and kind (the child and the follow-up
   `automated`, the fork, its parent and the early fork `interactive`; the copied text is stored under the fork), a second run adds nothing, injected text and hidden messages are not stored, the branch's
   abandoned text and the branch summary are, planted secrets are stored only as markers, a deleted source keeps its chunks, forgotten
   sessions stay out (also in the session-start pass and the sync of the current session, not only in backfill).
5. Search: topic, a question answered only by a compaction summary, an exact error string and a session-scope paraphrase find the right
   pi session in the top three; the child is found only with `include_automated`.
6. The finder with host `pi`: a good marker, and each refusal of Part 5 on a pi path (a path outside the root, a link, a name that is
   not `<timestamp>_<id>.jsonl`, a `host` of `claude-code` in a `pi-<pid>.json`); the shared id and name rules for both adapters.
7. The sync on pi: text added to a running pi session is found with no pass (the Part 5 test on a pi session with a fake host
   process, in both layouts); a project default from the working directory reaches `Plan` while `currentSession` stays `guessed`; the project default from the working directory (inside a repository, outside one, the host unknown, the host `claude-code`,
   an explicit argument); the guess across both hosts.
8. The extension (`pi/*.test.js`, in `test:shim`): the marker is written at `session_start` and at each turn once the session file exists
   (and not before: the events are driven in pi's real order, and the marker exists before the first `tool_call` of a new session), removed at
   `session_start` of a new session with no file so that no marker names the previous session after `/new`, with mode 0600 in a 0700 directory, rewritten for a new session id, absent for a child, for `PI_SUBAGENT_ID`, for a
   session with no file, with the archive off, on Windows, with an unfollowable server; two
   extension instances in one process (a user session and a child) leave one marker, naming the user's session; the breadcrumb is in
   the hidden message after `session_compact` once; added again by `session_tree` and by a reload when no context message has followed the last compaction, and not when one has; absent after a failed compaction and with the archive off; the breadcrumb text
   equals the text `hooks/compaction-breadcrumb/run.sh` prints; a marker with the same fields and an `at` from before the process started is rewritten, and one with a later `at` is left untouched (same mtime); a foreign marker is overwritten at the next turn; a failing step never throws into pi.
9. The commands: `transcripts backfill` with no `--host` archives every detected host (one report per host, a host with no sessions
   says so), `--host pi` and `--host claude-code` limit it, any other host is a usage error naming both; `transcripts status` counts
   sessions and chunks per host; the `enable` text names both hosts. The existing CLI tests pass an adapter factory that ignores the host: the command calls the factory once per host, and a test fails if one host's fixture is archived under two reports.
10. Hermetic: `ArchiveHome` and every other helper that isolates `HOME` and `CLAUDE_CONFIG_DIR` for a composition or pass test also set
    `PI_CODING_AGENT_DIR` and `PI_CODING_AGENT_SESSION_DIR` to their temporary directory; `HermeticSync.Options` stays as it is (it
    names no adapters and reads no environment). `TranscriptToolHermeticGuardTests` gains a source scan that fails where a test file sets
    `CLAUDE_CONFIG_DIR` without `PI_CODING_AGENT_DIR`, so no test reads the developer's real pi sessions.
11. Speed: the Part 5 and Part 3 checks pass unchanged with two adapters registered; the cost of enumerating a large pi directory is
   measured once and recorded.
12. A manual check on a real pi (Linux), recorded in the PR and the docs: the extension loaded with `-e`, a temporary data directory
    where `transcripts enable` was run, the freshly built engine given to pi through a project `mcp.json` entry named `total-recall`
    (which takes precedence over the extension's own registration, so no engine is downloaded; the run uses `--approve` for project
    trust and never `/trust`, which writes `~/.pi/agent/trust.json`), only the worktree's extension loaded and pi's built-in MCP support kept (`--no-extensions` also disables the built-in `mcp`, `codemode`
    and `tool-search` extensions, so the run adds `-e builtin:mcp` and the built-in matching the entry's exposure, `-e builtin:codemode` by
    default, or sets `"exposure": "direct"` in the temporary entry; the builder confirms the flags against pi 1.6.1), a throwaway working directory and
    `PI_CODING_AGENT_SESSION_DIR` itself set to a temporary directory (the flat layout): the marker appears
    named by the pi pid, text added to the running session is found, and after `compact` over RPC the next turn carries the note once.
    What is not run is recorded as not observed.

## 8. Documentation and what goes stale

`docs/hosts/pi.md` (what the extension now writes and says, the child limit, the session directory limit, the observations above),
`docs/transcripts.md` (every statement that the archive reads only Claude Code: the intro, Privacy, Enabling, backfill and its
`--host`, the marker section, the guess, the breadcrumb section, Known limits), `PRIVACY.md` (the pi session files read, the `pi-<pid>`
markers), `README.md` (the table row), `docs/configuration.md` (the `enabled` sentence), `AGENTS.md` and
`src/TotalRecall.Infrastructure/AGENTS.md` (the where-to-look rows, the adapter list, the guess, the finder), `CHANGELOG.md`, the
`TranscriptAdapters` summary, the `BackfillCommand` usage and its error texts, and the `EnableCommand` texts. After the edits, a grep for
"only Claude Code", "Claude Code today" and "only host" finds nothing stale. No tool, setting, schema or catalog change.

## Deviation from the parent design

1. **A child is `automated` with at most two user turns, not one, and a fork is told from a child by copied history** (call 2), from the author's history.
2. **`SessionHead` gains `ParentSession` and `CopiedHistory`, `SessionKindRule.Decide` gains the nullable `isChild`, `SyncResult.ProjectKey` and `Plan` change for the working-directory default, and the adapter gains `SessionPathDepth` and `UsesProjectFolderFallback`**, and the finder's id and file-name rules move
   to the adapter: changes to Part 2 and Part 5 interfaces, needed for the second host.
3. **`branch_summary` is kept as a summary chunk** (call 3); the parent names only pi's `compaction` entry.
4. **The working directory is a default project only inside a git repository** (call 8), and the host comes from `session_start` as
   well as `TOTAL_RECALL_HOST` (call 7).
5. **`backfill` defaults to every detected host** (call 9).

## Acceptance (the parent's Done when, mapped)

| Parent's Done when | Test or deliverable |
|---|---|
| A fixture of pi sessions with a compaction, an error, a child and a fork backfills | tests 1, 2, 4 |
| and is searchable like the Claude Code fixture | test 5 |
| the child as `automated` and the fork as `interactive` | tests 3 and 4 |
| text added to a running pi session is found by search | test 7, the manual check |
| the forgotten text stays out of the pass and the sync; the record is private and absent for children, no file, Windows, archive off; a failed compaction adds no note; the default project from the working directory | tests 4, 7, 8 |
| backfill hosts and status; no test reads real pi sessions; speed; the manual check; documentation | tests 9, 10, 11, 12, the documentation grep |
| with a user session and an in-process child both live, only the user's session has a marker and search from either resolves to it | test 8 (one marker naming the user's session) and test 6 (a lookup by the shared pid) |
| after a pi compaction the next turn carries the breadcrumb once | test 8, the manual check |

## Left to the builder

The file and type names beyond those above (the extension's marker module, the process-wide host value, `PiRecordMapper`'s helpers);
how the shared id pattern is factored between the two adapters; the SQL of the per-host counts in `status`; how the pi fixture
renderer shares code with `ClaudeFixtureHome`; the exact wording of `status`, `backfill` and the notes (the tests pin the substrings
the spec quotes).
