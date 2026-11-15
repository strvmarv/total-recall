# Git sync 0b-2: the pure merge decisions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `SyncMerge.decide : MergeInput -> MergeDecision`, a pure, total, deterministic function in F# `TotalRecall.Core` that implements every per-entry row of the umbrella's merge tables, with row-by-row and property tests. Nothing calls it yet.

**Architecture:** One new F# source file, `src/TotalRecall.Core/SyncMerge.fs` (module `TotalRecall.Core.SyncMerge`), placed after `SyncValidation.fs`. It holds the input and output types, the per-group rules (sticky, tier, content, tags), the whole-entry table and the held-delete closing rule. The spec allows a second file `SyncMergeGroups.fs` "if one file grows past a comfortable size"; the code is about 450 lines, so it stays one file. Tests are `tests/TotalRecall.Core.Tests/SyncMergeTests.fs` (Expecto, FsCheck 2.16.6).

**Tech Stack:** F# (Core is pure: no I/O, no mutable state, AOT-safe), Expecto + FsCheck for tests.

**Spec:** `docs/superpowers/specs/2026-11-15-git-sync-merge-decisions-design.md` (read it first; this plan records what it leaves to the implementer and gives the code). The umbrella is `docs/superpowers/specs/2026-11-15-git-sync-design.md`. Issue #107.

## Global Constraints

- The spec file is read-only for the whole run. Commits carry no trailer and no AI attribution; messages are plain imperative sentences (see `git log`).
- Core has no I/O and no mutable state. No new package. Types come from `Types.fs` (`Tier`, `EntryType`, `SourceTool`), `SyncStamp.fs` (`Stamp`, `MachineId`) and `SyncRecord.fs` (`SyncRecord`, `Placement`, `tierRank`, `normaliseTags`). `Stamp.compare` is the ordering.
- `SyncMerge.fs` goes right after `SyncValidation.fs` in `TotalRecall.Core.fsproj`; `SyncMergeTests.fs` goes right after `SyncValidationTests.fs` in `TotalRecall.Core.Tests.fsproj`.
- Several records share the labels `Content`, `Tags`, `Sticky` and `Tier` (`LocalEntry`, `GroupStamps`, `GroupFlags`, `Merged`, `BaseUpdate`, plus `SyncRecord` and `Placement`). F# infers the last declared. **Every record construction in the code and the tests is type-qualified (`{ Merged.Content = ...; Tags = ... }`) or annotated, and every function parameter that is one of these records is annotated.**
- Test command: `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj` (focused: add `--filter "FullyQualifiedName~SyncMerge"`). The Core tests need no native library. The full profile verify blocks run at the end (Task 6).
- A test name quotes the umbrella's row (for example `W6.1 a pending change of the text`), so a reviewer compares rows one to one.

## Interpretations and deviations (recorded here because the spec is read-only)

1. **One file** (see Architecture). Types are exactly the spec's, with the extra helper `clearsPending : EntryOutcome -> bool -> bool` that states the spec's "Row effects" paragraph as code (the caller in 1b-i uses it; Task 3 tests it).
2. **`BaseUpdate` for an `unchanged` group is `KeepBase`** (same stamp, so the same value; fewer writes). `own` and `newer` groups are `BaseBecomesTheirs`; `older` is `KeepBase`; tags are always `BaseBecomesTheirs` when there is a record.
3. **`RowState` in W4 is `Some Active` when `Row` is `None`**, so the caller creates the row that will hold the new base; `None` otherwise. W1, W3 (insert), W8.1 are `Some Active`; W9 (not own) and W10 are `Some Forgotten`; W6.4 is `Some Forgotten`; everything else is `None` (unchanged).
4. **Idempotence allows `Repush` to repeat** along with `PushDeletion`, `FreshContentStamp`, `PushNeeded` and `Defer`: a `Repush` flag asks the push step to write the base's group value and it repeats until that push lands, exactly as the spec says for the other push-implied outputs. The spec's list names the four; `Repush` is the same kind of output.
5. **Order of arrival for tier** (to be stated in the PR body next to the acceptance criteria, since the spec's wording is the original). The spec's property says content and tier end "as the greater stamp (the warmer tier) decides". Taken literally for tier it contradicts "a group is never taken from an older stamp": versions V1 (hot, stamp 1) then V2 (cold, stamp 2) give hot in the order V1,V2 (V1 is newer than the base when it arrives) and the unchanged local tier in the order V2,V1 (V1 is then older than the base). The test asserts what the rules do guarantee: **content** is order independent for any two versions; **tier** is monotone (a decision never lowers a tier) in either order and order independent whenever the greater-stamp version is not colder than the other. The reading of "(the warmer tier)" is "upward only".
6. **`pendingText`** (Decided here 4) is `Pending.Content` is set and the local text differs from the base's text. W6.4's `PendingChangesDropped` fires when a tag set differs from the base's, `Pending.Tier` is set, or project, scope or entry type differ from the base's (a colder local tier alone is a local demotion, which never syncs, and is not "pending").
7. **Held-delete closing** is computed once at the end (`finish`): a `RemoteDeletion` item closes when the record is present or the entry is gone; a `LocalDeletion` item closes when the entry is present, the record is absent or the decision is `InsertFromRecord`; W6.1 forces a close when any item is open; `Defer` never closes.
8. **Order of reviews** is sticky first then content (the sticky group is decided first), `[NewPin]` before `[PinEdit]`.

## Review Focus

Inputs a task's tests would not otherwise force; each is pinned in the task named in brackets.

1. A record that unpins and edits the text of a pinned entry at once: text taken directly, no `PinEdit` [Task 2].
2. A held `pin-edit` followed by a remote deletion: not a pending change, so the deletion is held, not republished [Task 4].
3. A foreign text edit over an own tier stamp with the entry gone and no base: not pushed as a deletion [Task 3].
4. A held entry keeps its pending pin, tags and tier (`clearsPending` false) [Tasks 3, 4].
5. Every decision for every combination of the small domain returns without an exception [Task 5].

---

### Task 1: Types, fsproj wiring and the test builders

**Files:**
- Create: `src/TotalRecall.Core/SyncMerge.fs`
- Create: `tests/TotalRecall.Core.Tests/SyncMergeTests.fs`
- Modify: `src/TotalRecall.Core/TotalRecall.Core.fsproj`, `tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`

**Interfaces:** the spec's input and output types, verbatim, plus `clearsPending`. `decide` is added in Task 2.

- [ ] **Step 1: Write the file header and the types.** `SyncMerge.fs`:

```fsharp
module TotalRecall.Core.SyncMerge

// Git sync, milestone 0b-2: the pure merge decisions. For one entry in one sync cycle, `decide`
// says what to do to the local entry, to the row's base and pending stamps, which reviews to
// open and what is implied for the push. It performs nothing and calls nothing; the cycle
// (milestone 1b) carries the decision out. Pure, total and deterministic.

open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord

type RowState =
    | Active
    | Forgotten
    | Left

type Direction =
    | RemoteDeletion
    | LocalDeletion

/// The local entry's content fields. Mirrors the record's content group.
type ContentFields =
    { Text: string
      Project: string option
      Scope: string option
      EntryType: EntryType
      Source: SourceTool option
      CreatedAtMs: int64 }

/// `Tags` is sorted and unique.
type LocalEntry =
    { Content: ContentFields
      Tags: string list
      Tier: Tier
      Sticky: bool }

/// A stamp per stamped group. Tags have none: a tag change is "the local tags differ from the base's".
type GroupStamps =
    { Content: Stamp option
      Sticky: Stamp option
      Tier: Stamp option }

type MergeInput =
    { Row: RowState option
      Base: SyncRecord option
      Theirs: SyncRecord option
      Local: LocalEntry option
      Pending: GroupStamps
      Pushing: GroupStamps
      ApplyProven: bool
      HeldDelete: Direction option }

type Side =
    | Local
    | Remote

type StampAction =
    | KeepPending
    | ClearPending
    | RestampPending

type ContentApply =
    | LeaveContent
    | AdoptCreatedAt
    | TakeTheirs
    | HeldAsPinEdit

type StickyApply =
    | LeaveSticky
    | UnpinLocally
    | ReviewNewPin

type TagsOutcome =
    { Result: string list
      ApplyLocal: bool
      PushNeeded: bool }

type GroupFlags =
    { Content: bool
      Sticky: bool
      Tier: bool }

type Merged =
    { Content: ContentApply * StampAction
      Tags: TagsOutcome
      Sticky: StickyApply * StampAction
      Tier: Tier option * StampAction
      Repush: GroupFlags }

type EntryOutcome =
    | InsertFromRecord
    | MergeGroups of Merged
    | LeaveEntry
    | DeleteEntry
    | HoldDeletion of Direction
    | PushDeletion
    | Ignore
    | DropRow
    | Defer

type BaseChange =
    | KeepBase
    | BaseBecomesTheirs

type BaseUpdate =
    { Content: BaseChange
      Tags: BaseChange
      Sticky: BaseChange
      Tier: BaseChange }

type ConflictLoser =
    { Side: Side
      Text: string
      Stamp: Stamp option }

type ReviewRequest =
    | NewPin
    | PinEdit
    | Conflict of ConflictLoser

type MergeNote =
    | EditBeatsDeletion
    | PendingChangesDropped

type MergeDecision =
    { Entry: EntryOutcome
      Base: BaseUpdate
      RowState: RowState option
      Reviews: ReviewRequest list
      Notes: MergeNote list
      CloseHeldDelete: bool
      FreshContentStamp: bool }

/// The row's pending stamps are cleared for the outcomes where the entry is gone or has just been
/// inserted, and left alone where an entry stays (a held entry keeps its pending pin, tags and tier).
let clearsPending (outcome: EntryOutcome) (hasEntry: bool) : bool =
    match outcome with
    | InsertFromRecord
    | DeleteEntry
    | DropRow
    | PushDeletion
    | HoldDeletion LocalDeletion -> true
    | Ignore -> not hasEntry
    | HoldDeletion RemoteDeletion
    | LeaveEntry
    | Defer
    | MergeGroups _ -> false
```

(`Local` the case of `Side` and the label `Local` of `MergeInput` coexist; patterns on a side write `Side.Local`.)

- [ ] **Step 2: Wire the project files.** Add `<Compile Include="SyncMerge.fs" />` after `SyncValidation.fs` in `src/TotalRecall.Core/TotalRecall.Core.fsproj`, and `<Compile Include="SyncMergeTests.fs" />` after `SyncValidationTests.fs` in the test fsproj.

- [ ] **Step 3: Write the test builders and the first tests.** `SyncMergeTests.fs` starts with the module header and these shared builders (all later tasks append to this file):

```fsharp
module TotalRecall.Core.Tests.SyncMergeTests

open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge

let private m1 = (MachineId.tryCreate "0000000a").Value   // this machine
let private m2 = (MachineId.tryCreate "0000000b").Value   // another machine
let private st (n: int) (m: MachineId) : Stamp = { TimeMs = 1_700_000_000_000L + int64 n * 1000L; Counter = 0; Machine = m }
/// Our stamp at second n, and a remote stamp at second n.
let private ours n = st n m1
let private theirs n = st n m2

let private placement (tier: Tier) (tierStamp: Stamp) (sticky: bool) (stickyStamp: Stamp) : Placement =
    { Placement.Tier = tier; TierStamp = tierStamp; Sticky = sticky; StickyStamp = stickyStamp }

/// A record at stamp 1 of the other machine: warm, not sticky, no tags.
let private record (text: string) : SyncRecord =
    { Id = "3f9c2a7e"; Content = text; EntryType = Decision; Tags = []; Source = None
      CreatedAtMs = 1_700_000_000_000L; Project = None; Scope = None
      Placement = placement Warm (theirs 1) false (theirs 1); ContentStamp = theirs 1; Origin = None; WrittenBy = None }

let private fields (text: string) : ContentFields =
    { Text = text; Project = None; Scope = None; EntryType = Decision; Source = None; CreatedAtMs = 1_700_000_000_000L }

let private local (text: string) : LocalEntry =
    { LocalEntry.Content = fields text; Tags = []; Tier = Warm; Sticky = false }

let private noStamps : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

/// The neutral input: an active row, base = record = local text "base", everything at stamp 1, proven apply.
let private input : MergeInput =
    { Row = Some Active; Base = Some (record "base"); Theirs = Some (record "base"); Local = Some (local "base")
      Pending = noStamps; Pushing = noStamps; ApplyProven = true; HeldDelete = None }

let private withTheirs (f: SyncRecord -> SyncRecord) (i: MergeInput) : MergeInput = { i with Theirs = i.Theirs |> Option.map f }
let private withBase (f: SyncRecord -> SyncRecord) (i: MergeInput) : MergeInput = { i with Base = i.Base |> Option.map f }
let private withLocal (f: LocalEntry -> LocalEntry) (i: MergeInput) : MergeInput = { i with Local = i.Local |> Option.map f }
let private withText (t: string) (e: LocalEntry) : LocalEntry = { e with Content = { e.Content with Text = t } }
let private withContentStamp (s: Stamp) (r: SyncRecord) : SyncRecord = { r with ContentStamp = s }
let private withSticky (sticky: bool) (s: Stamp) (r: SyncRecord) : SyncRecord =
    { r with Placement = { r.Placement with Sticky = sticky; StickyStamp = s } }
let private withTier (tier: Tier) (s: Stamp) (r: SyncRecord) : SyncRecord =
    { r with Placement = { r.Placement with Tier = tier; TierStamp = s } }

/// The `Merged` of a decision that must be `MergeGroups`.
let private groups (d: MergeDecision) : Merged =
    match d.Entry with
    | MergeGroups m -> m
    | other -> failtestf "expected MergeGroups, got %A" other

let private applyToContent (m: Merged) = fst m.Content
let private pendingOfContent (m: Merged) = snd m.Content
```

Add one smoke test (`clearsPending` table is Task 3): `testCase "types construct" <| fun _ -> Expect.equal input.Row (Some Active) "row"`.

- [ ] **Step 4: Run and commit.** `dotnet build src/TotalRecall.Core` then the focused tests pass. Commit: `Add the input and output types for the git sync merge decisions`.

---

### Task 2: The group rules and the W4 and W5 rows

**Files:** Modify `src/TotalRecall.Core/SyncMerge.fs`, `tests/TotalRecall.Core.Tests/SyncMergeTests.fs`.

**Interfaces:** private `classify`, `stickyGroup`, `tierGroup`, `contentGroup`, `mergeTags`, `baseUpdateFor`, `mergeGroups`; public `decide` handles W4 and W5 here and returns `Defer` for every other combination (a temporary placeholder; Tasks 3 and 4 replace it).

- [ ] **Step 1: Write the failing tests** (one `testCase` per row, in `testList "SyncMerge group rules"`). Each uses `input` with `Theirs`/`Local`/`Pending`/`Pushing`/`Base` changed as described and asserts the group's `(apply, action)`, `d.Base`, `d.Reviews`, and, where stated, `Repush`. For W5 the base is `record "base"` at stamps `theirs 1`; a "newer" group means the record's group stamp is `theirs 3`.

Settled-first (spec "Settled first"):
  - own content, `p = t` : record content stamp `ours 3` with text "mine", `Pushing.Content = Some (ours 3)`, `Pending.Content = Some (ours 3)`, local text "mine" → `(LeaveContent, ClearPending)`, base content `BaseBecomesTheirs`, no review.
  - own content, newer pending: same but `Pending.Content = Some (ours 5)` → `(LeaveContent, KeepPending)`.
  - own with base newer than the record (base content stamp `theirs 9`, record `ours 3` = Pushing) → not own: classified `older`, base kept, `Repush.Content = true` (no pending).
  - a stamp carrying this machine's id that matches no `Pushing` or `Pending` → `newer`, taken (`TakeTheirs`);
  - **own push with another machine's tag change in between**: own content stamp (`Pushing.Content` = `Pending.Content` = the record's stamp), record tags `["x"]` differing from local `[]`, base tags `[]` → `(LeaveContent, ClearPending)` and `Tags.ApplyLocal = true`, `Tags.Result = ["x"]`.
  - `older` content with no pending: `(LeaveContent, KeepPending)`, base content `KeepBase`, `Repush.Content = true`; with `Pending.Content = Some (ours 4)` and local text "edit": `Repush.Content = false`, base kept.
  - `unchanged` content (t = b): `(LeaveContent, KeepPending)`, `KeepBase`, no repush.
  - same three for sticky (record `withSticky true (theirs 3)` etc.) and tier.

Content rows:
  - FC1 `sameContent`: record text "same" at `theirs 3`, local text "same", with and without `Pending.Content` → `(LeaveContent, ClearPending)` / `(LeaveContent, KeepPending)`.
  - FC2 sticky local entry, record text "remote" at `theirs 3`, `Pending.Content` unset → `(HeldAsPinEdit, ClearPending)`, `Reviews = [PinEdit]`, base content `BaseBecomesTheirs`. Also with a pending stamp older than the record's (`ours 2`): same. The same with **only `project` differing** and with **only `scope` differing** (`HeldAsPinEdit`, `[PinEdit]`); and the NC2 twins of both. Also a record that differs only in `entry_type` → `TakeTheirs` (taken directly).
  - FC3 not sticky, no pending → `(TakeTheirs, KeepPending)`.
  - FC4 pending newer than the record (`ours 5` vs `theirs 3`), local text "edit" differs from base text "base" and from the record's "remote" which differs from base → `(LeaveContent, KeepPending)`, `Reviews = [Conflict { Side = Side.Remote; Text = "remote"; Stamp = Some (theirs 3) }]`. With the record's text equal to the base's (a project-only change by them): no conflict.
  - FC5 pending older than the record (`ours 2` vs `theirs 3`), both changed → `(TakeTheirs, ClearPending)`, `Reviews = [Conflict { Side = Side.Local; Text = "edit"; Stamp = Some (ours 2) }]`.
  - **Both stamp orders, both texts changed**: exactly one `Conflict`, the loser is the older side (FC4 and FC5 above).
  - **Unpin and edit at once** (Review Focus 1): sticky local entry, record text "remote" at `theirs 3` and `withSticky false (theirs 3)`, no pending → sticky `(UnpinLocally, KeepPending)` and content `(TakeTheirs, KeepPending)`, **no** `PinEdit` in `Reviews`.
  - sticky local entry whose own edit is later (`Pending.Content = Some (ours 5)`, record `theirs 3`, text differs) → FC4: `LeaveContent`, no `PinEdit`.
  - Tag edit one side and text edit the other do not conflict: local tags `["a"]`, record text newer "remote", no local text change → `TakeTheirs`, no review, tags result keeps "a".

Content, no base (`Row = Some Active`, `Base = None`):
  - NC1 same content → `(AdoptCreatedAt, ClearPending)`; NC2 sticky local, text differs → `(HeldAsPinEdit, ClearPending)`, `[PinEdit]`; NC3 same text, record project "p" → `(TakeTheirs, ClearPending)`, no review; NC4 different text → `(TakeTheirs, ClearPending)`, `[Conflict { Side = Side.Local; Text = local text; Stamp = Pending.Content }]`; `Row = None` behaves as no base and `RowState = Some Active`; all base updates `BaseBecomesTheirs`.
  - own no base: `Pushing.Content = Some (record's stamp)` → `(LeaveContent, ClearPending)` when `p = t`.

Sticky rows FS1–FS7, NS1–NS3, NS2 restamp (one `testCase` per row, named for it):
  - FS1 `p` set (`ours 2`), local sticky = record sticky → `(LeaveSticky, ClearPending)`. FS2 `p = ours 5`, record unpin at `theirs 3` → `(LeaveSticky, KeepPending)`. FS3 `p = ours 2`, record unpin `theirs 3`, local sticky → `(UnpinLocally, ClearPending)`. FS4 `p = ours 2`, record pins `theirs 3`, local not sticky → `(ReviewNewPin, ClearPending)`, `[NewPin]`. FS5 `p` unset, equal → `(LeaveSticky, KeepPending)`. FS6 unset, record unpin, local sticky → `(UnpinLocally, KeepPending)`. FS7 unset, record pin, local not sticky → `(ReviewNewPin, KeepPending)`, `[NewPin]`.
  - Stamps, not values: pin then unpin then re-pin by the other machine (record sticky again at `theirs 5` while base is the older pin at `theirs 1`, local sticky): FS1 or FS5, nothing happens; an old offline unpin (record unpin at `theirs 0`, base pinned at `theirs 1`): `older`, `LeaveSticky`, `Repush.Sticky = true`; both sides ending at the same value: `LeaveSticky`.
  - NS1–NS3 as the spec table (`Base = None`): NS2 with `p = ours 5` vs `theirs 3` → `(LeaveSticky, KeepPending)`; with `p` unset or older → `(LeaveSticky, RestampPending)`.
  - A remote pin never pins: for every sticky-row input the apply is never anything that sets `Sticky = true` (assert `ReviewNewPin`/`LeaveSticky`/`UnpinLocally` only; there is no pinning case by construction).

Tier rows FR1–FR4 and the no-base rule:
  - FR1 record warmer (`withTier Hot (theirs 3)`, local warm) → `(Some Hot, ClearPending)`; FR2 `p` set and equal tiers → `(None, ClearPending)`; FR3 `p = ours 5`, local hot, record warm at `theirs 3` → `(None, KeepPending)`, and `p = ours 2` → `(None, RestampPending)`; FR4 unset and local warmer → `(None, KeepPending)`; a record colder than local never lowers (`None`).
  - No base: same tier → `(None, ClearPending)`; record warmer → `(Some t, ClearPending)`; local warmer with `p` unset → `(None, RestampPending)`, with `p = ours 5` vs `theirs 3` → `KeepPending`.

Tags:
  - three-way (`ApplyProven = true`, base `["a";"b"]`): local removes "a" and the record adds "c" → result `["b";"c"]`, `ApplyLocal = true`, `PushNeeded = true`; both sides remove "b" → removed; one side removes, the other keeps → removed; both add the same tag → one; result equal to local → `ApplyLocal = false`; result equal to the record's → `PushNeeded = false`.
  - unproven apply with a base (`ApplyProven = false`): union of local and record (a reset is not a removal): base `["a"]`, local `["a"]`, record `[]` → `["a"]`.
  - no base: union.
  - tags are never `own` and a tag-only change alone gives `LeaveContent`/`LeaveSticky`/`None` for the stamped groups.

Whole-entry W4/W5 plumbing: W4 (`Row = Some Active`, no base) and `Row = None` with a `Base` that must be ignored (Decided here 10) give the same decision as no base; `RowState = Some Active` only when `Row = None`; W5 `RowState = None`; `CloseHeldDelete = true` when `HeldDelete` is `Some _` (the record is present and the entry exists), `FreshContentStamp = false`, `Notes = []`.

- [ ] **Step 2: Run the tests, confirm they fail** (no `decide` yet).

- [ ] **Step 3: Write the implementation** in `SyncMerge.fs`, below `clearsPending`:

```fsharp
// ---------------------------------------------------------------------------------------------
// Stamps and classification

let private gt (a: Stamp) (b: Stamp) : bool = Stamp.compare a b > 0

/// How the record's stamp for one group relates to the row. `Own`: the record carries a stamp this
/// row proves it sent (equal to `Pushing` or `Pending`) and the base is not newer than it.
type private Class =
    | Own
    | Older
    | Unchanged
    | Newer

let private classify (t: Stamp) (b: Stamp option) (pending: Stamp option) (pushing: Stamp option) : Class =
    let baseNewer = match b with Some bs -> gt bs t | None -> false
    let provable = pushing = Some t || pending = Some t
    if provable && not baseNewer then Own
    else
        match b with
        | None -> Newer
        | Some bs ->
            let c = Stamp.compare t bs
            if c > 0 then Newer elif c < 0 then Older else Unchanged

/// One group's result: the apply, the pending-stamp action, the reviews, and whether the push step
/// re-publishes the base's value for this group (`Re`).
type private G<'a> = { Value: 'a; Action: StampAction; Re: bool; Revs: ReviewRequest list }

let private g (value: 'a) (action: StampAction) (revs: ReviewRequest list) : G<'a> =
    { Value = value; Action = action; Re = false; Revs = revs }

/// `own`, `older` and `unchanged` groups are settled before any table is consulted.
let private settled (cls: Class) (p: Stamp option) (t: Stamp) (leave: 'a) : G<'a> option =
    match cls with
    | Own -> Some (g leave (if p = Some t then ClearPending else KeepPending) [])
    | Older -> Some { Value = leave; Action = KeepPending; Re = p.IsNone; Revs = [] }
    | Unchanged -> Some (g leave KeepPending [])
    | Newer -> None

// ---------------------------------------------------------------------------------------------
// Sticky, tier, content, tags

let private stickyGroup (cls: Class) (hasBase: bool) (l: LocalEntry) (t: SyncRecord) (p: Stamp option) : G<StickyApply> =
    let ts = t.Placement.StickyStamp
    match settled cls p ts LeaveSticky with
    | Some r -> r
    | None ->
        let ls = l.Sticky
        let rs = t.Placement.Sticky
        if hasBase then
            match p with
            | Some pv ->
                if ls = rs then g LeaveSticky ClearPending []                        // FS1
                elif gt pv ts then g LeaveSticky KeepPending []                      // FS2
                elif not rs then g UnpinLocally ClearPending []                      // FS3
                else g ReviewNewPin ClearPending [ NewPin ]                          // FS4
            | None ->
                if ls = rs then g LeaveSticky KeepPending []                         // FS5
                elif not rs then g UnpinLocally KeepPending []                       // FS6
                else g ReviewNewPin KeepPending [ NewPin ]                           // FS7
        elif ls = rs then g LeaveSticky ClearPending []                              // NS1
        elif ls then                                                                 // NS2: a local pin is kept and pushed
            (match p with
             | Some pv when gt pv ts -> g LeaveSticky KeepPending []
             | _ -> g LeaveSticky RestampPending [])
        else g ReviewNewPin ClearPending [ NewPin ]                                  // NS3

let private warmer (a: Tier) (b: Tier) : bool = tierRank a < tierRank b

let private tierGroup (cls: Class) (hasBase: bool) (l: LocalEntry) (t: SyncRecord) (p: Stamp option) : G<Tier option> =
    let ts = t.Placement.TierStamp
    match settled cls p ts None with
    | Some r -> r
    | None ->
        let lt = l.Tier
        let rt = t.Placement.Tier
        let localWins = match p with Some pv when gt pv ts -> KeepPending | _ -> RestampPending
        if hasBase then
            if warmer rt lt then g (Some rt) ClearPending []                         // FR1
            elif p.IsSome && lt = rt then g None ClearPending []                     // FR2
            elif p.IsSome && warmer lt rt then g None localWins []                   // FR3
            else g None KeepPending []                                               // FR4
        elif lt = rt then g None ClearPending []
        elif warmer rt lt then g (Some rt) ClearPending []
        else g None localWins []

let private sameContent (l: ContentFields) (t: SyncRecord) : bool =
    l.Text = t.Content && l.Project = t.Project && l.Scope = t.Scope && l.EntryType = t.EntryType

/// A sticky entry holds a record's content as a pin-edit when text, project or scope differ.
let private pinDiffers (l: ContentFields) (t: SyncRecord) : bool =
    l.Text <> t.Content || l.Project <> t.Project || l.Scope <> t.Scope

let private contentGroup
    (cls: Class) (b: SyncRecord option) (effSticky: bool) (l: LocalEntry) (t: SyncRecord) (p: Stamp option) : G<ContentApply> =
    let ts = t.ContentStamp
    match settled cls p ts LeaveContent with
    | Some r -> r
    | None ->
        match b with
        | Some bb ->
            let pendingText = p.IsSome && l.Content.Text <> bb.Content
            let both = pendingText && t.Content <> bb.Content && t.Content <> l.Content.Text
            if sameContent l.Content t then g LeaveContent (if p.IsSome then ClearPending else KeepPending) []      // FC1
            elif effSticky && pinDiffers l.Content t && (p.IsNone || gt ts p.Value) then
                g HeldAsPinEdit ClearPending [ PinEdit ]                                                           // FC2
            elif p.IsNone then g TakeTheirs KeepPending []                                                         // FC3
            elif gt p.Value ts then                                                                                // FC4
                g LeaveContent KeepPending
                    (if both then [ Conflict { Side = Remote; Text = t.Content; Stamp = Some ts } ] else [])
            else                                                                                                   // FC5
                g TakeTheirs ClearPending
                    (if both then [ Conflict { Side = Side.Local; Text = l.Content.Text; Stamp = p } ] else [])
        | None ->
            if sameContent l.Content t then g AdoptCreatedAt ClearPending []                                       // NC1
            elif effSticky && pinDiffers l.Content t then g HeldAsPinEdit ClearPending [ PinEdit ]                  // NC2
            elif l.Content.Text = t.Content then g TakeTheirs ClearPending []                                      // NC3
            else g TakeTheirs ClearPending [ Conflict { Side = Side.Local; Text = l.Content.Text; Stamp = p } ]    // NC4

/// Three-way set merge when there is a base and the apply is proven; otherwise the union (a reset
/// pull would read the three-way merge as a removal).
let private mergeTags (proven: bool) (b: SyncRecord option) (l: string list) (t: string list) : TagsOutcome =
    let ls = Set.ofList l
    let ts = Set.ofList t
    let result =
        match b with
        | Some bb when proven ->
            let bs = Set.ofList bb.Tags
            Set.unionMany [ bs; ls; ts ]
            |> Set.filter (fun e -> if bs.Contains e then ls.Contains e && ts.Contains e else ls.Contains e || ts.Contains e)
            |> Set.toList
        | _ -> Set.union ls ts |> Set.toList
    let result = normaliseTags result
    { Result = result; ApplyLocal = result <> normaliseTags l; PushNeeded = result <> normaliseTags t }

let private allTheirs : BaseUpdate =
    { BaseUpdate.Content = BaseBecomesTheirs; Tags = BaseBecomesTheirs; Sticky = BaseBecomesTheirs; Tier = BaseBecomesTheirs }

let private keepAll : BaseUpdate =
    { BaseUpdate.Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }

let private classOf (input: MergeInput) (b: SyncRecord option) (t: SyncRecord) =
    let c = classify t.ContentStamp (b |> Option.map (fun x -> x.ContentStamp)) input.Pending.Content input.Pushing.Content
    let s = classify t.Placement.StickyStamp (b |> Option.map (fun x -> x.Placement.StickyStamp)) input.Pending.Sticky input.Pushing.Sticky
    let r = classify t.Placement.TierStamp (b |> Option.map (fun x -> x.Placement.TierStamp)) input.Pending.Tier input.Pushing.Tier
    c, s, r

/// Per group: an `older` or `unchanged` group keeps its base; `own` and `newer` take the record's. Tags always do.
let private baseUpdateFor (input: MergeInput) (b: SyncRecord option) (t: SyncRecord) : BaseUpdate =
    match b with
    | None -> allTheirs
    | Some _ ->
        let c, s, r = classOf input b t
        let f cls = match cls with Own | Newer -> BaseBecomesTheirs | Older | Unchanged -> KeepBase
        { BaseUpdate.Content = f c; Tags = BaseBecomesTheirs; Sticky = f s; Tier = f r }

let private mergeGroups (input: MergeInput) (b: SyncRecord option) (l: LocalEntry) (t: SyncRecord) : EntryOutcome * ReviewRequest list =
    let cc, cs, cr = classOf input b t
    let hasBase = b.IsSome
    let sticky = stickyGroup cs hasBase l t input.Pending.Sticky          // decided first
    let effSticky = l.Sticky && sticky.Value <> UnpinLocally
    let content = contentGroup cc b effSticky l t input.Pending.Content
    let tier = tierGroup cr hasBase l t input.Pending.Tier
    let tags = mergeTags input.ApplyProven b l.Tags t.Tags
    let merged : Merged =
        { Merged.Content = (content.Value, content.Action)
          Tags = tags
          Sticky = (sticky.Value, sticky.Action)
          Tier = (tier.Value, tier.Action)
          Repush = { GroupFlags.Content = content.Re; Sticky = sticky.Re; Tier = tier.Re } }
    MergeGroups merged, sticky.Revs @ content.Revs
```

and the placeholder `decide` (Task 3 replaces it):

```fsharp
// ---------------------------------------------------------------------------------------------
// The whole-entry table

let private baseDecision : MergeDecision =
    { Entry = Ignore; Base = keepAll; RowState = None; Reviews = []; Notes = []; CloseHeldDelete = false; FreshContentStamp = false }

let decide (input: MergeInput) : MergeDecision =
    let b = if input.Row.IsSome then input.Base else None          // Decided here 10
    match input.Row, input.Theirs, input.Local with
    | (None | Some Active), Some t, Some l ->                       // W4 and W5
        let outcome, reviews = mergeGroups input b l t
        { baseDecision with
            Entry = outcome
            Base = baseUpdateFor input b t
            RowState = (if input.Row.IsNone then Some Active else None)
            Reviews = reviews
            CloseHeldDelete = input.HeldDelete.IsSome }
    | _ -> { baseDecision with Entry = Defer }                       // placeholder until Task 3
```

- [ ] **Step 4: Run the focused tests until green.** Then run the whole Core test project. Dispatch the reviewer for this task (adversarial; the task diff). Fix findings and commit: `Add the group rules and the field-wise and no-base merge for git sync`.

---

### Task 3: The whole-entry rows without ordered lists, and held-delete closing

**Files:** Modify `SyncMerge.fs` (replace the placeholder `decide`), `SyncMergeTests.fs`.

**Rows:** W1, W2, W3, W7, W9, W10, W11, W12, W13, W14 (checked first), and the `finish` step that sets `CloseHeldDelete`. W6 and W8 call two placeholder functions that return `baseDecision` (an `Ignore`) until Task 4 replaces them.

- [ ] **Step 1: Write the failing tests** (`testList "SyncMerge whole-entry rows"`), each in a proven and an unproven apply unless it is independent of it:
  - W1 record, no entry, no row → `InsertFromRecord`, `RowState = Some Active`, base all `BaseBecomesTheirs`, `Reviews = []`; record sticky → `[NewPin]`; `Row = None` with a stale `Base` supplied: ignored (still W1).
  - W2 `Left` row, record, no entry → `Ignore`; also with a base.
  - W3 `Forgotten`, base `record "base"` at `theirs 1`, record text "remote" at `theirs 3` → `InsertFromRecord`, `RowState = Some Active`, `Notes = [EditBeatsDeletion]`; record only tier/tags/pin changed (text equal) → `Ignore`; record text older (`theirs 0`) → `Ignore`; record text newer but its stamp is `own` (Pushing) → `Ignore`; text equal to the base with a newer stamp → `Ignore`; `Forgotten` with no base → `Ignore`.
  - W7 absent record, entry, no row → `LeaveEntry` (base kept, `RowState = None`); also `Row = Some Active` with no base.
  - W9 `Active`, no base, record present, no entry: `Pushing.Content = Some (record's stamp)` → `PushDeletion`, base all `BaseBecomesTheirs`, `RowState = None`; foreign content stamp → `Ignore`, `RowState = Some Forgotten`, base all `BaseBecomesTheirs`; **own tier stamp but a foreign text stamp** (`Pushing.Tier = Some (record's tier stamp)`, `Pushing.Content = None`) → `Ignore`/`Forgotten` (Review Focus 3); `Pushing.Content` set but to another stamp → `Ignore`.
  - **Chain** (a base-less deleted row with a foreign record, not resurrected until a newer, different text): feed W9 (foreign content stamp) → `Ignore`, `Some Forgotten`, base := theirs; build the next input from that (`Row = Some Forgotten`, `Base = Some` the record) → W3 `Ignore`; a record with text "newer" at a higher stamp → `InsertFromRecord`.
  - W10 `Active` and `Forgotten`, with base, no record, no entry → `Ignore`, `RowState = Some Forgotten`, base kept.
  - W11 `Active`, no base, no record, no entry → `DropRow`. W12 `Forgotten` no base → `Ignore`; `Left` (with and without base) → `Ignore`. W13 no row → `Ignore`.
  - W14 `Forgotten` or `Left` row with an entry, record present and absent → `Defer`, `CloseHeldDelete = false`, base kept, `RowState = None`; `Defer` wins over a `HeldDelete`.
  - Held-delete closing: `RemoteDeletion` item with the record present and the entry present (W4/W5) → closed; with the entry gone → closed; with record absent and entry present (W7) → not closed; `LocalDeletion` item with the entry present → closed; with the record absent and the entry gone (W10) → closed; with the record present and the entry gone, `Ignore` (W9) → not closed (`HeldDelete = Some LocalDeletion`); `HeldDelete = None` → never closed.
  - `clearsPending` table: `InsertFromRecord`, `DeleteEntry`, `DropRow`, `PushDeletion`, `HoldDeletion LocalDeletion` → true; `Ignore` true without an entry and false with one; `HoldDeletion RemoteDeletion`, `LeaveEntry`, `Defer` and a `MergeGroups` → false.
  - Totality skeleton (extended in Task 5): `decide` of an input with every `Row` returns a decision.

- [ ] **Step 2: Run, confirm failures.**

- [ ] **Step 3: Replace the placeholder `decide`** with the full dispatch (W6 and W8 call `absentRecordEntryPresent` and `presentRecordEntryGone`, which are placeholders returning `{ baseDecision with Entry = Defer }` until Task 4):

```fsharp
let private insertFrom (t: SyncRecord) (editBeatsDeletion: bool) : MergeDecision =
    { baseDecision with
        Entry = InsertFromRecord
        Base = allTheirs
        RowState = Some Active
        Reviews = (if t.Placement.Sticky then [ NewPin ] else [])
        Notes = (if editBeatsDeletion then [ EditBeatsDeletion ] else []) }

/// "Text newer": the record's content stamp is greater than the base's, its text differs from the
/// base's, and the stamp is not one this row proves it sent.
let private textNewer (input: MergeInput) (b: SyncRecord) (t: SyncRecord) : bool =
    let own = classify t.ContentStamp (Some b.ContentStamp) input.Pending.Content input.Pushing.Content = Own
    gt t.ContentStamp b.ContentStamp && t.Content <> b.Content && not own

let private absentRecordEntryPresent (input: MergeInput) (bb: SyncRecord) (l: LocalEntry) : MergeDecision * bool =
    baseDecision, false                                                  // Task 4

let private presentRecordEntryGone (input: MergeInput) (bb: SyncRecord) (t: SyncRecord) : MergeDecision =
    baseDecision                                                         // Task 4

/// The held-delete item closes when the situation it was opened for is over.
let private finish (input: MergeInput) (forceClose: bool) (d: MergeDecision) : MergeDecision =
    let close =
        match d.Entry, input.HeldDelete with
        | Defer, _ -> false
        | _, None -> false
        | _, _ when forceClose -> true
        | _, Some RemoteDeletion -> input.Theirs.IsSome || input.Local.IsNone
        | e, Some LocalDeletion -> input.Local.IsSome || input.Theirs.IsNone || e = InsertFromRecord
    { d with CloseHeldDelete = close }

let decide (input: MergeInput) : MergeDecision =
    let b = if input.Row.IsSome then input.Base else None          // Decided here 10
    let d, forceClose =
        match input.Row, input.Theirs, input.Local with
        | Some (Forgotten | Left), _, Some _ -> { baseDecision with Entry = Defer }, false          // W14
        | None, Some t, None -> insertFrom t false, false                                           // W1
        | Some Left, Some _, None -> baseDecision, false                                            // W2
        | Some Forgotten, Some t, None ->                                                           // W3
            match b with
            | Some bb when textNewer input bb t -> insertFrom t true, false
            | _ -> baseDecision, false
        | (None | Some Active), Some t, Some l ->                                                   // W4 and W5
            let outcome, reviews = mergeGroups input b l t
            { baseDecision with
                Entry = outcome
                Base = baseUpdateFor input b t
                RowState = (if input.Row.IsNone then Some Active else None)
                Reviews = reviews }, false
        | Some Active, Some t, None ->
            match b with
            | Some bb -> presentRecordEntryGone input bb t, false                                   // W8
            | None ->                                                                               // W9
                if input.Pushing.Content = Some t.ContentStamp then { baseDecision with Entry = PushDeletion; Base = allTheirs }, false
                else { baseDecision with Entry = Ignore; Base = allTheirs; RowState = Some Forgotten }, false
        | (None | Some Active), None, Some l ->
            match b with
            | Some bb -> absentRecordEntryPresent input bb l                                        // W6
            | None -> { baseDecision with Entry = LeaveEntry }, false                               // W7
        | Some Active, None, None ->
            match b with
            | Some _ -> { baseDecision with Entry = Ignore; RowState = Some Forgotten }, false     // W10
            | None -> { baseDecision with Entry = DropRow }, false                                  // W11
        | Some Forgotten, None, None ->
            match b with
            | Some _ -> { baseDecision with Entry = Ignore; RowState = Some Forgotten }, false      // W10
            | None -> baseDecision, false                                                           // W12
        | Some Left, None, None -> baseDecision, false                                              // W12
        | None, None, None -> baseDecision, false                                                   // W13
    finish input forceClose d
```

- [ ] **Step 4: Run until green;** run the whole Core project. Reviewer gate for the task. Commit: `Add the whole-entry merge rows and held-delete closing for git sync`.

---

### Task 4: The ordered lists W6 and W8

**Files:** Modify `SyncMerge.fs` (replace the two placeholders), `SyncMergeTests.fs`.

- [ ] **Step 1: Write the failing tests** (`testList "SyncMerge deletion rows"`):
  - **W6 (absent record, entry present, `Active` with base).** Quote each umbrella step in the test name.
    - W6.1 pending change of the text (`Pending.Content = Some (ours 4)`, local text "edit" ≠ base text "base") → `LeaveEntry`, `FreshContentStamp = true`, `Notes = [EditBeatsDeletion]`, base kept, in proven and unproven applies, for a sticky local entry, with a `RemoteDeletion` item open (`CloseHeldDelete = true`) and with a `LocalDeletion` item open (`CloseHeldDelete = true`).
    - **A held pin-edit is not a pending change** (Review Focus 2): `Pending.Content` unset, local text "pin text" ≠ base text "remote" (the base became the remote text when the edit was held) → not W6.1: a sticky entry gives `HoldDeletion RemoteDeletion`, `FreshContentStamp = false`. Also `Pending.Content` set with local text equal to the base text → not W6.1 (a project-only edit).
    - W6.2 sticky entry, no text change → `HoldDeletion RemoteDeletion`; `Pending.Sticky` set on a non-sticky entry (a pending pin or unpin) → `HoldDeletion RemoteDeletion`, `FreshContentStamp = false`; a `RemoteDeletion` item already open → `LeaveEntry`; a `LocalDeletion` item open → `HoldDeletion RemoteDeletion` with `CloseHeldDelete = true` (replace).
    - W6.3 `RemoteDeletion` item open, not sticky, nothing pending → `LeaveEntry`, `CloseHeldDelete = false`.
    - W6.4 proven, nothing special → `DeleteEntry`, `RowState = Some Forgotten`, base kept, `Notes = []`; with a pending tag change (local tags differ from base tags), with `Pending.Tier` set, with a project change → `Notes = [PendingChangesDropped]`; a colder local tier alone → no note; a `Pending.Content` set with equal text and equal other fields → no note.
    - W6.5 unproven, nothing special → `HoldDeletion RemoteDeletion`.
    - Pending stamps survive a held entry: `clearsPending` false for the W6.2 and W6.5 outcomes and `DeleteEntry` true.
    - **Deletion needs proof** (spot cases, the property is Task 5): `DeleteEntry` never appears for a sticky entry, an unproven apply, a pending sticky stamp, a pending text change, a `RemoteDeletion` item open.
  - **W8 (present record, entry gone, `Active` with base).**
    - W8.1 `edit` (record text "remote" at `theirs 3` vs base "base") → `InsertFromRecord`, `RowState = Some Active`, `Notes = [EditBeatsDeletion]`, base all `BaseBecomesTheirs`; a sticky record adds `[NewPin]`; with a `RemoteDeletion` or `LocalDeletion` item open → `CloseHeldDelete = true`.
    - An unchanged record, a record changed only in tier, tags, sticky, project, scope or entry type, an older record, and a record whose text stamp is `own` fall through: proven → `PushDeletion`; unproven → `HoldDeletion LocalDeletion`; `LocalDeletion` item open → `Ignore` with the item staying (`CloseHeldDelete = false`); a `RemoteDeletion` item open with the entry gone → `CloseHeldDelete = true` alongside `PushDeletion` or `HoldDeletion LocalDeletion` (replace).
    - Base per group in W8: an older tier group keeps its base (`BaseUpdate.Tier = KeepBase`), a newer sticky group `BaseBecomesTheirs`, tags always `BaseBecomesTheirs`.
    - **A deletion pushed first and an edit written back converge** (two-machine test, each step through `decide`): machine A (entry deleted locally, base text "base"), machine B (pending text edit "edit", record absent after A's push). B at W6.1 → `FreshContentStamp`; the record then returns with text "edit" at a stamp above the base (use `theirs 5`); A at W8.1 → `InsertFromRecord`. Reverse order: the edit lands first (A sees the record with new text): W8.1; then A's deletion is gone, B sees its record present: no deletion. Both orders end with the memory on both machines.
    - A reset to a pre-edit commit does not re-insert a forgotten memory with older text (W3 with an older text stamp → `Ignore`).
    - A machine-driven promotion does not undo a forget: W3 and W8 with a tier-only change → `Ignore` / no insert; W6.4's note counts it as pending.

- [ ] **Step 2: Run, confirm failures.**

- [ ] **Step 3: Replace the two placeholders:**

```fsharp
let private absentRecordEntryPresent (input: MergeInput) (bb: SyncRecord) (l: LocalEntry) : MergeDecision * bool =
    let pendingText = input.Pending.Content.IsSome && l.Content.Text <> bb.Content
    let sticky = l.Sticky || input.Pending.Sticky.IsSome
    let remoteOpen = input.HeldDelete = Some RemoteDeletion
    if pendingText then                                                                     // W6.1: an edit beats the deletion
        { baseDecision with Entry = LeaveEntry; FreshContentStamp = true; Notes = [ EditBeatsDeletion ] }, true
    elif sticky then                                                                        // W6.2
        { baseDecision with Entry = (if remoteOpen then LeaveEntry else HoldDeletion RemoteDeletion) }, false
    elif remoteOpen then { baseDecision with Entry = LeaveEntry }, false                    // W6.3
    elif input.ApplyProven then                                                             // W6.4
        let dropped =
            normaliseTags l.Tags <> normaliseTags bb.Tags
            || input.Pending.Tier.IsSome
            || l.Content.Project <> bb.Project
            || l.Content.Scope <> bb.Scope
            || l.Content.EntryType <> bb.EntryType
        { baseDecision with
            Entry = DeleteEntry
            RowState = Some Forgotten
            Notes = (if dropped then [ PendingChangesDropped ] else []) }, false
    else { baseDecision with Entry = HoldDeletion RemoteDeletion }, false                   // W6.5

let private presentRecordEntryGone (input: MergeInput) (bb: SyncRecord) (t: SyncRecord) : MergeDecision =
    let bu = baseUpdateFor input (Some bb) t
    if textNewer input bb t then insertFrom t true                                          // W8.1
    elif input.HeldDelete = Some LocalDeletion then { baseDecision with Entry = Ignore; Base = bu }      // W8.2
    elif input.ApplyProven then { baseDecision with Entry = PushDeletion; Base = bu }                    // W8.3
    else { baseDecision with Entry = HoldDeletion LocalDeletion; Base = bu }                             // W8.4
```

- [ ] **Step 4: Run until green;** the whole Core project. Reviewer gate. Commit: `Add the deletion rows W6 and W8 of the git sync merge`.

---

### Task 5: Properties, the reference interpreter, totality

**Files:** Modify `SyncMergeTests.fs` only (a defect found here is fixed in `SyncMerge.fs` with its own test in the earlier lists).

- [ ] **Step 1: Write the generators.** Small pools so stamps tie, are older, newer and own at useful rates:
  - stamps from `{ours 0..5, theirs 0..5}` (`Gen.elements`), record texts from `["base"; "a"; "b"]`, tags subsets of `["x"; "y"; "z"]`, tiers, sticky booleans, project from `[None; Some "p"]`;
  - `genRecord`, `genEntry`, `genPending` (each stamp `None` or from the pool), `genInput` over `Row` (`None`, `Some Active`, `Some Forgotten`, `Some Left`), `Base`/`Theirs`/`Local` as `None` or generated (Theirs with the same id), `ApplyProven`, `HeldDelete`;
  - `Prop.forAll (Arb.fromGen genInput)` for every property; FsCheck config default (100 tests) through `testPropertyWithConfig { FsCheckConfig.defaultConfig with maxTest = 2000 }`.

- [ ] **Step 2: Write the reference interpreter** (test code): `apply (i: MergeInput) (d: MergeDecision) : MergeInput` models the entry, the row and the held item:
  - entry: `InsertFromRecord` → `Local = Some` (the record's content, tags, tier, `Sticky = false`); `MergeGroups m` → content `TakeTheirs`/`AdoptCreatedAt` take the record's content fields / `CreatedAtMs`, `HeldAsPinEdit`/`LeaveContent` keep; tags `ApplyLocal` → `Result`; sticky `UnpinLocally` → `false`; tier `Some t` → `t`; `DeleteEntry` → `Local = None`; `DropRow` → `Row = None`, `Base = None`, `Local = None`; **`PushDeletion` models the push landing**: `Theirs = None` and `Row = Some Forgotten` (the push step removes the file; W9's `base := theirs` followed by a second call with the same record would otherwise reach W8.4 in an unproven apply, which is the umbrella's rule for a deletion that is not provably ours, not a defect). That `PushDeletion` repeats until the push lands is a direct test, not part of the property;
  - row: `RowState = Some s` → `Row = Some s`; a `MergeGroups` with a `RestampPending` gives the pending stamp `{ record stamp with Counter = +1 }`; `ClearPending` → `None`; if `clearsPending d.Entry (i.Local.IsSome)` all three pending stamps are cleared;
  - base: composed group by group per `d.Base` (the spec's composition rule: `Id`, `Origin`, `WrittenBy` from the record; a group marked `BaseBecomesTheirs` takes the record's values, `KeepBase` keeps the old base's; with no old base, the record);
  - held item: `CloseHeldDelete` → `None`; close first (`CloseHeldDelete` → none), then a `HoldDeletion x` opens `Some x` if no item is open (so a decision that closes one item and holds the other direction replaces it); an open item stays otherwise.

- [ ] **Step 3: Write the properties** (a direct test beside them: for a proven apply `decide i` for a `PushDeletion` input returns `PushDeletion` again until the record goes away) (`testList "SyncMerge properties"`):
  1. **Total and deterministic.** `decide i = decide i` and no exception for every generated input; plus an **exhaustive** test over `Row` (4) × record (none, same as base, newer text) × entry (none, plain, sticky) × base (none, present) × proven × `HeldDelete` (3) × `Pending.Sticky` (2) that the function returns a decision for every combination.
  2. **Deletion needs proof.** `DeleteEntry` implies: entry present, `Row = Some Active`, base present, `Theirs = None`, entry not sticky, `Pending.Sticky = None`, no pending text change, `ApplyProven`, `HeldDelete <> Some RemoteDeletion`.
  3. **A pin is never taken from a record.** Asserted on the decision, not the interpreter: for every input the sticky apply of a `MergeGroups` is one of `LeaveSticky`, `UnpinLocally`, `ReviewNewPin` (the type has no pinning case; the test pins that no other case is added), `ReviewNewPin` occurs exactly with `NewPin` in `Reviews`, a non-sticky local entry never gets `UnpinLocally`, and an `InsertFromRecord` of a sticky record carries `[NewPin]` with the interpreter's inserted entry non-sticky.
  4. **Never from an older stamp.** For every stamped group whose classification is `older`: the decision leaves that group's apply as leave, `KeepPending`, base `KeepBase`, and `Repush` is set exactly when the group has no pending stamp (assert on `MergeGroups`; the classification is recomputed in the test from the same rule: not own, effective base present, record stamp below the base's; the effective base is `if Row.IsSome then Base else None` because `decide` ignores a `Base` when `Row` is `None`).
  5. **Idempotent** (spec property over the interpreter): for every input, `i2 = apply i (decide i)`; then `decide i2` has an entry in `{ LeaveEntry; Ignore; PushDeletion; Defer }` or a `MergeGroups` whose groups are all neutral (`LeaveContent`, `LeaveSticky`, `None`, `ApplyLocal = false`, every action `KeepPending`; `Repush` and `PushNeeded` may repeat), `Reviews = []`, and `Notes = []` unless `FreshContentStamp` (then `[EditBeatsDeletion]` only). Inputs that cannot occur in a live store are excluded by a generator filter stated in the test: `Row = None` with a base, a `Row` of `Forgotten`/`Left` with an entry.
  6. **Order of arrival for content** (Interpretation 5): with `Pushing` and `Pending` empty and V1 and V2 carrying the same `Placement.Sticky` and `StickyStamp` (a coherent own state needs the local entry to carry the own text, which a generator cannot assume; and a pin followed by an unpin changes whether the entry holds or takes text, which the spec's property excludes for sticky; the property's name says both), from the same input with an entry, a base and two record versions V1, V2 with different content stamps: `apply (decide with V1)` then V2 versus V2 then V1 end with the same local text and the same `Project` (the only other content field the generator varies; `source` and `created_at` follow the winner and are not compared). **Tier is monotone** in either order, and **order independent when V1 and V2 have different `Placement.TierStamp`s and the one with the greater tier stamp is not colder** (the stamp is the tier stamp, not the content stamp).
  7. **A decision never lowers a tier:** when `Local` was present before the decision, after `apply` the local tier rank is not greater than before.

- [ ] **Step 4: Run, fix.** A property that fails is analysed against the spec before the code or the property changes; if the spec is wrong the finding is recorded in the task's report and in the PR (the spec is read-only here). Reviewer gate. Commit: `Add the properties and the totality tests for the git sync merge`.

---

### Task 6: Documentation, AOT and the full verification

**Files:** Modify `src/TotalRecall.Core/AGENTS.md`.

- [ ] **Step 1:** add one table row for `SyncMerge.fs`: `TotalRecall.Core.SyncMerge` — "Git sync: `decide : MergeInput -> MergeDecision`, the pure per-entry merge (whole-entry, no-base and field-wise rules, own-push, deletion rows); types for the input, the decision, review requests and notes; `clearsPending`". No CHANGELOG change (nothing user-visible).
- [ ] **Step 2: AOT.** `dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true` shows no new warning (compare the IL warning count with `main`'s).
- [ ] **Step 3: Verify.** `node skills/develop/scripts/profile.js verify-files .github/develop.md <dir>`, then each block with `sh -e` from the worktree (needs `npm ci --ignore-scripts` and `sh scripts/fetch-bge-small.sh` first for the ONNX tests; three integration tests that need a live Cortex server fail without one, environmental).
- [ ] **Step 4:** record each acceptance criterion's result for the PR body. Commit: `Document the git sync merge decisions in the Core guide`.
