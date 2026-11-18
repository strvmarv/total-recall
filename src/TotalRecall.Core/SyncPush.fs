/// Git sync, milestone 1b-i-3: the pure rules of the push. The push writes this machine's changed memories into the
/// local clone as patches of the records already there (a new memory becomes a new record), commits once and pushes
/// without force. This module decides, without touching anything: which rows are candidates, which candidates are left
/// out and why (`exclusion`, `pathOccupied`), what happens to a block mark (`markAction`), which groups of an entry are
/// written (`planChanged`), the new record (`newRecord`), the row after a push (`afterPush`), the commit message, the
/// machine label, the `published` mapping of a failed push and the staged-set check. Pure and total: no I/O, no clock.
module TotalRecall.Core.SyncPush

open System
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge

// ---------------------------------------------------------------------------------------------
// Which entries

/// What the pusher reads of a `git_sync_entry` row to decide whether it is a candidate (decision 4). `BaseTagsHash` is
/// `SyncScan.tagsHash` of the base's tags (`None`: no base, or one that cannot be read); `ScannedTagsHash` the row's
/// `scanned_tags_hash` (`None`: nothing seen).
type RowFacts =
    { State: RowState
      HasBase: bool
      BaseTagsHash: string option
      ScannedTagsHash: string option
      Pending: GroupStamps }

/// An `active` row is a candidate when it has no base, or a pending content, sticky or tier stamp, or tags that differ
/// from its base's (the scan stores the entry's tags hash; a NULL hash is "nothing seen" and is no difference).
let isCandidate (row: RowFacts) : bool =
    row.State = Active
    && (not row.HasBase
        || row.Pending.Content.IsSome
        || row.Pending.Sticky.IsSome
        || row.Pending.Tier.IsSome
        || (match row.ScannedTagsHash, row.BaseTagsHash with
            | Some scanned, Some baseHash -> scanned <> baseHash
            | _ -> false))

/// Why a candidate is left out of this push (decision 5).
type Exclusion =
    | Issue
    | RejectedRecord
    | NotReconciled
    | RecordMissing
    | RecordUnreadable
    | UnreadableBase
    | PathOccupied
    | BlockedElsewhere
    | UnreadableEntry

let exclusionName (e: Exclusion) : string =
    match e with
    | Issue -> "issue"
    | RejectedRecord -> "rejected-record"
    | NotReconciled -> "not-reconciled"
    | RecordMissing -> "record-missing"
    | RecordUnreadable -> "record-unreadable"
    | UnreadableBase -> "unreadable-base"
    | PathOccupied -> "path-occupied"
    | BlockedElsewhere -> "blocked-elsewhere"
    | UnreadableEntry -> "unreadable-entry"

/// The facts the exclusion order reads. `HasIssueOrSkip`: an open `apply-failed` issue or an id the scan skipped.
/// `RejectedNamesId`: a rejected path of the tree names this id. `RecordAccepted`: the tree holds an accepted record.
type ExclusionFacts =
    { HasIssueOrSkip: bool
      RejectedNamesId: bool
      HasBase: bool
      RecordAccepted: bool
      RecordUnreadable: bool
      BaseUnreadable: bool
      PathOccupied: bool
      BlockedElsewhere: bool
      EntryUnreadable: bool }

/// The first exclusion that matches, in the order of decision 5.
let exclusion (f: ExclusionFacts) : Exclusion option =
    if f.HasIssueOrSkip then Some Issue
    elif f.RejectedNamesId then Some RejectedRecord
    elif not f.HasBase && f.RecordAccepted then Some NotReconciled
    elif f.HasBase && not f.RecordAccepted then Some RecordMissing
    elif f.RecordAccepted && f.RecordUnreadable then Some RecordUnreadable
    elif f.BaseUnreadable then Some UnreadableBase
    elif f.PathOccupied then Some PathOccupied
    elif f.BlockedElsewhere then Some BlockedElsewhere
    elif f.EntryUnreadable then Some UnreadableEntry
    else None

/// True when a listed path is in the way of the record this id would be written to (decision 5, `path-occupied`).
/// `listed` is every path of the tree, accepted or rejected. Every comparison ignores case, because a clone on Windows
/// or macOS folds case: a foreign `memories/AB/x.json` or `memories/ab/ABCDEF12.json` would make the file land elsewhere
/// or stage nothing. The record's own exact path is never a collision, and a sibling with the same directories never is.
/// An id that is not eligible has no path: `false`.
let pathOccupied (id: string) (listed: string seq) : bool =
    if not (SyncValidation.isEligibleId id) then false
    else
        let record = SyncValidation.recordPath id
        let shardDir = "memories/" + SyncValidation.shard id
        let ic = StringComparison.OrdinalIgnoreCase
        let ord = StringComparison.Ordinal
        let collides (path: string) =
            if String.Equals(path, record, ord) then false
            else
                String.Equals(path, record, ic)
                || String.Equals(path, shardDir, ic)
                || String.Equals(path, "memories", ic)
                || path.StartsWith(record + "/", ic)
                || (match path.Split('/') with
                    | segments when segments.Length >= 2 ->
                        String.Equals(segments.[0], "memories", ic)
                        && String.Equals(segments.[1], SyncValidation.shard id, ic)
                        && not (String.Equals(segments.[0], "memories", ord) && String.Equals(segments.[1], SyncValidation.shard id, ord))
                    | _ -> false)
        listed |> Seq.exists collides

// ---------------------------------------------------------------------------------------------
// Block marks

/// The `blocked_reason` words the writer itself sets (decision 8); every other word belongs to a later part.
let writerReasons : string list =
    [ "too-large"; "too-many-tags"; "tag-too-long"; "ineligible-id"; "not-encodable" ]

let isWriterReason (reason: string) : bool = List.contains reason writerReasons

let blockName (b: SyncValidation.Block) : string =
    match b with
    | SyncValidation.TooLarge -> "too-large"
    | SyncValidation.TooManyTags -> "too-many-tags"
    | SyncValidation.TagTooLong -> "tag-too-long"
    | SyncValidation.IneligibleId -> "ineligible-id"
    | SyncValidation.NotEncodable -> "not-encodable"
    | SyncValidation.ClockExhausted -> "clock-exhausted"

type MarkAction =
    | KeepMark
    | SetMark of reason: string
    | ClearMark

/// What happens to a row's block marks (decision 8). A candidate with a violation gets the mark; a candidate without
/// one, and a non-candidate (which has nothing pending), loses a mark the writer set; a mark of another name is never
/// touched, and a non-candidate never reaches the limit check, so a violation does not apply to it.
let markAction (currentReason: string option) (violation: SyncValidation.Block option) (candidate: bool) : MarkAction =
    let writersOwn =
        match currentReason with
        | Some r -> isWriterReason r
        | None -> false
    match currentReason, violation, candidate with
    | Some r, _, _ when not (isWriterReason r) -> KeepMark
    | _, Some v, true -> SetMark(blockName v)
    | _, Some _, false -> KeepMark
    | _, None, _ -> if writersOwn then ClearMark else KeepMark
