# Git sync milestone 0a: store groundwork Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the four store and importer capabilities that git sync builds on (caller-chosen creation time and scope/entry-type edits, a tolerant source-tool reader, an all-or-nothing SQLite store transaction, stable crash-safe memory import ids), wiring no sync.

**Architecture:** Small options on `InsertEntryOpts`/`UpdateEntryOpts`; a new `SqliteStoreTransaction` whose statements live in shared static helpers that `SqliteStore` also calls (so there is one SQL implementation); a pure `ImportIds.FromHash`; one shared `ImportUtils.InsertOrRepairMemory` used by `ImportMarkdownFile` (memory path) and the Hermes memory-section loop.

**Tech Stack:** .NET 10, C# with the F# `TotalRecall.Core` types (`Entry`, `Tier`, `ContentType`, `EntryType`, `SourceTool`), Microsoft.Data.Sqlite, sqlite-vec (`*_vec` tables), xUnit, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-15-git-sync-store-groundwork-design.md` (read-only; the umbrella design is `2026-11-15-git-sync-design.md`).

## Global Constraints

- Test project: `tests/TotalRecall.Infrastructure.Tests`, namespace `TotalRecall.Infrastructure.Tests` (sub-namespaces by folder). New test classes carry **no** `[Trait("Category","Integration")]` (CI runs `--filter "Category!=Integration"`); they use `SqliteConnection.Open(":memory:")` plus `MigrationRunner.RunMigrations(conn)` like `Sync/RoutingStoreTests.cs`.
- Existing `SqliteStore` methods keep their behavior; `IStore`, `RoutingStore`, `MoveHelpers.MoveAndReEmbed` and its callers are untouched.
- The store transaction is `SqliteStore`-only and is not on `IStore`.
- Ids derived for imports match `^[0-9a-f][0-9a-f-]{7,63}$`; the derived form is the first 16 bytes of `SHA-256(UTF-8(contentHash))` as lowercase `8-4-4-4-12` hex.
- AOT-safe: no reflection serializers, no new NuGet packages.
- Commit messages: one plain imperative sentence, no prefixes, **no `Co-Authored-By` or any trailer**.
- Run tests with `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "<filter>" -nologo` from the worktree root; a full build first if needed.

## Review Focus

Inputs and failure modes the spec implies that no task's own tests would otherwise cover; each has its test in the named task.

1. A failure injected at `insert-vec`, then a later successful insert that reuses the rolled-back rowid, must leave the right vector for the new row (Task 2).
2. `Move` to the same tier and type: the store's `Move` throws (primary-key violation); the transaction's `Move` must throw too and change nothing (Task 4).
3. `Insert` with an id that already exists throws and the transaction rolls back with no partial vector row (Task 2).
4. The same text in Hermes `MEMORY.md` and `USER.md` is one memory (global hash dedupe), and its derived id does not collide with a piece of different text (Task 7).
5. Moving a sticky hot memory to warm and back to hot must not carry the sticky flag (Task 4).

---

### Task 1: Small options, Postgres guards, tolerant source-tool reader

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Storage/IStore.cs` (`InsertEntryOpts`, `UpdateEntryOpts`)
- Modify: `src/TotalRecall.Infrastructure/Storage/SqliteStore.cs` (`BindInsertParameters`, `Update`, `SourceToolMapping`)
- Modify: `src/TotalRecall.Infrastructure/Storage/PostgresStore.cs` (`Insert`, `InsertWithEmbedding`, `Update`)
- Test: `tests/TotalRecall.Infrastructure.Tests/Storage/StoreOptionsTests.cs` (create)

**Interfaces:**
- Produces: `InsertEntryOpts` gains a trailing `long? CreatedAt = null` (unix ms). `UpdateEntryOpts` gains `string? Scope { get; init; }` and `EntryType? EntryType { get; init; }`. `internal static FSharpOption<SourceTool> SourceToolMapping.TryParse(string? value)` (`None` for null or unknown, never throws; `Parse` unchanged).

- [ ] **Step 1: Write the failing tests** in `StoreOptionsTests`:
  - `Insert_WithCreatedAt_KeepsItAndStampsUpdatedAtNow`: `Insert(Tier.Warm, ContentType.Memory, new InsertEntryOpts("c", CreatedAt: 1_000L))`; `Get` → `CreatedAt == 1000`, `UpdatedAt >= startMs` (taken before the insert), `LastAccessedAt >= startMs`. Same for `InsertWithEmbedding` with a `FakeEmbedder` vector.
  - `Insert_WithoutCreatedAt_StampsNow`: `CreatedAt >= startMs`.
  - `Update_ScopeAndEntryType_ChangesOnlyThose`: insert with `Scope: "a"`, `EntryType: EntryType.Correction`, tags `["t"]`; `Update(... new UpdateEntryOpts { Scope = "b", EntryType = EntryType.Decision })`; `Get` → `Scope == "b"`, `EntryType.IsDecision`, `Content` and `Tags` unchanged.
  - `PostgresStore_Insert_WithCreatedAt_Throws`, `PostgresStore_InsertWithEmbedding_WithCreatedAt_Throws`, `PostgresStore_Update_WithScope_Throws`, `PostgresStore_Update_WithEntryType_Throws`: build `new PostgresStore(NpgsqlDataSource.Create("Host=localhost;Database=x"), "owner")` (nothing connects before the guard) and `Assert.Throws<NotSupportedException>`.
  - `SourceToolMapping_TryParse`: `null` and `"nope"` give `FSharpOption<SourceTool>.None` without throwing; each of `claude-code, copilot-cli, opencode, cursor, cline, hermes, manual` gives the matching case; `SourceToolMapping.Parse("nope")` still throws `ArgumentException`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~StoreOptionsTests"`. Expected: FAIL (compile errors on the new members).
- [ ] **Step 3: Implement** the members above. `SqliteStore.BindInsertParameters` binds `$created_at` to `opts.CreatedAt ?? now` (leave `updated_at` and `last_accessed_at` as `now`). `SqliteStore.Update` adds `scope = $scope` when `opts.Scope is not null` and `entry_type = $entry_type` (via `EntryTypeMapping.ToDbValue`) when `opts.EntryType is not null`. `PostgresStore.Insert`/`InsertWithEmbedding` throw `NotSupportedException("CreatedAt is not supported by the Postgres store")` and `Update` throws `NotSupportedException` for `Scope`/`EntryType`, each as the first statement after the null check. `TryParse` mirrors `Parse`'s switch and returns `None` in the default arm.
- [ ] **Step 4: Run** the same filter, then `--filter "FullyQualifiedName~SqliteStore|FullyQualifiedName~Importer"`. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Add CreatedAt, scope and entry-type update options and a tolerant source-tool reader"`.

### Task 2: Store transaction core: insert with vector, get, commit and rollback

**Files:**
- Create: `src/TotalRecall.Infrastructure/Storage/SqlRows.cs` (internal static helpers, shared SQL)
- Create: `src/TotalRecall.Infrastructure/Storage/SqliteStoreTransaction.cs`
- Modify: `src/TotalRecall.Infrastructure/Storage/SqliteStore.cs` (`Insert`, `InsertWithEmbedding`, `Get` delegate to `SqlRows`; add `BeginTransaction`)
- Create: `tests/TotalRecall.Infrastructure.Tests/TestSupport/StoreSnapshot.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Storage/SqliteStoreTransactionTests.cs` (create)

**Interfaces:**
- Produces:
  - `SqliteStore.BeginTransaction() : SqliteStoreTransaction` (throws `InvalidOperationException` when one is already open on that store).
  - `public sealed class SqliteStoreTransaction : IDisposable` with `string Insert(Tier, ContentType, InsertEntryOpts, ReadOnlyMemory<float>? embedding = null)`, `Entry? Get(Tier, ContentType, string id)`, `void Commit()`, `void Dispose()` (rolls back if not committed, and releases the store's "open" flag), `internal Action<string>? BeforeStep { get; set; }`. Any call after `Commit` or `Dispose` throws `InvalidOperationException`.
  - `internal static class SqlRows`: `InsertRow(MsSqliteConnection, SqliteTransaction?, string table, string id, InsertEntryOpts, long now)`, `ReadRow(MsSqliteConnection, SqliteTransaction?, string table, string id) : Entry?` (moved from the private row reader), and `internal static class VecRows` inside it: `InsertWithOrphanCleanup(conn, tx, vecTable, long rowid, ReadOnlyMemory<float> embedding)` (deletes any vector row at that rowid, then inserts), `Delete(conn, tx, vecTable, long rowid)`, `ReadBytes(conn, tx, vecTable, long rowid) : byte[]?`. `SqliteStore` methods call these with `tx = null` or their own transaction; behavior unchanged.
  - Step names passed to `BeforeStep` by `Insert`: `insert-row` (before the content row), `insert-vec` (before the vector row).
  - `StoreSnapshot.Take(MsSqliteConnection conn, bool maskTimes = false) : string`: a stable text dump of every `(tier, type)` content table (`MigrationRunner.TableName`) ordered by `id` (with `created_at`, `updated_at`, `last_accessed_at` replaced by `*` when `maskTimes`), for each such table the `id` plus the hex of the vector joined by rowid (and the count of vector rows), and the `import_log` rows.

- [ ] **Step 1: Write the failing tests** in `SqliteStoreTransactionTests` (helper `NewStore()` returns a migrated `:memory:` connection and its `SqliteStore`; embeddings from `FakeEmbedder`):
  - `Commit_InsertWithEmbedding_PersistsRowAndVector` (content row present; `SELECT count(*) FROM hot_memories_vec` is 1 and its rowid equals the content row's rowid).
  - `Insert_WithoutEmbedding_HasNoVectorRow`.
  - `Insert_HonorsIdAndCreatedAt`.
  - `Dispose_WithoutCommit_RollsBack` (snapshot equals the snapshot taken before `BeginTransaction`).
  - `BeginTransaction_WhileOpen_Throws`, and `BeginTransaction_AfterCommitOrDispose_Works`.
  - `OperationsAfterCommit_Throw` (`Insert`, `Get`, `Commit` each throw `InvalidOperationException`).
  - `FailureAtInsertRow_RollsBack` and `FailureAtInsertVec_RollsBack` (set `tx.BeforeStep = s => { if (s == "insert-vec") throw new InvalidOperationException("injected"); }`, expect the exception, dispose, snapshot equals the original).
  - Review Focus 1: after `FailureAtInsertVec_RollsBack`, a second transaction inserts a different entry with its own embedding and commits; the vector read back for it (via the joined snapshot) is that embedding.
  - Review Focus 3: `Insert_WithExistingId_ThrowsAndRollsBack`.
  - `Insert_MatchesStoreInsertWithEmbedding`: the same `InsertEntryOpts` (with fixed `Id` and `CreatedAt`) inserted through the transaction on one database and through `SqliteStore.InsertWithEmbedding` on a twin give equal `StoreSnapshot.Take(conn, maskTimes: true)`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SqliteStoreTransactionTests"`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** `SqlRows`/`VecRows` by moving the SQL out of `SqliteStore.Insert`, `InsertWithEmbedding` (the vector part, including the orphan cleanup) and `Get` (taking an optional transaction and setting `cmd.Transaction` when given), then `SqliteStoreTransaction` and `SqliteStore.BeginTransaction` (one `SqliteTransaction`, an `_open` flag on the store cleared in `Commit`/`Dispose`; call `BeforeStep` before each statement group). Write the `StoreSnapshot` helper. Confirm in a one-line note in the XML comment of `BeginTransaction` what Microsoft.Data.Sqlite does when another command on the same connection runs while a transaction is open, and document the rule "no other code may use this connection while a store transaction is open".
- [ ] **Step 4: Run** the filter above and `--filter "FullyQualifiedName~SqliteStore|FullyQualifiedName~VectorSearch|FullyQualifiedName~Importer"`. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Add the SQLite store transaction with insert, get, commit and rollback"`.

### Task 3: Transaction update, delete, sticky and import-log

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Storage/SqlRows.cs` (add `UpdateRow`, `DeleteRow`, `SetStickyRow`, `IsStickyRow`)
- Modify: `src/TotalRecall.Infrastructure/Storage/SqliteStore.cs` (`Update`, `Delete`, `SetSticky`, `IsSticky` delegate)
- Modify: `src/TotalRecall.Infrastructure/Storage/SqliteStoreTransaction.cs`
- Modify: `src/TotalRecall.Infrastructure/Telemetry/ImportLog.cs` (shared static `LogImportRow`)
- Test: `tests/TotalRecall.Infrastructure.Tests/Storage/SqliteStoreTransactionTests.cs`

**Interfaces:**
- Consumes: Task 2's `SqlRows`, `VecRows`, `BeforeStep`, `StoreSnapshot`.
- Produces: on `SqliteStoreTransaction`: `void Update(Tier, ContentType, string id, UpdateEntryOpts)`, `void Delete(Tier, ContentType, string id)` (deletes the vector row, resolved by the content rowid before the content row goes; a missing id is a no-op), `void SetSticky(ContentType, string id, bool sticky)`, `bool IsSticky(ContentType, string id)`, `void LogImport(string sourceTool, string sourcePath, string contentHash, string targetEntryId, Tier, ContentType)`. Steps: `update`, `delete-vec`, `delete-row`, `set-sticky`, `log-import`. `SqlRows.UpdateRow(conn, tx, table, id, UpdateEntryOpts, now)` builds the same SET clauses the store did; `ImportLog.LogImportRow(conn, tx, sourceTool, sourcePath, contentHash, targetEntryId, Tier, ContentType)` is the existing `INSERT OR IGNORE` with the existing stable row id, and `ImportLog.LogImport` calls it.

- [ ] **Step 1: Write the failing tests** (same class): `Update_Commits_AndMatchesStoreUpdate` (twin comparison, masked times); `Update_ScopeAndEntryType_Work`; `Delete_RemovesRowAndVector`; `Delete_MissingId_IsNoOp`; `FailureAtDeleteVec_RollsBackBothRows` and `FailureAtDeleteRow_RollsBack` (snapshot equal to the original, vector row still present); `SetSticky_IsSticky_RoundTrip` and `SetSticky_RollsBackOnDispose`; `LogImport_WritesSameRowAsImportLog` (twin: `ImportLog.LogImport` on one database, `tx.LogImport` on the other, equal snapshots with times masked, and `ImportLog.IsAlreadyImported(hash)` true after commit); `LogImport_RollsBackOnFailureAtLogImport`; `FailureAtUpdate_RollsBack`; `FailureAtSetSticky_RollsBack`.
- [ ] **Step 2: Run** the class. Expected: FAIL.
- [ ] **Step 3: Implement** by extracting the four row statements from `SqliteStore` into `SqlRows` (store methods delegate with `tx = null`), `ImportLog.LogImportRow`, and the transaction methods.
- [ ] **Step 4: Run** the class plus `--filter "FullyQualifiedName~ImportLog|FullyQualifiedName~SqliteStore"`. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Add update, delete, sticky and import-log operations to the store transaction"`.

### Task 4: Transaction move, and the all-steps rollback sweep

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Storage/SqlRows.cs` (add `MoveRow`)
- Modify: `src/TotalRecall.Infrastructure/Storage/SqliteStore.cs` (`Move` delegates to `SqlRows.MoveRow` inside its own transaction)
- Modify: `src/TotalRecall.Infrastructure/Storage/SqliteStoreTransaction.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Storage/SqliteStoreTransactionTests.cs`

**Interfaces:**
- Consumes: Tasks 2 and 3.
- Produces: `void Move(Tier fromTier, ContentType fromType, Tier toTier, ContentType toType, string id, ReadOnlyMemory<float>? embedding = null)` on the transaction. Steps in order: `move-copy` (read the source row, insert the target row exactly as `SqliteStore.Move` does: `updated_at` = now, every other column kept, `sticky` not carried), `move-vec` (target vector = the given embedding, else the source vector bytes; none if the source has none and none is given), `delete-vec` (source vector), `delete-row` (source row). Throws `InvalidOperationException` when the source row does not exist. `SqlRows.MoveRow(conn, tx, fromTable, toTable, id, now)` is the copy-then-delete SQL moved out of `SqliteStore.Move`.

- [ ] **Step 1: Write the failing tests**: `Move_CopiesRowAndVector` (hex of the vector equal at the target; source row and vector gone; `created_at` kept); `Move_WithEmbedding_UsesIt`; `Move_NoSourceVectorNoEmbedding_TargetHasNone`; `Move_MissingSource_ThrowsAndChangesNothing`; Review Focus 5 `Move_StickyHotToWarmAndBack_DoesNotCarryStickyFlag` (SetSticky true in hot, move to warm, move back to hot, `IsSticky` false); Review Focus 2 `Move_SameTierAndType_ThrowsAndChangesNothing`; `Move_MatchesStoreMoveAndMoveAndReEmbed` (twin databases: the transaction move with `embedding: e` versus `SqliteStore.Move` + `MoveHelpers.MoveAndReEmbed` with a `FakeEmbedder`: equal snapshots with times masked); `FailureAtEachMoveStep_RollsBack` (loop over `move-copy`, `move-vec`, `delete-vec`, `delete-row`); and `AllStepsSweep`: a helper runs one transaction doing `Insert` (with embedding), `Update`, `SetSticky`, `Move` hot to warm, `LogImport`, `Delete`, counting `BeforeStep` calls to get the total N with no failure (then commits and checks the final snapshot), and then for each k in 1..N re-runs it on a fresh database throwing at the k-th step and asserts the snapshot equals the one taken before `BeginTransaction`.
- [ ] **Step 2: Run** the class. Expected: FAIL.
- [ ] **Step 3: Implement** `MoveRow` by moving the insert-then-delete SQL from `SqliteStore.Move` (which keeps its own `BeginTransaction`, `Commit`, rollback), then the transaction's `Move`.
- [ ] **Step 4: Run** the class plus `--filter "FullyQualifiedName~SqliteStore|FullyQualifiedName~MoveHelpers|FullyQualifiedName~HotTierCompactor|FullyQualifiedName~Importer"`. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Add move to the store transaction and a rollback sweep over every step"`.

### Task 5: Stable import ids

**Files:**
- Create: `src/TotalRecall.Infrastructure/Importers/ImportIds.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/ImportIdsTests.cs` (create)

**Interfaces:**
- Produces: `public static class ImportIds { public static string FromHash(string contentHash); }` in namespace `TotalRecall.Infrastructure.Importers`. Pure; throws `ArgumentNullException` for null.

- [ ] **Step 1: Write the failing tests**: `FromHash_KnownValue` (`FromHash("abc") == "ba7816bf-8f01-cfea-4141-40de5dae2223"`); `FromHash_MatchesIdRule` (regex `^[0-9a-f][0-9a-f-]{7,63}$` and length 36 for several real hashes from `ImportLog.ContentHash`); `FromHash_IsDeterministic_AndDiffersByHash`; `FromHash_Null_Throws`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ImportIdsTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `SHA256.HashData(Encoding.UTF8.GetBytes(contentHash))`, first 16 bytes to lowercase hex, formatted `8-4-4-4-12`.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Add the hash-derived import id helper"`.

### Task 6: Shared crash-safe memory import and the markdown memory path

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Importers/ImportUtils.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/ImportRepairTests.cs` (create)

**Interfaces:**
- Consumes: `ImportIds.FromHash` (Task 5); `IStore.Get/Insert`, `IEmbedder.Embed`, `IVectorSearch.InsertEmbedding`, `IImportLog.LogImport`.
- Produces: `internal readonly record struct MemoryImportResult(string EntryId, Tier Tier, bool Repaired);` and `internal static MemoryImportResult ImportUtils.InsertOrRepairMemory(IStore store, IEmbedder embedder, IVectorSearch vectorSearch, IImportLog importLog, string sourceToolName, string sourcePath, string contentHash, Tier tier, InsertEntryOpts opts)`. It derives the id, looks for `Get(t, ContentType.Memory, id)` in `tier` first then Hot, Warm, Cold; if found it skips the insert and uses that tier (`Repaired = true`); otherwise it inserts `opts with { Id = id }`, and if that insert throws and a second lookup now finds the entry it carries on as found, else rethrows the original exception. It then embeds the **stored** content when the entry pre-existed (else `opts.Content`), calls `InsertEmbedding(entryTier, Memory, id, embedding)`, and `LogImport(sourceToolName, sourcePath, contentHash, id, entryTier, Memory)`. `ImportMarkdownFile` calls it when `contentType.IsMemory` (tier and options as today) and keeps the existing flow for knowledge; its outcome for a repaired import is still `Imported`.

- [ ] **Step 1: Write the failing tests** in `ImportRepairTests` (fixture: migrated `:memory:` connection, `SqliteStore`, `VectorSearch`, `ImportLog`, `FakeEmbedder`, a temp dir; no Integration trait):
  - `New_InsertsEmbedsAndLogs`: id equals `ImportIds.FromHash(hash)`, one entry, one vector row, one log row, `Repaired == false`.
  - `Repair_EntryOnly`, `Repair_EntryAndVectorNoLog`, `Repair_FullyImported_Idempotent` (pre-insert the entry with the derived `Id` directly through `store.Insert`; after the call: one entry, one vector row, one log row, `Repaired == true`).
  - `Repair_EntryMovedToOtherTier`: entry pre-inserted in `Hot`, target `Warm`: result tier Hot, vector in `hot_memories_vec`, log row tier hot, no entry in warm.
  - `Repair_EmbedsStoredContent`: stored text differs in length from the imported text; the vector equals `FakeEmbedder.Embed(stored)`.
  - `Race_InsertThrowsButEntryAppears_CarriesOn` and `Race_InsertThrowsAndNoEntry_Rethrows` with NSubstitute `IStore` (`Get` returns null for the first three lookups then an entry; `Insert` throws) and substitute `IVectorSearch`/`IImportLog`.
  - `ImportMarkdownFile_Memory_UsesDerivedId_AndRerunRepairs`: import a temp file as `ContentType.Memory`; id equals `FromHash(ImportLog.ContentHash(raw))`; delete the log row and the vector row by SQL; call `ImportMarkdownFile` again: `Imported`, one entry, one vector, one log row.
  - `ImportMarkdownFile_Knowledge_KeepsRandomId` (id differs from `FromHash`, and two imports of the same file into two fresh stores differ).
  - `Legacy_RandomIdWithLogRow_IsSkipped`: insert an entry with a random id and `LogImport` its hash; `ImportMarkdownFile` returns `Skipped`, one entry, id unchanged.
  - `ClaudeCode_MemoryFile_DerivedId_ReferenceFile_RandomId`: build a `ClaudeCodeImporter` over a temp tree like `ClaudeCodeImporterTests` (one `type: user` memory file and one `type: reference` file), run `ImportMemories()`; the memory's id is derived, the `reference` entry (cold knowledge) is not.
  - `RoutingStore_NewImportEnqueuesOneUpsert_RepairEnqueuesNone`: `RoutingStore(new SqliteStore(conn), Substitute.For<IRemoteBackend>(), new SyncQueue(conn))`: a new import yields exactly one queued upsert; pre-inserting the entry through the local store and then importing yields none.
  - Cross-store: the same `InsertOrRepairMemory` call works with `RoutingStore` and the NSubstitute store above (the shared code uses only `IStore`, `IVectorSearch`, `IImportLog`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ImportRepairTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** `InsertOrRepairMemory` and the `contentType.IsMemory` branch in `ImportMarkdownFile` (the existing try/catch and the knowledge path stay as they are).
- [ ] **Step 4: Run** the filter plus `--filter "FullyQualifiedName~Importer|FullyQualifiedName~ImportUtils|FullyQualifiedName~ImportLog"`. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Give memory imports hash-derived ids and repair half-finished imports on retry"`.

### Task 7: Hermes memory sections use the shared helper

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Importers/HermesImporter.cs` (`ImportMemoryFile`)
- Test: `tests/TotalRecall.Infrastructure.Tests/HermesMemoryIdTests.cs` (create)

**Interfaces:**
- Consumes: `ImportUtils.InsertOrRepairMemory` (Task 6).
- Produces: each `§`-delimited piece (trimmed, non-empty, not already in the import log) is imported through the helper with its piece hash, tier `Warm`, the existing options (summary of the first 200 characters, `SourceTool.Hermes`, tags, project, `EntryType.Imported`); `imported++` for both inserted and repaired pieces.

- [ ] **Step 1: Write the failing tests** (`HermesMemoryIdTests`, fixture as in `HermesImporterTests` but without the Integration trait): `TwoFreshStores_SamePieces_SameIds`; Review Focus 4 `SameTextInMemoryAndUser_IsOneMemory_AndIdsDifferForDifferentText`; `HalfImportedPiece_IsRepaired_AndRestImported` (pre-insert piece 1 with its derived id and no vector or log; `ImportMemories()` reports `Imported == 2` for a two-piece `MEMORY.md`, one entry per piece, one vector and one log row each, no errors); `EmbedFailureThenRerun_NoDuplicates` (`ThrowingEmbedder(throwOnCall: 1)` on the first run: the error is reported; a second run with `FakeEmbedder` leaves exactly one entry, vector and log row per piece); `HermesKnowledge_KeepsRandomId` (`ImportKnowledge()` of `SOUL.md`: id not derived).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~HermesMemoryIdTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** by replacing the insert, embed, `InsertEmbedding` and `LogImport` sequence in the piece loop with one `InsertOrRepairMemory` call.
- [ ] **Step 4: Run** the filter plus `--filter "FullyQualifiedName~Hermes"`. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Use the shared crash-safe memory import for Hermes memory sections"`.

### Task 8: Documentation

**Files:**
- Modify: `CHANGELOG.md` (`### Changed` under `## Unreleased`)
- Modify: `src/TotalRecall.Infrastructure/AGENTS.md` (and any `AGENTS.md` or doc under `docs/` that says how imported memories get ids, found with `grep -rn "Guid\|import_log\|random id" --include=*.md`)

**Interfaces:**
- Consumes: the behavior of Tasks 1 to 7.
- Produces: a changelog entry stating that new memory imports (Claude Code memory files and Hermes memory sections) get ids derived from the import hash in every storage mode, that knowledge imports and existing memories keep their random ids, and that an interrupted memory import is repaired by the next run (#95, #93); a short paragraph in the Infrastructure `AGENTS.md` on `ImportIds`, `ImportUtils.InsertOrRepairMemory` and `SqliteStore.BeginTransaction`.

- [ ] **Step 1: Write the entry and the paragraph.** Match the style of the neighbouring changelog entries (bold lead sentence with the issue number, then detail).
- [ ] **Step 2: Run the repository's verify commands**, in order, from the worktree root, each expecting exit 0: `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`; `npm run test:shim` (run `npm ci` first if `node_modules` is absent); the catalog drift guard (`dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q` then `dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -`).
- [ ] **Step 3: Check the AOT publish** adds no new warning: `dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true -nologo -v q 2>&1 | grep -i "warning IL"` returns no line that is new relative to `origin/main` (run the same command on a clean `origin/main` worktree if any line appears, and compare).
- [ ] **Step 4: Commit** `git add -A && git commit -m "Document hash-derived memory import ids and the store transaction"`.
