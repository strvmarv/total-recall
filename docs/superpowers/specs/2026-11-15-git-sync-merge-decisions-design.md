# Git sync, milestone 0b-2: the pure merge decisions

Date: 2026-11-15. Milestone 0b-2 of the git sync feature tracked in #93 (0a is #100, 0b-1 is #105). The umbrella
design is `2026-11-15-git-sync-design.md` (see "What syncs", "Ordering: a hybrid logical clock", "The sync
cycle" step 3, "Deletions", "Pin review" and "Conflict review"); the record model, stamps and clock this builds
on are `2026-11-15-git-sync-record-format-design.md`. The umbrella's tables are the source of the rules; this
document fixes the **representation** (the input and output types), the **order of evaluation**, and the few
places where the umbrella's tables leave a case open (listed under "Decided here"). Milestone 0b is split in
three; this is the second: 0b-3 (migration 18, the sync tables and `GitSyncConfig`) is independent of it.

## Problem

When a pulled record and the local memory of the same id differ, the cycle must decide what happens to each part
of it: whose text wins, what happens to the tags, to the pin, to the tier, and what a missing file means. Those
decisions have to be the same on every machine whatever the order in which changes arrive, must never delete
without proof, and must never turn a pin on without the user. They are the heart of the feature and the part
where a mistake is permanent data loss, so they are written once, as a pure function over plain values, and
tested row by row before any code that touches git or the database uses them.

## Goals

1. One pure function `decide : MergeInput -> MergeDecision` in `TotalRecall.Core` that answers, for one entry in
   one cycle, every row of the umbrella's **whole-entry table**, **no-base table** and **field-wise table**,
   the **own-push rule**, and the deletion rows of "Deletions" that are decisions about one entry.
2. The decision is **data**: what to do to the local entry (per group), what to do to the row's base and
   pending stamps, which review items to open, which notes to show, and which pushes are implied. The caller
   (milestone 1b) performs it; `decide` performs nothing.
3. Totality and determinism: every input, including an inconsistent snapshot, gives a decision and never an
   exception; the same input always gives the same decision.
4. The safety properties the umbrella states are properties of the function and are tested as such: a deletion
   only with proof; a pin never taken from a record; a group never taken from an older stamp; applying a record
   twice changes nothing.
5. No change to existing behavior. Nothing calls it yet.

## Non-goals

- Reading the clone, the scan, stamping, the push and the writer, the lock, the apply transaction and its
  re-read: milestone 1b-i. The delete guard's counting and the application of held items: 1b-ii.
- The `git_sync_*` tables and `GitSyncConfig`: 0b-3 (they will hold what `MergeInput` carries).
- The life cycle of review items (`approve`, `decline`, the transition table, `record_hash`): 1b-iii. This
  milestone only says **which items a decision opens** and with what payload.
- Alias detection (an entry whose record is a legacy alias of another): 1b-iii and 5. The caller intercepts an
  alias before it calls `decide`.
- Hot-budget and content-cap placement ("a record whose text exceeds a cap is applied at a colder tier"), the
  embedding, and the per-entry budget and ordering: 1b-i. `decide` says "insert at the record's tier".
- Hashes (`record_hash`, scanned hashes) and the deterministic-id import logic.

## Decided here

The umbrella leaves these open; the spec fixes them so the implementation does not guess. Each is small and none
changes a rule the umbrella states.

1. **`changed` is stamp comparison**, per group, in three states: `newer` (the record's stamp is greater than the
   base's), `older` (less) and `unchanged` (equal). A group is **never taken from an `older` record**: the local
   group is kept and the group is marked for re-push from the base (the umbrella places this marker under
   unproven applies; a proven apply cannot produce it, because every push is a fast-forward, so the function
   treats both the same).
2. **A group is `own`** (the record carries a stamp this row can prove it sent) when the record's stamp equals
   the row's `Pushing` stamp or its `Pending` stamp for that group and the base, if there is one, is not newer
   than the record. An `own` group is settled before any table is consulted: nothing is applied for it, and its
   pending stamp is cleared only if it equals the record's. Tags have no stamp and are never `own`.
3. **`sameContent`** compares text, `project`, `scope` and `entry_type`. `source` and `created_at` follow
   whichever side wins and are not compared.
4. **"Both sides changed the text"** means the local text and the record's text each differ from the base's text
   and from each other.
5. **A sticky local entry holds remote content as a `pin-edit`** when the record's text, `project` or `scope`
   differs from the local entry's; a difference only in `entry_type` or `source` is taken directly. When the
   local side wins (its pending stamp is greater) nothing is held: the local pin text is what will be pushed.
6. **Pending text change** (the deletion rows) is derived: the entry's text differs from the base's text. A
   pending tag, sticky, tier, project or scope change does not count.
7. **A snapshot that cannot occur** (the scan re-activates a `forgotten` or `left` row whose entry exists) gives
   `Defer`: leave this entry for the next cycle and do not advance `L`, the same outcome the umbrella uses for a
   snapshot that changed under the transaction.
8. **A base-less row with no entry** is dropped whenever the record is absent, and also when the record is
   present but is not provably this machine's own push; the umbrella drops it only for "empty `pushing_stamps`".

## Design

### Where it lives

`src/TotalRecall.Core/SyncMerge.fs` (module `TotalRecall.Core.SyncMerge`), after `SyncValidation.fs`, plus
`SyncMergeGroups.fs` for the per-group rules if one file grows past a comfortable size. Pure; it uses the
0b-1 types (`SyncStamp.Stamp`, `SyncRecord.SyncRecord`, `Placement`, `Tier`, `EntryType`, `SourceTool`).

### Input

```fsharp
type RowState = Active | Forgotten | Left

/// The local entry's content fields. Mirrors the record's content group.
type ContentFields =
    { Text: string; Project: string option; Scope: string option
      EntryType: EntryType; Source: SourceTool option; CreatedAtMs: int64 }

type LocalEntry =
    { Content: ContentFields; Tags: string list; Tier: Tier; Sticky: bool }   // Tags sorted and unique

/// A stamp per stamped group. Tags have none: a tag change is "the local tags differ from the base's".
type GroupStamps = { Content: Stamp option; Sticky: Stamp option; Tier: Stamp option }

type MergeInput =
    { Row: RowState option          // None: no row for this id
      Base: SyncRecord option       // the last reconciled record (base_json)
      Theirs: SyncRecord option     // the record at R; None: its path is absent from R's tree
      Local: LocalEntry option      // None: no memory with this id (a local deletion, or never existed)
      Pending: GroupStamps          // persisted stamps of locally changed groups
      Pushing: GroupStamps          // pushing_stamps: what this row proves it sent
      ApplyProven: bool
      HeldDeleteOpen: bool }        // a held-delete item is open for this id
```

`Row = None` with `Local = Some _` is a new local entry (the scan creates its row in the same cycle; the function
accepts both). Every `Stamp` is in its domain (0b-1).

### Output

```fsharp
type Direction = RemoteDeletion | LocalDeletion

/// What to do with a group's pending stamp after the decision.
type StampAction =
    | KeepPending       // leave it as it is (including: none)
    | ClearPending      // the change is already in the repository, was superseded, or converged
    | RestampPending    // issue a new stamp above theirs' stamp for the group and keep it pending

type ContentApply =
    | LeaveContent
    | AdoptCreatedAt    // keep the local fields, take the record's created_at (no-base, same content)
    | TakeTheirs        // apply the record's text, project, scope, entry type, source and created_at
    | HeldAsPinEdit     // sticky local entry: the record's content is held; the local text stays active

type StickyApply =
    | LeaveSticky
    | UnpinLocally      // a remote unpin
    | ReviewNewPin      // the record pins and the local entry is not sticky: a new-pin item; entry stays unpinned

type TagsOutcome =
    { Result: string list       // the merged set, sorted and unique
      ApplyLocal: bool          // Result differs from the local tags
      PushNeeded: bool }        // Result differs from the record's tags

type GroupFlags = { Content: bool; Sticky: bool; Tier: bool }   // tags are never re-pushed from a base

type Merged =
    { Content: ContentApply * StampAction
      Tags: TagsOutcome
      Sticky: StickyApply * StampAction
      Tier: Tier option * StampAction     // Some t: promote the local entry to t (never colder)
      Repush: GroupFlags }                // groups the record regressed (older stamp): re-push the base's value and stamp

type EntryOutcome =
    | InsertFromRecord            // insert theirs non-sticky at the record's tier
    | MergeGroups of Merged       // the entry exists and so does the record
    | LeaveEntry                  // change nothing locally
    | DeleteEntry                 // remote deletion applied
    | HoldDeletion of Direction   // open a held-delete item; delete nothing
    | PushDeletion                // the local deletion stands: the push step removes the file
    | Ignore                      // nothing to do (left, forgotten, nothing at all)
    | DropRow                     // the row never reached the repository
    | Defer                       // leave this entry for the next cycle; do not advance L

type BaseChange = KeepBase | BaseBecomesTheirs

type ConflictLoser = { Side: Side; Text: string; Stamp: Stamp option }   // Side = Local | Remote

type ReviewRequest =
    | NewPin                      // the record pins; the entry stays an ordinary hot entry
    | PinEdit                     // the record's content is held (the caller reads it from theirs)
    | Conflict of ConflictLoser   // both sides changed the text; the loser's text is kept

type MergeNote =
    | EditBeatsDeletion           // an edit of the text won against a deletion
    | PendingChangesDropped       // a pending tag or tier change was dropped with the entry

type MergeDecision =
    { Entry: EntryOutcome
      Base: BaseChange                    // the row's base after the decision
      RowState: RowState option           // Some s: the row's new state; None: unchanged (or no row)
      Reviews: ReviewRequest list
      Notes: MergeNote list
      CloseHeldDelete: bool               // the open held-delete item is closed without an answer
      FreshContentStamp: bool }           // the push step writes the record again with a new content stamp
```

`Tier option * StampAction` and the like are written as small records in code if that reads better; the shape is
fixed, the spelling is not. There is **no case that pins a local entry**, so no decision can turn a pin on.

### Order of evaluation

For one input, `decide` evaluates in this order and returns the first that applies:

1. **Inconsistent snapshot** (`Defer`): `Row` is `Forgotten` or `Left` and `Local` is `Some`.
2. **Group classification.** If `Theirs` and `Base` are both present, each stamped group is `newer`, `older` or
   `unchanged`. A group is `own` per Decided here 2 (this is computed also when there is no base). The
   classifications are inputs to the tables below.
3. **Whole-entry table** (W1 to W15) by which of the record, the entry and the base exist.
4. **Group rules** for W4 and W5: the **no-base** rules (N-rows) when there is no base, otherwise the
   **field-wise** rules (F-rows); `own` groups and, with a base, `older` and `unchanged` groups are settled
   first.

In an **unproven** apply the tags rule is the no-base union even when there is a base (the three-way merge
would read a reset as a removal), and the deletion rows change as stated in W7, W9 and W11 to W13.

### Whole-entry table

`theirs`, `entry` and `base` say which exist. "Text newer" means the record's `content` stamp is greater than the
base's and its text differs from the base's text, and the stamp is not `own`.

| # | Record | Entry | Row, base | Outcome |
|---|---|---|---|---|
| W1 | present | none | no row | `InsertFromRecord`; row `Active`, base := theirs; `Reviews = [NewPin]` if the record is sticky |
| W2 | present | none | `Left` | `Ignore` |
| W3 | present | none | `Forgotten` | base present and text newer: as W1 (row `Active`, base := theirs, note `EditBeatsDeletion`); otherwise `Ignore` |
| W4 | present | present | no row, or `Active` without base | `MergeGroups` by the no-base rules; base := theirs |
| W5 | present | present | `Active` with base | `MergeGroups` by the field-wise rules; base := theirs |
| W6 | absent | present | `Active`, base, and the entry is sticky or `Pending.Sticky` is set | `HoldDeletion RemoteDeletion` (if `HeldDeleteOpen`: `LeaveEntry`) |
| W7 | absent | present | `Active`, base, not sticky | see "Absent record, entry present" |
| W8 | absent | present | no row, or `Active` without base | `LeaveEntry` (a new local entry; the push step writes it) |
| W9 | present | none | `Active` with base | see "Present record, entry gone" |
| W10 | present | none | `Active` without base | row has `Pushing` stamps and the record is `own` for at least one of them: `PushDeletion`, base := theirs; otherwise `DropRow` |
| W11 | absent | none | `Active` or `Forgotten`, base | row `Forgotten`, base kept; `Entry = Ignore` |
| W12 | absent | none | `Active`, no base | `DropRow` |
| W13 | absent | none | `Left` | `Ignore` |
| W14 | absent | none | no row | `Ignore` |
| W15 | present or absent | present or none | `Forgotten` or `Left` with an entry | `Defer` (rule 1) |

**Absent record, entry present** (W7), `Active` with a base, entry not sticky and no pending sticky. In order:
1. text pending and (`ApplyProven` or `HeldDeleteOpen`): `LeaveEntry`, `FreshContentStamp`, `CloseHeldDelete`
   when an item is open, note `EditBeatsDeletion`, base kept (the push writes the record from the base);
2. `HeldDeleteOpen`: `LeaveEntry` (the item stays open);
3. `ApplyProven`: `DeleteEntry`, row `Forgotten`, base kept; note `PendingChangesDropped` when a tag or tier
   change was pending;
4. otherwise (unproven): `HoldDeletion RemoteDeletion`.
For a sticky entry (W6) with text pending and an open item, step 1 applies first.

**Present record, entry gone** (W9), `Active` with a base. Let `edit` be "text newer".
1. `edit`: `InsertFromRecord`, row `Active`, base := theirs, note `EditBeatsDeletion`, `Reviews = [NewPin]` if
   the record is sticky (the edit wins in every apply, proven or not);
2. otherwise the local deletion stands: proven: `PushDeletion`, base := theirs, row unchanged until the push
   completes; unproven: `HoldDeletion LocalDeletion`, base := theirs.
An unchanged record and one changed only outside the text (tier, tags, sticky, project, scope, entry type) are
the same case.

### Group rules

Notation: `L` the local entry, `T` the record, `B` the base, `p` the pending stamp of the group, `t` the record's
stamp of the group. `RestampPending` means "issue a stamp above `t` and keep it pending".

**Settled first (with a base):** an `own` group gives `Leave` with `ClearPending` if `p = t`, else `KeepPending`;
an `older` group gives `Leave`, `KeepPending` and its `Repush` flag; an `unchanged` group gives `Leave`,
`KeepPending`. Only `newer` groups that are not `own` reach the rows below. **Without a base** only `own` groups
are settled first.

**Content, field-wise** (`newer`, not `own`). First match:

| # | Condition | Apply, pending, reviews |
|---|---|---|
| FC1 | `sameContent(L, T)` | `LeaveContent`; `ClearPending` if `p` is set |
| FC2 | `L` sticky, text/project/scope of `T` differ from `L`, and (`p` unset or `t > p`) | `HeldAsPinEdit`; `ClearPending`; `Reviews = [PinEdit]` |
| FC3 | `p` unset | `TakeTheirs` |
| FC4 | `p > t` | `LeaveContent`, `KeepPending`; if both sides changed the text: `Conflict {Remote, T.text, t}` |
| FC5 | `t > p` | `TakeTheirs`, `ClearPending`; if both sides changed the text: `Conflict {Local, L.text, p}` |

**Content, no base** (not `own`). First match:

| # | Condition | Apply, pending, reviews |
|---|---|---|
| NC1 | `sameContent(L, T)` | `AdoptCreatedAt`, `ClearPending` |
| NC2 | `L` sticky and text/project/scope differ | `HeldAsPinEdit`, `ClearPending`, `Reviews = [PinEdit]` |
| NC3 | same text, other fields differ | `TakeTheirs`, `ClearPending` |
| NC4 | different text | `TakeTheirs`, `ClearPending`, `Conflict {Local, L.text, p}` |

**Tags.** Field-wise, in a proven apply: `Result` keeps an element of `B` iff it is in both `L` and `T`, and keeps
an element not in `B` iff it is in `L` or `T` (the three-way set merge). No base, or an unproven apply: the union
of `L` and `T`. `ApplyLocal` = `Result` differs from `L`; `PushNeeded` = `Result` differs from `T`. With no
record (W8) the tags result is `L`'s and `PushNeeded` is true.

**Sticky, field-wise** (`newer`, not `own`). First match:

| # | Condition | Apply, pending |
|---|---|---|
| FS1 | `p` set and `L.Sticky = T.Sticky` | `LeaveSticky`, `ClearPending` |
| FS2 | `p` set and `p > t` | `LeaveSticky`, `KeepPending` |
| FS3 | `p` set, `t > p`, `T` not sticky | `UnpinLocally`, `ClearPending` |
| FS4 | `p` set, `t > p`, `T` sticky | `ReviewNewPin`, `ClearPending`; `Reviews = [NewPin]` |
| FS5 | `p` unset and `L.Sticky = T.Sticky` | `LeaveSticky`, `KeepPending` |
| FS6 | `p` unset, `T` not sticky, `L` sticky | `UnpinLocally` |
| FS7 | `p` unset, `T` sticky, `L` not sticky | `ReviewNewPin`; `Reviews = [NewPin]` |

**Sticky, no base** (not `own`):

| # | Condition | Apply, pending |
|---|---|---|
| NS1 | `L.Sticky = T.Sticky` | `LeaveSticky`, `ClearPending` |
| NS2 | `L` sticky, `T` not | `LeaveSticky`; `KeepPending` if `p > t`, else `RestampPending` (a local pin is kept and pushed) |
| NS3 | `L` not sticky, `T` sticky | `ReviewNewPin`, `ClearPending`; `Reviews = [NewPin]` |

**Tier, field-wise** (`newer`, not `own`). First match:

| # | Condition | Promote, pending |
|---|---|---|
| FR1 | `T.tier` warmer than `L.tier` | `Some T.tier`, `ClearPending` |
| FR2 | `p` set and `L.tier = T.tier` | `None`, `ClearPending` |
| FR3 | `p` set and `L.tier` warmer than `T.tier` | `None`; `KeepPending` if `p > t`, else `RestampPending` |
| FR4 | otherwise | `None`, `KeepPending` |

**Tier, no base** (not `own`): same tier: `None`, `ClearPending`; `T.tier` warmer: `Some T.tier`, `ClearPending`;
`L.tier` warmer: `None`; `KeepPending` if `p > t`, else `RestampPending`.

"Warmer" is `SyncRecord.tierRank` lower (hot is the warmest). A decision never lowers a tier.

### Properties

- **Total and deterministic.** Any `MergeInput`, including an inconsistent one and a fuzzed one, gives one
  `MergeDecision` and no exception.
- **Deletion needs proof.** `DeleteEntry` occurs only for: a present entry, a base, an absent record, a
  non-sticky entry with no pending sticky stamp and no pending text change, `ApplyProven`, no open held-delete
  item.
- **A pin is never taken from a record.** No `StickyApply` or `EntryOutcome` pins a local entry, and
  `InsertFromRecord` is always non-sticky (a sticky record adds `NewPin`).
- **Never from an older stamp.** For every stamped group an `older` record leaves the local group unchanged and
  sets that group's `Repush`.
- **Idempotent.** Applying a decision to the state it was made from (a small reference interpreter in the tests)
  and calling `decide` again on the result, with the same record, gives a decision that changes nothing: no
  local change, no review, no note, no re-push, `Entry` one of `LeaveEntry`, `Ignore` or a `MergeGroups` whose
  every part is a leave.
- **Order of arrival for the stamped groups.** For two record versions of one entry with different stamps in a
  group, applying them in either order leaves the local content, sticky and tier groups as the greater stamp
  (the warmer tier) decides. Tags are excluded: the three-way merge is relative to a base that moves, and two
  machines' versions of one file are a linear history in practice.

## Testing

F# tests in `tests/TotalRecall.Core.Tests/SyncMergeTests.fs` (Expecto; FsCheck for properties). Generators build
records and entries from a small pool so stamps tie, are older, newer and own at useful rates.
- **Whole-entry table:** one test per row W1 to W15, each in proven and unproven, with each distinguishing
  input (sticky, pending sticky, pending text, open held-delete, row state, own stamps); W7 steps 1 to 4;
  W9 for an edit, an unchanged record, a tier-only and a tag-only change, a sticky record.
- **Group rules:** one test per row FC1 to FC5, NC1 to NC4, FS1 to FS7, NS1 to NS3, FR1 to FR4 and the no-base
  tier rule, plus the settled-first cases (`own` with `p = t` and with a newer `p`, `older`, `unchanged`);
  stamps rather than values deciding "changed" (an unpin, a re-pin and an old offline unpin; both sides ending
  at the same value); a conflicting text edit on both sides in both stamp orders; a tag edit on one side and a
  text edit on the other do not conflict; tags three-way (removal one side, addition the other) and the union
  in an unproven apply and with no base; a sticky local entry holding remote text, and winning with its own.
- **Own pushes:** a record carrying this machine's pushed stamp, with another machine's tag change in between;
  a stamp carrying this machine's id that matches nothing remembered is an ordinary remote change; an `own`
  stamp with a base newer than the record is not `own`.
- **Properties:** the five above, over generated inputs; the idempotence property uses a reference interpreter
  that is test code (it applies a decision to a model of the entry and the row).
- **Defer and totality:** every combination of `Row` x record x entry x base x proven x held-open x sticky
  (exhaustive over the small domain) returns a decision.
The project builds under `PublishAot` with no new warning.

## Risks

- The tables are long and a transcription error is a data-loss bug. Each row has a test named for it, the
  deletion safety is also a property, and the umbrella's wording is quoted in the test names so review compares
  them one to one.
- The `StampAction` and `Repush` outputs are the contract 1b-i/1b-ii depend on. If implementing the cycle shows
  an output is missing, 0b-2's types change in that milestone; nothing outside Core uses them before.

## Documentation

- `src/TotalRecall.Core/AGENTS.md`: one row for `SyncMerge.fs` (and `SyncMergeGroups.fs` if added).
- `CHANGELOG.md` is **not** changed: nothing user-visible happens in this milestone.
