# Make the current Claude Code session searchable while it runs (Part 5 of the transcript archive): design

Parent: #145, Part 5 (docs/superpowers/specs/2026-11-18-transcript-archive-design.md)

Date: 2026-11-24. Status: draft for review. This is the sub-spec of Part 5 of the transcript archive. It follows the parent's
"Shared decisions" (above all decisions 7, 9, 10 and 12) and adds the detail that belongs to this part. Parts 1 (opt-in, the store,
`forget`, isolation from sync), 2 (`transcripts backfill`, the adapter, redaction, the chunker), 3 (embedding and
`transcript_search`) and 4 (`transcript_get`, `transcript_sessions`) are merged.

## Goal

With the archive on, text added to a **running** Claude Code session is found by `transcript_search` at once, with no wait for the
next `session_start` pass, and the tools know which session is "current" so they can default to it:

- A small hook (a second entry beside the pinned floor's, independent of it) records which session each Claude Code process is
  running: a **marker** per host process.
- Before `transcript_search`, `transcript_get` or `transcript_sessions` runs, the engine finds the current session's marker by
  walking its own ancestor process ids, reads that session's file and ingests its new tail (**sync-on-read**), under a small budget.
- With a marker, `transcript_search` defaults to the current project (and `scope: "session"` means the current session) instead of
  "everything".
- Without a marker the tools still work and say so: the newest recently written session file is synced as a **guess**, disclosed as
  `currentSession: "guessed"`, and is never used to choose a scope.

Success is the parent's "Done when": text added to a running session after the last pass is found by `transcript_search` with no hook
running other than the marker writer, including text a compaction removed from the model's context; two parallel sessions in one
directory each resolve to their own file (Linux and macOS: simulated host processes in the tests plus a manual check on a real Claude
Code; a platform the spike could not settle is a recorded known limit); a marker pointing outside the transcript root, by `..` or by a
symlink too, is ignored; sync-on-read never re-ingests a forgotten session or the records before the cutoff; and with the archive on,
`session_start` and `memory_search` stay within the bounds of Part 3.

Non-goals (other parts, or never): the breadcrumb after a compaction (Part 6); pi, and any host other than Claude Code (Part 7;
the finder and the sync are written per host so that Part 7 adds a host, not a mechanism); a marker for Cursor, Copilot CLI or
Codex; `prune`, `rescan`, `reproject` and the re-embed (Part 8); an ingest triggered by a hook (a hook never ingests: the parent's
decision 11, and embedding is slow); Windows (a recorded known limit, see "The spike"); a marker that is removed when a session
ends (stale markers are pruned, see section 1); a new tool, a new argument of `transcript_get` or `transcript_sessions`, or any
schema change.

## The spike (what was observed, and what it settles)

Run on 2026-11-24 on Linux (CachyOS, kernel 7.3), Claude Code 2.1.330, with a temporary `--settings` file registering a logging
`SessionStart` and `UserPromptSubmit` hook and a logging stdio MCP server, in an isolated working directory. Both `claude -p` and an
interactive session (a pseudo-terminal, with a `/clear` in the middle) were run. The logging scripts walked `/proc/<pid>/stat`
upwards from their own pid.

| Observed | Consequence |
|---|---|
| In `-p` and interactive, a hook command (`node hook.js`) and the MCP server process are both **direct children of the `claude` process**. | A hook and the engine share one ancestor, the host process, on Linux. |
| For the installed plugin the engine's chain is `total-recall` (engine) -> `node bin/start.js` (the shim) -> host; the host's own parent is a shell or terminal. (Seen on the running pi processes of this machine: engine -> shim -> `pi`; the same shape.) | The engine walks up; the host is its nearest ancestor that has a marker. The walk must go at least three hops. |
| The hook payload carries `session_id`, `transcript_path` (`~/.claude/projects/<cwd with / and . replaced by ->/<session_id>.jsonl`), `cwd`, `hook_event_name`; `SessionStart` also `source` (`startup`, `clear`; `compact` and `resume` per Claude Code's documentation, not run here); `UserPromptSubmit` also `prompt`, `prompt_id`, `permission_mode`. | The marker is built from the payload alone. |
| After `/clear` the **same host pid and the same MCP server** carry a **new `session_id`** and a new transcript file; the MCP server's own environment still holds the **old** `CLAUDE_CODE_SESSION_ID`. | The environment variable is wrong after a `/clear`; a marker rewritten on every prompt is right. This is why the marker is written by a hook on `UserPromptSubmit` too. |
| The hook's environment has `CLAUDE_PID` (the host pid); the MCP server's has `CLAUDE_CODE_SESSION_ID` but, interactively, no `CLAUDE_PID`. | Not relied on: the hook uses `$PPID`, the engine its ancestors. |
| `claude` in an unknown folder asks "do you trust this folder" before it starts; a hook does not run before the answer. | Tests of the real host need a trusted folder (the spike marked one temporary folder trusted in `~/.claude.json`). |

Not observed, and so recorded as known limits in `docs/hosts/claude-code.md` and `docs/transcripts.md`: **macOS** (the hook side
is portable shell and node; the engine side reads parents with `ps -o ppid= -p <pid>`, covered by a simulated parent function and
by no run on a real Mac); **Windows** (Claude Code's hook shell there is Git Bash, whose `$PPID` is an MSYS pid and not a Windows
pid, and the engine has no cheap parent lookup: Part 5 writes no marker on Windows, and a Windows user gets the guess and
`currentSession: "guessed"`); a host process started under a wrapper that does not exec (the shim finds no marker among the
ancestors it can read: the same guess); a Claude Code that does not send `transcript_path` (no marker is written).

## Calls made here (the person can overrule them at sign-off)

1. **The marker file is named by the host pid and found by an ancestor walk.** `<dataDir>/live/claude-code-<pid>.json`, the pid
   being the hook shell's `$PPID`. The engine tries each ancestor pid, nearest first, so two sessions in one directory (or one tmux
   server) resolve to their own file: the only marker named by an ancestor of the engine is its own host's.
2. **The hook is a shell wrapper that tests the sentinel before it starts node** (decisions 10 and 12): archive off, no node
   process, no cost on a prompt.
3. **A marker is trusted only after the engine re-checks it:** shape, `host_pid` equal to the file name, a session id the adapter
   accepts, a file name equal to `<session_id>.jsonl`, and a real path (every symlink resolved, `..` removed) under the host's
   transcript root. Anything else is the same as no marker.
4. **The guess is bounded and never chooses a scope.** Without a marker the newest session file written within 30 minutes is
   synced; older files are not guessed. The parent says "the most recently modified session file for the project", but the engine
   cannot know the project under Claude Code without a marker (the working directory is the plugin directory), so the guess is the
   newest file of the host. It is disclosed, and a guessed session is never turned into a default `session` or `project`.
5. **`transcript_get` and `transcript_sessions` sync text only, and never embed.** Part 4 promised that reading needs no model and
   sends nothing anywhere; that stays true. Only `transcript_search` (which holds an embedder anyway) embeds the tail, within the
   parent's small budget. A tail left unembedded is found by its words and counted in `pendingEmbedding`.
6. **The embed queue gets a session preference.** The queue is ordered by chunk id, so the new tail sits behind any backlog and 8
   chunks would never reach it. The store's queue read takes an optional session row id whose chunks come first. This is the one
   change to a Part 3 interface.
7. **Sync never fails a read.** Every failure is logged to stderr and the read goes on with what is stored. The search answer
   discloses an incomplete catch-up (`syncNote`).

## 1. The marker and its writer (Claude Code)

**Registration.** `hooks/hooks.json` gets a second command entry in the `SessionStart` group (matcher unchanged:
`startup|resume|clear|compact`) and in the `UserPromptSubmit` group, beside the floor's and the session-start script's, with a
`timeout` of 5 seconds: `bash "$CLAUDE_PLUGIN_ROOT/hooks/live-marker/run.sh"`. Claude Code runs hook entries of one group
side by side, so the writer neither waits for the floor nor delays it, and it works with `floor_enabled = false` and with no engine
provisioned. Only Claude Code reads this file (Cursor, Copilot CLI and Codex have their own manifests).

**`hooks/live-marker/run.sh`** (shell, no node unless needed). It never prints and always exits 0.

1. Leave at once (exit 0) on `MINGW*`, `MSYS*` and `CYGWIN*` (`uname -s`): no marker on Windows.
2. Resolve the data directory by the engine's rule (`resolveDataDir`): a non-empty `TOTAL_RECALL_HOME` as it is, else
   `.total-recall` under a non-empty `HOME`, else under `USERPROFILE`; none of them set, leave. (A test pins this wrapper against
   `scripts/fetch-binary.js` `resolveDataDir` over a table of environments.)
3. Leave unless `<dataDir>/live/enabled` is a regular file. (Archive off: nothing more runs.)
4. Run `node "$PLUGIN_ROOT/hooks/live-marker/write-marker.js" "$PPID"` with the hook's stdin, stdout and stderr discarded.

**`hooks/live-marker/write-marker.js`** (plain ESM, no dependency, no build). Arguments: the host pid (a decimal positive integer
without leading zeros, at most 2147483647; else leave). It reads the hook payload from stdin for at most 500 ms and 64 KiB. It writes
**nothing** unless the payload is an object with: `session_id` matching `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$` (the same rule the
adapter uses for the file name; one shared list of accepted and refused ids is checked by both test suites), `transcript_path` an
absolute path of at most 4096 characters with no NUL, and `cwd` the same. The marker is

```json
{"v":1,"host":"claude-code","host_pid":1234,"session_id":"…","transcript_path":"…","cwd":"…","at":"2026-11-24T10:00:00.000Z"}
```

(`at` is the write time, UTC, milliseconds, `Z`.) The parent's shape is `{session_id, transcript_path, cwd, at}`; `v`, `host` and
`host_pid` are added so the engine can refuse a file that is not what its name says. It is written **atomically**: `live/` created
mode 0700 when absent, the content to a temporary file in `live/` mode 0600, then renamed over `claude-code-<pid>.json`. A write
failure, a full disk or a read-only directory is swallowed. The process has an overall deadline of 3 seconds and exits 0 on every
path.

**Pruning.** When the payload's `hook_event_name` is `SessionStart` (so once per session start, never on a prompt), after writing,
the writer lists `live/` (at most 200 names looked at) and deletes only: a name matching `^claude-code-([0-9]{1,10})\.json$` whose
pid no longer exists (`process.kill(pid, 0)` fails with `ESRCH`; `EPERM` counts as alive) or whose modification time is more than 30
days old, and a `*.tmp` of the same family older than one hour. It never deletes `enabled`, its own file, or any other name.
(A marker of a session that ended stays until the next session start prunes it: a pid that is reused by a new host process is
overwritten by that process's first `SessionStart` before any tool call can read it; a stale file for a dead pid is never read
because no live process has that pid among its ancestors.)

**What the marker records**: a session id, the path of a transcript file, a working directory and a time, in
`<dataDir>/live/`, which is 0700. It is local, never synced, never read by a tool as text (`docs/transcripts.md` and `PRIVACY.md`
say so).

## 2. Finding the current session (the engine)

`LiveSessionFinder` (`src/TotalRecall.Infrastructure/Transcripts/`) takes the data directory, the adapters (their `Host` and
`Root`), a function that lists the engine's ancestor pids and a path resolver, and returns the current session or null:

1. **Ancestors.** `ProcessAncestry.Chain()` lists the engine's ancestors nearest first, excluding the engine itself, at most 12:
   - Linux: the parent of a pid is field 4 of `/proc/<pid>/stat`, read after the **last** `)` (the command name may hold spaces
     and parentheses);
   - macOS: `ps -o ppid= -p <pid>` (a subprocess per hop, 1 second each and 3 seconds for the chain, killed at the limit);
   - any other platform: an empty list.
   It stops at pid 1 or lower, at a repeated pid and at a failed read. It is computed on first use and kept for the life of the
   process (the ancestors of a process do not change; a failed or empty result is not kept, so the next call tries again).
2. **For each ancestor pid and each adapter**, read `<dataDir>/live/<host>-<pid>.json` and accept it only if all hold: at most 16
   KiB; a JSON object; `v` is 1; `host` equals the adapter's host; `host_pid` equals the pid; `session_id` is an id the adapter
   accepts; `transcript_path` and `cwd` are absolute strings; the **real path** of `transcript_path` is under the real path of
   `adapter.Root` (compared on whole path components, case-sensitively except on a case-insensitive platform), is a regular file
   (not a link) and its name is `<session_id>.jsonl`. The first accepted marker wins (nearest ancestor).
3. The real path is computed by resolving each component, following links (at most 40 in total, else refused) and removing `.`
   and `..` as it goes, so a symlinked directory, a symlinked file and a `..` that climbs out all give a path outside the root and
   the marker is refused. The same function resolves the root.
4. A refused or unreadable marker is **not** an error: it is skipped and the walk goes on, and a refusal is logged once per file
   per process to stderr (never stdout).

The result is `CurrentSession(Host, HostSessionId, TranscriptPath, Cwd, Kind = "marker")`. The finder opens no archive and reads
nothing but the marker files and the root's path components.

**The guess.** With no marker, `TranscriptSyncService` takes, for each adapter whose `Detect()` is true, the newest session file
by modification time (ties broken by the ordinal path) among `EnumerateSessions()` sources modified within the last 30 minutes of
the service's clock, and across hosts the newest of those. None within the window: no current session (`Kind = "none"`). The
result is `Kind = "guessed"`.

## 3. Sync-on-read

`TranscriptSyncService` (`src/TotalRecall.Infrastructure/Transcripts/`), built by `ServerComposition` for the sqlite and Cortex
compositions beside the search service and handed to the three handlers (null in a composition without it: no sync). One method,
`SyncCurrent(embed, ct)`, returns a `SyncResult(Current, Complete, Note)`; it never throws.

The handlers call it **after** the sentinel check and the argument parsing (an invalid call syncs nothing, the off answer opens
nothing) and **before** the read service, inside a catch that logs and goes on. Steps:

1. **Single flight.** One sync runs at a time per process; a call that finds one running skips its own and reads what is stored
   (`Complete = false`, no note: the other call is bringing the tail in).
2. **Find the current session** (section 2). `none`: nothing to sync, the result says `none`.
3. **Open the archive** for writing without creating it (`TranscriptStore.Open(location, create: false)`). Absent or refused:
   nothing to sync (the read service answers as it does today).
4. **Ingest the session's file** through `TranscriptIngester.IngestSession` for the host's adapter, with the `SessionSource` made
   from the file's current size and modification time (the real path for a marker, the enumerated source for a guess), under a
   **2-second budget** (a cancellation source on the service's clock, as the pass does). This is the same code path as the
   `session_start` pass and backfill, so the exclusions hold by construction: a forgotten session (by id, by project key, by a root
   under which its `cwd` lies) is `Excluded` before its file is opened, and records at or before the global cutoff (and a pruned
   session's cutoff) are dropped by the builder. An unchanged file (size, modification time and cutoff as stored) costs one stat and
   one state read. An ingest that ran out of time leaves the transaction untouched (the write is one transaction) and the result
   `Complete = false` with a note. Statuses `Retry`, `Skipped` and `Changed` are also `Complete = false`; `Excluded`, `Empty`,
   `Unchanged`, `New` and `Updated` are complete.
5. **Embed the tail (search only).** When the caller passes an embed plan (the search's context: its embedder, settings, storage
   mode and `[embedding]` config) and the session has chunks waiting, run the Part 3 worker once with `EmbedBudget(2 s, 8 chunks)`
   and the session preferred (call 6), using the same gate the search computes (one shared helper builds the gate input from the
   context; the sync never builds an embedder of its own). A gate that refuses (a remote provider without the opt-in, a model not
   ready, vectors of another model, a re-embed running) or a lease held by another run ends the step without a word: the tail is found
   by its words and counted in `pendingEmbedding`. `transcript_get` and `transcript_sessions` pass no plan and skip this step.

The total is bounded: 2 s of ingest, then at most one embed batch round of 2 s or 8 chunks (the time is checked between batches of
up to 16, so one batch may overrun, as in Part 3), plus a model load the first time. It is the parent's "about 2 seconds or 8
chunks".

`session_start`, `memory_search` and every other tool are untouched: no sync, no marker read, no new work at boot (the finder and the
ancestry are built lazily by the first transcript tool call). The `ArchivePass` of Part 3 is unchanged and still runs from
`session_start`; it and a sync of the same session race safely through `ApplyIngest`'s own re-checks (a write that finds the row or
the cutoff changed is redone once, then left, as in Part 2).

## 4. Defaults from the marker (`transcript_search`)

`TranscriptSearchRequest` gets the current session (or null); `TranscriptSearchService.Plan` uses it, **only when its `Kind` is
`marker`**:

| Arguments | Today (Part 3) | With a marker |
|---|---|---|
| `scope` omitted, `session` given | session | session (unchanged) |
| `scope` omitted, `project` given | project | project (unchanged) |
| `scope` omitted, neither | all | **project**, the key of the marker's `cwd`, with `scopeNote` "defaulted to the current project (<key>)" |
| `scope: "project"`, no `project` | all, with a note | **project**, the key of the marker's `cwd` |
| `scope: "session"`, no `session` | argument error | **session**, the marker's session |
| `scope: "all"` | all | all |

Without a marker (`guessed` or `none`) every row is today's behaviour; the note of the second row now reads "the current project
could not be determined, so every project was searched" (it said "not detected yet"), and the error of the fifth row is the same text
plus "; the current session is not known here". An explicit argument always wins. The key of the marker's `cwd` is computed by the
code the ingest uses (`ProjectIdentityRule.Derive` with the project's directory name from the transcript path), so it equals the key
stored for that session's chunks; a `cwd` that no longer exists gets the path key, as in decision 8. If the current session is itself
automated, a default project search leaves its own hits out (`include_automated` is false by default); a named session is always
searched.

**The answer gets three fields** (`TranscriptSearchResponseDto`; the existing ones are unchanged):

- `currentSession`: `"marker"`, `"guessed"` or `"none"` (always present);
- `currentSessionRef`: `"<host>:<host_session_id>"` for `marker` and `guessed`, else absent;
- `syncNote`: a short sentence, present only when the catch-up of the current session was not complete ("the newest text of the
  current session may be missing: the catch-up ran out of time", "…: the session file could not be read", and so on); absent
  otherwise, and absent for a skipped single-flight call.

`transcript_get` and `transcript_sessions` answer exactly as they do today (they sync, they do not report it).

## 5. The embed queue's session preference (the one change to Part 3)

`ITranscriptStore.NextEmbedWork(int limit, IReadOnlyCollection<long> exclude, long? preferSession = null)`: when
`preferSession` is set, the order is `(chunk's session row = preferSession) DESC, id`, else `id` as now. `TranscriptEmbedWorker.Run`
gets the same optional argument and passes it on every batch read. Both `ITranscriptStore` implementers (`TranscriptStore`, the test
`DecoratingTranscriptStore`) take it. The `session_start` pass and `transcripts embed` pass nothing and behave as before. A test
shows that with 50 chunks of an older session waiting and 5 new chunks of the current one, an 8-chunk run embeds the 5 first.

## 6. Fixtures and tests

New tests have no network, no real Claude Code and no model download; the real-host check is manual and recorded.

- **Writer (node, in `test:shim`).** `hooks/live-marker/write-marker.test.js`: the marker shape and file mode 0600 / dir 0700,
  atomic replace (no `.tmp` left), a changed `session_id` for the same pid replaces the content (the `/clear` case), every payload
  rule (a bad `session_id` from the shared list, a relative or NUL-holding path, a missing `transcript_path`, garbage stdin,
  oversized stdin, a stdin that never closes: nothing written and exit 0), an unwritable `live/` (exit 0), pruning (a dead pid and
  an old file removed, a live pid and `enabled` and a foreign name kept, at most 200 names looked at, only on `SessionStart`).
  `hooks/live-marker/run.test.js`: with the sentinel absent a recording `node` shim is **never started**; with it present the
  writer runs with the shell's `$PPID`; the data-directory table matches `resolveDataDir`; a Windows `uname` leaves; `hooks.json`
  holds the second entry in both groups with the timeout (`hooks/hooks.config.test.js` is updated for it); and the writer works in a
  plugin tree with no `binaries/` and with a floor that is disabled.
- **Simulated host processes.** A node test starts two long-lived fake host processes (`sleep`-like node scripts) in one directory,
  runs the real `run.sh` under each (so `$PPID` is that process) with different payloads, and asserts two files named by the two
  pids; killing one and running a `SessionStart` for the other prunes the dead one's file.
- **Finder (xUnit).** `LiveSessionFinderTests`: nearest ancestor first, two sessions in one directory each resolve their own file
  from their own simulated chain (engine -> shim -> host A, and -> host B), a marker for a non-ancestor pid is never used, every
  refusal of section 2 (a `..` climbing out of the root, a symlinked directory inside the root pointing outside, a symlinked file,
  a file that is not `<session_id>.jsonl`, another session id than the file's, `host_pid` not equal to the name, oversized,
  malformed, wrong `v`, wrong host, a path under the root of another host) skips it and the walk continues; a valid farther marker
  is used when a nearer one is refused. The symlink cases are skipped on Windows. `ProcessAncestryTests`: a real child and
  grandchild process on Linux report the right parents (including a command name with spaces and a `)`), the chain stops at a
  repeat, the macOS reader is exercised through an injected `ps` runner (a fixture output, a timeout, a failure).
- **Sync (xUnit, a real archive built by backfill of the fixture session).**
  1. Text appended to the session file after the last pass (new exchanges, then a compaction boundary and its summary, then more
     exchanges) is found by `transcript_search` by its words with `ArchivePass` never run, and the text from before the boundary is
     found too.
  2. The same session grown across two syncs equals the same session ingested once (the Part 2 equality, through the sync path).
  3. Forgotten: a forgotten session, a forgotten project (by key, and by a root for a session whose key differs), the global
     cutoff, and a pruned session's cutoff: a sync of the current session ingests nothing it must not, and the file is not opened for a
     forgotten id.
  4. Budget: with a clock that is spent after the ingest, the ingest stops, the result is `Complete = false` and the search still
     answers; with a backlog of 200 older waiting chunks the sync embeds the current session's new chunks first, at most 8, and the
     rest wait.
  5. `transcript_get` and `transcript_sessions` sync text only: a recording embedder given to the composition is never called by
     them, and a remote provider without the opt-in is never built by any of the three.
  6. Defaults: the table of section 4 row by row, with a marker and without; a guessed session never changes a scope; the
     `currentSession` and `currentSessionRef` fields; `syncNote` present only when incomplete; the error of the fifth row.
  7. The guess: the newest file within 30 minutes wins, one at 31 minutes is not guessed, two files are broken by time then path,
     and the result says `guessed`.
  8. A failing sync (a store that throws on open, an unreadable file, a file deleted between the stat and the read, a store busy
     past the timeout) logs to stderr and never fails or changes the read; two concurrent syncs run once.
  9. Off: with the archive off none of the three tools reads a marker, enumerates a file or opens the archive (the existing off
     tests are extended), and `session_start` plus `memory_search` construct no finder (a source scan and a counting seam).
  10. **Bounds of Part 3**: the Part 3 speed test for `memory_search` and the `session_start` check are re-run with the archive on and
      the sync wired in (a median within 20% of Part 3's and under its own bound); a sync with nothing new costs a median under
      100 ms on a 2,000-session archive; the guess over 5,000 session files takes under 1 s.
- **The composition seam.** `ServerComposition` takes the ancestry function and the data directory through a test seam like
  `ArchivePassOptions`, so a test places a marker for a fake host pid and gives the engine a chain that contains it.
- **The manual check on a real Claude Code** (recorded in the PR, not a test): `claude -p` with `--settings` registering the real
  `hooks/live-marker/run.sh` (with `TOTAL_RECALL_HOME` a temporary directory that holds the sentinel) and `--mcp-config`
  registering the freshly built engine as the MCP server with the same `TOTAL_RECALL_HOME`; a prompt that makes the model call
  `transcript_search`. The marker must be named by the real `claude` pid, and the answer's `currentSession` must be `"marker"`
  with the right `currentSessionRef`. Two such runs at once in one directory must give two different refs. The trust prompt of an
  unknown folder is answered once by hand.

## 7. Documentation and what goes stale

- `docs/transcripts.md`: a section "Keeping the current session up to date" (the marker, what it records and where, the sync and
  its budget, the defaults, `currentSession`, `syncNote`, the guess, the known limits, that a hook never ingests), the status line,
  and the table of the three tools.
- `docs/tools.md`: the three new fields of `transcript_search` and the defaults; `docs/hosts/claude-code.md`: the second hook, the
  observed facts of the spike (Linux, Claude Code 2.1.330) and the unobserved platforms; `docs/configuration.md` unchanged (no new
  setting).
- `AGENTS.md`: the WHERE TO LOOK rows (the marker writer and wrapper, the finder and ancestry, the sync service, the embed
  preference), the Claude Code hook description (a second entry beside the floor's), the transcript tool paragraph; the
  Infrastructure and handler `AGENTS.md` files; `PRIVACY.md` (the marker is local); `CHANGELOG.md`.
- Stale text to search and fix: "the current project is not detected yet", "Until the marker exists (Parts 1 to 4)", "Part 5" as a
  future part in `docs/`, `README.md` and the handler descriptions; the `transcript_search` description and `catalog.json` (the
  description gains the defaults; `dump-catalog | diff catalog.json -` must be clean).

## Deviation from the parent design

1. **No marker on Windows.** The parent allows "the spike settles or declares unsupported"; it is declared unsupported (see the
   spike section), with the guess as the fallback.
2. **The guess is the newest file of the host within 30 minutes**, not "the most recent file for the project" (the engine does not
   know the project without a marker), and it never chooses a scope or a session (call 4).
3. **`transcript_get` and `transcript_sessions` sync text only**; the parent's decision 7 says sync-on-read embeds the new tail
   (call 5). Part 4's no-model guarantee is kept.
4. **The marker has `v`, `host` and `host_pid`** besides the parent's four fields.
5. **The engine tries every ancestor, nearest first**, and the hook names the file by the host pid; the parent says the engine
   "finds its own entry by walking its ancestor process ids", which this makes exact.
6. **A change to a Part 3 interface** (`NextEmbedWork` and `TranscriptEmbedWorker.Run` take an optional session preference), needed
   for the parent's "about 8 chunks" to mean the tail (call 6).
7. **Three new fields on a `transcript_search` answer** (`currentSession`, `currentSessionRef`, `syncNote`); the parent says only that
   the response says `guessed`.

## Acceptance (the parent's Done when, and every criterion of the ticket, mapped)

The parent's Done when: "Text added to a running Claude Code session after the last pass is found by `transcript_search` with no
hook running other than the marker writer, including text a compaction removed from the model's context": sync test 1 and the
manual check. "Two parallel sessions in one directory each resolve to their own file": the finder test, the simulated host test
and the manual check. "A marker pointing outside the transcript root, by a `..` or a symlink as well, is ignored": the finder
test. "Sync-on-read never re-ingests a forgotten session or the records before the cutoff": sync test 3. "With the archive on,
`session_start` and `memory_search` stay within the bounds of Part 3": sync test 10.

The ticket's criteria, in the order of the issue (number: where):

| Criterion | Test or deliverable |
|---|---|
| 1 the record is written on start and on every prompt, follows `/clear`, private permissions | writer tests (shape, modes, a changed session id for one pid), simulated host test, manual check |
| 2 archive off: nothing written, no process but a shell test | `run.test.js` (a recording `node` is never started) |
| 3 floor off or no engine; bad input, unwritable directory, a hook that cannot finish | writer tests, `run.test.js` (no `binaries/`, `floor_enabled = false`), deadline test |
| 4 records of ended sessions removed, nothing else | writer pruning tests, simulated host test |
| 5 two sessions in one directory each resolve their own file | finder test, simulated host test, manual check; macOS by the injected parent reader |
| 6 a path outside the transcript folder by `..` or a symlink, malformed or misnamed, is ignored | finder test (every refusal of section 2) |
| 7 text added after the last pass, across a compaction, found with no pass and no other hook | sync test 1, manual check |
| 8 forgotten session or project, records before the cutoff, never stored again | sync test 3 |
| 9 backlog: the current session's new chunks embedded first within 2 s or 8 chunks | the preference test of section 5, sync test 4 |
| 10 `transcript_get` and `transcript_sessions` store text only | sync test 5 |
| 11 default scope from the current session, explicit wins, `currentSession` | sync test 6 |
| 12 the guess within 30 minutes, disclosed, never chooses a scope | sync tests 7 and 6 |
| 13 a failed or late catch-up never fails the read, the search says so | sync tests 8 and 4 |
| 14 `session_start` and `memory_search` within Part 3's bounds, the cost of a no-news sync and of the guess | sync test 10 |
| 15 archive off: no record read, no file listed, no archive opened | sync test 9 |
| 16 the spike recorded, macOS and Windows stated | "The spike" above, `docs/hosts/claude-code.md`, the manual check in the PR |
| 17 documentation, catalog and registered tools agree, no stale text | section 7, the catalog drift check, the registry-and-catalog test of Part 4 extended |

## Left to the builder

The file layout inside `hooks/live-marker/`; the names of the internal types beyond those above; the exact wording of `syncNote`
and the notes (the tests pin the substrings the spec quotes); how the shared gate helper is factored out of the search service; how
the real-path function is written (a `realpath` P/Invoke on Unix or a component walk), provided it passes the refusals of
section 2 under NativeAOT and does not use reflection; whether the ancestry reader keeps its `ps` runner as an injected delegate;
the placement of the embed preference in the SQL.
