# Git sync, milestone 0a: store groundwork

Date: 2026-11-15. Milestone 0a of the git sync feature tracked in #93; the umbrella design is
`2026-11-15-git-sync-design.md` (see its "A transaction-composable SQLite path", "Imports" and "Delivery
plan"), and the draft criteria for this milestone are in `2026-11-15-git-sync-acceptance-criteria.md`.
This document is the spec for **this milestone only**. It adds capabilities and tests and wires no sync.

## Problem

The sync engine (later milestones) needs four things the store and the importers do not offer today:

- a way to insert an entry with a given `created_at` (insert always stamps "now"), and to update an
  entry's `scope` and `entry_type` (`Update` can set neither);
- a way to read a stored `source_tool` string without throwing on a value this build does not know;
- one transaction that covers insert, move, delete, update, the vector rows, the sticky flag and the
  import-log write together. Today `SqliteStore.Move` and `InsertWithEmbedding` each open their own
  transaction, `MoveHelpers.MoveAndReEmbed` is four separate steps with the embedding in the middle, and
  `ImportLog` writes on its own;
- imports that give the same entry the same id on every machine, and that survive being killed half-way.
  Importers insert with a random `Guid.NewGuid()` id, then embed, then insert the vector, then log the
  import, as four separate steps; a kill between them leaves an entry that the next run neither skips (no
  log row) nor can repair.

## Goals

1. The four capabilities above exist, are tested, and change no existing behavior except the id of newly
   imported **memories** (below).
2. Two machines that import the same host file get the same memory id, in every storage mode.
3. A memory import that was interrupted is repaired by the next run: one entry, one embedding, one log
   row, no error.

## Non-goals

- Any sync table, config, codec, clock, merge rule or git call (milestones 0b and later).
- Changing `IStore`, `PostgresStore`, `RoutingStore` or any existing method's behavior, or any caller of
  `MoveHelpers.MoveAndReEmbed`.
- Re-keying memories that already exist (they keep their random ids).
- Deterministic ids for **knowledge** imports. Knowledge is not synced; its ids stay random.
- Making the existing import path transactional (see "Crash-safe imports" for why a repair is used).

## Design

### Small options

- `InsertEntryOpts` gains a trailing `long? CreatedAt = null` (unix milliseconds). `SqliteStore` binds
  `created_at` to it when set and to "now" otherwise; `updated_at` and `last_accessed_at` stay "now". The
  Postgres store does not use it (git sync never runs on Postgres); its XML comment says so.
  `RoutingStore` passes the options through unchanged.
- `UpdateEntryOpts` gains `string? Scope` and `EntryType? EntryType`. `SqliteStore.Update` writes each when
  set. `PostgresStore.Update` throws `NotSupportedException` when either is set (nothing sets them today),
  so a future caller cannot be silently ignored.
- `SourceToolMapping` gains `TryParse(string? value)`, which returns `None` for `null` or an unknown
  string and never throws. `Parse` is unchanged (it keeps throwing on corruption).

### The store transaction

`SqliteStore.BeginTransaction()` returns a `SqliteStoreTransaction` (`IDisposable`). It is not on `IStore`.

Operations, all on the store's single connection and one transaction:

| Operation | Behavior |
|---|---|
| `Insert(tier, type, opts, embedding?)` | inserts the row (id from `opts.Id` or a new GUID, `CreatedAt` honored); with an embedding, also inserts the vector row, after removing an orphan vector row at that rowid, exactly as `InsertWithEmbedding` does. Returns the id. |
| `Move(fromTier, fromType, toTier, toType, id, embedding?)` | copies the row to the target table as `SqliteStore.Move` does (`updated_at` = now, every other field kept, `sticky` not carried), moves the vector row (the given embedding, else a copy of the source vector bytes; if the source has no vector row and none is given, the target gets none), then deletes the source vector row and the source row. Throws if the source row does not exist. |
| `Delete(tier, type, id)` | deletes the vector row and the content row. A missing id is a no-op. |
| `Update(tier, type, id, opts)` | the same SET clauses as `SqliteStore.Update` (the SQL is shared, not copied). |
| `SetSticky(type, id, sticky)` / `IsSticky(type, id)` | as the store's, on the hot table. |
| `Get(tier, type, id)` | reads an entry inside the transaction. |
| `LogImport(sourceTool, sourcePath, contentHash, targetEntryId, tier, type)` | writes the `import_log` row as `ImportLog.LogImport` does (`INSERT OR IGNORE`, same stable row id). |
| `Commit()` | commits. After it, every operation throws `InvalidOperationException`. |
| `Dispose()` | rolls back if `Commit` was not called. |

Rules:

- The existing `SqliteStore` methods keep their behavior. Their SQL is refactored so the transaction and
  the store share one implementation of each statement; the shared helpers take an optional transaction.
- One transaction at a time per store: `BeginTransaction()` while another is open throws
  `InvalidOperationException`. While one is open, the same store's other methods must not be called (they
  run on the same connection); the builder confirms what Microsoft.Data.Sqlite does with such a call and
  documents it in the XML comment. Nothing in this milestone calls the store that way.
- Embeddings are computed by the caller **before** `BeginTransaction()`; the transaction never calls the
  embedder.
- An internal test seam, `Action<string>? BeforeStep`, is called with a step name (`insert-row`,
  `insert-vec`, `move-copy`, `move-vec`, `delete-vec`, `delete-row`, `update`, `set-sticky`,
  `log-import`) before each statement group, so a test can throw at each step and check that the
  transaction left no partial state.
- No production code in this milestone uses the transaction; milestone 1b-i does.

### Deterministic memory ids

`ImportIds.FromHash(string contentHash)` (new, in `TotalRecall.Infrastructure.Importers`, pure) returns the
first 16 bytes of `SHA-256(UTF-8(contentHash))` formatted as a lowercase `8-4-4-4-12` hex string. The input
is the same hash the import log and `IsAlreadyImported` use. The result is not an RFC 4122 UUID (no
version or variant bits); it only has to look like the ids the store already creates and to match the id
rule of the sync design (`^[0-9a-f][0-9a-f-]{7,63}$`).

It is used for imports that insert a **memory** (`ContentType.Memory`):

- `ImportUtils.ImportMarkdownFile`, which every markdown importer calls, derives the id from the file hash
  when the content type is Memory and leaves the knowledge path unchanged;
- `HermesImporter`, which inserts each `§`-delimited piece itself, derives the id from the piece's hash.

### Crash-safe imports

A repair on retry, not a transaction: in cortex mode the importers write through `RoutingStore`, which
cannot share a transaction, and an import that spans a store write, an embedder call and a log write is
not worth making transactional for a case a retry can fix.

For a memory import, after `IsAlreadyImported(hash)` has said "not yet":

1. derive the id;
2. look for an entry with that id in every tier of the memory type (`Get` on hot, warm and cold);
3. if one exists: **skip the insert**, remember the tier it is in, and continue with step 4; otherwise
   insert it with the derived id (as today, with the target tier);
4. embed the content and call `InsertEmbedding` for the entry's tier (it already replaces an orphan
   vector row, so it is safe to repeat);
5. write the `import_log` row (`INSERT OR IGNORE`, so a repeat is harmless).

So a kill after step 3's insert, or after step 4, is repaired by the next run: one entry, one embedding,
one log row. In cortex mode step 3's skipped insert is not re-enqueued; the earlier insert already was.
The Hermes importer's per-piece loop uses the same steps, so one half-imported piece no longer raises a
primary-key error that aborts the rest of the file.

An entry that exists under the derived id but whose content differs (a user edited a memory that was
imported but never logged) is treated as already imported. The odds of a SHA-256 collision are ignored.

### Compatibility

- New **memory** imports get hash-derived ids in every storage mode. This is the only behavior change
  visible outside the new APIs. Ids are not shown to users apart from `memory_inspect` and similar output.
- Memories imported earlier keep their random ids. Knowledge imports are unchanged.
- No schema change and no migration.

## Testing

Unit tests in `tests/TotalRecall.Infrastructure.Tests`, beside the existing store and importer tests:

- **Options:** an entry inserted with `CreatedAt` keeps that value and `updated_at` is "now"; `Update`
  with `Scope` and `EntryType` changes only those columns; `PostgresStore.Update` throws for them
  (a test that does not need a Postgres server, using the argument check before any connection use, or
  skipped like the existing Postgres tests); `TryParse` returns `None` for `null` and for an unknown
  string, and the right case for each known one; `Parse` still throws on an unknown string.
- **Transaction commits:** insert with embedding, move, delete, update, sticky and `LogImport` in one
  transaction commit together; the rows, the vector rows and the import log match what the separate
  existing methods would have produced (compare against `SqliteStore.Move` and `InsertWithEmbedding` on a
  twin database).
- **Transaction rollback:** for each step name, throw from `BeforeStep` and assert the content tables, the
  vector tables and `import_log` are byte-for-byte as before `BeginTransaction()`; disposing without
  `Commit` does the same; a second `BeginTransaction()` while one is open throws; operations after
  `Commit` throw.
- **Move without an embedding** copies the source vector; with none to copy, the target has no vector row
  and nothing throws.
- **Existing behavior:** the existing store, `MoveAndReEmbed` and importer tests pass unchanged, except
  where they assert a memory import's id.
- **Deterministic ids:** `ImportIds.FromHash` returns a known value for a fixed input, always matches the
  id rule, and differs for different hashes; the same file imported into two fresh stores gives the same
  memory id (every markdown importer, and each Hermes piece); a knowledge import still gets a random id.
- **Repair:** with the insert done and the embedding and log row missing, with the insert and embedding
  done and the log row missing, and with a fully imported entry, a re-run leaves exactly one entry, one
  vector row and one log row, reports no error, and in the Hermes case finishes the remaining pieces; the
  repair also works when the entry has since moved to another tier; with `RoutingStore` as the store, the
  repair does not enqueue a second upsert.
- **AOT:** the project still publishes under `PublishAot` with no new warning.

## Risks

- Microsoft.Data.Sqlite may require every command on a connection with an open transaction to carry that
  transaction; the shared helpers take the transaction explicitly, so the transaction's own commands are
  correct either way. The builder checks what happens to a store call made while one is open and records
  it in the XML comment.
- The vector table (`vec0`) inside a transaction: `InsertWithEmbedding` already does this today, so it is a
  known-good pattern.
- Refactoring the shared SQL touches `SqliteStore` methods that many callers use; the existing tests and
  the new twin-database comparison are the guard.

## Documentation

- `CHANGELOG.md`, `### Changed` under Unreleased: new memory imports get ids derived from the import
  hash, in every mode (#93).
- `src/TotalRecall.Infrastructure/AGENTS.md` (or the nearest `AGENTS.md`), if it describes how importers
  assign ids, is updated to match.
