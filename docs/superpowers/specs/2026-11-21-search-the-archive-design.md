# Search the archive by meaning and by words (Part 3 of the transcript archive): design

Parent: #145, Part 3 (docs/superpowers/specs/2026-11-18-transcript-archive-design.md)

Date: 2026-11-21. Status: draft for review. This is the sub-spec of Part 3 of the transcript archive. It follows the parent's
"Shared decisions" (above all decisions 7 and 9) and adds the detail that belongs to this part. Part 1 (opt-in, the store, `forget`,
isolation from sync) and Part 2 (`transcripts backfill`: the adapter, redaction, the chunker, per-session ingest) are merged.

## Goal

With the archive on and populated, `transcript_search` finds what was said in past sessions, **by meaning and by words**, ranked by its
own relative ranking, scoped to a session, a project or everything, with automated sessions left out unless asked for. The chunks Part 2
stores as text get vectors in the background, so a first backfill is searchable by words at once and by meaning as the vectors arrive.
Three things trigger the embedding: `transcripts backfill` (ingest, then embed), a new `transcripts embed` command, and one bounded
background pass started by each `session_start` (ingest new sessions, then embed), which never delays the call.

In scope, as the parent's Breakdown lists them: the behaviour of `transcript_search`; the vector table and its migration (decision 9's
spike, settled below); the background embedding worker with a per-pass budget, resumable, running on text alone when the embedder is not
ready, and the embedder fingerprint of decision 7; its three triggers; the merge of vector and text candidates, `pendingEmbedding`, the
ranking and the arguments of decision 9; embedding progress in `status`; the remote-embedding rule of decision 7 including the
dimension refusal; the fixture query set; the documentation and chores of decision 14. Out: `transcript_get` and `transcript_sessions`
(Part 4), default-scope resolution from the live marker and sync-on-read (Part 5), the breadcrumb (Part 6), pi (Part 7), the re-embed
after a model change, `prune`, `rescan` and `reproject` (Part 8). The two other tools stay as Part 1 left them.

## What exists today and what this part adds to it

Parts 1 and 2 give: `tx_session`, `tx_chunk` (with `embedded`, `content` = header line, a blank line, then the body, at most 1,800
characters of body), `tx_chunk_fts` (external-content FTS5 over `content`, unicode61, with insert/delete/update triggers and
`secure-delete`), `tx_forgotten`, `tx_state`; `TranscriptStore` (`Open`, `Status`, `ApplyIngest`, the `Forget*` methods); `TranscriptBackfill`
and the `transcripts backfill` command; the three stub tools (`transcript_search` answers `{enabled:true, available:false, hint}` when the
archive is on); the connection's `secure_delete` and the sqlite-vec extension loaded on every archive connection (`SqliteOpener`).
**There is no vector table, no read API of any kind on `ITranscriptStore`, no embedder, no fingerprint for the archive and no trigger other
than `backfill`.**

This part adds, inside `TotalRecall.Infrastructure.Transcripts` unless said otherwise:

- archive migration 3 (section 1);
- `TranscriptEmbedWorker` and its gate `EmbedGate` (section 3), the `ArchivePass` runner (section 4);
- `TranscriptSearch` (a service: request in, response out) and its text query builder (section 5), and a store read API for it;
- `TotalRecall.Server`: `TranscriptSearchHandler` runs the service; `SessionStartHandler` kicks the pass;
- `TotalRecall.Cli`: `transcripts embed`, the embed step of `backfill` (`--no-embed`), and the embedding lines of `status`;
- the fixture query set and tests (section 8), the documentation (section 9).

## Calls made here (the person can overrule them at sign-off)

1. **The vector table has metadata columns and no partition key.** The spike (section 1) showed that sqlite-vec 0.1.9 supports both,
   that a partition key cannot be updated (`UPDATE on partition key columns are not supported yet`), and that metadata filters keep a
   scoped query fast. `transcripts reproject` (Part 8) must rewrite the project, so the project is a metadata column.
2. **Vector rows are cleaned by triggers on `tx_chunk` and `tx_session`, not by store code.** Every delete path (forget, a vanished
   chunk in an ingest, Part 8's prune) and a changed chunk or session kind keep the vectors right with no path able to forget a step, and a
   reused chunk id can never meet an old vector. A connection that cannot load vec0 cannot write the archive at all (see section 1).
3. **A hit carries the whole chunk body** (at most 1,800 characters), not an excerpt (the person's choice: until Part 4's `transcript_get`
   exists, the hit is all the agent sees). Ten hits are about 4,500 tokens at most.
4. **`transcripts backfill` embeds after it ingests**, in the foreground, with progress and a safe Ctrl+C; `--no-embed` skips it. The
   parent's "Done when" for this part says "after backfill".
5. **The pass runs the adapters that exist** (Claude Code today; Part 7 adds pi through the same list), ingest first (at most 15 s of
   wall time), then embedding (at most 30 s), then stops; the rest waits for the next start or a command.
6. **A session-scope query does not apply the "two chunks per session" cap** (the parent says the cap is for "the top results"; inside a
   single session it would make the scope useless).
7. **`pendingEmbedding` counts the chunks in the searched scope that cannot be found by meaning right now** (no vector, or vectors
   unusable), not only the ones among the candidates. It tells the agent how much of the scope the words-only path had to cover.
8. **Embedding progress goes to the CLI `transcripts status`** only; the MCP `status` tool and `session_start`'s `backgroundTasks` are
   unchanged (they are the memory store's).
9. The rule of "text first, vectors later" has no exception: a chunk is searchable by words the moment `ApplyIngest` commits.
10. **Enabling the archive now means "archive my sessions from now on".** The parent's decision 10 says the `session_start` pass does
    nothing while the archive is off, which implies it runs while on; Part 1 and 2's enable text said "nothing is read until you
    run backfill", true only while no pass existed. The text is rewritten (section 9). The alternative, an extra "ingest
    automatically" flag set by the first backfill, was rejected: an archive meant to survive compaction would capture nothing for a
    user who enabled it and never ran a command.
11. **One embedding run at a time, ten-minute lease, dead-pid takeover on the same machine** (section 3), and a chunk is given up on
    only after three failures of its own while the embedder works for others.

## 1. The vector table, its migration and the spike

### What the spike found (2026-11-21, real `vec0.so`, sqlite-vec 0.1.9, throwaway, not committed)

- `vec0(project text partition key, ...)` creates, and metadata columns (`text`, `integer`) filter inside the nearest-neighbour search
  (`WHERE embedding MATCH ? AND k = ? AND project = ? AND session_id = ? AND kind = ? AND ts >= ?`), with exact results (checked
  against the inserted data). `IN (...)` on a partition key returns `k` rows per partition (a quirk to avoid: this design never uses it).
- Text metadata values of 21 and 36 characters (a repository slug, a UUID) insert and compare exactly.
- `UPDATE` of a metadata column works, also by `WHERE rowid IN (SELECT ...)`; `UPDATE` of a partition key column fails.
- A trigger on an ordinary table can `DELETE FROM <vec0 table> WHERE rowid = old.id`; a foreign-key cascade (`DELETE` of a session)
  fires it for every cascaded chunk; and after `DELETE`, `PRAGMA secure_delete` and `wal_checkpoint(TRUNCATE)` the deleted vector's bytes
  are no longer in the database file or the write-ahead log (zeroed by vec0).
- Latency, 100,000 vectors of 384 floats, in memory: a global search for 20 neighbours 43 ms; with a metadata filter on project, session
  or time 17 to 26 ms; a partition filter 1 ms. (A larger archive and an on-disk file are estimates, not measured.)
- A connection without the extension cannot write a table that has these triggers (`no such module: vec0`): see "Consequence" below.

### Migration 3 (`TranscriptMigrationRunner`, the archive's own list; main `_schema_version` stays 18)

It adds, in one transaction with the others of the runner:

```text
CREATE VIRTUAL TABLE tx_chunk_vec USING vec0(
    session_id integer, project text, kind text, ts integer,
    embedding float[384] distance_metric=cosine)           -- rowid = tx_chunk.id

tx_chunk_vec_ad   AFTER DELETE ON tx_chunk                 -> DELETE FROM tx_chunk_vec WHERE rowid = old.id
tx_chunk_vec_au   AFTER UPDATE OF content ON tx_chunk      -> DELETE FROM tx_chunk_vec WHERE rowid = old.id
tx_session_vec_ku AFTER UPDATE OF kind, project_key ON tx_session
                  WHEN old.kind IS NOT new.kind OR old.project_key IS NOT new.project_key
                  -> UPDATE tx_chunk_vec SET kind = new.kind, project = new.project_key
                     WHERE rowid IN (SELECT id FROM tx_chunk WHERE session_id = new.id)
tx_chunk_fts_au   (dropped and recreated) AFTER UPDATE OF content ON tx_chunk   -- was: after any update
ALTER TABLE tx_chunk ADD COLUMN embed_attempts INTEGER NOT NULL DEFAULT 0        -- failed embed tries of this chunk (section 3)
tx_chunk_reset_au AFTER UPDATE OF content ON tx_chunk -> UPDATE tx_chunk SET embedded = 0, embed_attempts = 0 WHERE id = new.id
                      -- (the inner UPDATE does not set content, so it does not fire the trigger again)
idx_tx_chunk_unembedded  ON tx_chunk(id) WHERE embedded = 0 AND embed_attempts < 3
UPDATE tx_chunk SET embedded = 0 WHERE embedded <> 0       -- no vector exists yet, so a 1 is wrong (Part 1 and 2 tests wrote it)
```

- The vector table's columns: `session_id` is `tx_chunk.session_id`; `project` is `tx_session.project_key`; `kind` is
  `tx_session.kind`; `ts` is the chunk's `ts` as epoch seconds (`0` when the text cannot be parsed, so a chunk of unknown time is not
  found by a `since` filter, and is by every other).
- The vector row of a chunk exists only while `embedded = 1`. `embedded` and the vector row are written in **one transaction** by the
  worker (section 3), and any change of a chunk's content, from any writer, resets `embedded` and the attempt count and removes the
  vector (the two triggers on `content`), so they cannot disagree after a commit.
- `tx_chunk_fts_au` narrows to `UPDATE OF content`: the existing trigger fires on any column, so marking a chunk embedded would delete and
  re-insert its text-index entry for nothing (and write the text again into the index's pages). A changed chunk (Part 2's
  `ApplyIngest` sets `content` and `embedded = 0`) still fires it. The `secure-delete` option of the index is unaffected.
- `embed_attempts` exists so one chunk that can never be embedded cannot block the ones behind it (section 3 item 6); a chunk
  whose content changes gets a fresh count, and `transcripts embed --retry-failed` resets every count (section 4). A chunk with three failed tries leaves the partial index, so the worker no longer picks
  it up; it stays `embedded = 0` (searchable by words, counted in `pendingEmbedding`) and `status` counts it as "could not be embedded".
- The embedding dimension is 384, fixed by the table (parent: "`vec0` is `float[384]` today"). A model of another size cannot fill it
  (section 3, the gate).
- **Existing tests that pin the archive's schema** (`TranscriptSchemaTests` object list and version, `TranscriptOpenTests`,
  `TranscriptStoreTests`: version 2) are updated to 3 and the new objects.
- **Consequence for connections.** Every connection that writes `tx_chunk` or `tx_session` must have loaded vec0 (all archive
  connections do: `SqliteOpener`). The test helpers that open a raw `Microsoft.Data.Sqlite` connection to an archive (`ArchiveSnapshot`, `LeakScan`,
  `TranscriptOpenTests`, `EnableCommandTests` and the `TranscriptSyncIsolationTests` rig; `TranscriptTestDb.Connect` already goes
  through `SqliteOpener`) must load it too; the builder fixes them. An older engine (Part 2's) opening a version-3
  archive finds a higher version than it knows and leaves the schema alone (the runner's loop does not run); its writes still work,
  because it loads vec0 too.
- **The runner begins its write transaction only when a migration is pending** (a small change to Part 1's `TranscriptMigrationRunner.Run`:
  read the version first). Today it starts one on every open, and a `BEGIN IMMEDIATE` on the archive (the main database by default) would
  make every `transcript_search` and `status` queue behind an ingest or a large `forget` for up to the 5 s busy timeout and fail.
- **`Open(create:false)` keeps running the archive runner** on an archive it finds (Part 1's deferred finding): a version-2 archive is
  migrated to 3 by whichever command opens it first, `status` included. This is a one-time, small, transactional change and is kept;
  the builder documents it in `docs/transcripts.md`.

### The store's read and vector API (`ITranscriptStore`, names the builder's to settle)

Not specified beyond what the sections below need: `NextUnembedded(limit)` (id, content, session ids), `StoreVectors(batch)` (the
section 3 transaction), `EmbeddingCounts(filter)`, `VectorSearch(query vector, filter, k)`, `TextSearch(match string, filter, limit)`,
`GetChunks(ids)` (with session fields), the embed fingerprint read and write, the lease. All SQL parameterised; every one opens no
transaction of its own except `StoreVectors` and the lease.

## 2. The embedder fingerprint (decision 7)

The vectors of the archive are recorded with the embedder that made them, in `tx_state` (a separate archive file has no `_meta`):
`embed.provider`, `embed.model`, `embed.revision`, `embed.dimensions` (the same four facts as `EmbedderFingerprint`, whose keys the
main store keeps in `_meta`; the archive has its own writer and reader, not `EmbedderFingerprint`, which takes an `IMetaStore`).

- **Stamping.** The worker stamps the running embedder's descriptor in the same transaction as the first vectors it stores, when no
  fingerprint is stored **or when no vector row exists** (a fingerprint left behind by a `forget` of everything is replaced, not
  held against the next model). A fingerprint is never rewritten while a vector row exists.
- **Comparison.** `Match` (the four equal), `Unstamped` (none stored, or none stored that a vector row backs: no vector of another
  model can exist), `Mismatch`. Search uses vectors only on
  `Match` or `Unstamped`, and the worker embeds only on those. On `Mismatch` the archive is searched by words alone and `status` says
  the vectors were made by another model; replacing them is Part 8's re-embed. Vectors of two models are never mixed.
- **Descriptor read is cheap.** `IEmbedder.Descriptor` never loads the model; for the local model it comes from the registry.

## 3. The embedding worker and its gate

### `EmbedGate` (decides, before any chunk text is read for embedding, whether and how the worker may run)

Inputs: the `[transcripts]` settings, the storage mode, the effective `[embedding]` config, the embedder's `Descriptor`, the stored
fingerprint, the embedder's readiness. First failing rule wins; each gives a state the CLI and `status` print:

| Order | Condition | State (`status` text) | Worker |
|---|---|---|---|
| 1 | archive off, or storage mode postgres | not applicable | does not run |
| 2 | provider is not `local` and `allow_remote_embedding` is not `true` | `words only: the embedding provider is remote and allow_remote_embedding is off` | does not run; **no archive text reaches the provider**, and the search does not embed the query either |
| 3 | `Descriptor.Dimensions` is not 384 (any provider; in practice an opted-in remote model) | `words only: the provider's vectors have N dimensions; the archive holds 384` | does not run |
| 4 | stored fingerprint `Mismatch` | `words only: vectors were made by <stored>; the running embedder is <current>` | does not run |
| 5 | the local model is not ready (files missing, wrong size) | `words only: embedding model not ready (<reason>)` | does not run; retried on the next pass |
| 6 | (server pass only) the memory store's re-embed is running (`ReindexProgress` `Running`) | `waiting: the memory re-embed is running` | defers |
| - | none | `ready` | runs |

- Rule 3 reads the configured dimensions, which for a remote provider are a request parameter the service may ignore. So **before
  the first archive batch of a run** the worker embeds one fixed probe string that is not archive text, requires the returned
  vector to have 384 floats (state 3 otherwise, run ends, nothing of the archive sent), and every vector is length-checked again
  before its insert.
- A provider other than `local` is `openai` or `bedrock` (`EmbedderFactory`); the gate reads `cfg.Embedding.Provider` (default
  `local`), not the type of the object, so a recording fake with provider `openai` is a remote one.
- **Readiness** of the local model: a new small member on the local embedder (or a static check through `ModelManager`'s existing
  structural validation) that returns a reason without loading the model. Nothing today answers it (`ModelNotReadyException` exists
  but nothing throws it, and a missing model surfaces as an `InvalidOperationException` from the first `Embed`). The gate uses the
  check **and** treats an `InvalidOperationException` from model loading (the missing-model case, the only error `OnnxEmbedder`
  throws for it) as "not ready" (state 5), so a model that passes the check but cannot be found does not fail the pass. An
  `OnnxRuntimeException` is a failure of the chunk or the model, handled by item 6, not "not ready".
- The gate is a pure function of its inputs plus the readiness result, so it is unit-tested by table.

### The worker (`TranscriptEmbedWorker.Run(store, embedder, gate, budget, ct, progress)`)

1. **Lease.** One worker per archive at a time across processes (a Claude Code engine and a pi engine can both start a pass). The
   lease is a `tx_state` row `embed.lease` = `"<machine>:<pid>:<run id>:<unix ms>"` (the run id is random per run), taken in `BEGIN IMMEDIATE` (insert, or take over when the
   stored time is more than 10 minutes old, **or** when the holder's machine name is this machine's and no process with that pid
   exists, so a second Ctrl+C or a crash does not lock others out for ten minutes; the check and write are one operation under the
   write lock, unlike the memory re-embed's advisory lock), renewed at every batch **only while the stored value still carries this run's id** (otherwise the batch's transaction rolls back and the
   run ends with `StoppedBy = lease`), released in `finally` **only if the stored value still carries this run's id** (a worker whose lease was taken over, for example a
   laptop that slept, must not delete the new holder's lease). A pass that cannot take it
   ends with state `another embed run is active` and no error. `transcripts embed` and the embed step of `backfill` take it too (their
   handling when it is held is in section 4).
2. **Batch loop.** Until the budget, the cancellation or no unembedded chunk is left: read up to 16 chunks with `embedded = 0 AND embed_attempts < 3` (index
   `idx_tx_chunk_unembedded`, ascending `id`: the order the chunks were ingested, newest sessions first within one backfill and later
   passes after earlier ones; no recency priority is attempted); embed their `content` with
   `IEmbedder.Embed` **outside any write transaction**; then one `BEGIN IMMEDIATE` transaction that (a) re-checks the gate's fingerprint
   state, (b) for each chunk runs `UPDATE tx_chunk SET embedded = 1 WHERE id = $id AND embedded = 0 AND content = $content` and, only
   when it changed one row, `INSERT INTO tx_chunk_vec(rowid, session_id, project, kind, ts, embedding) SELECT c.id, c.session_id,
   s.project_key, s.kind, COALESCE(CAST(strftime('%s', c.ts) AS INTEGER), 0), $embedding FROM tx_chunk c JOIN tx_session s ON
   s.id = c.session_id WHERE c.id = $id` (the embedding bytes are the IEEE little-endian float array `VectorSearch` writes). **The
   scope columns come from the live rows inside the write transaction, never from the read that preceded the embedding**, so a kind,
   project or time change committed in between cannot leave a vector row that disagrees with its session (the trigger of section 1
   fires only on rows that exist). (c) stamps the fingerprint when section 2 says to, (d) renews the lease. A chunk re-ingested
   between the read and the write (its `content` changed) matches nothing and is left for the next batch.
3. **What is embedded.** The chunk's `content`, header line included (it carries the project, the date and the kind, which is how a
   query like "in project X last week" can find it), which is also what the text index holds. The embedder's own truncation applies
   (a chunk is at most about 450 tokens of a 512-token window).
4. **Budget.** `EmbedBudget(MaxSeconds, MaxChunks)`; the pass uses 30 s or 256 chunks, whichever comes first, the commands none. The clock is checked between batches, so a
   batch (16 chunks) may overrun it. The worker returns `EmbedRun(Embedded, Remaining, State, StoppedBy)` where `StoppedBy` is `done`,
   `budget`, `cancelled`, `gate` or `lease`.
5. **Threading and CPU.** The pass runs the worker on its own **background** thread (`IsBackground`, so it never keeps the process alive after the host closes it) at below-normal priority with its own connection and its own
   embedder instance (the pattern of `MaybeStartBackgroundReindex`; sharing the server's instance is the builder's option if its
   tokenizer is proven thread-safe, which the code does not say). Thread priority does not limit ONNX Runtime's own intra-op thread
   pool, so the pass's local embedder is limited to a small number of intra-op threads (an option the builder adds to
   `OnnxEmbedder`, 2 as the starting value); `transcripts embed` and `backfill` keep the default. The second model instance costs its
   memory (the docs say so). It never touches the MCP stdio streams and writes nothing to stdout.
6. **Failure, and the chunk that never works.** Three classes:
   - *The embedder cannot run at all* (the model missing, as in the gate's rule 5; a returned vector that is not 384 floats, rule 3):
     the run ends with that state, and no `embed_attempts` changes.
   - *Systemic or transient* (`IOException`, `HttpRequestException`, `TaskCanceledException`/timeout, `SqliteException` BUSY or LOCKED,
     the lease lost): the batch rolls back, the run ends with state `embedding failed: <message>` (or `lease`), and no
     `embed_attempts` changes, so an outage or a busy database never marks a chunk bad.
   - *Anything else* (the embedder throws on a text, the response is malformed, the insert rejects a value): the batch rolls back, the
     failure is logged through `ExceptionLogger.LogChain`, and the batch is **retried one chunk at a time**. When a chunk fails
     alone, the worker embeds **the probe string** (the fixed non-archive text of the gate's rule 3 note): if the probe fails too,
     the embedder is the problem, so the run ends with `embedding failed: <message>` and **nothing is counted**; if the probe
     succeeds, the failure is the chunk's, it gets `embed_attempts + 1` (its own small transaction), and the run goes on. After three
     tries a chunk leaves the work list (section 1). This needs no earlier success in the run, so a bad chunk at the front of the
     queue is counted from its first failure. A run also ends, uncounted beyond what is already written, after **five consecutive
     chunks** that failed alone with a healthy probe (a failure that hits everything but the probe string must not walk the whole
     backlog to its three tries in one run).
   - *Skipping within a run:* a chunk that failed alone is not read again by the same run (the loop keeps the highest id it has
     tried and reads `id > that` first, then wraps to the lowest id once), so a run with no budget ends.
   The run's result records how many chunks it gave up on. Nothing a pass does can fail `session_start`.
7. **Last result.** After a run the worker writes `embed.last_run` (`{at, embedded, remaining, state}` JSON, no text) to `tx_state`
   for `status`.

### Cleanup is not the worker's

A chunk deleted, changed or moved to another kind or project is handled by the triggers of section 1; the worker only adds.

## 4. The three triggers

### `ArchivePass` (the `session_start` trigger)

`ArchivePass.Kick()` is called by `SessionStartHandler.ExecuteAsync` after the response is built (the `EnsureInitializedAsync` call
and everything the handler does today stay as they are). It **returns at once**: when a pass is running in this process
(`Interlocked`), or when one started less than 10 minutes ago, it does nothing; otherwise it starts the pass with `Task.Run` (a
dedicated thread for the embedding part, section 3 item 5) and returns. Nothing in `session_start` waits for it, reads a file for it or
opens the archive for it except one `File.Exists` of the sentinel (the gate that `Kick` shares with the three tools).

The pass, in order, each step inside its own try/catch that logs and goes on:

1. Re-check, as `BackfillCommand` does: `ConfigLoader.LoadTranscriptsSettings()` enabled, storage mode not postgres,
   `TranscriptStore.Open(location, create:false)` is `Opened` (anything else: stop, silently; the archive is off or absent).
2. **Ingest** (limited by time only: 15 s; Part 2's ingest has no chunk budget), for each adapter in `TranscriptAdapters.All()` (a new static list: `ClaudeCodeAdapter` today): when the adapter's
   `Detect()` is true, run `TranscriptBackfill.Run` with a cancellation token that cancels after 15 s in total for the pass (Part 2's
   run is atomic per session and newest file first; files not reached wait for the next start). A failing adapter is logged and
   never blocks the next one.
3. **Embed** through the worker with the 30 s budget, if the gate says `ready`.

The pass's own `CancellationTokenSource` belongs to `ServerCompositionHandles` and is cancelled by `Dispose`, like the re-embed's.
`SessionStartHandler` is constructed inside `BuildRegistry`, which is shared with the Postgres and test paths and documented as pure
(no I/O), and the transcript tools are registered after it returns. So `OpenSqlite` and `OpenCortexCore` build the `ArchivePass`
(from the settings and the main database path; nothing is opened until the pass runs) before `BuildRegistry` and pass it in as a new
optional parameter; it is null on the Postgres path and in tests that do not give one. (`OpenProduction` reaches `OpenSqlite`, so the pass object **exists**
on the boot path; it only exists there, it is **never started** there: `Kick` is called from `SessionStartHandler` and nowhere else.) A
test pins that Postgres gets null. **It is not
started from `Program.RunServeAsync` or `OpenProduction`** (the boot-path rule of `Program.cs`): a source-scan test pins this the way
`TranscriptSentinelIsolationTests` pins the sentinel mirror.

Two processes may each run a pass (a second host session): ingest is safe (Part 2's `ApplyIngest` conflict handling), and embedding
is serialised by the lease.

### `transcripts embed`

`total-recall transcripts embed`: runs the worker with no budget until nothing is left or Ctrl+C (the first press cancels between
batches, a second ends the process, as `backfill` does), printing `embedded N of M (K remaining)` about every 5 seconds. Gates like
`backfill`: refuses unless the archive is on, refuses on Postgres, opens with `create:false`. It is **not** terminal-gated (it reads the
archive and writes vectors; text goes only to the local model unless the person opted in by hand). Exit 0 when it finished, was
cancelled, or found nothing to do; 1 when the gate says the worker cannot run (it prints the state text of section 3), when the
lease is held by another run (it prints `another embed run is active; try again later`) or when the run ended on `embedding failed`;
2 on a usage error. Chunks that failed alone are listed in the closing line (`N could not be embedded`) and do not change the code.
`--retry-failed` first resets every chunk's `embed_attempts` to 0 (the way back for chunks given up on after a fixed cause).

### `transcripts backfill`

Unchanged until its ingest is done. Then, unless `--no-embed`, it runs the same worker (no budget, Ctrl+C-safe, progress lines). The
embed step runs **also when the adapter finds no session files** (Part 2's early "no Claude Code sessions found" return no longer ends
the command: the case where the host has deleted its files while chunks still wait is the one the archive exists for), and is
**skipped when the ingest was interrupted** by Ctrl+C (the person asked to stop). Its
exit code stays the ingest's (0, 1 or 2 as Part 2 defined it): an embed that cannot run (the gate, a held lease, a failure) is reported in the
summary ("N chunks wait for embedding: <state>") and does not change the code. `--no-embed` prints nothing about embedding but the waiting count.

## 5. `transcript_search`

### Request and defaults

The schema Part 1 registered is unchanged: `query` (required), `scope` (`session|project|all`), `project`, `session`
(`<host>:<host_session_id>`, split at the first colon), `since` (ISO-8601), `include_automated`, `top_k` (1 to 50, default 10). Only
the description changes and the schema's `default: "project"` on `scope` is removed (it would be wrong until Part 5: the effective
default is described in the tool description), which the pinned handler tests and `catalog.json` follow. A `since` that does not parse, an empty `query` or a `session` without a colon is an argument error through
`ArgumentParsing`/`ErrorTranslator` like the memory tools' own.

**Effective scope** (parent decision 9; the marker of decision 12 is Part 5's): an explicit `scope` wins; otherwise `session` when a
`session` argument is given, else `project` when a `project` argument is given, else `all`. `scope=project` with no `project`
argument is answered as `all`, with `scopeNote` saying so; `scope=session` with no `session` argument is an argument error. `scope=all`
ignores `project`. (Until Part 5 a default-scope call from Claude Code therefore searches everything, as the parent says.)

**Filters** (each applies to **both** candidate sources, inside the query, never afterwards): the session (`session_id` of the named
`<host>:<id>`; an unknown session returns no results and says so), the project (`project_key` equals the argument exactly), `since`
(chunk `ts` at or after it), and `kind = 'interactive'` unless `include_automated` **or the effective scope is `session`** (naming a session is explicit intent, and
the id of an automated session comes from Part 4's `transcript_sessions`; the kind filter is for project and all-sessions searches).
`since` accepts a full ISO-8601 instant or a date alone (read as 00:00:00 UTC).

### Candidates

1. **By meaning**, when the gate is `ready` or `Unstamped`/`Match` (section 2): `IEmbedder.EmbedQuery(query)` (the bge instruction
   prefix applies), then `SELECT rowid, distance FROM tx_chunk_vec WHERE embedding MATCH $q AND k = $k AND <filters> ORDER BY distance`
   with `k = max(40, 4 × top_k)`. Similarity is `1 - distance`. **Any failure of the meaning path at query time** (the model not
   ready, an `OnnxRuntimeException`, a remote `HttpRequestException` or timeout, a query vector that is not 384 floats, a vec0 error)
   falls back to words: `search.meaning` is `off` with the reason, the call is not an error, and a test with a throwing fake pins it.
   The store reads of one search (vector candidates, word candidates, `pendingEmbedding`, the chunk rows) run inside one read
   transaction, so a commit between them cannot pair a reused chunk id with the wrong text. The query is not embedded when the provider is remote without the
   opt-in or the fingerprint mismatches (nothing is sent anywhere), and a search that finds the embedder not ready answers by words.
2. **By words**, always: two ranked lists from `tx_chunk_fts` joined to `tx_chunk` and `tx_session` for the filters, `bm25`-ordered,
   each `LIMIT k`:
   - **the phrase**: when the query has two or more tokens, the whole query as one FTS5 phrase (`"tok tok tok"`), so an exact error
     string such as `SQLITE_BUSY: database is locked` matches as the sequence unicode61 makes of it (punctuation separates tokens
     in the query and in the text alike);
   - **the words**: the query's tokens, each quoted, joined by `OR`; tokens of one character are dropped; at most 12 tokens are used
     (the longest 12, ties by first position), so a long question stays a bounded query.
   Token and quote handling reuses `FtsSearch.SanitizeFtsQuery`'s quoting (an embedded `"` is doubled) but not its implicit AND, which
   returns nothing for a sentence. A query with no usable token has no word candidates.
   The builder measures the words query on a synthetic archive of 100,000 chunks and records the time in the PR; if an `OR` over very
   common tokens is slow it drops the commonest tokens (by `fts5vocab` document counts) first. This is a measurement and a mitigation, not an acceptance
   gate.

### Merge and ranking (its own, not the memory ranker)

Scores are used **relatively**: raw similarity ran 0.2 to 0.6 in the prototype, so no absolute threshold exists anywhere in this design.

1. Three ordered lists: V (by similarity), P (phrase hits by `bm25`), W (word hits by `bm25`).
2. **Reciprocal rank fusion**: `fused(c) = Σ 1 / (60 + rank_L(c))` over the lists L that contain `c` (rank starting at 1; P and W
   count as two lists, so a chunk that matches the phrase and the words is helped, by design).
3. Multipliers on the fused score (initial values, **untuned**; the builder tunes them against the fixture query set and records the
   final values and why in the PR):
   - compaction summary (`chunk_type = 'compaction-summary'`) × 1.25;
   - an error chunk (`has_error = 1`) × 1.20 **when the query looks like an error string** (it contains `error`, `exception`,
     `failed`, `fatal`, `panic`, `traceback`, an `E`-code such as `ENOENT`/`SQLITE_BUSY` (all-caps token with `_` or at least 4
     letters), an exit/HTTP status number after `exit`/`status`/`HTTP`, or a `.NET`/Python-style `Xxx.YyyException`/`...Error` token);
   - recency × `1 + 0.10 · 2^(-age_days / 180)` (age from the chunk `ts`; unknown time: no boost).
4. Sort by the final score (ties: newer `ts`, then lower id). **At most two chunks per session** among the returned results, unless
   the effective scope is `session` (call 6); the cap is applied walking the sorted list, taking the next chunk of a session that is
   not full, until `top_k` or no candidate is left.
5. `score` in the response is the final score divided by the best final score of this answer (the top hit is `1.0`): a **relative**
   number, documented as such.

### Response

```text
{
  "enabled": true,
  "query": "...", "scope": "all|project|session", "scopeNote": "...",   // note only when it changed the scope
  "search": { "meaning": "on" | "off", "reason": "<state text of section 3>" },   // reason only when off
  "pendingEmbedding": 12,           // chunks in the searched scope that cannot be found by meaning now
  "results": [ {
      "id": 4012,                    // tx_chunk.id (Part 4's transcript_get takes it)
      "session": "claude-code:3f2a...",  "project": "owner/repo", "kind": "interactive",
      "title": "<session title>", "ts": "2026-11-18T10:02:11Z",
      "chunkType": "dialogue" | "compaction-summary", "hasError": false,
      "exchanges": "3-4", "via": ["meaning"|"words"|"phrase", ...], "score": 1.0,
      "text": "<the chunk body, header line removed>" } ]
}
```

- `via` lists the candidate lists the chunk came from, so the agent can tell an exact phrase hit from a paraphrase.
- `text` is the part of `content` after the first blank line (the header's facts are fields). A chunk whose content has no blank line
  is returned whole.
- `pendingEmbedding` is the number of chunks in the searched scope (the same filters) that cannot be found by meaning now: when the
  meaning path is on, those with `embedded = 0`; when it is off, **all** chunks in scope (so the number is "how many chunks the words
  path alone had to cover").
- **Size cap.** The `text` of all hits together is capped at 40,000 characters (about 10,000 tokens, under Claude Code's default MCP
  output cap): hits are added in rank order until the next would exceed it, and the answer then carries `"truncated": true` with the
  number of hits left out. The default `top_k` of 10 is at most 18,000 characters, so only a large `top_k` meets it.
- The tool never opens the memory store, logs no retrieval event and writes no data (opening the archive may apply a pending schema
  step, section 1). A `TranscriptStore.Open` that returns `Refused` or `Failed` is answered as an error result carrying its message.
  An archive that
  is absent or has no chunks answers `{enabled:true, results:[], note:"nothing is archived yet; run total-recall transcripts backfill"}`.
  The off answer (`{enabled:false, hint}`) is Part 1's and unchanged.
- The handler opens the archive per call (`TranscriptStore.Open(location, create:false)`; the location from the settings and the
  main database path, as `BackfillCommand` resolves it) and closes it after: no connection is held between calls, and a `forget` in
  another process is never blocked by one.
- AOT: the response is built through the source-generated `JsonContext` (new DTOs registered there), not hand-written strings.

## 6. `status`

`transcripts status` gains these lines after the chunk counts, and loses the always-printed `search: not available in this version yet`
(three test files pin it; they are updated):

```text
embedding: 4,210 of 4,388 chunks embedded (178 waiting, 3 could not be embedded)
embedder:  local bge-small-en-v1.5 (ready)               | words only: <state text of section 3>
vectors:   made by local bge-small-en-v1.5               | none yet | made by <other> (not used)
last embed run: 2026-11-21 10:44, embedded 60, 118 remaining, stopped by budget
search:    meaning and words                              | words only
```

The embed state is computed by the same `EmbedGate` the worker uses (no embedder is constructed when its descriptor is not needed: the
remote providers' descriptors come from config, the local one from the registry). A store that cannot be read keeps Part 1's
"could not read the archive" line (a known deferred wording).

## 7. Interactions with the rest of the archive

- **`forget` and `unforget`** (Part 1): `ForgetSession`, `ForgetProject` and `ForgetAll` delete `tx_chunk` rows (or cascade from
  `tx_session`); the triggers delete the vectors in the same transaction, and `Finish`'s truncating checkpoint scrubs them (section 1:
  the spike's result). A test proves it by looking for a stored vector's bytes in the file and the write-ahead log after a forget. A chunk's vector deleted
  by an ingest (a changed or vanished chunk) is removed from the table and its slot zeroed at once, but older frames of it can stay in
  the write-ahead log until the next ordinary checkpoint; only `forget` runs the truncating one (the docs say so).
  `ForgetAll` deletes every chunk with the trigger firing per row; the builder measures it on a 10,000-chunk archive.
- **`ApplyIngest`** (Part 2) is not changed by this part except that its `UPDATE tx_chunk SET ... embedded = 0, content = ...` and its
  `DELETE`s now fire the vec triggers; a session whose `kind` flips on a resumed pass updates its vector rows' `kind` through
  `tx_session_vec_ku`. A search never sees a vector whose chunk is gone or changed, because the delete is part of the same statement's
  transaction.
- **Cortex and git sync**: nothing here is reachable from `Sync` or `GitSync`; the isolation test of Part 1 (it scans those
  directories for `tx_`, `Transcript`, `Archive`) must still pass and is extended to the new file names.
- **`session_start` and the memory tools** are untouched in behaviour: `memory_search`, `kb_search`, the config snapshot and the
  smoke test do not read the archive.
- **Parts 4 to 8**: `transcript_get` reads `GetChunks`; Part 5's sync-on-read uses the worker with a 2 s/8-chunk budget (the
  `EmbedBudget` of section 3 is the seam); Part 8's re-embed deletes the vectors and the fingerprint and runs the worker, and its
  `reproject` updates `tx_session.project_key`, which the trigger carries into the vectors.

## 8. Fixtures and tests

The corpus of Part 2 (`tests/fixtures/transcripts/claude-code/`, synthetic, `zqx` markers) stays. Part 3 adds a **query set**:
`tests/fixtures/transcripts/search/` holds hand-written Claude Code sessions (placeholders `{{CWD}}`, `{{ID}}`, `{{T+n}}` as
`ClaudeFixtureHome` renders them) with natural dialogue on distinct topics, and `queries.json` (query, scope arguments, the
`expectedSession`, the kind of query). A generator in the test project adds **filler**: 30 more short sessions across other
topics and two projects, so the corpus is about 40 sessions and about 100 chunks (so embedding with the real model takes seconds to
a minute in CI; the builder measures it and trims the filler, not the queries, if it is longer). The model is the real bge-small
(`models/bge-small-en-v1.5`, fetched by `scripts/fetch-bge-small.sh` in CI and by the worktree setup). The tests that need it use a
model-available fact attribute (built like `GitAvailableFact`): it skips with a visible reason on a machine without the model and
**fails when `CI` is set** (a skip there would let the acceptance queries pass unrun), and the tests carry no `Integration` trait, so
CI runs them.

The queries (each asserts the expected session is **in the top three** unless said otherwise):

1. a **topic / how-to** query in other words than the text uses ("how do we ship the staging site" against a session about deploying
   staging), plus a second how-to query ("how did we stop the flaky test" against a session about retrying a timeout), the parent's
   four kinds being topic, how-to, compaction summary and exact error;
2. a query answered **only by a compaction summary** (a fact present in no dialogue chunk, only in the summary);
3. an **exact error string** (words and punctuation as in an error chunk);
4. a **session-scope paraphrase** query, with the session argument, where the 39 other sessions would fill a global top-k, and
   the expected chunk is found (the headline use of decision 9);
5. an **automated** session's chunk: not in a project or all-sessions result without `include_automated`, in it with it, and found
   without the flag when its session is named in a session-scope query;
6. a **project** scope query returns only that project's sessions, and `since` excludes older chunks;
7. a chunk **without a vector** (`embedded = 0`, the worker held back by a test gate): found by its words, `pendingEmbedding` counts
   it, `search.meaning` is `on` for the others;
8. **remote provider, no opt-in**: a recording fake `IEmbedder` with provider `openai` configured; backfill, `embed`, the pass and a
   search run; the fake's recorded calls contain **no archive text and no query**, `search.meaning` is `off` with the remote reason and
   the words query still finds its chunk; with the opt-in and 384 dimensions the fake does receive chunk text and the fingerprint
   is stamped `openai`; with the opt-in and 1,536 dimensions it receives none and the state says the dimension refusal;
9. **fingerprint mismatch**: vectors stamped by one model, another model running: words only, no mixing, nothing embedded;
10. **model not ready** (the model directory missing, through the seam, not by deleting the real one): the pass and `embed` end with
    the not-ready state, chunks stay `embedded = 0`, search by words works; a fake remote provider that ignores the requested size and
    returns 768 floats receives **only the probe string**, never chunk text, and the run ends in the dimension-refusal state;
11. **triggers**: deleting a chunk, changing its content, forgetting a session, a project and everything remove the vector rows (count
    `tx_chunk_vec` and its shadow tables); a forget of a session, a project and everything leaves no stored vector's bytes in the
    file and the write-ahead log after its checkpoint; a session whose kind flips has its vector rows' kind changed, **also when the
    flip is committed between a worker's read and its write** (a test seam between them) and likewise a project or `ts` change; a
    chunk id reused after a delete meets no old vector;
12. **resume and races**: a worker stopped by the budget or by cancellation resumes with no chunk embedded twice and none lost;
    a chunk changed between read and write stays `embedded = 0`; two workers (two connections, each with its own run id) do not both embed (the lease), and a worker that lost its lease does not delete the new holder's;
    a stale lease (older than 10 minutes) is taken over, and so is one whose pid on this machine no longer exists (a live pid on
    another machine name is not); a chunk whose embedding fails alone does not block the chunks behind it, is skipped after three
    tries, stays `embedded = 0`, and gets a fresh count when its content changes; bad chunks at the **lowest ids** (one, and three in a row) are counted from their first failure while the
    rest are embedded; a fake whose probe also fails ends the run with nothing counted; five consecutive lone failures with a healthy
    probe end the run; a failed chunk is not re-read by the same run (a run with no budget ends); a transient failure (a fake that throws `HttpRequestException`, a `SQLITE_BUSY`) counts nothing and ends the run;
    `embed --retry-failed` resets the counts; a worker that lost its lease stops at its next batch; a forget of everything followed
    by a different model restamps the fingerprint instead of reporting a mismatch;
13. **through the triggers, not the worker** (the parent's rule): the queries 1 to 4 run after `transcripts backfill` (the command's
    own embed step, through `BackfillCommand` with the Part 2 seams), and again after a `session_start` on a server built with the
    archive on (an `ArchivePass` that the test awaits through its `WaitIdleAsync`/last-result seam, never the worker directly);
14. **`session_start` speed**: on the fixture store with the archive on and populated, with the pass's worker held busy by a delayed
    embedder, the **median of ten** `session_start` calls (a fresh lifecycle each, same harness) is within 10% of the median with
    the archive off; and `memory_search` returns **identical results** for a fixed query with the archive off, on and empty, on and
    populated (a deep equality of the result lists);
15. the **source-scan** tests: `Kick` is called only from `SessionStartHandler`, `ArchivePass` is not referenced from `Program.cs` or the body of `OpenProduction`, and the
    Postgres path builds the registry with no pass; nothing under `Sync` or
    `GitSync` names the new types; the three tools' registration sites are the only places that construct the search service.

Unit tests as usual: the gate table; the text query builder (quotes, operators such as `-`, `*`, `:`, `NEAR`, `OR`, empty, one
character tokens, 12-token cap); the fusion and the per-session cap; the error-string detector; the response DTOs; `since` parsing;
the lease (acquire, renew, expire, release); the store read API on a raw-SQL-seeded archive; `status` text; the `embed` command's
exit codes; `backfill --no-embed`.

## 9. Documentation (decision 14, and what goes stale)

- **Consent wording (call 10).** The enable confirmation (`EnableCommand.cs`, the prompt that says "Nothing is read until you run
  'total-recall transcripts backfill', and the archive cannot be searched yet"), `EnableCommand.cs` (also the text near line 120),
  `docs/transcripts.md` (around lines 3, 13, 25, 29 and 45: "nothing runs on its own", "enabling stores nothing by itself") and
  `PRIVACY.md` (around lines 26 and 90) say that the archive is read only on a backfill; the builder greps for every such sentence. With this part, enabling means: from now on, each
  session start reads the host's session files in the background and archives their dialogue (redacted, local, never synced), and
  turns it into vectors on this machine (or on the remote service only if `allow_remote_embedding` was set by hand). The three texts
  and their tests are rewritten to say so. No release carries the old text (the enable command is not in v4.8.0, the latest tag), so
  no one enabled under it.
- `docs/transcripts.md`: a search section (the three triggers, scope rules, ranking in words, the response, what `pendingEmbedding`
  means, the meaning-versus-words explanation, the remote-embedding rule and the opt-in, the model-change limit until Part 8, the
  one-time schema step); the statements that say search is "not available" or that `allow_remote_embedding` is "reserved: no effect
  yet" (lines near 30, 166, 207, 215, 232 today) are corrected.
- `docs/configuration.md` (the `allow_remote_embedding` rows near lines 62 and 91), `PRIVACY.md` (line near 56: the remote
  embedding rule is now enforced, and what a vector is: derived from the text, kept in the same file, removed with it), `docs/commands.md`
  (`embed`, `backfill --no-embed`), the AGENTS files that describe the archive, the three-tool descriptions and the announce files
  that list tools (`skills/commands/SKILL.md`, `skills/using-total-recall/SKILL.md`, the Kiro steering file and the host pages under
  `docs/hosts/` only where they mention the stub), and `CHANGELOG.md` (an unreleased internal note, as Part 2 did).
- `catalog.json` regenerated (`dotnet run --project src/TotalRecall.Host -- dump-catalog > catalog.json`); the handler count in
  `AGENTS.md` (54 today) does not change (no handler is added).
- Parts 1 to 3 are announced and documented as usable together; the docs say so and say that reading a hit in full waits for Part 4.

## Deviation from the parent design

Listed so the person sees each at sign-off. None edits the parent.

1. **Metadata columns, no partition key** (decision 9 left it to this part's spike: "a partition key on the project (and a metadata
   column for the session), or, if that is not available, an over-fetch"). Both are available; the partition key cannot be updated,
   and `reproject` must rewrite the project. The over-fetch fallback is not built.
2. **Triggers keep the vectors right** (the parent only says deletes must scrub vector shadow tables). It also changes the existing
   text-index trigger to `UPDATE OF content`.
3. **`pendingEmbedding`** counts chunks in scope without a usable vector, not "candidates that were text-only" (call 7).
4. **No per-session cap inside a session-scope query** (call 6).
5. **The query is not embedded either** when the provider is remote without the opt-in (the parent's rule is that archive text is not
   sent; the query is the user's text, and the words path needs no vector, so nothing is sent at all).
6. **`transcripts backfill` embeds** (the parent lists backfill as a trigger of the worker) and gains `--no-embed`; `embed` is
   not terminal-gated (it only adds vectors).
7. **The whole chunk body is returned** per hit; the parent leaves the hit's shape to this part.
8. **Words candidates are an `OR` of tokens plus a phrase list**, not the memory FTS's implicit AND, which returns nothing for a sentence.
9. **A session-scope query ignores the automated filter** (the parent applies `include_automated` to the search as a whole): a named
   session is explicit intent.
10. **A per-chunk `embed_attempts` count** (an extra column) so one chunk that cannot be embedded does not block the rest.

11. **The effective scope is inferred** from a `session` or `project` argument when `scope` is not given (the parent's default is
    `project`), and the schema's `default: "project"` on `scope` is dropped.
12. **The pass has a chunk budget for embedding (256) but ingest is limited by time only** (decision 7 says time and chunk budget
    for the pass; Part 2's backfill has no chunk limit to bound it by).
13. **Embedding progress is shown in the CLI `status` only** (call 8).
14. **A response is capped at 40,000 characters of text** with a `truncated` flag (the parent leaves the hit's shape to this part).

## Acceptance (the parent's Done when, mapped)

| Ticket criterion | Test (section 8) |
|---|---|
| a topic query, a compaction-summary-only query and an exact error string each return the right session in the top three, after backfill or the `session_start` pass (not by calling the worker directly) | queries 1, 2, 3, run by 13 |
| a session-scope paraphrase query still finds its chunk with many other sessions present | 4 (run by 13) |
| an automated session appears only with `include_automated` | 5 |
| a chunk without a vector is found by its words and counted in `pendingEmbedding` | 7 |
| remote provider and no opt-in: a recording fake receives no archive text and word search works | 8 |
| `session_start` within 10% of its time with the archive off (median of ten), and `memory_search` identical | 14 |

## Left to the builder

The names of the store methods and DTOs; the exact tokenisation of the query; the final multipliers (against the query set) and
whether the fusion constant 60 stays; the pass's two budgets (15 s, 30 s) and the batch size 16, tuned after measuring the embed
rate on the fixtures (the parent's 3 chunks a second is an estimate for one ingest alone, and at that rate one pass embeds about 90
chunks, so the builder records the real rate and says in the docs how long a first backfill takes); whether the pass shares the
server's embedder; the readiness check's exact form; how the test helpers load vec0; the words-query measurement and its mitigation;
the layout of `status`.
