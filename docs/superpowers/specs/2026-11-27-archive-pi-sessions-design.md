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
author's own pi history (809 session files, read only). The spike's scripts are not committed.

| What | Observed |
|---|---|
| The extension's process and the MCP server | the extension's `process.pid` is the **direct parent** of an MCP server it registers (chain: server, then `pi`, then `timeout`/`bash`/the outer shell). The engine's chain under the plugin is engine, shim (`node`), the pi process, so the extension's pid is an ancestor of the engine, as the marker design needs |
| What an extension can read | at `session_start` and `before_agent_start`: `ctx.sessionManager.getSessionId()` (the uuid), `getSessionFile()` (`<dir>/<timestamp>_<uuid>.jsonl`), `getHeader()` (`{type: "session", version: 3, id, timestamp, cwd}`; a root session has no `parentSession`) and `ctx.cwd` |
| Compaction | `-p` (print mode) does not run `/compact` (the model answers that it is a harness command); in `--mode rpc` the `compact` command ran: `session_compact` fired (`reason: "manual"`), the file got a `compaction` entry, and a hidden message returned from the **next** `before_agent_start` was stored **once**, as a `custom_message` with `display: false`, after the user message of that turn. A session too small to compact answers "Nothing to compact"; `ctx.compact()` at `agent_end` in `-p` is cancelled by the shutdown |
| The author's history | 790 of 809 files have a `parentSession` (pi-subagents children, and forks); 781 of them hold **one** user turn, 9 hold **two** (a task prompt and one follow-up), 1 holds more; the 19 files without a parent are the author's own conversations (1 to many turns). Entry types seen: `message` (roles `user`, `assistant`, `toolResult`, `system`), `compaction` (28), `custom_message` (`total-recall-context` hidden, `subagent-notification`), `custom`, `usage`, `model_change`, `thinking_level_change`, `session_info`. Tool calls are blocks `{type: "toolCall", name, arguments}` in assistant messages; the names seen are `bash`, `read`, `edit`, `write`, `Agent`, `codemode`, `grep` and others |
| The file | an append-only tree: entries carry `id` and `parentId`; `/tree` navigation adds entries on another branch of the same file, so file order is not one conversation |

Not observed: macOS and Windows, a real `/fork` file and a real `branch_summary` entry (their shapes are from pi's documentation,
`docs/session-format.md`), a `bashExecution` message (documentation only), pi started with `--session-dir` or the `sessionDir`
setting pointing elsewhere, and an automatic (threshold or overflow) compaction.

## Calls made here (the person can overrule them at sign-off)

1. **The session id is pi's own id.** `pi:<uuid>`, the id pi shows, not the whole file name. The adapter parses it out of
   `<timestamp>_<id>.jsonl`, so the marker, the `session` argument and the results all say `pi:<uuid>`.
2. **A pi session is `automated` when its header names a `parentSession` and it holds at most two real user turns.** The parent says
   "at most one"; in the author's history that would file 9 of 790 children as `interactive` (a child that got a task prompt and one
   follow-up). A fork of a real conversation carries the parent's many turns (the rule is a heuristic, stored and re-evaluated, as the
   parent says). No pi session is marked `automated` by its wording: the long-prompt and opening-phrase rules of the Claude Code
   rule do not apply.
3. **The whole file is archived in file order, abandoned branches included.** The archive is a record of what was said, and the text
   of a branch the user left is still something said. A `branch_summary` entry is kept like a compaction summary (its own chunk type).
   `context_edit` entries change only what the model sees and are ignored: the text they hide stays archived.
4. **Where pi's sessions are.** `PI_CODING_AGENT_SESSION_DIR` (an absolute path), else `PI_CODING_AGENT_DIR/sessions`, else
   `<home>/.pi/agent/sessions`; a relative value, or no home, means pi is not detected. The `--session-dir` flag and the `sessionDir`
   setting are not visible to the engine: sessions stored there are not archived and a marker for them is refused (a recorded limit).
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

**Enumeration.** `<root>/<project dir>/<file>.jsonl`, one directory level down, links skipped, as for Claude Code. The file name is
`<timestamp>_<id>.jsonl`: the timestamp is `YYYY-MM-DDTHH-MM-SS-mmmZ` (24 characters), then `_`, then the id, which is 1 to 128
characters of `A-Z a-z 0-9 . _ -`. A name that does not fit is counted in `Skipped`. The `HostSessionId` is the id (call 1); the
header's own `id` is not compared, the name decides. The `SessionSource` path, size and mtime are as for Claude Code.

**Head.** The first complete line must be a `{"type": "session", ...}` header; its SHA-256 is the fingerprint, `cwd` and `timestamp`
come from it, and a new member `ParentSession` (a non-empty string `parentSession`) is added to `SessionHead`. A first line that is
not a session header, or is over the line limit, gives no fingerprint, so the session is skipped and counted, as for Claude Code.
Versions 1 and 2 of the format are read with the same rules (their unknown roles are ignored).

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

`SessionKindRule.Decide` takes one more input, `hasParent`, from the head's `ParentSession` (always false for Claude Code, whose rule is
unchanged). For a session with a parent the result is a function of the number of real user turns alone: **at most 2 is `automated`,
more is `interactive`**; a session without a parent is `interactive` (a pi session is never `automated` by its wording). The turn count
is the sum of the settled and the current pass's real user turns, so a child that grows from 2 turns to 3 flips to `interactive` through
the existing rewrite path (a changed kind re-reads the file from the start), and a fork, which carries the parent's turns, is
`interactive` from its first pass. The kind is stored and can be re-evaluated, as the parent says.

## 4. The current session on pi

**Engine side.** `LiveSessionFinder` looks for `<live>/pi-<pid>.json` for each ancestor as it does for `claude-code`; the marker's
`host` must be `pi`. Two things in it are Claude-specific and are moved to the adapter: the session id check
(`ClaudeCodeAdapter.IsValidId`) becomes `ITranscriptAdapter.IsValidSessionId`, and "the file is named `<id>.jsonl`" becomes
`ITranscriptAdapter.IsSessionFileName(fileName, id)` (pi: the name is `<timestamp>_<id>.jsonl` with the timestamp of section 1). Every
other refusal stays (size, `v`, `host_pid` equal to the name, an absolute `cwd`, a path with no `.` or `..` under the adapter's root
with exactly two components below it, neither a link, a process start check). `TranscriptSyncService`, `ArchivePass` and
`TranscriptBackfill` already work per adapter and need no change for this.

**Extension side.** A new module of the pi package writes the marker for the user's session:

- **When.** In `session_start` (every reason, so `/new`, `/resume` and `/fork` rewrite it for the new session) and in
  `before_agent_start` for a user prompt, only when the extension's own `hookable` rule holds (so never for a child, for a
  `PI_SUBAGENT_ID` process or for a session with no session file) and the platform is not Windows. At `session_start` it also prunes
  the markers of ended pi processes, as the Claude Code hook does at `SessionStart`.
- **Which archive.** The data directory is resolved as the engine does (`resolveDataDir` over the environment and, when pi follows a
  hand-configured `total-recall` server, that server's `TOTAL_RECALL_*` values; a server that cannot be followed means no marker). With
  the archive off (no `<data directory>/live/enabled`) the module writes nothing.
- **What.** `<data directory>/live/pi-<process.pid>.json` with `v`, `host` `pi`, `host_pid`, `session_id` (pi's id), `transcript_path`
  (the session file), `cwd` (`ctx.cwd`) and `at`, through the shared writer (call 5): private modes, atomic rename.
- **Never a failure.** Every step is inside a try, writes nothing to stdout or stderr, and takes no await: the marker is a small file
  written synchronously.
- **Children.** pi-subagents builds a child in the same process, so it would share the pid and overwrite the user's marker; it is
  never written for. A child's own tool calls therefore resolve to the **user's** session through the same pid (parent decision 12,
  accepted: children do not search the archive).

**The breadcrumb.** `session_compact` already queues the instructions for the next prompt. It now appends the note when the archive is
on (the same sentinel test as the marker), so the hidden message of the next `before_agent_start` carries both, once. `session_tree`,
reload and a failed compaction add nothing. The note is the text of the Claude Code hook (call 6), and `total-recall-context`
messages are dropped by the adapter, so the note is never archived.

## 5. The default project from the working directory

When no marker is found and the host (call 7) is `pi`, the sync derives the project of the engine's working directory
(`ProjectResolver`, as the ingest does) and, if it lies inside a git repository (call 8), returns it as the project of the current
call without naming a session: `currentSession` stays `guessed` or `none`. `transcript_search` then defaults, with no `scope`, to that
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

1. `PiAdapterTests`: root resolution (each variable, relative and empty cases), enumeration (names, ids, skipped names, links, an
   empty file), head (fingerprint, cwd, parent, a first line that is not a header), line starts, bad lines.
2. `PiRecordMapperTests`: every row of section 2 on a line written for it, the stub detail order, the 500-character error cut after
   redaction, string and array user content, an unknown entry type.
3. Kind: `SessionKindRule` with `hasParent` (1, 2, 3 turns; no parent with a long first prompt stays `interactive`); an ingest where a
   child grows from 2 to 3 turns and flips; the fork stays `interactive`.
4. The corpus: `backfill` of the pi fixtures stores the sessions and chunks per project and kind (the child and the follow-up
   `automated`, the fork `interactive`), a second run adds nothing, injected text and hidden messages are not stored, the branch's
   abandoned text and the branch summary are, planted secrets are stored only as markers, a deleted source keeps its chunks, forgotten
   sessions stay out.
5. Search: topic, a question answered only by a compaction summary, an exact error string and a session-scope paraphrase find the right
   pi session in the top three; the child is found only with `include_automated`.
6. The finder with host `pi`: a good marker, and each refusal of Part 5 on a pi path (a path outside the root, a link, a name that is
   not `<timestamp>_<id>.jsonl`, a `host` of `claude-code` in a `pi-<pid>.json`); the shared id and name rules for both adapters.
7. The sync on pi: text added to a running pi session is found with no pass (the Part 5 test on a pi session with a fake host
   process); the project default from the working directory (inside a repository, outside one, the host unknown, the host `claude-code`,
   an explicit argument); the guess across both hosts.
8. The extension (`pi/*.test.js`, in `test:shim`): the marker is written at `session_start` and at each user prompt, rewritten for a
   new session id, absent for a child, for `PI_SUBAGENT_ID`, with the archive off, on Windows, with an unfollowable server; two
   extension instances in one process (a user session and a child) leave one marker, naming the user's session; the breadcrumb is in
   the hidden message after `session_compact` once, absent on reload, `session_tree`, and with the archive off; the breadcrumb text
   equals the text `hooks/compaction-breadcrumb/run.sh` prints; a failing step never throws into pi.
9. Speed: the Part 5 and Part 3 checks pass unchanged with two adapters registered; the cost of enumerating a large pi directory is
   measured once and recorded.
10. A manual check on a real pi (Linux), recorded in the PR and the docs: the extension loaded with `-e`, a temporary data directory
    where `transcripts enable` was run, the freshly built engine given to pi through a project `mcp.json` entry named `total-recall`
    (which takes precedence over the extension's own registration, so no engine is downloaded), a throwaway working directory and
    `--session-dir` set to a temporary directory under the root the engine reads (`PI_CODING_AGENT_SESSION_DIR`): the marker appears
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

1. **A child is `automated` with at most two user turns, not one** (call 2), from the author's history.
2. **`SessionHead` gains `ParentSession` and `SessionKindRule.Decide` gains `hasParent`**, and the finder's id and file-name rules move
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
| with a user session and an in-process child both live, only the user's session has a marker and search from either resolves to it | test 8 (one marker naming the user's session) and test 6 (a lookup by the shared pid) |
| after a pi compaction the next turn carries the breadcrumb once | test 8, the manual check |

## Left to the builder

The file and type names beyond those above (the extension's marker module, the process-wide host value, `PiRecordMapper`'s helpers);
how the shared id pattern is factored between the two adapters; the SQL of the per-host counts in `status`; how the pi fixture
renderer shares code with `ClaudeFixtureHome`; the exact wording of `status`, `backfill` and the notes (the tests pin the substrings
the spec quotes).
