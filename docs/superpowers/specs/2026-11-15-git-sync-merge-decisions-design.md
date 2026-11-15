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

The umbrella leaves these open; the spec fixes them so the implementation does not guess. Each is small; the
ones that go beyond the umbrella's text are marked **(beyond)** and justified.

1. **`changed` is stamp comparison**, per group, in three states: `newer` (the record's stamp is greater than the
   base's), `older` (less) and `unchanged` (equal). A group is **never taken from an `older` record**: the local
   group is kept and **the base keeps its group value and stamp** (the repository is caught up from the base,
   so the base must not be overwritten with the older record). The group is marked for re-push (`Repush`) only
   when it has **no pending change** (a group with a real pending change goes through the normal path, as the
   umbrella says). The umbrella places the marker under unproven applies; a proven apply cannot produce it,
   because every push is a fast-forward, so the function treats both the same.
2. **A group is `own`** (the record carries a stamp this row can prove it sent) when the record's stamp equals
   the row's `Pushing` stamp or its `Pending` stamp for that group and the base, if there is one, is not newer
   than the record. An `own` group is settled before any table is consulted: nothing is applied for it, its
   base becomes the record's, and its pending stamp is cleared only if it equals the record's. Tags have no
   stamp and are never `own`.
3. **`sameContent`** compares text, `project`, `scope` and `entry_type`. `source` and `created_at` follow
   whichever side wins and are not compared.
4. **A pending change of the text** (used by the deletion rows and by "both sides changed the text") means
   `Pending.Content` is set **and** the local text differs from the base's text. A held `pin-edit` is not a
   pending change: holding one clears the content stamp, and the remote text becomes the base while the local
   pin text stays.
5. **"Both sides changed the text"** (field-wise) means a pending change of the text, the record's text differs
   from the base's text, and the two texts differ from each other. With no base, differing texts are a conflict.
6. **A sticky local entry holds remote content as a `pin-edit`** when the record's text, `project` or `scope`
   differs from the local entry's; a difference only in `entry_type` or `source` is taken directly. "Sticky" is
   **effective** stickiness: the local flag unless the same decision unpins it (the sticky group is decided
   first), so a record that unpins and edits at once takes its text directly and opens no `pin-edit`. When the
   local side wins (its pending stamp is greater) nothing is held: the local pin text is what will be pushed.
7. **An edit of the text beats a deletion in every apply (beyond).** A pending change of the text on an entry
   whose record is absent is published again with a fresh content stamp, proven or not, pinned or not, with or
   without an open item. The umbrella gets the same result in two steps (hold the deletion, then close the item
   when the push writes the record back); deciding it directly avoids an item that opens and closes. A pin is
   still never deleted unasked: only the user's own edit of its text republishes it.
8. **A snapshot that cannot occur** (the scan re-activates a `forgotten` or `left` row whose entry exists) gives
   `Defer`: leave this entry for the next cycle and do not advance `L`.
9. **A base-less row with no entry and a record present (beyond).** If the record is provably this machine's own
   push the local deletion is pushed. Otherwise the user's deletion stands and the other machine's record is
   not resurrected: the row becomes `Forgotten` with the record as its base (only a newer, different text
   brings it back). The umbrella drops the row only when no record exists; dropping it here would lose the
   record from the visit set and from the `L..R` diff for good.
10. **`Base` is ignored when `Row` is `None`** (a new local entry has no base by definition).

## Design

### Where it lives

`src/TotalRecall.Core/SyncMerge.fs` (module `TotalRecall.Core.SyncMerge`), after `SyncValidation.fs`, plus
`SyncMergeGroups.fs` for the per-group rules if one file grows past a comfortable size. Pure; it uses the
0b-1 types (`SyncStamp.Stamp`, `SyncRecord.SyncRecord`, `Placement`, `Tier`, `EntryType`, `SourceTool`).

### Input

```fsharp
type RowState = Active | Forgotten | Left

type Direction = RemoteDeletion | LocalDeletion

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
      HeldDelete: Direction option } // the direction of the held-delete item open for this id, if any
```

`Row = None` with `Local = Some _` is a new local entry (the scan creates its row in the same cycle; the function
accepts both). Every `Stamp` is in its domain (0b-1).

### Output

```fsharp
type Side = Local | Remote

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

/// The base is updated per group (an `older` group keeps its base). Tags and the whole-entry rows use all four.
type BaseUpdate = { Content: BaseChange; Tags: BaseChange; Sticky: BaseChange; Tier: BaseChange }

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
      Base: BaseUpdate                    // the row's base after the decision, per group
      RowState: RowState option           // Some s: the row's new state; None: unchanged (or no row)
      Reviews: ReviewRequest list
      Notes: MergeNote list
      CloseHeldDelete: bool               // the open held-delete item is closed without an answer
      FreshContentStamp: bool }           // the push step writes the record again with a new content stamp
```

`Tier option * StampAction` and the like are written as small records in code if that reads better; the shape is
fixed, the spelling is not. Several records share the labels `Content`, `Tags`, `Sticky` and `Tier` (and
`SyncRecord` and `Placement` have more), so code and tests annotate record construction. There is **no case that
pins a local entry**, so no decision can turn a pin on.

**Row effects the outcome implies.** For every outcome other than `MergeGroups` and `LeaveEntry` the row's
pending stamps are cleared (the entry was inserted, deleted, forgotten or dropped). `MergeGroups` and
`LeaveEntry` act on pending stamps only through the `StampAction` of each group and `FreshContentStamp`.
`Base = BaseBecomesTheirs` for all four groups is written "base := theirs" in the tables; "base kept" is all
four `KeepBase`.

### Order of evaluation

For one input, `decide` evaluates in this order and returns the first that applies:

1. **Inconsistent snapshot** (`Defer`): `Row` is `Forgotten` or `Left` and `Local` is `Some`.
2. **Group classification.** If `Theirs` and `Base` are both present, each stamped group is `newer`, `older` or
   `unchanged`. A group is `own` per Decided here 2 (this is computed also when there is no base). The
   classifications are inputs to the tables below. **The sticky group is decided first**, so the content rules
   can use effective stickiness (Decided here 6).
3. **Whole-entry table** (W1 to W14) by which of the record, the entry and the base exist.
4. **Group rules** for W4 and W5: the **no-base** rules (N-rows) when there is no base, otherwise the
   **field-wise** rules (F-rows); `own` groups and, with a base, `older` and `unchanged` groups are settled
   first.

In an **unproven** apply the tags rule is the no-base union even when there is a base (the three-way merge
would read a reset as a removal), and the deletion rows change as stated in W7, W9 and W11 to W13.

### Whole-entry table

`theirs`, `entry` and `base` say which exist (`Base` counts only when `Row` is not `None`). "Text newer" means
the record's `content` stamp is greater than the base's, its text differs from the base's text, and the stamp is
not `own`. Every combination of record, entry, row and base falls in exactly one row.

| # | Record | Entry | Row, base | Outcome |
|---|---|---|---|---|
| W1 | present | none | no row | `InsertFromRecord`; row `Active`, base := theirs; `Reviews = [NewPin]` if the record is sticky |
| W2 | present | none | `Left` | `Ignore` |
| W3 | present | none | `Forgotten` | base present and text newer: as W1 (row `Active`, base := theirs, note `EditBeatsDeletion`); otherwise `Ignore` |
| W4 | present | present | no row, or `Active` without base | `MergeGroups` by the no-base rules; base := theirs |
| W5 | present | present | `Active` with base | `MergeGroups` by the field-wise rules; base per group |
| W6 | absent | present | `Active` with base | the ordered list "Absent record, entry present" |
| W7 | absent | present | no row, or `Active` without base | `LeaveEntry` (a new local entry; the caller pushes it whole) |
| W8 | present | none | `Active` with base | the ordered list "Present record, entry gone" |
| W9 | present | none | `Active` without base | the record is `own` for a stamp in `Pushing`: `PushDeletion`, base := theirs; otherwise `Ignore`, row `Forgotten`, base := theirs (Decided here 9) |
| W10 | absent | none | `Active` or `Forgotten`, with base | `Ignore`; row `Forgotten`, base kept |
| W11 | absent | none | `Active`, no base | `DropRow` |
| W12 | absent | none | `Forgotten` without base, or `Left` | `Ignore` |
| W13 | absent | none | no row | `Ignore` |
| W14 | present or absent | present | `Forgotten` or `Left` | `Defer` (Decided here 8); checked first |

**Absent record, entry present** (W6), in order; the first that applies:
1. a pending change of the text: `LeaveEntry`, `FreshContentStamp`, base kept (the push writes the record from the
   base), `CloseHeldDelete` if an item is open, note `EditBeatsDeletion` (Decided here 7);
2. the entry is sticky or `Pending.Sticky` is set: `HoldDeletion RemoteDeletion` (`LeaveEntry` if a
   `RemoteDeletion` item is already open);
3. a `RemoteDeletion` item is open: `LeaveEntry` (the item stays);
4. `ApplyProven`: `DeleteEntry`, row `Forgotten`, base kept; note `PendingChangesDropped` when a tag or tier
   change was pending;
5. otherwise (unproven): `HoldDeletion RemoteDeletion`.

**Present record, entry gone** (W8), in order; the first that applies. `edit` is "text newer":
1. `edit`: `InsertFromRecord`, row `Active`, base := theirs, note `EditBeatsDeletion`, `CloseHeldDelete` if any
   item is open, `Reviews = [NewPin]` if the record is sticky;
2. a `LocalDeletion` item is open: `Ignore` (the item stays; the user has not answered), base per group;
3. `ApplyProven`: `PushDeletion`, base per group, row unchanged until the push completes;
4. otherwise: `HoldDeletion LocalDeletion`, base per group.
An unchanged record and one changed only outside the text (tier, tags, sticky, project, scope, entry type) fall
in 2 to 4. "Base per group" keeps the base of an `older` group and updates the others (Decided here 1).

**Held-delete items.** `CloseHeldDelete` is set when: a `RemoteDeletion` item is open and the record is present
or the entry is gone (the record reappeared, or the deletion is moot); a `LocalDeletion` item is open and the
entry is present or the record is absent or an edit of the text wins (W8.1); and in W6.1 as stated. A decision
never opens an item that is already open (`HoldDeletion` becomes `LeaveEntry`).

### Group rules

Notation: `L` the local entry, `T` the record, `B` the base, `p` the pending stamp of the group, `t` the record's
stamp of the group. `RestampPending` means "issue a stamp above `t` and keep it pending".

**Settled first (with a base):** an `own` group gives `Leave`, base becomes the record's, and `ClearPending` if
`p = t`, else `KeepPending`; an `older` group gives `Leave`, `KeepPending`, **base kept**, and its `Repush` flag
only if `p` is unset; an `unchanged` group gives `Leave`, `KeepPending`. Only `newer` groups that are not `own`
reach the rows below. **Without a base** only `own` groups are settled first.

**Content, field-wise** (`newer`, not `own`). First match:

| # | Condition | Apply, pending, reviews |
|---|---|---|
| FC1 | `sameContent(L, T)` | `LeaveContent`; `ClearPending` if `p` is set |
| FC2 | `L` effectively sticky, text/project/scope of `T` differ from `L`, and (`p` unset or `t > p`) | `HeldAsPinEdit`; `ClearPending`; `Reviews = [PinEdit]` |
| FC3 | `p` unset | `TakeTheirs` |
| FC4 | `p > t` | `LeaveContent`, `KeepPending`; if both sides changed the text: `Conflict {Remote, T.text, t}` |
| FC5 | `t > p` | `TakeTheirs`, `ClearPending`; if both sides changed the text: `Conflict {Local, L.text, p}` |

FC2 clears the content stamp but the base becomes the record, so the held text is not a local change.

**Content, no base** (not `own`). First match:

| # | Condition | Apply, pending, reviews |
|---|---|---|
| NC1 | `sameContent(L, T)` | `AdoptCreatedAt`, `ClearPending` |
| NC2 | `L` effectively sticky and text/project/scope differ | `HeldAsPinEdit`, `ClearPending`, `Reviews = [PinEdit]` |
| NC3 | same text, other fields differ | `TakeTheirs`, `ClearPending` |
| NC4 | different text | `TakeTheirs`, `ClearPending`, `Conflict {Local, L.text, p}` |

**Tags.** Field-wise, in a proven apply: `Result` keeps an element of `B` iff it is in both `L` and `T`, and keeps
an element not in `B` iff it is in `L` or `T` (the three-way set merge). No base, or an unproven apply: the union
of `L` and `T`. `ApplyLocal` = `Result` differs from `L`; `PushNeeded` = `Result` differs from `T`. With no
record (W7) there is no merge; the caller pushes the new entry whole.

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
- **Deletion needs proof.** `DeleteEntry` occurs only for: a present entry, a row `Active` with a base, an
  absent record, a non-sticky entry with no pending sticky stamp and no pending change of the text,
  `ApplyProven`, no open `RemoteDeletion` item.
- **A pin is never taken from a record.** No `StickyApply` or `EntryOutcome` pins a local entry, and
  `InsertFromRecord` is always non-sticky (a sticky record adds `NewPin`).
- **Never from an older stamp.** For every stamped group an `older` record leaves the local group unchanged and
  the group's base unchanged; `Repush` is set for it exactly when the group has no pending stamp.
- **Idempotent.** Apply a decision's effects to a model of the entry, the row and the held item (a reference
  interpreter in the tests), call `decide` again with the same record, and there is no further local change, no
  new review request, no new note and no new item. The outcomes that legitimately repeat until the push or the
  user acts are `PushDeletion`, `FreshContentStamp`, `PushNeeded` and `Defer`; `HoldDeletion` repeats as
  `LeaveEntry` because an open item is never reopened.
- **Order of arrival for content and tier.** For two record versions of one entry with different stamps in a
  group, applying them in either order leaves the local content and tier groups as the greater stamp (the
  warmer tier) decides. Tags are excluded (the three-way merge is relative to a base that moves, and two
  machines' versions of one file are a linear history in practice). Sticky is excluded: a remote pin is only
  ever a review request, so a pin followed by an older unpin and the reverse legitimately differ in what is
  asked of the user.

## Testing

F# tests in `tests/TotalRecall.Core.Tests/SyncMergeTests.fs` (Expecto; FsCheck for properties. Generators build
records and entries from a small pool so stamps tie, are older, newer and own at useful rates.
- **Whole-entry table:** one test per row W1 to W14, each in proven and unproven, with each distinguishing
  input (sticky, pending sticky, pending text, open held-delete, row state, own stamps); W6 steps 1 to 5 (including a held pin-edit followed by a remote deletion, which is not a pending change);
  W8 for an edit, an unchanged record, a tier-only and a tag-only change, a sticky record.
- **Group rules:** one test per row FC1 to FC5, NC1 to NC4, FS1 to FS7, NS1 to NS3, FR1 to FR4 and the no-base
  tier rule, plus the settled-first cases (`own` with `p = t` and with a newer `p`, `older`, `unchanged`);
  stamps rather than values deciding "changed" (an unpin, a re-pin and an old offline unpin; both sides ending
  at the same value); a conflicting text edit on both sides in both stamp orders; a tag edit on one side and a
  text edit on the other do not conflict; tags three-way (removal one side, addition the other) and the union
  in an unproven apply and with no base; a sticky local entry holding remote text, and winning with its own.
- **Own pushes:** a record carrying this machine's pushed stamp, with another machine's tag change in between;
  a stamp carrying this machine's id that matches nothing remembered is an ordinary remote change; an `own`
  stamp with a base newer than the record is not `own`.
- **Held items and bases:** an item closed when the record reappears, when an edit of the text wins and when
  the entry or record changes; an item never reopened; an older group keeping its base value and stamp in W5
  and W8, and its `Repush` only without a pending stamp; a record that unpins and edits at once; a base-less
  deleted row with a foreign record (W9) not resurrected until a newer, different text.
- **Properties:** the properties above, over generated inputs; the idempotence property uses a reference interpreter
  that is test code (it applies a decision to a model of the entry and the row).
- **Defer and totality:** every combination of `Row` x record x entry x base x proven x held-open x sticky
  (exhaustive over the small domain) returns a decision.
The project builds under `PublishAot` with no new warning. `TotalRecall.Core.fsproj` gets `SyncMergeGroups.fs`
(if split) and `SyncMerge.fs` after `SyncValidation.fs`, in that order; the test fsproj gets `SyncMergeTests.fs`
before `Program.fs`.

## Risks

- The tables are long and a transcription error is a data-loss bug. Each row has a test named for it, the
  deletion safety is also a property, and the umbrella's wording is quoted in the test names so review compares
  them one to one.
- The `StampAction` and `Repush` outputs are the contract 1b-i/1b-ii depend on. If implementing the cycle shows
  an output is missing, 0b-2's types change in that milestone; nothing outside Core uses them before.

## Documentation

- `src/TotalRecall.Core/AGENTS.md`: one row for `SyncMerge.fs` (and `SyncMergeGroups.fs` if added).
- `CHANGELOG.md` is **not** changed: nothing user-visible happens in this milestone.
