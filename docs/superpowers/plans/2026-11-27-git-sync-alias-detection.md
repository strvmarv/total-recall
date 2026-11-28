# Git sync alias detection and skip-insert (milestone 1b-iii part 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pulled record whose `origin.hash` maps in this machine's `import_log` to a different, existing local memory is recorded as an alias and not inserted; a machine holding the non-canonical id reads the disappearance of that record under an open alias as convergence onto the canonical record (one transaction, tier, tags and pin carried, a differing local text kept in a `conflict` item or a held `pin-edit`), never as a deletion.

**Architecture:** A pure Core module `SyncAlias` holds the canonical rule, the group computation, the plain `Ignore` that replaces an insert (`skipInstead`, like `SyncDeletion.holdInstead`) and the choice of the converging and ignored ids. `GitSyncAliases` is the plain SQL of `git_sync_alias` and the `import_log` lookups, and plans the skip's one write as an `AliasPlan` (planned before `BEGIN IMMEDIATE`, recomputed inside, a difference defers). The applier decides the skip per visit inside `ApplyCore`; a pre-pass before the delete-guard preview computes the converging and ignored ids (excluded from the preview and from the unproven fallback hold); a converging id is carried out by the existing tail of `ApplyCore` (extracted as `PlanAndCarry`) on the canonical id after a new store step re-keys the entry (`SqliteStoreTransaction.RekeyMemory`) and its `conflict` item, so the unchanged no-base merge, `PinPlanning.Plan` and `ConflictItems` do all the carrying.

**Tech Stack:** .NET 10, C# (Infrastructure) and F# (Core), xUnit (C# tests) and Expecto (Core tests: a `testList` name must contain the text the `--filter "FullyQualifiedName~..."` uses, e.g. `testList "SyncAlias.detect"`), SQLite with sqlite-vec and FTS5, a bare git repository in tests.

**Spec:** `docs/superpowers/specs/2026-11-25-git-sync-alias-detection-design.md` (approved at `9472835bb3923ed99ce4d117bb338438764fccd6`; read it first; decisions quoted as "D<n>", convergence conditions as "condition <n>"). Parent: `2026-11-21-git-sync-review-secrets-design.md` (Shared decisions 1 to 9, Part 5). Umbrella: `2026-11-15-git-sync-design.md` ("Legacy alias resolution", the pull tables). Format and neighbours: the Part 4 plan `docs/superpowers/plans/2026-11-24-git-sync-conflict-review.md`.

## Global Constraints

- The approved spec is read-only for the whole run: `git diff --quiet 9472835bb3923ed99ce4d117bb338438764fccd6 HEAD -- docs/superpowers/specs/2026-11-25-git-sync-alias-detection-design.md` must exit 0 before every push.
- No schema change, no migration, no new index (`git_sync_alias` of migration 18 is used as it is; the open-row reads are scans of the small table; `import_log` is read through the existing `idx_import_log_content_hash` and `idx_import_log_target_entry_id`). Deviation list: none.
- `SyncMerge.decide`, `MergeInput`, the record format and every `SyncMergeTests` case stay unchanged. The skip and the ignore are changes of the decision after `decide` returns (`SyncAlias.skipInstead`, `SyncAlias.ignored`).
- `ApplySummary` gains no field: a skip is a `NoOp` (counted `Unchanged`), a convergence is `Updated`.
- Only `GitSyncCycle*.cs` may name the cycle and only `GitSyncPusher*`/`GitSyncCycle*` files may name the pusher (also in doc comments: `GitSyncAliases.cs`, `GitSyncApplier.Alias.cs`, `GitSyncApplier.Converge.cs` name neither); `GitSyncCycleCallerTests` stays unchanged and green.
- The `sync` verbs stay hidden; nothing new in README, user docs, catalog, hooks or plugin files (there is no new verb or tool).
- No AI attribution or co-author trailer in any commit message. No `--force` push. Never commit `TestResults` hang dumps. No secret literal in any source file: tokens come from `FakeSecrets` (`tests/TotalRecall.Infrastructure.Tests/Transcripts/Support/FakeSecrets.cs`; this part needs none).
- No new warning in `dotnet build src/TotalRecall.sln -c Release`.
- Test projects: Core `tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`; Infrastructure `tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj`.
- Dev loop (each task): `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~Sync"`; `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync|FullyQualifiedName~SqliteStoreTransaction|FullyQualifiedName~ReviewService|FullyQualifiedName~Alias"`; the release build adds no warning.
- Where a task changes behaviour an existing test pins, change that test in the same task and name it in the commit message (D7 expects none; watched: `GitSyncApplierGuardTests`, `GitSyncApplierUnprovenTests`, `GitSyncApplierSafetyTests` (transaction counts), `GitSyncApplierPullTests` (blob reads, faults keyed by blob sha at 608-653)).
- Ids are compared ordinally everywhere (`String.CompareOrdinal`, `StringComparer.Ordinal`; F# `compare` on strings is ordinal).

## Review Focus

1. **A deferral loop after the re-key.** The plan made before the transaction reads `x`'s entry and `x`'s conflict item and presents them as `c`'s (`LocalRead.Present` of `x`, `conflictX with { EntryId = c }`); after `RekeyMemory` and `RekeyConflictItem` the ordinary re-read of `c` must equal them exactly, or every pull defers and `L` never moves. Task 6: `ADifferentText_TakesTheCanonicalText_AndFilesTheLocalTextAsAConflict` (asserts `Deferred` empty and `Complete`), `TheOthersConflictItem_IsReKeyed_AndALoserEqualToTheAppliedTextIsDropped`, `AfterConvergence_ARepeatedPullIsANoOp`.
2. **An open alias read as a deletion.** The preview (W6.4 would count it), the unproven fallback hold (W6.5 / `HoldIfUnprovenCandidate`) and the both-gone case must never delete or hold. Task 4: `ConvergingAndIgnoredRemovals_AreNotCountedByTheGuard` (11 removals over the floor of 10), `ACanonicalRecordAlsoAbsent_IsIgnored_TheEntryKept_NoHold_AliasOpen`, `InAnUnprovenPull_IgnoredAndScanSkippedAliasIds_OpenNoFallbackHold`; Task 6: `InAnUnprovenPull_AFailedConvergence_OpensNoFallbackHold_AndTheNextPullConverges`. The pre-pass must also fail closed, never open: a W1 candidate blob that cannot be read, a canonical blob of the target choice (step 4) that cannot be read, or a knowledge check that cannot be read, must keep every `x` that could have converged through this pull's detection, and a transient fault on the lowest candidate must never make `x` converge onto a higher one (irreversible: `b|x` resolved, `import_log` retargeted, unlike other machines) (Task 4: `AFailedCandidateBlobRead_KeepsTheOther_NotDeletedNotHeld_AndConvergesNextPull`, `AFailedBlobReadOfTheLowestCandidate_KeepsX_NotConvergedOntoTheHigher_AndConvergesOntoTheLowestNextPull`, `AnUnreadableKnowledgeCheckOnTheTarget_IgnoresTheOther`).
3. **The only copy of a local text.** A differing local text, a pending local edit and earlier losers must survive (NC4, NC2, the bound of 8 dropping the oldest). Task 6: `EightOldLosersAndADifferingLocalText_KeepTheNewestLocalText_AndDropTheOldest`, `APendingLocalTextEdit_IsFiledAsAConflict_NotWrittenBack`, `APinnedEntryWithADifferentText_KeepsThePinText_AndHoldsAPinEdit`.
4. **A half-done re-key.** Vector (vec0 keyed by rowid), FTS rows (triggers), `parent_id`, tags and `sticky` must move with the row, and a fault after the re-key must leave `x` exactly as it was. Task 5: `RekeyMemory_KeepsRowidVectorFtsAndEveryColumn`, `RekeyMemory_RewritesParentIdInEveryContentTable`, `AFaultAfterTheRekey_RollsTheRekeyBack`; Task 6: `AFaultAfterTheReKey_RollsEverythingBack`.
5. **A skip that differs between pulls or blocks `L`.** A racing importer must defer (nothing written), a repeated pull must write nothing and open no transaction, and an existing row of any state must not be reopened. Task 3: `ARacingImportLogRow_DefersTheEntry_AndWritesNothing`, `TheSamePullAgain_WritesNoRow_AndOpensNoTransaction`, `AnExistingAliasRowOfAnyState_IsLeftAlone_AndTheSkipStillApplies`, `AFreshMachinesFirstPull_WithBothRecordsOfAGroup_InsertsOneSkipsTheOther_AndIsComplete`.
6. **A re-appearing record or a re-detection resurrecting a duplicate.** Task 6: `TheReappearanceOfTheOthersRecord_IsSkippedAsAnAlias`; Task 3: `ALaterLowerArrival_AddsItsRow_AndLeavesTheEarlierRow`.

## Spec test ownership

| Spec Tests clause | Task |
|---|---|
| Core: canonical (ordinal min), group and rows, `skipInstead` plain `Ignore`, target choice, converging/ignored choice | 1 |
| Alias store: `import_log` targets (any `source_tool`, memory type, existing, eligible, not the record's id), open rows by hash and by other id, `INSERT OR IGNORE`, resolved upsert, import retarget, `HasAnyItem`, `AliasPlan` | 2 |
| Skip: no insert, one row `(min, other, h, open)`, `NoOp`, no row; same pull again no row, no transaction | 3 |
| Canonical id record lower / higher; three members | 3 |
| Deleted target: no alias, inserted; log row for the record's own id: no alias | 3 |
| `git-sync` log row is a target; another `target_type` is not | 3 |
| Existing alias row of each state left alone, skip applies | 3 |
| Racing importer (log row, deleted entry) defers, nothing written | 3 |
| Unproven pull skips the same | 3 |
| Fresh machine, both records in the first pull: one inserted, one skipped, nothing deferred | 3 |
| Three members one at a time (`(a,b)`, lower `c`: skip, `(c,b)` added, `(a,b)` untouched); ineligible local id no target | 3 (detection); 6 (the later convergence of `b` onto the lowest parsing candidate) |
| Sticky record's skip writes no row, no `new-pin` | 3 |
| Re-key keeps vector, tags, `parent_id`, `access_count` | 5 (store), 6 (through the pull) |
| One test per D4 row (id, tier, tags, sticky, content, row base, items, `import_log` retarget, alias state, `x` gone from store, vectors, rows) | 6 |
| Canonical present while `x` absent: same pull and later pull | 6 |
| Catch-up in one pull and in two pulls, same final state; canonical visit deferred by a racing importer | 6 |
| Guard preview does not count converging removals | 4 |
| Pre-pass fails closed: a failed or cancelled candidate blob read (step 3, W1 candidates) puts every qualifying `x` in `Ignored` (kept, not deleted, not held, converges on the next pull); the same for a failed or cancelled read of a canonical blob in the target choice (step 4: a fault on the lowest candidate `a` never converges `x` onto the higher `b`; converges onto `a` next pull); an unreadable knowledge check on the target means not free | 4 |
| The guard preview reads a canonical blob that is also a local deletion once (it takes the blob already in `blobs`) | 4 |
| Canonical also absent: `Ignore`, kept, no hold, alias open; canonical not parsing the same | 4 |
| Entry (memory or knowledge), `forgotten` or pending-deletion row under `c`: ignored, not counted | 4 |
| Held-delete item on `x`: ordinary rules decide | 4 |
| Two local members removed together: lowest converges, other ignored (also when the lowest defers), guard counts neither | 6 (4 proves "counts neither") |
| D4's arrival deduped against `x`'s earlier re-keyed losers (local text equal to one of them: listed once) | 6 |
| Lowest candidate not parsing, higher parsing: converges onto the higher | 6 |
| Earlier `apply-failed` of `x` removed; `allowed_hash` carried; `x`'s conflict item re-keyed and reconciled | 6 |
| Unproven pull converges; failed convergence, scan-skipped `x`, ignored `x` open no fallback hold; rolled-back fault after the re-key | 4 (ignored, scan-skipped), 6 (converges, failed, rollback) |
| `x`'s losers ahead of D4's loser, dedupe, bound 8; pin items removed | 6 |
| Reappearance of `x`'s record skipped | 6 |
| Across machines (A, B legacy copies; third machine T converging; conflict item / held pin edit) | 7 |
| `GitSyncCycleCallerTests` unchanged | 7 (final verify) |

## File map

| File | Change |
|---|---|
| `src/TotalRecall.Core/SyncAlias.fs` (new; `TotalRecall.Core.fsproj` after `SyncConflict.fs`), `tests/TotalRecall.Core.Tests/SyncAliasTests.fs` (new; after `SyncConflictTests.fs`) | Task 1 |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncAliases.cs` (new: `AliasRow`, `AliasPlan`, `GitSyncAliases`), `GitSyncReviews.cs` (`HasAnyItem`, `RekeyConflictItem`), `tests/.../GitSync/GitSyncAliasesTests.cs` (new), `tests/.../GitSync/AliasTestSupport.cs` (new: `SeedLegacy`, `AliasRows`) | Tasks 2, 5 |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Entry.cs`, `GitSyncApplier.cs` (class doc), `tests/.../GitSync/GitSyncApplierAliasSkipTests.cs` (new) | Task 3 |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Alias.cs` (new: pre-pass), `GitSyncApplier.Pull.cs`, `GitSyncApplier.Entry.cs`, `tests/.../GitSync/GitSyncApplierConvergenceTests.cs` (new) | Task 4 |
| `src/TotalRecall.Infrastructure/Storage/SqliteStoreTransaction.cs`, `Storage/SqlRows.cs`, `tests/.../Storage/SqliteStoreTransactionTests.cs`, `tests/.../GitSync/GitSyncReviewsTests.cs` | Task 5 |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Converge.cs` (new), `GitSyncApplier.Entry.cs` (`PlanAndCarry` extracted, `CarryOut` convergence steps), `GitSyncApplier.Pull.cs`, `GitSyncApplier.cs` (class doc) | Task 6 |
| `tests/.../GitSync/TwoMachineRig.cs` (third machine), `tests/.../GitSync/AliasCycleTests.cs` (new), `AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` | Task 7 |

## Spec/code notes

1. **The guard's floor.** `SyncDeletion.guardLimit` is `max 10 (active * percent / 100)`, so no configuration gives "a guard limit of 1" (spec Tests). The guard test uses 11 converging or ignored removals in one pull (11 > 10 trips without the exclusion) with `DeleteGuardPercent = 0`.
2. **Where the `AliasPlan` is planned and recomputed.** Planned in `ApplyCore` right after `Decide` (before `BEGIN IMMEDIATE`); recomputed in `CarryOut` at a new `alias` step after the item step, inside the transaction. The ordinary re-read at the start of `CarryOut` already compares the record id's row and entry, so an importer that creates the record's own id also defers. A difference defers with the existing text `the entry changed since the decision was made` (D1: "the existing deferral, with its existing reason text").
3. **A skip writes but is a `NoOp`.** `CarryOut` counts nothing for the alias writes, so a transaction that only writes alias rows returns `EntryResult.NoOp` (D6: "a skip is a `NoOp`"); `ApplyOne` then runs its usual `CleanUp`.
4. **How convergence enters the existing machinery.** The tail of `ApplyCore` (from `StorePlan plan;` at `GitSyncApplier.Entry.cs:183` to the end) is extracted unchanged as `PlanAndCarry`; `ApplyCore` calls it with no convergence. `ApplyConvergence(x, target, proven)` (through `ConvergeCore`) reads `x` and `c`, builds the no-base `MergeInput` with `BuildInput(row: null, oldBase: null, theirs: canonical, local: x's view, proven, held: null)`, and calls `PlanAndCarry` for id `c` with `row: null`, `read: x's LocalRead.Present`, `held: null`, `pinItems: []`, `conflictItem: conflictX with { EntryId = c }` and a `Convergence` context. `CarryOut` with that context first re-checks `x` and `c`, re-keys, then runs its ordinary re-read of `c` (which must equal the plan), store step, `BuildRow` (row `null` → a new row for `c`), item step (`PinPlanning.Plan` on `c`'s re-keyed item), then the convergence finish step. Nothing of the merge, store, row or item planning is duplicated.
5. **Seams of a convergence.** `BeforeEntry`, `BeforeTransaction` and `BeforeEntryStep` are called with the visited id `x` (not `c`), so a test that faults "the entry `x`" reaches its convergence. New step names: `rekey-row`, `rekey-parent` (store), `alias` (skip), `rekey` and `converge` (applier).
6. **Condition 5 needs a cross-kind read.** `GitSyncReviews` restricts every statement to its own kind; `HasAnyItem(conn, id, tx)` (`SELECT 1 FROM git_sync_review WHERE entry_id = $id LIMIT 1`) is added as its one cross-kind read, documented as such. `RekeyConflictItem` is a conflict member (`kind = 'conflict'`, any state; `ReadConflictItem` reads only `open|unseen`, the only states a conflict row takes).
7. **The pre-pass's hashes of `x`.** D1 detects through `import_log`; the pre-pass finds the hashes of `x` the same way (`GitSyncAliases.HashesOf`: every `content_hash` of a memory row whose `target_entry_id = x`, any `source_tool`), not from `x`'s base origin, so a detection of the pre-pass and the per-visit detection read the same rows. It reads the blobs of this pull's W1 candidates (visited, accepted, no row, no entry, not knowledge, not scan-skipped, eligible) only when at least one `x` meets conditions 1, 2 and 7, through the existing `blobs` map (the loop and `PreviewDeletions` take them from it, so no blob is read twice; a failed read stays in the map for the loop to report). The pre-pass fails closed: when any of those blob reads is not ok (failed or cancelled) it returns every such `x` in `Ignored` (kept, retried next pull), never `None` and never skipping the candidate, because a skipped candidate could be the only route by which `x` converges, and `x` would then take W6.4 and be deleted while the pull completes. The same rule holds for the blobs of the target choice (`PrepareAliases` step 4): a candidate canonical blob that is read and fails or is cancelled is not "does not parse" (that would let `x` converge onto a higher `b` because a transient fault hid the lower `a`: irreversible, `b|x` resolved and `import_log` retargeted, different from the other machines); any not-ok read there returns `Ignored: Q` too, and only a blob that reads ok and does not parse is dropped from `parses`. An `ArgumentException` or `InvalidCastException` from `InKnowledge` (what `ApplyCore` catches at its knowledge check) makes an id not a candidate and a target not free.
8. **The re-key is safe at the SQL level.** The tier tables declare `id TEXT PRIMARY KEY NOT NULL` (`Schema.cs:96`), so the rowid is the implicit integer rowid, not the id: `UPDATE <table> SET id = $new WHERE id = $old` keeps the rowid, so the `vec0` row (keyed by rowid) follows, the FTS `_au` trigger (`Schema.cs:383-385`) re-indexes the same rowid with the same content and tags, and `sticky`, `access_count`, `summary`, `source`, `metadata`, `decay_score`, `created_at`, `updated_at` and `last_accessed_at` stay (a raw `UPDATE`, not `SqlRows.UpdateRow`, which would set `updated_at`). `parent_id` is rewritten in the six tables of `MigrationRunner.AllTablePairs`; the legacy `pinned_*` tables (merged into hot in 4.0) are not read by the store and are not touched.
9. **`allowed_hash`** is set on `c`'s new row after `BuildRow` (`next with { AllowedHash = x's row.AllowedHash }`); `blocked_*` stay NULL (D3 step 4).
10. **The resolved write** is an upsert (`INSERT ... ON CONFLICT(canonical_id, other_id) DO UPDATE SET state = 'resolved'`): a detection of the pre-pass has no row yet, an open row is updated (D3 step 7). Other open rows naming `x` with another canonical id are not touched (rows are never rewritten, D1).
11. **Task 4 routes a converging id through the ignored path** until Task 6 replaces that branch, so Task 4 compiles and is green alone; Task 4's tests assert only what stays true after Task 6 (not counted, not deleted, not held), never "kept" for a converging id.
12. **A canonical id that is a member being visited.** The canonical of a group is the ordinal minimum, so it is visited before the others, but nothing relies on it (D2): `ConvergeCore` re-checks condition 5 itself, before and inside the transaction.
13. **The open rows' `origin_hash` is nullable** in the schema; a row with NULL hash is never written by this part and is ignored by the reads that select by hash (`origin_hash = $h` is never true for NULL), and as a candidate of the pre-pass it is skipped.
14. **Convergence re-reads more than the spec lists.** D3 step 1 names `x`, `c` and `x`'s conflict item; the plan also compares `x`'s row, pin items and held item, and `c`'s knowledge entries, row and review rows (conditions 1, 5 and 7), a superset that only adds deferrals on a real change.
15. **Historical rows keep the old id** (D4): `compaction_log` and `retrieval_events` are not touched by the convergence.
16. **Line hints checked:** `GitSyncApplier.Entry.cs` 98 (`ApplyOne`), 110 (`ApplyCore`), 166 (`Decide`), 183 (`StorePlan plan;`), 254 (no-op gate), 265 (`BeforeTransaction`), 388 (`CarryOut`), 452 (`row` step), 477 (`pins` step), 512 (`close`), 541 (`issue`); `GitSyncApplier.Pull.cs` 112 (preview call), 228 (`Tally(ApplyOne(...))`), 285-287 (`HoldIfUnprovenCandidate`), 375/392 (`PreviewDeletions` and its scan-skip check); `ApplyPlanning.cs` 307 (`LogImport`, origin hash else content hash); `Schema.cs` 907-913 (`git_sync_alias`, PK `(canonical_id, other_id)`, states `open|converged|resolved|abandoned`).

---

### Task 1: Core `SyncAlias` (D1 to D3, pure)

**Files:**
- Create: `src/TotalRecall.Core/SyncAlias.fs` (registered after `SyncConflict.fs` in `TotalRecall.Core.fsproj`), `tests/TotalRecall.Core.Tests/SyncAliasTests.fs` (after `SyncConflictTests.fs` in the test fsproj)

**Interfaces:**
- Produces (`module TotalRecall.Core.SyncAlias`, opens `SyncMerge`; module doc: "Git sync, milestone 1b-iii part 5 (#165): the pure rules of alias detection and convergence. Pure and total: no I/O."):

```fsharp
/// The ordinal minimum. `invalidArg` on an empty list.
val canonical : ids: string list -> string

type DetectInput =
    { RecordId: string
      /// Existing, eligible local memory ids whose import_log rows carry the record's origin hash, without RecordId.
      Targets: string list
      /// (canonical, other) of every `open` git_sync_alias row with the same origin hash.
      OpenRows: (string * string) list
      /// The ids of OpenRows that exist here as a memory.
      LocalMembers: Set<string>
      /// (canonical, other) of every git_sync_alias row, any state.
      ExistingRows: Set<string * string> }

type Detection =
    { Canonical: string
      /// The union of RecordId, Targets and both ids of OpenRows, distinct, ordinal.
      Members: string list
      /// (Canonical, x) for every member x that is RecordId, a Target or a LocalMember, x <> Canonical,
      /// and not in ExistingRows; ordinal by x.
      Rows: (string * string) list }

/// None when Targets is empty (no alias: the record is inserted as usual).
val detect : DetectInput -> Detection option

/// The plain Ignore: Entry Ignore, Base all KeepBase, RowState None, Reviews [], Notes [],
/// CloseHeldDelete false, FreshContentStamp false.
val ignored : MergeDecision

/// D1: an InsertFromRecord becomes `ignored`; every other decision is returned unchanged.
val skipInstead : MergeDecision -> MergeDecision

/// D3 condition 3: the ordinally lowest candidate in `parses` (accepted at R and parsing); None when none.
val target : candidates: string list -> parses: Set<string> -> string option

/// One local id meeting conditions 1 to 3 and 7, with its target (None: no candidate parses) and whether the
/// target id is free here (condition 5).
type Candidate = { Id: string; Target: string option; TargetFree: bool }

type Choice = { Converging: Map<string, string>; Ignored: Set<string> }

/// D2/D3: a candidate with no target or a target that is not free is ignored; of the candidates that share a
/// target the ordinally lowest converges onto it and the others are ignored (condition 6).
val choose : Candidate list -> Choice
```

- [ ] **Step 1: Write the failing tests** in `SyncAliasTests.fs` (Expecto; ids are eligible-looking lower-hex strings such as `"aaaa0001"`, `"bbbb0002"`, `"cccc0003"`).
  - `testList "SyncAlias.canonical"`: `canonical ["bbbb0002"; "aaaa0001"] = "aaaa0001"`; a single id; ordinal not culture: `canonical ["a1aaaaaa"; "a-aaaaaa"] = "a-aaaaaa"` (`-` is 0x2D, below `1`); the empty list throws `ArgumentException`.
  - `testList "SyncAlias.detect"`: no targets → `None` (also with open rows); record lower than the local id (`RecordId = aaaa0001`, `Targets = [bbbb0002]`) → `Canonical aaaa0001`, `Rows [(aaaa0001, bbbb0002)]`; record higher (`RecordId = bbbb0002`, `Targets = [aaaa0001]`) → `Canonical aaaa0001`, `Rows [(aaaa0001, bbbb0002)]`; three members (`RecordId = cccc0003`, `Targets = [aaaa0001; bbbb0002]`) → rows `(aaaa0001, bbbb0002)` and `(aaaa0001, cccc0003)`; an existing row of the pair (any state, via `ExistingRows`) is not listed; a later lower arrival (`RecordId = 0000aaaa`, `Targets = [bbbb0002]`, `OpenRows = [(aaaa0001, bbbb0002)]`, `LocalMembers = {bbbb0002}`, `ExistingRows = {(aaaa0001, bbbb0002)}`) → `Canonical 0000aaaa`, `Members [0000aaaa; aaaa0001; bbbb0002]`, `Rows [(0000aaaa, bbbb0002)]` (no row for `aaaa0001`, which is neither the record nor local); an open-row member that is local gets a row; `Members` is distinct and ordinal for unsorted, repeated input.
  - `testList "SyncAlias.skipInstead"`: `skipInstead (insertFrom stickyRecord false)` equals `ignored` exactly (no `NewPin`, `RowState None`, `Base` all `KeepBase`); `ignored.Entry = Ignore`; a `Defer`, a `LeaveEntry`, a `DeleteEntry` and a `MergeGroups` decision come back unchanged (build them with `SyncMerge.decide` on small inputs as `SyncDeletionTests` does).
  - `testList "SyncAlias.target"`: the lowest parsing (`target ["cccc0003"; "aaaa0001"; "bbbb0002"] (set ["bbbb0002"; "cccc0003"]) = Some "bbbb0002"`); none parses → `None`; empty → `None`; duplicates ignored.
  - `testList "SyncAlias.choose"`: empty → both empty; `Target = None` → ignored; `TargetFree = false` → ignored; two candidates with target `aaaa0001` (`bbbb0002`, `cccc0003`) → `Converging = map [bbbb0002, aaaa0001]`, `Ignored = {cccc0003}`; given in reverse order the same; two candidates with different targets both converge; a candidate whose target is not free does not stop another with a free target.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~SyncAlias"`. Expected: FAIL (compile errors: no `SyncAlias`).
- [ ] **Step 3: Implement** the module as specified (sorting with `List.sortWith (fun a b -> String.CompareOrdinal(a, b))`, `List.distinct`). `SyncMergeTests.fs` is not touched.
- [ ] **Step 4: Run** the step 2 command, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src/TotalRecall.Core tests/TotalRecall.Core.Tests && git commit -m "Add the pure alias detection and convergence rules"`.

### Task 2: The alias store and the skip plan (D1, D3 steps 5 and 7)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/GitSyncAliases.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncAliasesTests.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/AliasTestSupport.cs`
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (`HasAnyItem`; class doc: the one cross-kind read), `tests/.../GitSync/GitSyncReviewsTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SyncAlias.detect`, `DetectInput`, `Detection`).
- Produces:
  - `internal sealed record AliasRow(string CanonicalId, string OtherId, string? OriginHash, string State)`.
  - `internal sealed record AliasPlan(string Hash, string Canonical, IReadOnlyList<string> Targets, IReadOnlyList<string> Inserts)` with `bool IsEmpty => Inserts.Count == 0` and `bool Matches(AliasPlan? other)` (not null, `Hash` and `Canonical` ordinal-equal, `Targets` and `Inserts` `SequenceEqual`). `Inserts` are the other ids of the rows `(Canonical, other)` to write.
  - `internal static class GitSyncAliases` (plain SQL on the caller's connection, every member with an optional `SqliteTransaction? tx = null` passed to the command, as `GitSyncRows` does):
    - `ReadAll(conn, tx) : IReadOnlyList<AliasRow>` (`ORDER BY canonical_id, other_id COLLATE BINARY`);
    - `ReadOpenByHash(conn, string hash, tx) : IReadOnlyList<AliasRow>` (`state = 'open' AND origin_hash = $h`);
    - `ReadOpenOf(conn, string otherId, tx) : IReadOnlyList<AliasRow>` (`state = 'open' AND other_id = $x`, ordinal by canonical);
    - `ImportTargets(conn, string hash, string recordId, tx) : IReadOnlyList<string>`: `SELECT DISTINCT l.target_entry_id FROM import_log l WHERE l.content_hash = $h AND l.target_type = 'memory' AND (EXISTS (SELECT 1 FROM hot_memories m WHERE m.id = l.target_entry_id) OR EXISTS (... warm_memories ...) OR EXISTS (... cold_memories ...))` (table names from `MigrationRunner.TableName(tier, ContentType.Memory)`), then in C# keep ids with `SyncValidation.isEligibleId(id)` and `id != recordId`, ordinal, distinct;
    - `HashesOf(conn, string entryId, tx) : IReadOnlyList<string>` (`SELECT DISTINCT content_hash FROM import_log WHERE target_entry_id = $id AND target_type = 'memory'`, ordinal);
    - `MemoryExists(conn, string id, tx) : bool` (the three `EXISTS` above);
    - `InsertOpen(conn, string canonical, string other, string hash, tx)`: `INSERT OR IGNORE INTO git_sync_alias (canonical_id, other_id, origin_hash, state) VALUES ($c, $o, $h, 'open')`;
    - `MarkResolved(conn, string canonical, string other, string hash, tx)`: `INSERT INTO git_sync_alias (...) VALUES ($c, $o, $h, 'resolved') ON CONFLICT(canonical_id, other_id) DO UPDATE SET state = 'resolved'` (an existing row keeps its `origin_hash`);
    - `RetargetImports(conn, string fromId, string toId, tx)`: `UPDATE import_log SET target_entry_id = $to WHERE target_entry_id = $from AND target_type = 'memory'` (row ids, which are `MD5(tool:path:hash)`, stay);
    - `PlanSkip(conn, string recordId, string hash, tx) : AliasPlan?`: `Targets = ImportTargets(...)`; null when empty; else `SyncAlias.detect` with `OpenRows` from `ReadOpenByHash`, `LocalMembers` = the ids of those rows for which `MemoryExists`, `ExistingRows` from `ReadAll`; returns `new AliasPlan(hash, d.Canonical, targets, d.Rows.Select(r => r.Item2).ToList())`.
  - `GitSyncReviews.HasAnyItem(conn, string entryId, tx = null) : bool` (`SELECT 1 FROM git_sync_review WHERE entry_id = $id LIMIT 1`, any kind and state).
  - Test support (`AliasTestSupport`, `internal static`): `SeedLegacy(SqliteStore store, SqliteConnection conn, string id, string text, string hash, Tier? tier = null, string sourceTool = "claude-code", ContentType? type = null)` inserts the entry (`store.Insert(tier ?? Tier.Warm, type ?? ContentType.Memory, new InsertEntryOpts(Content: text, Id: id))`) and its row through `new ImportLog(conn).LogImport(sourceTool, "/home/u/.claude/memory/" + id + ".md", hash, id, tier ?? Tier.Warm, type ?? ContentType.Memory)`; `AliasRows(SqliteConnection conn) : IReadOnlyList<string>` as `canonical|other|hash|state` lines from `GitSyncAliases.ReadAll`; `Hash(string text) => ImportLog.ContentHash(text)`.

- [ ] **Step 1: Write the failing tests** in `GitSyncAliasesTests` (over `ScanRig` from `GitSyncScannerTestSupport.cs`, `[Fact]`):
  - `ImportTargets_ReadsEveryToolsMemoryRows_ForExistingEligibleIdsOtherThanTheRecord` (a `claude-code` row and a `git-sync` row for two existing ids → both; a row whose entry was deleted, a `knowledge` row with an existing knowledge entry, a row for the record's own id, a row for an existing entry with the ineligible id `LEGACY_ID` → none of these; ordinal order);
  - `HashesOf_ReadsTheMemoryRowsOfTheId`;
  - `MemoryExists_IsTrueInAnyOfTheThreeTiers_AndFalseForKnowledge`;
  - `InsertOpen_WritesAnOpenRow_AndLeavesAnExistingRowOfAnyStateAsItIs` (theory over `open`, `converged`, `resolved`, `abandoned`: the row and its hash unchanged);
  - `MarkResolved_InsertsAResolvedRow_OrResolvesAnOpenOne_KeepingItsHash`;
  - `ReadOpenByHash_AndReadOpenOf_ReadOnlyOpenRows`;
  - `RetargetImports_MovesOnlyTheIdsMemoryRows` (a `knowledge` row and another id's rows untouched; ids of the rows unchanged);
  - `PlanSkip_WithNoTarget_IsNull`;
  - `PlanSkip_GivesTheCanonicalTheTargetsAndTheMissingRows` (record higher and lower; three members; an existing `resolved` row of the pair → `Inserts` empty and `IsEmpty`);
  - `AliasPlan_Matches_ComparesEveryMember`.
  - `GitSyncReviewsTests.HasAnyItem_IsTrueForARowOfAnyKindOrState`.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSyncAliases|FullyQualifiedName~GitSyncReviews"`. Expected: FAIL (compile errors).
- [ ] **Step 3: Implement** the members, the records and the test support as specified.
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the git sync alias store and the skip plan"`.

### Task 3: Skip-insert in the pull (D1, D6)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.cs` (the class doc around line 83, Step 3), `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Entry.cs` (`ApplyCore` after `Decide` at 166, the no-op gate at 254, `CarryOut` signature at 388 and a new `alias` step after the item step, before `close` at 512; the `ApplyCore` doc: an insert whose origin maps to another local memory is skipped)
- Create: `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncApplierAliasSkipTests.cs` (`ApplyRig` for one id; `PullRig` with `[GitFact]` for whole pulls, with the `NewPull`/`Pull` helpers as in `GitSyncApplierConflictTests` 544-565)

**Interfaces:**
- Consumes: Task 1 (`SyncAlias.skipInstead`), Task 2 (`GitSyncAliases.PlanSkip`, `GitSyncAliases.InsertOpen`, `AliasPlan`, `AliasTestSupport`).
- Produces: `CarryOut(..., AliasPlan? aliasPlan)` (a new last parameter before `phase`), used again by Task 6.

- [ ] **Step 1: Write the failing tests.** Ids: `R` = the record's id, `L` = a local legacy id, chosen so the ordinal order is stated in each test (e.g. `L = "3f9c2a7e-0000-4000-8000-0000000000a1"`, `R = "3f9c2a7e-0000-4000-8000-0000000000b2"`); records `ApplyRig.Rec(R, ..., originHash: h)` with `h = AliasTestSupport.Hash("host file")`; the local twin seeded by `AliasTestSupport.SeedLegacy(_rig.Scan.Store, _rig.Scan.Connection, L, "Use bun.", h)`. Each asserts `Is<EntryResult.NoOp>` (or `Carried` where inserted), `_rig.Entry(R)`, the alias rows (`AliasTestSupport.AliasRows`), `_rig.Scan.Rows().ContainsKey(R)`, and the review rows where relevant.
  - `ARecordWhoseOriginADifferentLocalIdImported_IsSkipped_WithOneOpenAliasRow` (`R > L`: no entry `R`, rows `[L|R|h|open]`, no `git_sync_entry` row for `R`, `NoOp`, entry `L` unchanged (`_rig.Full()` equal except the alias table));
  - `TheSamePullAgain_WritesNoRow_AndOpensNoTransaction` (second `Apply`: `NoOp`, `Applier.TransactionCount` unchanged, rows unchanged);
  - `TheCanonicalId_IsTheLowestOfTheGroup` (theory: record lower → `R|L|h|open`; record higher → `L|R|h|open`);
  - `TwoLocalIdsForOneHash_SkipTheRecord_WithARowPerNonCanonicalMember` (two seeded twins `L1 < L2 < R`: rows `L1|L2|h|open`, `L1|R|h|open`);
  - `AMatchingLogRowWhoseEntryWasDeleted_IsNoAlias_AndTheRecordIsInserted` (twin deleted from the store after seeding: `Carried(Inserted)`, no alias row);
  - `ALogRowForTheRecordsOwnId_IsNoAlias` (an `import_log` row `h → R` with no entry: inserted, no row);
  - `AGitSyncLogRow_IsATarget` (twin seeded with `sourceTool: "git-sync"`: skipped);
  - `AKnowledgeLogRow_IsNoTarget` (twin seeded with `type: ContentType.Knowledge`: inserted);
  - `AnIneligibleLocalId_IsNoTarget` (twin id `LEGACY_ID`: inserted, no row);
  - `ARecordWithoutAnOrigin_IsInserted` (no `originHash`, a twin seeded with the record's content hash: inserted);
  - `AnExistingAliasRowOfAnyState_IsLeftAlone_AndTheSkipStillApplies` (theory `open|converged|resolved|abandoned`: the row is seeded by SQL, the apply is `NoOp`, opens no transaction, the row is unchanged);
  - `ALaterLowerArrival_AddsItsRow_AndLeavesTheEarlierRow` (twin `B`; record `A` with `A < B` skipped → `A|B|h|open`; then record `C` with `C < A`: skipped, rows `A|B|h|open` and `C|B|h|open`);
  - `ARacingImportLogRow_DefersTheEntry_AndWritesNothing` (`Applier.BeforeTransaction = id => { if (id == R) SeedLegacy(... L2 ..., h) }` where the plan had one target and inserts: `Deferred` with reason `the entry changed since the decision was made`, no alias row, no entry `R`);
  - `ARacingDeletionOfTheTarget_DefersTheEntry_AndWritesNothing` (`BeforeTransaction` deletes twin `L`: `Deferred`, no row, no entry `R`);
  - `AnUnprovenPull_SkipsTheSame` (`Apply(record, proven: false)`: the same row, `NoOp`);
  - `AStickyRecordsSkip_WritesNoRowAndNoNewPinItem` (`sticky: true`: `ReviewRows() == 0`, no `git_sync_entry` row);
  - `AFreshMachinesFirstPull_WithBothRecordsOfAGroup_InsertsOneSkipsTheOther_AndIsComplete` (`PullRig`, no local entries, records `A < B` with the same origin committed together: one pull inserts `A` (its `git-sync` log row carries `h`), skips `B` (row `A|B|h|open`), `summary.Complete`, `Deferred` empty, `rig.Last == head`, `Inserted == 1`);
  - `ASkippedRecord_DoesNotHoldL_InAPullWithAnotherChange` (`PullRig`: a seeded twin and the record plus an unrelated new record in one commit: complete, `L` advances, the skip counted in `Unchanged`).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSyncApplierAliasSkip"`. Expected: FAIL on every skip test (the record is inserted); `AMatchingLogRowWhoseEntryWasDeleted...`, `ALogRowForTheRecordsOwnId...`, `AKnowledgeLogRow...`, `AnIneligibleLocalId...` and `ARecordWithoutAnOrigin...` already pass and are regression guards (say so in the report).
- [ ] **Step 3: Implement** in `ApplyCore`, right after `var (decision, approved) = Decide(...)` and before the guard conversion:

```csharp
AliasPlan? aliasPlan = null;
if (row is null && local is null && decision.Entry.IsInsertFromRecord && theirs!.Record.Origin is { } origin)
{
    try { aliasPlan = GitSyncAliases.PlanSkip(conn, id, origin.Value.Hash); }
    catch (SqliteException ex) when (IsBusy(ex)) { return new EntryResult.Deferred("the database is busy", Busy: true); }
    catch (SqliteException ex) { return new EntryResult.Failed("the alias lookup failed: " + OneLine(ex.Message)); }
    if (aliasPlan is not null)
        decision = SyncAlias.skipInstead(decision); // D1: the plain Ignore, so no row and no new-pin item
}
```

  The no-op gate gains `&& (aliasPlan is null || aliasPlan.IsEmpty)`. `CarryOut` gets `aliasPlan`; after the item step and before `close`:

```csharp
if (aliasPlan is not null)
{
    Step(id, "alias");
    var now = GitSyncAliases.PlanSkip(conn, id, aliasPlan.Hash, dbTx);
    if (!aliasPlan.Matches(now))
        return new EntryResult.Deferred("the entry changed since the decision was made", Busy: false);
    foreach (var other in now!.Inserts)
        GitSyncAliases.InsertOpen(conn, now.Canonical, other, now.Hash, dbTx);
}
```

  The alias writes are not counted, so `CarryOut` still ends in `EntryResult.NoOp` for a skip (Spec/code note 3). The `ApplyCore` doc and the `GitSyncApplier` class doc (`GitSyncApplier.cs` ~83) gain one sentence each: "An insert (W1) whose record's origin hash maps in `import_log` to a different existing memory is skipped and recorded in `git_sync_alias` (alias D1)."
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS, no existing test changed.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Skip the insert of a pulled record whose origin is a different local memory"`.

### Task 4: The pre-pass, ignored ids, the guard preview and the fallback hold (D2, D3 conditions, D6)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Alias.cs` (the pre-pass), `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncApplierConvergenceTests.cs` (`[GitFact]`, `PullRig`)
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Pull.cs` (the pre-pass call before the preview at 112, `PreviewDeletions` gets the pass, skips its ids after the scan-skip check at 392 and takes a blob already in `blobs` instead of reading it again at ~400, the `ApplyOne` call at 228, `HoldIfUnprovenCandidate` at 285-287; `ApplyAsync` doc), `GitSyncApplier.Entry.cs` (`ApplyOne`/`ApplyCore` gain `bool aliasIgnored = false`)

**Interfaces:**
- Consumes: Tasks 1 to 3 (`SyncAlias.target`, `SyncAlias.choose`, `SyncAlias.detect`, `SyncAlias.ignored`, `GitSyncAliases.ReadOpenOf`, `HashesOf`, `ImportTargets`, `ReadOpenByHash`, `MemoryExists`, `ReadAll`, `GitSyncReviews.HasAnyItem`).
- Produces:
  - `internal sealed record ConvergeTarget(string CanonicalId, ParsedRecord Record, string Sha, string Hash)` (the canonical record as read at R, its blob id and the group's origin hash).
  - `private sealed record AliasPass(IReadOnlyDictionary<string, ConvergeTarget> Converging, IReadOnlySet<string> Ignored)` with `static readonly AliasPass None` and `bool Excludes(string id)`.
  - `private async Task<AliasPass> PrepareAliases(IReadOnlyList<string> ids, Dictionary<string, string> skippedByScan, TreeView view, Dictionary<string, GitSyncRow> rows, Dictionary<string, HeldItem> held, Dictionary<string, GitOutcome<byte[]>> blobs, CancellationToken ct)`.
  - `ApplyOne(string id, ParsedRecord? theirs, bool proven, HeldItem? held, string? blobSha, DeleteGuardVerdict guard, bool aliasIgnored = false)`: with `aliasIgnored` the decision after `Decide` is replaced by `SyncAlias.ignored` (before the guard conversion), so the entry and its row stay and the unchanged item step (`PinPlanning.Plan`, `recordPresent: false`) reconciles its items.

  `PrepareAliases`, in order (Spec/code note 7):
  1. `absent` = the ids of `ids` whose row is `active` with a base and that are not in `view.Present` (condition 2). Empty: return `None` (nothing can converge).
  2. For each `x` of `absent`, in ordinal order: skip it when `held` has it (condition 7) or `SyncValidation.isEligibleId(x)` is false; `ReadLocal(x)` must be `LocalRead.Present` (condition 1; a `FormatException` or any other read skips `x`). Its candidates start as `ReadOpenOf(conn, x)` rows with a non-null `OriginHash`: `(row.CanonicalId, row.OriginHash)`.
  3. When at least one `x` passed step 2 (`Q` = the set of those `x`; they meet conditions 1, 2 and 7: a local entry, an active row with a base, the record absent from R, no held-delete): the W1 candidates are the ids of `ids` with `view.Accepted`, no row, not in `skippedByScan`, eligible, `ReadLocal(id) is LocalRead.Absent` and `!InKnowledge(id)` (an `ArgumentException` or `InvalidCastException` from `InKnowledge`, as `ApplyCore` catches at its knowledge check, makes the id not a candidate). For each, its blob from `blobs` or read with `_client.CatBlobAsync(record.Sha, SyncValidation.maxFileBytes, ct)` and stored in `blobs`. **Fail closed:** when ANY such blob read is not ok (failed or cancelled, `ct` cancelled included), the pre-pass neither skips that candidate nor returns `None`: it returns `new AliasPass(empty Converging, Ignored: Q)` at once. An `x` whose only route to convergence was this pull's detection would otherwise take W6.4 and be deleted while the pull still completes, losing its text, tags, tier and pin; ignored, it is kept and retried next pull (the failed read stays in `blobs` for the loop to report). A blob that reads but does not parse is not a candidate (it cannot be inserted either; the loop rejects it). For a candidate with an origin hash `h` that is one of `HashesOf(conn, x)` for some `x` in `Q`, compute `SyncAlias.detect` exactly as `GitSyncAliases.PlanSkip` does (targets of `h` without the record id); for every `x` of `Q` in `Targets` with `x != Canonical`, add `(Canonical, h)` to `x`'s candidates (a detection counts like an open row, D2).
  4. `parses` = the candidate ids (all `x`) that are in `view.Accepted` and whose blob (from `blobs`, read and stored if absent) parses with `RecordReader.Parse(bytes, record.Path)`; keep each parsed record and sha. **Fail closed, as in step 3:** a blob read here that is not ok (failed or cancelled, `ct` cancelled included) is NOT "does not parse": the pre-pass returns `new AliasPass(empty Converging, Ignored: Q)` at once (the failed read stays in `blobs` for the loop to report). Only a blob that reads ok and does not parse is left out of `parses`. Otherwise a transient fault on the lowest candidate `a` would make `SyncAlias.target` pick a higher `b`.
  5. Per `x` with at least one candidate: `t = SyncAlias.target(candidates, parses)`; `free` = for `t`: `ReadLocal(t) is LocalRead.Absent && !InKnowledge(t) && !rows.ContainsKey(t) && !GitSyncReviews.HasAnyItem(conn, t)` (condition 5); a knowledge check on `t` that throws `ArgumentException` or `InvalidCastException` means `t` is not free (fail closed), so `x` is ignored. `x` with no candidate is in neither set.
  6. `SyncAlias.choose` over those candidates gives `Converging` (each mapped to `new ConvergeTarget(t, parsed, sha, h)`, `h` the hash of the first candidate of `x` that names `t`) and `Ignored`.

  In `ApplyAsync`, after `held` and `blobs` are built: `AliasPass aliases;` from `await PrepareAliases(...)` inside `try`, `catch (Exception ex) when (ex is FormatException or SqliteException or InvalidOperationException or ArgumentException or InvalidCastException)` (the last two are what `InKnowledge` can throw; inside `PrepareAliases` they are first handled as above, this catch is the backstop) → `return new ApplyOutcome.Failed("the alias rows could not be read: " + OneLine(ex.Message))` (nothing is written before the preview). `PreviewDeletions` gains `AliasPass aliases` and `if (aliases.Excludes(id)) continue;` after its scan-skip check. The loop's call becomes `ApplyOne(id, theirs, fetch.Proven, held.GetValueOrDefault(id), sha, preview.Verdict, aliasIgnored: aliases.Excludes(id))` (Spec/code note 11: Task 6 replaces this for converging ids). `HoldIfUnprovenCandidate` returns at once when `aliases.Excludes(id)`.

  `PreviewDeletions` reads blobs without checking `blobs` (`GitSyncApplier.Pull.cs` ~400: it always calls `CatBlobAsync` for an accepted record that is a local deletion), so a canonical id that is also a local deletion would be read twice (once by the pre-pass, once by the preview). Decision: the preview takes a blob already in `blobs` (`if (!blobs.TryGetValue(id, out var blob)) { blob = await ...CatBlobAsync(...); blobs[id] = blob; }`), a Task 4 change to `PreviewDeletions` (tested by `ACanonicalThatIsAlsoALocalDeletion_IsReadOnce`, counting `CatBlob` commands for its sha through `_rig.Fault`, which returns null).

- [ ] **Step 1: Write the failing tests** in `GitSyncApplierConvergenceTests` (helpers in the class: `NewPull`/`Pull` as in `GitSyncApplierConflictTests`, with an optional `ScanSummary` and `ApplyOptions`; `Has(id)`; and `SyncedPair(string x, string c, string localText, string canonicalText)`: commits `x`'s record with origin `h` and pulls (the entry `x` and its `git-sync` log row `h → x` exist, its row is `active` with a base), then commits `c`'s record with the same origin and pulls (skipped, row `c|x|h|open`); ids with `c < x`; `Dump(rig)`: the six content tables, the vector counts, `import_log`, `git_sync_entry`, `git_sync_review`, `git_sync_alias` and `git_sync_issue` as text, in the style of `ScanRig.Dump`; `Snapshot(rig)`: the same without clock-of-the-wall columns (`import_log.timestamp`, the content tables' `updated_at` and `last_accessed_at`), for comparing two rigs). Each pull also asserts `Deleted` empty and `GuardTripped` empty.
  - `ConvergingAndIgnoredRemovals_AreNotCountedByTheGuard` (`ApplyOptions(0, DeleteGuardPercent: 0)`; eleven pairs with distinct hashes built as `SyncedPair` does but in two commits and two pulls (all eleven `x` records, then all eleven `c` records), then all eleven `x` records removed in one commit and one pull: `GuardTripped` empty, `Held` empty, `Deleted` empty, every `Has(x_i) || Has(c_i)`);
  - `ACanonicalRecordAlsoAbsent_IsIgnored_TheEntryKept_NoHold_AliasOpen` (both records removed: entry `x` and its row unchanged, `Held` empty, row `c|x|h|open`);
  - `ACanonicalRecordThatDoesNotParse_IsIgnored` (`c`'s path replaced by `PutText(SyncValidation.recordPath(c), "{not json")` and `x` removed: as above; the rejection of `c` is recorded as usual);
  - `AnEntryUnderTheCanonicalId_IgnoresTheOther_AndTheGuardCountsNeither` (theory: a memory, a knowledge entry inserted under `c` with `rig.Store.Insert`; then `x` removed: `x` kept, alias open, not in `Deleted`, no hold);
  - `ARowOrAReviewUnderTheCanonicalId_IgnoresTheOther` (theory: a `forgotten` row for `c` written with `GitSyncRows.Write`; an `active` row with a base for `c` and no entry (a pending local deletion); a `held-delete` review row for `c`: as above; the rows are written with `GitSyncRows.Write(rig.Connection, new GitSyncRow(c, state, Encoding.UTF8.GetString(RecordWriter.Write(cRecord).Bytes!), null, ...))` and the review row with `GitSyncReviews.UpsertHeld`);
  - `AFailedCandidateBlobRead_KeepsTheOther_NotDeletedNotHeld_AndConvergesNextPull` (`c < x`; `x` synced with origin `h` and its legacy twin's `import_log` row `h` seeded but no open row yet, so this pull's detection is the only route; one commit adds `c`'s record with origin `h` and removes `x`; `_rig.Fault = c => c.IsCatBlob && ((SyncGit.GitCommand.CatBlob)c).sha == cBlob ? FaultingGitProcess.Failure(SyncGit.FailureKind.Other, "bad object") : null` as in `GitSyncApplierPullTests` 626; a theory over `FailureKind.Other` and `FailureKind.Cancelled`. Both: `x` and its row unchanged, `Deleted` and `Held` empty, no `held-delete` row, no alias row, and with the fault cleared the next pull converges `x` onto `c`. `Other` only: `c` is in `Failed` and the summary is complete. `Cancelled` only: `Failed` is empty (a cancelled blob read stops the loop and is never a failure: `GitSyncApplier.Pull.cs:191-196`, pinned by `GitSyncApplierPullTests.cs:602` `AGitCancelledOnABlobRead_IsNotAFailure_AndHoldsL_AndWritesNoIssue`), the summary is incomplete and `L` is not advanced);
  - `AFailedBlobReadOfTheLowestCandidate_KeepsX_NotConvergedOntoTheHigher_AndConvergesOntoTheLowestNextPull` (step 4 fail closed; ids `a < b < x`, both `a` and `b` records with origin `h` already in R and parsing, `x` synced with origin `h`, alias rows `a|x|h|open` and `b|x|h|open` made as `SyncedPair` does, then `x`'s record removed; `_rig.Fault` fails the `CatBlob` of `a`'s sha only (`FailureKind.Other`, as above; a theory also with `Cancelled`): `x` and its row kept, not converged onto `b`, `Deleted` and `Held` empty, no `held-delete` row, both alias rows unchanged (still open, `b|x` not resolved), the `import_log` rows of `h` not retargeted; with the fault cleared the next pull converges `x` onto `a`, the lowest candidate);
  - `AnUnreadableKnowledgeCheckOnTheTarget_IgnoresTheOther` (there is no store seam for knowledge reads: `InKnowledge` calls `_store.Get` directly (`GitSyncApplier.Entry.cs:84`). Mechanism, as `GitSyncScannerReadTests.cs:145` makes an unreadable entry (`UPDATE warm_memories SET source_tool = 'bogus' WHERE id = ...`): insert a knowledge entry under `c` with `rig.Store.Insert(Tier.Warm, ContentType.Knowledge, ...)`, then corrupt it by SQL `UPDATE warm_knowledge SET source_tool = 'bogus' WHERE id = '<c>'` (`rig.Scan.Exec`; the content tables share one DDL, `Schema.cs` `ContentTableDdl`), so `_store.Get` reaches `SourceToolMapping.Parse`, which throws `ArgumentException` (`SqliteStore.cs` ~610; what `InKnowledge`'s callers catch); first assert in the test that `Store.Get` on that id throws, then: `x` kept, not deleted, not held, alias open);
  - `ACanonicalThatIsAlsoALocalDeletion_IsReadOnce` (after `SyncedPair`, `c` was skipped and has no entry or row, so the local deletion is set up directly: keep the open alias row `c|x|h` that `SyncedPair` wrote and seed an `active` `git_sync_entry` row with a base for `c` by `GitSyncRows.Write` (as `ARowOrAReviewUnderTheCanonicalId_IgnoresTheOther` does) and no entry under `c`, so the scan reports `c` as a local deletion; `x` removed, `c`'s record still present: the `CatBlob` commands seen for `c`'s sha number one);
  - `AHeldDeleteItemOnTheOther_LeavesTheOrdinaryRules` (regression guard, already passes before Task 4: W6.3 is the ordinary rule; an open `remote` `held-delete` item seeded on `x` before `x` is removed: W6.3 leaves it, the item stands, `x` kept, alias open; the pre-pass put `x` in neither set: an unproven variant also leaves the hold as it is);
  - `AnIgnoredIdsNewPinItem_IsRemovedByTheUnchangedReconcile` (an open `new-pin` item seeded on `x`, both records removed: `x` kept, the item gone);
  - `InAnUnprovenPull_IgnoredAndScanSkippedAliasIds_OpenNoFallbackHold` (theory: both records removed and the `last-synced` ref deleted (`CloneGit("update-ref", "-d", "refs/total-recall/last-synced")`, as `GitSyncApplierUnprovenTests` 69 does); and `x` removed, `c` present, with the scan summary `scan with { Skipped = [new SkippedItem(x, null, "test")] }`: `Held` empty, no `held-delete` row for `x`, `x` kept).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplierConvergence"`. Expected: FAIL (the removals are deleted by W6.4, held by W6.5, or counted by the guard); `AHeldDeleteItemOnTheOther_LeavesTheOrdinaryRules` already passes and is a regression guard (say so in the report).
- [ ] **Step 3: Implement** the signatures and steps above, and the `ApplyAsync` doc sentence: "Before the preview a pre-pass (alias D2) computes the converging and ignored ids, which the preview does not count and the unproven fallback never holds."
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS (the `Cancelled` variants of the blob-fault tests assert `Failed` empty and an incomplete summary, not a failure); `GitSyncApplierGuardTests`, `GitSyncApplierUnprovenTests`, `GitSyncApplierPullTests` and `GitSyncApplierSafetyTests` unchanged and green.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Read aliases before the deletion checks and never read an open alias as a deletion"`.

### Task 5: The re-key store step (D3 step 2, D5)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Storage/SqlRows.cs` (`RekeyRow`, `RekeyParent`), `src/TotalRecall.Infrastructure/Storage/SqliteStoreTransaction.cs` (`RekeyMemory`), `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (`RekeyConflictItem`)
- Test: `tests/TotalRecall.Infrastructure.Tests/Storage/SqliteStoreTransactionTests.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncReviewsTests.cs`

**Interfaces:**
- Produces:
  - `public void SqliteStoreTransaction.RekeyMemory(Tier tier, string oldId, string newId)`: doc "Git sync alias convergence: the memory `oldId` in `tier` becomes `newId`. The row keeps its rowid, so its vector and FTS rows follow, and every other column (`sticky`, `access_count`, `summary`, `source`, `metadata`, `decay_score`, `tags`, the timestamps) stays; then `parent_id` of every content row naming `oldId` becomes `newId`. Steps: `rekey-row`, `rekey-parent`. Throws `InvalidOperationException` when `oldId` has no row in `tier`, or `newId` is already an id in any of the six content tables." Body: the existence checks with `SqlRows.RowId` over `MigrationRunner.AllTablePairs`, `Step("rekey-row")`, `SqlRows.RekeyRow(_conn, tx, MigrationRunner.TableName(tier, ContentType.Memory), oldId, newId)` (`UPDATE <table> SET id = $new WHERE id = $old`; throws `InvalidOperationException` when `changes() != 1`), `Step("rekey-parent")`, `SqlRows.RekeyParent(_conn, tx, table, oldId, newId)` for each table of `AllTablePairs` (`UPDATE <table> SET parent_id = $new WHERE parent_id = $old`).
  - `GitSyncReviews.RekeyConflictItem(conn, string fromId, string toId, tx = null)`: `UPDATE git_sync_review SET entry_id = $to WHERE entry_id = $from AND kind = 'conflict'` (a missing row is a no-op; a `conflict` row already under `toId` makes SQLite throw on the primary key, which the caller rules out by condition 5).

- [ ] **Step 1: Write the failing tests.** In `SqliteStoreTransactionTests`:
  - `RekeyMemory_KeepsRowidVectorFtsAndEveryColumn` (a hot sticky memory with tags `["a","b"]`, summary, metadata, `access_count` 7 set by SQL and a vector; after commit: `store.Get(Hot, Memory, new)` has every column equal to the old row's but the id (compare the raw `SELECT * ... ` rows except column `id`), `Get(old)` null, the rowid unchanged, `hot_memories_vec` count unchanged and the vector bytes at that rowid unchanged, `SELECT rowid FROM hot_memories_fts WHERE hot_memories_fts MATCH '<a word of the content>'` returns the rowid whose id is `new`, `IsSticky(new)` true);
  - `RekeyMemory_RewritesParentIdInEveryContentTable` (a warm memory and a cold knowledge entry with `parent_id = old`: both name `new`);
  - `RekeyMemory_RefusesAMissingSourceOrATakenTarget` (old absent; new present in another table (knowledge): `InvalidOperationException`, nothing changed after rollback);
  - `AFaultAfterTheRekey_RollsTheRekeyBack` (`BeforeStep = FailAt("rekey-parent")`: after dispose the old row, its vector and FTS row and the children's `parent_id` are as before).
  - In `GitSyncReviewsTests`: `RekeyConflictItem_MovesOnlyTheConflictRow` (a `conflict` and a `new-pin` row of `x`: only the conflict row is under `c`, payload, hash, machine, state and `arrived_at` unchanged).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SqliteStoreTransaction|FullyQualifiedName~GitSyncReviews"`. Expected: FAIL (compile errors).
- [ ] **Step 3: Implement** the members as specified (raw SQL, no `updated_at` change).
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the store step that re-keys a memory and its conflict item"`.

### Task 6: Convergence (D3, D4, D5, D6)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Converge.cs`
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.Entry.cs` (extract `PlanAndCarry`; `CarryOut` convergence steps), `GitSyncApplier.Pull.cs` (the converging branch in the loop), `GitSyncApplier.cs` (class doc and the `BeforeEntryStep` doc: the new steps)
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncApplierConvergenceTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 5 (`ConvergeTarget`, `AliasPass`, `RekeyMemory`, `RekeyConflictItem`, `GitSyncAliases.RetargetImports`, `MarkResolved`, `HasAnyItem`, `CarryOut(..., aliasPlan)`).
- Produces:
  - `private sealed record Convergence(string OtherId, string CanonicalId, string Hash, GitSyncRow OtherRow, LocalRead.Present OtherRead, IReadOnlyList<PinItem> OtherPins, ConflictItem? OtherConflict)`.
  - `private EntryResult PlanAndCarry(string id, SyncMerge.MergeDecision decision, bool converted, bool reopen, bool heldAfter, GitSyncRow? row, LocalRead read, HeldItem? held, bool proven, string? blobSha, ParsedRecord? oldBase, ParsedRecord? theirs, IReadOnlyList<PinItem> pinItems, ConflictItem? conflictItem, string thisMachine, AliasPlan? aliasPlan, Convergence? convergence)`: the tail of `ApplyCore` from `StorePlan plan;` moved unchanged (`local` = `(read as LocalRead.Present)?.View`), except: the no-op shortcut is skipped when `convergence` is not null; `BeforeTransaction?.Invoke(convergence?.OtherId ?? id)`; `CarryOut` gets `convergence`. `ApplyCore` keeps everything up to and including the `Defer`/`DropRow` returns and then calls it with `convergence: null`.
  - `internal EntryResult ApplyConvergence(string x, ConvergeTarget target, bool proven)`: `ConvergeCore`, then `Failed` → `RecordFailure(x, null, f)`, `Skipped` → `RecordSkip(x, s)`, `NoOp` → `CleanUp(x, r)`, else the result (as `ApplyOne`).

  `ConvergeCore(x, target, proven)`, before the transaction:
  1. `BeforeEntry?.Invoke(x)`; `x` not eligible → `Skipped("the id is not eligible")`.
  2. Read (a `FormatException` → `Failed("the row cannot be read: ...")`): `rowX = GitSyncRows.Read(conn, x)`, `readX = ReadLocal(x)`, `pinsX = PinPlanning.ItemsOf(conn, x)`, `conflictX = GitSyncReviews.ReadConflictItem(conn, x)`, `heldX = GitSyncReviews.ReadHeldItem(conn, x)`, `thisMachine = ThisMachine(conn)`; for `c = target.CanonicalId`: `readC = ReadLocal(c)`, `InKnowledge(c)`, `GitSyncRows.Read(conn, c)`, `GitSyncReviews.HasAnyItem(conn, c)`.
  3. Unless `readX is LocalRead.Present`, `rowX` is `active` with a base, `heldX` is null, `readC is LocalRead.Absent`, no knowledge, no row and no review row for `c` (conditions 1, 5, 7 as of now): `Deferred("the entry changed since the decision was made")`.
  4. `decision = SyncMerge.decide(BuildInput(null, null, target.Record, present.View, proven, null))` (the unchanged no-base merge, D3 step 3; always `MergeGroups` with `RowState = Some Active`, else `Failed("convergence needs a merge decision")`).
  5. `return PlanAndCarry(c, decision, converted: false, reopen: false, heldAfter: SyncConflict.heldAfter(decision, false), row: null, read: present, held: null, proven, blobSha: target.Sha, oldBase: null, theirs: target.Record, pinItems: [], conflictItem: conflictX is null ? null : conflictX with { EntryId = c }, thisMachine, aliasPlan: null, convergence: new Convergence(x, c, target.Hash, rowX, present, pinsX, conflictX))`.

  So before `BEGIN IMMEDIATE`: `PlanStore` (the canonical text embedded when the decision takes a different text, D3), `ExpectedEntry`, the planned row and `PinPlanning.Plan` over `c`'s expected item (`x`'s re-keyed conflict item and the decision's `PinEdit`/`NewPin`/`Conflict` requests), all by the existing code.

  `CarryOut` with `convergence` (`seam` = `convergence?.OtherId ?? id` replaces `id` in every `Step(...)` call and in `tx.BeforeStep`):
  - before the ordinary re-read, at phase `reread`: re-read `x`'s row, entry, pin items, conflict item and held item, and `c`'s memory, knowledge, row and `HasAnyItem`; any difference from the `Convergence` (row, `LocalRead`, pins `SequenceEqual`, conflict by value, held null, `c` free) → `Deferred("the entry changed since the decision was made")`. Then phase `work`: `Step(seam, "rekey")`, `tx.RekeyMemory(convergence.OtherRead.Tier, x, c)`, `GitSyncReviews.RekeyConflictItem(conn, x, c, dbTx)`; then phase `reread` again and the ordinary re-read of `c` runs unchanged (it must equal `row: null`, `read`, `held: null`, `[]` and the planned conflict item; Review Focus 1);
  - at the `row` step, `next = next with { AllowedHash = convergence.OtherRow.AllowedHash }` before the write (Spec/code note 9);
  - the item step is unchanged (`c`'s plan recomputed and `Matches`ed; the reconcile runs before the arrival, D5);
  - after the `alias` step and before `close`: `Step(seam, "converge")`, then `GitSyncRows.Delete(conn, x, dbTx)`, `GitSyncIssues.Delete(conn, x, GitSyncIssues.ApplyFailed, dbTx)`, `GitSyncReviews.DeletePinItem(conn, x, kind, dbTx)` for each of `PinPlanning.Kinds`, `GitSyncAliases.RetargetImports(conn, x, c, dbTx)`, `ApplyPlanning.LogImport(tx, c, theirs!.Record, stored!)`, `GitSyncAliases.MarkResolved(conn, c, x, convergence.Hash, dbTx)` (D3 steps 4 to 7);
  - the result is `Carried(Updated, ...)` (the row is always written), so D6 holds with no new summary field.

  In the loop (`GitSyncApplier.Pull.cs`, before the `ApplyOne` call at 228): `if (aliases.Converging.TryGetValue(id, out var target)) { Tally(ApplyConvergence(id, target, fetch.Proven)); continue; }`, and `ApplyOne(..., aliasIgnored: aliases.Ignored.Contains(id))`. A failed, skipped or deferred convergence leaves `x` and its row as they are; `HoldIfUnprovenCandidate` already returns for it (Task 4).

- [ ] **Step 1: Write the failing tests** in `GitSyncApplierConvergenceTests`. A helper `AssertConverged(rig, x, c, ApplySummary s)` checks: `Has(x)` false and `Has(c)` true; the vector count of all tiers unchanged against the count before the pull; no `git_sync_entry` row for `x`; `c`'s row `active` with `base_json` parsing to the canonical record (`ApplyPlanning.ParseBase(c, ...)`: id `c`, its content and stamps), comparison columns equal to the stored entry's; no `import_log` row with `target_entry_id = x`; a `git-sync` row `sync:c` with hash `h` targeting `c`; the alias row `c|x|h|resolved`; `s.Updated >= 1`, `s.Deleted` and `s.Held` empty, `s.Complete`, `s.Deferred` empty. Every test below ends with `AssertConverged` unless it says otherwise, and asserts the entry's tier, tags, sticky flag, content and items (`ConflictItem`/pin items read by the rig's connection). `SyncedPair` is Task 4's; local edits are made with `rig.Store.Update`/`SetSticky`/`Move` and a scan before the removal.
  - D4: `SameContent_AdoptsCreatedAt_AndFilesNothing` (equal text, project, scope, type; different `createdAt`: the entry's `created_at` is the canonical record's, no review row);
  - `ADifferentText_TakesTheCanonicalText_AndFilesTheLocalTextAsAConflict` (NC4: content = canonical text, conflict item `open` with loser `local|<x text>||<this machine>`, hash `SyncConflict.shownHash (Some canonical) [x text]`);
  - `APinnedEntryWithADifferentText_KeepsThePinText_AndHoldsAPinEdit` (NC2: `x` moved to hot and pinned locally before the removal; content stays `x`'s text, sticky, a `pin-edit` item on `c` with `ApplyPlanning.BaseShownHash(canonical)`);
  - `OnlyProjectOrScopeDiffers_TakesTheRecords_WithNoItem` (NC3: same text, local project `p-local`: project = the record's, no review row);
  - `APinnedEntryAndANonStickyRecord_StaysPinned_AndTheStickyStampIsRestamped` (NS2: sticky, `c`'s row `StickyStamp` not null);
  - `AStickyRecordAndAnUnpinnedEntry_StaysUnpinned_WithANewPinItem` (NS3: not sticky, a `new-pin` item on `c`);
  - `TheTags_AreTheUnion` (local tag `t-local`, record tag `t-remote`: both);
  - `TheWarmerTierWins_AndALocalWinnerIsRestamped` (theory: `x` hot / record warm → hot and `TierStamp` not null; `x` cold / record warm → warm, no pending tier stamp);
  - `APendingLocalTextEdit_IsFiledAsAConflict_NotWrittenBack` (a local text edit, scanned (`ContentStamp` on `x`'s row), then the removal: content = canonical, the edited text is the loser, no `FreshContentStamp` note in `s.Notes`);
  - order and pulls: `TheCanonicalAddAndTheRemoval_InOnePull_Converge` (only `x` synced, then one commit adds `c` and removes `x`); `TheAddThenTheRemoval_InTwoPulls_EndTheSame` (two rigs with the same ids, records and fixed applier clock: `Snapshot` of both equal); `TheCanonicalPresent_ConvergesInALaterPull` (`c` added and pulled, later `x` removed);
  - `ACanonicalVisitDeferredByARacingImporter_StillConvergesTheOther` (one commit adds `c` and removes `x`; `BeforeTransaction = id => { if (id == c) SeedLegacy(... another id ..., h) }`: `c` is in `Deferred`, `x` converges (`AssertConverged` without `Complete`/`Deferred`, the alias row written by the convergence `resolved`), `L` not advanced; the next pull is complete);
  - `TheTwoLocalMembers_TheLowestConverges_TheOtherIsIgnored` (two `x1 < x2` with canonical `c < x1`; `SyncedPair` cannot make two local members, because the second record with origin `h` would be skipped, so the records of `x1` and `x2` are committed without an origin and pulled, then `import_log` rows `h → x1` and `h → x2` are added with `ImportLog.LogImport("claude-code", ...)`, then `c`'s record with origin `h` is pulled (skipped, rows `c|x1`, `c|x2`); both records removed together: `x1` converged, `x2` kept, `c|x2|h|open`, neither counted) and `..._AlsoWhenTheLowestDefers` (`BeforeTransaction` edits `x1`'s text: `x1` deferred and kept, `x2` kept and not converged, no hold, nothing deleted);
  - `TheLowestCandidateNotParsing_ConvergesOntoAHigherOne` (rows `a|x` and `b|x` open with `a < b < x`, `a`'s path holds invalid JSON: converges onto `b`; `a|x` stays `open`. Setup: `b`'s record carries origin `h` and `x` has an `import_log` row for `h` (or `b` was pulled and skipped in an earlier pull), so `b` is not inserted when it is visited before `x`);
  - `ThreeMembersOneAtATime_ConvergeOntoTheLowestParsingCandidate` (Task 3's `(a,b)` then `(c,b)` with `c < a`: removing `b`'s record converges `b` onto `c`; `a|b|h|open` untouched);
  - `AnEarlierApplyFailedIssueOfTheOther_IsRemoved` (an `apply-failed` row seeded on `x` with `GitSyncIssues.Upsert`);
  - `TheAllowedHash_IsCarried` (`x`'s row `allowed_hash = "abc"` set by SQL: `c`'s row has it, `blocked_*` NULL);
  - `TheOthersConflictItem_IsReKeyed_AndALoserEqualToTheAppliedTextIsDropped` (an item on `x` with losers `[canonical text; "Old loser."]`, local text = canonical: losers `["Old loser."]`, `unseen`, hash of the canonical text) and `TheOthersConflictItem_LeftWithNoLoser_IsRemoved` (losers `[canonical text]`, same text: no item);
  - `EightOldLosersAndADifferingLocalText_KeepTheNewestLocalText_AndDropTheOldest` (an item on `x` with losers `l1..l8`, `x`'s text differs: losers `l2..l8, <x text>` in that order);
  - `D4sArrival_IsDedupedAgainstTheOthersReKeyedLosers` (an item on `x` with losers `["Old loser.", <x text>]`, `x`'s local text differs from the canonical text, so D4 files `<x text>` as a loser: the re-keyed item lists `<x text>` once, `["Old loser.", <x text>]`, hash of the canonical text);
  - `TheOthersPinItems_AreRemoved` (an `approved` `new-pin` and an `open` `pin-edit` row seeded on `x`: none left on `x`);
  - `ParentIdAccessCountAndVector_FollowTheReKey` (a child memory with `parent_id = x`, `access_count` 5 set by SQL on `x`: the child names `c`, `c` has `access_count` 5, `c`'s vector bytes equal `x`'s before when the text is unchanged);
  - `AnUnprovenPull_Converges` (the `last-synced` ref deleted before the removal pull);
  - `InAnUnprovenPull_AFailedConvergence_OpensNoFallbackHold_AndTheNextPullConverges` (`BeforeEntryStep = (id, step) => { if (id == x && step == "converge") throw ... }` in an unproven pull: `s.Failed` holds `x`, `s.Held` empty, `x` and its row unchanged; the seam cleared, the next pull converges);
  - `AFaultAfterTheReKey_RollsEverythingBack` (fault at `converge`, proven: `x`'s entry, vector, FTS row, row and conflict item exactly as before (`Dump` equal except the new `apply-failed` row of `x`), no entry `c`, alias still `open`);
  - `AChangedEntryBeforeTheTransaction_DefersTheConvergence` (`BeforeTransaction` edits `x`'s text: `Deferred` holds `x`, nothing else written);
  - `TheReappearanceOfTheOthersRecord_IsSkippedAsAnAlias` (after convergence `x`'s record is committed again: `NoOp`, no entry `x`, no row `x`);
  - `AfterConvergence_ARepeatedPullIsANoOp` (a pull with nothing new: `TransactionCount` unchanged, dump equal).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplierConvergence"`. Expected: FAIL on every new test (the id is ignored, not converged); Task 4's tests stay green.
- [ ] **Step 3: Implement** the extraction (a move: `git diff` of `ApplyCore`'s tail shows only the new parameters and the three convergence changes), `GitSyncApplier.Converge.cs`, the `CarryOut` steps and the loop branch as specified; extend the `BeforeEntryStep` doc in `GitSyncApplier.cs` with `alias`, `rekey`, `rekey-row`, `rekey-parent`, `converge`, and the class doc with one sentence: "A memory whose record disappeared under an open alias converges onto the canonical record in one transaction (alias D3): it is re-keyed and the canonical record is applied as a first sync; it is never read as a deletion."
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS, no existing test changed.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Converge a memory onto the canonical record when its alias record disappears"`.

### Task 7: Three-machine proofs and the guides

**Files:**
- Modify: `tests/TotalRecall.Infrastructure.Tests/GitSync/TwoMachineRig.cs` (a third machine), `AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md`
- Create: `tests/TotalRecall.Infrastructure.Tests/GitSync/AliasCycleTests.cs` (`[GitFact]`/`[GitTheory]`, `TwoMachineRig`)

**Interfaces:**
- Consumes: everything above.
- Produces (`TwoMachineRig`): `public const string MachineT = "7b3d6f21"`; `public CycleRig T { get; }` (built like `B` on `A.Push.Remote`, disposed first); `public void JoinT()` (as `JoinB` with `MachineT`); `public Task<CycleOutcome> RunT(CancellationToken ct = default)`; `public void RemoveOnRemote(string id)`: an ordinary commit by "another machine" made in a scratch clone under `A.Pull.Source.Env.Root` (`git clone <A.Push.Remote.Url> <dir>`, `git rm memories/<shard>/<id>.json` via `SyncValidation.recordPath(id)`, `git commit -m "remove <id>"`, `git push origin HEAD:main`, run with `A.Pull.Source.Env.RunGit`), so no machine's clone is rewritten. Existing `TwoMachineRig` users are unchanged (T is created but never joined by them; if building a third `CycleRig` in every existing test is measurably slow, make `T` lazy).

- [ ] **Step 1: Write the tests.** The legacy entries are seeded directly (spec Tests: a current import derives its id from the hash, so the importer cannot produce the case): `AliasTestSupport.SeedLegacy(m.Pull.Store, m.Pull.Connection, id, text, h, Tier.Warm)` with `h = AliasTestSupport.Hash("~/.claude/memory/use-bun.md")`, ids `IdA = "3f9c2a7e-0000-4000-8000-0000000000a1"` (on A) < `IdB = "3f9c2a7e-0000-4000-8000-0000000000b2"` (on B).
  - `TwoLegacyCopies_NeitherMachineGetsADuplicate_AndEachRecordsOneAlias`: `Seed()`, `JoinB()`, seed A's and B's legacy copies (same text); `RunA`, `RunB`, `RunA`: A holds only `IdA`, B holds only `IdB`, each has exactly one alias row `IdA|IdB|h|open`, every cycle complete (the pushed records carry the origin `h` through the pusher's `FirstOrigin`).
  - `AThirdMachine_ConvergesOntoTheCanonicalEntry_WhenTheOtherRecordIsRemoved` (`[GitTheory]`, `pinned: false|true`): A's copy `"Use bun."`, B's copy `"Use bun, not npm."` (same `h`); `RunB` (B pushes `IdB`); `JoinT()`, `RunT` (T inserts `IdB`); on T tag `IdB` with `t-local`, move it to hot, and when `pinned` pin it (`SetSticky`); `RunA` (A pushes `IdA`, skips `IdB`); `RunT` (T skips `IdA`: one alias row, still one entry); `RemoveOnRemote(IdB)`; `RunT`: T holds one entry, `IdA`, hot, tags include `t-local`, `IdB` gone, the alias row `IdA|IdB|h|resolved`, no held deletion, and `pinned: false` → text `"Use bun."` with a `conflict` item on `IdA` whose loser is `"Use bun, not npm."`; `pinned: true` → sticky, text `"Use bun, not npm."`, a `pin-edit` item on `IdA`. Every cycle complete.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AliasCycle"`. Expected: PASS (Tasks 3 to 6 built it). To see each can fail, temporarily return `aliasPlan = null` (the skip) or skip the converging branch and watch it go red, then restore it. A real failure is fixed in the owning task's code, never by weakening the test.
- [ ] **Step 3: Update the guides.**
  - `AGENTS.md`: the git sync pull row of "WHERE TO LOOK" (line 93) gains "aliases: a pulled record whose origin is a different local memory is skipped and recorded in `git_sync_alias`; a memory whose record disappears under an open alias converges onto the canonical record (#165)" and the files `GitSyncAliases.cs`, `GitSyncApplier.Alias.cs`, `GitSyncApplier.Converge.cs`, `Core/SyncAlias.fs`; a new row "Git sync alias detection and convergence (#165)" with those files.
  - `src/TotalRecall.Infrastructure/AGENTS.md`: a paragraph **"Git sync alias detection and convergence (`GitSync/GitSyncAliases.cs`, `GitSyncApplier.Alias.cs`, `GitSyncApplier.Converge.cs`, the `alias` step of `GitSyncApplier.Entry.cs`, `SqliteStoreTransaction.RekeyMemory`; rules in `Core/SyncAlias.fs`; milestone 1b-iii part 5, #165)"** after the conflict review paragraph, covering: the lookup (any `source_tool`, memory type, existing, eligible, not the record's id), the skip (`skipInstead`, plain `Ignore`, per visit, `AliasPlan` planned and recomputed, a repeated pull opens no transaction, never defers by itself), the pre-pass (only when an active row with a base is absent from R, the `blobs` map, the converging and ignored ids, excluded from the preview and the fallback hold), the converging transaction (re-key, the unchanged no-base merge through `PlanAndCarry`, the finish step, `resolved`), and the limits (no pass, no withhold, no `abandoned`; a non-canonical entry is still pushed; an open group whose canonical record is gone stays open). In the pull paragraph (line 74) add the pre-pass to the order of work and `alias`, `rekey`, `rekey-row`, `rekey-parent`, `converge` to the test seam steps; in "Store groundwork" add `SqliteStoreTransaction.RekeyMemory`; in the `GitSyncReviews` sentence add `HasAnyItem` (the one cross-kind read) and `RekeyConflictItem`; in the conflict review paragraph the sentence "**Helper for the alias pass (Part 5).**" becomes "**Helper for the alias pass (milestone 5).**" and notes that Part 5's convergence does not use it (the item is re-keyed, D5).
  - `src/TotalRecall.Core/AGENTS.md`: a `SyncAlias.fs` row after the `SyncApply.fs` row: "Git sync, alias detection (milestone 1b-iii part 5, #165): `canonical` (ordinal minimum), `detect` (the group and the rows to write), `ignored` / `skipInstead` (the plain `Ignore` replacing an insert, as `SyncDeletion.holdInstead` replaces a deletion), `target` (the lowest parsing candidate) and `choose` (the converging and ignored ids). Pure and total."
  - `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md`: after the `### 1b-iii part 4. Conflict review (#164)` section, a heading `### 1b-iii part 5. Alias detection and skip-insert (#165)` followed by the ticket's 17 criteria copied verbatim from `/home/gomanjoe/.cache/tmp/develop-5GvV92/b.md` between `<!-- tr-develop:ac-begin -->` and `<!-- tr-develop:ac-end -->` (the markers themselves not copied).
  - No README, user doc, catalog, hook or plugin file changes.
- [ ] **Step 4: Verify everything:** the release build; the dev loop; `--filter "FullyQualifiedName~GitSyncCycleCallerTests|FullyQualifiedName~TwoMachine|FullyQualifiedName~ConflictReviewCycle|FullyQualifiedName~PinReviewCycle"`; the profile verify commands of `.github/develop.md` (`npm ci --ignore-scripts` and `sh scripts/fetch-bge-small.sh` if the worktree lacks them, then `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`, `npm run test:shim`, and the catalog drift guard `dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q` then `dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -`); `git diff --quiet origin/main -- tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncCycleCallerTests.cs`; the spec guard from Global Constraints. Expected: all pass, no new warning, no diff.
- [ ] **Step 5: Commit** `git add tests AGENTS.md src/TotalRecall.Infrastructure/AGENTS.md src/TotalRecall.Core/AGENTS.md docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md && git commit -m "Prove alias detection and convergence across three machines and document it"`.
