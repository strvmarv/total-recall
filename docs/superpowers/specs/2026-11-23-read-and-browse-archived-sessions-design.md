# Read and browse archived sessions (Part 4 of the transcript archive): design

Parent: #145, Part 4 (docs/superpowers/specs/2026-11-18-transcript-archive-design.md)

Date: 2026-11-23. Status: draft for review. This is the sub-spec of Part 4 of the transcript archive. It follows the parent's
"Shared decisions" (above all decisions 3, 9 and 10) and adds the detail that belongs to this part. Parts 1 (opt-in, the store,
`forget`, isolation from sync), 2 (`transcripts backfill`, the adapter, redaction, the chunker) and 3 (embedding and
`transcript_search`) are merged.

## Goal

With the archive on, an agent that found a hit with `transcript_search` can **read around it**, and can **list the sessions** that
exist:

- `transcript_get` returns the exchanges around a hit (or a stated range of one session) as readable text, within a token budget,
  and says when it had to leave something out.
- `transcript_sessions` lists archived sessions newest first, each labelled by its title, with automated ones hidden unless asked for.
- While the archive is off, both answer exactly what Part 1 made them answer (`{"enabled": false, ...}`).

Success is the parent's "Done when": given a hit from search, `transcript_get` returns the surrounding exchanges within the budget and
flags truncation; `transcript_sessions` lists a project's sessions newest first with their titles and hides automated ones unless
asked; both answer that the archive is off while it is off.

Non-goals (other parts, or never): sync-on-read and "the current session" (Part 5: the live marker, the default scope, the read of a
session's newest tail); the breadcrumb (Part 6); pi (Part 7); `prune`, `rescan`, `reproject` and the re-embed (Part 8); any way to
write, edit or delete through a tool (management is CLI-only); a transcript-wide dump (one call reads one window, never a whole
session); searching (`transcript_search` is unchanged); any new column or migration.

## What exists today and what this part adds to it

Parts 1 to 3 give: `tx_session` (`host`, `host_session_id`, `project_key`, `cwd`, `started_at`, `ended_at`, `kind`, `title`,
`exchange_count`, ...), `tx_chunk` (one **exchange** per chunk: `exchange_start = exchange_end`, a `part` number when a long exchange
is split, `chunk_type` `dialogue` or `compaction-summary`, `has_error`, `ts`, `content` = a header line, a blank line, then a body of
at most 1,800 characters), `TranscriptStore.Open(location, create: false)`, the `ITranscriptSearchReads` read transaction (the search's),
`TranscriptSearchService.SplitSession` and `ChunkText`, the sentinel gate `Func<bool>` read on every call, and two stub handlers
(`TranscriptGetHandler`, `TranscriptSessionsHandler`) whose schemas already exist and whose on-answer is "not available in this version
yet". **No read API for exchanges by range or for sessions exists.** Exchange numbers are the adapter's units, numbered from 0 in file
order, a compaction summary included; a unit that kept no text has no chunk, so the stored numbers can have gaps.

This part adds, inside `TotalRecall.Infrastructure.Transcripts` unless said otherwise:

- a read API on `ITranscriptStore` for this part (section 1);
- `TranscriptReadService` (a service: request in, response out) with `Get` and `Sessions` (sections 2 and 3);
- `TotalRecall.Server`: `TranscriptGetHandler` and `TranscriptSessionsHandler` run the service; the wire DTOs go in the source-generated
  `JsonContext`; `ServerComposition` builds the service in `OpenSqlite` and `OpenCortexCore`, where the three tools are registered;
- the documentation and chores of section 7. No change to `TotalRecall.Cli` and none to the schema.

## Calls made here (the person can overrule them at sign-off)

1. **When the window is larger than the budget, the requested exchanges are kept and the context is trimmed from the far edges** (the
   person's choice at brainstorm). Context is added outward one exchange at a time, and an exchange is returned whole or not at all
   (except for one exchange that alone is over the budget: call 3).
2. **A new optional argument `max_tokens`** (200 to 20,000, default 2,000). The parent says "capped to a token budget (default about
   2,000 tokens)" and names no argument; without one the budget could never be raised. The schema changes, so `catalog.json` is
   regenerated.
3. **A budget is counted as `ceil(characters / 4)` of the exchange text** (the estimate the engine already uses for transcripts: 4
   bytes a token, here characters), the JSON around it not counted. A first requested exchange that alone exceeds the budget is returned
   **cut at the budget**, marked, with a note to raise `max_tokens`: a smaller unit than an exchange would need a cursor into parts,
   and the archive keeps little per exchange (the average chunk is about 1,400 characters).
4. **`context` counts stored exchanges**, not numbers: the `context` nearest exchanges that have text on each side of the requested
   range. A gap (a unit that kept nothing) therefore costs no context.
5. **Reading a named session or a chunk id never applies the automated filter** (as a session-scope search does): naming is explicit
   intent, and the id of an automated session comes from `transcript_sessions`.
6. **`transcript_sessions` lists only sessions that hold at least one chunk.** A row that a rewritten source file reset
   (`exchange_count` 0, nothing stored yet) has nothing to read.
7. **`since` on sessions means "active since"**: the session's last activity (`ended_at`, else `started_at`) is at or after it. A
   session with neither time never matches a `since`.
8. **`transcript_sessions` adds `automatedHidden`**, the number of automated sessions the filters would have shown had
   `include_automated` been true (only when it is above 0 and `include_automated` is false). Without it an agent cannot tell that the
   reviewer sessions exist.
9. **There is no default project** for either tool: no `project` means every project. The live marker's cwd and session (Part 5) apply
   to the search's default scope, not here.
10. **Reading needs no embedder and no model**: both tools work whenever the archive is on and the file opens, whatever the state of
    the embedding.

## 1. The read API

`ITranscriptStore` gets one method, `BeginReads()`, returning an `ITranscriptReads : IDisposable` that holds **one deferred read
transaction** for the whole call (as `BeginSearchReads` does for a search), so a `forget` or an ingest commit between two of its
queries cannot pair a session with another session's chunks. Dispose ends the transaction. Nothing it does writes. It is a new
interface and a new file (`TranscriptReads.cs`); `ITranscriptSearchReads` is not changed.

```
SessionRow? SessionByRef(string host, string hostSessionId)            // null when none
SessionRow? SessionById(long sessionId)
ChunkRow?   ChunkById(long chunkId)                                      // the search's ChunkRow, with SessionId
IReadOnlyList<ChunkRow> ChunksOfExchange(long sessionId, int exchange)   // ascending part
IReadOnlyList<int> ExchangesInRange(long sessionId, int from, int to)    // stored exchange numbers in [from, to], ascending
IReadOnlyList<int> ExchangesBefore(long sessionId, int exchange, int n)  // the n nearest stored numbers below, nearest first
IReadOnlyList<int> ExchangesAfter(long sessionId, int exchange, int n)   // the n nearest stored numbers above, nearest first
SessionList ListSessions(SessionFilter filter, int limit)
```

- `SessionRow`: `Id`, `Ref` (`<host>:<host_session_id>`), `Project`, `Kind`, `Title`, `StartedAt`, `EndedAt`, `ExchangeCount`,
  `ChunkCount` (a count of the session's chunks).
- `SessionFilter`: `Project` (exact key, as the search's), `SinceEpoch` (compared with `COALESCE(ended_at, started_at)` as epoch
  seconds, by the same SQL expression the search uses for chunk times), `IncludeAutomated`.
- `SessionList`: the rows (newest first: `COALESCE(ended_at, started_at)` descending, sessions with neither time last, then `id`
  descending), `Total` (sessions that match, before the limit) and `AutomatedHidden` (as call 8).
- Only sessions with at least one chunk are listed or counted (call 6). All SQL is parameterised. Exchange numbers are read from the
  chunks (`exchange_start`), never from `exchange_count`.
- `ChunkRow.Content` is returned whole; the service cuts the header with `ChunkText`.

## 2. `transcript_get`

**Arguments.** Either `id` (a chunk id from `transcript_search`) **or** `session` (`<host>:<host_session_id>`) with `from_exchange` and
optionally `to_exchange` (default: `from_exchange`). Also `context` (integer 0 to 10, default 1) and `max_tokens` (integer 200 to
20,000, default 2,000). Errors are `ArgumentException`s, which `ErrorTranslator` turns into "Invalid arguments: ...": both forms or
neither; `from_exchange` or `to_exchange` given with `id`, or `session` without `from_exchange`; a malformed session
(`SplitSession`); `from_exchange` below 0; `to_exchange` below `from_exchange`; a range wider than 200 exchanges; `context` or
`max_tokens` out of range or not an integer.

**Resolving.** `id` finds the chunk, then its session; the requested range is that chunk's exchange. An id with no chunk, and a
session with no row, are **error results** (`isError` true, plain text): "no archived chunk has id N (it may have been forgotten)";
"no archived session <ref> (use transcript_sessions to list them)". With nothing archived at all (no file, or no chunk) the answer is
the search's note, `nothing is archived yet; run total-recall transcripts backfill`, as an error result for `get` and as an empty
list for `sessions` (section 3).

**The window.** The requested exchanges are the stored exchanges in `[from, to]`, ascending. Their context is the `context` nearest
stored exchanges before the first and the `context` nearest after the last (call 4); none at a session's ends. If the range holds no
stored exchange the answer has no exchanges, no context and a note saying which numbers were asked and how many exchanges the session
has; it is not an error.

**The text of an exchange.** All chunks of the exchange, ascending part, each with its header line cut (`ChunkText`), joined with a
blank line, with the continuation prefix of every part after the first removed: a part after the first starts with
`User (continued): <the question, up to 300 characters>` and a blank line (`TranscriptChunker.ContinuedLabel`), repeated only so a
part is searchable on its own. The prefix is removed only where it is literally present (an exchange with no user text, such as a
compaction summary, has none); the label is private to the chunker today and becomes `internal` so the reader shares it. The text is returned as stored (it is already redacted; nothing is redacted or altered again). The
exchange is reported with its number, its `ts`, its chunk type and whether any chunk has an error.

**The budget.** The budget is `max_tokens`, counted as call 3. Fill in this order and stop at the first exchange that does not fit:
(1) the requested exchanges ascending; (2) the context, alternating one before and one after (before first), nearer before farther,
until `context` on each side are in or the budget is full. (An exchange of the first step that does not fit ends the answer there; a
**first** requested exchange that alone is over the budget is cut at the budget on a character-safe boundary and ends with
`[cut: raise max_tokens to read the rest]`.) Reading stops as soon as the budget is known to be full, so a window over a huge session
is not read whole.

**The answer** (source-generated DTOs; a field that says nothing is omitted):

```
{
  "enabled": true,
  "session": "claude-code:3f2a9c1e-...",     // as search names it
  "project": "strvmarv/total-recall",
  "kind": "interactive",
  "title": "Back up Claude Code sessions...",   // null when the session has none
  "requested": "12-14",                          // the asked range, or the hit's exchange
  "context": 1,
  "exchanges": [
    {"n": 11, "ts": "2026-11-18T09:12:03.0000000Z", "chunkType": "dialogue", "hasError": false, "requested": false, "text": "..."},
    {"n": 12, ... "requested": true, "text": "..."}
  ],
  "approxTokens": 1840,                          // of the text returned
  "truncated": true,                             // only when something asked for was left out
  "nextFromExchange": 14,                        // only when requested exchanges were left out: the first one not returned
  "note": "..."                                  // only when it says something
}
```

`truncated` is true when any **requested** exchange was left out or cut, or any context exchange was left out for the budget (not
when a session simply has no more exchanges). `nextFromExchange` appears only when requested exchanges after the last one returned
were left out; reading on is `session` with `from_exchange: nextFromExchange`. A note says what was cut, and "raise `max_tokens`"
for a cut exchange.

## 3. `transcript_sessions`

**Arguments** (the stub's schema, unchanged): `project` (exact key), `since` (ISO-8601 date or date-time, parsed by
`TranscriptSearchService.ParseSince`), `limit` (1 to 100, default 20), `include_automated` (default false). Argument errors as above.

**The answer:**

```
{
  "enabled": true,
  "total": 143,                                  // sessions that match, before the limit
  "more": 123,                                   // total - returned, only when above 0
  "automatedHidden": 93,                         // call 8
  "sessions": [
    {"session": "claude-code:3f2a...", "project": "strvmarv/total-recall", "kind": "interactive",
     "title": "Back up Claude Code sessions...", "startedAt": "...", "endedAt": "...", "exchanges": 41, "chunks": 52}
  ],
  "note": "..."                                  // only when it says something (an empty archive)
}
```

`exchanges` is the session's `exchange_count` (valid exchange numbers are below it, some may hold no text); `chunks` is the stored
chunk count. `title` is the label and may be null. With nothing archived the answer is `total` 0, no sessions and the note of
section 2. Order and filters are those of section 1.

## 4. The handlers and the composition

- `TranscriptGetHandler` and `TranscriptSessionsHandler` take `(Func<bool> isActive, TranscriptReadService? service = null)`. The
  gate is read **once per call**: off gives Part 1's off answer (`TranscriptToolAnswers.Off`, unchanged); on with no service gives
  the not-set-up answer (the search's `OnSearchNotSetUp`, renamed `OnNotSetUp`, or a new constant for the two tools); on with a
  service runs it. `TranscriptToolAnswers.OnNotAvailable` and `For` go away (nothing answers "not available in this version yet"
  any more); the tests and docs that pin that text change with it.
- Arguments are parsed in the handler as the search handler does (`ArgumentParsing`, `ReadOptionalBool` shared, not copied). An
  `ArgumentException` is the caller's error. An archive that cannot be opened or read is an **error result**, written to stderr with
  the `[total-recall] transcript_get` prefix, never an exception that escapes.
- `TranscriptReadService(Func<TranscriptReadContext?> context, TimeProvider? time = null)`, with an internal open seam for tests as the
  search service has. `TranscriptReadContext` holds the `TranscriptLocation` only (no settings, no embedder). It opens the archive
  per call with `TranscriptStore.Open(location, create: false)`, reads in one `BeginReads()` and closes before returning. Opening may
  apply a pending archive migration (Part 3 documents this); nothing else is written, and the memory store is never touched.
- `ServerComposition` registers the new handlers in `OpenSqlite` and `OpenCortexCore`, beside the search handler, with a context
  built from the resolved location (the search's `TranscriptSearchContextFor` is split so the location part is shared). `OpenPostgres`
  registers no archive tool today and still registers none. The tool count (54), the announce files and the tool names do not change;
  the two descriptions lose "not available in this version yet", and the `get` schema gains `max_tokens`.
- Responses are source-generated DTOs in `JsonContext` (no reflection; NativeAOT). The strings of the off and not-set-up answers stay
  hard-coded JSON.

## 5. Interactions with the rest of the archive

- **Forget**: a session or chunk a `forget` removed is simply absent; a read in flight sees the state at its transaction's start.
  Nothing here keeps text anywhere (no cache, no log of what was read; the stderr line of an error names no text).
- **Search**: `transcript_search`'s hit `id`, `session` and `exchanges` fields are exactly what `get` takes; a test follows a hit.
- **Redaction and privacy**: nothing is sent anywhere. Everything returned was redacted at ingest; no tool path reaches a host's
  files or a provider.
- **Part 5** will run its sync-on-read before the service is called; the handler is the place (nothing here depends on the file being
  current, and a session mid-ingest reads as of the last commit).
- **Cortex and git sync**: nothing here is reachable from `Sync` or `GitSync`; the isolation test of Part 1 (it scans those directories
  for `tx_`, `Transcript`, `Archive`) must still pass and covers the new file names by its scan.

## 6. Fixtures and tests

New tests are in files without the `Integration` trait. Archive text is built through the store's `ApplyIngest` or a backfill of the
existing fixtures (`tests/fixtures/transcripts/`), never by hand-written SQL; a fixture with a **long exchange** (several parts) and a
session with a **gap** (an exchange with no kept text) is added.

1. Follow a hit: backfill with `--no-embed`, `transcript_search` by words, then `transcript_get` with the hit's `id`: the requested
   exchange is there, one exchange of context on each side, ascending, flagged `requested`.
2. `context` 0 returns the exchange alone; `context` 10 near a session's start or end returns what exists, with `truncated` false.
3. A range by `session` with `from_exchange` and `to_exchange`; a range partly outside the session; a range over a gap (the gap is
   skipped, context still counts stored exchanges).
4. A multi-part exchange: the parts are joined, no header line and no `User (continued):` prefix remains, the text equals the chunker's
   own parts rejoined.
5. The budget: a window over `max_tokens` keeps the requested exchanges and drops far context first, alternating before and after;
   `truncated` true, no `nextFromExchange`; a requested range over the budget ends at the last whole exchange with `nextFromExchange`
   (reading on from it returns the rest, and no exchange appears twice); a first exchange alone over the budget is cut, marked, with
   the note; `approxTokens` is at most `max_tokens` (a cut exchange included).
6. A named session or chunk id of an **automated** session is readable; a forgotten session or chunk gives the error result; a
   session name that is malformed or both or neither form of arguments is an argument error; `context` 11, `max_tokens` 100 and a
   range of 201 are argument errors.
7. `transcript_sessions`: newest first (with a session without times last), the project filter, `since` (active-since, boundary
   included), `limit` with `more` and `total`, automated hidden with `automatedHidden`, shown with `include_automated`, a reset
   session (no chunks) absent, the title as label, an empty or missing archive gives `total` 0 and the note.
8. Off: both tools answer the Part 1 off text, read the setting per call (`enable`/`disable` apply without a restart) and open no
   file; on with no service answers not-set-up. The registry's tool list and `catalog.json` agree (the drift check).
9. Read-only and consistent: a read while another connection holds an ingest transaction succeeds; no row count changes; a forget
   between two calls gives the error result on the second.
10. The speed of the reads on a synthetic archive of 100,000 chunks and 2,000 sessions: `transcript_get` of a window and
    `transcript_sessions` with filters each answer in well under a second (measured and recorded in the PR; the check asserts only a
    generous bound).

Use the exact error strings above in tests that pin them. Tests that change environment variables use
`[Collection("process-environment-console")]`.

## 7. Documentation and what goes stale

- `docs/transcripts.md`: "The three tools" (the table; the off, on and not-set-up answers; the status line at the top that says
  `transcript_get` and `transcript_sessions` are not built), a section "Reading the archive" (the get window, `max_tokens` and
  `truncated`, `transcript_sessions` and `automatedHidden`, the numbering of exchanges), and the sentence "until `transcript_get`
  arrives" in "Searching the archive".
- `docs/tools.md`, `docs/commands.md` where it lists the tools, `AGENTS.md` (the transcript row: no longer stubs),
  `src/TotalRecall.Server/Handlers/AGENTS.md` and `src/TotalRecall.Infrastructure/AGENTS.md`, `CHANGELOG.md` if the repository keeps
  an unreleased section for it, `catalog.json` (regenerated with `dump-catalog`), and the announce text of `using-total-recall` only if
  it names these tools (it does not today: grep).
- A grep for `not available in this version yet`, `still stubs`, `waits for Part 4` and `until .transcript_get. arrives` finds every
  stale line.

## Deviation from the parent design

Listed so the person sees each at sign-off. None edits the parent.

1. **A `max_tokens` argument** is added to `transcript_get` (the parent's tool line has none; call 2).
2. **The budget is `ceil(characters / 4)`** of the text, and `context` counts stored exchanges (calls 3 and 4); the parent says only
   "about 2,000 tokens" and "a context size".
3. **`transcript_sessions` hides sessions that hold nothing and adds `total`, `more` and `automatedHidden`** (calls 6 and 8); the
   parent lists `project`, `since`, `limit` and `include_automated`.
4. **A named session or chunk is read whatever its kind** (call 5); the parent applies `include_automated` to the tool.
5. **No default project** (call 9): the parent's "project and current session" resolution is written for the search.

## Acceptance (the parent's Done when, mapped)

- "Given a hit from search, `transcript_get` returns the surrounding exchanges within the budget and flags truncation": tests 1, 2, 3
  and 5.
- "`transcript_sessions` lists a project's sessions newest first with their titles and hides automated ones unless asked": test 7.
- "both answer that the archive is off while it is off": test 8.

## Left to the builder

The names of the DTOs and of the new read types; the SQL of `ExchangesBefore` and `ExchangesAfter` (an index on
`(session_id, exchange_start, part)` already exists through the unique key) and of the session list; how the window reads stop early;
the exact wording of notes; whether `TranscriptReadService.Get` and `Sessions` share a helper for the empty-archive check with the
search service; the fixture content; the cut of a first exchange on a safe boundary (`TextCut`).
