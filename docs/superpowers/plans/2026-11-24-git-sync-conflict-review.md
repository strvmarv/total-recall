# Git sync conflict review (milestone 1b-iii part 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The pull applies the clock's winner of a text conflict at once and files the losing text as a `conflict` item; nothing postpones any more; the person answers by id with `keep`, `swap` or `merge` (shown hash required) through the review service and the hidden `sync review` verbs.

**Architecture:** A pure Core module `SyncConflict` holds the shown hash (D2), the append rule (D1) and the ordered reconcile rules (D4). The applier drops `SyncApply.postponement`, carries every decision out and, in the existing item step of Part 3, plans the conflict item's one write (`ConflictWrite`) inside the `PinPlan`, before `BEGIN IMMEDIATE` on the expected post-decision state and again inside it (a difference defers). `GitSyncReviews` gains conflict members, `ConflictItems` is the shared arrival rule (also the Part 5 helper), `ReviewService` gains `KeepAsync`/`SwapAsync`/`MergeAsync` on the generic frame, `ReviewCommand` gains `keep`/`swap`/`merge`.

**Tech Stack:** .NET 10, C# (Infrastructure, Cli) and F# (Core), xUnit (C# tests) and Expecto (Core tests: a `testList` name must contain the text the `--filter "FullyQualifiedName~..."` uses, e.g. `testList "SyncConflict.reconcile"`), SQLite, a bare git repository in tests.

**Spec:** `docs/superpowers/specs/2026-11-24-git-sync-conflict-review-design.md` (read it first; decisions quoted as "D<n>"). Parent: `2026-11-21-git-sync-review-secrets-design.md` (Shared decisions 1 to 9, Part 4). Mechanism generalised: Part 3, `2026-11-23-git-sync-pin-review-design.md` and its plan `docs/superpowers/plans/2026-11-23-git-sync-pin-review.md`.

## Global Constraints

- The approved spec is read-only for the whole run: `git diff --quiet bd212589d2cbe0cf91be0bb43299f4b9e27dea66 HEAD -- docs/superpowers/specs/2026-11-24-git-sync-conflict-review-design.md` must exit 0 before every push.
- No schema change, no migration (`git_sync_review` already admits kind `conflict`, states `open|unseen`, `machine`, `payload_json`). `SyncMerge.decide`, the record format and every `SyncMergeTests` case stay unchanged.
- Only `GitSyncCycle*.cs` may name the cycle and only `GitSyncPusher*`/`GitSyncCycle*` files may name the pusher; `ConflictItems.cs`, `PinPlanning.cs`, `ApplyPlanning.cs`, `ReviewService*.cs` and `ReviewCommand.cs` name neither; `GitSyncCycleCallerTests` stays unchanged and green.
- The verbs stay hidden (`ReviewCommand.Hidden => true`); no README, user doc, catalog, hook or plugin file names them; `HiddenCommandTests` needs no change.
- Refusal names added are exactly `no-conflict-item`, `unknown-loser`, `bad-merge-text`; reused: `stale-item`, `shown-hash-required`, `unreadable-entry`, `unshowable-text`, `over-pin-cap`, `busy`, `failed`. Item kind exactly `conflict`; payload exactly `{"losers":[{"side":..,"text":..,"stamp":..,"machine":..}]}` written with `Utf8JsonWriter`; losers bounded at 8; merge text at most 262144 UTF-8 bytes.
- No AI attribution or co-author trailer in any commit message. No `--force` push. Never commit `TestResults` hang dumps. No secret literal in any source file: tokens come from `FakeSecrets` (`tests/TotalRecall.Infrastructure.Tests/Transcripts/Support/FakeSecrets.cs`).
- Test projects: Core `tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`; Infrastructure `tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj`; CLI `tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj` (no Infrastructure internals: seed review rows by plain SQL; `SyncConflict.shownHash` is public Core).
- Dev loop (each task): `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~Sync"`; `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync|FullyQualifiedName~ReviewService"`; from Task 6 also `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "FullyQualifiedName~Sync|FullyQualifiedName~HiddenCommand"`; the release build adds no warning (`dotnet build src/TotalRecall.sln -c Release`).
- Where a task changes behaviour an existing test pins, change that test in the same task and name it in the commit message.

## Review Focus

1. **The deferral loop.** If the planned and the in-transaction conflict plans read a different entry or a different "held after this decision", the id defers on every pull and `L` never advances: a conflict item beside a pin-edit rule 4 apply, and a first W8.4 hold, must each be carried out and a repeated pull be a no-op (Task 4: `AConflictItemAndPinEditRule4InOnePull_AgreeAndAreNotDeferred`, `AFirstLocalHold_KeepsTheItem_AndTheNextPullIsANoOp`).
2. **The only copy of a local text.** An NC4/FC5 item whose entry is then deleted here and held (W8.4, or a guard-converted push deletion) must survive until the deletion is carried out, and be listed with its losers after `restore` (Task 4: `AFirstW8_4Hold_KeepsTheItem`, `AGuardConvertedHold_KeepsTheItem`; Task 5: `AConflictItem_SurvivesAHeldLocalDeletion_AndIsListedAfterRestore`).
3. **A swap stamp that does not win.** With a pending local content stamp newer than the base (FC4 kept the local text), the swap stamp must exceed both, and the swapped text must end on both machines (Task 5: `ASwap_IssuesAStampAboveTheBaseAndThePendingStamp`; Task 7: `KeepSwapAndMerge_EachLeaveTheStatedTextOnBothMachines`).
4. **Hostile loser text** (ESC, U+202E, CRLF, a line equal to the end delimiter, 300 000 characters): the list removes the characters and caps it, the CLI loser block cannot be ended early, `swap` of such a remote loser is `unshowable-text` while a local loser swaps as it is (Task 5: `AHostileRemoteLoser_IsSanitisedInTheList_AndSwapRefusesUnshowableText`; Task 6: `Review_List_ALoserHoldingTheDelimiter_CannotEndItsBlock`).
5. **A refusal that writes.** A stale hash commits only the item (`unseen`, live hash; dump otherwise equal); every other refusal leaves the dump byte-equal (Task 5: `AStaleHash_IsStaleItem_AndOnlyTheItemIsWritten`, `EveryOtherRefusal_WritesNothing`).

## Spec test ownership

| Spec Tests clause | Task |
|---|---|
| Core: `shownHash` known answer, boundaries, order and count; `reconcile` every rule and order; `append` dedupe, bound, order; `decide` conflict cases unchanged | 1 |
| `GitSyncReviews` conflict members (read, upsert, append with dedupe and bound 8, mark unseen, delete), kind-restricted SQL; `ConflictItems.Append` without a decision | 2 |
| Nothing postpones: FC4, FC5, NC4, `NewPin` + `Conflict` carried out, pull complete, `L` advances, item fields (text, machine, stamp, side, D2 hash); repeated pull no transaction; pinned FC4 files the record's text; second conflict appends `unseen`; same text once; visit set reaches a conflict id; `ApplyDrop` removes the item | 3 |
| The push of a local winner still happens | 7 |
| Reconcile: deleted / forgotten / left removes; missing row keeps; loser equal to entry dropped and empty item removed; applied-text change `unseen`; unchanged pull rewrites nothing; W8.4, guard hold, W8.2 keep; W8.3 and approved hold carried out remove; pin-edit rule 4 interplay; changed item defers | 4 |
| `sync init`/`join` keep the item (existing `SyncSetupInitTests`, `SyncSetupJoinTests` "the conflict row stays") | 7 (final verify, unchanged) |
| `keep`/`swap`/`merge` write sets, re-embed, stamp, comparison columns, scan quiet; refusals; hot limit warm; restore survival; list | 5 |
| Two machines: later edit on both and loser listed; `keep`/`swap`/`merge` text on both after both cycles; second conflict keeps both; unanswered conflict blocks nothing; swap with a token held back by the push | 7 |
| CLI | 6 |
| Existing tests listed in D8 | 3 |
| `GitSyncCycleCallerTests` unchanged; verbs hidden | 7 (final verify) |

## File map

| File | Change |
|---|---|
| `src/TotalRecall.Core/SyncConflict.fs` (new; `TotalRecall.Core.fsproj` after `SyncPin.fs`), `tests/TotalRecall.Core.Tests/SyncConflictTests.fs` (new; after `SyncPinTests.fs`) | D1, D2, D4 |
| `src/TotalRecall.Core/SyncSetup.fs` (+ `SyncSetupTests.fs`), `SyncApply.fs` (+ `SyncApplyTests.fs`) | three refusals; `postponement` removed |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (+ `GitSyncReviewsTests.cs`), `ConflictItems.cs` (new, + `ConflictItemsTests.cs`) | conflict members, payload, arrival rule, Part 5 helper |
| `PinPlanning.cs`, `GitSyncApplier{,.Entry,.Pull}.cs`, `GitSyncCycle.cs`, `GitSyncCycle.Attempt.cs` (test seam only) | D3, D4 |
| `ApplyPlanning.cs`, `ReviewService{,.List}.cs`, `ReviewService.Conflicts.cs` (new) | D6, D7 |
| `src/TotalRecall.Cli/Commands/Sync/ReviewCommand.cs` | D7 |
| `AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` | Task 7 |

## Spec/code notes

1. The cycle builds its own applier (`GitSyncCycle.Attempt.cs:160`) with no hook, and a cancellation during the apply ends the cycle `Cancelled`, not as an incomplete `Done`; so the cycle tests of D8 get their deferral through a new test seam `GitSyncCycle.ConfigureApplier` (`internal Action<GitSyncApplier>?`, a `GitSyncCycle*` file) exposed as `CycleRig.ConfigureApplier`, used to set the applier's `BeforeTransaction`. The seam is chosen over the existing `FailureKind.Cancelled` cat-blob fault (`CycleRig.Pull.Fault`, which also yields an incomplete `Done` through `gitCancelled`) because that fault is sensitive to id order; a `BeforeTransaction` deferral of one named id is deterministic.
2. D3 says `Requests` "today ignores" conflicts; `Requests` stays pin-only (it feeds `Arrivals`) and the three gates use one new `PinPlanning.HasWork(pinItems, conflictItem, decision)`.
3. D3 names `ConflictItems.Append(conn, tx, id, loser, nowMs)`; the hash needs the stored entry's text, read through the store transaction, so the plan's signature takes `SqliteStoreTransaction tx` (which carries the connection and transaction).
4. D4 rule 3, "with the entry absent under a hold rules 2 and 3 do nothing": read as Keep (the stored hash is not rewritten); the list shows the live hash (applied text `None`) and every answer refuses `stale-item` before the hash check, so nothing can be answered with the old hash.
5. D1's fallback machine (`_meta git_sync_machine`) can be missing or invalid; the loser's machine is then `""` (listed as `-`). D7's "entry deleted here, deletion held" needs the CLI to know a hold exists: the list sets `TextSource` to `held deletion` in that case.
6. `ReviewItem` is a positional record compared by value in tests; `Losers` is filled with the shared `ReviewItem.NoLosers` (`Array.Empty<ConflictLoser>()`) for the other kinds so those comparisons hold. `EntryResult.Postponed` goes with the early return (internal; the `Pull.cs:248` case goes too).
7. Line hints checked: `SyncApplyTests.fs` 61-130 (`isNone`, `postponementTests`, `reasonNameTests`) and 360-373; `GitSyncApplierEntryTests` 138, 461, 508; `GitSyncApplierPinReviewTests` 293-322; `PinReviewCycleTests` 438; `GitSyncApplierPullTests` 327; `GitSyncApplierSafetyTests` 498; `GitSyncCyclePullTests` 135; `GitSyncCyclePushTests` 230; `GitSyncCycleDeletionTests` 437 and 627. `ReviewServiceCycleTests` does not rely on a conflict (line 354 only asserts `Postponed` empty) and stays unchanged.

---

### Task 1: Core `SyncConflict` and the refusals

**Files:**
- Create: `src/TotalRecall.Core/SyncConflict.fs`, `tests/TotalRecall.Core.Tests/SyncConflictTests.fs` (each registered after the `SyncPin` file)
- Modify: `src/TotalRecall.Core/SyncSetup.fs` (`Refusal` after `UnshowableText`, line 54; `refusalName`), `tests/TotalRecall.Core.Tests/SyncSetupTests.fs` (`allRefusals`, names list line 26)

**Interfaces:**
- Produces (`module TotalRecall.Core.SyncConflict`, opens `SyncStamp`, `SyncMerge`, `SyncScan`):

```fsharp
type ItemState = Open | Unseen                       // stateName "open"|"unseen"; tryState
type Loser = { Side: Side; Text: string; Stamp: string; Machine: string }   // Stamp "" = none
val maxLosers : int                                  // 8
val sideName : Side -> string                        // "local" | "remote"; trySide
val loserOf : thisMachine: string -> SyncMerge.ConflictLoser -> Loser
val shownHash : applied: string option -> losers: string list -> string
val append : losers: Loser list -> loser: Loser -> Loser list
val heldAfter : d: MergeDecision -> heldOpenOrApproved: bool -> bool
type ItemView = { State: ItemState; Hash: string; Losers: Loser list }
type ReconcileInput = { Item: ItemView; EntryText: string option; Row: RowState option; HeldAfter: bool }
type Reconciled = Keep | Remove | Rewrite of hash: string * losers: Loser list
val reconcile : ReconcileInput -> Reconciled
```

  `loserOf`: `Stamp.format` of the stamp and its `MachineId.value`, else `""` and `thisMachine`. `shownHash`: SHA-256 lowercase hex of `total-recall/review/conflict/v1\n`, `addField applied`, `lengthBytes losers.Length`, then `addField (Some text)` per loser in order. `append`: unchanged when a kept loser has the same text, else appended at the end and the head dropped while longer than 8. `heldAfter`: `HoldDeletion _` → true; `PushDeletion | DeleteEntry` → false; else `heldOpenOrApproved && not d.CloseHeldDelete`. `reconcile`, first match: (1) `Row` is `Some Forgotten|Some Left`, or `EntryText = None && not HeldAfter` → `Remove`; entry absent (held) → `Keep`; (2) drop losers whose text equals the entry text, none left → `Remove`; (3) `shownHash (Some entry) kept` ≠ `Item.Hash` → `Rewrite(hash, kept)`; (4) `Keep`.
- `SyncSetup.Refusal` gains `NoConflictItem | UnknownLoser | BadMergeText`, named `no-conflict-item`, `unknown-loser`, `bad-merge-text`.

- [ ] **Step 1: Write the failing tests** in `SyncConflictTests.fs`. `testList "SyncConflict.shownHash"`: a known answer the test builds with `SHA256.HashData` over bytes it writes by hand (prefix; `01 00000001 'a'`; `00000002`; `01 00000001 'x'`; `01 00000000`) for `shownHash (Some "a") [ "x"; "" ]`; `None` ≠ `Some ""`; `shownHash (Some "ab") ["c"] <> shownHash (Some "a") ["bc"]`; `["x";"y"]` ≠ `["y";"x"]`; `["x"]` ≠ `["x";""]`; 64 lowercase hex. `testList "SyncConflict.append"`: to empty; at the end; the same text from the other side is not added; the ninth drops the oldest (texts `t2..t9` left, order kept); a duplicate of a dropped text is added again. `testList "SyncConflict.loserOf"`: with a stamp (text form, its machine), without (`""`, `thisMachine`). `testList "SyncConflict.heldAfter"`: every `EntryOutcome` shape × held flag × `CloseHeldDelete`, including `HoldDeletion LocalDeletion` with no held item (true), `PushDeletion` with an approved item (false). `testList "SyncConflict.reconcile"`: rule 1 (forgotten; left; entry absent, not held; forgotten beats held), entry absent and held → `Keep` (also with a hash that differs), rule 2 (one loser equal → dropped and `Rewrite`; all equal → `Remove`), rule 3 (applied text changed → `Rewrite` with the same losers; `Unseen` item with the same hash → `Keep`), row `None` with entry present → not removed, rule 4. `SyncSetupTests`: the three refusals and their names appended. A note that `SyncMergeTests` is not touched.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~SyncConflict|FullyQualifiedName~SyncSetup"`. Expected: FAIL (compile errors).
- [ ] **Step 3: Implement** the module and the refusals as specified.
- [ ] **Step 4: Run** the step 2 command, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the pure conflict review rules and refusals"`.

---

### Task 2: Conflict items in the review store and the shared arrival rule (D1, D3 helper)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (members; class doc lines 28-34: `conflict` rows are read and changed only by the conflict members)
- Create: `src/TotalRecall.Infrastructure/GitSync/ConflictItems.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/ConflictItemsTests.cs`
- Test: `GitSyncReviewsTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `internal sealed record ConflictItem(string EntryId, string State, string? RecordHash, string? Machine, long ArrivedAt, string PayloadJson)` (all value members, so the re-read compares by value).
  - `GitSyncReviews.ReadConflictItems(conn, tx = null)` (kind `conflict`, states `open|unseen`, ordinal by id); `ReadConflictItem(conn, id, tx = null)`; `UpsertConflictItem(conn, ConflictItem item, bool keepArrivedAt, tx = null)` (insert with `item.ArrivedAt`; on `(entry_id, 'conflict')` set state, hash, machine, payload, `direction` NULL, and `arrived_at` only when `!keepArrivedAt`); `MarkConflictUnseen(conn, id, string hash, tx = null)`; `DeleteConflictItem(conn, id, tx = null)`; `internal static string ConflictPayload(IEnumerable<SyncConflict.Loser>)` (`Utf8JsonWriter`, keys `side`, `text`, `stamp`, `machine`); `internal static FSharpList<SyncConflict.Loser> ReadConflictLosers(string payload)` (a payload that is not that shape, or a loser with an unknown side or a missing key, reads as no losers / is skipped). Every statement says `kind = 'conflict'`.
  - `internal enum ConflictWriteAction { Remove, Upsert, MarkUnseen }`; `internal sealed record ConflictWrite(ConflictWriteAction Action, string? State, string? Hash, string? Machine, string? PayloadJson, bool Fresh)`; `internal sealed record ConflictState(string State, string Hash, string Machine, FSharpList<SyncConflict.Loser> Losers, bool Fresh)`.
  - `ConflictItems.Of(ConflictItem? item) : ConflictState?`; `Arrive(ConflictState? current, SyncConflict.Loser loser, string appliedText) : ConflictState` (none: `open`, `[loser]`, `Fresh`; else `SyncConflict.append`, and when the new hash differs: `unseen`, the new hash, machine of the last loser; equal hash: `current` unchanged); `Diff(ConflictItem? before, ConflictState? after) : ConflictWrite?` (none→none and equal: null; gone: `Remove`; only state/hash changed: `MarkUnseen`; else `Upsert`, `Fresh` for a new item); `Write(conn, tx, id, ConflictWrite w, long nowMs)` (`Upsert` → `UpsertConflictItem(..., keepArrivedAt: !w.Fresh)` with `arrived_at` = `nowMs`); `Append(SqliteStoreTransaction tx, string id, SyncConflict.Loser loser, long nowMs)` (the Part 5 helper: reads the item and the stored entry's text in `tx`, an absent or unreadable entry throws `InvalidOperationException`, then `Write(Diff(item, Arrive(Of(item), loser, text)))`).

- [ ] **Step 1: Write the failing tests.** `GitSyncReviewsTests`: `ReadConflictItems_ReadsOpenAndUnseenConflictRows_AndNoOtherKind` (a `held-delete`, a `new-pin`, a `conflict` `resolved` row ignored); `UpsertConflictItem_WritesTheRow_AndAReplaceKeepsArrivedAt` (columns `open|h|m|NULL|<payload>|100`, then `unseen|h2|m2|NULL|<payload2>|100`; with `keepArrivedAt: false` 200; a `new-pin` row of the id untouched); `MarkConflictUnseen_SetsStateAndHashOnly`; `DeleteConflictItem_RemovesOnlyTheConflictRow`; `ConflictPayload_RoundTrips_HostileText` (NUL, CRLF, U+202E, `"`, 300 000 characters); `ReadConflictLosers_OfAMalformedPayload_IsEmpty`; `ThePinAndHeldMembers_StillIgnoreConflictRows`. `ConflictItemsTests` (`ApplyRig`, entry seeded with `Scan.AddMemory`): `Append_FilesAnOpenItem_WithoutADecision` (hash `SyncConflict.shownHash (Some text) [loser]`, machine, `arrived_at` = now); `Append_ASecondText_IsAppendedUnseen_KeepingArrivedAt`; `Append_TheSameTextTwice_IsOneLoser_AndWritesNothing`; `Append_ANinthLoser_DropsTheOldest`; `Append_ToAnAbsentEntry_Throws_AndWritesNothing`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncReviews|FullyQualifiedName~ConflictItems"`. Expected: FAIL.
- [ ] **Step 3: Implement** the members, `ConflictItems` and the class doc.
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add conflict items to the review store and the shared arrival rule"`.

---

### Task 3: The pull stops postponing and files conflicts (D3, D8)

**Files:**
- Modify: `src/TotalRecall.Core/SyncApply.fs` (remove lines 20-42: `PostponeReason`, `reasonName`, `postponement`; module doc), `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier.cs` (class doc ~83: no decision postpones; a conflict files its loser), `GitSyncApplier.Entry.cs` (remove `EntryResult.Postponed` and lines 174-175; pre-read; gates at ~215, the re-read ~400-405, the `pins` step ~444; `ApplyDrop` ~556), `GitSyncApplier.Pull.cs` (visit set line 51/84; the `Postponed` case 248; `complete` 317 without `postponed.Count`, `Postponed` passed as an empty list), `PinPlanning.cs`, `GitSyncCycle.cs` and `GitSyncCycle.Attempt.cs` (seam, note 1), `tests/.../GitSync/CycleTestSupport.cs` (`public Action<GitSyncApplier>? ConfigureApplier`, passed in `Cycle()`)
- Create: `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncApplierConflictTests.cs` (`ApplyRig`; `[GitFact]` for `PullRig` cases)
- Test (changed, each named in the commit): `SyncApplyTests.fs` (remove `isNone`, `postponementTests`, `reasonNameTests`, the `postponement d |> ignore` line of the totality property and the property "only a conflict postpones"); `GitSyncApplierEntryTests.ANoBaseFirstSync_WithDifferingText_IsPostponedAsAReview_AndNothingChanges` → `ANoBaseFirstSync_WithDifferingText_TakesTheRecord_AndFilesTheLocalText`, `APostponedTextConflict_ChangesNothing` → `AnFC5Conflict_TakesTheRecord_AndFilesTheLocalEdit`, helper `AssertPostponed` removed; `GitSyncApplierPinReviewTests` (class doc; `AssertPostponed` removed; `ANewPinWithAConflict_StillPostpones_AndWritesNothing` → `ANewPinWithAConflict_WritesBothItems`; `AnNC4Conflict_StillPostpones` → `AnNC4Conflict_IsCarriedOut_WithAConflictItem`); `PinReviewCycleTests.ANewPinWithAConflict_StillMakesBsCycleIncomplete` → `ANewPinWithAConflict_IsCarriedOutOnB_WithBothItems_AndBsCycleCompletes` (complete, `L` = remote head, A's text not sticky, `new-pin` and `conflict` items, B's text the loser; class doc line 19); `GitSyncApplierPullTests.ANoBaseTextDifferenceStillHoldsL_WhileANewPinIsInsertedWithAnItem` → `ANoBaseTextDifference_IsAppliedWithAConflictItem_AndLAdvances`; `GitSyncApplierSafetyTests.ANoOp_RemovesTheFailureRow_InOneSmallTransaction_AndAPostponedOrSkippedEntryDoesNot` → `..._AndADeferredOrSkippedEntryDoesNot` (the NC4 device becomes `BeforeTransaction` editing `other`'s text: `Deferred`, issue kept); cycle tests whose NC4 only made the pull incomplete get `ConfigureApplier = a => a.BeforeTransaction = id => { if (id == X) <add the local memory X> }` on a record-only id (the insert re-read finds an entry: `Deferred`), asserting `Deferred` instead of `Postponed`: `GitSyncCyclePullTests.AnIncompletePull_IsDone_WithThePushSkipped_AndLastOkIsRecorded` (C), `GitSyncCyclePushTests.ThePushIsNeverReached_AfterARefusedOrAnIncompletePull` (D), `GitSyncCycleDeletionTests.ARecordAddedAndDeletedBetweenTwoPulls_IsDeleted` (P on B, seam cleared after `taken`; the final pull is then NC4: P ends with A's text and B's text in the item); `GitSyncCycleDeletionTests.AfterAPullOfOnlyDeletionsHeldItemsAndRepushMarkers_TheCycleIsComplete_AndAReviewStillMakesItIncomplete` → `..._AndAConflictNoLongerMakesItIncomplete` (Y: complete, push not skipped, `L` advances, Y has the other machine's text, B's text in the item).

**Interfaces:**
- Consumes: Tasks 1, 2.
- Produces:
  - `PinPlan(IReadOnlyList<PinWrite> Writes, string? ApplyBaseJson, ConflictWrite? Conflict = null)`: `IsEmpty` also needs `Conflict is null`; `Matches` also compares `Conflict` by value.
  - `PinPlanning.ConflictRequests(SyncMerge.MergeDecision d) : IReadOnlyList<SyncMerge.ConflictLoser>` (in order); `HasWork(IReadOnlyList<PinItem> pins, ConflictItem? conflict, SyncMerge.MergeDecision d) : bool` (an item of either sort or a request of either sort).
  - `PinPlanning.Plan(IReadOnlyList<PinItem> items, ConflictItem? conflict, SyncMerge.MergeDecision d, SyncMerge.LocalEntry? stored, GitSyncRow? rowAfter, bool recordPresent, string? machine, string thisMachine) : PinPlan`: early return on `!HasWork`; after the pin writes, over `shown` (the entry as pin-edit rule 4 leaves it): `ConflictItems.Arrive` for each request with `SyncConflict.loserOf(thisMachine, l)` (no entry: `InvalidOperationException`), then `Conflict = ConflictItems.Diff(conflict, state)`.
  - `ApplyCore`: reads `conflictItem = GitSyncReviews.ReadConflictItem(conn, id)` and `thisMachine = SyncSql.GetMeta(conn, SyncIdentity.MetaKeys.Machine) ?? ""` with the row; gate `HasWork`; `CarryOut` gets both, re-reads the conflict item (a difference defers), recomputes `Plan` with the same `thisMachine`, and after `PinPlanning.Write` calls `ConflictItems.Write` (counted `Updated`). `ApplyDrop` deletes the conflict item at step `pins`. The pull adds `ReadConflictItems(conn)` ids to the visit set.

- [ ] **Step 1: Write the failing tests** in `GitSyncApplierConflictTests` (records with `writtenBy` set, rig machine `4f2a9c1e`): `AnFC4LocalWinner_IsKept_WithTheRecordsTextAsARemoteLoser` (text and pending stamp kept, item `open`, loser `remote|<record text>|<St(20) text>|7b3d1e55`, hash `shownHash (Some local) [record text]`, `machine` = `7b3d1e55`); `AnFC5RecordWinner_IsApplied_WithTheLocalTextAsALocalLoser` (pending stamp cleared, loser stamp = the old pending stamp, machine `4f2a9c1e`); `AnNC4WithoutARow_FilesTheLocalText_WithThisMachinesId` (no scan run: stamp `""`); `APinnedEntrysOwnNewerEdit_FilesTheRecordsText` (FC4 on a sticky entry); `ARepeatedPull_ChangesNothing_AndOpensNoTransaction` (`NoOp`, `TransactionCount` unchanged); `ASecondConflict_AppendsTheText_AndMarksTheItemUnseen` (`arrived_at` kept); `TheSameLosingTextTwice_IsOneLoser`; `TheVisitSet_ReachesAConflictId_ThatNothingElseVisits` (`PullRig`, applier `BeforeEntry` records ids); `ApplyDrop_RemovesTheConflictItem`; `AFullPull_WithConflicts_IsComplete_AndAdvancesL` (`PullRig`: FC5 and NC4 ids, `Postponed` empty, `LastSha` = head). Then change the listed existing tests.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplier|FullyQualifiedName~GitSyncCycle|FullyQualifiedName~PinReviewCycle"` and the Core `SyncApply` filter. Expected: FAIL on the new and the changed tests.
- [ ] **Step 3: Implement** the signatures above.
- [ ] **Step 4: Run** the step 2 commands, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Apply text conflicts in the pull and file the loser as a conflict item"` (the body names every changed test above).

---

### Task 4: Reconcile conflict items in the pull (D4, D5)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/GitSync/ConflictItems.cs`, `PinPlanning.cs`, `GitSyncApplier.Entry.cs`
- Test: `GitSyncApplierConflictTests.cs`, `ApplyRig.cs` (`ConflictItem(string id) : string?` as `state|hash|machine|<texts joined by '¦'>`)

**Interfaces:**
- Consumes: Task 3.
- Produces:
  - `ConflictItems.Reconcile(ConflictState? current, string? entryText, SyncMerge.RowState? row, bool heldAfter) : ConflictState?` (`SyncConflict.reconcile`; `Rewrite` → `unseen`, the hash, the kept losers, machine unchanged).
  - `PinPlanning.Plan(..., string thisMachine, bool heldAfter)`: `Reconcile(Of(conflict), shown?.Content.Text, rowAfter?.State, heldAfter)` before the arrivals.
  - `ApplyCore` computes `heldAfter = SyncConflict.heldAfter(decision, held is not null)` once, after the guard conversion (`holdInstead`) and before planning, and passes the same value to both plans; the pre-transaction plan reads `ExpectedEntry` and the `BuildRow` row exactly as for the pin items.

- [ ] **Step 1: Write the failing tests** (each pull also asserts `Deferred` empty): `TheEntryDeletedHereByARemoteDeletion_RemovesTheItem` (W6.4, row `forgotten`); `AForgottenRow_RemovesTheItemAtTheNextPull` (row set `forgotten` as the push's `Forget` leaves it, then a pull of the id); `ALeftRow_RemovesTheItem`; `ARowThatDoesNotExistYet_KeepsTheItem` (row deleted by SQL, entry present, record absent: W7); `ALoserEqualToTheEntryText_IsDropped_AndTheLastOneRemovesTheItem` (a local edit to the loser's text, scanned, then a pull); `AnAppliedTextChange_MarksTheItemUnseen_WithTheSameLosers`; `AnUnchangedPull_RewritesNothing` (`NoOp`, no transaction, row bytes equal); `AFirstW8_4Hold_KeepsTheItem` (entry deleted here, unproven pull: hold written, item unchanged) and `AFirstLocalHold_KeepsTheItem_AndTheNextPullIsANoOp`; `AGuardConvertedHold_KeepsTheItem` (`ApplyOne(..., new GitSyncApplier.DeleteGuardVerdict(false, true))`); `AnOpenLocalHold_KeepsTheItem` (W8.2); `AProvenW8_3Deletion_RemovesTheItemInThatPull`; `AnApprovedHoldCarriedOut_RemovesTheItemInThatPull` (`LocalHold(..., approve: true)`); `AConflictItemAndPinEditRule4InOnePull_AgreeAndAreNotDeferred` (a pinned entry with a `pin-edit` item and a conflict item, unpinned here: the held text is applied, the conflict hash is `shownHash (Some <base text>) losers`, a repeated pull is a `NoOp`); `AConflictItemChangedBeforeTheTransaction_DefersTheEntry` (`BeforeTransaction` calls `MarkConflictUnseen`: `Deferred`, nothing else written).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplier"`. Expected: FAIL on the new tests.
- [ ] **Step 3: Implement** the signatures above.
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Reconcile conflict items in the pull"`.

---

### Task 5: `keep`, `swap` and `merge` in the review service, and the list (D6, D7)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/ReviewService.Conflicts.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/ReviewServiceConflictTests.cs`
- Modify: `ReviewService.cs` (`ReviewOperation` gains `Keep, Swap, Merge`), `ReviewService.List.cs`, `ApplyPlanning.cs` (`ApplyText`, `DropBeatenTierStamp` extracted from `GitSyncApplier.Entry.cs` pins step and called there too), `ReviewServiceTests.cs` lines 60-63 and `ReviewServicePinTests.cs` 564-570 (`ReviewItem.NoLosers` appended; values unchanged)

**Interfaces:**
- Consumes: Tasks 1, 2, 4.
- Produces:
  - `public sealed record ConflictLoser(string Side, string? Machine, string? Stamp, string? Text, bool TextCut)`; `ReviewItem` gains a last member `IReadOnlyList<ConflictLoser> Losers` and `public static readonly IReadOnlyList<ConflictLoser> NoLosers`; `List()` adds open and unseen conflict items: `Text` = applied text (`ShownText` at the pinned cap, source `local entry`; entry absent and an open or approved held deletion: `Text` null, source `held deletion`), `Machine` = `ShownField(item.Machine)`, project/scope/direction/cause null, `ShownHash` = live `SyncConflict.shownHash` of the raw fields, `Losers` newest first (`Side`, `ShownField` machine and stamp, `ShownText` text).
  - `public const string NoConflictItemText = "no open conflict review for this id; nothing changed"`; `public Task<SetupResult> KeepAsync(string entryId, string? shownHash, CancellationToken ct = default)`, `SwapAsync(string entryId, string? shownHash, int? loserIndex, CancellationToken ct = default)`, `MergeAsync(string entryId, string? shownHash, string? text, CancellationToken ct = default)` on `RunAsync<ConflictFacts>`; `internal sealed record ConflictFacts(ConflictItem Item, FSharpList<SyncConflict.Loser> Losers, LocalRead.Present Entry, GitSyncRow? Row, string? Text)`.
  - `CheckConflict` order (D6): item `open`/`unseen` (`no-conflict-item`); hash present (`shown-hash-required`); entry read (absent: `stale-item`, "the next sync closes the item", or with a held deletion "restore or accept the held deletion first"; duplicate/unreadable: `unreadable-entry`); swap/merge: row `active` with a base (missing row: `failed` "not synced yet; run a sync first"; other: `failed`); live hash (else `MarkConflictUnseen` + `stale-item` with what it shows now, `CommitRefusal`); swap: index (omitted with one loser = 1; 1-based newest first; else `unknown-loser`), a `remote` loser `ShownText(text, int.MaxValue).Text == text` (`unshowable-text`); merge: not null/blank, ≤ 262144 UTF-8 bytes, no `\0` (`bad-merge-text`); swap/merge on a sticky entry: text within the pinned cap (0 or less: 500; `over-pin-cap`, `PinnedTierLimits.ContentLimitMessage`).
  - Embedding before the transaction (swap/merge, text differing from the pre-read entry): `Embedded(text, vector)` (its first member carries the embedded text); inside, a chosen text that differs from the entry and from `Embedded`'s text is `stale-item`, nothing written.
  - Work: `keep` and an equal text: `BeforeStep("resolve")`, `DeleteConflictItem`. Swap/merge: `ApplyPlanning.ApplyText(SqliteStoreTransaction tx, string id, LocalRead.Present current, string text, float[] vector, int hotMaxContentChars) : SyncMerge.LocalEntry` (`Update { Content = text }` only; hot, not sticky, limit > 0 and `text.Length` over it: `Move` to warm with the vector, else `ReplaceEmbedding`); stamp `IssueStamp(conn, tx, NowMs(), Some(max(base.ContentStamp, row.ContentStamp)))` (null: `failed`, `NoStampText`); row `DropBeatenTierStamp(WithComparison(row, applied) with { ContentStamp = stamp }, applied.Tier, base)`; `BeforeStep("row")`, `BeforeStep("resolve")`, `DeleteConflictItem`; `Done($"swapped {id}: ...the next sync pushes it", RevertWarning)` (merge likewise).

- [ ] **Step 1: Write the failing tests** (`ReviewServiceConflictTests`, items made by `ApplyRig` pulls, `ReviewServices.For`): `Keep_RemovesTheItem_AndNothingElse` (dump equal but the row; a pending content stamp stays); `Swap_MakesTheLoserTheText_ReEmbedded_AndOnlyTheText` (project, scope, type, source, tags, tier, `created_at` equal; embedder calls +1; vector of the text); `ASwap_IssuesAStampAboveTheBaseAndThePendingStamp` (Review Focus 3, FC4 item); `ASwap_LeavesTheComparisonColumnsCurrent_AndTheScanStampsNothing`; `Merge_MakesTheGivenTextTheText`; `ATextEqualToTheEntry_OnlyRemovesTheItem`; `ASwapOverTheHotLimit_StoresTheEntryWarm`; `ASwapWithSeveralLosers_TakesTheNamedOne_NewestFirst`; `AStaleHash_IsStaleItem_AndOnlyTheItemIsWritten` (Review Focus 5: applied text changed; a loser appended); `EveryOtherRefusal_WritesNothing` (theory: no item, no hash, entry absent (both texts), missing row, several losers and no index, index 0 and 3, empty / whitespace / 262145-byte / NUL merge text, over the pinned cap with cap 50 and with cap 0 at 501 characters); `AHostileRemoteLoser_IsSanitisedInTheList_AndSwapRefusesUnshowableText` (Review Focus 4; a local loser with the same characters swaps); `AHeldSyncLock_IsBusy`; `AFaultAtTheLastWrite_RollsTheAnswerBack` (`BeforeStep` throws at `resolve`); `AConflictItem_SurvivesAHeldLocalDeletion_AndIsListedAfterRestore` (Review Focus 2); `List_ShowsAConflict_WithTheAppliedTextAndNumberedLosers`; `AnIdWithAPinItemAndAConflict_EachVerbAnswersItsOwnKind` (`approve` on a conflict-only id: `no-pin-item`; `keep` on a pin-only id: `no-conflict-item`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ReviewService"`. Expected: FAIL.
- [ ] **Step 3: Implement** the members above.
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS; every Part 2 and Part 3 `ReviewService*` test green with values unchanged.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Keep, swap and merge conflict review items"` (name the two list tests changed for the new `ReviewItem` member).

---

### Task 6: The hidden `keep`, `swap` and `merge` verbs (D7)

**Files:**
- Modify: `src/TotalRecall.Cli/Commands/Sync/ReviewCommand.cs` (usage, `Description`, list lines, the verbs, `AnswerPinAsync` filtering to `new-pin`/`pin-edit`, a stdin seam)
- Test: `tests/TotalRecall.Cli.Tests/Commands/Sync/SyncCommandTests.cs` (a `SeedConflicts` helper beside `SeedPins` (~389): a warm entry, an active row with a `base_json` from `RecordWriter`, the review row by plain SQL with `machine` and a payload built by hand; hashes via `SyncConflict.shownHash`)

**Interfaces:**
- Consumes: Task 5 (`KeepAsync`, `SwapAsync`, `MergeAsync`, `ReviewItem.Losers`, `NoConflictItemText`).
- Produces: `public ReviewCommand(Func<bool> isTerminal, Func<Stream> openStandardInput)` (the other constructors pass `Console.OpenStandardInput`). Usage adds `keep <id> --shown <hash> | swap <id> --shown <hash> [--loser <n>] | merge <id> --shown <hash> --text-file <path|->`. `--shown` required (8+ hex digits; else exit 2); `--loser` a positive integer, only for `swap`; `--text-file` required for `merge` only; `-` reads the seam's stream; at most 262144 bytes read as strict UTF-8 (`new UTF8Encoding(false, true)`, a leading BOM dropped); unreadable, invalid or oversized input is a usage error (exit 2, service not called). The item is the listing's `conflict` item of the id (none: `refused: no-conflict-item: <text>`, exit 1; listing failed: `failed`); its full hash is passed when `--shown` prefixes it, else `--shown`. List: header `<id>  conflict  <state>  arrived <ISO>  shown <h|->`, then the applied text block (or the line `entry deleted here, deletion held` when `TextSource` is `held deletion`), then per loser `loser <n>  <side>  machine <m|->  stamp <s|->` and `TextBlock($"loser {n}", text, cut)`.

- [ ] **Step 1: Write the failing tests:** `Review_List_ShowsAConflict_WithTheAppliedTextAndNumberedLosers`; `Review_List_ALoserHoldingTheDelimiter_CannotEndItsBlock` (Review Focus 4); `Review_List_ADeletedEntryUnderAHold_SaysSo`; `Review_Keep_WithAnEightDigitPrefix_IsKept`; `Review_ConflictVerbs_WithoutShown_AreAUsageError` (theory: keep, swap, merge); `Review_Keep_WithAWrongHash_IsStaleItem_ExitOne`; `Review_Swap_WithLoser_TakesThatText`; `Review_Swap_WithABadLoser_IsAUsageError` (`0`, `x`); `Review_Merge_FromAFile_SetsTheText`; `Review_Merge_FromStandardInput_SetsTheText`; `Review_Merge_WithAnOversizedOrInvalidOrMissingFile_IsAUsageError_AndChangesNothing` (theory, `DatabaseBytes()` unchanged); `Review_Keep_OfAnIdWithoutAConflict_IsNoConflictItem`; `Review_PinAndHeldVerbsAndBulkForms_IgnoreConflicts`.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "FullyQualifiedName~Sync|FullyQualifiedName~HiddenCommand"`. Expected: FAIL on the new tests; `HiddenCommandTests` green and unchanged.
- [ ] **Step 3: Implement** the command changes.
- [ ] **Step 4: Run** the step 2 filter, then the dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the hidden sync review keep, swap and merge verbs"`.

---

### Task 7: Two-machine proofs and the guides

**Files:**
- Create: `tests/TotalRecall.Infrastructure.Tests/GitSync/ConflictReviewCycleTests.cs` (`[GitFact]`, `TwoMachineRig`; service from `ReviewServices.For(rig.B.Pull.DbPath, ...)`)
- Modify: `AGENTS.md` (rows 93-95: the pull files conflicts and nothing postpones; `review` answers conflicts with `keep`/`swap`/`merge`, #164; `ReviewService.Conflicts.cs`, `ConflictItems.cs`, `SyncConflict.fs`); `src/TotalRecall.Infrastructure/AGENTS.md` (line 74: replace "Only a decision that carries a `Conflict` request ... is **postponed**" and "nothing postponed or deferred" with: nothing postpones, `ApplySummary.Postponed` is always empty; replace "no member reads or changes `conflict` rows" with the conflict members; line 78: drop "`SyncApply.postponement` postpones only a conflict" and "a new pin with a conflict still makes the cycle incomplete"; a new paragraph "Git sync conflict review" covering the item and payload, the shown hash, the arrival in the item step, the reconcile with "held after this decision", the answers and the `ConflictItems.Append` helper for Part 5); `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` (line 235-236: the sentence "... it is still held back whole until conflicts can be answered." becomes "... both items are written and B's sync completes."; a heading `### 1b-iii part 4. Conflict review (#164)` after part 3 with the ticket's 17 criteria verbatim from `/home/gomanjoe/.cache/tmp/develop-HDph1N/body.md` between the `tr-develop:ac-begin`/`ac-end` markers)

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Write the tests:** `TwoTextEdits_LeaveTheLaterOnBothMachines_AndTheLoserListedOnB` (A edits, then B later; A cycles, B cycles: FC4 keeps B's text, files A's (side `remote`, A's machine), and pushes; A cycles: both end with B's text; every cycle complete); `ARecordNewerThanBsEdit_IsAppliedOnB_AndBsTextIsTheLoser`; `KeepSwapAndMerge_EachLeaveTheStatedTextOnBothMachines` (theory keep/swap/merge; after the answer, `RunB` then `RunA`; both texts checked on both machines); `ASecondConflict_KeepsBothLosingTexts`; `AnUnansweredConflict_BlocksNothing` (B's cycle complete, `PushSkipped` null, another local memory pushed in the same cycle, held and pin items unchanged, the item still listed); `ASwapOfATextHoldingAToken_IsHeldBackByThePush` (`FakeSecrets`; the record keeps the old text, the entry is blocked).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ConflictReviewCycle"`. Expected: PASS (Tasks 3 to 5 built it); to see each can fail, temporarily skip its answer or edit and watch it go red, then restore it. A real failure is fixed in the owning task's code, never by weakening the test.
- [ ] **Step 3: Update the guides** as listed.
- [ ] **Step 4: Verify everything:** the release build; the three dev-loop commands; `--filter "FullyQualifiedName~GitSyncCycleCallerTests|FullyQualifiedName~TwoMachine|FullyQualifiedName~SyncSetupInit|FullyQualifiedName~SyncSetupJoin"`; the profile verify commands of `.github/develop.md` (`npm ci --ignore-scripts` and `sh scripts/fetch-bge-small.sh` if the worktree lacks them, then `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`, `npm run test:shim`, and the catalog drift guard `dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q` then `dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -`); `git diff --quiet origin/main -- tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncCycleCallerTests.cs`; the spec guard from Global Constraints. Expected: all pass, no new warning, no diff.
- [ ] **Step 5: Commit** `git add tests AGENTS.md src/TotalRecall.Infrastructure/AGENTS.md docs && git commit -m "Prove conflict review across two machines and document it"`.
