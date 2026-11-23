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
3. **A budget is counted as the sum, over the returned exchanges, of `ceil(characters / 4)` of each exchange's text** (this part's own
   estimate; the pinned-directive floor uses the same 4 per token for its transcript growth). Nothing else is counted: the JSON around
   the text and the separators between exchanges (they are separate array items) are not. `approxTokens` is that same sum, so the fit
   test and the reported number always agree. A first requested exchange that alone exceeds the budget is returned **cut**, marked,
   with a note to raise `max_tokens`: the cut text plus its marker `[cut: raise max_tokens to read the rest]` is at most
   `4 x max_tokens` characters (the text is cut at `TextCut.SafeLength` of `4 x max_tokens` minus the marker's length, so the marker is
   counted), so `approxTokens` never exceeds `max_tokens`. A smaller unit than an exchange would need a cursor into parts, and the
   archive keeps little per exchange (the average chunk is about 1,400 characters).
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
    the embedding, and nothing they do sends anything to an embedding provider.
11. **`transcript_sessions` gets an optional `before`** (an ISO-8601 date or date-time: sessions whose last activity is strictly
    earlier). Without it nothing older than the newest `limit` (at most 100) sessions could ever be listed, however large `total` is;
    `since` and `before` together bound a window to page through. It is the one new argument of that tool.
12. **Unknown things are errors that name how to continue; an empty archive adds a reason.** A named session or chunk id that does not
    exist (never archived, or forgotten) is an error result with the exact text of section 2, whether or not anything else is
    archived; when the archive holds no chunk at all the text adds that nothing is archived yet and how to backfill. The one case
    where `get` could not even look is the same text. `transcript_sessions` has no named target: an empty archive is an empty list with
    a note, and a filter that matches nothing in a non-empty archive is an empty list without one.

## 1. The read API

`ITranscriptStore` gets one method, `BeginReads()`, returning an `ITranscriptReads : IDisposable` that holds **one deferred read
transaction** for the whole call (as `BeginSearchReads` does for a search), so a `forget` or an ingest commit between two of its
queries cannot pair a session with another session's chunks. Dispose ends the transaction. Nothing it does writes. It is a new
interface and a new file (`TranscriptReads.cs`); `ITranscriptSearchReads` is not changed.

```
bool        AnyChunk()                                                    // an EXISTS over tx_chunk, as the search's
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
- `SessionFilter`: `Project` (exact key, as the search's), `Since` (at or after) and `Before` (strictly before), both `DateTimeOffset?`, each
  compared with `COALESCE(ended_at, started_at)` **as text against `TranscriptTime.Iso(value)`** (the archive writes every time
  through `TranscriptTime.Iso`: fixed-width UTC with 7 fractional digits and `Z`, which sorts as text), so a bound keeps full
  precision and paging with `before` set to the oldest listed session's `endedAt` (else `startedAt`) loses a session only when two
  last activities are identical to the 100 ns; a session with neither time matches neither bound, so it is listed only while fewer than
  `limit` sessions match (it sorts last), for example after narrowing with `project`; the note of an overflowing list says to page with
  `before`. `IncludeAutomated`.
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
(`SplitSession`); `from_exchange` below 0; `to_exchange` below `from_exchange`; a range wider than 200 exchanges; `context`,
`max_tokens`, `from_exchange` or `to_exchange` out of range or not an integer (a number with a fractional part is refused, not
truncated: `ArgumentParsing.ReadOptionalInt` truncates and is used by other tools, so two **new** strict readers are added beside it,
one for an `int` and one for the `long` chunk `id`, and only these tools use them); a chunk `id` below 1. The width of a range is
computed in `long`.

**Resolving.** `id` finds the chunk, then its session; the requested range is that chunk's exchange. An id with no chunk, and a
session with no row, are **error results** (`isError` true, plain text), exactly:

- `no archived chunk has id <N> (it may have been forgotten); use transcript_sessions to list sessions`
- `no archived session <ref>; use transcript_sessions to list sessions`

When the archive holds no chunk at all (no file, or no row in `tx_chunk`: `AnyChunk` is false) the same text is followed by
` (nothing is archived yet; run total-recall transcripts backfill)`. The named target is checked first, so a forgotten session is
reported unknown whatever else is archived (call 12). A `get` on an archive file that does not exist gives the same error with the
suffix and creates nothing (the archive is opened with `create: false`).

**The window.** The requested exchanges are the stored exchanges in `[from, to]`, ascending. Their context is the `context` nearest
stored exchanges before the first and the `context` nearest after the last (call 4); none at a session's ends. If the range holds no
stored exchange the answer has no exchanges, no context and a note saying which numbers were asked and how many exchanges the session
has; it is not an error.

**The text of an exchange.** All chunks of the exchange, ascending part, each with its header line cut (`ChunkText`), joined with a
blank line, with the continuation prefix of every part after the first removed: a part after the first starts with
`User (continued): <the question, up to 300 characters>` and a blank line (`TranscriptChunker.ContinuedLabel`), repeated only so a
part is searchable on its own. The prefix is removed from a later part only when the exchange's chunk type is `dialogue` and its **first** part body begins with the
chunker's `User: ` block (the unit had user text, which is the only case the chunker adds the prefix) and the later part begins with
the label exactly; any other text, a compaction summary that happens to start with those words included, is returned as stored. The
label is private to the chunker today and becomes `internal` so the reader shares it. The text is returned as stored (it is already redacted; nothing is redacted or altered again). The
exchange is reported with its number, its `ts`, its chunk type and whether any chunk has an error.

**The budget.** The budget is `max_tokens`, counted as call 3. Fill in this order and stop at the first exchange that does not fit:
(1) the requested exchanges ascending; (2) the context, alternating one before and one after (before first), nearer before farther,
until `context` on each side are in or the budget is full. (An exchange of the first step that does not fit ends the answer there, and the answer points to it as `nextFromExchange`; a
**first** requested exchange that alone is over the budget is cut on a character-safe boundary so that text and marker fit the budget
(call 3) and ends with `[cut: raise max_tokens to read the rest]`; nothing follows it, no context included.) Reading stops as soon as the budget is known to be full, so a window over a huge session
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
  "nextToExchange": 14,                          // with it: the last exchange asked for (the answer's end of the asked range)
  "note": "..."                                  // only when it says something
}
```

`truncated` is true when any **requested** exchange was left out or cut, or any context exchange was left out for the budget (not
when a session simply has no more exchanges). `nextFromExchange` and `nextToExchange` appear together, only when requested exchanges after the last one returned
were left out; **reading on is `session` with `from_exchange: nextFromExchange`, `to_exchange: nextToExchange` and `context: 0`**
(`context: 0` because the earlier answer already ended on the exchange just before; with the default context it would be returned
again as context). The note spells out exactly those arguments. A note also says what was cut, and "raise `max_tokens`" for a cut
exchange.

A **cut** first exchange ends the answer (nothing follows it, no context included), and the pointer is decided the same way: when
stored requested exchanges follow the cut one, `nextFromExchange` is the next stored requested exchange after it and
`nextToExchange` the asked end, and the note says exchange N was returned cut, is not read on, and that `max_tokens` must be raised
to read it whole. The pointer is absent only when no requested exchange remains. So reading on always reaches every requested
exchange: a cut exchange is the only one that is not complete, and the note says which.

## 3. `transcript_sessions`

**Arguments** (the stub's schema plus `before`): `project` (exact key), `since` and `before` (ISO-8601 date or date-time, parsed by
`TranscriptSearchService.ParseSince`; `since` is at or after, `before` strictly earlier, both on the session's last activity (calls 7
and 11); `before` not after `since` is not an error, it simply matches nothing; the error of a malformed value names its argument, so
`ParseSince` gets an argument-name parameter, default `since`, and the handler passes `before`), `limit` (1 to 100, default 20), `include_automated`
(default false). Argument errors as above, for either tool. A call with no `arguments` (or null) is an empty argument set for
`transcript_sessions` (every argument is optional, as `MemoryRecentHandler` treats it); for `transcript_get` it is the "neither form"
argument error. `before` is new in the schema (call 11), and the description of `since` becomes "active since".

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
chunk count. `title` is the label and may be null. With no chunk archived (`AnyChunk` false) the answer is `total` 0, no sessions and the note
`nothing is archived yet; run total-recall transcripts backfill`; a filter that matches nothing in a non-empty archive is the same
without a note. When `more` is above 0 the note says that only the newest `limit` are listed and to narrow with `project`, `since` or
`before`. Order and filters are those of section 1.

## 4. The handlers and the composition

- `TranscriptGetHandler` and `TranscriptSessionsHandler` take `(Func<bool> isActive, TranscriptReadService? service = null)`. The
  gate is read **once per call**: off gives Part 1's off answer (`TranscriptToolAnswers.Off`, unchanged); on with no service gives
  the not-set-up answer, one shared constant `OnNotSetUp`: `{"enabled":true,"available":false,"hint":"The transcript archive is not
  set up in this server."}` (the search's `OnSearchNotSetUp`, whose text names only the search, is renamed and reworded to it, and
  `TranscriptToolHandlerTests` follows); on with a service runs it. `TranscriptToolAnswers.OnNotAvailable` and `For` go away (nothing answers "not available in this version yet"
  any more); the tests and docs that pin that text change with it.
- Arguments are parsed in the handler (`ArgumentParsing`, with the two strict readers of section 2 and `ReadOptionalBool`, which moves there from
  `TranscriptSearchHandler` as `internal` so the three handlers share it; a missing `arguments` is as section 3 says, and `arguments`
  present but not an object is an argument error for both tools, as for the search). An
  `ArgumentException` is the caller's error. An archive that cannot be opened or read is an **error result**, written to stderr with
  the `[total-recall] transcript_get` prefix, never an exception that escapes.
- `TranscriptReadService(Func<TranscriptReadContext?> context, TimeProvider? time = null)`, with an internal open seam for tests as the
  search service has. `TranscriptReadContext` holds the `TranscriptLocation` only (no settings, no embedder). It opens the archive
  per call with `TranscriptStore.Open(location, create: false)`, reads in one `BeginReads()` and closes before returning. Opening may
  apply a pending archive migration (Part 3 documents this); nothing else is written, and the memory store is never touched.
- `ServerComposition` registers the new handlers in `OpenSqlite` and `OpenCortexCore`, beside the search handler, with a context
  built from the resolved location (the search's `TranscriptSearchContextFor` is split so the location part is shared). `OpenPostgres`
  registers no archive tool today and still registers none. The tool count (54), the announce files and the tool names do not change;
  the two descriptions lose "not available in this version yet"; the `get` schema gains `max_tokens` and the `sessions` schema gains
  `before` (and its `since` becomes "active since"). `catalog.json` is regenerated.
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
2. `context` 0 returns the exchange alone; `context` 10 near a session's start or end returns what exists, with `truncated` false (the
   test sets `max_tokens` high enough, or uses short fixture exchanges, so the budget is not what ends the window).
3. A range by `session` with `from_exchange` and `to_exchange`; a range partly outside the session; a range over a gap (the gap is
   skipped, context still counts stored exchanges).
4. A multi-part exchange: the parts are joined, no header line and no `User (continued):` prefix remains, the text equals the chunker's
   own parts rejoined; a multi-part **compaction summary** whose first part begins with `User: ` and whose later part begins with
   the words `User (continued): ` is returned as stored.
5. The budget: a window over `max_tokens` keeps the requested exchanges and drops far context first, alternating before and after;
   `truncated` true, no `nextFromExchange`; a requested range over the budget ends at the last whole exchange with `nextFromExchange`
   (reading on from it returns the rest, and no exchange appears twice); a first exchange alone over the budget is cut, marked, with
   the note; `approxTokens` is at most `max_tokens` (a cut exchange and its marker included). **Reading on** issues exactly the call
   the answer names (`from_exchange: nextFromExchange`, `to_exchange: nextToExchange`, `context: 0`), repeated until no
   `nextFromExchange` remains, and asserts that the union of the requested exchanges equals the original range with none twice. One
   range holds an **oversized exchange in the middle** (alone over the budget): reading on cuts it, the note says so, the pointer
   still moves past it, and every later requested exchange is reached.
6. A named session or chunk id of an **automated** session is readable; a forgotten session or chunk gives the exact error results
   of section 2; a session name that is malformed or both or neither form of arguments is an argument error; `context` 11,
   `max_tokens` 100, a range of 201, `context: 1.5`, `max_tokens: 200.9`, `from_exchange: -1` and `id: 0` are argument errors (a JSON number whose value is whole, such as
   `2.0`, is accepted); a `transcript_get` call with no `arguments` is the "neither form" error. For `transcript_sessions`: `limit` 0,
   101 and 1.5, a malformed `since`, a malformed `before` (the text names `before`), and a non-boolean `include_automated` are
   argument errors, and a call with no `arguments` lists sessions.
7. `transcript_sessions`: newest first (with a session without times last), the project filter, `since` (active-since, boundary
   included) and `before` (strict) alone and together, `limit` with `more` and `total` and the narrowing note, automated hidden with
   `automatedHidden`, shown with `include_automated`, a reset session (no chunks) absent, the title as label, an empty or missing
   archive gives `total` 0 and the note, and a `project` that matches nothing in a non-empty archive gives `total` 0 and no note.
   `get` on an absent archive file and on a file with no chunk gives the unknown-target error with the nothing-archived suffix, and
   creates no file.
8. Off: both tools answer the Part 1 off text, read the setting per call (`enable`/`disable` apply without a restart) and open no
   file; on with no service answers not-set-up. The registry's tool list and `catalog.json` agree (the drift check).
9. Read-only and consistent: a read while another connection holds an ingest transaction succeeds; no row count changes; a forget
   between two calls gives the error result on the second, **including when the forgotten session was the only archived one** (the
   unknown-session text, with the nothing-archived suffix).
10. The speed of the reads on a synthetic archive of 100,000 chunks and 2,000 sessions: `transcript_get` of a window and
    `transcript_sessions` with filters each answer in under one second (the test asserts that bound on the median of five calls; the
    measured values are recorded in the PR).
11. **No embedding provider is touched**: with `[embedding] provider = openai` (and no `allow_remote_embedding`) and a recording
    `IEmbedder`, and again with no local model files present, both tools answer normally and the recorder shows zero `Embed` and
    zero `EmbedQuery`/probe calls.

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
- A grep (case-insensitive) for `not available in this version yet`, `not built yet`, `still stubs`, `waits for Part 4`,
  `until .transcript_get. arrives`, `transcript_get. will take`, `later release`, `until they ship` and `Part 4` finds every stale
  line in `docs/`, `AGENTS.md`, the `AGENTS.md` files under `src/`, `PRIVACY.md` and the handler descriptions. The hits in
  `StatusCommandTests.cs` and `TranscriptsCliTests.cs` that pin the absence of the old search status line are intentional, and so
  are hits about `retention_days` ("reserved for a later release") and the default scope ("a later release adds a default of the
  current project"), which belong to Parts 5 and 8: a hit is changed only when it says `transcript_get` or `transcript_sessions` is
  missing, unbuilt or coming.

## Deviation from the parent design

Listed so the person sees each at sign-off. None edits the parent.

1. **A `max_tokens` argument** is added to `transcript_get` (the parent's tool line has none; call 2).
2. **The budget is `ceil(characters / 4)`** of the text, and `context` counts stored exchanges (calls 3 and 4); the parent says only
   "about 2,000 tokens" and "a context size".
3. **`transcript_sessions` hides sessions that hold nothing and adds `total`, `more` and `automatedHidden`** (calls 6 and 8); the
   parent lists `project`, `since`, `limit` and `include_automated`.
4. **A named session or chunk is read whatever its kind** (call 5); the parent applies `include_automated` to the tool.
5. **No default project** (call 9): the parent's "project and current session" resolution is written for the search.
6. **`transcript_get` answers more than the parent's "surrounding exchanges, a token budget and a `truncated` flag"**: `requested`
   marks which exchanges were asked for, `approxTokens` reports the budget used, `nextFromExchange` and `nextToExchange` say how to
   read on, and an unknown session or chunk is an error result (call 12); the first exchange alone over the budget is cut (call 3).
7. **`since` on `transcript_sessions` means the session's last activity** (call 7) and a **`before`** argument is added (call 11); the
   parent's line has `since` only and does not say what it compares.

## Acceptance (the parent's Done when, and every criterion of the ticket, mapped)

The parent's three lines: "Given a hit from search, `transcript_get` returns the surrounding exchanges within the budget and flags
truncation": tests 1, 2, 3 and 5. "`transcript_sessions` lists a project's sessions newest first with their titles and hides
automated ones unless asked": test 7. "Both answer that the archive is off while it is off": test 8.

The ticket's criteria, in the order of the issue (number: where):

| Criterion | Test |
|---|---|
| 1 a hit by id, default context | 1 |
| 2 context 0, and 10 near the ends | 2 |
| 3 a session range, gaps, past the end | 3 |
| 4 a multi-part exchange | 4 |
| 5 a window over the budget | 5 |
| 6 a requested range over the budget, reading on | 5 |
| 7 a first exchange alone over the budget | 5 |
| 8 an automated session readable; unknown or forgotten target | 6 and 9 |
| 9 invalid arguments (either tool) | 6 and 7 |
| 10 the session list: order, fields, project, `since`, `before` | 7 |
| 11 the limit, `total`, `more` | 7 |
| 12 automated hidden, `automatedHidden`, shown on request | 7 |
| 13 a session with no readable chunks | 7 |
| 14 off, follows enable and disable; on but empty | 8 and 7 |
| 15 read during an ingest; forget between calls | 9 |
| 16 no provider touched, no model needed | 11 |
| 17 the speed at 100,000 chunks | 10 |
| 18 the documentation, the catalog and the registered tools | 8 and the stale-text grep of section 7 |

## Left to the builder

The names of the DTOs and of the new read types; the SQL of `ExchangesBefore` and `ExchangesAfter` (an index on
`(session_id, exchange_start, part)` already exists through the unique key) and of the session list; how the window reads stop early;
the exact wording of notes; whether `TranscriptReadService.Get` and `Sessions` share a helper for the empty-archive check with the
search service; the fixture content; the cut of a first exchange on a safe boundary (`TextCut`).
