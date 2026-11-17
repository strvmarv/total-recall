/// Git sync, milestone 1b-i-2: the pure rules of the pull. The pull (fetch, then apply) reads what the repository holds
/// and carries `SyncMerge.decide`'s decisions out, one entry at a time. This module holds what is decided without
/// touching anything: which decisions this milestone does not carry out (`postponement`), the `pushing_stamps` column
/// text, the hot-tier limit, which entries the apply looks at (`visitSet`) and the row an entry has afterwards
/// (`planRow`). Pure and total: no I/O, no clock (a stamp is issued by the caller through a function).
module TotalRecall.Core.SyncApply

open System
open System.Text.Json
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge

// ---------------------------------------------------------------------------------------------
// Postponement

/// Why a decision is not carried out here: it needs a part of git sync that a later milestone builds.
type PostponeReason =
    /// An outcome or note of the deletion rules (applying, holding or pushing a deletion, a `forgotten` row, a held-delete close).
    | Deletion
    /// The decision opens a review item (a pin, a pin-edit, a conflict).
    | Review
    /// A group's value is re-pushed from the base.
    | Repush

let reasonName (r: PostponeReason) : string =
    match r with
    | Deletion -> "deletion"
    | Review -> "review"
    | Repush -> "repush"

/// `Some reason` when the decision must not be carried out by this milestone (in this order of precedence); `None` when it is.
let postponement (d: MergeDecision) : PostponeReason option =
    let deletionOutcome =
        match d.Entry with
        | DeleteEntry
        | HoldDeletion _
        | PushDeletion -> true
        | _ -> false
    if deletionOutcome
       || not d.Notes.IsEmpty
       || d.FreshContentStamp
       || d.CloseHeldDelete
       || d.RowState = Some Forgotten then
        Some Deletion
    elif not d.Reviews.IsEmpty then
        Some Review
    else
        match d.Entry with
        | MergeGroups m when m.Repush.Content || m.Repush.Sticky || m.Repush.Tier -> Some Repush
        | _ -> None

// ---------------------------------------------------------------------------------------------
// The `pushing_stamps` column

/// `{"content":"<stamp>","sticky":"<stamp>","tier":"<stamp>"}` in the stamp text form; a missing key is no stamp.
module PushingStamps =

    let private none : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

    /// Total: null, blank, `{}`, `[]`, text that is not JSON, a non-object root, and a duplicate, non-string or
    /// unparsable key all read as no stamp for that group (the rest of the object is still read).
    let decode (text: string) : GroupStamps =
        if String.IsNullOrWhiteSpace text then none
        else
            try
                use doc = JsonDocument.Parse text
                let root = doc.RootElement
                if root.ValueKind <> JsonValueKind.Object then none
                else
                    let props = [ for p in root.EnumerateObject() -> p ]
                    let get (name: string) : Stamp option =
                        match props |> List.filter (fun p -> p.Name = name) with
                        | [ p ] when p.Value.ValueKind = JsonValueKind.String ->
                            match p.Value.GetString() with
                            | null -> None
                            | s -> Stamp.tryParse s
                        | _ -> None
                    { GroupStamps.Content = get "content"; Sticky = get "sticky"; Tier = get "tier" }
            with ex when not (ex :? OutOfMemoryException) -> none

    /// `None` (the column becomes NULL) when no group has a stamp; else only the present keys, in the order content,
    /// sticky, tier, with no spaces.
    let encode (s: GroupStamps) : string option =
        let part (name: string) (v: Stamp option) =
            match v with
            | Some x -> [ "\"" + name + "\":\"" + Stamp.format x + "\"" ]
            | None -> []
        match part "content" s.Content @ part "sticky" s.Sticky @ part "tier" s.Tier with
        | [] -> None
        | parts -> Some("{" + String.Join(",", parts) + "}")

// ---------------------------------------------------------------------------------------------
// Rejection key, hot limit, visit set

/// The key of a `rejected-record` issue row: the path of the rejected entry through `SyncValidation.sanitizeText`
/// (at most 64 UTF-16 code units, so two paths that share their first 64 share one row).
let rejectionKey (path: string) : string = SyncValidation.sanitizeText path

/// The hot per-entry limit (decision 9), judged on the result of the decision: true when the decision leaves a
/// non-sticky entry in the hot tier with text longer than `maxChars` **and** it is the decision that put it there
/// (an insert, a promotion into hot, or the record's different text on an entry that is or becomes hot). A sticky
/// result is exempt. The caller then stores or moves the entry to the warm tier.
let demotesToWarm (maxChars: int) (d: MergeDecision) (theirs: SyncRecord) (local: LocalEntry option) : bool =
    let overLimit (text: string) = text.Length > maxChars
    match d.Entry with
    | InsertFromRecord ->
        theirs.Placement.Tier = Hot && not theirs.Placement.Sticky && overLimit theirs.Content
    | MergeGroups m ->
        match local with
        | None -> false
        | Some l ->
            let takesRecordText = (fst m.Content = TakeTheirs)
            let resultTier = defaultArg (fst m.Tier) l.Tier
            let resultSticky = l.Sticky && (fst m.Sticky <> UnpinLocally)
            let resultText = if takesRecordText then theirs.Content else l.Content.Text
            let textChanged = takesRecordText && theirs.Content <> l.Content.Text
            let promotedIntoHot = (fst m.Tier = Some Hot) && l.Tier <> Hot
            resultTier = Hot && not resultSticky && overLimit resultText && (textChanged || promotedIntoHot)
    | _ -> false

/// The sets the visit set is built from (decision 5). `Present` holds the ids whose path is in `R`'s listing, accepted
/// or rejected (a rejection that names an id); `Accepted` the ids with an accepted record.
type VisitInput =
    { /// No `L`, or an unproven apply: every accepted record is read.
      Unproven: bool
      Accepted: Set<string>
      /// The ids whose record changed between `L` and `R` (the diff's `Changed` verdicts).
      Changed: Set<string>
      Present: Set<string>
      /// Rows with no base.
      NoBaseRows: Set<string>
      /// Ids with an open `apply-failed` issue.
      ApplyFailed: Set<string>
      /// Accepted ids whose path key has an open `rejected-record` issue.
      Revisit: Set<string>
      /// `active` rows with a base.
      ActiveWithBase: Set<string>
      /// The scan's `LocalDeletions`.
      LocalDeletions: Set<string> }

/// The entries to decide, in ordinal id order, distinct.
let visitSet (v: VisitInput) : string list =
    let changed = if v.Unproven then v.Accepted else v.Changed
    Set.unionMany
        [ changed
          Set.intersect v.NoBaseRows v.Accepted
          v.ApplyFailed
          v.Revisit
          Set.difference v.ActiveWithBase v.Present
          v.LocalDeletions ]
    |> Set.toList
    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
