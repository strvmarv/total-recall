# Git sync, milestone 1b-ii: deletions

Date: 2026-11-19. Status: draft for review. Part of #93 (git sync). Milestone 1b-i (the scan, the pull, the push and the
cycle) is merged; this is the second of the umbrella's three "cycle core" milestones. The umbrella design is
`2026-11-15-git-sync-design.md` (the sections "The sync cycle" and "Deletions: a missing file is never proof"); the parts it builds on are
`2026-11-17-git-sync-scan-design.md`, `2026-11-17-git-sync-pull-design.md`, `2026-11-17-git-sync-push-design.md` and
`2026-11-18-git-sync-cycle-design.md`. Where this document is more precise it wins for this milestone, and every departure
is listed under "Departures from the earlier designs".

## Goal

Make deletions work in both directions, safely: a memory forgotten on one machine is forgotten on the others, and nothing is
ever deleted without proof. After this milestone a pull applies a remote deletion that is proven, **holds** one that is not
(or that is too large, or that would remove a pin) as a `held-delete` item instead of applying it, a push removes the record
of a memory deleted here, a local edit of the text beats a deletion in either direction, and a repository that was reset
is repaired from this machine's last agreed state instead of being trusted.

Success: the cycle runs to completion with deletions in play (no deletion postpones the pull any more), `L` advances, the
push runs, and exactly the deletions the rules prove are carried out. A guard-tripped, unproven or pin-protected deletion
is stored as an open item that a later milestone can answer; nothing about it is lost or applied meanwhile.

## Non-goals

Left to later milestones and **not** built here:

- **Answering a held item** (`accept`, `restore`, the bulk forms and their terminal check), `sync review`, `sync now`,
  `sync unblock`: 1b-iii. The draft acceptance criteria for 1b-ii listed `accept` and `restore`; the owner moved them to
  1b-iii (they need `sync review`, and a restore of a pin record needs the `new-pin` review). This milestone stores
  everything an answer needs and closes an item only by the two rules below (a record that reappears, a text edit).
- Review items for pins and conflicts (`new-pin`, `pin-edit`, `conflict`), the secret block, alias detection, the `sync`
  block of `status` (1b-iii). An entry whose decision has a review request is still **postponed** with the reason `review`.
- Any caller of the cycle, the pusher or the applier. `GitSyncCycleCallerTests` keeps pinning "no caller" until 1b-iii
  merges. Nothing is advertised or released as a feature.
- Arrivals text, the `sync` block, progress, scheduling, `session_start` integration (milestones 2 and 3). The apply reports
  its notes as data.
- Bulk deletion tooling, a purge, anything that deletes in the repository's history. History stays append-only.

## What exists and what is new

The merge decisions are complete and tested in `Core/SyncMerge.fs`: `decide` already returns `DeleteEntry`, `HoldDeletion`,
`PushDeletion`, `LeaveEntry` with `FreshContentStamp`, the notes `EditBeatsDeletion` and `PendingChangesDropped`,
`CloseHeldDelete`, `Merged.Repush`, and row state `Forgotten`, and its input already carries `ApplyProven` and `HeldDelete`.
Today the applier **postpones** every one of them (`SyncApply.postponement`: reasons `deletion` and `repush`) and passes
`HeldDelete = null`; the pusher never removes a file, excludes rows with `record-missing` and `not-reconciled`, and has no
`repush_groups` or write-back path; nothing reads or writes `git_sync_review`; `repush_groups` is an opaque column nothing sets;
`delete_guard_percent` is loaded into `GitSyncConfig` and reaches only `RepoGuard`.

New in this milestone:

- `Core.SyncDeletion` (F#, pure): the guard limit and verdict, the conversion of a deletion decision into a hold, the cause
  words of a held item, and the codec of `repush_groups`.
- `Infrastructure.GitSync.GitSyncReviews`: typed access to `git_sync_review` for `held-delete` items, taking the caller's
  transaction as `GitSyncIssues` does.
- The applier: a **deletion preview** (decision 2), real handling of the postponed outcomes, `HeldDelete` from the open items,
  the repush marker, and a richer `ApplySummary`.
- The pusher: removal of a deleted memory's record, write-back of a record that a text edit saved, repush of reverted groups,
  exclusion of held entries, and the `forgotten` transition after a push.

## Approaches

The merge tables are fixed by the umbrella, so the choice was where the decisions and the delete guard live.

- **A. The apply is the single decision point (chosen).** The pull previews every deletion candidate before it applies
  anything, counts both directions, trips the guard per direction, and then decides each entry with that verdict. The push
  receives an explicit list of the local deletions to remove and only executes. One place counts, one place writes review
  state, and both directions use the same notion of "eligible". The cost is a second, pure run of `decide` over the (few)
  deletion candidates, and that local deletions are decided in the apply even though the push acts on them.
- **B. The guard split between the apply (remote deletions) and the push (local deletions).** The push would count and write
  held items itself, making it a second decision point that writes review state, with two counts that can disagree. Rejected.
- **C. A persisted approved-deletion marker on the row.** It would survive a crash between the apply and the push without
  recomputing, but needs a schema migration, and nothing needs it: the next cycle's scan reports the same local deletions and
  `decide` is pure, so the next apply reaches the same verdict. Rejected.

## Decisions

### Staging: preview, verdict, decide

1. **The deletion candidates** of a pull are the ids that can produce a `DeleteEntry` or a `PushDeletion`: the ids in the visit
   set that have **no accepted record** in R's listing and no rejected path (`DeleteEntry` and every hold for a vanished
   record come from these), and every id of the scan's `LocalDeletions` that has an accepted record (`PushDeletion`, and the hold of
   an unproven apply). Ids the scan skipped and ids with a rejected path are never candidates.
2. **The preview** runs before the first entry transaction. For each candidate it builds the same `MergeInput` as the real decision, **including `HeldDelete` from the open items** (that is what leaves an entry with an open item out of the count, as the umbrella requires; one shared function; the record's blob is read only for a `LocalDeletions` id that has one) and runs
   `SyncMerge.decide`, reading nothing it will write. It counts the decisions whose entry outcome is `DeleteEntry` (**remote**)
   and `PushDeletion` (**local**). A candidate the preview cannot decide (an unreadable row, base or blob, or a record that does not parse) is not counted;
   the real decision reports it as it does today.
3. **The limit** is `max(10, N * delete_guard_percent / 100)` with integer division (rounded down), where `N` is the number of
   `active` rows at the start of the pull (rows with and without a base; a `forgotten` or `left` row is not an entry). A direction
   **trips** when its count is **greater than** the limit. The limit uses `[git_sync] delete_guard_percent` (default 20, clamped
   0 to 100 as the config already does); `ApplyOptions` and `CycleSettings` carry it (a new member with the default of 20, so
   existing constructions are unchanged).
4. **A tripped direction applies none of its deletions.** For every candidate of that direction the real decision's
   `DeleteEntry` / `PushDeletion` is replaced by `HoldDeletion <direction>` (`SyncDeletion.holdInstead`, pure: the decision keeps its `Base` update, **drops its `Notes` and sets `RowState` to `None`** (a `DeleteEntry` decision says `Forgotten`; the entry stays, so the row must not become `forgotten`), and `CloseHeldDelete` as decided; nothing is deleted). An untripped direction is carried out as decided. A hold that
   `decide` itself returns (a pin held, an unproven apply) is never counted and never converted. The count and limit of a tripped direction are in the summary
   (`GuardTripped`). **A guard never blocks the cycle**: held items let `L` advance and the push proceed.
5. **Unproven applies** need no new fetch logic. When `ApplyProven` is false `decide` already returns `HoldDeletion` for every
   `active` row with a base whose entry exists and whose record is absent (remote), and for every pending local deletion
   (local); no `DeleteEntry` exists to count, so no remote hold is guard-tripped, whatever the percentage, and nothing is deleted or re-pushed blind. (`decide`'s W9 still returns `PushDeletion` in an unproven apply: a row with no base whose own `pushing_stamps` prove the record in R is its own push. That is a counted local deletion like any other, and the guard can convert it.) The visit set already
   contains those rows (`ActiveWithBase` minus `Present`, `LocalDeletions`).

### What each outcome does

6. **`DeleteEntry`** (proven, not sticky, no pending text, no open item, and under the guard): in the entry's immediate
   transaction (the existing re-read protocol applies unchanged) delete the vector row and the content row from the entry's tier
   (`SqliteStoreTransaction.Delete`, the same two steps as `memory_delete`), set the row `forgotten` **keeping its base**, clear
   its pending stamps, `pushing_stamps` and `repush_groups`, and remove its `apply-failed` issue. `import_log` rows are left
   as they are (a host file that was imported is not imported again). The note `PendingChangesDropped`, when `decide`
   sets it, goes to the summary.
7. **`HoldDeletion` (either direction)**: in the entry's transaction upsert one `git_sync_review` item
   `(entry_id, 'held-delete')`: `state = 'open'`, `direction` `remote` or `local`, `record_hash` the blob id of the record in R
   (NULL when R has none), `payload_json` `{"cause":"<cause>"}`, `machine` NULL, `arrived_at` the apply's clock in
   milliseconds. The **cause** (`SyncDeletion.holdCause`) is `guard` for a converted decision, `unproven` for any hold in an
   unproven apply, and `sticky` for a proven remote hold of a pinned entry. Nothing else changes: the entry stays,
   the row keeps its base, and a **local** hold also clears the pending stamps (as `clearsPending` says, the entry is gone;
   its text for a later restore is the row's `base_json`). One item per entry: a second hold for an id whose item exists in any state replaces
   it with an open one (1b-iii may keep answered items; this milestone never leaves one).
8. **`HeldDelete` input**: for every id the applier reads the open `held-delete` items once (as it reads rows and issues) and
   passes the item's direction to `decide`. The decision's **`CloseHeldDelete`** deletes the item in the same transaction as the
   rest of the entry. A closed item is removed, not marked (my call: nothing in this milestone needs the history, and a later
   hold of the same id needs the key free). **Order inside the transaction:** the close runs first and the hold's upsert after it, so a
   decision that carries both (an open `remote` item, the entry then deleted here while the record is still in R, and an unproven or
   guard-converted `HoldDeletion LocalDeletion`) ends with exactly one open item, of the new direction. **An open item stays open until
   something closes it.** A `guard` item is not re-evaluated when the count later falls under the limit, and an `unproven` item is not
   re-decided by a later proven cycle: `decide` leaves an entry with an open item alone (W6.3, W8.2) and the only other closers are
   the two rules above and the answers of 1b-iii, as the umbrella says ("excluded from deletion and from the guard count until answered").
9. **`PushDeletion`** (a local deletion with proof): the apply writes nothing to the store; the row's base follows the
   decision (the own-push case adopts the record as the base), the pending stamps are cleared as `clearsPending` says, and the id
   is added to `ApplySummary.PushDeletions`. The row stays `active`; it becomes `forgotten` when the push that removed the
   record is finalized (decision 14). A crash in between is harmless: the next scan reports the deletion again, and the next apply decides it again. **The applier's existing "the decision changes nothing, so open no transaction" shortcut must account for what is new**: a hold (the item is written), a close (the item is removed) and a `PushDeletion` (the id is reported in `PushDeletions`) are never short-circuited to a no-op because the row happens to be unchanged.
10. **`LeaveEntry` with `FreshContentStamp`** (a pending text edit beats a remote deletion): the apply issues a **fresh
    content stamp** above the greater of the row's pending content stamp and its base's content stamp, stores it as the row's
    pending content stamp (through the applier's existing `IssueStamp`, inside the entry's transaction at the existing `row` step, as
    `RestampPending` does today: a rollback rolls the clock back with the rest, so no stamp is burned), closes any open held item, and lists the note `EditBeatsDeletion`. The entry is untouched. The entry
    now has a pending text change and no record in R, which is the push's write-back case (decision 15).
11. **`InsertFromRecord` with `EditBeatsDeletion`** (the record's text is newer than the deletion, the entry is gone): the existing
    non-sticky insert, the base becomes the record, the note goes to the summary, any held item is closed. A record that pins still
    produces a `NewPin` review request, so it is postponed with the reason `review` (1b-iii), as before.
12. **Row states.** The apply creates `forgotten` rows only where `decide` says so: `DeleteEntry` (decision 6), and the decisions whose `RowState` is `Forgotten` with the entry already gone: W10 (record absent, base present: the deletion was already made, possibly by this machine before a crash) and W9's second branch (no base, record present and not this row's own push: the base becomes the record and the row `forgotten`). An own push whose record is gone and whose row has no base is W11 (`DropRow`). `DropRow`, `Ignore` and `Defer` are carried out as
    today. Nothing else creates a `forgotten` row except decision 14. The scan's reactivation of a `forgotten` or `left` row whose memory
    exists again is unchanged.
13. **A rejected path of a locally deleted id, or an accepted path whose record does not parse.** Today an id with a rejected path, or an accepted path whose blob is invalid, that the scan reports as a local deletion is postponed as a `deletion` (two branches of the pull). With deletion handling real that would stall every push on one bad record, which the umbrella
    forbids ("one bad record cannot stop every push"). Such an id is **not decided and not postponed**: it is listed in the summary's `Skipped` with the reason `the record is rejected` (or `the record is invalid`), `L` is not held back, nothing is written for it, and it stays in the visit
    set through `LocalDeletions` until the path is valid or gone. The preview does not count it. (My call; the pull design only said "never read as a deletion".)

### The push

14. **A local deletion is pushed.** The pusher receives the apply's `PushDeletions`. For each id still in the scan's `LocalDeletions` it
    removes the file `memories/<shard>/<id>.json` from the clone (the record the clone holds at `L`; a record that is missing or
    that fails the tree rules is left alone and reported), stages the removal, and counts it in the commit message
    (`sync: <machine> +<added> ~<changed> -<removed>`, where `-0` was a constant before; a write-back counts as `+`, a record that is new in the repository, and a repush or edit as `~`). The writer's "nothing to write" stop and its staged-set check (`SyncPush.stagedSetMatches`, which accepts only added and modified files today) both change: a removal-only push is a push, and a deleted file is an expected staged entry. A push with removals and no other change is a
    normal push. After a **successful** push the finalize transaction sets each removed id's row `forgotten` (base kept, pending,
    `pushing_stamps` and `repush_groups` cleared). A failed or non-fast-forward push removes nothing from the rows; the clone is
    reset to `L` by the next push as today. A crash after the push and before the finalize is repaired by the table's "record absent,
    local entry gone, base present" row (decision 12).
15. **A record a text edit saved is written back.** A candidate row that has a base, **no record in R** and a **pending content
    stamp** (decision 10) is written as a new file whose starting record is the row's `base_json` with the entry's current groups patched
    onto it as for any entry, using the fresh pending content stamp. This replaces the `record-missing` exclusion for exactly this
    case. The record has no remote copy to take unknown fields from, so unknown fields of the base survive (the row keeps them).
16. **Repush of reverted groups.** `decide` flags a group (`Merged.Repush`) when the record's stamp for it is older than the base's
    (a reset reverted it) and the group has no pending local change. The apply writes the row's **`repush_groups`** as the
    **flags of this decision** when it carries a `MergeGroups` decision out (replacing the old marker: a group the repository has
    caught up on, or that now has a pending change, is cleared). Every other outcome leaves the marker as it is, except that
    `DeleteEntry`, a row that becomes `forgotten` or `left`, a `HoldDeletion` of either direction, and the finalize of a pushed deletion clear it; a deferred, failed or
    postponed entry keeps it. (A marker cannot outlive a hold, so the writer never meets one on a row whose record is absent from R.) The marker is formatted by `SyncDeletion.RepushGroups` as the group names in the fixed order `content,sticky,tier`
    joined by commas, NULL when none. The visit set gains the rows whose `repush_groups` is not NULL (`SyncApply.VisitInput` field `RepushRows`), so a pull with nothing new still decides them and the marker survives a failed push, a non-fast-forward
    retry and `L` moving to `R`. A row with a marker is a **push candidate** even with no pending stamp. For each flagged group the writer
    patches the record **at R** by setting that group's value and stamp to the **base's value and stamp** (no new stamp, no
    clock call; a local-only state such as a declined pin, a held pin edit or a local demotion never enters the record). It
    records the base's stamp in `pushing_stamps` before the push as for any group. After a successful push the row's base is the
    record just written and `repush_groups` is cleared. A group with a real pending local change takes the normal path and its
    marker is dropped by the apply (decision above).
17. **Held entries are not pushed.** A row with an open `held-delete` item (read from the store when the push prepares) is excluded
    with the new reason `held-delete` and writes nothing: a tag, a pin or a promotion on it can no longer write the vanished record
    back. The only thing that writes it back is a pending text change, and the apply has already closed the item then
    (decision 10). A row whose item is `local` is a `LocalDeletions` row and was never a candidate.
18. **Exclusions narrowed.** After a complete pull the push candidate with a base and no record is one of: deleted (not a candidate), held (excluded, decision 17), or saved by a text edit
    (written back, decision 15). The `record-missing` exclusion therefore stays only for a base-and-no-record row **with no pending change of the text** (nothing the pusher may write
    from nothing) and is documented as defensive; a row with a base, no record and a pending text change leaves it. **`not-reconciled` stays, unchanged**: it is reachable, because a path
    that the listing accepts but whose blob does not parse is a rejection the pull never decides (decision 13), so a no-base row over such a record must not be written over it blind, and the pusher
    reads a record only for rows with a base. The pusher's **gate** is unchanged: it runs only after a complete pull (`SyncCycle.pushDecision`).

### The pull's result and `L`

19. **`postponement` becomes review-only.** The reasons `deletion` and `repush` are removed; `review` remains (precedence is moot).
    Everything else is carried out. A pull is **complete** when every id was processed and none was postponed (a review) or deferred,
    exactly as before. Held items, applied deletions and repush markers do not hold `L` back.
20. **`ApplySummary`** gains, as plain data (no text for the user): `Deleted` (ids), `Held` (id, direction, cause), `GuardTripped`
    (direction, count, limit), `PushDeletions` (ids), `ClosedHeld` (ids), `Notes` (id, note word), `RepushMarked` (id, groups).
    `Postponed` can then only hold `review`; the cycle's existing summary carries these through without change.

## Constraints and calls

- **No schema change.** `git_sync_review` already has the columns and the `held-delete` kind; `repush_groups` already exists.
  Migration 18 stays as it is.
- **Memories only; tier up only; append-only history** (owner decisions, not reopened). A deletion removes a file in a new commit.
- **Pins are never deleted by another machine without the user** (the `sticky` hold), and every re-insert stays non-sticky.
- **My calls** (marked so a reviewer can overrule them): the guard's denominator is the `active` rows at the start of the pull and
  the limit rounds down; a closed item is removed; `payload_json` carries only `cause`; `machine` is not recorded; a rejected path of a
  locally deleted id is skipped and not postponed (decision 13); `repush_groups` is a comma list in a fixed order; the `forgotten` transition
  after a push happens in the finalize transaction.
- **Left to the builder:** the structure of the preview (a shared function that builds `MergeInput`, reuse of the blobs it read), the
  exact writer mechanics for removal and write-back inside the existing plan, write and finalize steps, the test seams, and the defensive
  exclusion of decision 18.

## Departures from the earlier designs

- The pull design postponed `deletion` and `repush`; this milestone carries them out and keeps only `review`.
- The pull design left a rejected path of a locally deleted id postponed as `deletion`; it is now skipped (decision 13).
- The push design's `record-missing` exclusion narrows to a row with no pending text change, `not-reconciled` is kept, and the commit message counts removals (decisions 14 and 18).
- The cycle design's "pull-incomplete" skip now arises only from a review item, a deferral or a cancellation.
- The draft acceptance criteria for 1b-ii listed `accept`, `restore` and the bulk forms; they move to 1b-iii (this milestone updates the companion
  criteria file accordingly).
- A held item's `payload_json` carries a `cause`, which the umbrella does not define.

## Requirements on the later parts

- **1b-iii** answers held items: `accept` deletes the entry locally (remote) or pushes the deletion (local) and bypasses the guard; `restore`
  keeps the local entry and re-pushes it as a new record with its base cleared (remote) or re-inserts the entry **from `base_json`**, non-sticky,
  with a `new-pin` item for a sticky record (local). It decides the lifecycle of answered items (`approved`, `declined`, `resolved`) in
  `git_sync_review`; this milestone only creates `open` items and removes closed ones. The bulk `accept-deletions --all` and
  `restore-deletions --all` refuse without an interactive terminal. It replaces the `review` postponement and must not change what a postponed
  entry looked like (nothing was recorded).
- **Milestone 2** reads `ApplySummary` (`Held`, `GuardTripped`, `Deleted`, `Notes`) for arrivals, the `sync` block and hints.

## Interfaces (for the plan)

- `Core.SyncDeletion`: `guardLimit : percent:int -> active:int -> int`, `guardTrips : count:int -> limit:int -> bool`,
  `holdInstead : MergeDecision -> MergeDecision` (a `DeleteEntry` becomes `HoldDeletion RemoteDeletion`, a `PushDeletion` becomes
  `HoldDeletion LocalDeletion`, anything else unchanged), `holdCause : proven:bool -> converted:bool -> direction -> string`,
  `RepushGroups.encode/decode`. All pure and total.
- `Core.SyncApply`: `postponement` review-only; `VisitInput.RepushRows`; `planRow` handles `FreshContentStamp` (needs the base's content
  stamp in `RowBefore`), the `repush_groups` replacement and a `Forgotten` row from `DeleteEntry`.
- `Core.SyncPush`: `isCandidate` accepts a row with a marker; `stagedSetMatches` accepts a deleted file; `Exclusion` gains `HeldDelete` and `RecordMissing` narrows (decision 18); the writer's pure plan gains the removal
  list, the write-back and the repush patch; `commitMessage` takes the removal count.
- `Infrastructure.GitSync.GitSyncReviews` (new): `ReadOpenHeld`, `UpsertHeld`, `Delete`, each with the caller's transaction.
- `GitSyncApplier`: `ApplyOptions` gains `DeleteGuardPercent`; the preview; the carry-out of `DeleteEntry`, `HoldDeletion`, `PushDeletion`,
  `FreshContentStamp`, the held-item reads and closes, the repush marker; `ApplySummary` as in decision 20.
- `GitSyncPusher`: removals, write-back, repush, the held exclusion, finalize setting `forgotten`. `CycleSettings` gains `DeleteGuardPercent`.

## Tests

Nothing is advertised, so the tests are the product. New code is tested in the existing rigs (Core property and table tests; the applier,
pusher and cycle rigs with an in-process bare repository, one or two stores and the stub embedder). In particular:

- **Core**: every `guardLimit` and `guardTrips` boundary (9, 10, 11 deletions, 100 entries at 20 %, 0 %, 100 %, no entries); `holdInstead` on
  every entry outcome; `holdCause`; the `repush_groups` codec round trip and its rejection of any other text; `postponement` returns only `review`;
  `visitSet` with a marker row; `planRow` for each new outcome; `isCandidate` and the narrowed `record-missing`; `holdInstead` clears `RowState` and notes.
- **Applier**: each outcome of decisions 6 to 12 against a real store (the existing fault-injection steps at every transaction step leave content,
  vector, row, held item and issue exactly as before the rollback); a preview that trips only the remote direction, only the local direction
  and both; a pull with the guard tripped still advances `L`; the cause words; the held item is closed by a reappearing record and by a pending text
  edit and left alone by a pending tag, pin, tier or project change; an id with a rejected path and a local deletion is skipped and does not hold `L`.
- **Pusher**: a removal commit (and a commit with additions, changes and removals), the count in the message, a removal that fails to push leaves rows
  as they were, the finalize sets `forgotten`, a write-back from `base_json` with the fresh stamp, repush of one, two and three groups with the base's
  value and stamp and no clock call, the marker kept after a failed push and after a non-fast-forward, a held row writes nothing.
- **Cycle (two machines)**: the scenarios of the acceptance criteria, in both orders of arrival where the umbrella says so.
- `GitSyncCycleCallerTests` is unchanged and still passes; the shim tests, the tool catalog check and the release build pass with no new warning.

## Acceptance criteria

(The ticket copies these, refined.)

1. A forget on machine A is applied on machine B by one cycle each; B's unpushed tag, project or promotion change does not keep the memory, B's unpushed text edit keeps it on both machines in either push order, and a pending pin change on B holds it (criterion 3).
2. A memory forgotten on one machine is not re-inserted on another by a record with the same text, nor by older text after a reset; a newer text edit that does not pin it re-inserts it (non-sticky) and the summary notes it (a record that pins goes to review and is postponed, as before).
3. A memory pinned on B is held (not deleted) when another machine deletes it, with cause `sticky`, also when the pin is only pending (not yet in the repository); the entry and its pin remain.
4. When more than `max(10, delete_guard_percent of the active rows at the start of the pull, rounded down)` deletions in one direction would be applied or pushed, none of that direction is applied,
   each becomes an open `held-delete` item with cause `guard`, the pull is complete, `L` advances, other changes are still pushed, and the summary has the count
   and the limit; at exactly the limit all are applied; the other direction is judged on its own.
5. A hand-reset remote (six of nine records, and one case above the guard), after two cycles per machine with an unrelated push between: nothing is deleted
   locally, each vanished record is an open item (cause `unproven`, no guard involved), each reverted group is republished from its base value and stamp, and a local
   demotion on B never enters the record.
6. A push that fails after an unproven apply still republishes the reverted groups on the next cycle, and a non-fast-forward retry keeps the marker.
7. An entry with an open held item is not written back by a tag, pin or promotion while the item is unanswered; a pending text edit saves the entry (the item is closed,
   the record is written back from the base with a stamp newer than the deletion); a record that reappears closes the item; an unrelated entry's item is untouched.
8. A local deletion on A is removed from the repository by the next cycle (one commit whose message counts it), and the row is `forgotten` with its base; a kill after
   the push and before the finalize leaves no duplicate commit and the next cycle repairs the row.
9. A local deletion whose record in the repository is rejected, or accepted by path but invalid, is skipped and reported, holds neither `L` nor the other pushes, and nothing is
   removed.
10. A row that is `active` with a base, whose entry is present and unchanged and whose record is absent from the trees of both `L` and `R` (so it is absent from the `L..R` diff: the record was added and deleted between two pulls of this machine) is decided as a deletion in a proven pull and is deleted, the row
    `forgotten`; `sync init --url <new repository>` on a machine with rows deletes
    nothing; a blocked, never-pushed entry survives a lost clone.
11. After a pull no postponement has the reason `deletion` or `repush`; `review` still postpones. A no-base row whose accepted path holds an invalid record is not overwritten by a push; a
    base-and-no-record row with no pending text change writes nothing.
12. A fault injected at every step of an entry's transaction (delete, hold, close, the `row` step that issues the fresh stamp and writes the repush marker) leaves the content row, the vector row, the entry's row,
    its held item and its issue exactly as before.
13. Nothing outside the existing cycle, pusher and applier files calls them (`GitSyncCycleCallerTests` passes unchanged); the draft acceptance-criteria file moves
    `accept`/`restore` to 1b-iii; the contributor guides (`AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`) describe the new pieces; the existing
    checks, the shim tests, the tool catalog check and the release build pass with no new warning.
