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

This part adds, inside `TotalRecall.Infrastructure.Transcripts` (plus the CLI command):

- a **second archive migration** (`TranscriptMigrationRunner`, own list, own version table) adding to `tx_session`:
  `settled_user_turns INTEGER NOT NULL DEFAULT 0`, `built_under_cutoff TEXT NULL` (section 7) and `project_root TEXT NULL`
  (section 6), and an index on `tx_session(project_key, kind)` for the counts of `status`. Part 1's tests that pin the archive schema
  (`TranscriptSchemaTests`: the object list, the `tx_session` columns, `Run_RecordsVersion1`; `TranscriptStoreTests` and
  `TranscriptOpenTests`: `SchemaVersion == 1`) are updated to version 2;
- a change to Part 1's **`ForgetProject`**: the roots it records are the sessions' `project_root` values, not their `cwd` values
  (section 6; Part 1's spec left that reduction to this part), and its tests are updated;
- store methods for the ingest (section 8);
- the adapter, the exclusion-aware ingester, the exchange builder, the chunker, the redactor, the kind and project rules (sections 2 to 7);
- `transcripts backfill`, and the per-project and per-kind counts in `transcripts status` (section 9);
- the fixture corpus and harness (section 10) and the documentation (section 11).

The adapter interface, the ingest records and `TranscriptBackfill` are `public` (the CLI assembly and its tests use them); the rest may
be `internal`. Nothing in the main migration chain, the memory or KB tables, `SessionLifecycle` or the MCP tool list changes.

## Calls made here (the person can overrule them at sign-off)

1. **Claude Code's session directory** is `$CLAUDE_CONFIG_DIR/projects` when that variable is set and non-empty, else
   `~/.claude/projects` (the `UserProfile` folder, as `ClaudeCodeUsageImporter` does). The existing importers ignore `CLAUDE_CONFIG_DIR`;
   this adapter honours it because Claude Code does, exposes the resolved root, and leaves the others alone.
2. **`backfill` is not terminal-gated.** The parent gates only `enable`, `forget`, `unforget` and `prune` (decision 10): backfill stores
   what the already-enabled archive is meant to store, and it is the way scripts and `cron` keep the archive current. It refuses unless
   the archive is on.
3. **Session kind uses the file's `entrypoint` as well** (section 6): in the author's history 88 of 101 sessions carry `entrypoint:
   sdk-py` (reviewer and subagent runs), a stronger signal than the prompt text. The parent leaves the rule to the builder.
4. **Each stored text is capped at 100,000 characters** (after redaction), so a pasted multi-megabyte log cannot turn one prompt into
   thousands of chunks; the cut is marked `[... N more characters not stored]`. The parent sets no cap.
5. **The chunk header names the project by its key** (`strvmarv/total-recall`, or the folder name), not by a path. The parent writes
   `project <path>`; the key is what every later scope and filter uses, and a path would put a user's directory layout into every
   embedding.
6. **Cutoff comparison is "at or before"**, as decision 7 says. Part 1's `IsBeforeCutoff` is strict (`<`) and has tests that pin that, so
   it is left alone; ingestion reads the cutoff once per pass through a new `IngestNotBefore()` and compares itself.
7. **Fake secrets in fixtures are assembled at test time**, never written as literals in committed files, so repository secret scanning
   and push protection do not flag the corpus (section 10).
8. **Some tool inputs are the assistant's answer and are stored as assistant text, not as a stub**: `StructuredOutput` (the whole answer
   of an SDK run: 70 of the author's 88 SDK sessions contain no other assistant text) and `ExitPlanMode` (its `plan`). `AskUserQuestion` is
   stored as a stub of its question text; the user's choice lives in a successful tool result, which is dropped, and this is a known loss.
9. **A record without a timestamp is not given one.** Decision 7 has it take the timestamp of the record before it, which depends on where
   a pass starts and so cannot give the same result for a resumed pass as for a whole-file pass. Claude Code writes a `timestamp` on every
   user and assistant record (checked on 101 files, 0 exceptions), so the rule costs nothing in practice: such a record is dropped while a
   cutoff exists and kept otherwise, and the unit's date and `ts` come from its other records.
10. **A chunk never spans units**, so `exchange_start = exchange_end` always (the header still reads `exchanges a-b`, with `a = b`); the
    repeated question in later parts of a long exchange is cut to 300 characters; the fingerprint hashes the first line, not the first N bytes
    (a small file that grows would change an N-byte fingerprint).

## 1. Data flow

```
backfill (CLI)  ->  ClaudeCodeAdapter.EnumerateSessions()  ->  for each session file (newest first):
   exclusion + unchanged checks  ->  ReadHead  ->  project identity  ->  exclusion check with the key  ->
   ReadRecords(from the watermark)  ->  ExchangeBuilder (drop rules, cutoff filter, redaction, units)  ->
   TranscriptChunker  ->  store.ApplyIngest(...)  (one transaction; exclusion, cutoff and prior state re-checked inside it)
```

The adapter is pure (it reads files and never writes anything). `TranscriptIngester` is the only code that touches the store.
`TranscriptBackfill` (a loop around the ingester over all sessions of one adapter) is what the command calls and what Part 3's
`session_start` pass will call with a budget; both take a `CancellationToken` and an optional progress callback. A failure on one file
is logged through `ExceptionLogger.LogChain`, counted, and never stops the others.

## 2. The adapter and the common record (decision 2)

```csharp
public interface ITranscriptAdapter              // namespace TotalRecall.Infrastructure.Transcripts
{
    string Host { get; }                                       // "claude-code"
    string Root { get; }                                       // the resolved projects directory (call 1)
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

**Enumeration.** `<projects>/<encoded-cwd>/<id>.jsonl`, one directory level only. The root itself may be a symbolic link (dotfile
setups); entries beneath it are checked. `<id>` (the file name without `.jsonl`) is the `host_session_id` and must match
`^[A-Za-z0-9._-]{1,128}$` (anything else is skipped and counted). Not read: nested directories (subagent files under
`<session>/subagents/`), symbolic links beneath the root (files or directories) and anything that is not a regular file. When the same
id appears in two project directories, the file whose path is the stored `source_path` of that session wins, else the one with the newest
mtime, and the other is skipped and counted. A file whose head has
no complete line yet is skipped this pass (a fresh session being written).

**Reading.** Streamed as **bytes**, line by line (split on `0x0A`), never loaded whole, because the watermark is a byte offset. A
trailing partial line (no `0x0A`) is never consumed and never parsed. A line over 16 MiB is skipped and counted (`skippedLines`) rather
than parsed. A line that is not valid JSON, is valid JSON but not an object, has a `message` that is not an object, fails to decode (a
lone surrogate escape), or is a record of an unknown type is skipped (and counted); one bad line never fails the file. Each record yields zero or more
`TranscriptRecord`s with the byte offsets of its line (a user record with text and errors yields the text first, then the errors).
`ReadHead` reads at most the first 200 complete lines and returns the first `cwd`, the first `entrypoint`, the first `timestamp`, and
`Fingerprint`: the SHA-256 (hex) of the first complete line (a first line over 16 MiB has no fingerprint, and the session is skipped and
counted).

**Claude Code record mapping** (verified on current Claude Code files, 2026-11-19: records carry `type`, `uuid`, `timestamp`, `cwd`,
`sessionId`, `entrypoint`, `isMeta`, `isSidechain`, and user records an `origin` object; message content is a string or an array of
blocks):

| Record | Result |
|---|---|
| `type: "user"`, not `isMeta`, not `isSidechain`, not `isCompactSummary`, `origin` absent or `origin.kind == "human"`, `message.content` a string or with `text` blocks | `UserText` (the text after the dropped-text rules of section 3; no record if nothing is left) |
| a `user` record whose `origin.kind` is present and is anything else (the author's files hold 148 `task-notification` records: results of background tasks and subagents, injected by the host) | nothing |
| `type: "attachment"` with `attachment.type == "queued_command"`, `attachment.commandMode == "prompt"` and `attachment.origin.kind == "human"` (a prompt the user typed while the agent was working; the fields all sit inside `attachment`, the record has no `origin` of its own; in the author's files some such prompts exist only here) | `UserText` of `attachment.prompt`. No de-duplication rule: no prompt that is both a queued attachment and a `user` record was observed (19 queued attachments in 101 files), and fixtures use the attachment shape only |
| a `user` record with `tool_result` blocks, `is_error: true` | `ToolError` (the block's text: a string, or the concatenation of its `text` blocks) |
| the same, not an error | nothing (successful tool output is dropped, decision 3) |
| `type: "assistant"`, not `isSidechain`, `text` block | `AssistantText` |
| `type: "assistant"`, `tool_use` block named `StructuredOutput` or `ExitPlanMode` (call 8) | `AssistantText`: for `ExitPlanMode` its `plan`; for `StructuredOutput` its input rendered as `key: value` lines (strings as they are, other values as compact JSON) |
| `type: "assistant"`, any other `tool_use` block | `ToolStub` (`ToolName` and the stub text of section 3) |
| `thinking` blocks, `image` blocks, any other block type | nothing |
| `type: "system"`, `subtype: "compact_boundary"` | `CompactionBoundary` |
| a `user` record with `isCompactSummary: true` | `CompactionSummary` (its text; a string or `text` blocks) |
| `isMeta: true`, `isSidechain: true`, and every other record type (other `attachment` types, `last-prompt`, `file-history-snapshot`, `system` with another subtype, `api-request*`, `cost-state`, ...) | nothing |

`Ts` is the record's `timestamp` (ISO 8601, any offset, normalised to UTC), or null when absent (call 9).

## 3. What is kept (decision 3)

Kept: user text, assistant text, one-line tool stubs and the error output of a failed tool call. Dropped: everything else in the table
above, and inside **user** text (assistant text is the model's own words and is kept as written, so a message that talks about these tags
loses nothing):

- these blocks, whole, with their tags: `<system-reminder>`, `<task-notification>`, `<local-command-stdout>`, `<local-command-stderr>`,
  `<local-command-caveat>`, `<command-message>`, `<bash-stdout>`, `<bash-stderr>`, `<EXTREMELY_IMPORTANT>` (a block with no closing tag is
  dropped to the end of the text only when its opening tag starts the text or a line); the builder adds the hosts' `<ide_*>` context tags the fixtures show;
- a slash command is kept as plain text: `<command-name>/develop</command-name><command-args>148</command-args>` becomes `/develop 148`,
  and `<bash-input>ls -l</bash-input>` (the `!` shell mode) becomes a `ToolStub` record `[Bash: ls -l]` (it joins the open unit; it is not a user turn and does not start a unit or give the title);
- a user text that is only a host interruption notice (`[Request interrupted by user...]`) is dropped, and is not a real user turn.

**Tool stub** text: `[<ToolName>: <detail>]`, where the detail is one field of the tool's input chosen by tool name (`Bash`: `command`;
`Read`, `Edit`, `Write`, `NotebookEdit`: `file_path`; `Grep`, `Glob`: `pattern`; `WebFetch`: `url`; `WebSearch`: `query`; `Task`, `Agent`:
`description`; `AskUserQuestion`: the text of its first question; any other tool: the first string-valued field, in document order). The
order is **redact the raw field, then fold newlines to spaces and collapse whitespace, then cut to 160 characters** (the cut is marker-safe,
section 4); the `[Name: ` and `]` wrapper is added after and is not counted. A tool whose chosen field is missing or empty gives `[<ToolName>]`.

**Error text**: `[error: <text>]`: the text redacted whole, then cut to **500 characters** with `...` appended (marker-safe), newlines kept
(a stack trace is what is searched for). An empty error gives `[error]`. Errors are not subject to the 100,000-character rule.

**Text cap** (call 4): a user or assistant text or a compaction summary is cut, after redaction, at 100,000 characters with
`[... N more characters not stored]` appended (N counts characters of the redacted text).

**"Characters"** in this document means UTF-16 code units, counted the same way everywhere, and every cut avoids splitting a surrogate pair.

## 4. Redaction (decision 6)

**API.** `SensitiveContentScanner.Redact(string? text)` returns `RedactionResult(string Text, int Count, IReadOnlyList<string> Categories)`
(null or empty in, the same out with count 0). The existing `Scan`, `ScanSecrets` and `Detectors` keep their behaviour and tests
unchanged; `Redact` builds on the same token shapes and extends the `sk-` shape (below) in its own pattern set only.
`public const int RedactionVersion = 1`, written to every chunk's `redaction_version`. A marker is exactly `[redacted:<category>]` with one
of these categories:

| category | what it matches | what is replaced |
|---|---|---|
| `token` | the existing shapes (`ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_`, `github_pat_`, `glpat-`, `xox[baprs]-`, `AKIA`) and `sk-` keys **including `sk-ant-...`, `sk-proj-...`, `sk-svcacct-...` and `sk-admin-...`** (the likeliest secrets in a Claude Code transcript; the existing `sk-[A-Za-z0-9]{16,}` misses them because of the hyphens). The `sk-` shapes need a left boundary (not preceded by a letter or digit, so `task-list-management-and-more` is not a key) and the extended forms at least 20 characters after the prefix | the token |
| `private-key` | `-----BEGIN [A-Z ]{0,40}PRIVATE KEY( BLOCK)?-----` | from the BEGIN line to the end of the matching `-----END ...-----` line, **or to the end of the text when the END line is missing** |
| `bearer` | `Authorization: <scheme> <value>` (also `=`; quotes allowed; scheme `Bearer`, `Basic`, `Token`, `Digest` or none), `x-api-key: <value>`, and a bare `Bearer <value>` | the value (at least 8 characters after `Authorization` or `x-api-key`, 16 after a bare `Bearer`) |
| `url-credentials` | `scheme://user:password@host` | the password only (the user name and the host stay searchable). The user information is the text after `scheme://` up to the first `/`, `?`, `#` or whitespace, split at its **last** `@`; the password is what lies between the first `:` and that `@` (so `mysql://root:P@ssw0rd@localhost` is one password) |
| `secret-assignment` | an assignment (`=` or `:`, quoted or not, also JSON `"name": "value"` **and its backslash-escaped form `\"name\": \"value\"`**, which is how a credential appears inside a decoded command string; optional backslashes are allowed around the name, the separator and the value quotes) to a secret-named variable: names ending in `_KEY`, `_TOKEN`, `_SECRET`, `_PASSWORD`, `_PASSWD`, or equal to `PASSWORD`, `PASSWD`, `SECRET`, `TOKEN`, `API_KEY`, `APIKEY`, `PASSPHRASE`, `CLIENT_SECRET`, `ACCESS_KEY`, `AUTH_TOKEN` (any case); also the command-line flags `--password`, `--passwd`, `--token`, `--api-key`, `--secret` and `--auth-token` followed by `=` or whitespace | the value: a quoted value ends at its closing quote; a `$(...)` or backtick command substitution is one value (balanced parentheses); any other unquoted value is the whole run of non-whitespace characters (so a `,`, `;`, `&` or `)` inside a password does not leave a suffix behind). A multi-word unquoted value is a residual risk |
| `unprocessable` | a text that a detector could not finish (the timeout below) | the whole text |

Rules that every detector follows:

- **Overlaps merge** (the union of the spans is replaced by one marker, the category of the span that starts first).
- **Only a reference or a placeholder is left alone**, and only when the **whole value** is one: `$NAME`, `${NAME}` or `%NAME%` with
  `NAME` matching `[A-Za-z_][A-Za-z0-9_]*` **when the value is unquoted or double-quoted** (so `${X:-literal}`, `$(echo hunter2)` and
  `'$Tr0ub4dor9'` in single quotes, where a shell does not expand it, are not exempt), `<word-ish>`, `{{ ... }}`, or one of
  `true`, `false`, `null`, `none`, `undefined`, or empty. `GH_TOKEN="$t" gh api ...`, which fills this
  repository's own history, is therefore not redacted. **There is no exemption by length or by first character**: a short password is
  still a password, and `PASSWORD='$ecret!9'` or a value that merely starts with `<`, `{` or `%` is redacted. The table test pins both
  directions with positive and negative examples per category; the builder writes the exact patterns.
- **Every repeated element is bounded or unambiguous** (no nested quantifiers, and a class that can also match what follows it carries an
  upper bound), and every
  regex has a match timeout of 5 seconds as a **backstop**, not an everyday path: the builder verifies that each pattern finishes in well
  under a second on adversarial 1 MiB inputs (a long run of identifier characters, of `=`, of `:`, of quotes). A timeout **fails
  closed**: the whole text becomes `[redacted:unprocessable]` (count 1) and nothing from it is stored. Interpreted regexes only (NativeAOT:
  no `RegexOptions.Compiled`; the non-backtracking engine is not used because it has no lookbehind).
- Redaction runs on the **full, untruncated text, before any truncation and before chunking** (so a token cannot be cut or split), on
  every text this part stores: user and assistant text, stub details, error text, compaction summaries, and the session title (which is
  derived from already-redacted text).
- **Cutting.** One tested function cuts text to a limit and **every cut in this part uses it** (the 160-, 500-, 120- and 300-character cuts, the 1,800-character chunk limit and the 100,000-character cap) and never lands inside a marker: when the limit falls inside `[redacted:...]`, the
  cut goes **before** the marker and `...` is appended. This is what keeps a token that straddles character 160 or 500 from leaving a prefix.
- Emails are not redacted (parent decision 6).

Residual risk, documented in `docs/transcripts.md`: only the listed shapes are detected. Not detected: a password or key written in prose
or in a variable whose name is not on the list, a secret in a format not listed (cloud keys other than `AKIA`, JWTs, database passwords
outside a URL or a listed variable), anything a user pastes in a different encoding, and a secret split across two messages.

## 5. Exchanges and the chunker (decisions 3 and 5)

**Units.** Records are folded, in file order, into numbered **units**; the number is the `exchange_start`/`exchange_end` of its chunks,
counted from 0 over the session as stored. A unit is one of:

- a **dialogue exchange**: it starts at a `UserText` record and takes every following `AssistantText`, `ToolStub` and `ToolError` record
  until the next `UserText`, a `CompactionBoundary` or a `CompactionSummary` (consecutive `UserText` records each start a unit);
- a **summary unit**: one `CompactionSummary` record;
- an **assistant-only exchange**: assistant, stub or error records with no `UserText` before them (records after a cutoff, or after a
  boundary) form a dialogue exchange with no user part. An error never starts a unit by itself unless nothing else is open.

A `CompactionBoundary` closes the open unit and starts nothing. A unit with no kept content is not a unit (no number is consumed). Each
unit remembers the `StartOffset` of its first record and the `Ts` of its first record that has one; with none, the header date is
`unknown` and the chunk's `ts` is null.

**Cutoff filter** (decision 7, call 9), applied to records **before** they form units, with the cutoff read once per session pass
(`IngestNotBefore()`): a record with `Ts <= cutoff` is dropped; a record with no `Ts` is dropped while a cutoff exists. So an exchange
whose question is before the cutoff and whose answer is after it contributes the answer only (as an assistant-only exchange).

**Chunk text.** A chunk's `content` is a header line, a blank line and a body:

```
claude-code session <first 8 characters of the id> | project <project_key> | <yyyy-MM-dd of the unit's first Ts, UTC, or unknown> | <kind> | <dialogue|compaction-summary> | exchanges <a>-<a>

User: <the user text>

Assistant: <text>

[Bash: <detail>]

[error: <text>]
```

Blocks appear in record order, separated by one blank line, each starting with its label (`User:`, `Assistant:`, a stub, an error).
A summary chunk has the header and the paragraphs of the summary, no labels. `has_error = 1` when the chunk contains an error block.
`chunk_type` is `dialogue` or `compaction-summary`; `ts` is the unit's first `Ts` (ISO 8601 UTC) or null; `redaction_version` is 1;
`embedded` is 0.

**Size.** The body of a chunk is at most **1,800 characters** (the header is not counted). A unit whose blocks do not fit is split into
parts (`part` = 0, 1, 2, ... within the unit; a unit that fits is one part, part 0) at block boundaries; a block longer than the
**capacity left in its part** (1,800 minus what the part already holds, including the repeated-question line of a later part) is split on paragraph boundaries (`\n\n`), then on line boundaries, then, if a line is still too long, on whitespace, and
only as a last resort at the limit (never inside a marker or a surrogate pair). **Every part after the first begins with the question
repeated** (call 10): a line `User (continued): <the user text cut to 300 characters>`, which counts toward the 1,800. A unit with no
user part repeats nothing. Every part has its own header (the same unit numbers).

The chunker is a pure function: `Chunk(unit, header facts) -> list of chunks`.

## 6. Session kind (decision 4) and project identity (decision 8)

**Kind** is `interactive` or `automated` and is decided from the kept records of the file (after the cutoff filter; it is stored, so it can be re-evaluated). The first rule
that applies wins:

1. the session's `entrypoint` (from the head) starts with `sdk` (case-insensitive) -> `automated`;
2. exactly one real user turn in the whole session (a `UserText` record after the cutoff filter) and either its text starts, after
   trimming, with a known automation opening (a list in code, matched case-insensitively, from the prototype: `Review this change for
   security`, `You previously flagged`, `Scope:`, `You are reviewing`, `Read the full review brief`, `Round <n>`) or is longer than 1,500
   characters (measured on the redacted text) -> `automated`;
3. otherwise `interactive` (this includes a session with no real user turn).

The number of real user turns is `settled_user_turns` (the turns in the units before the watermark, kept on the session row; section 7)
plus the turns in this pass. With a total of two or more, rule 2 does not apply. When the total is exactly one and that turn is not in
the part this pass re-read (`settled_user_turns = 1`), rule 2 cannot be evaluated and the stored kind is kept. A kind that changes
between passes (for example a first prompt of 1,400 characters followed by a second turn) triggers the redo of section 7, so the two
directions are both allowed. A session with no kept content stores nothing, so it has no kind.

**Title.** The first real user turn of the session, redacted, newlines folded to spaces, cut to 120 characters (marker-safe). It is set
when the stored title is null (a first pass, a rewritten pass, or a later pass that finds the session's first real turn) and kept after
that; a session with no real user turn has a null title.

**Project identity.** `project_key` and `project_key_source` are computed once per pass from the session's `cwd`:

1. the `cwd` of the head (the first record that carries one). With none, the `cwd` is the encoded project directory name decoded as
   `ClaudeCodeUsageImporter.DecodeProjectDirName` does (lossy, documented) and the key source is `path`.
2. If the directory exists, `ProjectResolver.Resolve(cwd)` gives the key (`owner/repo` from the remote, else the repository root's
   folder name, lower case; the resolver already follows a worktree's `.git` file to its repository).
3. If it does not exist (deleted worktrees are the common case for historical sessions): a path with a `/.claude/worktrees/<name>` or
   `/.zoid/worktrees/<name>` segment is cut back to the part before that segment (the repository that held the worktree); the resolver
   runs on that root if it exists; otherwise the key is the last path segment of the root, lower case.
4. `project_key_source` is `remote` when the key contains `/`, else `path` (a folder name never does). A null or empty result falls back
   to the folder name of the `cwd`, and to `unknown` (source `path`) when there is no `cwd` at all.

5. Paths are split on both `/` and `\` (a Windows `cwd`), for the worktree rule and for the last segment.
6. **`project_root`** (a new column) is the repository's directory when one is known, else null: the directory that holds the `.git`
   entry found by the resolver's walk up from the `cwd`, or the part before the `/.claude/worktrees/` or `/.zoid/worktrees/` segment of
   a path whose directory is gone. A directory that is a filesystem root or the user's home directory is never a root. A session with no
   repository (a `cwd` of `/tmp`, a home directory, a missing directory with no worktree segment) has a null root.
   Part 1's `forget` of a project stores the **`project_root` values** of the sessions it removes (not their `cwd`), so a project that
   has no repository is excluded by its key only, and forgetting the project of `/home/user` cannot exclude every project under it.
7. A key derived with source `path` never replaces a stored key with source `remote` for the same session (a deleted repository
   directory would otherwise re-key and rewrite the session); re-keying is Part 8's `reproject`.

`cwd` is stored on the session (the key is recomputable without a migration). The adapter and the key rule do not run before the
cheap exclusion check of section 7, step 1.

## 7. Per-session ingest: watermark, resume, exclusion

One `TranscriptIngester.IngestSession(source)` does, for one source file:

1. **Exclusion by id.** `store.IsExcluded(host, id, null, null)`: when true the file is **not opened**; count `excluded`.
2. **State.** Read the cutoff once, `cutoff = store.IngestNotBefore()`, and `store.GetIngestState(host, id)` (the session row, or null).
   If a row exists and `source_size` equals the file's current size, `source_mtime` equals its mtime **and `built_under_cutoff` equals
   `cutoff` (compared as the text stored in `tx_state`)**, the file is unchanged: count `unchanged`, stop (no read). (`source_size` is the file's **stat size observed before the
   read**, so a file that ends in a partial line is not re-read until it changes. A changed cutoff makes the row stale even when the file
   did not change: that is how records hidden by a cutoff come back after `unforget --cutoff`.)
   When there is a cutoff and the file's mtime is at or before it, the file cannot hold a record after it: count `excluded`, stop (no
   read; this also keeps every older file from being re-read on each run after a forget-everything).
3. **Head.** `ReadHead`. No fingerprint -> count `skipped`, stop. Compute the project identity (section 6) and call `store.IsExcluded(host,
   id, key, cwd)`: when true, count `excluded`, stop. (This is the first read of the file; nothing from it is stored.)
4. **Mode.** No row: **fresh** (start 0). A row whose `source_fingerprint` differs from the head's, or whose `source_size` is larger
   than the file's current size, or whose `ingested_offset` is above 0 with a byte before it that is not `0x0A`: the **source changed**
   (the host replaced or truncated the file): the session is **not ingested**, its stored chunks are **kept** (the archive keeps what the
   host loses), the run counts it `changed`, and the user can `forget` the session and run backfill to store the new file. A row with
   `exchange_count = 0`, or whose `built_under_cutoff` differs from `cutoff` (the cutoff was cleared by `unforget`, so the records it
   had hidden must come back), or whose `project_key` differs from the key derived now (item 7 of section 6 aside): a **rewritten** pass
   (start 0, replace every chunk of the session, the settled turn count starts at 0). Otherwise **resume**: start at `ingested_offset`.
   A pass that starts at offset 0 always counts turns from 0, never from the stored `settled_user_turns`.
5. **Read** from the start offset to the end of the last complete line, build units, chunk them, derive kind and title (section 6).
   - The **watermark** (`ingested_offset`) is the `StartOffset` of the first record of the **last unit**: the last unit may still receive
     records, so each pass re-reads from there and rebuilds it. `exchange_count` is the number of the last unit + 1. The unit numbering of a
     resumed pass continues from `exchange_count - 1` (the number of the unit that is read again). `settled_user_turns` is the real user
     turns in all units before the last unit.
   - `source_size` and `source_mtime` (milliseconds) are the stat values observed **before** the read began (a file that grew during the
     read is therefore read again next pass, never skipped); `source_fingerprint` is the head's; `built_under_cutoff` is `cutoff` (null
     when none); `started_at` is the first `Ts` of any kept record of the session, `ended_at` the largest `Ts` of a kept
     record (a rewritten pass recomputes both, and the title, from scratch; a resumed pass keeps the stored `started_at` and title once
     set and takes `ended_at` as the larger of the stored value and this pass's); `source_path` is the current path.
   - Headers carry the kind and the project key. A **resumed** pass that derives a kind different from the stored one (the project key
     case is decided in step 4) is **redone once as a rewritten pass** (start 0, turns counted from 0, replace every chunk, with the new
     kind): the kind of a whole-file read is deterministic, so the redo cannot change it again and cannot loop. Otherwise the chunks of
     the earlier units would keep a stale header and a grown session would not equal the same session ingested once.
   - A pass over a session with **no kept content**: with no row it stores nothing (the file is read again on the next run, which is
     cheap and counted `empty`); with a row (a rewritten pass, as after a cutoff change, that now keeps nothing, or a cutoff that hides everything) it **deletes the
     session's chunks** and sets the row to: `ingested_offset` 0, `exchange_count` 0, `settled_user_turns` 0, null title, `started_at` and
     `ended_at`, the stored kind kept (it is NOT NULL), and the new source fields and cutoff. A row with `exchange_count = 0` is read as a
     rewritten pass next time (step 4), so the numbering never starts from -1.
6. **Apply** with `store.ApplyIngest(...)` (section 8): one `BEGIN IMMEDIATE` transaction that checks, inside it, that (a) the session is
   not excluded (a `forget` that committed since step 3 wins: outcome `Excluded`, nothing written), (b) `ingest_not_before` still equals
   the `cutoff` the pass was built under, compared as the stored text (a forget-everything that committed since: outcome `Stale`), and (c) the session row still equals
   the state the pass started from (**every field of the state read in step 2**; or still absent for a fresh pass; another pass committed
   since: outcome `Conflict`). On `Stale` or `Conflict` nothing is written and the ingester redoes the
   session **once** from step 2; a second one counts the session `retry` and leaves it for the next run. The write is an **upsert by
   `(session_id, exchange_start, part)`**: a chunk row whose content and metadata are identical is left untouched (its id and `embedded`
   are kept), a changed one is updated (`embedded` reset to 0), and the rows of the rebuilt range that no longer exist are deleted. Then
   the transaction commits. The **session row is upserted first and the chunks after it** (the chunks reference the row, and foreign keys
   are on). A later pass never resurrects a forgotten session.

Guarantees (each has a test): a second run changes nothing, **including row ids and `embedded`**; a session grown across two passes at
**any** cut point (a line boundary inside an exchange, between exchanges, just after a compaction summary, in the middle of a line)
equals the same file ingested once (all `tx_session` columns except `id` and `source_mtime`, and all `tx_chunk` columns except `id`,
compared as ordered sets); deleting the source file afterwards leaves every chunk and the session row; a session forgotten before still
has its file but stays out; a session that straddles the forget-everything cutoff contributes only its records after it, also when the
forget happens **while** a pass is running (`Stale`), and the records the cutoff had hidden are ingested after `unforget --cutoff`;
two passes over the same session at once end in the same state as one (`Conflict`).

## 8. Store additions (`ITranscriptStore`)

Names are the builder's; the behaviour is fixed.

- `SessionIngestState? GetIngestState(string host, string hostSessionId)`: `source_size`, `source_mtime`, `source_fingerprint`,
  `ingested_offset`, `exchange_count`, `settled_user_turns`, `kind`, `project_key`, `project_root`, `title`, `started_at`, `ended_at`, `built_under_cutoff`.
- `DateTimeOffset? IngestNotBefore()`: the global cutoff, or null.
- `IngestOutcome ApplyIngest(SessionIngest ingest)`: `SessionIngest` carries the session fields (host, id, project key and source, cwd,
  started/ended, kind, title, the source stat fields, offset, counts, the cutoff it was built under), the **expected prior state** (the
  state read in step 2, or "none"), the first rebuilt unit number (0 means every chunk) and the new chunks. Outcomes: `Applied(chunksNew,
  chunksChanged, chunksUnchanged, chunksRemoved, created)`, `Excluded`, `Stale`, `Conflict`. The checks of section 7 step 6 run inside
  the store's own transaction through private, transaction-aware variants of `IsExcluded` and the cutoff read.
- A second migration in the archive runner (see "What exists today").
- `Status()` gains the rows of `(project_key, kind, sessions, chunks)`, ordered by chunks descending, returned whole (the command
  truncates for display). `TranscriptStatus` gets them as a new member with a default of empty.

All SQL is parameterised; the store keeps its own connection and `secure_delete`; every write goes through the store (the ingester never
opens SQLite itself).

## 9. The `backfill` command and `status`

`total-recall transcripts backfill [--host claude-code]` (`--host` is the only option; an unknown host is a usage error, exit 2; today
the only host is `claude-code`; Part 7 adds `pi`).

Order: parse arguments -> read the configuration strictly (a malformed config exits 1, as the other commands) -> the archive must be on
(`[transcripts] enabled = true`) and the storage mode must not be Postgres -> `TranscriptStore.Open(location, create: false)` (absent
tables, as after a hand edit of `config.toml` that never ran `enable`, print that the archive tables do not exist and that `total-recall
transcripts enable` creates them, and exit 1; an older archive is migrated to the second migration here) -> only then create the adapter and
touch the host directory. **While the archive is off, backfill prints that it is off and how to enable it, exits 1, and has read nothing
from the host directory** (a test double adapter that fails if it is ever enumerated proves it). It needs no terminal and takes no
confirmation.

Output: a progress line every 50 sessions, then a summary: sessions seen, `new`, `updated`, `unchanged`, `excluded`, `empty` (nothing
kept), `skipped` (no complete first line, unreadable name, duplicate id), `changed` (the host replaced or truncated the file; kept as it was), `retry` (left for the next run), `failed` (an error on that
file, logged), chunks new, changed, unchanged and removed. Exit 0 when no file failed, 1 when any failed (the others were still done), 2
for a usage error. Sessions are processed newest file first. A Ctrl+C stops after the current session and prints the summary so far.

`transcripts status` keeps its lines and adds, under the totals, one line per `(project, kind)` with its sessions and chunks (at most 30
lines, then `and N more`). The line `search: not available in this version yet` stays.

## 10. Fixture corpus and harness (decision 13)

A committed corpus of **synthetic** Claude Code sessions (written for the tests; nobody's history) under
`tests/fixtures/transcripts/claude-code/`, plus a harness `ClaudeFixtureHome` that builds a temporary `projects/` tree from them. The
harness is one source file used by both test projects that need it (`Infrastructure.Tests` and `Cli.Tests`, linked into the second), and
the fixtures are copied to or found from each test output by the csproj (the existing `tests/fixtures/skills` is found by walking up;
either way works).

| Fixture | Content |
|---|---|
| `decision` | interactive (`entrypoint: cli`), three turns, tool stubs, a successful tool result carrying a unique marker, a `system-reminder` block, a `thinking` block, an `isMeta` record, an `isSidechain` record, a slash command, a `task-notification` user record and a queued human prompt (the `attachment` shape) |
| `compaction` | turns, a `compact_boundary`, an `isCompactSummary` record (several paragraphs), more turns |
| `error` | a failed `Bash` call whose error text holds an exact string a later part will search for |
| `automated-sdk` | `entrypoint: sdk-py`, a long prompt and a `StructuredOutput` answer |
| `automated-opening` | `entrypoint: cli`, one prompt starting `Review this change for security`; a copy of it with a second turn appended by the harness (kind flips to interactive) |
| `secrets` | one planted secret per category (token shapes `ghp_`, `github_pat_`, `glpat-`, `xox`, `sk-`, `sk-ant-`, `sk-proj-`, `AKIA`; a private key; `Authorization: Bearer`; `https://user:pass@host`; `API_KEY=...`), also inside a tool stub, inside an error, in the first prompt (the title), a token that straddles character 160 of a stub and one that straddles character 500 of an error, a private key whose END line is cut off, and the placeholder forms that must **not** be redacted (`GH_TOKEN="$t"`) |
| `worktree` | `cwd` under `/.../.claude/worktrees/<name>` of a directory that does not exist, and another session in a repository the test creates with a remote |
| `no-kept` | attachments, a sidechain, a meta record, a hook output: nothing to keep |
| `straddle` | records whose timestamps lie around a cutoff the test sets |

**Placeholders.** Fixtures never hold a secret as a literal (call 7): they hold `{{SECRET:github-token}}`, `{{SECRET:aws-key}}`, ...,
`{{CWD}}`, `{{ID}}` and `{{T+n}}` (a timestamp `n` seconds after a base the test chooses). The harness substitutes them **after
JSON-escaping** the value, so a multi-line private key is valid in a JSON string. The fakes are assembled in code from parts, are at least
12 characters, look random and match their detector (for example `"ghp_" + 36 random letters`). Padding that makes a secret straddle
character 160 or 500 is computed by the harness from the substituted length. The directory name of a placed session is the harness's
encoding of `{{CWD}}` (every non-alphanumeric character becomes `-`, as Claude Code does).

The planted value is also available to the test, which asserts that **no substring of 8 or more characters of it** (case-insensitive) is
present in any `tx_session` or `tx_chunk` row, in the text index's tables, or in the database, WAL and shared-memory files, and that the
marker of its category is.

The harness can also append to a placed session (a line, a partial line, a whole exchange) to simulate growth, replace a session's file,
and delete a source.

## 11. Documentation

`docs/transcripts.md`: the status banner and the sections that say "nothing is stored yet" are corrected (the archive can be enabled,
backed up with `backfill`, inspected and emptied; it cannot be searched yet); new sections: what `backfill` reads and stores, what is
kept and dropped (including the loss of the user's `AskUserQuestion` choice), **what is redacted and what is not (the categories and the
residual risk of section 4)**, where Claude Code's files are (`CLAUDE_CONFIG_DIR`), how resume works in two lines. `docs/commands.md`: the
`backfill` row. `PRIVACY.md`: the stored-data row now says that after `backfill` the archive holds the dialogue of the user's Claude Code
sessions, locally, with known secret shapes redacted, and what is not covered; the retention paragraph says nothing the host deletes later
is removed from the archive. `docs/configuration.md`, `README.md` and `CHANGELOG.md` (unreleased, internal) are corrected where they say
nothing is stored. The enable confirmation text and the line that `enable` prints (`EnableCommand`: "Nothing is stored yet in this
version") are corrected to match, with their tests. The announcement of the feature stays with the release that completes Parts 1 to 3;
nothing here is announced. `src/TotalRecall.Infrastructure/AGENTS.md` (the archive migration list) and the root `AGENTS.md` table get the
new locations.

## Deviation from the parent design

- Session kind adds the `entrypoint` rule (call 3). The parent leaves the rule to the builder.
- The cutoff is compared "at or before" inside the ingester through a new `IngestNotBefore()`; Part 1's strict `IsBeforeCutoff` is
  unchanged (call 6). A record without a timestamp is not given its predecessor's (call 9): decision 7's rule cannot be applied the same way
  to a resumed pass.
- Kind is decided from the **kept** records, so a session that straddles a forget-everything is classified on what remains (decision 4
  says "from the session file alone"); counting the hidden turns would need them to be read again on every pass.
- A cap of 100,000 characters per stored text (call 4), `CLAUDE_CONFIG_DIR` (call 1), and two tool inputs stored as assistant text (call 8):
  decision 3 keeps stubs only, and these two are the assistant's answer.
- The chunk header says `project <key>`, not `project <path>`; a chunk never spans units; the repeated question is cut to 300
  characters; the fingerprint hashes the first line (calls 5 and 10).
- A second archive migration adds `settled_user_turns` and `built_under_cutoff` to `tx_session`; the parent's sketch of `tx_session` does
  not list them. `built_under_cutoff` is what lets a pass notice that a forget-everything raced it or that `unforget --cutoff` revealed
  records it had dropped.
- Part 1's `ForgetProject` now records the sessions' repository roots instead of their working directories (section 6, item 6), as Part 1's
  own spec said Part 2 would do; a project with no repository is excluded by key only.
- Known limits: a file replaced by a larger one that keeps the same first line, and whose old offset happens to start a line, is not
  detected and is resumed from the stale offset; a host that rewrites a session file (none observed: Claude Code appends) leaves the stored
  session as it was until the user forgets it and runs backfill again.
- Notes for later parts: Part 8's `rescan` and `reproject` will have to rebuild the **header line** of a stored chunk (it carries the
  project key and the kind), not only the redacted body. Part 5 accepts a marker's transcript path only under the host's transcript root
  (decision 12): with `CLAUDE_CONFIG_DIR` honoured here, that root is `ITranscriptAdapter.Root`, not a fixed `~/.claude/projects`.

## Acceptance (the parent's Done when, mapped)

- Backfill of the fixture sessions stores them, and `status` and a direct read of the store show sessions and chunks per project and kind:
  ingest and command tests.
- A second run adds or changes nothing (ids and `embedded` included); a grown session equals one ingested at once at every cut point; a
  forget-everything during a pass and two simultaneous passes end correctly: ingest tests (section 7).
- Successful tool output, thinking, reminders, host-injected `task-notification` text and the model's other tool inputs are not stored;
  errors are, cut to 500; the answers of `StructuredOutput` and `ExitPlanMode` are: kept-and-dropped tests.
- A planted secret of each shape (also in a stub, an error, a first prompt, straddling 160 and 500, an unterminated key, Anthropic and
  OpenAI key shapes) is stored as a marker with no prefix left, and a reference such as `$t` is not redacted: redaction and corpus tests
  (section 10).
- Deleting a source file after ingest keeps its chunks: ingest test.
- A forgotten session or project whose file exists stays out; a session straddling the cutoff contributes only what follows: ingest tests
  against the real Part 1 store.
- With the archive off, backfill refuses and reads nothing from the host directory: command test with a failing adapter double.
- Kind and project identity: rule tests (sdk entrypoint, openings, 1,500 characters, a kind flip on growth, a missing worktree directory,
  a repository with a remote).
- The documentation lists what is detected and what is not: the docs test suite and review.

## Left to the builder

Exact class and record names, the regexes of section 4 (pinned by the table test and the adversarial-input timing check), the
automation-opening list beyond the prototype's, the extra `<ide_*>` tags, the exact `status` layout, the buffer size of the byte reader,
how the harness file is shared between the two test projects, and the shape of the tested helper that cuts a text without landing inside
a marker. The 16 MiB line limit, the 1,800 and 100,000 character limits and the 160 and 500 character limits are fixed.
