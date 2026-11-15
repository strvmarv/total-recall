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
    { /// `None`: no row for this id.
      Row: RowState option
      /// The last reconciled record (`base_json`). Ignored when `Row` is `None`.
      Base: SyncRecord option
      /// The record at R; `None`: its path is absent from R's tree.
      Theirs: SyncRecord option
      /// `None`: no memory with this id (a local deletion, or never existed).
      Local: LocalEntry option
      /// Persisted stamps of locally changed groups.
      Pending: GroupStamps
      /// What this row proves it sent.
      Pushing: GroupStamps
      ApplyProven: bool
      /// The direction of the held-delete item open for this id, if any.
      HeldDelete: Direction option }

type Side =
    | Local
    | Remote

/// What to do with a group's pending stamp after the decision.
type StampAction =
    /// Leave it as it is (including: none).
    | KeepPending
    /// The change is already in the repository, was superseded, or converged.
    | ClearPending
    /// Issue a new stamp above the record's stamp for the group and keep it pending.
    | RestampPending

type ContentApply =
    | LeaveContent
    /// Keep the local fields, take the record's `created_at` (no base, same content).
    | AdoptCreatedAt
    /// Apply the record's text, project, scope, entry type, source and `created_at`.
    | TakeTheirs
    /// A sticky local entry: the record's content is held; the local text stays active.
    | HeldAsPinEdit

type StickyApply =
    | LeaveSticky
    /// A remote unpin.
    | UnpinLocally
    /// The record pins and the local entry is not sticky: a new-pin review; the entry stays unpinned.
    | ReviewNewPin

type TagsOutcome =
    { /// The merged set, sorted and unique.
      Result: string list
      /// `Result` differs from the local tags.
      ApplyLocal: bool
      /// `Result` differs from the record's tags.
      PushNeeded: bool }

/// Tags are never re-pushed from a base, so they have no flag.
type GroupFlags =
    { Content: bool
      Sticky: bool
      Tier: bool }

type Merged =
    { Content: ContentApply * StampAction
      Tags: TagsOutcome
      Sticky: StickyApply * StampAction
      /// `Some t`: promote the local entry to `t` (never colder).
      Tier: Tier option * StampAction
      /// Groups the record regressed (older stamp): re-push the base's value and stamp.
      Repush: GroupFlags }

type EntryOutcome =
    /// Insert the record non-sticky at the record's tier.
    | InsertFromRecord
    /// The entry exists and so does the record.
    | MergeGroups of Merged
    /// Change nothing locally.
    | LeaveEntry
    /// A remote deletion, applied.
    | DeleteEntry
    /// Open a held-delete item; delete nothing.
    | HoldDeletion of Direction
    /// The local deletion stands: the push step removes the file.
    | PushDeletion
    /// Nothing to do.
    | Ignore
    /// The row never reached the repository.
    | DropRow
    /// Leave this entry for the next cycle; do not advance `L`.
    | Defer

type BaseChange =
    | KeepBase
    | BaseBecomesTheirs

/// The base is updated per group: an `older` group keeps its base value and stamp.
type BaseUpdate =
    { Content: BaseChange
      Tags: BaseChange
      Sticky: BaseChange
      Tier: BaseChange }

/// The loser of a text conflict: which side, its text, and the stamp it carried.
type ConflictLoser =
    { Side: Side
      Text: string
      Stamp: Stamp option }

type ReviewRequest =
    /// The record pins; the entry stays an ordinary hot entry.
    | NewPin
    /// The record's content is held (the caller reads it from the record).
    | PinEdit
    /// Both sides changed the text; the loser's text is kept.
    | Conflict of ConflictLoser

type MergeNote =
    /// An edit of the text won against a deletion.
    | EditBeatsDeletion
    /// A pending tag, tier or non-text content change was dropped with the entry.
    | PendingChangesDropped

type MergeDecision =
    { Entry: EntryOutcome
      /// The row's base after the decision, per group.
      Base: BaseUpdate
      /// `Some s`: the row's new state; `None`: unchanged (or no row).
      RowState: RowState option
      Reviews: ReviewRequest list
      Notes: MergeNote list
      /// The open held-delete item is closed without an answer.
      CloseHeldDelete: bool
      /// The push step writes the record again with a new content stamp.
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
