# Transcript archive

> **Status: back up, embed and search.** Once the archive is on, total-recall archives your Claude Code sessions in the background at each session start (and on demand with `total-recall transcripts backfill`), turns the text into vectors on your machine and lets the assistant search it, by meaning and by words, with `transcript_search`. Backing up, embedding and searching are usable together. Reading a hit in full beyond the chunk the search returns (`transcript_get`), listing sessions (`transcript_sessions`) and other agent hosts arrive in a later release: until then those two tools only report whether the archive is on.

## What it is

The transcript archive is an opt-in store for the dialogue of your agent sessions: your prompts and the assistant's replies, kept so that a later session can look back at what was said, not only at the memories that were distilled from it. It is separate from the memory tiers and the knowledge base: nothing in the archive is injected at session start, and nothing in it is a memory.

It is **off by default**. Only you can turn it on, from a terminal or by editing `config.toml`. An assistant cannot enable, disable or redirect it through `config_set`, the web UI or `config set` (see [Changing the settings](#the-transcripts-settings)).

## What is stored

Only while the archive is enabled, and only from the session files of a host (today Claude Code), read at each session start by a background pass or when you run `total-recall transcripts backfill`, it keeps:

- the dialogue of your Claude Code sessions (your prompts and the assistant's replies, with one-line stubs for tool calls and the error output of failed ones), split into chunks (see [What is kept and what is dropped](#what-is-kept-and-what-is-dropped));
- a little metadata about each session: the host and its session id, the project (its key, and the repository directory the project is excluded under), the working directory, start and end times, a title (your first prompt, cut to 120 characters), whether it was `interactive` or `automated`, and the size, modification time and a fingerprint of the file it was read from, so that a second run can tell what changed;
- a vector for each chunk (384 numbers made from the chunk's text by the embedding model), so that the chunk can be found by meaning. The vectors derive from the text, live in the same file and are removed with it (see [Embedding](#embedding)).

How it is kept:

- **Locally.** The archive lives in a SQLite database on your machine: in the main database (`~/.total-recall/total-recall.db` by default) or, if you set `transcripts.db_path`, in a separate file.
- **With known secret shapes redacted.** Text is scrubbed of the secret shapes listed in [What is redacted and what is not](#what-is-redacted-and-what-is-not) before it is stored. Only those shapes are found: read that section, including its limits, before you rely on it.
- **Never synced by total-recall.** The archive is not sent to Cortex and is not part of git sync. Nothing that syncs reads or writes its tables. This is about total-recall's own sync: if the main database, or the `transcripts.db_path` file, sits in a folder that another service shares (Dropbox, iCloud, OneDrive), that service copies the archive, and `forget` cannot reach those copies.
- **Not removed when the host deletes its files.** Once a session is in the archive, deleting or losing the host's session file does not remove it. The archive keeps what the host loses; only `forget` removes it.

Enabling the archive creates empty tables; the first read of a host's files comes with the next session start (the background pass, see [How the archive is filled](#how-the-archive-is-filled)) or with `backfill`, whichever is first. Only `transcripts enable` creates the tables; a hand edit of `config.toml` does not (and, with no tables, the pass does nothing).

## Privacy

- The archive is off until you run `total-recall transcripts enable` in a terminal and confirm. The confirmation says that, once on, total-recall reads the host's session files in the background at each session start and archives their dialogue (redacted, local, never synced), turns it into vectors on this machine (a remote service only if you set `allow_remote_embedding` by hand), and that `transcripts forget` deletes it.
- The archive text is embedded by the local model on your machine. It is never sent to a remote embedding service, and the search query is not sent either, unless you set `allow_remote_embedding = true` by hand while a remote provider is configured (see [The remote embedding rule](#the-remote-embedding-rule)). The vectors derive from the text, live in the same file as the text and are deleted with it.
- If the archive file is in a folder that another service shares, that service copies it; total-recall does not sync it, and deleting it with `forget` does not remove the service's copies.
- You can delete all of it, or one session or one project, at any time (see [Forgetting and unforgetting](#forgetting-and-unforgetting)), and keep it from being archived again.
- The archive is not supported with Postgres storage (`[storage] connection_string`, or `mode = "postgres"`): `enable` refuses, and the archive stays off.

See [PRIVACY.md](../PRIVACY.md) for everything total-recall stores and what can leave your machine.

## Enabling

```bash
total-recall transcripts enable
```

The command must be run in a terminal (standard input must not be redirected) and asks you to confirm. There is no flag to skip the question: a script, or a way to set it without a terminal, is a hand edit of `config.toml` (see below). Declining changes nothing.

On a yes it opens the archive (creating its tables), sets `transcripts.enabled = true` in `~/.total-recall/config.toml` and writes a marker file, `<data directory>/live/enabled`, that tells the tools (and, in a later release, the hosts' hooks) that the archive is on. The tools notice the change at once; no restart is needed. **Enabling means "archive my sessions from now on":** from the next session start the background pass reads your Claude Code session files and archives them, with no further command. `enable` itself stores nothing; it tells you to run `total-recall transcripts backfill` if you want your existing sessions archived now rather than a few at a time.

Editing `config.toml` by hand also works (`[transcripts]` `enabled = true`). The engine mirrors the setting into the marker file when it starts, so a hand edit takes effect at the next engine start. A hand edit creates no tables: only `transcripts enable` does, and until you run it `transcripts status` shows the archive as on with nothing stored, and `backfill` and `embed` refuse (they say the tables do not exist) and the pass does nothing.

To turn it off:

```bash
total-recall transcripts disable
```

`disable` needs no terminal. It sets `enabled = false` and removes the marker file. It stops the background pass too. **What the archive holds is kept**; use `transcripts forget --all` to delete it.

## Backing up Claude Code sessions

```bash
total-recall transcripts backfill [--host claude-code] [--no-embed]
```

`backfill` reads the session files Claude Code keeps on your machine and stores what the archive keeps of them, then embeds the chunks that wait for it. `claude-code` is the default and, today, the only host; any other `--host` value is a usage error. `--no-embed` skips the embedding step (the closing summary then only says how many chunks wait).

- **The embedding step.** After the ingest, unless `--no-embed` is given, `backfill` runs the same worker as `transcripts embed` in the foreground, with no time budget, until nothing is left or Ctrl+C (see [Embedding](#embedding)). It also runs when Claude Code has no session files (the host may have deleted them while chunks still wait for their vectors), and it is skipped when the ingest was interrupted. It never changes the exit code: an embed that cannot run (the gate, a lease held by another run, a failure) is reported as `N chunks wait for embedding: <reason>` in the summary. A first backfill of a large archive therefore takes as long as embedding it (see [How long it takes](#how-long-it-takes)); the ingest alone is fast.
- **No terminal and no confirmation.** It needs neither, so a script or `cron` can run it. It refuses unless the archive is on: while `[transcripts] enabled` is false it prints how to enable it, exits 1 and has not read anything from Claude Code's directory. It also refuses under Postgres storage, and when the archive tables do not exist (a hand edit of `config.toml` that never ran `enable`), where it says that `total-recall transcripts enable` creates them.
- **Where the files are.** `$CLAUDE_CONFIG_DIR/projects` when `CLAUDE_CONFIG_DIR` is set and not empty, else `~/.claude/projects`. Only files of the form `<projects>/<project directory>/<session id>.jsonl` are read, one directory level down. The projects directory itself may be a symbolic link; symbolic links beneath it are not followed. The files under `<session>/subagents/` (subagent runs) are not read, and neither is a file whose name, without `.jsonl`, is not made of letters, digits, `.`, `_` and `-` (at most 128 characters). When Claude Code has no such directory, it prints `no Claude Code sessions found at <path>` and exits 0.
- **Order and interruption.** Newest file first. It prints a progress line every 50 sessions and a summary at the end. Ctrl+C stops (the session being read is left for the next run) and prints the summary so far with an `interrupted: the remaining sessions were not processed` line; the exit code is that of the sessions done. A second Ctrl+C ends the process at once, with no summary.
- **Safe to repeat.** A second run stores nothing new. A session that grew since is read again from the start of its last exchange (see [How resume works](#how-resume-works)). Deleting a session file afterwards leaves what was stored.
- **What stays out.** A session you forgot, a session of a project you forgot, and everything before the cutoff of `forget --all` are not stored, even though their files exist (see [Forgetting and unforgetting](#forgetting-and-unforgetting)).

The summary:

```
backfill claude-code: 120 sessions seen
  new 118, updated 0, unchanged 0, excluded 2, empty 0, skipped 0, changed 0, retry 0, failed 0
  chunks: new 4000, changed 0, unchanged 0, removed 0
```

| Count | Meaning |
|---|---|
| `new`, `updated`, `unchanged` | Sessions stored for the first time, stored again because they grew, or the cutoff or their project key changed, and left as they were. |
| `excluded` | Left out because you forgot them (the session, its project, or everything before the cutoff). |
| `empty` | Nothing in the file is kept (only tool output, hook notices and the like); nothing is stored. It is looked at again on the next run. |
| `skipped` | No complete first line yet (a session being written), a file name that is not a valid session id, or a session id that appears in two project directories (the other copy is skipped). |
| `changed` | The host replaced or shortened the file. It is **kept as it was stored** and not read again; the summary names each one as `host:id`. |
| `retry` | The session changed while this run worked on it (another run, or a `forget`), twice in a row; left for the next run. |
| `failed` | An error on that file (logged). The other files are still stored. |

Further lines: `unreadable lines: N` when some lines could not be read (a line that is not valid JSON, not an object, or over 16 MiB is skipped and counted, and never fails the file), and `interrupted: ...` after Ctrl+C. Exit codes: 0 when no file failed, 1 when any file failed or the archive cannot be used, 2 for a usage error.

**A `changed` session.** The archive keeps what the host loses, so a file that was replaced or truncated does not overwrite what was stored. If you want the new version instead, the stored one has to go: run `total-recall transcripts forget --session claude-code:<id>`, then `total-recall transcripts unforget --session claude-code:<id>`, then `backfill` again. Both versions cannot be kept, and the old chunks are lost. (A replacement that is larger than the old file, starts with the same line and happens to leave an old offset on a line start is not detected: it is read on from the old offset. Claude Code appends to its files and was not seen to rewrite them.)

`transcripts status` shows the totals and, under them, one line per project and kind (at most 30 lines, then `and N more`).

## How the archive is filled

Three things fill and embed the archive. They can run at the same time, in different processes: ingest is safe to repeat, and embedding is serialised by a lease (see [Embedding](#embedding)).

1. **The session-start pass** (automatic, while the archive is on and storage is not Postgres). When a session starts, `session_start` returns at once and a background pass begins, at most once per ten minutes per process: first it ingests the host's session files (at most 15 seconds of wall time; sessions not reached wait for the next start), then it embeds waiting chunks (at most 30 seconds or 256 chunks, whichever comes first), then it stops. It runs on its own background thread with its own copy of the embedding model, limited to two intra-op threads, writes nothing to the host's output and can never fail or delay `session_start` (nothing in `session_start` waits for it). A pass that cannot start the embedding because another process holds the lease just ends.
2. **`total-recall transcripts embed`** and 3. **`total-recall transcripts backfill`**: the same two steps on demand, in the foreground and with no budget (see above and [Embedding](#embedding)).

Text comes first and vectors later: a chunk can be found by its words the moment it is stored, and by meaning once it has a vector.

## Embedding

The embedding worker turns each chunk (its whole text, header line included, which carries the project, the date and the kind) into a 384-number vector with the same bundled local model the memory store uses (`bge-small-en-v1.5`), and stores the vector next to the chunk. A chunk with no vector is found by its words only.

- **Gate.** Before any archive text is read for embedding, the worker checks, in this order, whether it may run, and `transcripts status` prints the answer: the archive is off or storage is Postgres (not applicable); the configured provider is remote and `allow_remote_embedding` is off, or the provider's vectors are not 384 numbers long, or the vectors on disk were made by another model (all three: `words only: ...`); the local model is not ready (`words only: embedding model not ready (...)`); the memory store's own re-embed is running (`waiting`, session-start pass only).
- **Budgets.** The pass: 30 seconds or 256 chunks. `embed` and `backfill`: none. The clock is checked between batches of 16 chunks, so a batch may run slightly over.
- **One run at a time.** A lease (ten minutes, renewed every batch, taken over at once when its holder on this machine is no longer running) keeps two runs from embedding the same archive. `embed` then says `another embed run is active; try again later` and exits 1; the pass just ends.
- **Failures.** An outage, a busy database or a missing model ends the run and marks no chunk bad. A chunk that fails on its own (the embedder rejects its text while it still works for a fixed probe text) gets a failed try; after three it leaves the work list and is counted as one that could not be embedded in the closing lines (those counts are estimates: up to that many). `total-recall transcripts embed --retry-failed` resets the tries of all chunks first, the way back after a cause was fixed.

```bash
total-recall transcripts embed [--retry-failed]
```

Runs the worker with no budget until nothing is left or Ctrl+C (the first press stops between batches, a second ends the process), printing `embedded N of M (K remaining)` about every five seconds. It needs no terminal (it only adds vectors) and refuses unless the archive is on and its tables exist. Exit 0 when it finished, was cancelled or found nothing to do; 1 when the gate says the worker cannot run (it prints the reason), when another run holds the lease or when the run ended on `embedding failed`; 2 for a usage error.

### The remote embedding rule

By default archive text is embedded only by the local model. If `[embedding] provider` is `openai` or `bedrock`, the archive stays words-only and **no archive text is sent to the provider**, and the search query is not sent either, until you set `[transcripts] allow_remote_embedding = true` by hand in `config.toml` (`config_set`, the web UI and `config set` refuse it). With the opt-in:

- what is sent: the text of each chunk to embed (including its header line), the search queries, and, once per run before the first batch, one fixed probe sentence that is not archive text;
- what is not sent: anything that is not a chunk's text or a query: no session metadata, titles or file names beyond what a chunk's own header line says;
- the archive holds 384-number vectors. If the provider returns another length (the configured dimensions are only a request), the probe refuses it (`words only: the provider's vectors have N dimensions; the archive holds 384`) before any archive text is sent, and every returned vector is length-checked again before it is stored.

### The model fingerprint and changing the model

The archive records which embedder made its vectors (provider, model, revision, dimensions). Search and the worker use vectors only while the running embedder matches, or while no vector exists yet. After you change the embedding model the archive is searched **by words only** and `transcripts status` says `made by <old> (not used)`; vectors of two models are never mixed. Replacing the vectors with the new model's is not built yet (a later release will); until then the only way is to forget everything and read the sessions again (`transcripts forget --all`, `transcripts unforget --cutoff`, `backfill`), which keeps only what the host's files still hold.

### How long it takes

Measured on the development machine with the real bundled model: a whole `backfill` run (ingest, model load and embedding) over the 83-chunk test corpus took 2.2 to 3.2 seconds, and a `session_start` plus its pass took 6.0 seconds in the acceptance test. These runs include the model load, so they give no per-chunk rate. As an estimate, not a measurement, a first backfill of 10,000 chunks should take under about 7 minutes; a slower machine, or the pass's two threads, takes longer. A session-start pass embeds at most 256 chunks and runs at most once per ten minutes per process, so a few thousand chunks (3,000 need about 12 passes) take a dozen or more session starts; `transcripts embed` or `backfill` does it all at once. The pass's second copy of the embedding model costs memory (the model file alone is about 133 MB); `embed` and `backfill` run in a process of their own.

### The one-time schema step

The embedding feature adds a third schema version to the archive (a vector table, triggers that delete a chunk's vector when the chunk changes or goes, and a per-chunk try count). The first command, pass or search that opens an archive of an older version applies it once, automatically, and sets every chunk's embedded flag to "not embedded" (no vector existed before). The step needs the sqlite-vec extension that ships with the engine; a connection that cannot load it cannot write the archive.

### Vectors and the write-ahead log

`forget` removes the vectors with the text and its truncating checkpoint scrubs them from the file and the write-ahead log (the text index already worked this way). A vector deleted by an *ingest* (a chunk that changed, or vanished from a rewritten session) is removed and its slot zeroed at once, but older frames of it can stay in the write-ahead log until the next ordinary checkpoint; only `forget` runs the truncating one.

## Searching the archive

The assistant searches with `transcript_search` (see [The three tools](#the-three-tools)). A hit is the whole chunk, at most 1,800 characters, so it is all the assistant sees of a past exchange until `transcript_get` arrives.

**By meaning and by words.** The query is embedded and compared with the chunks' vectors (finds paraphrases: "how did we decide to cache results" finds a chunk that says "we settled on an LRU"), and it is also run as a phrase and as a list of words against the text index (finds exact strings: an error message, an identifier, a file name). The phrase run matches the whole query as a sequence; the word run matches any of up to twelve of its tokens, ranked by BM25, one-letter tokens dropped. The query is normalised to Unicode NFC first, so an accent typed as a separate combining mark (`re` + U+0301 + `sume`) finds the same chunks as `résume` and `resume`. When the meaning search is not possible (the gate above says words only, the model is not ready, the vector search failed) the call is not an error: the answer says `"search": {"meaning": "off", "reason": "..."}` and uses words.

**Scope** (what is searched):

- `scope` given: it wins. `session` needs `session` (`<host>:<host_session_id>`; an unknown one returns no results and says so), `project` uses `project` (the key as archived, for example `owner/repo`), `all` ignores `project`.
- `scope` omitted: `session` when `session` is given, else `project` when `project` is given, else `all`. `scope=project` with no `project` is answered as `all` and `scopeNote` says so. (A later release adds a default of the current project.)
- Automated sessions (see [Project and kind](#project-and-kind)) are left out unless `include_automated` is true; a search of a named session always includes it.
- `since` (ISO-8601 instant, or a date alone read as 00:00 UTC) keeps chunks at or after it; a chunk of unknown time is found by every other filter and not by `since`. `top_k` is 1 to 50 (default 10). All filters apply inside both searches.

**Ranking** is its own, not the memory store's, and uses ranks, not absolute scores (raw similarity of real queries runs about 0.2 to 0.6, so no threshold is used anywhere):

1. Three lists are built: by meaning, by phrase and by words.
2. They are merged by reciprocal rank fusion (`1 / (60 + rank)` summed over the lists a chunk appears in), so a chunk found by several routes rises.
3. Then three multipliers on that score: a **compaction summary** x 1.25 (a summary condenses a whole stretch of work, so it is a good place to start); an **error chunk** x 1.20 when the query itself looks like an error (it contains `error`, `exception`, `failed`, `fatal`, `panic`, `traceback`, an all-caps code such as `ENOENT` or `SQLITE_BUSY`, an exit or HTTP status number, or a `...Exception` / `...Error` name), because then the failed tool output is what is wanted; and a **recency** boost of 1 + 0.10 x 2^(-age in days / 180), up to ten percent for a chunk from today, half of that (five percent) after 180 days, a quarter after a year. These started as the design's guesses and were kept: the acceptance queries (a topic, a compaction-summary-only query, an exact error string, a paraphrase inside one session) pass with them, so none was changed.
4. Sort by the final score (ties: newer, then lower id). **At most two chunks per session** among the results, so one long session cannot fill the answer, except when the scope is a single session.
5. `score` in the answer is the final score divided by the best one: a relative number, the top hit is 1.0.

**The answer**: `enabled`, `query`, `scope`, `scopeNote` (only when it changed the scope), `search` (`meaning` `on` or `off`, `reason` when off), `pendingEmbedding`, `results`, and `truncated` with `truncatedHits` when the size cap cut hits. Each result has `id` (the chunk id, which `transcript_get` will take), `session` (`<host>:<id>`), `project`, `kind`, `title`, `ts`, `chunkType` (`dialogue` or `compaction-summary`), `hasError`, `exchanges` (such as `3-4`), `via` (which of `meaning`, `phrase`, `words` found it, so an exact hit can be told from a paraphrase), `score` and `text` (the chunk body without its header line; the header's facts are fields).

- **`pendingEmbedding`** counts the chunks in the searched scope that cannot be found by meaning right now: those with no vector, or, when the meaning search is off, all of them. It tells the assistant how much of the scope was covered by words only (for example straight after enabling the archive, before the first passes finished).
- **Size cap.** The `text` of all hits together is capped at 40,000 characters (about 10,000 tokens). Hits are added in rank order until the next would not fit; `truncated: true` and `truncatedHits` (the number left out) say so. The default of ten hits is at most 18,000 characters.
- An archive that is absent or empty answers `results: []` with `note: nothing is archived yet; run total-recall transcripts backfill`. The search opens the archive per call and closes it, never opens the memory store, logs no retrieval event and writes nothing (apart from the one-time schema step, see above).

**Measured.** A words query over a synthetic archive of 100,000 chunks (a debug build, twelve common tokens) took 154 to 182 milliseconds unfiltered (237 milliseconds for the twelve commonest tokens, the worst case) and about 86 to 101 milliseconds with project, kind and `since` filters together; `forget --all` on 10,000 chunks, with the vector triggers firing per row, took about one second.

## What is kept and what is dropped

Kept:

- **Your text**: your prompts, including a prompt you typed while the agent was still working, and a slash command as plain text (`/develop 148`).
- **The assistant's text.** Two tool inputs are the assistant's own answer and are stored as assistant text, not as stubs: `StructuredOutput` (the whole answer of an SDK run, as `key: value` lines) and `ExitPlanMode` (its plan).
- **One-line tool stubs**: `[Bash: git status]`, `[Read: /path/file]`, ... one field of the tool's input chosen by tool name (a command, a file path, a pattern, a URL, a query, a description, or the first string field), folded to one line and cut to **160** characters (the tool name is cut to 64). A command you ran in the shell mode (`!`) is a stub too. For `AskUserQuestion` the stub holds the text of its first question; **the choice you made lives in the tool's result, which is dropped, so your answer to that question is not kept.** This is a known loss.
- **The error output of a failed tool call**: `[error: ...]`, cut to **500** characters, line breaks kept (a stack trace is what you will look for).
- **Compaction summaries**, as their own `compaction-summary` chunks.

Dropped:

- the output of tool calls that succeeded (file contents, command output);
- the assistant's thinking, images and any other block that is not text or a tool call;
- records the host adds to the file for itself: meta records, the work of sidechains and subagents, the results of background tasks and subagents that the host injects as a user message, and every record type that is not a user, assistant, compaction or queued-prompt record;
- inside your text: `<system-reminder>`, `<task-notification>`, the local command output and caveat blocks (`<local-command-stdout>`, `<local-command-stderr>`, `<local-command-caveat>`), `<command-message>`, `<bash-stdout>`, `<bash-stderr>`, `<EXTREMELY_IMPORTANT>` and the IDE context blocks (`<ide_...>`), with their tags, and a message that is only a host interruption notice (`[Request interrupted by user ...]`). The assistant's text is kept as written, even when it mentions these tags.

Limits. Every text is cut at **100,000** characters after redaction, with `[... N more characters not stored]` appended, so a pasted log cannot turn one prompt into thousands of chunks. A chunk's body is at most **1,800** characters: a longer exchange is split into parts, and every part after the first starts with your question again (cut to 300 characters). Each chunk begins with one header line (host, the first 8 characters of the session id, the project key, the date, the kind, the chunk type and the exchange number). A "character" is a UTF-16 code unit.

## What is redacted and what is not

Before anything is cut or split, every text that is stored (your text, the assistant's text, stub details and tool names, error text, compaction summaries and the session title) is scanned, and what is found is replaced by a marker, `[redacted:<category>]`. The text of the secret is not stored, and no part of it is left behind. Two things hold for every marker: an overlap of two findings becomes one marker, and a cut to 160, 500 or any other limit never lands inside a marker (it goes before the marker and adds `...`, or the not-stored note at the 100,000 cap, so a token that straddles the limit leaves no prefix).

| Marker | What is found | What is replaced |
|---|---|---|
| `[redacted:token]` | GitHub tokens (`ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, `github_pat_`), GitLab `glpat-`, Slack `xox[baprs]-`, AWS access key ids `AKIA...`, and `sk-` keys (including `sk-ant-`, `sk-proj-`, `sk-svcacct-`, `sk-admin-`, `sk-None-` and `sk-or-v1-` forms; at least 20 characters after the prefix (16 for the plain alphanumeric form `sk-` followed by letters and digits only), and not inside a longer word) | the token |
| `[redacted:private-key]` | a `-----BEGIN ... PRIVATE KEY-----` block | from the BEGIN line to the END line, or to the end of the text when the END line is missing |
| `[redacted:bearer]` | `Authorization: <scheme> <value>` (also with `=`, quotes, and the schemes `Bearer`, `Basic`, `Token`, `Digest`), `x-api-key: <value>`, and a bare `Bearer <value>` (a value of at least 8 characters after `Authorization` or `x-api-key`, at least 16 after a bare `Bearer`) | the value |
| `[redacted:url-credentials]` | `scheme://user:password@host` | the password only; the user name and host stay |
| `[redacted:secret-assignment]` | a value assigned (`=` or `:`, quoted or not, also JSON and its backslash-escaped form inside a command string) to a name ending in `_KEY`, `_TOKEN`, `_SECRET`, `_PASSWORD` or `_PASSWD`, or to `PASSWORD`, `PASSWD`, `SECRET`, `TOKEN`, `APIKEY` or `PASSPHRASE` (any case), and the flags `--password`, `--passwd`, `--token`, `--api-key`, `--secret`, `--auth-token` | the value (a quoted value to its closing quote, or, when more of the same shell word follows that quote, to the end of that word, as in `'It'\''s-long'`; the word ends at a space, a shell operator, a closing bracket, a comma, a backtick, a quote of the other kind, which usually closes an enclosing string (but not a piece of the other kind that holds only the value's own quote, as in `'It'"'"'s-long'`), or a quoted piece that does not close on its line; a `$(...)` or backtick command as one value; otherwise the whole run up to a space) |
| `[redacted:unprocessable]` | a text the redactor could not finish within its 5 second limit | the whole text |

A timeout **fails closed**: the whole text becomes `[redacted:unprocessable]` and nothing from it is stored. It does not happen on ordinary input.

**What is left alone.** A value is not redacted when the whole value is a reference or a placeholder: `$NAME`, `${NAME}` or `%NAME%` (unquoted or in double quotes; in single quotes a shell does not expand it, so it is redacted), `<something>`, `{{ ... }}`, `true`, `false`, `null`, `none`, `undefined`, or empty. So `GH_TOKEN="$t" gh api ...` is stored as it is. For secret assignments and URL passwords there is no exemption for a short value or by first character: `PASSWORD='$ecret!9'` is redacted. Email addresses are not redacted.

**What is over-redacted.** The rules prefer losing text to keeping a secret, so some harmless text goes too:

- a type hint or a property with a secret-sounding name: `token: string`, `apiKey: string`, `primary_key = id`, `MAX_TOKEN = 5`;
- `TOKEN= cmd`: the value after a blank is taken to be the secret and the command is lost;
- the word after `Authorization:` when it is not a scheme (`Authorization: use credentials`, `Authorization: OAuth2 authentication` lose the second word), and the names in a list such as `["Authorization", "Content-Type"]`;
- a URL whose query happens to hold `a:b@c`;
- a long `sk-...` run that is not a key.

**Residual risk: what is not detected.** Only the shapes above are found. These are stored as written:

- a password or key written in prose, or in a variable whose name is not on the list;
- a secret in a format that is not listed: cloud keys other than `AKIA`, JWTs, database passwords outside a URL or a listed variable;
- anything you paste in a different encoding (a name that is encoded twice inside a JSON string, such as `\\\"password\\\":`, is not recognised);
- a secret split across two messages;
- a multi-word value written without quotes (only the first word is replaced);
- a header value shorter than the minima in the `[redacted:bearer]` row.

Treat the archive as sensitive data. When it lives in the main database it has the permissions that file already has, which on Linux and macOS may let other local users read it; a separate `transcripts.db_path` file is created readable only by you on Linux and macOS (0600, see Unix file permissions below), and on Windows it follows your profile's permissions. Redaction is a safety net, not a guarantee. `forget` is how you remove what should not be there.

## How resume works

Each stored session remembers, as a byte position, where its last exchange started. A file that has grown is read again from there, so the last exchange is rebuilt with what was added and every earlier one is left as it is; the result equals what reading the whole file at once would give. A file that was replaced or became shorter is not read again and is reported as `changed` (see above).

## Project and kind

**Project.** The key comes from the working directory the session ran in: `owner/repo` from the repository's `origin` remote, else the folder name of the repository or, outside a repository, of the working directory, in lower case. A worktree of a repository gives the repository's key, also when the worktree was deleted afterwards (a path with a `/.claude/worktrees/<name>` or `/.zoid/worktrees/<name>` segment is cut back to the repository that held it). A session whose file records no working directory takes it from the name of Claude Code's project directory (decoded; the decoding is lossy).

**Kind.** `interactive` or `automated`, decided from the file when it is stored. A session whose `entrypoint` starts with `sdk` (the SDK runs of reviewers and subagents) is `automated`. Otherwise a session with exactly one prompt is `automated` when that prompt starts with a known automation opening (`Review this change for security`, `You previously flagged`, `Scope:`, `You are reviewing`, `Read the full review brief`, `Round <n>`) or is longer than 1,500 characters; any other session is `interactive`. When a session grows, its kind is decided again. The kind is stored so that search leaves automated runs out unless `include_automated` is set.

## Status

```bash
total-recall transcripts status
```

Needs no terminal and never creates anything. It prints the setting (`enabled = true` or `false`), whether the archive is active (the marker file is present; a difference between the setting and the marker is said, and applies at the next engine start), where the archive is, and, when the archive tables exist, the schema version, the number of sessions (interactive and automated) and chunks, one line per project and kind with its sessions and chunks (at most 30, then `and N more`), the forgotten sessions and projects with their identifiers and times, and the cutoff. After the chunk counts come the embedding lines:

```
embedding: 4,210 of 4,388 chunks embedded (178 waiting, 3 could not be embedded)
embedder:  local bge-small-en-v1.5 (ready)      or  words only: <the reason>
vectors:   made by local bge-small-en-v1.5      or  none yet  or  made by <other> (not used)
last embed run: 2026-11-21 10:44, embedded 60, 118 remaining, stopped by budget
search:    meaning and words                    or  words only
```

The embedder state is computed by the same gate the worker uses. While the archive is off, or under Postgres storage, the embedding lines read `embedder:  not applicable (the archive is off)` (or `... (postgres storage)`) and `search:    not available (the archive is off)` (or `... (postgres storage)`); under Postgres status also prints `storage: postgres, where the transcript archive is not supported`. When the archive has no tables yet it prints `on, nothing stored` or `off, nothing stored` after the location line, and nothing more. It exits 1 when the archive cannot be located or read (an invalid `db_path`, an unreadable file), and then it does not claim that nothing is stored.

## Forgetting and unforgetting

```bash
total-recall transcripts forget --session <host>:<id>
total-recall transcripts forget --project <key>
total-recall transcripts forget --all
```

Exactly one selector. The command must be run in a terminal. It prints how many sessions and chunks will be deleted and asks you to confirm ("This cannot be undone"); declining changes nothing. It works whether or not the archive is enabled, so you can disable first. With no archive tables there is nothing to forget, and it says so without creating anything.

`forget` also keeps the text from coming back:

- `--session <host>:<id>` deletes that session and records the session so it is not archived again. A session is written as the host name, a colon and the host's session id; the identifier is split at the first colon.
- `--project <key>` deletes the sessions of that project and records the project and the repository directories its sessions belong to (the `project_root` of each stored session; a project whose directory exists but holds no repository is excluded by its key only, and so is one whose root would be a filesystem root, your home directory or a directory above it), so it is not archived again. For a session that ran in a worktree outside its repository (one made with `git worktree add ../app-hotfix`, say), the directory each such session ran in (when it lies outside the repository directory) is recorded as well, so its sessions stay out after the worktree is removed; that directory is never recorded when it is a filesystem root, your home directory, a directory above your home directory or a directory above the repository directory, and a session whose project has no repository directory records no directory at all. A session that was never archived before the forget is not covered once its worktree is removed.
- `--all` deletes everything and sets a cutoff: nothing from before the moment you ran it is archived later.

The records hold identifiers, project keys and directory names, never conversation text.

The deletion is made so that the deleted text is gone from the archive file, its text index and its write-ahead log. If the write-ahead log cannot be truncated (another process holds the database), `forget` says so, exits 1 and does not print a "forgot" message: the rows are deleted, but their text may still be in the log until it is checkpointed. Run the command again when the other process is idle.

To let something be archived again:

```bash
total-recall transcripts unforget --session <host>:<id>
total-recall transcripts unforget --project <key>
total-recall transcripts unforget --cutoff
```

`unforget` needs a terminal and asks first. It removes the exclusion record (or the cutoff) and says whether there was one. **It brings nothing back**: what was forgotten stays deleted, and only later archiving is allowed again. `status` lists the forgotten sessions and projects, which are the identifiers to give `unforget`.

Exit codes of all the `transcripts` commands: 0 done (a declined confirmation also exits 0 and changes nothing), 1 failure, 2 usage error or no terminal.

## The `[transcripts]` settings

```toml
[transcripts]
enabled = false               # the archive is off unless you turn it on
db_path = ""                  # empty: the main database; else an absolute path or one starting with ~/
retention_days = 0            # 0 keeps archived text forever
allow_remote_embedding = false  # true lets a remote embedding provider embed archive text and queries (set by hand)
```

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `false` | Turns the archive on. |
| `db_path` | `""` | Where the archive lives. Empty keeps it in the main database. Otherwise a separate SQLite file, written as an absolute path or one starting with `~/` (the rules of `TOTAL_RECALL_DB_PATH`). A value that is not valid makes the archive unavailable, and `status` says why; `enable` refuses it. |
| `retention_days` | `0` | Reserved for a later release; **has no effect yet**. |
| `allow_remote_embedding` | `false` | Lets a remote embedding provider (`[embedding] provider = "openai"` or `"bedrock"`) receive archive text and search queries. Off: the archive is embedded and searched by the local model only, and with a remote provider configured it is searched by words only. It only matters if you configured a remote embedder. See [The remote embedding rule](#the-remote-embedding-rule). |

**Changing the settings.** The `transcripts` settings are changed by editing `config.toml` by hand, or by the `transcripts` commands above (`enable` and `disable` write `enabled`). `config_set`, the web UI and `total-recall config set` refuse every `transcripts.*` key, so an assistant that read a planted memory cannot turn the archive on, move it or let a remote service embed it.

## The three tools

The archive adds three MCP tools in SQLite and Cortex modes (not Postgres). They are for the assistant, not the web UI.

| Tool | Purpose |
|---|---|
| `transcript_search` | Search the archive. `query` is required; optional `scope` (`session`, `project`, `all`; omitted: see [Searching the archive](#searching-the-archive)), `project`, `session` (`<host>:<id>`), `since`, `include_automated`, `top_k` (1 to 50, default 10). |
| `transcript_get` | Read the archive: one chunk by `id`, or a range of a `session` with `from_exchange` and `to_exchange`; `context` (0 to 10, default 1). |
| `transcript_sessions` | List archived sessions. Optional `project`, `since`, `limit` (1 to 100, default 20), `include_automated`. |

`transcript_search` works as described in [Searching the archive](#searching-the-archive). `transcript_get` and `transcript_sessions` are not built yet: they only say whether the archive is on. All three read the setting each call, so `enable` and `disable` apply at once:

- off: `{"enabled":false,"hint":"Run `total-recall transcripts enable` in a terminal to turn the archive on."}`
- on, `transcript_get` or `transcript_sessions`: `{"enabled":true,"available":false,"hint":"This tool is not available in this version yet."}`

See [MCP tools](tools.md) for the full tool list.

## Unix file permissions

On Linux and macOS a separate archive file (the `db_path` file) is created readable only by you (mode 0600), with any missing parent directories created 0700, and the directory holding the marker file (`<data directory>/live/`) is created 0700, with the marker file 0600. The main database file is not changed: when the archive lives in the main database, it has the permissions that file already has.

On Windows total-recall sets nothing beyond the defaults: access follows the permissions of your user profile.

## What deleting `total-recall.db` does

When the archive lives in the main database (`db_path` empty), deleting `~/.total-recall/total-recall.db` deletes the archive together with your memories. When `db_path` names a separate file, deleting the main database leaves the archive alone; delete that file too, or use `total-recall transcripts forget --all`. Deleting by hand does not leave a cutoff or exclusion records; `forget` does.
