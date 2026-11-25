# Git sync pin review (milestone 1b-iii part 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pin that arrives from the repository becomes an ordinary entry with a `new-pin` item, a remote edit to a memory pinned here is held as a `pin-edit` item, and the person answers each by id with `approve` or `decline` (shown hash required), through the review service and the hidden `sync review` verbs.

**Architecture:** A pure Core module `SyncPin` holds the shown hash (D1), the arrival rule (D3) and the ordered reconcile rules (D4). The applier stops postponing `NewPin`/`PinEdit` (only `Conflict` postpones), carries the decision out, then runs the reconcile and the arrival in the entry's transaction, planned once before `BEGIN IMMEDIATE` on the expected post-decision state and recomputed inside it (a difference defers). `GitSyncReviews` gains pin-item members; `ReviewService` gains `ApproveAsync`/`DeclineAsync` on a kind-aware check frame; `ReviewCommand` gains `approve`/`decline`.

**Tech Stack:** .NET 10, C# (Infrastructure, Cli) and F# (Core), xUnit (C# tests) and Expecto (Core tests: a `testList` name must contain the text the `--filter "FullyQualifiedName~..."` uses, e.g. `testList "SyncPin.reconcile"`), SQLite, a bare git repository in tests.

**Spec:** `docs/superpowers/specs/2026-11-23-git-sync-pin-review-design.md` (read it first; its decisions are quoted as "D<n>"). Parent: `2026-11-21-git-sync-review-secrets-design.md` (Shared decisions 1 to 9, Part 3). Umbrella: `2026-11-15-git-sync-design.md` ("Pin review"). Pattern: Part 2, `2026-11-22-git-sync-held-deletions-design.md`.

## Global Constraints

- The approved spec is read-only for the whole run: `git diff --quiet f951ef79878478b1b4e42b382480fb3fa764c5eb HEAD -- docs/superpowers/specs/2026-11-23-git-sync-pin-review-design.md` must exit 0 before every push.
- No schema change, no migration (`git_sync_review` already has `kind` `new-pin`/`pin-edit`, states `open|unseen|approved|declined`, and a `machine` column: `Schema.cs:884-894`). `SyncMerge.decide` and every existing `SyncMerge` test stay unchanged.
- Only `GitSyncCycle*.cs` may name the cycle and only `GitSyncPusher*`/`GitSyncCycle*` files may name the pusher; `PinPlanning.cs`, `ReviewService*.cs`, `ApplyPlanning.cs` and `ReviewCommand.cs` name neither; `GitSyncCycleCallerTests` stays unchanged and green.
- The verbs stay hidden (`ReviewCommand.Hidden => true`); no README, user doc, catalog, hook or plugin file names them; `HiddenCommandTests` needs no change.
- Refusal names added are exactly `no-pin-item`, `over-pin-cap`, `unshowable-text`; reused: `stale-item`, `edited-locally`, `shown-hash-required`, `unreadable-entry`, `busy`, `failed`. Item kinds are exactly `new-pin`, `pin-edit`; a pull-made payload is exactly `{}`, a restore payload stays `{"origin":"restore"}`. Text sources stay `local entry` and `repository record`.
- No AI attribution or co-author trailer in any commit message. No `--force` push. Never commit `TestResults` hang dumps. No secret literal in any source file: tokens come from `FakeSecrets.Get("<name>")` (`tests/TotalRecall.Infrastructure.Tests/Transcripts/Support/FakeSecrets.cs`).
- Test projects: Core `tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`; Infrastructure `tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj`; CLI `tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj` (no Infrastructure internals: seed review rows by plain SQL).
- Dev loop (each task): `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~Sync"`; `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync|FullyQualifiedName~ReviewService"`; from Task 6 also `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "FullyQualifiedName~Sync|FullyQualifiedName~HiddenCommand"`; the release build adds no warning (`dotnet build src/TotalRecall.sln -c Release`).
- Where a task changes behaviour an existing test pins, change that test in the same task and name it in the commit message.

## Review Focus

1. **The deferral loop.** A sticky record whose `project` or `scope` is `""` (a record accepts it; `LocalEntryReader.ToLocalEntry` reads an empty one back as none): if the expected post-decision entry is not normalised the same way, the planned reconcile never equals the in-transaction one and the id defers on every pull, so `L` never advances (Task 4: `AnEmptyProjectInAPinnedRecord_IsCarriedOut_AndARepeatedPullIsANoOp`).
2. **Hostile project, scope and machine strings** (newline, ESC, U+202E, 300 characters): each is one sanitised line of at most 200 characters in the list and the CLI header stays one line; `approve` refuses `unshowable-text` (Task 5: `HostileProjectScopeAndMachine_AreSanitisedInTheList_AndApproveRefusesUnshowableText`; Task 6: `Review_List_AHostileMachine_StaysOnTheHeaderLine`).
3. **A restamp decision with a pin item:** a `pin-edit` item plus a tier FR3 local-wins (or an NS2) restamp is planned with the placeholder issuer, carried out once with one real stamp, neither failed nor deferred (Task 4: `APinEditItem_WithATierRestamp_IsCarriedOut_NotFailedOrDeferred`).
4. **A stale refusal commits only the item:** the dump before and after differs only in that review row (`unseen`, the live hash, the machine) (Task 5: `AStaleHash_IsStaleItem_AndOnlyTheItemIsWritten`).
5. **Approve of a warm entry over the hot cap** (hot cap 100, pinned cap 500, text 300): moved to hot, sticky, `decay_score` 1.0; the next scan stamps nothing and a repeated pull of the same record is a no-op that does not demote it (Task 5: `ApprovingAWarmNewPinOverTheHotCap_PinsItInHot_AndNothingUndoesIt`).

## Spec test ownership

| Spec Tests clause | Task |
|---|---|
| Core: `shownHash`, `arrival`, `reconcile` | 1 |
| Core: `postponement` cases (only `Conflict` postpones) | 3 |
| Part 2 restore: shown hash and the record's machine | 2 |
| Pin from A on B: ordinary hot entry, row, base, open item with hash and machine, not in the pinned block, pull complete; over-hot-cap insert goes warm; W3 and W8.1 inserts non-sticky with an item | 3 (applier); 7 (two machines, `L`) |
| `approve` new-pin (all but the cycle), the warm-to-hot move; `decline` new-pin (entry, item, no stamp) | 5 |
| `approve` new-pin: a following cycle pushes nothing; `decline` new-pin: cycle pushes nothing, A unchanged | 7 |
| A later genuinely new pin re-opens a declined item; second remote edit `unseen`; remote edit to a pinned memory (pin text active, tags, tier, item) | 3 |
| Repeated pull changes nothing; the transitions (record change, remote unpin, deleted/forgotten, pinned here) | 4 |
| Tag edit while held pushes only tags; pin pushes as an ordinary local pin; unpin cycle pushes only the unpin; local edit pushes; declined edit pushes B's text and A holds it; secret check | 7 |
| `approve`/`decline` pin-edit (write sets, cap, warning, stale, `edited-locally`) | 5 |
| Local unpin / local edit / both while held (applier); regress to non-sticky; text back equal; project-only edit closes; `ApplyDrop`; pre-transaction reconcile and deferral; restamp with an item; visit set, `unseen` as open in reconcile and visit; no-op does not rewrite | 4 |
| Conflict decisions still postpone (`NewPin` + `Conflict`, NC4) | 3; 7 (cycle) |
| Hostile strings; `unseen` in answer and list; lock busy; one transaction | 5 |
| CLI | 6 |
| Existing postponement tests changed | 3 (and 2 for the restore tests) |
| `GitSyncCycleCallerTests` unchanged | 7 (final verify) |

## File map

| File | Change |
|---|---|
| `src/TotalRecall.Core/SyncPin.fs` (new; `TotalRecall.Core.fsproj` after `SyncApply.fs`), `tests/TotalRecall.Core.Tests/SyncPinTests.fs` (new; after `SyncApplyTests.fs`) | D1, D3 arrival, D4 reconcile |
| `src/TotalRecall.Core/SyncSetup.fs` (+ `SyncSetupTests.fs`), `SyncApply.fs` (+ `SyncApplyTests.fs`) | three refusals; `postponement` |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (+ `GitSyncReviewsTests.cs`) | `PinItem` and its members; `UpsertNewPin` replaced |
| `src/TotalRecall.Infrastructure/GitSync/PinPlanning.cs` (new), `ApplyPlanning.cs`, `GitSyncApplier.{Entry,Pull}.cs` | arrival, reconcile, D5 |
| `src/TotalRecall.Infrastructure/GitSync/ReviewService{,.List,.Restore}.cs`, `ReviewService.Pins.cs` (new) | the answers, the list, D9 |
| `src/TotalRecall.Cli/Commands/Sync/ReviewCommand.cs` | `approve`, `decline`, kind in the list |
| `AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` | Task 7 |

## Spec/code notes

1. Pin-edit rule 3's `scanned_content_hash` clause cannot fire inside the pull: `CarryOut` defers an entry that disagrees with its row (`LocalEntryReader.AgreesWithRow`, `GitSyncApplier.Entry.cs` reread step) and `BuildRow` rewrites that column from the stored entry, so in the pull only the pending content stamp closes the item; the clause matters for the service's `edited-locally`. The pure function keeps it as specified.
2. D4 new-pin rules do not say what a missing post-decision base (or row) means; the plan reads "no base" as "not sticky" (rule 3 removes the item).
3. Records may carry `project`/`scope` = `""` (`SyncValidation.opt` accepts any string) while the store reads them back as none; `shownHash` keeps them distinct (D1), so a `pin-edit` whose base has `""` never meets rule 2 against the entry and is closed only by another rule. Not changed here (Review Focus 1 guards the loop).
4. D8 does not name the refusal for an id with two pin items; the CLI uses `stale-item` ("this memory has two pin reviews; run a sync first"). D8 says newlines and tabs in project/scope/machine are "replaced": the plan replaces each with one space.
5. Spec line hints drifted: the tests are `GitSyncApplierEntryTests` 474 and 799 (not 796), `GitSyncApplierSafetyTests` 498, `GitSyncApplierRepushTests.ReviewStillPostpones` 321, `GitSyncApplierPullTests.ANewPinAndANoBaseTextDifference_AreReviewsThatHoldL` 327, `GitSyncCyclePullTests.AnIncompletePull_IsDone_WithThePushSkipped_AndLastOkIsRecorded` 135, `GitSyncCyclePushTests.ThePushIsNeverReached_AfterARefusedOrAnIncompletePull` 230, `GitSyncCycleDeletionTests` 147, 434 and 622, and `SyncApplyTests.fs` 83-99 plus the property "only a review postpones" (~362). Part 2's `ReviewServiceRestoreTests` (253, 445) also assert the blob hash D9 replaces.
6. `GitSyncReviews.UpsertNewPin` (Part 2) is the only pin writer today; it is replaced by `UpsertPinItem`. `ReviewItem` is positional, so `ReviewServiceTests` (lines 60-61) and the CLI header tests (`SyncCommandTests` 373, 378, 493, 623) change with the new shape and the kind in the header (D8), held-delete values unchanged.
7. Moving `SyncApply.postponement` into Task 1 would break the Infrastructure suite until the applier writes items, so it lives in Task 3 with the applier.

---

### Task 1: Core `SyncPin` and the refusals

**Files:**
- Create: `src/TotalRecall.Core/SyncPin.fs`, `tests/TotalRecall.Core.Tests/SyncPinTests.fs` (both registered in their fsproj after the `SyncApply` file)
- Modify: `src/TotalRecall.Core/SyncSetup.fs` (`Refusal` after `ShownHashRequired`, line 48; `refusalName`), `tests/TotalRecall.Core.Tests/SyncSetupTests.fs` (`allRefusals`, names list line 26)

**Interfaces:**
- Produces (`module TotalRecall.Core.SyncPin`, opens `SyncMerge`; the hash uses `SyncScan.addBytes`/`addField`/`sha256Hex`, which are `internal` to Core):

```fsharp
type ItemKind = NewPinItem | PinEditItem          // kindName: "new-pin" | "pin-edit"; tryKind
type ItemState = Open | Unseen | Approved | Declined   // stateName; tryState ("open" ... "declined")
type PinItemView = { Kind: ItemKind; State: ItemState; Hash: string }
type EntryView = { Sticky: bool; ContentHash: string; Shown: string }   // entryView (l: LocalEntry)
type PinRowView = { State: RowState; HasBase: bool; PendingContent: bool; ScannedContentHash: string option }
type BaseView = { Shown: string; Sticky: bool }
type ReconcileInput = { Item: PinItemView; Entry: EntryView option; Row: PinRowView option; Base: BaseView option; RecordPresent: bool }
type Reconciled = Keep | Remove | MarkUnseen of hash: string | ApplyBaseThenRemove
type Arrived = OpenItem | LeaveItem | UnseenItem
val shownHash : text: string -> project: string option -> scope: string option -> string
val arrival : existing: (ItemState * string) option -> hash: string -> Arrived
val reconcile : ReconcileInput -> Reconciled
```

  `shownHash`: SHA-256 lowercase hex of `total-recall/review/pin/v1\n`, then `addField (Some text)`, `addField project`, `addField scope`. `arrival`: none, `Approved` or `Declined` → `OpenItem`; `Open`/`Unseen` with the same hash → `LeaveItem`, else `UnseenItem`. `reconcile`: D4's rules first match in order per kind (rule 3 of new-pin: `not RecordPresent`, or `Base` is `None` or not sticky; pin-edit rule 1 includes `Row = None` or `not HasBase`; rule 3: `PendingContent || ScannedContentHash <> Some entry.ContentHash`).
- `SyncSetup.Refusal` gains `NoPinItem | OverPinCap | UnshowableText`, named `no-pin-item`, `over-pin-cap`, `unshowable-text`.

- [ ] **Step 1: Write the failing tests** in `SyncPinTests.fs`. `testList "SyncPin.shownHash"`: a known answer whose expected value the test computes with `SHA256.HashData` over bytes it builds by hand (prefix; `01 00000001 'x'`; `00`; `01 00000000`) for `shownHash "x" None (Some "")`; `None` and `Some ""` differ in each field; `shownHash "a" (Some "bc") None <> shownHash "ab" (Some "c") None`; result is 64 lowercase hex. `testList "SyncPin.arrival"`: all nine cells (none; each of four states × same/different hash). `testList "SyncPin.reconcile"`: one case per D4 row and branch: new-pin 1 (entry absent; row forgotten; row left), 2 (entry sticky), 3 (record absent; base not sticky; no base; record absent wins over rule 4), 4 (`Open` and `Unseen` with a different shown hash → `MarkUnseen entryShown`), 5 (`Declined` and `Approved` with a different hash, `Open` with the same hash → `Keep`); pin-edit 1 (absent; forgotten; left; no base; no row), 2 (entry shown = base shown, also for an `Approved` item and with a pending stamp: rule 2 before rule 3), 3 (pending stamp; scanned hash differs; and with the entry unpinned too → `Remove`, rule 3 before rule 4: "both at once, the edit stands"), 4 (entry not sticky → `ApplyBaseThenRemove`), 5 (`Open`/`Unseen`, base shown ≠ item hash → `MarkUnseen baseShown`), 6 (`Declined`, `Approved`, record absent with the entry pinned → `Keep`). `SyncSetupTests`: the three refusals in `allRefusals` and their names appended to the expected list.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~SyncPin|FullyQualifiedName~SyncSetup"`. Expected: FAIL (compile errors).
- [ ] **Step 3: Implement** the module and the refusals as specified above.
- [ ] **Step 4: Run** the step 2 command, then the whole dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the pure pin review rules and refusals"`.

---

### Task 2: Pin items in the review store, and the restore correction (D2, D9)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (members; the class doc lines 18-24: held-delete rows are read and changed by the held members, `new-pin`/`pin-edit` rows only by the pin members, `conflict` rows by none), `ReviewService.Restore.cs` (`RestoreLocal`), `tests/TotalRecall.Infrastructure.Tests/GitSync/ApplyRig.cs` (`Rec(..., string? writtenBy = null)`)
- Test: `GitSyncReviewsTests.cs`, `ReviewServiceRestoreTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SyncPin.shownHash`).
- Produces:
  - `internal sealed record PinItem(string EntryId, string Kind, string State, string? RecordHash, string? Machine, long ArrivedAt, string? Origin = null)` (`Origin` read from `payload_json` `origin`, null for `{}`).
  - `GitSyncReviews.ReadPinItems(conn, tx = null) : IReadOnlyList<PinItem>` (kinds `new-pin`, `pin-edit`, states `open|unseen|approved|declined`, ordinal by id then kind); `ReadPinItem(conn, string entryId, string kind, tx = null) : PinItem?` (same states); `UpsertPinItem(conn, PinItem item, tx = null)` (insert or replace on `(entry_id, kind)`: state, hash, machine, `arrived_at`, `direction` NULL, payload `{}` or `{"origin":"<Origin>"}` via `Utf8JsonWriter`); `MarkPinUnseen(conn, id, kind, string recordHash, string? machine, tx = null)` (state, hash, machine; payload and `arrived_at` kept); `ResolvePinItem(conn, id, kind, string state, tx = null)` (`approved` or `declined`, else `ArgumentException`); `DeletePinItem(conn, id, kind, tx = null)`. Every member's SQL is restricted to its kind; `UpsertNewPin` and `RestorePayload` are removed.
  - `RestoreLocal` writes `UpsertPinItem(new PinItem(id, "new-pin", "open", SyncPin.shownHash(stored.Content.Text, stored.Content.Project, stored.Content.Scope), Unopt(record.WrittenBy), NowMs(), Origin: "restore"))`.

- [ ] **Step 1: Write the failing tests.** `GitSyncReviewsTests`: `ReadPinItems_ReadsBothKindsInEveryAnswerState_AndNoHeldDelete` (rows `new-pin` open, `pin-edit` unseen, `new-pin` declined, `pin-edit` approved, a `held-delete`, a `pin-edit` `resolved`: four items in order); `ReadPinItem_ReadsOneByIdAndKind`; `UpsertPinItem_WritesTheItem_AndASecondReplacesIt` (columns `open|h2|m|NULL|{}|200`; with `Origin: "restore"` the payload is `{"origin":"restore"}`; a `held-delete` row of the same id untouched) replacing `UpsertNewPin_WritesAnOpenNewPinRow_AndASecondReplacesIt`; `MarkPinUnseen_SetsStateHashAndMachine_KeepingPayloadAndArrivedAt`; `ResolvePinItem_SetsOnlyTheState` (and throws for `open`); `DeletePinItem_RemovesOnlyThatKind`; `ReadOpenHeld_StillIgnoresPinRows`. `ReviewServiceRestoreTests`: `PinnedLocalHold` builds its record with `writtenBy: "machine-a"`; `AStickyBase_OpensANewPinItem_WithTheHeldItemsHash` becomes `AStickyBase_OpensANewPinItem_WithTheShownHashAndTheRecordsMachine` (`NewPinRows` also selects `machine`: `open|{SyncPin.shownHash(PinnedText, null, null)}|NULL|machine-a|{"origin":"restore"}|{Now}`); `AStickyBase_OpensANewPinItem_AndAFollowingUnprovenPull_NeitherPostponesTheIdNorChangesTheItem` expects the shown hash of the restored entry instead of the blob.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncReviews|FullyQualifiedName~ReviewServiceRestore"`. Expected: FAIL.
- [ ] **Step 3: Implement** the members, the restore change and the class doc.
- [ ] **Step 4: Run** the step 2 filter, then the whole dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add pin items to the review store and key the restored new-pin item on its shown hash"` (name the two changed restore tests and the replaced `UpsertNewPin` test).

---

### Task 3: The pull stops postponing pins (D3, arrival)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/PinPlanning.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncApplierPinReviewTests.cs` (`ApplyRig`-based; `[GitFact]` for `PullRig` cases)
- Modify: `src/TotalRecall.Core/SyncApply.fs` (`postponement`, `PostponeReason.Review` doc: "a conflict"), `GitSyncApplier.Entry.cs` (`CarryOut`: pins step after `LogImport`, before `close`)
- Test (changed, each named in the commit): `SyncApplyTests.fs` (the two cases at lines 83-99 and the property "only a review postpones", whose generator gains a `Conflict`); `GitSyncApplierEntryTests.APostponedNewPin_ChangesNothing_AndNeverCallsTheEmbedder` → `ANewPin_IsInsertedNonSticky_WithAnOpenNewPinItem`; `NewerTextThanTheDeletion_ThatPins_IsPostponedAsAReview` → `NewerTextThanTheDeletion_ThatPins_IsInsertedNonSticky_WithANewPinItem` (W3 for `forgotten`, W8.1 for `active`); `GitSyncApplierSafetyTests.ANoOp_RemovesTheFailureRow_..._AndAPostponedOrSkippedEntryDoesNot` (its postponed case becomes an NC4 conflict: a warm memory `other` with `"Local text."`, record `"Their text."`); `GitSyncApplierRepushTests.ReviewStillPostpones` → `ANewPinOnTopOfAReset_IsCarriedOut_WithAnItem_AndKeepsTheMarker` (not postponed, `Complete`, open `new-pin` item, `RepushMarked` holds A with the still-reverted groups, entry not sticky); `GitSyncApplierPullTests.ANewPinAndANoBaseTextDifference_AreReviewsThatHoldL` → `ANoBaseTextDifferenceStillHoldsL_WhileANewPinIsInsertedWithAnItem` (one postponed, B; A present, not sticky); `GitSyncCyclePullTests.AnIncompletePull_...`, `GitSyncCyclePushTests.ThePushIsNeverReached_...`, `GitSyncCycleDeletionTests.ARecordAddedAndDeletedBetweenTwoPulls_IsDeleted` and `..._AndAReviewStillMakesItIncomplete`: the pin record that only made the pull incomplete becomes an NC4 conflict (the machine first adds a local memory with that id and other text; the final `Has(..)` asserts its own text); `GitSyncCycleDeletionTests.ForgottenMemory_..._AndNewerTextBringsItBackNonSticky`: Y (newer text that pins) is now inserted non-sticky with an open `new-pin` item, `Inserted == 2`, no postponed, `Complete`.

**Interfaces:**
- Consumes: Task 1 (`SyncPin`), Task 2 (`PinItem`, `ReadPinItem`, `UpsertPinItem`, `MarkPinUnseen`).
- Produces:
  - `SyncApply.postponement d = if d.Reviews |> List.exists (function Conflict _ -> true | _ -> false) then Some Review else None`.
  - `internal enum PinWriteAction { Remove, MarkUnseen, Open }`; `internal sealed record PinWrite(string Kind, PinWriteAction Action, string? Hash, string? Machine)`.
  - `PinPlanning.Arrivals(SyncMerge.MergeDecision d, Func<string, PinItem?> itemOf, SyncMerge.LocalEntry? stored, SyncRecord? postBase, string? machine) : IReadOnlyList<PinWrite>`: per `NewPin` request kind `new-pin` with `shownHash` of `stored`; per `PinEdit` kind `pin-edit` with `shownHash` of `postBase` (text, project, scope); `SyncPin.arrival` on `itemOf(kind)`.
  - `PinPlanning.Write(MsSqliteConnection conn, SqliteTransaction tx, string id, IEnumerable<PinWrite> writes, long nowMs)` (`Remove` → `DeletePinItem`; `MarkUnseen` → `MarkPinUnseen`; `Open` → `UpsertPinItem` with state `open`, payload `{}`, `arrived_at` = `nowMs`).
  - `CarryOut`: at a new step `pins` (`Step(id, "pins")`), `Arrivals(decision, kind => ReadPinItem(conn, id, kind, dbTx), stored, ParseBase(id, (next ?? row)!.BaseJson).Parsed?.Record, Unopt(theirs?.Record.WrittenBy))` then `Write`; any write counts as `Updated`.

- [ ] **Step 1: Write the failing tests** in `GitSyncApplierPinReviewTests` (records with `writtenBy: "machine-a"`): `ANewPin_IsAnOrdinaryHotEntry_WithAnOpenItem` (W1: hot, not sticky, row `LocalSticky == false`, base sticky, item `new-pin|open|shownHash(text)|machine-a`, no sticky entry in `Store.List(Tier.Hot, ContentType.Memory, new ListEntriesOpts { StickyOnly = true })`); `ANewPinOverTheHotCap_IsInsertedWarm_WithAnItem` (`new ApplyRig(hotMaxContentChars: 100)`, 300-character text); `FS4_FS7_NS3_KeepTheEntryUnpinned_AndApplyTheOtherGroups` (theory: tags merged, tier applied, item open); `ARemoteEditToAPinnedMemory_IsHeld_TagsAndTierApply_AndAPinEditItemOpens` (FC2: text unchanged, `ContentStamp` null, base content = record's, item hash = `shownHash(recordText, project, scope)`); `ANoBaseEditToAPinnedMemory_IsHeldAsAPinEdit` (NC2); `ASecondRemoteEdit_MarksThePinEditItemUnseen_WithTheNewHashAndMachine`; `ALaterNewPin_ReopensADeclinedItem` (seed `declined` via `UpsertPinItem`, then an FS4 record with a newer sticky stamp: item `open`, new hash); `AnOpenItemWithTheSameHash_IsLeftAsItIs` (`arrived_at` kept); `ANewPinWithAConflict_StillPostpones_AndWritesNothing` (`Full()` unchanged); `AnNC4Conflict_StillPostpones`. Then change the listed existing tests.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplier|FullyQualifiedName~GitSyncCycle"` and the Core `SyncApply` filter. Expected: FAIL on the new and the changed tests.
- [ ] **Step 3: Implement** the signatures above.
- [ ] **Step 4: Run** the step 2 commands, then the whole dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Carry out pin decisions in the pull and open pin review items"` (the body lists every changed test above).

---

### Task 4: Reconcile pin items in the pull (D4, D5)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/GitSync/PinPlanning.cs`, `ApplyPlanning.cs`, `GitSyncApplier.Entry.cs` (`ApplyCore`, `CarryOut`, `ApplyDrop`), `GitSyncApplier.Pull.cs` (visit set, line ~77)
- Test: `GitSyncApplierPinReviewTests.cs`, `ApplyRig.cs` (`PinItems(string id)`: the id's rows as `kind|state|hash|machine` lines)

**Interfaces:**
- Consumes: Task 3.
- Produces:
  - `ApplyPlanning.ExpectedEntry(StorePlan plan, SyncRecord? theirs, SyncMerge.LocalEntry? local) : SyncMerge.LocalEntry?`: null for `Delete`; for `Insert` the record's content (non-sticky, `InsertTier`); else `local` with `Update` applied (content group for `TakeTheirs`, `created_at` for `AdoptCreatedAt`, tags), tier `MoveTo ?? local.Tier`, sticky false when `Unpin`; an empty project or scope becomes none, as `LocalEntryReader.ToLocalEntry` reads it.
  - `ApplyPlanning.PlaceholderIssue(FSharpOption<SyncStamp.Stamp>? floor) : FSharpOption<SyncStamp.Stamp>` (the floor, else a fixed valid stamp); `WriteStore(SqliteStoreTransaction tx, string id, Tier current, StorePlan plan, float[]? vector)` (the `Unpin`/`Update`/`Move`/`ReplaceEmbedding` branch moved out of `CarryOut`); `ApplyBaseContent(SqliteStoreTransaction tx, string id, LocalRead.Present current, SyncRecord baseRecord, float[]? vector, int hotMaxContentChars) : SyncMerge.LocalEntry` (plans a `MergeGroups` decision with content `(TakeTheirs, KeepPending)`, the entry's tags with `ApplyLocal = false`, `LeaveSticky`, no tier change, through `PlanStore`, so the fields and the tier rule are `TakeTheirs`'; writes with `WriteStore`; reads back); `WithComparison(GitSyncRow row, SyncMerge.LocalEntry stored) : GitSyncRow` (the four scanned columns).
  - `internal sealed record PinPlan(IReadOnlyList<PinWrite> Writes, string? ApplyBaseJson)` with `IsEmpty`, `Matches(PinPlan other)` (writes in order, `ApplyBaseJson` ordinal) and `static Empty`; `PinPlanning.Plan(IReadOnlyList<PinItem> items, SyncMerge.MergeDecision d, SyncMerge.LocalEntry? stored, GitSyncRow? rowAfter, bool recordPresent, string? machine) : PinPlan`: `SyncPin.reconcile` per item on `stored`, `rowAfter` and its parsed base (an unparsable base throws `InvalidOperationException`), `ApplyBaseThenRemove` sets `ApplyBaseJson = rowAfter.BaseJson`; then `Arrivals` over the items as the reconcile leaves them.
  - `ApplyCore`: reads the id's two pin items with the row; when there is one or the decision has a `NewPin`/`PinEdit`, plans `PinPlan` before `BEGIN IMMEDIATE` on `ExpectedEntry` and `BuildRow(..., stored: expected, ..., PlaceholderIssue)`; embeds the base's text when `ApplyBaseJson` is set and that text differs from the expected entry's (an embedder failure is `Failed`); the no-op shortcut also needs `pinPlan.IsEmpty`.
  - `CarryOut`: the reread step also defers when the re-read pin items differ; the `pins` step recomputes `Plan` from the stored entry and `next ?? row` and returns `Deferred("the pin review changed since the decision was made")` (no commit) when `!Matches`; else `ApplyBaseContent` + `GitSyncRows.Write(WithComparison(next ?? row, stored))` when `ApplyBaseJson` is set, then `Write`. `ApplyDrop` deletes both pin kinds at step `pins`. The pull adds `ReadPinItems(conn)` ids to the visit set (read in the same `try`).

- [ ] **Step 1: Write the failing tests** (each also asserts `Deferred` empty where a pull runs): `ExpectedEntry_EqualsTheStoredEntry_ForEveryPlanShape` (insert, insert colder, take-theirs, adopt, tags, unpin, move, delete, with project `""`); `AnEmptyProjectInAPinnedRecord_IsCarriedOut_AndARepeatedPullIsANoOp` (Review Focus 1); `ARepeatedPullOfTheSameRecord_IsANoOp_AndOpensNoTransaction` (`TransactionCount` and `PinItems` unchanged, embedder calls unchanged); `TheRecordChangesWhileANewPinWaits_MarksItUnseen_WithTheNewHash`; `ARemoteUnpin_RemovesTheItems_AndUnpinsAnEntryPinnedHere` (new-pin rule 3 and pin-edit via rule 2 when FS3 + FC3 also take the content); `TheEntryDeletedHereOrForgotten_RemovesTheItem` (theory); `PinningHere_RemovesTheNewPinItem`; `ARegressionToNonSticky_WhileTheBaseKeepsThePin_LeavesTheItem`; `AHeldEditWhoseTextComesBackEqual_ClosesTheItem_EvenWithAnotherSourceOrCreatedAt`; `UnpinningHereWhileAnEditIsHeld_AppliesTheHeldContent_AndRemovesTheItem` (text, project, scope, one embedder call for the base text, `ScannedContentHash` = `contentHash(stored)`, no content stamp, not sticky; a 300-character base text with hot cap 100 lands warm); `ALocalTextOrProjectEditWhileHeld_RemovesTheItem_AndKeepsTheLocalContent` (theory: text, project only); `UnpinAndEditTogether_TheEditStands`; `ADeclinedPinEdit_IsRemovedByTheNextPull`; `ApplyDrop_RemovesThePinItems`; `TheVisitSet_ContainsEveryPinItemId_InEachOfTheFourStates` (`PullRig`, proven pull with no change: an item whose entry was pinned locally is removed for each state; `unseen` treated as open); `APinEditItem_WithATierRestamp_IsCarriedOut_NotFailedOrDeferred` (Review Focus 3; and an NS2 case); `AnItemChangedBeforeTheTransaction_DefersTheEntry` (`BeforeTransaction` calls `MarkPinUnseen`: `Deferred`, nothing else written); `AnUnchangedItem_IsNotRewrittenByANoOpPull`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplier"`. Expected: FAIL on the new tests.
- [ ] **Step 3: Implement** the signatures above. If an existing test pins a `TransactionCount` or `Unchanged` count the visit set changes, adjust only that number and name it.
- [ ] **Step 4: Run** the step 2 filter, then the whole dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Reconcile pin review items in the pull and apply a held edit on unpin"`.

---

### Task 5: `approve` and `decline` in the review service (D6, D7, D8 list, D9.2)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/ReviewService.Pins.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/ReviewServicePinTests.cs`
- Modify: `ReviewService.cs` (generic frame, shared row/entry check), `ReviewService.List.cs`, `ReviewServiceTests.cs` (lines 60-61: the new `ReviewItem` shape, values unchanged)

**Interfaces:**
- Consumes: Tasks 1, 2, 4 (`ApplyBaseContent`, `WithComparison`).
- Produces:
  - `ReviewOperation` gains `Approve, Decline`; `internal readonly record struct CheckOutcome<TFacts>(SetupResult? Refusal, TFacts? Facts, bool CommitRefusal = false) where TFacts : class`; `RunAsync<TFacts>(string entryId, SetupResult noItem, Func<SqliteStoreTransaction, CheckOutcome<TFacts>> check, Func<SqliteStore, Embedded?>? embed, Func<SqliteStoreTransaction, TFacts, Embedded?, SetupResult> work, CancellationToken ct)`: as today, plus a refusal is committed only when `CommitRefusal`. The held answers and `CheckForTestsAsync` pass `NoHeldItem` and the existing `CheckItem`, which calls the extracted `ReadRowAndEntry(tx, entryId, out GitSyncRow? row, out LocalRead? read) : SetupResult?` (row `active` with a base: `failed`; duplicate or unreadable: `unreadable-entry`); held behaviour and tests unchanged.
  - `internal sealed record PinFacts(PinItem Item, GitSyncRow Row, LocalRead.Present Entry, ParsedRecord Base, string LiveHash)`; `public const string NoPinItemText = "no open pin review of this kind for this id; nothing changed"`.
  - `public Task<SetupResult> ApproveAsync(string entryId, string kind, string? shownHash, CancellationToken ct = default)` and `DeclineAsync(...)`. `CheckPin` order: item `open`/`unseen` of `kind` (`no-pin-item`, also for an unknown kind); `ReadRowAndEntry` and the base parses (`failed`); hash present (`shown-hash-required`); entry present and sticky state fits the kind (`stale-item`, nothing written); pin-edit: `row.ContentStamp != null || SyncScan.contentHash(entry) != row.ScannedContentHash` (`edited-locally`); `shownHash != LiveHash` (`stale-item` "... now: unseen <kind> <live hash>", `CommitRefusal` after `MarkPinUnseen(live hash, base WrittenBy)`); approve only: `!Showable(text, project, scope)` (`unshowable-text`), then `text.Length > cap` with `cap <= 0` → 500 (`over-pin-cap`, `PinnedTierLimits.ContentLimitMessage(cap, length)`).
  - Approve new-pin: `Move` to hot when not hot (vector copied), `SetSticky(true)`, `Update(Hot, ..., new UpdateEntryOpts { DecayScore = 1.0 })`, row `with { LocalSticky = true, LocalTier = Hot, StickyStamp = null }`, `BeforeStep?.Invoke("resolve")`, `ResolvePinItem(approved)`. Approve pin-edit: embedding of the base text before the transaction (`embed` as `EmbedBase` does, only for pin-edit approve), inside it `base_json` must equal `Embedded.BaseJson` (else `stale-item`), `ApplyBaseContent`, `WithComparison`, resolve. Decline new-pin: resolve `declined` only. Decline pin-edit: `ApplyPlanning.IssueStamp(conn, tx, NowMs(), Some(base.ContentStamp))` (null: `failed`, `NoStampText`) as the pending `ContentStamp`, resolve `declined`, and the line `this replaces that change in the repository and on your other machines where the memory is not pinned; where it is pinned it is held there as an edit`.
  - `public sealed record ReviewItem(string Kind, string EntryId, string State, string? Direction, string? Cause, string? ShownHash, long ArrivedAt, string? Machine, string? Project, string? Scope, string? Text, bool TextCut, string TextSource, string? CurrentText, bool CurrentTextCut)`; `List()` adds open and unseen pin items (ordinal by id, then kind; held deletions keep `Machine`/`Project`/`Scope`/`CurrentText` null); `internal static string? ShownField(string? value)` (the text rules, then `\n` and `\t` each become a space, cut at 200 surrogate-safe) for machine, project and scope; `internal static bool Showable(string text, string? project, string? scope)` (`ShownText(text, int.MaxValue).Text == text` and `ShownField` leaves project and scope equal); the shown hash computed live from the raw fields (null when unreadable).

- [ ] **Step 1: Write the failing tests** (`ReviewServicePinTests`, items made by `ApplyRig` pulls; `ReviewServices.For(..., hotMaxContentChars, pinnedMaxContentChars)`): `ApprovingANewPin_PinsIt_InHot_WithDecayOne_AndRowLocalStickyAndNoStamp`; `ApprovingAWarmNewPinOverTheHotCap_PinsItInHot_AndNothingUndoesIt` (Review Focus 5); `OverThePinnedCap_IsOverPinCap_WithTheCapInTheMessage_AndTheItemOpen` (theory: cap 50; cap 0 → 500 with a 501-character text); `AStaleHash_IsStaleItem_AndOnlyTheItemIsWritten` (Review Focus 4; theory over the record's text changed and the entry's text changed; both kinds); `NoHash_IsShownHashRequired` (both verbs); `AnEntryAlreadyPinned_IsStaleItem_ForANewPin` and `AnUnpinnedEntry_IsStaleItem_ForAPinEdit` (nothing written); `TextWithACharacterTheListRemoves_IsUnshowableText`; `HostileProjectScopeAndMachine_AreSanitisedInTheList_AndApproveRefusesUnshowableText` (Review Focus 2); `DecliningANewPin_ChangesOnlyTheItem`; `ApprovingAPinEdit_TakesTheHeldContent_ReEmbedded_StillSticky_NoStamp`; `APinEditOverTheCap_IsOverPinCap`; `DecliningAPinEdit_LeavesAContentStampAboveTheBase_AndCarriesTheWarning`; `APinEditAfterALocalTextOrProjectEdit_IsEditedLocally_AndNothingIsWritten` (theory); `AnUnseenItem_IsAnswerable_AndListed`; `List_ShowsANewPinAndAPinEdit_WithKindSourcesCurrentTextMachineAndLiveHash`; `AHeldSyncLock_IsBusy`; `AFaultAtTheLastWrite_RollsTheAnswerBack` (`BeforeStep` throws at `resolve`: dump unchanged); `AnIdWithAHeldDeletionAndAPinItem_EachVerbAnswersItsOwnKind` (accept refuses nothing about the pin; approve on a held-only id is `no-pin-item`).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ReviewService"`. Expected: FAIL.
- [ ] **Step 3: Implement** the members above.
- [ ] **Step 4: Run** the step 2 filter, then the whole dev loop. Expected: PASS; every Part 2 `ReviewService*` test green with values unchanged.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Approve and decline pin review items"` (name the `ReviewServiceTests` list test changed for the new `ReviewItem` shape).

---

### Task 6: The hidden `approve` and `decline` verbs (D8)

**Files:**
- Modify: `src/TotalRecall.Cli/Commands/Sync/ReviewCommand.cs` (usage, `Description`, list header, `approve`/`decline`, bulk and `accept`/`restore` filter by kind)
- Test: `tests/TotalRecall.Cli.Tests/Commands/Sync/SyncCommandTests.cs` (the seeding helper writes pin rows by plain SQL; the four held-delete header asserts at lines 373, 378, 493, 623 gain the kind; `Review_List_WithNoHeldDeletions_SaysSo` expects `nothing to review`)

**Interfaces:**
- Consumes: Task 5 (`ApproveAsync`, `DeclineAsync`, `ReviewItem`, `NoPinItemText`).
- Produces: usage `Usage: total-recall sync review list | accept <id> [--shown <hash>] | restore <id> [--shown <hash>] | approve <id> --shown <hash> | decline <id> --shown <hash> | accept-deletions --all | restore-deletions --all`. Headers: `<id>  held-delete  <state>  <direction> (<where>)  cause <c|->  shown <h|->`; `<id>  <kind>  <state>  from <machine|->  project <p|->  scope <s|->  shown <h|->`, then the text block (`Text` with its source) and, for a `pin-edit`, a second block for `CurrentText` from the `local entry`. `approve|decline <id> --shown <h>`: no `--shown` is a usage error (`--shown is required`, exit 2); the listing failing prints `refused: failed: <reason>`, exit 1; the id's pin items: none → `refused: no-pin-item: <NoPinItemText>`, two → `refused: stale-item: this memory has two pin reviews; run a sync first`, exit 1, service not called; one → its full hash when `<h>` prefixes it, else `<h>`; `decline` of a `pin-edit` prints `warning: <the D7 sentence>` before the call.

- [ ] **Step 1: Write the failing tests:** `Review_List_ShowsANewPinAndAPinEdit_WithKindMachineAndBothTexts`; `Review_List_AHostileMachine_StaysOnTheHeaderLine`; `Review_Approve_WithAnEightDigitPrefix_IsApproved`; `Review_Approve_WithoutShown_IsAUsageError`; `Review_Decline_WithAShortPrefix_IsAUsageError`; `Review_Approve_WithAWrongHash_IsStaleItem_ExitOne`; `Review_Approve_OfAnIdWithoutAPinItem_IsNoPinItem`; `Review_DeclineOfAPinEdit_PrintsTheWarningFirst`; `Review_BulkForms_IgnorePinItems`; `Review_Accept_OfAnIdWithAPinItemToo_AnswersTheHeldDeletion`; usage rows `review approve`, `review approve a`, `review decline a --shown zzzzzzzz`.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "FullyQualifiedName~Sync|FullyQualifiedName~HiddenCommand"`. Expected: FAIL on the new and changed tests; `HiddenCommandTests` green and unchanged.
- [ ] **Step 3: Implement** the command changes.
- [ ] **Step 4: Run** the step 2 filter, then the whole dev loop. Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the hidden sync review approve and decline verbs"` (name the changed header tests).

---

### Task 7: Two-machine proofs and the guides

**Files:**
- Create: `tests/TotalRecall.Infrastructure.Tests/GitSync/PinReviewCycleTests.cs` (`[GitFact]`, `TwoMachineRig`; service from `ReviewServices.For(rig.B.Pull.DbPath, ...)`)
- Modify: `AGENTS.md` (the review-service and pull rows, lines 93-95: pin items, `approve`/`decline`); `src/TotalRecall.Infrastructure/AGENTS.md` (pull paragraph: "Only a decision that opens a review item (a pin, a pin edit, a conflict ...) is postponed" becomes conflicts only; the `GitSyncReviews` sentence that it "reads and changes only `held-delete` rows ... `UpsertNewPin` only" names the pin members; new paragraphs: pin items and states, the arrival and reconcile rules with the pre-transaction plan, D5, the answers); `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` (`### 1b-iii part 3. Pin review (#163)` after part 2, with the ticket's criteria)

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Write the tests:** `APinFromA_IsAnOrdinaryHotEntryOnB_WithAnOpenItem_AndBsCycleIsComplete` (B's `Last` = remote head); `ApprovingANewPinOnB_PinsIt_AndBsNextCyclePushesNothing` (remote head unchanged); `DecliningANewPinOnB_DoesNotUnpinA` (B pushes nothing; A's record still sticky after A's cycle); `PinningOnBWhileANewPinWaits_ClosesTheItem_AndPushesThePinAsAnOrdinaryChange`; `ATagEditOnBWhileAnEditIsHeld_PushesOnlyTheTags` (the record keeps A's text, gains B's tag); `ApprovingAHeldEditOnB_PushesNothing`; `DecliningAHeldEditOnB_PushesBsText_AndAThenHoldsIt` (the memory is pinned on B only: A's record and A's entry end with B's text); `ADeclinedEditWithAToken_IsHeldBackByThePushsSecretCheck` (`FakeSecrets.Get(...)` in B's text); `UnpinningOnBWhileAnEditIsHeld_AppliesIt_AndBsNextCyclePushesOnlyTheUnpin`; `ALocalTextOrProjectEditOnBWhileHeld_PushesBsContentGroup` (theory); `ANewPinWithAConflict_StillMakesBsCycleIncomplete`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PinReviewCycle"`. Expected: PASS (Tasks 3 to 5 built the behaviour); to see each can fail, temporarily skip its service call or local edit and watch it go red, then restore it. A real failure is fixed in the owning task's code, never by weakening the test.
- [ ] **Step 3: Update the guides** as listed.
- [ ] **Step 4: Verify everything:** the release build; the three dev-loop commands; `--filter "FullyQualifiedName~GitSyncCycleCallerTests|FullyQualifiedName~TwoMachine"`; the profile verify commands of `.github/develop.md` (`npm ci --ignore-scripts` and `sh scripts/fetch-bge-small.sh` if the worktree lacks them, then `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`, `npm run test:shim`, and the catalog drift guard `dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q` then `dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -`); `git diff --quiet origin/main -- tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncCycleCallerTests.cs`; the spec guard from Global Constraints. Expected: all pass, no new warning, no diff.
- [ ] **Step 5: Commit** `git add tests AGENTS.md src/TotalRecall.Infrastructure/AGENTS.md docs && git commit -m "Prove pin review across two machines and document it"`.
