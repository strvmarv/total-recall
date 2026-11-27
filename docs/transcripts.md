# Transcript archive

> **Status: back up, embed, search, read and browse.** Once the archive is on, total-recall archives your Claude Code sessions in the background at each session start (and on demand with `total-recall transcripts backfill`), turns the text into vectors on your machine and lets the assistant search it, by meaning and by words, with `transcript_search`, read around a hit or a stated range of a session with `transcript_get`, and list the archived sessions with `transcript_sessions`. Backing up, embedding, searching, reading and browsing are usable together. The session that is running is kept up to date too: before each of the three tools runs, its newest text is brought into the archive, and on Claude Code (observed on Linux, not on macOS or Windows) `transcript_search` then defaults to the current project (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)). Other agent hosts arrive in a later release.

## What it is

The transcript archive is an opt-in store for the dialogue of your agent sessions: your prompts and the assistant's replies, kept so that a later session can look back at what was said, not only at the memories that were distilled from it. It is separate from the memory tiers and the knowledge base: nothing in the archive is injected at session start, and nothing in it is a memory.

It is **off by default**. Only you can turn it on, from a terminal or by editing `config.toml`. An assistant cannot enable, disable or redirect it through `config_set`, the web UI or `config set` (see [Changing the settings](#the-transcripts-settings)).

## What is stored

Only while the archive is enabled, and only from the session files of a host (today Claude Code), read at each session start by a background pass, when you run `total-recall transcripts backfill`, or, for the session that is running, just before a transcript tool runs (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)), it keeps:

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

- The archive is off until you run `total-recall transcripts enable` in a terminal and confirm. The confirmation says that, once on, total-recall reads the host's session files in the background at each session start and archives their dialogue (redacted, local, never synced), turns it into vectors on this machine (a remote service only if you set `allow_remote_embedding` by hand), and that `transcripts forget` deletes it. Once it is on, the file of the session that is running is also read just before a transcript tool runs (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)).
- The archive text is embedded by the local model on your machine. It is never sent to a remote embedding service, and the search query is not sent either, unless you set `allow_remote_embedding = true` by hand while a remote provider is configured (see [The remote embedding rule](#the-remote-embedding-rule)). The vectors derive from the text, live in the same file as the text and are deleted with it.
- If the archive file is in a folder that another service shares, that service copies it; total-recall does not sync it, and deleting it with `forget` does not remove the service's copies.
- While the archive is on, the Claude Code hook `hooks/live-marker/run.sh` writes a small marker file per Claude Code process under `<data directory>/live/` (a session id, the path of its transcript file, its working directory and a time). It stays on your machine, is never synced and never read as text (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)).
- You can delete all of it, or one session or one project, at any time (see [Forgetting and unforgetting](#forgetting-and-unforgetting)), and keep it from being archived again.
- The archive is not supported with Postgres storage (`[storage] connection_string`, or `mode = "postgres"`): `enable` refuses, and the archive stays off.

See [PRIVACY.md](../PRIVACY.md) for everything total-recall stores and what can leave your machine.

## Enabling

```bash
total-recall transcripts enable
```

The command must be run in a terminal (standard input must not be redirected) and asks you to confirm. There is no flag to skip the question: a script, or a way to set it without a terminal, is a hand edit of `config.toml` (see below). Declining changes nothing.

On a yes it opens the archive (creating its tables), sets `transcripts.enabled = true` in `~/.total-recall/config.toml` and writes a marker file, `<data directory>/live/enabled`, that tells the tools and the Claude Code hook that records the current session (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)) that the archive is on. The tools notice the change at once; no restart is needed. **Enabling means "archive my sessions from now on":** from the next session start the background pass reads your Claude Code session files and archives them, with no further command. `enable` itself stores nothing; it tells you to run `total-recall transcripts backfill` if you want your existing sessions archived now rather than a few at a time.

Editing `config.toml` by hand also works (`[transcripts]` `enabled = true`). The engine mirrors the setting into the marker file when it starts, so a hand edit takes effect at the next engine start. A hand edit creates no tables: only `transcripts enable` does, and until you run it `transcripts status` shows the archive as on with nothing stored, and `backfill` and `embed` refuse (they say the tables do not exist) and the pass does nothing.

To turn it off:

```bash
total-recall transcripts disable
```

`disable` needs no terminal. It sets `enabled = false` and removes the marker file. No new background pass starts, and a pass already running stops at its next step (before the next host's sessions are read, or before the embed step), so within the 15-second ingest budget of the host being read, or before the embed step; an embed step already under way ends at its own limit, at most 30 seconds. **What the archive holds is kept**; use `transcripts forget --all` to delete it.

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

Four things fill and embed the archive. They can run at the same time, in different processes: ingest is safe to repeat, and embedding is serialised by a lease (see [Embedding](#embedding)).

1. **The session-start pass** (automatic, while the archive is on and storage is not Postgres). When a session starts, `session_start` returns at once and a background pass begins, at most once per ten minutes per process (a system clock set back before the last pass's start counts as due): first it ingests the host's session files (at most 15 seconds of wall time; sessions not reached wait for the next start), then it embeds waiting chunks (at most 30 seconds or 256 chunks, whichever comes first), then it stops. It runs on its own background thread with its own copy of the embedding model, limited to two intra-op threads, writes nothing to the host's output and can never fail or delay `session_start` (nothing in `session_start` waits for it). A pass that cannot start the embedding because another process holds the lease just ends.
2. **`total-recall transcripts embed`** and 3. **`total-recall transcripts backfill`**: the same two steps on demand, in the foreground and with no budget (see above and [Embedding](#embedding)).
4. **The sync before a transcript tool** (automatic, while the archive is on): the running session's file only, within a few seconds, text first and, for `transcript_search`, a few chunks of vectors (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)). It uses the same ingest as the pass, so what you forgot stays out.

Text comes first and vectors later: a chunk can be found by its words the moment it is stored, and by meaning once it has a vector.

## Embedding

The embedding worker turns each chunk (its whole text, header line included, which carries the project, the date and the kind) into a 384-number vector with the same bundled local model the memory store uses (`bge-small-en-v1.5`), and stores the vector next to the chunk. A chunk with no vector is found by its words only.

- **Gate.** Before any archive text is read for embedding, the worker checks, in this order, whether it may run, and `transcripts status` prints the answer: the archive is off or storage is Postgres (not applicable); the configured provider is remote and `allow_remote_embedding` is off, or the provider's vectors are not 384 numbers long, or the vectors on disk were made by another model (all three: `words only: ...`); the local model is not ready (`words only: embedding model not ready (...)`); the memory store's own re-embed is running (`waiting`, session-start pass only).
- **Budgets.** The pass: 30 seconds or 256 chunks. `embed` and `backfill`: none. The clock is checked between batches of 16 chunks, so a batch may run slightly over.
- **One run at a time.** A lease (ten minutes, renewed every batch, taken over at once when its holder on this machine is no longer running) keeps two runs from embedding the same archive. `embed` then says `another embed run is active; try again later` and exits 1; the pass just ends.
- **Failures.** An outage, a busy database or a missing model ends the run and marks no chunk bad. A chunk that fails on its own (the embedder rejects its text while it still works for a fixed probe text) gets a failed try; after three it leaves the work list. The closing line of `embed` and `backfill` says how many chunks failed alone in that run (`N could not be embedded`), also when it was interrupted; `status` counts the chunks that reached three tries. `total-recall transcripts embed --retry-failed` resets the tries of all chunks first, the way back after a cause was fixed.

```bash
total-recall transcripts embed [--retry-failed]
```

Runs the worker with no budget until nothing is left or Ctrl+C (the first press stops between batches, a second ends the process), printing `embedded N of M (K remaining)` about every five seconds. It needs no terminal (it only adds vectors) and refuses unless the archive is on and its tables exist. Exit 0 when it finished, was cancelled or found nothing to do; 1 when the gate says the worker cannot run (it prints the reason), when another run holds the lease or when the run ended on `embedding failed`; 2 for a usage error.

### The remote embedding rule

By default archive text is embedded only by the local model. If `[embedding] provider` is `openai` or `bedrock`, the archive stays words-only and **no archive text is sent to the provider**, and the search query is not sent either, until you set `[transcripts] allow_remote_embedding = true` by hand in `config.toml` (`config_set`, the web UI and `config set` refuse it). With the opt-in:

- what is sent: the text of each chunk to embed (including its header line), the search queries, and one fixed probe sentence that is never archive text, sent before the first batch and again after a chunk fails on its own;
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

The assistant searches with `transcript_search` (see [The three tools](#the-three-tools)). A hit is the whole chunk, at most 1,800 characters, so it is all the search shows of a past exchange; `transcript_get` reads the exchanges around it (see [Reading the archive](#reading-the-archive)).

**By meaning and by words.** The query is embedded and compared with the chunks' vectors (finds paraphrases: "how did we decide to cache results" finds a chunk that says "we settled on an LRU"), and it is also run as a phrase and as a list of words against the text index (finds exact strings: an error message, an identifier, a file name). The phrase run matches the whole query as a sequence (a query longer than 32 tokens skips the phrase run and is searched by meaning and by words only); the word run matches any of up to twelve of its tokens, ranked by BM25, one-letter tokens dropped. A combining mark stays part of its word, so an accent typed as a separate combining mark (`re` + U+0301 + `sume`) finds the same chunks as `résume` and `resume`. When the meaning search is not possible (the gate above says words only, the model is not ready, the vector search failed) the call is not an error: the answer says `"search": {"meaning": "off", "reason": "..."}` and uses words.

**Scope** (what is searched):

- `scope` given: it wins. `session` needs `session` (`<host>:<host_session_id>`; an unknown one returns no results and says so), `project` uses `project` (the key as archived, for example `owner/repo`), `all` ignores `project`.
- `scope` omitted: `session` when `session` is given, else `project` when `project` is given, else **the current project when the current session is known from its marker**, else `all`. `scope=project` with no `project` means the current project when the marker gives one, else it is answered as `all` and `scopeNote` says so. `scope=session` with no `session` means the current session (known from its marker; else an argument error). `scope=all` ignores the current session. The defaults come only from a marker, never from a guessed session; the table is in [Keeping the current session up to date](#keeping-the-current-session-up-to-date).
- Automated sessions (see [Project and kind](#project-and-kind)) are left out unless `include_automated` is true; a search of a named session always includes it.
- `since` (ISO-8601 instant, or a date alone read as 00:00 UTC) keeps chunks at or after it; a chunk of unknown time is found by every other filter and not by `since`. `top_k` is 1 to 50 (default 10). All filters apply inside both searches.

**Ranking** is its own, not the memory store's, and uses ranks, not absolute scores (raw similarity of real queries runs about 0.2 to 0.6, so no threshold is used anywhere):

1. Three lists are built: by meaning, by phrase and by words.
2. They are merged by reciprocal rank fusion (`1 / (60 + rank)` summed over the lists a chunk appears in), so a chunk found by several routes rises.
3. Then three multipliers on that score: a **compaction summary** x 1.25 (a summary condenses a whole stretch of work, so it is a good place to start); an **error chunk** x 1.20 when the query itself looks like an error (it contains `error`, `exception`, `failed`, `fatal`, `panic`, `traceback`, an all-caps code such as `ENOENT` or `SQLITE_BUSY`, an exit or HTTP status number, or a `...Exception` / `...Error` name), because then the failed tool output is what is wanted; and a **recency** boost of 1 + 0.10 x 2^(-age in days / 180), up to ten percent for a chunk from today, half of that (five percent) after 180 days, a quarter after a year. These started as the design's guesses and were kept: the acceptance queries (a topic, a compaction-summary-only query, an exact error string, a paraphrase inside one session) pass with them, so none was changed.
4. Sort by the final score (ties: newer, then lower id). **At most two chunks per session** among the results, so one long session cannot fill the answer, except when the scope is a single session.
5. `score` in the answer is the final score divided by the best one: a relative number, the top hit is 1.0.

**The answer**: `enabled`, `query`, `scope`, `scopeNote` (only when it changed the scope or defaulted it), `search` (`meaning` `on` or `off`, `reason` when off), `pendingEmbedding`, `results`, `truncated` with `truncatedHits` when the size cap cut hits, `currentSession` (`marker`, `guessed` or `none`, always present), `currentSessionRef` (`<host>:<host_session_id>`, for `marker` and `guessed`) and `syncNote` (only when the catch-up of the current session was not complete). Each result has `id` (the chunk id, which `transcript_get` takes), `session` (`<host>:<id>`), `project`, `kind`, `title`, `ts`, `chunkType` (`dialogue` or `compaction-summary`), `hasError`, `exchanges` (such as `3-4`), `via` (which of `meaning`, `phrase`, `words` found it, so an exact hit can be told from a paraphrase), `score` and `text` (the chunk body without its header line; the header's facts are fields).

- **`pendingEmbedding`** counts the chunks in the searched scope that cannot be found by meaning right now: those with no vector, or, when the meaning search is off, all of them. It tells the assistant how much of the scope was covered by words only (for example straight after enabling the archive, before the first passes finished).
- **Size cap.** The `text` of all hits together is capped at 40,000 characters (about 10,000 tokens). Hits are added in rank order until the next would not fit; `truncated: true` and `truncatedHits` (the number left out) say so. The default of ten hits is at most 18,000 characters.
- An archive that is absent or empty answers `results: []` with `note: nothing is archived yet; run total-recall transcripts backfill`. The search opens the archive per call and closes it, never opens the memory store and logs no retrieval event. It writes only the current session's new text and vectors, plus the short-lived embed lease and attempt counters, brought in just before it runs (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)), and the one-time schema step (see above).

**Measured.** A words query over a synthetic archive of 100,000 chunks (a debug build, twelve common tokens) took 154 to 182 milliseconds unfiltered (237 milliseconds for the twelve commonest tokens, the worst case) and about 86 to 101 milliseconds with project, kind and `since` filters together; `forget --all` on 10,000 chunks, with the vector triggers firing per row, took about one second.

## Reading the archive

Two tools read what the archive holds, and neither needs a model: they work whenever the archive is on and its file opens, whatever the state of the embedding, and nothing they do sends anything anywhere (no embedder and no embedding provider is built or called). They open the archive for the call, read it in one read transaction and close it, so a `forget` or an ingest that commits in the middle of a call cannot mix two sessions' rows. The only thing they write is the current session's new **text**, brought in just before they read (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date); it reads that one session file of the host and makes no vectors), and the one-time schema step (see above). Nothing is kept of what was read.

### `transcript_get`

Give **either** `id` (a chunk id from `transcript_search`) **or** `session` (`<host>:<host_session_id>`, as search names it) with `from_exchange` and optionally `to_exchange` (default: `from_exchange`; a range is at most 200 exchanges wide). Also `context` (0 to 10, default 1) and `max_tokens` (200 to 20,000, default 2,000). With `id` the requested range is that chunk's exchange.

**Exchange numbers.** An exchange is the unit the adapter numbers from 0 in file order (a compaction summary counts as one). A unit that kept no text has no chunk, so the stored numbers can have gaps. `context` counts **stored** exchanges: the `context` nearest exchanges that have text before the first requested one and after the last, so a gap costs no context. A range that holds no stored exchange is not an error: the answer has no exchanges and a note says which numbers were asked and how many exchanges the session has.

**What an exchange's text is.** All parts of the exchange, joined with a blank line, without the header line each chunk starts with and without the `User (continued): ...` line the chunker repeats at the top of every part after the first (only when the exchange is dialogue that began with your text; any other text, a compaction summary that happens to start with those words included, is returned as stored). The text is what was stored, so it is already redacted.

**The answer.** `enabled`, `session`, `project`, `kind`, `title` (null when the session has none), `requested` (the asked range, such as `12-14`), `context`, `exchanges`, `approxTokens`, and, only when they say something, `truncated`, `nextFromExchange`, `nextToExchange` and `note`. Each exchange has `n`, `ts`, `chunkType`, `hasError`, `requested` (false for context) and `text`, and the exchanges are in ascending order.

**The budget.** `max_tokens` is counted as the sum, over the returned exchanges, of `ceil(characters / 4)` of each exchange's text; nothing else (the JSON around it) is counted, and `approxTokens` is that same sum. The answer is filled in this order, stopping at the first exchange that does not fit: the requested exchanges ascending, then the context, alternating one before and one after (before first), nearest first. An exchange is returned whole or not at all, with one exception: a **first** requested exchange that alone is over the budget is returned cut, ending with `[cut: raise max_tokens to read the rest]`, and the cut text with its marker still fits the budget; nothing follows it, no context included, and the note says to raise `max_tokens`. So a window larger than the budget keeps what was asked for and loses the far context first.

**`truncated`** is true when a requested exchange was left out or cut, or a context exchange was left out to fit the budget; it is not true merely because a session has no more exchanges.

**Reading on.** When requested exchanges after the last returned one were left out (the budget ran out, or an exchange was returned cut), the answer gives `nextFromExchange` (the first one not returned; after a cut exchange, the next stored requested one) and `nextToExchange` (the end of the range that was asked), and the note spells out the call. **Reading on is `session`, `from_exchange: nextFromExchange`, `to_exchange: nextToExchange` and `context: 0`**: `context: 0` because the earlier answer already ended on the exchange just before, which with the default context would be returned again. Repeating that call until no `nextFromExchange` is given reaches every requested exchange, each once; a cut exchange is the only one not complete, and the note says which.

**Errors.** Argument errors (answered as `Invalid arguments: ...`): both forms or neither; `from_exchange` or `to_exchange` with `id`, or `session` without `from_exchange`; a malformed session; `from_exchange` below 0; `to_exchange` below `from_exchange`; a range wider than 200; `context`, `max_tokens`, `from_exchange`, `to_exchange` out of range or not a whole number (`1.5` is refused, not truncated; `2.0` is accepted); an `id` below 1. A named session or chunk that is not in the archive (never archived, or forgotten) is an error result with exactly this text, whatever else is archived:

- `no archived chunk has id <N> (it may have been forgotten); use transcript_sessions to list sessions`
- `no archived session <ref>; use transcript_sessions to list sessions`

When the archive holds no chunk at all (no file, or nothing in it) the text is followed by ` (nothing is archived yet; run total-recall transcripts backfill)`, and a read of a missing archive file creates nothing. An archive that cannot be opened or read is an error result too: its text names the cause (`the transcript archive could not be opened: <why>` or `the transcript archive could not be read: <message>`); two more texts say the archive cannot be reached at all: `the transcript archive cannot be located: <message>` (its settings could not be read) and `the transcript archive is not available in this storage mode`, the engine's stderr gets the cause (the full exception chain for a failed read), and nothing read from the archive is logged.

**Automated sessions are readable.** Naming a session or a chunk is explicit intent, so `transcript_get` does not apply the automated filter (as a search of one session does). The id of an automated session comes from `transcript_sessions` with `include_automated`.

### `transcript_sessions`

Lists archived sessions, newest first by last activity (`ended_at`, else `started_at`; sessions with neither time last, then the newer id first). Arguments, all optional: `project` (the key as archived), `since` (ISO-8601 date or date-time; **active since**: last activity at or after it), `before` (the same format; last activity strictly earlier), `limit` (1 to 100, default 20), `include_automated` (default false). A date alone is read as 00:00 UTC. A session with neither time never matches `since` or `before`. There is no default project: without `project` every project is listed. Only sessions that hold at least one chunk are listed (a session a rewritten source file reset has nothing to read).

**The answer.** `enabled`, `total` (the sessions that match, before the limit), `more` (`total` minus those returned, only when above 0), `automatedHidden`, `sessions` and, only when it says something (the empty-archive note or the narrowing note), `note`. `automatedHidden` is the number of automated sessions the filters would have shown had `include_automated` been true (only when above 0 and `include_automated` is false), so an assistant can tell that reviewer and subagent sessions exist. Each session has `session` (the `<host>:<id>` that `transcript_get` takes), `project`, `kind`, `title` (the label, may be null), `startedAt`, `endedAt`, `exchanges` (the session's exchange count: valid exchange numbers are below it, some may hold no text) and `chunks` (the stored chunk count).

**Paging with `before`.** Only the newest `limit` (at most 100) sessions are listed, so when `more` is above 0 the note says to narrow with `project`, `since` or `before`. To page back, call again with `before` set to the `endedAt` (else `startedAt`) of the oldest session listed; `since` and `before` together bound a window. Times are compared in full precision, so a page boundary loses a session only when two last activities are identical to 100 nanoseconds. A session with neither time sorts last and is listed only while fewer than `limit` sessions match, for example after narrowing with `project`.

An archive with no chunk answers `total` 0, no sessions and the note `nothing is archived yet; run total-recall transcripts backfill`; a filter that matches nothing in a non-empty archive answers `total` 0 without a note. Argument errors: `limit` outside 1 to 100 or not a whole number, a malformed `since` or `before` (the text names the argument), a non-boolean `include_automated`.

**Measured.** On a synthetic archive of 100,000 chunks in 2,000 sessions, `transcript_get` of one chunk by id took a median of 1.3 ms, a window by range 1.4 ms, and a filtered `transcript_sessions` 6.8 ms. These are measurements on one machine, not a promise; the test that guards them only asserts that each answers in under one second.

## Keeping the current session up to date

Without this, text added to the session you are in would be found only after the next session-start pass. With the archive on, three things make it searchable at once, on Claude Code:

1. **A marker.** A second hook entry of the plugin (`hooks/live-marker/run.sh`, beside the pinned floor's, on `SessionStart` and on every `UserPromptSubmit`; it works with `floor_enabled = false` and with no engine provisioned) records which session each Claude Code process is running. The wrapper first tests the file `<data directory>/live/enabled` with shell built-ins: with the archive off no other process starts and nothing is written. Otherwise a small node script writes `<data directory>/live/claude-code-<pid>.json` (`<pid>` is the Claude Code process), atomically, with `v`, `host`, `host_pid`, `session_id`, `transcript_path`, `cwd` and `at` (a UTC time). It is rewritten on every prompt because a `/clear` gives the same process a new session id. The directory is 0700 and the file 0600. The marker holds a session id, the path of a transcript file, a working directory and a time: **it is local, never synced and never read by a tool as text**. When a session starts, the script also deletes the markers of processes that no longer exist or are over 30 days old (and stray temporary files over an hour old); a marker of a session that ended stays until the next session start. The hook never prints anything, always exits 0 (its timeout is 5 seconds, the script's own deadline 3) and **never ingests anything**: it only writes the marker.
2. **A sync before the read.** Before `transcript_search`, `transcript_get` or `transcript_sessions` runs (after the tool has checked that the archive is on and read its arguments, so an invalid argument costs no sync), the engine finds the current session and ingests its new text:
   - The engine finds its host by walking its own ancestor processes (at most 12; for the plugin the shim, then the host) and taking the nearest one that has a marker. Two sessions in one directory therefore each find their own file. A marker is used only if it checks out: `host_pid` equals the file name, the session id is valid, and the transcript path is under Claude Code's projects directory with exactly two components below it (`<project directory>/<session id>.jsonl`), neither a link, with no `.` or `..`; a marker written before its process started is refused (with 2 seconds of slack, when the process start time can be read). Anything else counts as no marker, and a refused marker is logged to the engine's stderr.
   - The session's file goes through the same ingest as the session-start pass and `backfill`, under a 2-second budget, so a forgotten session, a forgotten project and the cutoff of `forget --all` stay out exactly as before. A file that has not changed costs one stat and one state read; the median of a sync with nothing new was 1.0 ms on an archive of 2,000 sessions (a Debug build on Linux, `TranscriptSyncSpeedTests`).
   - `transcript_search` then embeds up to 8 of the session's waiting chunks within 2 seconds, the session's chunks first even when an older backlog waits, under the same gate as the pass (a remote provider without `allow_remote_embedding`, a model that is not ready, vectors of another model or a running re-embed of the memory store skip this step). What is not embedded is found by its words and counted in `pendingEmbedding`. `transcript_get` and `transcript_sessions` never embed: they bring in text only, so reading still needs no model and sends nothing anywhere.
   - No step starts once 6 seconds have passed in all. The archive is opened with a short lock timeout: against a held write lock a call waited about 1.2 seconds (measured) and then answered from what is stored. A second call that finds a sync running waits for it, for at most 2 seconds.
   - A failure never fails the read. It is logged to the engine's stderr, the tool answers from what is stored, and `transcript_search` says so in `syncNote`.
3. **Defaults from the marker.** With a marker, `transcript_search` fills in what you leave out:

| Arguments | With a marker | Without one (`guessed` or `none`) |
|---|---|---|
| `scope` omitted, `session` given | that session | the same |
| `scope` omitted, `project` given | that project | the same |
| `scope` omitted, neither | the current project, with `scopeNote` `defaulted to the current project (<key>)` | all |
| `scope: "project"`, no `project` | the current project | all, with `scopeNote` `scope project was asked without a project argument (the current project could not be determined), so every project was searched` |
| `scope: "session"`, no `session` | the marker's session | an argument error: `scope session needs a session argument (<host>:<host_session_id>); the current session is not known here` |
| `scope: "all"` | all | all |

An argument you give always wins. An unknown `scope` is refused before any sync. **The project is the key the archive stored for the session**, not the marker's `cwd`: the key comes from the session file's first working directory, and the `cwd` of a prompt differs after a `cd` into a subdirectory or another repository. When the archive could not be used (the sync ran out of time waiting, the archive did not open, the session is forgotten) no project is known, and the row falls back to the column on the right, except that `scope: "session"` still means the marker's session. If the current session is itself automated (every `claude -p` run is, see [Project and kind](#project-and-kind)), a default project search leaves its own hits out, because `include_automated` is false by default; a search of that named session includes it.

**What the answer says.** `currentSession` is `marker` (found by the marker), `guessed` or `none`; `currentSessionRef` is its `<host>:<host_session_id>` (absent for `none`). `syncNote` is present only when the catch-up was not complete, as one sentence beginning `the newest text of the current session may be missing:` and ending with the reason (the catch-up ran out of time, the session file could not be read, the session file was replaced or truncated since it was archived, the archive could not be opened or written (it may be busy), and so on). A call that waited for a running sync and found it complete has no note. `transcript_get` and `transcript_sessions` sync but do not report it.

**The guess.** With no marker, the engine syncs the newest Claude Code session file written within the last 30 minutes (ties by path), so a Windows user, and anyone whose host process the engine cannot match, still finds recent text. It is disclosed as `currentSession: "guessed"` and **never chooses a scope or a session**: it only decides what is synced. Older files are not guessed: they wait for the pass.

**The exchange in flight.** The sync stores the session's unfinished last exchange, which holds your prompt and the call of the search being run, so a search can rank its own prompt among its hits. The assistant knows its own prompt; the hit costs one slot of `top_k`. The session-start pass already stored a live session this way.

**Known limits.**

- **Platforms.** The marker was observed on Linux with Claude Code 2.1.330 (see [Claude Code](hosts/claude-code.md)). **macOS was not observed**: the engine reads parent processes there with `ps -o ppid= -p <pid>`, which is covered by a test with a simulated parent reader and by no run on a Mac. **Windows writes no marker** (the hook shell there is Git Bash, whose `$PPID` is not a Windows process id, and the engine has no cheap parent lookup): a Windows user gets the guess. Other hosts (Cursor, Copilot CLI, Codex, pi) write no marker, and the archive reads only Claude Code's files today.
- **No match, so the guess.** A host process started under a wrapper that does not exec, a Claude Code that sends no `transcript_path`, and a hook shell that stays as the parent of the hook (so `$PPID` is that shell and not the host) all leave the engine without a marker among its ancestors. A `claude -p` started from a tool of another session finds its own marker first; if its own were refused, the walk would reach the outer session's.
- **A reused process id.** A marker written before its process started is refused (with 2 seconds of slack, when the process start time can be read), so a marker left by an earlier process with the same id is not used. When the start time cannot be read, the check is skipped.
- **A slow first ingest.** A session file whose first ingest needs more than 2 seconds is not written (the ingest is one transaction); each call tries again and says so in `syncNote`, until the 15-second session-start pass or `backfill` brings it in.
- **A replaced file.** A session id whose file now differs from the stored one is kept as stored (`syncNote` says the file was replaced or truncated), as in `backfill`.
- **A read-only archive.** If the archive file or its `-wal` and `-shm` files are read-only, every sync reports that the archive could not be written, while the tools keep answering from what is stored.
- **Automated sessions.** An automated session is left out of project and all searches by default, so a `claude -p` run does not find its own text in a default search; name it with `scope: "session"`.

## After a compaction

When Claude Code compacts a conversation (`/compact`, or automatically), the model keeps a summary and loses the earlier text from its context, although the archive still holds it. With the archive on, the plugin tells the model so, once per compaction, and only on Claude Code:

- **The hook entry.** `hooks/hooks.json` has a second group of `SessionStart` hooks whose matcher is exactly `compact`, holding one entry, `bash "$CLAUDE_PLUGIN_ROOT/hooks/compaction-breadcrumb/run.sh"`, with a 5 second timeout. Claude Code runs it only when the source of the event is `compact`, so `startup`, `resume` and `clear` add nothing, and the script never reads the event's payload. The startup instructions are delivered by the first group as before, also on `compact`.
- **Archive off.** The script finds the data directory by the engine's rule (the one the marker wrapper uses; both scripts are tested against `hooks/live-marker/data-dir-cases.json`) and tests `<data directory>/live/enabled` with shell built-ins. Without the file it prints nothing and no process other than its own shell starts. With the file the note is printed by the shell too: no node, no engine.
- **The note** is four fixed lines, with no session id and no count of exchanges, delivered as `additionalContext`: the conversation was just compacted, but the full text of this session is archived and searchable; call `transcript_search` with `scope "session"` and a query for the topic (the answer's `currentSession` says how the session was found); read around a hit with `transcript_get`, passing the id of the result; if the answer says the current session is not known here, search again without `scope`. The note is the instruction: `skills/commands/SKILL.md`, which is sent in every session whether the archive is on or off, is unchanged.
- **No ingest.** The hook runs nothing that archives. The catch-up happens when the model searches ([sync-on-read](#keeping-the-current-session-up-to-date)); the text the compaction removed is in the host's file, so it is found. The tests run a session with a compaction boundary and find an exchange from before it with `scope: "session"` and no `session` argument (`CompactedSession_ScopeSessionNoSession_FindsTheTextTheCompactionRemoved`).
- **Windows.** The marker is not written there, so `scope: "session"` with no `session` answers that the current session is not known; the note's last line sends the model to search without `scope`, which looks at every project. The note is shown there like anywhere else (the sentinel test uses only shell built-ins).

**What was observed.** On Claude Code 2.1.332, Linux, `claude -p` (a small model), with a temporary data directory where `total-recall transcripts enable` had been run, the real hooks of this repository registered through `--settings`, a freshly built engine started by Claude Code as the MCP server and a throwaway working directory: the first run (`startup`) carried the startup instructions and no note; `claude -p "/compact" --resume <id>` ran `SessionStart:compact` once, with the original group's script and the breadcrumb script both exiting 0 and printing nothing on stderr, and the session file then held one `hook_additional_context` entry holding the note once; the next turn, resumed, counted one note in its context and, following it, `transcript_search` with `scope: "session"` and no `session` answered `currentSession: "marker"`, five results (the compaction summary and two dialogue chunks among them) holding a code word that had been written before the compaction, and no `syncNote`. A second data directory with no `enabled` file, run the same way, had the startup instructions after the compaction and no note, and created no `live` directory. A resume of a compacted session still carries the note Claude Code stored; it is not said again.

**Not observed:** an interactive `/compact`, an automatic compaction (Claude Code's documentation names `compact` as the source of a compaction), macOS, Windows, and the production chain of engine, shim and host (the engine was started directly). The note does not know whether the session is excluded from the archive (a forgotten project, a session before the cutoff): there the search finds nothing.

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
| `transcript_search` | Search the archive. `query` is required; optional `scope` (`session`, `project`, `all`; omitted: the session or project you name, else the current project when the marker gives one, else all; see [Searching the archive](#searching-the-archive)), `project`, `session` (`<host>:<id>`), `since`, `include_automated`, `top_k` (1 to 50, default 10). The answer adds `currentSession`, `currentSessionRef` and `syncNote` (see [Keeping the current session up to date](#keeping-the-current-session-up-to-date)). |
| `transcript_get` | Read the archive: the exchanges around a chunk `id`, or a range of a `session` with `from_exchange` and `to_exchange`; `context` (0 to 10, default 1), `max_tokens` (200 to 20,000, default 2,000). See [Reading the archive](#reading-the-archive). |
| `transcript_sessions` | List archived sessions, newest first. Optional `project`, `since` (active since), `before`, `limit` (1 to 100, default 20), `include_automated`. |

`transcript_search` works as described in [Searching the archive](#searching-the-archive). `transcript_get` and `transcript_sessions` work as described in [Reading the archive](#reading-the-archive). Each of the three brings the current session's newest text into the archive before it reads ([Keeping the current session up to date](#keeping-the-current-session-up-to-date)); only `transcript_search` makes vectors for it. All three read the setting each call, so `enable` and `disable` apply at once, and with the archive off none of them reads a marker, lists a file or opens the archive:

- off: `{"enabled":false,"hint":"Run `total-recall transcripts enable` in a terminal to turn the archive on."}`
- on, but the server has no archive set up (a composition that gives the tool no reader): `{"enabled":true,"available":false,"hint":"The transcript archive is not set up in this server."}`
- on and set up: the answers of [Reading the archive](#reading-the-archive) and [Searching the archive](#searching-the-archive).

See [MCP tools](tools.md) for the full tool list.

## Unix file permissions

On Linux and macOS a separate archive file (the `db_path` file) is created readable only by you (mode 0600), with any missing parent directories created 0700, and the directory holding the marker file (`<data directory>/live/`) is created 0700, with the marker file 0600 (and the current-session markers there, `claude-code-<pid>.json`, 0600). The main database file is not changed: when the archive lives in the main database, it has the permissions that file already has.

On Windows total-recall sets nothing beyond the defaults: access follows the permissions of your user profile.

## What deleting `total-recall.db` does

When the archive lives in the main database (`db_path` empty), deleting `~/.total-recall/total-recall.db` deletes the archive together with your memories. When `db_path` names a separate file, deleting the main database leaves the archive alone; delete that file too, or use `total-recall transcripts forget --all`. Deleting by hand does not leave a cutoff or exclusion records; `forget` does.
