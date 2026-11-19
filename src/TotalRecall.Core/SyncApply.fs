/// Git sync, milestone 1b-i-2: the pure rules of the pull. The pull (fetch, then apply) reads what the repository holds
/// and carries `SyncMerge.decide`'s decisions out, one entry at a time. This module holds what is decided without
/// touching anything: which decisions this milestone does not carry out (`postponement`), the `pushing_stamps` column
/// text, the hot-tier limit, which entries the apply looks at (`visitSet`) and the row an entry has afterwards
/// (`planRow`). Pure and total: no I/O, no clock (a stamp is issued by the caller through a function). Milestone 1b-ii
/// carries deletions out in both directions (D6 to D13): `postponement` narrows and `planRow` handles a fresh content
/// stamp and an applied deletion.
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
    /// The decision opens a review item (a pin, a pin-edit, a conflict).
    | Review
    /// A group's value is re-pushed from the base.
    | Repush

let reasonName (r: PostponeReason) : string =
    match r with
    | Review -> "review"
    | Repush -> "repush"

/// `Some reason` when the decision must not be carried out by this milestone (in this order of precedence); `None` when it is.
/// A deletion in either direction (applied, pushed or held), a note, a fresh content stamp, a held-delete close and a
/// `forgotten` row are carried out (milestone 1b-ii, D6 to D12).
let postponement (d: MergeDecision) : PostponeReason option =
    if not d.Reviews.IsEmpty then
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
    // A limit of 0 or less means no limit (as `session_end` and `session_start` treat it).
    let overLimit (text: string) = maxChars > 0 && text.Length > maxChars
    match d.Entry with
    | InsertFromRecord ->
        // The entry is inserted non-sticky whatever the record says (decision 9).
        theirs.Placement.Tier = Hot && overLimit theirs.Content
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
      /// Accepted ids whose path key has an open `rejected-record` issue (a path the listing rejects is re-judged by the
      /// caller from the listing every pull and is not in this set).
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

// ---------------------------------------------------------------------------------------------
// The next row

/// What `planRow` reads of the row as it is now. `State = None`: there is no row for the id.
type RowBefore =
    { State: RowState option
      Pending: GroupStamps
      /// The raw text of the `pushing_stamps` column (null: NULL).
      PushingText: string option
      /// The tier of the row's base record (`None`: no base, or one that cannot be read).
      BaseTier: Tier option
      /// The content stamp of the row's base record (`None`: no base, or one that cannot be read).
      BaseContentStamp: Stamp option }

/// The columns the scan compares, taken from the entry as it is stored.
type Comparison =
    { ContentHash: string
      TagsHash: string
      Sticky: bool
      Tier: Tier }

/// The row's new state, pending stamps, `pushing_stamps` text and comparison columns. The base is not here: the
/// caller renders it from the decision's `Base` and the record. `Comparison = None`: leave the columns as they are.
type RowAfter =
    { State: RowState
      Pending: GroupStamps
      PushingText: string option
      Comparison: Comparison option }

let private warmer (a: Tier) (b: Tier) : bool = tierRank a < tierRank b

/// The row an entry has after the decision was carried out (decision 12.3). `Ok None`: write no row (there is none and
/// the decision creates none). `issue` issues a stamp above a floor (`None` when the clock range is exhausted, which
/// is an `Error`). `stored` is the entry as it is stored **after** the store operations; `hadEntry` whether an entry
/// existed when the decision was made; `colderTier` whether the hot limit stored or moved it to the warm tier. A
/// `LeaveEntry` with `FreshContentStamp` issues the pending content stamp above the greater of the pending one and the
/// base's (D10); a `DeleteEntry` that makes the row `forgotten` also clears `pushing_stamps` (D6).
let planRow
    (d: MergeDecision)
    (before: RowBefore)
    (theirs: SyncRecord option)
    (stored: LocalEntry option)
    (hadEntry: bool)
    (colderTier: bool)
    (issue: Stamp option -> Stamp option)
    : Result<RowAfter option, string> =
    let createsRow =
        match d.Entry with
        | InsertFromRecord
        | MergeGroups _ -> true
        | _ -> d.RowState.IsSome
    if before.State.IsNone && not createsRow then
        Ok None
    else
        let restamp (recordStamp: Stamp option) (current: Stamp option) : Result<Stamp option, string> =
            match issue (SyncScan.floorStamp recordStamp current) with
            | Some s -> Ok(Some s)
            | None -> Error "the clock cannot issue a stamp for this group"
        let pending : Result<GroupStamps, string> =
            match d.Entry with
            | MergeGroups m ->
                let apply (action: StampAction) (recordStamp: Stamp option) (current: Stamp option) =
                    match action with
                    | KeepPending -> Ok current
                    | ClearPending -> Ok None
                    | RestampPending -> restamp recordStamp current
                let c = apply (snd m.Content) (theirs |> Option.map (fun t -> t.ContentStamp)) before.Pending.Content
                let s = apply (snd m.Sticky) (theirs |> Option.map (fun t -> t.Placement.StickyStamp)) before.Pending.Sticky
                let t = apply (snd m.Tier) (theirs |> Option.map (fun t -> t.Placement.TierStamp)) before.Pending.Tier
                match c, s, t with
                | Ok c, Ok s, Ok t -> Ok { GroupStamps.Content = c; Sticky = s; Tier = t }
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e
            | LeaveEntry when d.FreshContentStamp ->
                // An edit of the text beat a remote deletion (D10): a new content stamp above the pending and the base's.
                restamp before.BaseContentStamp before.Pending.Content
                |> Result.map (fun c -> { before.Pending with Content = c })
            | outcome ->
                if clearsPending outcome hadEntry then Ok { GroupStamps.Content = None; Sticky = None; Tier = None }
                else Ok before.Pending
        match pending with
        | Error e -> Error e
        | Ok pending ->
            // The hot limit stored the entry colder than the decision meant: a pending tier stamp that the stored tier
            // no longer exceeds the base's for is dropped, as the scan would drop it.
            let pending =
                match stored, pending.Tier with
                | Some l, Some _ when colderTier ->
                    let baseTier =
                        match d.Base.Tier, theirs with
                        | BaseBecomesTheirs, Some t -> Some t.Placement.Tier
                        | _ -> before.BaseTier
                    match baseTier with
                    | Some bt when not (warmer l.Tier bt) -> { pending with Tier = None }
                    | _ -> pending
                | _ -> pending
            let pushingText =
                let becameContent = d.Base.Content = BaseBecomesTheirs
                let becameSticky = d.Base.Sticky = BaseBecomesTheirs
                let becameTier = d.Base.Tier = BaseBecomesTheirs
                let deleted =
                    match d.Entry, d.RowState with
                    | DeleteEntry, Some Forgotten -> true
                    | _ -> false
                if deleted then None // D6: an applied remote deletion proves nothing was sent any more
                elif not (becameContent || becameSticky || becameTier) then before.PushingText
                else
                    let current = PushingStamps.decode (defaultArg before.PushingText null)
                    PushingStamps.encode
                        { GroupStamps.Content = (if becameContent then None else current.Content)
                          Sticky = (if becameSticky then None else current.Sticky)
                          Tier = (if becameTier then None else current.Tier) }
            let comparison =
                stored
                |> Option.map (fun l ->
                    { ContentHash = SyncScan.contentHash l.Content
                      TagsHash = SyncScan.tagsHash l.Tags
                      Sticky = l.Sticky
                      Tier = l.Tier })
            Ok(
                Some
                    { State = defaultArg d.RowState (defaultArg before.State Active)
                      Pending = pending
                      PushingText = pushingText
                      Comparison = comparison }
            )
