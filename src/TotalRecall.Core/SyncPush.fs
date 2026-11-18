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

// ---------------------------------------------------------------------------------------------
// What is written

/// How the record's source tool changes (decision 6): kept unless the entry's tool differs, or the entry has none while
/// the record has a parsed one and no unparsed `source` text (a record written by a newer version may carry a source this
/// version cannot parse, and clearing it would lose it for every machine).
type SourceChange =
    | KeepSource
    | SetSource of SourceTool
    | ClearSource

let sourceChange (local: SourceTool option) (record: SourceTool option) (recordHasUnparsedSource: bool) : SourceChange =
    match local, record with
    | Some l, Some r when l = r -> KeepSource
    | Some l, _ -> SetSource l
    | None, Some _ when not recordHasUnparsedSource -> ClearSource
    | None, _ -> KeepSource

/// The content group, written whole: the entry's project and scope (an absent one clears the record's) and the source
/// change, with the row's pending content stamp.
type ContentWrite =
    { Stamp: Stamp
      Project: string option
      Scope: string option
      Source: SourceChange }

/// The groups of a changed entry that are written. `Stale`: the groups whose pending stamp was not greater than the
/// record's (not written; `content`, `sticky` or `tier`).
type GroupPlan =
    { Content: ContentWrite option
      Tags: string list option
      Sticky: Stamp option
      Tier: Stamp option
      Stale: string list }

let private greater (a: Stamp) (b: Stamp) : bool = Stamp.compare a b > 0

let private nonEmpty (v: string option) : string option =
    match v with
    | Some s when s <> "" -> Some s
    | _ -> None

/// Which groups of an entry with a record at `L` are written (decision 6): a group with a pending stamp that is greater
/// than the record's stamp for it, and the tags when they differ from the record's.
let planChanged (record: SyncRecord) (recordHasUnparsedSource: bool) (local: LocalEntry) (pending: GroupStamps) : GroupPlan =
    let stale = ResizeArray<string>()
    let content =
        match pending.Content with
        | Some s when greater s record.ContentStamp ->
            Some
                { Stamp = s
                  Project = nonEmpty local.Content.Project
                  Scope = nonEmpty local.Content.Scope
                  Source = sourceChange local.Content.Source record.Source recordHasUnparsedSource }
        | Some _ ->
            stale.Add "content"
            None
        | None -> None
    let sticky =
        match pending.Sticky with
        | Some s when greater s record.Placement.StickyStamp -> Some s
        | Some _ ->
            stale.Add "sticky"
            None
        | None -> None
    let tier =
        match pending.Tier with
        | Some s when greater s record.Placement.TierStamp -> Some s
        | Some _ ->
            stale.Add "tier"
            None
        | None -> None
    let tags =
        let local' = normaliseTags local.Tags
        if local' = record.Tags then None else Some local'
    { Content = content
      Tags = tags
      Sticky = sticky
      Tier = tier
      Stale = List.ofSeq stale }

let nothingToWrite (p: GroupPlan) : bool =
    p.Content.IsNone && p.Tags.IsNone && p.Sticky.IsNone && p.Tier.IsNone

/// The stamps of the groups that are written, in the shape of the `pushing_stamps` column.
let pushedStamps (p: GroupPlan) : GroupStamps =
    { GroupStamps.Content = p.Content |> Option.map (fun c -> c.Stamp)
      Sticky = p.Sticky
      Tier = p.Tier }

/// The record a new entry becomes (decision 7). `Error "missing-stamp"` when the row lacks a pending content, sticky or
/// tier stamp (the scan stamps all three groups of a new entry, so this is defensive). A sticky entry is hot.
let newRecord
    (id: string)
    (local: LocalEntry)
    (pending: GroupStamps)
    (writtenBy: string)
    (origin: Origin option)
    : Result<SyncRecord, string> =
    match pending.Content, pending.Sticky, pending.Tier with
    | Some c, Some s, Some t ->
        Ok
            { Id = id
              Content = local.Content.Text
              EntryType = local.Content.EntryType
              Tags = normaliseTags local.Tags
              Source = local.Content.Source
              CreatedAtMs = local.Content.CreatedAtMs
              Project = nonEmpty local.Content.Project
              Scope = nonEmpty local.Content.Scope
              Placement =
                { Placement.Tier = (if local.Sticky then Hot else local.Tier)
                  TierStamp = t
                  Sticky = local.Sticky
                  StickyStamp = s }
              ContentStamp = c
              Origin = origin
              WrittenBy = Some writtenBy }
    | _ -> Error "missing-stamp"

/// The pending stamps and the `pushing_stamps` text of a row after a push (decision 12): a pending stamp is cleared only
/// when it equals the stamp that was pushed for its group, and the pushed groups leave the `pushing_stamps` text.
type RowAfterPush =
    { Pending: GroupStamps
      PushingText: string option }

let afterPush (pending: GroupStamps) (pushingText: string option) (pushed: GroupStamps) : RowAfterPush =
    let clear (p: Stamp option) (q: Stamp option) =
        match p, q with
        | Some a, Some b when a = b -> None
        | _ -> p
    let current = SyncApply.PushingStamps.decode (defaultArg pushingText null)
    let drop (c: Stamp option) (q: Stamp option) = if q.IsSome then None else c
    { Pending =
        { GroupStamps.Content = clear pending.Content pushed.Content
          Sticky = clear pending.Sticky pushed.Sticky
          Tier = clear pending.Tier pushed.Tier }
      PushingText =
        SyncApply.PushingStamps.encode
            { GroupStamps.Content = drop current.Content pushed.Content
              Sticky = drop current.Sticky pushed.Sticky
              Tier = drop current.Tier pushed.Tier } }

// ---------------------------------------------------------------------------------------------
// The result of a push, the commit message, the label

/// Whether a failed push may have reached the remote (decision 11).
type Published =
    | No
    | Yes
    | Unknown

/// For a failure of the push command itself. `Yes` is never the answer here (it is only after the command succeeded).
let publishedAfterPush (kind: SyncGit.FailureKind) (stderr: string) : Published =
    match kind with
    | SyncGit.FailureKind.Cancelled
    | SyncGit.FailureKind.TimedOut
    | SyncGit.FailureKind.Network
    | SyncGit.FailureKind.OutputTooLarge -> Unknown
    | SyncGit.FailureKind.Other ->
        let t = (if isNull stderr then "" else stderr).ToLowerInvariant()
        if t.Contains("[remote rejected]", StringComparison.Ordinal)
           || t.Contains("[rejected]", StringComparison.Ordinal)
           || t.Contains("hook declined", StringComparison.Ordinal) then
            No
        else
            Unknown
    | _ -> No

/// `sync: <label> +<added> ~<changed> -0`: the push never deletes.
let commitMessage (added: int) (changed: int) (label: string) : string =
    sprintf "sync: %s +%d ~%d -0" label added changed

/// The OS machine name reduced to `A-Z a-z 0-9 . _ -` (every other character removed), at most 40 characters, else
/// `unknown`. Display only: never identity, never in a path.
let machineLabel (name: string) : string =
    let kept =
        (if isNull name then "" else name)
        |> Seq.filter (fun c -> Char.IsAsciiLetterOrDigit c || c = '.' || c = '_' || c = '-')
        |> Seq.truncate 40
        |> Seq.toArray
        |> String
    if kept = "" then "unknown" else kept

// ---------------------------------------------------------------------------------------------
// The staged set

/// One line of `diff --cached --name-status -z --no-renames`.
type StagedEntry = { Status: string; Path: string }

/// Parses the `-z` output `A\0path\0M\0path\0`. Anything malformed (an odd number of fields, a status that is not one
/// letter, a rename or copy shape) is an `Error`.
let parseStagedSet (stdout: byte[]) : Result<StagedEntry list, string> =
    if isNull stdout then Error "no output"
    else
        let text =
            try
                Ok(Text.UTF8Encoding(false, true).GetString stdout)
            with :? Text.DecoderFallbackException ->
                Error "the listing is not valid UTF-8"
        match text with
        | Error e -> Error e
        | Ok text ->
            let fields = text.Split('\000')
            // the text ends with a NUL, so the last field is empty
            let fields =
                if fields.Length > 0 && fields.[fields.Length - 1] = "" then Array.sub fields 0 (fields.Length - 1) else fields
            if fields.Length % 2 <> 0 then Error "an odd number of fields"
            else
                let entries =
                    [ for i in 0 .. 2 .. fields.Length - 2 -> { Status = fields.[i]; Path = fields.[i + 1] } ]
                match entries |> List.tryFind (fun e -> e.Status.Length <> 1 || not (Char.IsAsciiLetterUpper e.Status.[0]) || e.Path = "") with
                | Some bad -> Error(sprintf "an unexpected status '%s'" bad.Status)
                | None -> Ok entries

/// True when every written path is staged exactly once as an addition or a modification and nothing else is staged.
let stagedSetMatches (written: string list) (staged: StagedEntry list) : bool =
    let stagedPaths = staged |> List.map (fun e -> e.Path)
    staged |> List.forall (fun e -> e.Status = "A" || e.Status = "M")
    && List.sort stagedPaths = List.sort written
    && (List.distinct written).Length = written.Length
