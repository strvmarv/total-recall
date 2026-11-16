/// Git sync, milestone 1b-i-1: the pure rules of the scan. The scan (the local step of the sync cycle) compares every
/// eligible memory with its `git_sync_entry` row; `decide` says, for one id, what to do to the row. The two hashes
/// are the row's `scanned_content_hash` and `scanned_tags_hash`: the apply of a later milestone must set them from the
/// stored entry with these same functions, or the next scan would read a pulled change as a local edit.
/// Pure and total: no I/O, no clock (a stamp is issued by the caller from the action).
module TotalRecall.Core.SyncScan

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge

// ---------------------------------------------------------------------------------------------
// The hashes

let private lengthBytes (n: int) : byte[] =
    [| byte (n >>> 24); byte (n >>> 16); byte (n >>> 8); byte n |]

let private addBytes (buf: ResizeArray<byte>) (bytes: byte[]) = buf.AddRange bytes

/// `0x00` for an absent value, else `0x01`, a 4-byte big-endian length and the UTF-8 bytes.
let private addField (buf: ResizeArray<byte>) (value: string option) =
    match value with
    | None -> buf.Add 0uy
    | Some s ->
        let bytes = Encoding.UTF8.GetBytes s
        buf.Add 1uy
        addBytes buf (lengthBytes bytes.Length)
        addBytes buf bytes

let private sha256Hex (buf: ResizeArray<byte>) : string =
    Convert.ToHexString(SHA256.HashData(buf.ToArray())).ToLowerInvariant()

/// SHA-256 (lowercase hex) of `total-recall/scan/content/v1\n` and the six content fields in this order: text,
/// project, scope, entry type name, source tool name, `created_at` in milliseconds as decimal ASCII.
let contentHash (c: ContentFields) : string =
    let buf = ResizeArray<byte>()
    addBytes buf (Encoding.UTF8.GetBytes "total-recall/scan/content/v1\n")
    addField buf (Some c.Text)
    addField buf c.Project
    addField buf c.Scope
    addField buf (Some(entryTypeName c.EntryType))
    addField buf (c.Source |> Option.map sourceToolName)
    addField buf (Some(c.CreatedAtMs.ToString(CultureInfo.InvariantCulture)))
    sha256Hex buf

/// SHA-256 (lowercase hex) of `total-recall/scan/tags/v1\n`, the 4-byte big-endian count of the normalised tags and each
/// tag as a 4-byte big-endian length and its UTF-8 bytes.
let tagsHash (tags: string list) : string =
    let tags = normaliseTags tags
    let buf = ResizeArray<byte>()
    addBytes buf (Encoding.UTF8.GetBytes "total-recall/scan/tags/v1\n")
    addBytes buf (lengthBytes tags.Length)
    for t in tags do
        let bytes = Encoding.UTF8.GetBytes t
        addBytes buf (lengthBytes bytes.Length)
        addBytes buf bytes
    sha256Hex buf

// ---------------------------------------------------------------------------------------------
// Stamps

/// The greater of two optional stamps (the floor a new stamp must exceed: the group's base stamp and its pending stamp).
let floorStamp (a: Stamp option) (b: Stamp option) : Stamp option =
    match a, b with
    | Some x, Some y -> Some(if Stamp.compare x y >= 0 then x else y)
    | Some x, None
    | None, Some x -> Some x
    | None, None -> None

// ---------------------------------------------------------------------------------------------
// The row, the actions and `decide`

/// What the row's base record says, read by the scanner from `base_json`. `Tier = None` and no stamps: a base that could
/// not be read ("has a base, tier unknown, no floor").
type BaseFacts =
    { Tier: Tier option
      ContentStamp: Stamp option
      StickyStamp: Stamp option
      TierStamp: Stamp option }

/// The columns of a `git_sync_entry` row that `decide` reads. `None` in an optional column means NULL ("nothing seen").
type RowView =
    { State: RowState
      /// `None`: no base (NULL `base_json`).
      Base: BaseFacts option
      HasPushingStamps: bool
      ContentHash: string option
      TagsHash: string option
      LocalSticky: bool option
      LocalTier: Tier option
      PendingContent: Stamp option
      PendingSticky: Stamp option
      PendingTier: Stamp option }

type GroupAction =
    | Unchanged
    /// Store the new hash, flag or tier; issue no stamp.
    | StoreValue
    /// Issue a stamp above `floor`, and store the new hash, flag or tier.
    | IssueStamp of floor: Stamp option
    /// The tier only: a promotion that was undone before it was pushed. Clear the pending tier stamp and store the tier.
    | DropPendingStamp

type RowChanges =
    { Content: GroupAction
      Tags: GroupAction
      Sticky: GroupAction
      Tier: GroupAction }

type ScanAction =
    | NoChange
    /// No row yet: insert an `active` row with no base, stamp content, sticky and tier, store the hashes, flag and tier.
    | NewRow
    /// A `forgotten` or `left` row whose memory exists again: replace the row with a fresh one, as `NewRow`.
    | Reactivate
    | UpdateRow of RowChanges
    /// The memory was retyped to knowledge: state `left`, clear the three stamps and `repush_groups`, keep the rest.
    | MoveToLeft
    /// An `active` row whose memory exists in no table. Reported, never acted on here.
    | ReportDeletion of hasBase: bool * hasPushingStamps: bool

/// One id: its row (if any), its local memory (if any) and whether the id exists as a knowledge entry.
let decide (row: RowView option) (local: LocalEntry option) (existsAsKnowledge: bool) : ScanAction =
    match row, local with
    | None, Some _ -> NewRow
    | None, None -> NoChange
    | Some r, Some l ->
        match r.State with
        | Forgotten
        | Left -> Reactivate
        | Active ->
            let baseOf (f: BaseFacts -> Stamp option) = r.Base |> Option.bind f
            let content =
                match r.ContentHash with
                | None -> StoreValue
                | Some h when h <> contentHash l.Content ->
                    IssueStamp(floorStamp (baseOf (fun b -> b.ContentStamp)) r.PendingContent)
                | Some _ -> Unchanged
            let tags =
                match r.TagsHash with
                | Some h when h = tagsHash l.Tags -> Unchanged
                | _ -> StoreValue
            let sticky =
                match r.LocalSticky with
                | None -> StoreValue
                | Some s when s <> l.Sticky -> IssueStamp(floorStamp (baseOf (fun b -> b.StickyStamp)) r.PendingSticky)
                | Some _ -> Unchanged
            let tier =
                let baseTier = r.Base |> Option.bind (fun b -> b.Tier)
                // The clearing test comes first: a tier that is not warmer than the base's is no promotion event.
                let notWarmerThanBase =
                    match baseTier with
                    | Some bt -> tierRank l.Tier >= tierRank bt
                    | None -> false
                if notWarmerThanBase then
                    match r.PendingTier with
                    | Some _ -> DropPendingStamp
                    | None -> if r.LocalTier = Some l.Tier then Unchanged else StoreValue
                else
                    match r.LocalTier with
                    | None -> StoreValue
                    | Some lt when tierRank l.Tier < tierRank lt ->
                        IssueStamp(floorStamp (baseOf (fun b -> b.TierStamp)) r.PendingTier)
                    | Some lt when lt <> l.Tier -> StoreValue
                    | Some _ -> Unchanged
            if content = Unchanged && tags = Unchanged && sticky = Unchanged && tier = Unchanged then
                NoChange
            else
                UpdateRow { Content = content; Tags = tags; Sticky = sticky; Tier = tier }
    | Some r, None ->
        match r.State with
        | Active ->
            if existsAsKnowledge then MoveToLeft else ReportDeletion(r.Base.IsSome, r.HasPushingStamps)
        | Forgotten
        | Left -> NoChange

// ---------------------------------------------------------------------------------------------
// The clock warning

/// `git_sync_clock_warning`: a set of kinds, only ever added to, so a condition that recurs on every scan does not make
/// an idle scan write. The text is `clock: ` and the kind names, sorted ordinally and joined by `,`.
module ClockWarning =

    type Kind =
        | ClockReset
        | ClockUnreadable
        | FarFuture
        | Exhausted

    let kindName (k: Kind) : string =
        match k with
        | ClockReset -> "clock-reset"
        | ClockUnreadable -> "clock-unreadable"
        | FarFuture -> "far-future"
        | Exhausted -> "exhausted"

    let private prefix = "clock: "

    /// The text to store after adding `kinds` to the stored `current`, or `None` when nothing has to be written (no kind
    /// to add, or every kind is already stored). A stored text that is not in this form is replaced.
    let addKinds (current: string option) (kinds: Kind list) : string option =
        if List.isEmpty kinds then
            None
        else
            let readable, stored =
                match current with
                | Some t when t.StartsWith(prefix, StringComparison.Ordinal) ->
                    true,
                    t.Substring(prefix.Length).Split(',', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
                | _ -> false, Set.empty
            let added = kinds |> List.map kindName |> Set.ofList
            let union = Set.union stored added
            if readable && union = stored then
                None
            else
                Some(prefix + String.Join(",", union))
