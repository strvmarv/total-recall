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
    let baseNewer =
        match b with
        | Some bs -> gt bs t
        | None -> false
    let provable = pushing = Some t || pending = Some t
    if provable && not baseNewer then Own
    else
        match b with
        | None -> Newer
        | Some bs ->
            let c = Stamp.compare t bs
            if c > 0 then Newer elif c < 0 then Older else Unchanged

/// One group's result: the apply, the pending-stamp action, whether the push step re-publishes the
/// base's value for the group (`Re`), and the reviews it opens.
type private G<'a> =
    { Value: 'a
      Action: StampAction
      Re: bool
      Revs: ReviewRequest list }

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
                if ls = rs then g LeaveSticky ClearPending []                    // FS1
                elif gt pv ts then g LeaveSticky KeepPending []                  // FS2
                elif not rs then g UnpinLocally ClearPending []                  // FS3
                else g ReviewNewPin ClearPending [ NewPin ]                      // FS4
            | None ->
                if ls = rs then g LeaveSticky KeepPending []                     // FS5
                elif not rs then g UnpinLocally KeepPending []                   // FS6
                else g ReviewNewPin KeepPending [ NewPin ]                       // FS7
        elif ls = rs then g LeaveSticky ClearPending []                          // NS1
        elif ls then                                                             // NS2: a local pin is kept and pushed
            (match p with
             | Some pv when gt pv ts -> g LeaveSticky KeepPending []
             | _ -> g LeaveSticky RestampPending [])
        else g ReviewNewPin ClearPending [ NewPin ]                              // NS3

let private warmer (a: Tier) (b: Tier) : bool = tierRank a < tierRank b

let private tierGroup (cls: Class) (hasBase: bool) (l: LocalEntry) (t: SyncRecord) (p: Stamp option) : G<Tier option> =
    let ts = t.Placement.TierStamp
    match settled cls p ts None with
    | Some r -> r
    | None ->
        let lt = l.Tier
        let rt = t.Placement.Tier
        let localWins =
            match p with
            | Some pv when gt pv ts -> KeepPending
            | _ -> RestampPending
        if hasBase then
            if warmer rt lt then g (Some rt) ClearPending []                     // FR1
            elif p.IsSome && lt = rt then g None ClearPending []                 // FR2
            elif p.IsSome && warmer lt rt then g None localWins []               // FR3
            else g None KeepPending []                                           // FR4
        elif lt = rt then g None ClearPending []
        elif warmer rt lt then g (Some rt) ClearPending []
        else g None localWins []

let private sameContent (l: ContentFields) (t: SyncRecord) : bool =
    l.Text = t.Content && l.Project = t.Project && l.Scope = t.Scope && l.EntryType = t.EntryType

/// A sticky entry holds a record's content as a pin-edit when text, project or scope differ.
let private pinDiffers (l: ContentFields) (t: SyncRecord) : bool =
    l.Text <> t.Content || l.Project <> t.Project || l.Scope <> t.Scope

let private contentGroup
    (cls: Class)
    (b: SyncRecord option)
    (effSticky: bool)
    (l: LocalEntry)
    (t: SyncRecord)
    (p: Stamp option)
    : G<ContentApply> =
    let ts = t.ContentStamp
    match settled cls p ts LeaveContent with
    | Some r -> r
    | None ->
        match b with
        | Some bb ->
            let pendingText = p.IsSome && l.Content.Text <> bb.Content
            let both = pendingText && t.Content <> bb.Content && t.Content <> l.Content.Text
            if sameContent l.Content t then                                      // FC1
                g LeaveContent (if p.IsSome then ClearPending else KeepPending) []
            elif effSticky && pinDiffers l.Content t && (p.IsNone || gt ts p.Value) then
                g HeldAsPinEdit ClearPending [ PinEdit ]                         // FC2
            elif p.IsNone then g TakeTheirs KeepPending []                       // FC3
            elif gt p.Value ts then                                              // FC4
                g LeaveContent KeepPending
                    (if both then [ Conflict { Side = Remote; Text = t.Content; Stamp = Some ts } ] else [])
            else                                                                 // FC5
                g TakeTheirs ClearPending
                    (if both then [ Conflict { Side = Side.Local; Text = l.Content.Text; Stamp = p } ] else [])
        | None ->
            if sameContent l.Content t then g AdoptCreatedAt ClearPending []     // NC1
            elif effSticky && pinDiffers l.Content t then
                g HeldAsPinEdit ClearPending [ PinEdit ]                         // NC2
            elif l.Content.Text = t.Content then g TakeTheirs ClearPending []    // NC3
            else                                                                 // NC4
                g TakeTheirs ClearPending [ Conflict { Side = Side.Local; Text = l.Content.Text; Stamp = p } ]

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
            |> Set.filter (fun e ->
                if bs.Contains e then ls.Contains e && ts.Contains e else ls.Contains e || ts.Contains e)
            |> Set.toList
        | _ -> Set.union ls ts |> Set.toList
    let result = normaliseTags result
    { Result = result
      ApplyLocal = result <> normaliseTags l
      PushNeeded = result <> normaliseTags t }

let private allTheirs : BaseUpdate =
    { BaseUpdate.Content = BaseBecomesTheirs
      Tags = BaseBecomesTheirs
      Sticky = BaseBecomesTheirs
      Tier = BaseBecomesTheirs }

let private keepAll : BaseUpdate =
    { BaseUpdate.Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }

let private classOf (input: MergeInput) (b: SyncRecord option) (t: SyncRecord) : Class * Class * Class =
    let c =
        classify t.ContentStamp (b |> Option.map (fun x -> x.ContentStamp)) input.Pending.Content input.Pushing.Content
    let s =
        classify t.Placement.StickyStamp (b |> Option.map (fun x -> x.Placement.StickyStamp)) input.Pending.Sticky input.Pushing.Sticky
    let r =
        classify t.Placement.TierStamp (b |> Option.map (fun x -> x.Placement.TierStamp)) input.Pending.Tier input.Pushing.Tier
    c, s, r

/// Per group: an `older` or `unchanged` group keeps its base; `own` and `newer` take the record's. Tags always do.
let private baseUpdateFor (input: MergeInput) (b: SyncRecord option) (t: SyncRecord) : BaseUpdate =
    match b with
    | None -> allTheirs
    | Some _ ->
        let c, s, r = classOf input b t
        let f cls =
            match cls with
            | Own
            | Newer -> BaseBecomesTheirs
            | Older
            | Unchanged -> KeepBase
        { BaseUpdate.Content = f c; Tags = BaseBecomesTheirs; Sticky = f s; Tier = f r }

let private mergeGroups
    (input: MergeInput)
    (b: SyncRecord option)
    (l: LocalEntry)
    (t: SyncRecord)
    : EntryOutcome * ReviewRequest list =
    let cc, cs, cr = classOf input b t
    let hasBase = b.IsSome
    let sticky = stickyGroup cs hasBase l t input.Pending.Sticky      // decided first
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

// ---------------------------------------------------------------------------------------------
// The whole-entry table

let private baseDecision : MergeDecision =
    { Entry = Ignore
      Base = keepAll
      RowState = None
      Reviews = []
      Notes = []
      CloseHeldDelete = false
      FreshContentStamp = false }

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

// W6 (Task 4)
let private absentRecordEntryPresent (input: MergeInput) (bb: SyncRecord) (l: LocalEntry) : MergeDecision * bool =
    baseDecision, false

// W8 (Task 4)
let private presentRecordEntryGone (input: MergeInput) (bb: SyncRecord) (t: SyncRecord) : MergeDecision =
    baseDecision

/// The held-delete item closes when the situation it was opened for is over.
let private finish (input: MergeInput) (forceClose: bool) (d: MergeDecision) : MergeDecision =
    let close =
        match d.Entry, input.HeldDelete with
        | Defer, _ -> false
        | _, None -> false
        | _, Some _ when forceClose -> true
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
                Reviews = reviews },
            false
        | Some Active, Some t, None ->
            match b with
            | Some bb -> presentRecordEntryGone input bb t, false                                   // W8
            | None ->                                                                               // W9
                if input.Pushing.Content = Some t.ContentStamp then
                    { baseDecision with Entry = PushDeletion; Base = allTheirs }, false
                else
                    { baseDecision with Entry = Ignore; Base = allTheirs; RowState = Some Forgotten }, false
        | (None | Some Active), None, Some l ->
            match b with
            | Some bb -> absentRecordEntryPresent input bb l                                        // W6
            | None -> { baseDecision with Entry = LeaveEntry }, false                               // W7
        | Some Active, None, None ->
            match b with
            | Some _ -> { baseDecision with Entry = Ignore; RowState = Some Forgotten }, false      // W10
            | None -> { baseDecision with Entry = DropRow }, false                                  // W11
        | Some Forgotten, None, None ->
            match b with
            | Some _ -> { baseDecision with Entry = Ignore; RowState = Some Forgotten }, false      // W10
            | None -> baseDecision, false                                                           // W12
        | Some Left, None, None -> baseDecision, false                                              // W12
        | None, None, None -> baseDecision, false                                                   // W13
    finish input forceClose d
