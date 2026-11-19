# Git sync deletions (milestone 1b-ii) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the git sync cycle carry out deletions in both directions with proof: apply a proven remote deletion, hold what cannot be proven or is too large, push local deletions, write back a record a text edit saved, and repair a reset repository from the last agreed state.

**Architecture:** The merge decisions already exist in `Core/SyncMerge.fs` and are complete. The applier previews every deletion candidate (counts per direction, trips the delete guard), then decides each entry with that verdict and carries it out in the entry's existing immediate transaction. It hands the pusher an explicit list of local deletions in `ApplySummary.PushDeletions`; the pusher only executes (removes files, writes back from `base_json`, repushes reverted groups, skips held entries). Held items live in the existing `git_sync_review` table (kind `held-delete`); no schema change.

**Tech Stack:** .NET 10, C# (Infrastructure) and F# (Core), xUnit, SQLite, a bare git repository in tests.

**Spec:** `docs/superpowers/specs/2026-11-19-git-sync-deletions-design.md` (read it first; its decision numbers are quoted below as "D<n>"). Companion designs: `2026-11-17-git-sync-{pull,push,scan}-design.md`, `2026-11-18-git-sync-cycle-design.md`, umbrella `2026-11-15-git-sync-design.md`.

## Global Constraints

- The approved spec is read-only for the whole run (`git diff --quiet bdd846d47528e768cb499daf7ecefccb78cc0bd2 HEAD -- docs/superpowers/specs/2026-11-19-git-sync-deletions-design.md` must exit 0 before every push).
- No schema change, no new migration. `git_sync_review` and `repush_groups` already exist.
- Nothing outside `GitSyncCycle*`, `GitSyncPusher*`, `GitSyncApplier*` and their tests may name the cycle, the pusher or the applier; `GitSyncCycleCallerTests` stays unchanged and green.
- No `--force`, `-f` or `--force-with-lease` on any `git push` in the suite or the code. No AI attribution or co-author trailer in any commit message. Never commit `TestResults` hang dumps.
- Guard limit: `max(10, N * delete_guard_percent / 100)` with integer division, `N` = number of `active` rows at the start of the pull; a direction trips when its count is **greater than** the limit.
- Hold causes are exactly `guard`, `unproven`, `sticky`; directions are exactly `remote` and `local`; `payload_json` is `{"cause":"<cause>"}`; a held item's `state` is `open`; `arrived_at` is the applier clock in milliseconds.
- `repush_groups` text is the group names in the order `content,sticky,tier` joined by `,`, NULL when none.
- Commit message of a push: `sync: <machine> +<added> ~<changed> -<removed>`; a write-back counts as `+`, a repush or an edit as `~`.
- Dev loop (each task): `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.csproj --filter "FullyQualifiedName~Sync"` and `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync"` (the worktree symlinks `node_modules` and `models/`). Run the whole `Category!=Integration` suite only in Task 10.
- Where a task changes behaviour an existing test pins ("postponed"), change that test in the same task to the new behaviour and say so in the commit message.

## Review Focus

1. A decision that closes an open item and opens one of the other direction in one transaction (an open `remote` item, the entry deleted here, the record still in R, unproven or guard-converted): exactly one open item must remain, of the new direction (Task 3, 4).
2. The guard boundary and tiny stores: 9, 10 and 11 deletions; zero active rows (limit 10); 100 rows at 20 % (limit 20, 21 trips); 0 % and 100 % (Task 1, 5).
3. A kill between the push command and the finalize: no duplicate commit, the row repaired to `forgotten` by the next apply, and a finalize that refuses to mark `forgotten` an entry that exists again (Task 7).
4. A record that is rejected or accepted-but-unparsable for an id that is also a local deletion, or a no-base row over such a record: never decided, never counted, never removed, never overwritten, and `L` is not held (Task 4, 8).
5. A reset repository where a group's stamp is older than the base's on a group with a pending change, a declined pin or a local demotion: only flagged groups with no pending change are repushed, from the base's value and stamp, and nothing local-only enters the record (Task 6, 9).

---

## File map

| File | Change |
|---|---|
| `src/TotalRecall.Core/SyncDeletion.fs` (new) + fsproj | guard limit and verdict, `holdInstead`, `holdCause`, `RepushGroups` codec |
| `src/TotalRecall.Core/SyncApply.fs` | `postponement` review-only (in stages), `VisitInput.RepushRows`, `RowBefore.BaseContentStamp`, `RowAfter.Repush`, `planRow` for `FreshContentStamp`, `Forgotten`, marker |
| `src/TotalRecall.Core/SyncPush.fs` | `isCandidate` and `RowFacts` marker, `Exclusion` (`HeldDelete`, narrowed `RecordMissing`), `commitMessage` removed count, `stagedSetMatches` with deletions, repush helper |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs` (new) | `held-delete` rows with the caller's transaction |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncApplier*.cs` | `ApplyOptions.DeleteGuardPercent`, summary fields, preview, carry-outs |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncPusher*.cs` | removals, write-back, held exclusion, repush, finalize `forgotten` |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncCycle*.cs` | `CycleSettings.DeleteGuardPercent` |
| tests under `tests/TotalRecall.Core.Tests` and `tests/TotalRecall.Infrastructure.Tests/GitSync` | per task |
| `AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` | Task 10 |

---

### Task 1: Core `SyncDeletion` (pure rules)

**Files:**
- Create: `src/TotalRecall.Core/SyncDeletion.fs` (add to `TotalRecall.Core.fsproj` after `SyncMerge.fs`), `tests/TotalRecall.Core.Tests/SyncDeletionTests.fs` (add to the tests fsproj after `SyncMergeTests.fs`)

**Interfaces:**
- Produces (module `TotalRecall.Core.SyncDeletion`):
  - `guardLimit (percent: int) (active: int) : int` = `max 10 (active * (clamp percent 0 100) / 100)` (int arithmetic; negative `active` counts as 0)
  - `guardTrips (count: int) (limit: int) : bool` = `count > limit`
  - `holdInstead (d: SyncMerge.MergeDecision) : SyncMerge.MergeDecision`: `DeleteEntry` becomes `HoldDeletion RemoteDeletion`, `PushDeletion` becomes `HoldDeletion LocalDeletion`; for those two, `Notes = []` and `RowState = None`; every other entry outcome returns `d` unchanged
  - `directionName (d: SyncMerge.Direction) : string` = `"remote"` | `"local"`
    - `holdCause (proven: bool) (converted: bool) (direction: SyncMerge.Direction) : string` (the spec's signature): `"guard"` when converted; else `"unproven"` when not proven; else `"sticky"` for `RemoteDeletion` (a proven remote hold is a pin) and `"unproven"` for `LocalDeletion` (defensive: `decide` holds a local deletion only when unproven)
  - `module RepushGroups`: `encode (f: SyncMerge.GroupFlags) : string option`, `decode (text: string) : SyncMerge.GroupFlags` (null, blank and any text that is not exactly a comma list of distinct names from `content|sticky|tier` in the fixed order decode to all-false)

- [ ] **Step 1: Write the failing tests** in `SyncDeletionTests.fs`. Names and assertions: `guardLimit_is_ten_for_small_stores` (`guardLimit 20 0 = 10`, `guardLimit 20 49 = 10`, `guardLimit 20 50 = 10`, `guardLimit 20 100 = 20`, `guardLimit 20 109 = 21`), `guardLimit_clamps_percent` (`guardLimit -5 1000 = 10`, `guardLimit 0 1000 = 10`, `guardLimit 100 1000 = 1000`, `guardLimit 250 1000 = 1000`), `guardTrips_is_strictly_greater` (`guardTrips 10 10 = false`, `guardTrips 11 10 = true`, `guardTrips 0 10 = false`), `holdInstead_converts_delete_and_push` (a `DeleteEntry` decision with `RowState = Some Forgotten` and a `PendingChangesDropped` note becomes `HoldDeletion RemoteDeletion` with `RowState = None` and `Notes = []`; a `PushDeletion` decision with a `Base` update keeps that `Base` and becomes `HoldDeletion LocalDeletion`), `holdInstead_leaves_everything_else` (property over all other `EntryOutcome` cases: unchanged), `holdCause_table` (all eight input combinations), `repushGroups_roundtrip` (all 8 flag combinations round-trip; all-false encodes to `None`), `repushGroups_decode_rejects_other_text` (`"sticky,content"`, `"content,content"`, `"Content"`, `"tier "`, `""`, null all decode to all-false).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.csproj --filter "FullyQualifiedName~SyncDeletion"`. Expected: build failure or FAIL (module undefined).
- [ ] **Step 3: Implement** the module above, pure and total, in the style of `SyncApply.fs` (header comment naming the milestone and the spec decisions it serves).
- [ ] **Step 4: Run** the same command and then the whole `FullyQualifiedName~Sync` set. Expected: PASS.
- [ ] **Step 5: Commit** `Add the pure deletion rules: guard limit, hold conversion, repush marker codec`.

---

### Task 2: Held-delete rows, guard percent in the options, summary fields

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/GitSyncReviews.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncReviewsTests.cs`, `tests/TotalRecall.Infrastructure.Tests/GitSync/TwoMachineRig.cs`, `TwoMachineTests.cs`
- Modify: `GitSyncApplier.cs` (`ApplyOptions`, `ApplySummary`), `GitSyncCycle.cs` (`CycleSettings`) and the cycle file that builds `ApplyOptions`; every test construction of `ApplyOptions`, `ApplySummary` and `CycleSettings` (add the new members; defaults keep existing call sites compiling where a default is possible)

**Interfaces:**
- Produces:
  - `internal sealed record HeldItem(string EntryId, string Direction, string? RecordHash, string? Cause, long ArrivedAt)`
  - `internal static class GitSyncReviews`: `Dictionary<string, HeldItem> ReadOpenHeld(SqliteConnection conn, SqliteTransaction? tx = null)` (kind `held-delete`, state `open`, ordinal keys; `cause` parsed from `payload_json`, NULL when absent or unparsable), `void UpsertHeld(SqliteConnection conn, HeldItem item, SqliteTransaction? tx = null)` (state `open`, `payload_json` = `{"cause":"<cause>"}` or NULL, replaces any row with the same `(entry_id, 'held-delete')`), `void DeleteHeld(SqliteConnection conn, string entryId, SqliteTransaction? tx = null)` (no-op when absent). Other kinds in the table are never touched.
  - `ApplyOptions(int HotMaxContentChars, int DeleteGuardPercent = 20)`; `CycleSettings` gains `int DeleteGuardPercent = 20` (and passes it on).
  - Records `HeldEntry(string Id, string Direction, string Cause)`, `GuardTrip(string Direction, int Count, int Limit)`, `NoteEntry(string Id, string Note)`, `RepushEntry(string Id, string Groups)`, and `ApplySummary` gains, appended after `ColderTier`, in this order: `IReadOnlyList<string> Deleted`, `IReadOnlyList<HeldEntry> Held`, `IReadOnlyList<GuardTrip> GuardTripped`, `IReadOnlyList<string> PushDeletions`, `IReadOnlyList<string> ClosedHeld`, `IReadOnlyList<NoteEntry> Notes`, `IReadOnlyList<RepushEntry> RepushMarked`. The applier fills them with empty lists in this task.

- [ ] **Step 0: Build the two-machine test rig** (test code only; Tasks 7 to 10 need it, no existing rig has two stores). Changes: `PushRig(bool configuredRemote = false, BareRemote? sharedRemote = null)` uses `sharedRemote` instead of `BareRemote.Create(commits: 0)` when given; `PullRig.Join(string repoId = RepoId, string? host = null, string machineId = MachineId)` stores `machineId`; `CycleRig(BareRemote? sharedRemote = null)` passes it on. New `internal sealed class TwoMachineRig : IDisposable` with `CycleRig A` (seeds the remote: manifest plus its own records through `Push.Seed`) and `CycleRig B` built on `A.Push.Remote` with machine id `7b3d5e90`, `B.Join()` and a clone of the shared remote at `B.Pull.Paths.CloneDirectory` (`JoinB()` does it, after A's first push), and `Task RunBoth()` helpers `RunA()`, `RunB()`. The two machines have separate temp roots, databases, clocks (`Push.Now` independent) and recorders. Smoke test in `TwoMachineTests.cs`, which passes when written because it exercises existing behaviour: `AMemoryAddedOnA_ReachesB_AfterTwoCycles`.
- [ ] **Step 1: Write the failing tests** in `GitSyncReviewsTests.cs` (open the database with `SyncDb.OpenMigrated` as `GitSyncIssuesTests` does): `Upsert_then_read_returns_the_open_item`, `Upsert_replaces_an_existing_item_of_the_other_direction`, `Delete_removes_only_held_delete_rows` (a `new-pin` row with the same entry id survives), `Rows_follow_the_callers_transaction` (rollback leaves nothing), `Unparsable_payload_reads_as_no_cause`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncReviews"`. Expected: FAIL (type missing).
- [ ] **Step 3: Implement** `GitSyncReviews` (plain SQL, same style as `GitSyncIssues`), the option and summary members, and the plumbing in the cycle (`CycleSettings.DeleteGuardPercent` into `ApplyOptions`). Fix every compile error in existing tests by passing the defaults.
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~GitSync"`. Expected: PASS, no behaviour change (the smoke test of Step 0 passes too).
- [ ] **Step 5: Commit** `Add held-delete rows, the guard percent option and the new summary fields`.

---

### Task 3: Applier, remote deletions (apply, hold, close, edit beats deletion)

**Files:**
- Modify: `src/TotalRecall.Core/SyncApply.fs` (`postponement`, `RowBefore`, `RowAfter`, `planRow`), `GitSyncApplier.Entry.cs`, `GitSyncApplier.Pull.cs`, `GitSyncApplier.cs`
- Test: `tests/TotalRecall.Core.Tests/SyncApplyTests.fs`, `GitSyncApplierEntryTests.cs`, `GitSyncApplierPullTests.cs`, `GitSyncApplierSafetyTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SyncDeletion.holdCause`, `directionName`), Task 2 (`GitSyncReviews`, summary records).
- Produces:
  - `SyncApply.postponement` now returns `Some Deletion` only when the entry outcome is `PushDeletion` or `HoldDeletion LocalDeletion` (Task 4 removes that), `Some Review` when the decision has a review request, `Some Repush` for a repush flag (Task 6 removes that). The notes, `FreshContentStamp`, `CloseHeldDelete` and `RowState = Some Forgotten` no longer postpone.
  - `SyncApply.RowBefore` gains `BaseContentStamp: Stamp option` (the content stamp of the row's base record). `planRow`: for `LeaveEntry` with `FreshContentStamp = true` the pending content stamp becomes `issue (floorStamp (greater of the row's pending content stamp and BaseContentStamp))`, other pending stamps unchanged; `DeleteEntry` with `RowState = Some Forgotten` clears the pending stamps and `pushing_stamps` (as `clearsPending` already says).
  - A shared private function in the applier that builds the `MergeInput` for an id (row, base, theirs, local, pending, pushing, proven, **`HeldDelete` from the open held item of the id**), used by `ApplyCore` now and by the preview in Task 5.
  - In the entry transaction (`CarryOut`): `DeleteEntry` deletes the content row and vector through `SqliteStoreTransaction.Delete(currentTier, Memory, id)`; the row is written `forgotten` (base kept); `HoldDeletion RemoteDeletion` upserts the held item (`direction remote`, `record_hash` NULL, cause from `holdCause <ApplyProven> false RemoteDeletion`, `arrived_at` = applier clock); `CloseHeldDelete` removes the held item **before** any upsert of the same transaction; `FreshContentStamp` issues the stamp through the existing `IssueStamp` at the existing `row` step. `ApplySummary.Deleted`, `Held`, `ClosedHeld` and `Notes` (`edit-beats-deletion`, `pending-changes-dropped`) are filled.
    - The "decision changes nothing, so open no transaction" shortcut must not short-circuit a decision that writes or closes a held item or deletes an entry. `StorePlan` gains a delete flag that counts as a store operation and is excluded from the "read back after write" check (the entry is gone afterwards). `DeleteEntry`, `HoldDeletion` and `CloseHeldDelete` are reported as `CarriedKind.Updated` (counted in `Updated`) and **also** through the new summary lists; they are never `Inserted` or `Unchanged`.

- [ ] **Step 1: Write the failing tests.** Core (`SyncApplyTests.fs`): update `postponementTests` to the staged behaviour; `planRow_fresh_content_stamp_issues_above_pending_and_base` (a stub `issue` that records its floor: the floor is the greater of the two stamps); `planRow_delete_entry_forgotten_clears_pending`. Applier (`GitSyncApplierEntryTests.cs`, using `ApplyRig` and `PullTestSupport`; replace the four tests that pinned `Postponed("deletion")` for these outcomes with):
  - `ProvenRemoteDeletion_DeletesTheEntryAndItsVector_AndMarksTheRowForgottenKeepingTheBase` (row active with base, record absent in R, proven, not sticky, no pending text; assert content row gone, vector row gone, row state forgotten, `BaseJson` unchanged, pending stamps null, `Summary.Deleted == [id]`, `Complete`)
  - `RemoteDeletion_WithPendingTagTierProjectChange_StillDeletes_AndNotesTheDroppedChanges` (note `pending-changes-dropped`)
  - `RemoteDeletion_OfAStickyEntry_IsHeldWithCauseSticky` and `..._WithAPendingStickyStamp_...` (entry stays, one open item `remote`, cause `sticky`, `record_hash` NULL)
  - `RemoteDeletion_InAnUnprovenApply_IsHeldWithCauseUnproven_AndNothingIsDeleted`
  - `RemoteDeletion_WithAPendingTextEdit_KeepsTheEntry_IssuesAFreshContentStamp_AndNotesEditBeatsDeletion` (pending content stamp strictly greater than both the old pending stamp and the base's content stamp; open item closed; entry untouched)
  - `RecordReappearing_ClosesTheOpenRemoteItem` and `PendingTextEdit_ClosesTheOpenRemoteItem`; `PendingTagChange_DoesNotCloseTheOpenItem`
  - `OpenRemoteItem_LeavesTheEntryAlone_NeitherDeletedNorRehold` (W6.3: no new item, `LeaveEntry`, not counted)
  - `NewerTextThanTheDeletion_ReinsertsNonSticky_ClosesTheItem_AndNotes` (row forgotten or active-with-base entry gone, record text newer; a record that pins stays postponed as `review`)
  - `ForgottenRow_WithTheSameTextRecord_IsNotReinserted`; `ForgottenRow_WithOlderTextAfterAReset_IsNotReinserted`
  - `RowActiveWithBase_RecordAbsent_EntryGone_BecomesForgotten` (W10)
  - `HeldAndDeletionDecisions_FaultAtEveryStep_RollBackEverything` (the existing step-fault pattern, run for a delete, a hold, a close and a `FreshContentStamp` decision: for each step name the transaction reports, a fault leaves content row, vector row, entry row, held item, `apply-failed` issue and the persisted clock exactly as before)
  - `OpenItemClosedAndNewItemOpened_InOneTransaction_LeavesOneItem` is Task 4's (the local hold); here only the close is tested.
    Pull level (`GitSyncApplierPullTests.cs`): rewrite `ARemoteDeletion_IsPostponed_LStays_AndTheMemorySurvives` into `ARemoteDeletion_IsApplied_AndLAdvances`. **Other tests that go red in this task and are updated in it:** `GitSyncCyclePullTests.cs` `AnIncompletePull_IsDone_WithThePushSkipped…` and `GitSyncCyclePushTests.cs` `ThePushIsNeverReached_AfterARefusedOrAnIncompletePull` use `RemoveRecordOnRemote` to make a pull incomplete; a remote deletion is applied now, so they use a record that pins (a sticky record for an id this machine does not have, which is a `review` postponement) as the cause of incompleteness instead, and assert the same outcomes as before.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SyncApply|FullyQualifiedName~GitSyncApplier"`. Expected: FAIL on the new tests.
- [ ] **Step 3: Implement.** Pass the open held item's direction as `HeldDelete` (read all open items once per pull next to the rows and issues, as `ReadOpen` is; re-read the item inside the entry transaction at the existing `reread` step and defer the entry when it changed, as the row is). Carry out each outcome in `CarryOut`; filling `ApplySummary` lists from `EntryResult` (extend `EntryResult.Carried` with what the entry did: deleted, held direction and cause, closed, notes). A decision with `Notes` or `FreshContentStamp` and no store operation still writes its row.
- [ ] **Step 4: Run** the Step 2 command, then `--filter "FullyQualifiedName~GitSync"` in the Infrastructure tests. Expected: PASS.
- [ ] **Step 5: Commit** `Apply remote deletions: delete with proof, hold pins and unproven ones, edits beat deletions` (message states which pull tests changed).

---

### Task 4: Applier, local deletions (push list, hold, skipped invalid records)

**Files:** `SyncApply.fs`, `GitSyncApplier.Entry.cs`, `GitSyncApplier.Pull.cs`; tests as in Task 3.

**Interfaces:**
- Consumes: Tasks 1 to 3.
- Produces:
  - `postponement` returns `Some Deletion` for nothing any more; remaining reasons are `Review` and (until Task 6) `Repush`.
    - `PushDeletion` decisions are carried out as: row base follows the decision, pending stamps cleared (`clearsPending`), no store write; the id is appended to `ApplySummary.PushDeletions`; the row stays `active`. The no-op shortcut of `ApplyCore` (`Equals(preview, row)`) must not swallow a `PushDeletion` whose row is already unchanged: the id is reported regardless.
  - `HoldDeletion LocalDeletion` upserts a held item (`direction local`, `record_hash` = the blob id of the record in R, cause from `holdCause`: `decide` holds a local deletion only in an unproven apply, so `unproven`; `guard` arrives with Task 5), clears the pending stamps (`clearsPending`) and keeps the entry absent.
  - In the pull loop, an id with a rejected path, or with an accepted path whose blob does not parse, that is also in `scan.LocalDeletions` is **not postponed**: it is added to `ApplySummary.Skipped` with the reason `the record is rejected` or `the record is invalid`, nothing is written for it, `L` is not held back.
  - `ApplyOne` takes/forwards the record's blob sha so the held item can store it (the loop already passes it).

- [ ] **Step 1: Write the failing tests** (`GitSyncApplierEntryTests.cs`, `GitSyncApplierPullTests.cs`):
  - `ProvenLocalDeletion_WithTheRecordUnchanged_IsListedInPushDeletions_AndChangesNoStoreRow` (row stays active, pending stamps cleared, base per W8.3)
  - `OwnPushWithNoBase_AndTheRecordInR_IsAPushDeletion` (W9 first branch) and `NoBaseRecordPresentNotOwnPush_BecomesForgottenWithTheBaseTheRecord` (W9 second)
  - `LocalDeletion_InAnUnprovenApply_IsHeldLocalWithCauseUnproven_AndNotListedForPush`
  - `LocalDeletion_WhenTheRecordTextIsNewer_ReinsertsTheEntry` (W8.1)
  - `OpenLocalItem_LeavesTheDeletionHeld` (W8.2) and `OpenRemoteItem_ThenEntryDeletedHere_ReplacesItWithOneLocalItem` (the review-focus case 1: one item remains, direction `local`)
  - `ADeletedIdWithARejectedPath_IsSkippedNotPostponed_AndLIsNotHeld` and `ADeletedIdWithAnAcceptedButUnparsableRecord_IsSkippedNotPostponed`
  - `PushDeletions_AreOrdinalSortedAndDistinct`
  - `ForgottenRow_FromADeletionThatPushed_IsNotAVisitedLocalDeletionAgain` (scan reports nothing for a forgotten row)
  - Update `ALocalDeletion_WithTheRecordUnchanged_IsPostponed_AndLStays` and `ARejectedIdThatWasDeletedHere_IsPostponedAsADeletion` and `APostponedLocalDeletion_WithTheRecordPresent_ChangesNothing` to the new behaviour; any remaining test that expects a local deletion to be postponed changes to assert `Complete` and `PushDeletions`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SyncApply|FullyQualifiedName~GitSyncApplier|FullyQualifiedName~GitSyncCycle"`. Expected: FAIL.
- [ ] **Step 3: Implement** as listed; `EntryResult` gains the carried facts needed (local hold, push deletion).
- [ ] **Step 4: Run** the Step 2 command. Expected: PASS.
- [ ] **Step 5: Commit** `Apply local deletions: list them for the push, hold unproven ones, skip invalid records`.

---

### Task 5: Applier, the delete guard

**Files:** `GitSyncApplier.Pull.cs`, `GitSyncApplier.Entry.cs`, `GitSyncApplier.cs`; tests `GitSyncApplierGuardTests.cs` (new), `GitSyncApplierPullTests.cs`.

**Interfaces:**
- Consumes: Tasks 1, 3, 4 (`guardLimit`, `guardTrips`, `holdInstead`, the shared `MergeInput` builder, `holdCause`).
- Produces:
  - A **preview pass** before the first entry transaction in `ApplyAsync` (D1 to D4): for each deletion candidate (visited ids with no accepted record and no rejected path; ids of `scan.LocalDeletions` with an accepted, parseable record; never skipped ids, rejected or invalid ones) build the same `MergeInput` (with `HeldDelete`), run `SyncMerge.decide`, count `DeleteEntry` as remote and `PushDeletion` as local. A candidate that cannot be decided (unreadable row or base, unparsable record) is not counted. `limit = SyncDeletion.guardLimit options.DeleteGuardPercent activeRowCount` where `activeRowCount` is the number of `active` rows read at the start of the pull.
  - `ApplyOne` gains a parameter `GuardVerdict` (`RemoteTripped`, `LocalTripped`); when the real decision is `DeleteEntry` and remote tripped, or `PushDeletion` and local tripped, the decision is replaced by `SyncDeletion.holdInstead` and the cause is `guard`. `ApplySummary.GuardTripped` lists `GuardTrip(direction, count, limit)` for each tripped direction.
  - A guard-tripped pull is complete and `L` advances.

- [ ] **Step 1: Write the failing tests** in `GitSyncApplierGuardTests.cs` (build N rows and records with `PullTestSupport`; a helper that creates N memories with rows and bases, deletes the records from R): `ExactlyTheLimit_RemoteDeletionsAreAllApplied` (N=100 rows at 20 %: 20 deletions applied), `OneOverTheLimit_NoRemoteDeletionIsApplied_EachIsHeldWithCauseGuard_AndTheCountAndLimitAreReported` (21), `SmallStore_LimitIsTen` (10 applied, 11 held in a 12-row store), `LocalDirectionIsJudgedSeparately` (21 local deletions, 3 remote: remote applied, local held), `HeldAndAlreadyOpenItems_AreNotCounted` (11 deletions of which 2 have open items of that direction: 9 counted, none tripped), `UnprovenApply_NeverTrips_AndHoldsEverythingWithCauseUnproven_EvenWithZeroPercent`, `ZeroPercent_LimitIsStillTen_AndHundredPercentNeverTripsBelowN`, `TrippedPull_IsCompleteAndAdvancesL`, `GuardConversion_DropsTheNotes_AndLeavesTheRowActive` (no `forgotten` row, no pending change dropped), `UnreadableCandidate_IsNotCounted_AndReportedByTheRealDecision`, `W9OwnPush_IsCountedAsALocalDeletion_AndTheGuardCanConvertIt`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncApplierGuard"`. Expected: FAIL.
- [ ] **Step 3: Implement** the preview as a separate method (`PreviewDeletions`) returning the verdict and the counts; the blobs of `LocalDeletions` ids with a record are read once in the preview and reused by the loop (a small cache keyed by id), so a blob is not read twice.
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~GitSync"`. Expected: PASS.
- [ ] **Step 5: Commit** `Add the delete guard: hold every deletion of a direction that exceeds the limit`.

---

### Task 6: Applier, the repush marker

**Files:** `SyncApply.fs`, `GitSyncApplier.Entry.cs`, `GitSyncApplier.Pull.cs`; tests `SyncApplyTests.fs`, `GitSyncApplierRepushTests.cs` (new).

**Interfaces:**
- Produces:
  - `SyncApply.postponement` returns only `Some Review` or `None` (the `Deletion` and `Repush` cases and `reasonName` words for them are removed; remove dead code and update the tests).
    - `visitSet` includes `RepushRows` (rows whose `repush_groups` is not NULL).
    - `SyncApply.RowAfter` gains `Repush: MarkerChange` with `type MarkerChange = KeepMarker | SetMarker of string option` (`SetMarker None` clears). `VisitInput` gains `RepushRows: Set<string>` as its **last** field (`GitSyncApplier.Pull.cs` builds it positionally; update that call). `planRow`: `MergeGroups` sets the marker to `RepushGroups.encode m.Repush`; `DeleteEntry`, a decision with `RowState = Some Forgotten` or `Left`, and `HoldDeletion` (either direction) clear it; every other outcome keeps it.
  - The applier writes `git_sync_entry.repush_groups` from that, lists `ApplySummary.RepushMarked` (`RepushEntry(id, groups)`) for a non-null marker it set, and passes `RepushRows` to the visit set.

- [ ] **Step 1: Write the failing tests.** Core: `visitSet_includes_rows_with_a_repush_marker`; `planRow_merge_groups_sets_the_marker_from_the_repush_flags` (flags content+tier give `"content,tier"`; no flags gives a cleared marker when a marker existed); `planRow_keeps_the_marker_for_leave_entry_and_defer`; `planRow_clears_the_marker_for_delete_hold_forgotten`; `postponement_is_review_only`. Applier (`GitSyncApplierRepushTests.cs`): `ResetRepository_RecordGroupStampOlderThanBase_SetsTheMarker_AndChangesTheEntryNothing` (declined pin, held pin-edit and a local demotion on the entry stay as they were), `GroupWithAPendingLocalChange_IsNotMarked`, `MarkerRowWithNothingNew_IsStillVisitedAndStillDecided` (fetch "nothing new": `L == R`), `MarkerSurvivesAFailedEntry_AndADeferral`, `CaughtUpRecord_ClearsTheMarker` (record stamp equals the base's again), `PullWithOnlyMarkedGroups_IsComplete`, `ReviewStillPostpones`, `MarkerWrite_FaultAtEveryStep_RollsBackTheMarkerWithTheRest`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SyncApply|FullyQualifiedName~GitSyncApplier"`. Expected: FAIL.
- [ ] **Step 3: Implement.** The base for a later repush stays the row's `base_json` (the decision's `Base` already keeps it for an `Older` group).
- [ ] **Step 4: Run** `--filter "FullyQualifiedName~Sync"` (Core) and `--filter "FullyQualifiedName~GitSync"`. Expected: PASS.
- [ ] **Step 5: Commit** `Mark groups that a reset reverted, and visit marked rows; the pull postpones only reviews`.

---

### Task 7: Pusher, removing deleted records

**Files:** `SyncPush.fs`, `GitSyncPusher.cs`, `GitSyncPusher.Prepare.cs`, `GitSyncPusher.Plan.cs`, `GitSyncPusher.Write.cs`, `GitSyncPusher.Finalize.cs`; tests `SyncPushTests.fs` (update every Core test that pins the `-0` commit message, the two-argument `commitMessage` and the A/M-only `stagedSetMatches`), `GitSyncPusherWriteTests.cs`, `GitSyncPusherFinalizeTests.cs`, `GitSyncPusherPrepareTests.cs`, `GitSyncCyclePushTests.cs`.

**Interfaces:**
- Produces:
  - `SyncPush.commitMessage (added: int) (changed: int) (removed: int) (label: string)` = `sync: <label> +<added> ~<changed> -<removed>`; every caller updated.
  - `SyncPush.stagedSetMatches (written: string list) (removed: string list) (staged: StagedEntry list)`: each written path staged once as `A` or `M`, each removed path staged once as `D`, nothing else staged, no duplicates.
  - `PushPrepared.Removals: IReadOnlyList<string>` = ids in `apply.PushDeletions` that are also in `scan.LocalDeletions`, whose accepted record is in the clone's tree at `L`, with `PathOccupied` false and no rejected path naming the id; every other id of `PushDeletions` is listed in `PushSummary.Skipped` with reason `removal-not-possible`.
  - `PlanResult.Planned.Removals`; `PushSummary` gains `Removed` (ids, ordinal); the "nothing to write" early stops (`Plan` and `WriteAsync`) account for removals: a push with removals only is a push.
  - `WriteAsync`: for each removal `File.Delete` of `memories/<shard>/<id>.json` in the clone, stage it (the existing staging batches; a deleted path stages as a removal), check the staged set with `stagedSetMatches`, commit message counts `-<removed>`. A failure before the push command resets and cleans the clone (a deleted tracked file is restored by the reset).
  - `FinalizeAsync` (only when `commit` is not null): for each removed id, if the row is still `active` and **no memory with that id exists** (`LocalEntryReader`), write it `forgotten` with the base kept and the pending stamps, `pushing_stamps` and `repush_groups` cleared; otherwise leave it untouched. A new finalize step name `forget`.

- [ ] **Step 1: Write the failing tests.** Core: `commitMessage_counts_removals`, `stagedSetMatches_accepts_a_deleted_removal_path_and_rejects_a_deleted_unexpected_path`, `stagedSetMatches_rejects_a_removed_path_staged_as_modified`. Infrastructure: `RemovalOnlyPush_CommitsOneRemoval_WithMessageMinusOne_AndTheRecordIsGoneAtTheRemote`, `AdditionsChangesAndRemovals_AreOneCommitWithAllThreeCounts`, `RemovalOfARecordMissingFromTheClone_IsSkipped_AndNothingElseChanges`, `RemovalOfAnOccupiedOrRejectedPath_IsSkipped`, `FailedPushAfterARemoval_LeavesTheRowsAndTheCloneAsTheyWere` (non-fast-forward and a failed push command), `FinalizeSetsForgotten_KeepingTheBase_ClearingPendingPushingAndRepush`, `FinalizeDoesNotForgetAnEntryThatExistsAgain`, `KillAfterPushBeforeFinalize_NextApplyMakesTheRowForgotten_AndNoSecondCommitIsMade` (fault at the per-entry `finalize` step or the new `forget` step so the finalize transaction rolls back and the commit stays pushed; then run scan, fetch, apply and push again), `FaultAtEveryPusherStep_WithARemoval_...` (the existing fault pattern). Cycle: in `TwoMachineTests.cs` one test `ALocalDeletion_IsPushed_ThroughTheCycle` (the `TwoMachineRig` of Task 2: a memory synced to B, deleted on A; A's cycle removes the record; B's next cycle deletes it).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SyncPush"` and `--filter "FullyQualifiedName~GitSyncPusher|FullyQualifiedName~GitSyncCycle"`. Expected: FAIL.
- [ ] **Step 3: Implement.** The removal reads nothing new from the store; the apply decided it.
- [ ] **Step 4: Run** both commands plus the whole `FullyQualifiedName~GitSync`. Expected: PASS.
- [ ] **Step 5: Commit** `Push local deletions: remove the record in one commit and remember the memory as forgotten`.

---

### Task 8: Pusher, write-back, held entries, narrowed exclusions

**Files:** `SyncPush.fs`, `GitSyncPusher.Prepare.cs`, `GitSyncPusher.Plan.cs`; tests `SyncPushTests.fs`, `GitSyncPusherPrepareTests.cs`, `GitSyncPusherPlanTests.cs`, `GitSyncPusherEdgeTests.cs`, `GitSyncCyclePushTests.cs`.

**Interfaces:**
- Consumes: Task 2 (`GitSyncReviews`), Task 3 (fresh content stamp on the row).
- Produces:
  - `SyncPush.ExclusionFacts` gains `HeldDelete: bool` and `PendingContent: bool`; `Exclusion` gains `HeldDelete` (name `held-delete`); `exclusion` order: `Issue`, `RejectedRecord`, `HeldDelete`, `NotReconciled` (unchanged: no base and an accepted record), `RecordMissing` only when the row has a base, no accepted record **and no pending content stamp**, then the rest unchanged.
  - `PushPrepared` knows the open held items (read with `GitSyncReviews.ReadOpenHeld` when preparing).
  - **Write-back (D15):** a candidate with a base, no accepted record and a pending content stamp is planned from `parsedBase` (the row's `base_json`) patched with the entry's current groups through `planChanged` and `RecordPatcher.Patch` exactly as a changed entry; the file does not exist in the clone, so it is a new path; `PlannedEntry` carries `IsNew = true` for the commit count (a write-back counts as `+`) and `Plan` is the group plan; `pushing_stamps` is recorded before the push as for any group; after the push the row's base is the record just written (existing finalize).

- [ ] **Step 1: Write the failing tests.** Core: `exclusion_order_puts_held_delete_after_rejected_record`, `recordMissing_does_not_apply_when_a_content_stamp_is_pending`, `notReconciled_is_unchanged`. Infrastructure:
  - `HeldEntry_WithATagPinOrPromotion_IsExcludedAsHeldDelete_AndWritesNothing` (three variants)
  - `EntryWhoseTextEditBeatAnRemoteDeletion_IsWrittenBackFromTheBase_WithTheFreshStamp_AndCountsAsAdded` (end to end through apply then push: the file reappears, its content stamp is the fresh pending stamp, unknown fields of the base survive, row base = written record, pending cleared)
  - `NoBaseRowOverAnAcceptedButUnparsableRecord_IsNotOverwritten` (reason `not-reconciled`) 
  - `BaseRowWithNoRecordAndNoPendingText_WritesNothing` (reason `record-missing`)
  - `WriteBackWithAStaleTierStamp_SkipsOnlyThatGroup`
  - `HeldRowWithPendingTextEditAfterTheItemWasClosed_IsPushed`
  - Cycle (`TwoMachineTests.cs`): `TextEditOnB_BeatsAForgetOnA_InEitherPushOrder` (the `TwoMachineRig`, both orders; the memory survives on both with B's text).
- [ ] **Step 2: Run** the three command groups of Task 7. Expected: FAIL. Existing tests this task changes: `GitSyncPusherEdgeTests.cs` `ALocalDeletion_AMissingRecord_AndANotReconciledRow_WriteAndRemoveNothing…` edits entry B's text, which now makes it a write-back; its setup becomes a tag-only edit so B still has no pending content stamp and is excluded as `record-missing`, and a second test covers the text-edit write-back; the Core tests that pin the exclusion order and `isCandidate` facts (`SyncPushTests.fs`) change with `ExclusionFacts`.
- [ ] **Step 3: Implement.** Keep the `needsRecord` reading rule of `PrepareCoreAsync` (a record is read only for rows with a base).
- [ ] **Step 4: Run** the same. Expected: PASS.
- [ ] **Step 5: Commit** `Write back a record a text edit saved, skip held entries and narrow the missing-record exclusion`.

---

### Task 9: Pusher, repush of reverted groups

**Files:** `SyncPush.fs`, `GitSyncPusher.Prepare.cs`, `GitSyncPusher.Plan.cs`, `GitSyncPusher.Finalize.cs`; tests as in Task 8.

**Interfaces:**
- Consumes: Task 1 (`RepushGroups.decode`), Task 6 (marker written by the apply).
- Produces:
  - `SyncPush.RowFacts` gains `HasRepushMarker: bool`; `isCandidate` is true for a marker row. `SyncPush.repushGroupsOf (marker: string) : GroupFlags` is `SyncDeletion.RepushGroups.decode`. The pure patch: `SyncPush.repushPlan (baseRecord: SyncRecord) (flags: GroupFlags) (pending: GroupStamps) : RepushPlan` with `RepushPlan = { Content: ContentRepush option; Sticky: StickyRepush option; Tier: TierRepush option }` (each holds the base's values and stamps for a flagged group that has no pending stamp; `None` otherwise), and the pusher turns it into the `RecordPatch`.
  - In `PlanOne`, for a row with a marker and an accepted record at `L` (R): the groups named in the marker, and only those that have **no pending stamp**, are patched into the record at R from the base: content (text, entry type, project, scope, source: the base's values, stamp = the base's content stamp), sticky (the base's sticky flag, the base's sticky stamp, the base's tier stamp when the base is sticky), tier (the base's tier, the base's tier stamp). No clock call, no new stamp. Pending-based groups follow the normal path in the same record. The base's stamp per group is written into `pushing_stamps` before the push (the pushed stamps of a repush are the base's).
  - `FinalizeAsync`: after a successful push of a marker row, `repush_groups` is cleared and `BaseJson` is the record just written; after a failed or non-fast-forward push the marker stays (nothing is cleared).
  - A marker row whose record is absent from R is not a candidate for repush (nothing to patch); it is excluded by the normal exclusions.

- [ ] **Step 1: Write the failing tests.** Core: `isCandidate_is_true_for_a_marker_row`, `repushGroupsOf_decodes`. Infrastructure: `RepushOfOneTwoAndThreeGroups_WritesTheBaseValueAndStamp_WithNoClockCall` (assert the clock `_meta` value is unchanged and the record bytes equal the expected patch), `RepushKeepsLocalOnlyState_OutOfTheRecord` (a local demotion and a declined pin: the record's tier and sticky are the base's, not the local ones), `GroupWithAPendingStampIsNotRepushed_ButItsPendingChangeIsPushed`, `MarkerSurvivesAFailedPush_AndANonFastForwardRetry`, `FinalizeClearsTheMarker_AndSetsTheBaseToTheWrittenRecord`, `MarkerRowWithAnAbsentRecord_WritesNothing`, `RepushedRecord_HasNoUnknownFieldLoss`. Cycle (`TwoMachineTests.cs`): `HandResetRemote_SixOfNineRecords_NothingDeletedHeldAndRevertedGroupsRepublished` (the spec's criterion 5, two machines, two cycles each with an unrelated push between; one case above the guard) and `PushFailsAfterAnUnprovenApply_NextCycleStillRepublishes` (criterion 6).
- [ ] **Step 2: Run** the commands of Task 7. Expected: FAIL.
- [ ] **Step 3: Implement.** Build the repush patch in a pure function in `SyncPush.fs` where it only needs Core types (`SyncRecord`), and translate it to `RecordPatch` in the pusher.
- [ ] **Step 4: Run** the same. Expected: PASS.
- [ ] **Step 5: Commit** `Republish groups a reset reverted from the last agreed value and stamp`.

---

### Task 10: Cycle scenarios, guides and criteria, full verification

**Files:** `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncCycleDeletionTests.cs` (new), `GitSyncCycleCallerTests.cs` (unchanged, must pass), `AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md`, `CHANGELOG.md` only if the repository's unreleased section already lists git sync work (it need not).

**Interfaces:** Consumes everything above.

- [ ] **Step 1: Write the failing scenario tests** in `GitSyncCycleDeletionTests.cs` with the `TwoMachineRig` of Task 2 (two machines A and B on one bare repository), one named test per acceptance criterion of the spec that no earlier task covers end to end: `Forget_OnA_DeletesOnB_EvenWithPendingTagProjectOrPromotionOnB`; `ForgottenMemory_IsNotReinserted_BySameTextOrOlderTextAfterAReset_AndNewerTextBringsItBackNonSticky`; `PinnedOnB_DeletedOnA_IsHeldWithCauseSticky_AlsoWhenThePinIsOnlyPending`; `OverTheLimit_AllHeld_CycleCompletes_OtherChangesPush` and `ExactlyTheLimit_AllApplied`; `HeldEntryIsNotWrittenBack_ByTagPinOrPromotion_UntilTextEdited_RecordReappearingClosesTheItem`; `ARecordAddedAndDeletedBetweenTwoPulls_IsDeleted` (row active with base, entry present, record absent from both trees); `SyncInitToANewRepository_OnAMachineWithRows_DeletesNothing`; `ABlockedNeverPushedMemory_SurvivesALostClone`; `ADeletedMemoryWithAnInvalidRecord_IsSkippedAndHoldsNeitherLNorOtherPushes`; `AfterAPullOfOnlyDeletionsHeldItemsAndRepushMarkers_TheCycleIsComplete_AndAReviewStillMakesItIncomplete`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitSyncCycleDeletion"`. Expected: FAIL only where behaviour is missing; fix any gap in the production code found here (each fix gets its own small commit and test).
- [ ] **Step 3: Docs.** `src/TotalRecall.Core/AGENTS.md`: add `SyncDeletion.fs` and update the `SyncApply.fs` and `SyncPush.fs` rows (postponement review-only, `RepushRows`, `commitMessage` and `stagedSetMatches` shapes, `Exclusion` list). `src/TotalRecall.Infrastructure/AGENTS.md`: `GitSyncReviews`, the applier's preview and carry-outs, the pusher's removals, write-back, repush and held skip, the new `ApplyOptions`/`CycleSettings` member. Root `AGENTS.md`: the git sync "Where to look" rows that name the apply, push and scan files gain a one-line mention of deletions; keep "no caller" wording. Acceptance-criteria file: move the `accept`/`restore`/bulk lines of the 1b-ii section to the 1b-iii section and add the held-item facts 1b-iii may rely on (cause words, directions, `open` state).
- [ ] **Step 4: Verify fully.** Remove the `node_modules` symlink (`rm node_modules`, never `rm -r`) then run the profile's verify blocks in order from the worktree (`node <skill dir>/scripts/profile.js verify-files .github/develop.md <dir>`, then each file with `sh -e`). Expected: every block exits 0, the release build prints no new warning, the catalog drift diff is empty, `GitSyncCycleCallerTests` passes unchanged.
- [ ] **Step 5: Commit** `Add the deletion cycle scenarios and describe the new pieces in the guides`.
