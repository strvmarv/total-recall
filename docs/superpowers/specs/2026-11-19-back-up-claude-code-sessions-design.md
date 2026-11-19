# Back up Claude Code sessions, with secrets redacted (Part 2 of the transcript archive): design

Parent: #145, Part 2 (docs/superpowers/specs/2026-11-18-transcript-archive-design.md)

Date: 2026-11-19. Status: draft for review. This is the sub-spec of Part 2 of the transcript archive. It follows the parent's
"Shared decisions" and adds the detail that belongs to this part. Part 1 (the opt-in switch, the store, `forget`, isolation from sync)
is merged; this part fills the store.

## Goal

With the archive enabled, one command, `total-recall transcripts backfill`, reads Claude Code's session files and stores their dialogue
in the archive, redacted, as text chunks. The history the host will delete is kept. Nothing finds it yet: no tool searches it, there are
no vectors (`embedded` stays `0`), and nothing runs on its own (the `session_start` pass is Part 3). What this part ships on its own is
**preservation**.

In scope, as the parent's Breakdown lists them: the Claude Code adapter and the common record; the kept and dropped rules and the
compaction markers; session kind; project identity; the chunker; span-returning redaction with the detector set of decision 6, applied
before chunking and to tool stubs and error text; per-session watermarks with the resume rule and the exclusion check; `transcripts
backfill` and the counts in `status`; the fixture corpus and harness. Out: vectors and embedding, every tool's behaviour, the
`session_start` pass, the live session and its marker, the breadcrumb, pi, `prune`, `rescan`, `reproject` and the re-embed.

## What exists today (Part 1) and what this part adds to it

Part 1 gives: the `tx_session`, `tx_chunk` (+ `tx_chunk_fts` and its triggers), `tx_forgotten`, `tx_state` tables; `TranscriptStore`
(`Open(location, create)`, `Status()`, the `Forget*`/`Unforget*` methods, `IsExcluded(host, id, projectKey, cwd)`,
`IsBeforeCutoff(ts)`); the `transcripts` command group; the sentinel; `[transcripts]` settings. **It has no write path for sessions
or chunks**: tests insert with raw SQL.

This part adds, all inside the `tx_` world and `TotalRecall.Infrastructure.Transcripts` (plus the CLI command):

- a **second archive migration** (`TranscriptMigrationRunner`, own list, own version table): `tx_session.settled_user_turns INTEGER NOT
  NULL DEFAULT 0` (section 7) and an index on `tx_session(project_key, kind)` for the counts of `status`;
- store methods for the ingest (section 8);
- the adapter, the exclusion-aware ingester, the exchange builder, the chunker, the redactor, the kind and project rules (sections 2 to 7);
- `transcripts backfill`, and the per-project and per-kind counts in `transcripts status` (section 9);
- the fixture corpus and harness (section 10) and the documentation (section 11).

Nothing in the main migration chain, the memory or KB tables, `SessionLifecycle` or the MCP tool list changes.

## Calls made here (the person can overrule them at sign-off)

1. **Claude Code's session directory** is `$CLAUDE_CONFIG_DIR/projects` when that variable is set and non-empty, else
   `~/.claude/projects` (the `UserProfile` folder, as `ClaudeCodeUsageImporter` does). The existing importers ignore `CLAUDE_CONFIG_DIR`;
   this adapter honours it because Claude Code does, and leaves the others alone.
2. **`backfill` is not terminal-gated.** The parent gates only `enable`, `forget`, `unforget` and `prune` (decision 10): backfill stores
   what the already-enabled archive is meant to store, and it is the way scripts and `cron` keep the archive current. It refuses unless
   the archive is on.
3. **Session kind uses the file's `entrypoint` as well** (section 6): in the author's history 88 of 100 sessions carry `entrypoint:
   sdk-py` (reviewer and subagent runs), a stronger signal than the prompt text. The parent leaves the rule to the builder ("the builder
   refines it").
4. **Each stored text is capped at 100,000 characters** (after redaction), so a pasted multi-megabyte log cannot turn one prompt into
   thousands of chunks; the cut is marked `[... N more characters not stored]`. The parent sets no cap.
5. **The chunk header names the project by its key** (`strvmarv/total-recall`, or the folder name), not by a path. The parent writes
   `project <path>`; the key is what every later scope and filter uses, and a path would put a user's directory layout into every
   embedding.
6. **Cutoff comparison is "at or before"**, as decision 7 says. Part 1's `IsBeforeCutoff` is strict (`<`) and has tests that pin that, so
   it is left alone; ingestion reads the cutoff once per session pass through a new `IngestNotBefore()` and compares itself.
7. **Fake secrets in fixtures are assembled at test time**, never written as literals in committed files, so repository secret scanning
   and push protection do not flag the corpus (section 10).

## 1. Data flow

```
backfill (CLI)  ->  ClaudeCodeAdapter.EnumerateSessions()  ->  for each session file (newest first):
   exclusion + unchanged checks  ->  ReadHead  ->  project identity  ->  exclusion check with the key  ->
   ReadRecords(from the watermark)  ->  ExchangeBuilder (drop rules, cutoff filter, redaction, units)  ->
   TranscriptChunker  ->  store.ApplyIngest(...)  (one transaction, exclusion re-checked inside it)
```

The adapter is pure (it reads files and never writes anything). `TranscriptIngester` is the only code that touches the store.
`TranscriptBackfill` (a loop around the ingester over all sessions of one adapter) is what the command calls and what Part 3's
`session_start` pass will call with a budget; both take a `CancellationToken` and an optional progress callback. A failure on one file
is logged through `ExceptionLogger.LogChain`, counted, and never stops the others.

## 2. The adapter and the common record (decision 2)

```csharp
internal interface ITranscriptAdapter            // namespace TotalRecall.Infrastructure.Transcripts
{
    string Host { get; }                                       // "claude-code"
    bool Detect();                                             // cheap: the projects directory exists
    IEnumerable<SessionSource> EnumerateSessions();            // sources, unordered
    SessionHead ReadHead(SessionSource source);                // cheap: first lines only
    IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, CancellationToken ct);
}
record SessionSource(string HostSessionId, string Path, long Size, long MtimeMs);
record SessionHead(string? Cwd, string? Entrypoint, DateTimeOffset? FirstTimestamp, string? Fingerprint /* null: no complete first line yet */);
record TranscriptRecord(RecordKind Kind, long StartOffset, long EndOffset, DateTimeOffset? Ts, string? Text, string? ToolName);
enum RecordKind { UserText, AssistantText, ToolStub, ToolError, CompactionBoundary, CompactionSummary }
```

(Names and exact shapes are the builder's; what is fixed is the content below.)

**Enumeration.** `<projects>/<encoded-cwd>/<id>.jsonl`, one directory level only. `<id>` (the file name without `.jsonl`) is the
`host_session_id` and must match `^[A-Za-z0-9._-]{1,128}$` (anything else is skipped). Not read: nested directories (subagent files under
`<session>/subagents/`), symbolic links (files or directories) and anything that is not a regular file. A file whose head has no complete
line yet is skipped this pass (a fresh session being written).

**Reading.** Streamed as **bytes**, line by line (split on `0x0A`), never loaded whole, because the watermark is a byte offset. A
trailing partial line (no `0x0A`) is never consumed and never parsed. A line over 16 MiB is skipped and counted (`skippedLines`) rather
than parsed. A line that is not valid JSON, or a record of an unknown type, is skipped. Each record yields zero or more
`TranscriptRecord`s with the byte offsets of its line. `ReadHead` reads at most the first 200 complete lines and returns the first `cwd`,
the first `entrypoint`, the first `timestamp`, and `Fingerprint`: the SHA-256 (hex) of the first complete line
(a first line over 16 MiB has no fingerprint, and the session is skipped and counted).

**Claude Code record mapping** (verified on current Claude Code files, 2026-11-19: records carry `type`, `uuid`, `timestamp`, `cwd`,
`sessionId`, `entrypoint`, `isMeta`, `isSidechain`; message content is a string or an array of blocks):

| Record | Result |
|---|---|
| `type: "user"`, not `isMeta`, not `isSidechain`, not `isCompactSummary`, `message.content` a string or with `text` blocks | `UserText` (the text after the dropped-text rules of section 3; no record if nothing is left) |
| the same with `tool_result` blocks, `is_error: true` | `ToolError` (the block's text: a string, or the concatenation of its `text` blocks) |
| the same with `tool_result` blocks, not an error | nothing (successful tool output is dropped, decision 3) |
| `type: "assistant"`, not `isSidechain`, `text` block | `AssistantText` |
| `type: "assistant"`, `tool_use` block | `ToolStub` (`ToolName`, and the stub text of section 3) |
| `thinking` blocks, `image` blocks, any other block type | nothing |
| `type: "system"`, `subtype: "compact_boundary"` | `CompactionBoundary` |
| a `user` record with `isCompactSummary: true` | `CompactionSummary` (its text; a string or `text` blocks) |
| `isMeta: true`, `isSidechain: true`, and every other record type (`attachment`, `last-prompt`, `file-history-snapshot`, `system` with another subtype, `api-request*`, `cost-state`, ...) | nothing |

`Ts` is the record's `timestamp` (ISO 8601, any offset, normalised to UTC), or null when absent.

## 3. What is kept (decision 3)

Kept: user text, assistant text, one-line tool stubs and the error output of a failed tool call. Dropped: everything else in the table
above, and inside user and assistant text:

- these blocks, whole, with their tags: `<system-reminder>`, `<local-command-stdout>`, `<local-command-stderr>`, `<local-command-caveat>`,
  `<command-message>`, `<EXTREMELY_IMPORTANT>` (a block with no closing tag is dropped to the end of the text); the builder adds the
  hosts' `<ide_*>` context tags the fixtures show;
- a slash command is kept as plain text: `<command-name>/develop</command-name><command-args>148</command-args>` becomes `/develop 148`.

**Tool stub** text: `[<ToolName>: <detail>]`, where the detail is one field of the tool's input chosen by tool name (`Bash`: `command`;
`Read`, `Edit`, `Write`, `NotebookEdit`: `file_path`; `Grep`, `Glob`: `pattern`; `WebFetch`: `url`; `WebSearch`: `query`; `Task` and
`Agent`: `description`; any other tool: the first string-valued field, in document order), with newlines folded to spaces and runs of
whitespace collapsed. **Redaction runs on that whole field first**, then the result is cut to 160 characters (section 4's rule that a cut
never lands inside a marker); a tool whose chosen field is missing or empty gives `[<ToolName>]`.

**Error text**: `[error: <text>]`, redacted whole, then cut to 500 characters, with newlines kept (a stack trace is what is searched for).
An error whose text is empty gives `[error]`.

**Text cap** (call 4): a user or assistant text, a compaction summary or an error is cut, after redaction, at 100,000 characters with
`[... N more characters not stored]` appended (N counts characters of the redacted text).

## 4. Redaction (decision 6)

**API.** `SensitiveContentScanner.Redact(string? text)` returns `RedactionResult(string Text, int Count, IReadOnlyList<string> Categories)`
(null or empty in, the same out with count 0). The existing `Scan`, `ScanSecrets` and `Detectors` keep their behaviour and tests
unchanged; `Redact` reuses the token regex (one source for the shapes). `public const int RedactionVersion = 1`, written to every chunk's
`redaction_version`. A marker is exactly `[redacted:<category>]` with one of these categories:

| category | what it matches | what is replaced |
|---|---|---|
| `token` | the existing token shapes: `ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_`, `github_pat_`, `glpat-`, `xox[baprs]-`, `sk-`, `AKIA` | the token |
| `private-key` | `-----BEGIN [A-Z ]*PRIVATE KEY( BLOCK)?-----` | from the BEGIN line to the end of the matching `-----END ...-----` line, **or to the end of the text when the END line is missing** |
| `bearer` | `Authorization: <scheme> <value>` (also `=`; quotes allowed; scheme `Bearer`, `Basic`, `Token`, `Digest` or none) and a bare `Bearer <value>` | the value (at least 8 characters for `Authorization`, 16 for a bare `Bearer`) |
| `url-credentials` | `scheme://user:password@host` | the password only (the user name and the host stay searchable) |
| `secret-assignment` | an assignment (`=` or `:`, quoted or not, also JSON `"name": "value"`) to a secret-named variable: names ending in `_KEY`, `_TOKEN`, `_SECRET`, `_PASSWORD`, `_PASSWD`, or equal to `PASSWORD`, `PASSWD`, `SECRET`, `TOKEN`, `API_KEY`, `APIKEY`, `PASSPHRASE`, `CLIENT_SECRET`, `ACCESS_KEY`, `AUTH_TOKEN` (any case) | the value |

Rules that every detector follows:

- **Overlaps merge** (the union of the spans is replaced by one marker, the category of the first span).
- **A value that is clearly not a secret is left alone**, so shell and config prose stays searchable: shorter than 6 characters, or
  starting with `$`, `<`, `{`, `%`, or a variable reference or placeholder such as `${TOKEN}`, `$t`, `<your-token>`, `{{ secret }}`.
  (`GH_TOKEN="$t" gh api ...`, which fills this repository's own history, is not redacted.) A table test pins positive and negative
  examples per category; the builder writes the exact patterns.
- **All quantifiers are bounded and every regex has a match timeout** (1 second). A regex that times out **fails closed**: the whole text
  becomes `[redacted:unprocessable]` (count 1); nothing from it is stored. Interpreted regexes only (NativeAOT: no `RegexOptions.Compiled`).
- Redaction runs on the **full, untruncated text, before any truncation and before chunking** (so a token cannot be cut or split), on
  every text this part stores: user and assistant text, stub details, error text, compaction summaries, and the session title (which is
  derived from already-redacted text).
- **Cutting.** One function cuts text to a limit and never lands inside a marker: when the limit falls inside `[redacted:...]`, the cut
  goes **before** the marker and `...` is appended. This is what keeps a token that straddles character 160 or 500 from leaving a prefix.
- Emails are not redacted (parent decision 6).

Residual risk, documented in `docs/transcripts.md`: only the listed shapes are detected. Not detected: a password or key written in prose
or in a variable whose name is not on the list, a secret in a format not listed (cloud keys other than `AKIA`, JWTs, database passwords
outside a URL or a listed variable), anything a user pastes in a different encoding, and a secret split across two messages.

## 5. Exchanges and the chunker (decisions 3 and 5)

**Units.** Records are folded, in file order, into numbered **units**; the number is the `exchange_start`/`exchange_end` of its chunks,
counted from 0 over the session as stored. A unit is one of:

- a **dialogue exchange**: it starts at a `UserText` record and takes every following `AssistantText`, `ToolStub` and `ToolError` record
  until the next `UserText`, a `CompactionBoundary` or a `CompactionSummary`;
- a **summary unit**: one `CompactionSummary` record;
- an assistant-only run (records after a cutoff, or at the start of a resumed file, that have no `UserText` before them) is a dialogue
  exchange with no user part.

A `CompactionBoundary` closes the open unit and starts nothing. A unit with no kept content is not a unit (no number is consumed).
Each unit remembers the `StartOffset` of its first record and the `Ts` of its first record with a timestamp.

**Cutoff filter** (decision 7), applied to records **before** they form units, with the cutoff read once per session pass
(`IngestNotBefore()`): a record with `Ts <= cutoff` is dropped. A record without `Ts` takes the `Ts` of the record before it in the file
(for the first record after the watermark, the session's `started_at`; if none is known and a cutoff exists, it is dropped). So an
exchange whose question is before the cutoff and whose answer is after it contributes the answer only.

**Chunk text.** A chunk's `content` is a header line, a blank line and a body:

```
claude-code session <first 8 characters of the id> | project <project_key> | <yyyy-MM-dd of the unit's first Ts, UTC> | <kind> | <dialogue|compaction-summary> | exchanges <a>-<b>

User: <the user text>

Assistant: <text>

[Bash: <detail>]

[error: <text>]
```

Blocks appear in record order, separated by one blank line, each starting with its label (`User:`, `Assistant:`, a stub, an error).
A summary chunk has the header and the paragraphs of the summary, no labels. `has_error = 1` when the chunk contains an error block.
`chunk_type` is `dialogue` or `compaction-summary`; `ts` is the unit's first `Ts` (ISO 8601 UTC); `redaction_version` is 1; `embedded` is 0.

**Size.** The body of a chunk is at most **1,800 characters** (the header is not counted). A unit whose blocks do not fit is split into
parts (`part` = 0, 1, 2, ... within the unit; a unit that fits is one part, part 0) at block boundaries; a block longer than 1,800
characters is split on paragraph boundaries (`\n\n`), then on line boundaries, then, if a line is still too long, on whitespace, and
only as a last resort at the limit (never inside a marker). **Every part after the first begins with the question repeated**: a line
`User (continued): <the user text cut to 300 characters>`, which counts toward the 1,800. A unit with no user part repeats nothing. A
`part` has its own header with the same unit numbers.

The chunker is a pure function: `Chunk(unit, header facts) -> list of chunks`.

## 6. Session kind (decision 4) and project identity (decision 8)

**Kind** is `interactive` or `automated` and is decided from the file alone (it is stored, so it can be re-evaluated). The first rule
that applies wins:

1. the session's `entrypoint` (from the head) starts with `sdk` (case-insensitive) -> `automated`;
2. exactly one real user turn so far (a `UserText` record) and either its text starts with a known automation opening (a list in code,
   from the prototype: `Review this change for security`, `You previously flagged`, `Scope:`, `You are reviewing`, `Read the full review
   brief`, `Round <n>`) or is longer than 1,500 characters (measured on the redacted text) -> `automated`;
3. otherwise `interactive`.

The number of real user turns is `settled_user_turns` (the turns in the units before the watermark, kept on the session row; section 7)
plus the turns in this pass. Once there are two, rule 2 no longer applies, so a session can move from `automated` to `interactive`
when it grows, never the other way (rule 1 aside). A session with no kept content stores nothing, so it has no kind.

**Title.** The first real user turn of the session, redacted, newlines folded to spaces, cut to 120 characters (marker-safe), set when a
session is first stored or re-read from the start and kept after that (a session with no real user turn has a null title).

**Project identity.** `project_key` and `project_key_source` are computed once per pass from the session's `cwd`:

1. the `cwd` of the head (the first record that carries one). With none, the `cwd` is the encoded project directory name decoded as
   `ClaudeCodeUsageImporter.DecodeProjectDirName` does (lossy, documented) and the key source is `path`.
2. If the directory exists, `ProjectResolver.Resolve(cwd)` gives the key (`owner/repo` from the remote, else the repository root's
   folder name, lower case; the resolver already follows a worktree's `.git` file to its repository).
3. If it does not exist (deleted worktrees are the common case for historical sessions): a path with a `/.claude/worktrees/<name>` or
   `/.zoid/worktrees/<name>` segment is cut back to the part before that segment (the repository that held the worktree); the resolver
   runs on that root if it exists; otherwise the key is the last path segment of the root, lower case.
4. `project_key_source` is `remote` when the key contains `/`, else `path` (a folder name never does). A null or empty result falls back
   to the folder name of the `cwd`, and to `unknown` when there is no `cwd` at all.

`cwd` is stored on the session (the key is recomputable without a migration). The adapter and the key rule do not run before the
cheap exclusion check of section 7, step 2.

## 7. Per-session ingest: watermark, resume, exclusion

One `TranscriptIngester.IngestSession(source)` does, for one source file:

1. **Exclusion by id.** `store.IsExcluded(host, id, null, null)`: when true the file is **not opened**; count `excluded`.
2. **State.** `store.GetIngestState(host, id)` (the session row, or null). If a row exists and `size == source_size` and `mtime ==
   source_mtime`, the file is unchanged: count `unchanged`, stop (no read).
3. **Head.** `ReadHead`. No fingerprint -> count `skipped`, stop. Compute the project identity (section 6) and call `store.IsExcluded(host,
   id, key, cwd)`: when true, count `excluded`, stop. (This is the first read of the file; nothing from it is stored.)
4. **Mode.** No row: **fresh** (start 0, cutoff-filtered). A row whose `source_fingerprint` differs from the head's, or whose
   `source_size` is larger than the file's current size (the file was rewritten, replaced or truncated): **rewritten** (start 0, replace
   every chunk of the session). Otherwise **resume**: start at `ingested_offset`.
5. **Read** from the start offset to the end of the last complete line, build units, chunk them, derive kind and title (section 6).
   - The **watermark** (`ingested_offset`) is the `StartOffset` of the first record of the **last unit**: the last unit may still receive
     records, so each pass re-reads from there and rebuilds it. `exchange_count` is the number of the last unit + 1. The unit numbering of a
     resumed pass continues from `exchange_count - 1` (the number of the unit that is read again). `settled_user_turns` is the real user
     turns in all units before the last unit.
   - **A pass replaces all chunks of every unit it rebuilds as one set** (delete the rows with `exchange_start >= <first rebuilt unit>`,
     insert the new ones, so a changed number of parts leaves no orphan), in one transaction with the session row.
   - `source_size` is the offset after the last complete line consumed; `source_mtime` is the file's mtime in milliseconds read **before**
     the read began (a file that grew during the read is therefore read again next pass, never skipped); `source_fingerprint` is the head's.
     `started_at` is the first `Ts` of the session, `ended_at` the largest `Ts` seen, `source_path` the current path.
   - A pass over a session with no kept content stores nothing when there is no row; when there is a row it only updates the source
     fields.
   - **Headers carry the kind and the project key**, so when a resumed pass derives a kind or a project key that differs from the stored
     one (a session that grew from one long prompt to two turns; a repository that gained a remote), the pass is **redone as rewritten**
     (start 0, replace every chunk). Otherwise the chunks of the earlier units would keep a stale header and a grown session would not
     equal the same session ingested once.
6. **Apply** with `store.ApplyIngest(...)`: one `BEGIN IMMEDIATE` transaction that **re-checks the exclusion inside it** (a `forget`
   that committed between step 3 and here wins: the outcome is `Excluded`, nothing is written), re-reads the session row (two passes at
   once cannot lose an update), upserts the session row, replaces the chunks and commits. A later pass never resurrects a forgotten
   session.

Guarantees (each has a test): a second run changes nothing; a session grown across two passes at **any** cut point (a line boundary
inside an exchange, between exchanges, just after a compaction summary, in the middle of a line) equals the same file ingested once
(all `tx_session` columns except `id`, `source_mtime`, and all `tx_chunk` columns except `id`, compared as ordered sets); deleting the
source file afterwards leaves every chunk and the session row; a session forgotten before still has its file but stays out, and a
session that straddles the forget-everything cutoff contributes only its records after it.

## 8. Store additions (`ITranscriptStore`)

Names are the builder's; the behaviour is fixed.

- `SessionIngestState? GetIngestState(string host, string hostSessionId)`: `source_size`, `source_mtime`, `source_fingerprint`,
  `ingested_offset`, `exchange_count`, `settled_user_turns`, `kind`, `title`, `started_at`.
- `DateTimeOffset? IngestNotBefore()`: the global cutoff, or null.
- `IngestOutcome ApplyIngest(SessionIngest ingest)`: `SessionIngest` carries the session fields (host, id, project key and source, cwd,
  started/ended, kind, title, the source stat fields, offset, counts), `ReplaceFromExchange` (0 means every chunk) and the new chunks.
  Outcomes: `Applied(chunksWritten, chunksRemoved, created)` or `Excluded`.
- A second migration in the archive runner (see "What exists today"): `settled_user_turns` and the index.
- `Status()` gains the rows of `(project_key, kind, sessions, chunks)` (a left join, ordered by chunks descending), in addition to the
  existing totals.

All SQL is parameterised; the store keeps its own connection and `secure_delete`; every write goes through the store (the ingester never
opens SQLite itself).

## 9. The `backfill` command and `status`

`total-recall transcripts backfill [--host claude-code]` (`--host` is the only option; an unknown host is a usage error, exit 2; today
the only host is `claude-code`; Part 7 adds `pi`).

Order: parse arguments -> read the configuration strictly (a malformed config exits 1, as the other commands) -> the archive must be on
(`[transcripts] enabled = true`) and the storage mode must not be Postgres -> `TranscriptStore.Open(location, create: false)` (absent
tables mean the archive was never enabled) -> only then create the adapter and touch the host directory. **While the archive is off,
backfill prints that it is off and how to enable it, exits 1, and has read nothing from the host directory** (a test double adapter that
fails if it is ever enumerated proves it). It needs no terminal and takes no confirmation.

Output: a progress line every 50 sessions, then a summary: sessions seen, `new`, `updated`, `unchanged`, `excluded`, `empty` (nothing
kept), `skipped` (no complete first line, unreadable name), `failed` (an error on that file, logged), chunks written and removed. Exit 0
when no file failed, 1 when any failed (the others were still done), 2 for a usage error. Sessions are processed newest file first. A
Ctrl+C stops after the current session and prints the summary so far.

`transcripts status` keeps its lines and adds, under the totals, one line per `(project, kind)` with its sessions and chunks (at most 30
lines, then `and N more`). The line `search: not available in this version yet` stays.

## 10. Fixture corpus and harness (decision 13)

A committed corpus of **synthetic** Claude Code sessions (written for the tests; nobody's history) under
`tests/fixtures/transcripts/claude-code/`, plus a harness `ClaudeFixtureHome` that builds a temporary `projects/` tree from them:

| Fixture | Content |
|---|---|
| `decision` | interactive (`entrypoint: cli`), three turns, tool stubs, a successful tool result carrying a unique marker, a `system-reminder` block, a `thinking` block, an `isMeta` record, an `isSidechain` record, a slash command |
| `compaction` | turns, a `compact_boundary`, an `isCompactSummary` record (several paragraphs), more turns |
| `error` | a failed `Bash` call whose error text holds an exact string a later part will search for |
| `automated-sdk` | `entrypoint: sdk-py`, one long prompt |
| `automated-opening` | `entrypoint: cli`, one prompt starting `Review this change for security` |
| `secrets` | one planted secret per category (token shapes `ghp_`, `github_pat_`, `glpat-`, `xox`, `sk-`, `AKIA`; a private key; `Authorization: Bearer`; `https://user:pass@host`; `API_KEY=...`), also inside a tool stub, inside an error, in the first prompt (the title), a token that straddles character 160 of a stub and one that straddles character 500 of an error, and a private key whose END line is cut off |
| `worktree` | `cwd` under `/.../.claude/worktrees/<name>` of a directory that does not exist, and another session in a repository the test creates with a remote |
| `no-kept` | attachments, a sidechain, a meta record, a hook output: nothing to keep |
| `straddle` | timestamps arranged around a cutoff the test sets |

**Secrets are never literals in the corpus.** The fixtures use placeholders (`{{SECRET:github-token}}`, `{{SECRET:aws-key}}`, ...) and
the harness substitutes a fake that matches the detector (assembled in code from parts, for example `"ghp_" + new string('a', 36)`),
together with `{{CWD}}` and `{{ID}}`. The planted value is also available to the test, which asserts that **no substring of 4 or more
characters of it** (other than the marker's own text) is present anywhere in the stored `tx_session` and `tx_chunk` rows or in the
FTS shadow tables.

The harness can also append to a placed session (a line, a partial line, a whole exchange) to simulate growth, and delete a source.

## 11. Documentation

`docs/transcripts.md`: the status banner and the sections that say "nothing is stored yet" are corrected (the archive can be enabled,
backed up with `backfill`, inspected and emptied; it cannot be searched yet); new sections: what `backfill` reads and stores, what is
kept and dropped, **what is redacted and what is not (the residual risk of section 4)**, where Claude Code's files are
(`CLAUDE_CONFIG_DIR`), how resume works in two lines. `docs/commands.md`: the `backfill` row. `PRIVACY.md`: the stored-data row now says
that after `backfill` the archive holds the dialogue of the user's Claude Code sessions, locally, with known secret shapes redacted, and
what is not covered; the retention paragraph says nothing the host deletes later is removed from the archive. `docs/configuration.md`,
`README.md` and `CHANGELOG.md` (unreleased, internal) are corrected where they say nothing is stored. The enable confirmation text and
the line that `enable` prints (`EnableCommand`: "Nothing is stored yet in this version") are corrected to match, with their tests. The
announcement of the feature stays with the release that completes Parts 1 to 3; nothing here is announced.
`src/TotalRecall.Infrastructure/AGENTS.md` (the archive migration list) and the root `AGENTS.md` table get the new locations.

## Deviation from the parent design

- Session kind adds the `entrypoint` rule (call 3). The parent leaves the rule to the builder.
- The cutoff is compared "at or before" inside the ingester through a new `IngestNotBefore()`; Part 1's strict `IsBeforeCutoff` is
  unchanged (call 6).
- A cap of 100,000 characters per stored text (call 4) and `CLAUDE_CONFIG_DIR` (call 1).
- The chunk header says `project <key>`, not `project <path>` (call 5).
- A second archive migration adds `settled_user_turns` to `tx_session`; the parent's sketch of `tx_session` does not list it.
- Part 8's `rescan` and `reproject` will have to rebuild the **header line** of a stored chunk (it carries the project key and the kind),
  not only the redacted body. The header is built from the session row at ingest; recording it here so that Part 8 plans for it.

## Acceptance (the parent's Done when, mapped)

- Backfill of the fixture sessions stores them, and `status` and a direct read of the store show sessions and chunks per project and kind:
  ingest and command tests.
- A second run adds nothing; a grown session equals one ingested at once at every cut point: ingest tests (section 7).
- Successful tool output, thinking and reminders are not stored; errors are, cut to 500: kept-and-dropped tests.
- A planted secret of each shape (also in a stub, an error, a first prompt, straddling 160 and 500, an unterminated key) is stored as a
  marker with no prefix left: redaction and corpus tests (section 10).
- Deleting a source file after ingest keeps its chunks: ingest test.
- A forgotten session or project whose file exists stays out; a session straddling the cutoff contributes only what follows: ingest tests
  against the real Part 1 store.
- With the archive off, backfill refuses and reads nothing from the host directory: command test with a failing adapter double.
- Kind and project identity: rule tests (sdk entrypoint, openings, 1,500 characters, a missing worktree directory, a repository with a
  remote).
- The documentation lists what is detected and what is not: the docs test suite and review.

## Left to the builder

Exact class and record names, the regexes of section 4 (pinned by the table test), the automation-opening list beyond the prototype's,
the extra `<ide_*>` tags, the exact `status` layout, the buffer size of the byte reader, and the shape of the tested helper that cuts a text without landing inside a marker. The 16 MiB line limit, the 1,800 and 100,000 character limits and the 160 and 500 character limits are fixed.
