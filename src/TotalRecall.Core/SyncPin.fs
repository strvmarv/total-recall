/// Git sync, pin review (Part 3): the pure rules of a pin item (a `new-pin` or `pin-edit` row of `git_sync_review`).
/// The pull no longer postpones a pinned record; it carries the decision out as for any entry and then, in the same
/// transaction, reconciles the entry's item with what it did (`reconcile`) and applies the arrival rule (`arrival`).
/// `shownHash` is the `record_hash` of an item: the hash of what the item shows (text, project, scope), so a change
/// to anything else never makes an answer stale. Pure and total: no I/O, no clock.
module TotalRecall.Core.SyncPin

open System.Text
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncScan

/// What an item is about: a record that pins an entry that is not pinned here, or a held edit of a pinned entry.
type ItemKind =
    | NewPinItem
    | PinEditItem

/// `new-pin` or `pin-edit`, as stored in the `kind` column.
let kindName (k: ItemKind) : string =
    match k with
    | NewPinItem -> "new-pin"
    | PinEditItem -> "pin-edit"

let tryKind (s: string) : ItemKind option =
    match s with
    | "new-pin" -> Some NewPinItem
    | "pin-edit" -> Some PinEditItem
    | _ -> None

/// `Unseen`: what the item shows changed since it was shown; every reader treats it as open.
type ItemState =
    | Open
    | Unseen
    | Approved
    | Declined

/// `open`, `unseen`, `approved` or `declined`, as stored in the `state` column.
let stateName (s: ItemState) : string =
    match s with
    | Open -> "open"
    | Unseen -> "unseen"
    | Approved -> "approved"
    | Declined -> "declined"

let tryState (s: string) : ItemState option =
    match s with
    | "open" -> Some Open
    | "unseen" -> Some Unseen
    | "approved" -> Some Approved
    | "declined" -> Some Declined
    | _ -> None

/// A pin item as the reconcile sees it: its kind, its state and its stored `record_hash`.
type PinItemView =
    { Kind: ItemKind
      State: ItemState
      Hash: string }

/// The stored entry after the pull's decision: its sticky flag, its content-group hash (`SyncScan.contentHash`) and
/// the shown hash of its text, project and scope.
type EntryView =
    { Sticky: bool
      ContentHash: string
      Shown: string }

/// The `git_sync_entry` row after the decision: its state, whether it has a base, whether a content stamp is pending
/// and its `scanned_content_hash`.
type PinRowView =
    { State: RowState
      HasBase: bool
      PendingContent: bool
      ScannedContentHash: string option }

/// The post-decision base: the shown hash of its content group and its sticky flag.
type BaseView = { Shown: string; Sticky: bool }

type ReconcileInput =
    { Item: PinItemView
      /// `None`: no entry with this id.
      Entry: EntryView option
      /// `None`: no row for this id.
      Row: PinRowView option
      /// `None`: no base.
      Base: BaseView option
      /// Whether the record is present in the tree the pull reads.
      RecordPresent: bool }

/// What to do with the item. `MarkUnseen hash`: state `unseen` with this hash; `ApplyBaseThenRemove`: the base's
/// content group is applied to the entry (a local unpin applies the held edit), then the item is removed.
type Reconciled =
    | Keep
    | Remove
    | MarkUnseen of hash: string
    | ApplyBaseThenRemove

/// What the arrival of a pin request does: write the item `open`, leave it, or mark it `unseen` with the new hash.
type Arrived =
    | OpenItem
    | LeaveItem
    | UnseenItem

// ---------------------------------------------------------------------------------------------
// The shown hash

/// SHA-256 (lowercase hex) of `total-recall/review/pin/v1\n`, then the text, the project and the scope, each as
/// `contentHash` encodes a field (a presence byte, a 4-byte big-endian length, the UTF-8 bytes; `None` and the empty
/// string differ).
let shownHash (text: string) (project: string option) (scope: string option) : string =
    let buf = ResizeArray<byte>()
    addBytes buf (Encoding.UTF8.GetBytes "total-recall/review/pin/v1\n")
    addField buf (Some text)
    addField buf project
    addField buf scope
    sha256Hex buf

/// The view of a stored entry: its sticky flag, content-group hash and shown hash.
let entryView (l: LocalEntry) : EntryView =
    { Sticky = l.Sticky
      ContentHash = contentHash l.Content
      Shown = shownHash l.Content.Text l.Content.Project l.Content.Scope }

// ---------------------------------------------------------------------------------------------
// The arrival rule

/// The arrival of a `NewPin` or `PinEdit` request the decision carried, given the item that exists for the id
/// (state and hash) after the reconcile: none, or an answered one, opens; an open or unseen one with the same hash is
/// left; with a different hash it turns unseen.
let arrival (existing: (ItemState * string) option) (hash: string) : Arrived =
    match existing with
    | None -> OpenItem
    | Some((Approved | Declined), _) -> OpenItem
    | Some((Open | Unseen), h) -> if h = hash then LeaveItem else UnseenItem

// ---------------------------------------------------------------------------------------------
// The reconcile

let private isLive (state: ItemState) : bool =
    match state with
    | Open
    | Unseen -> true
    | Approved
    | Declined -> false

let private newPin (i: ReconcileInput) (entry: EntryView option) : Reconciled =
    let rowGone = i.Row |> Option.forall (fun r -> r.State <> Active)
    match entry with
    | None -> Remove
    | Some _ when rowGone -> Remove
    | Some e when e.Sticky -> Remove
    | Some _ when not i.RecordPresent || i.Base |> Option.forall (fun b -> not b.Sticky) -> Remove
    | Some e when isLive i.Item.State && e.Shown <> i.Item.Hash -> MarkUnseen e.Shown
    | Some _ -> Keep

let private pinEdit (i: ReconcileInput) (entry: EntryView option) : Reconciled =
    let row =
        match i.Row with
        | Some r when r.State = Active && r.HasBase -> Some r
        | _ -> None
    match entry, row, i.Base with
    | None, _, _
    | _, None, _
    | _, _, None -> Remove
    | Some e, Some _, Some b when e.Shown = b.Shown -> Remove
    | Some e, Some r, Some _ when r.PendingContent || r.ScannedContentHash <> Some e.ContentHash -> Remove
    | Some e, Some _, Some _ when not e.Sticky -> ApplyBaseThenRemove
    | Some _, Some _, Some b when isLive i.Item.State && b.Shown <> i.Item.Hash -> MarkUnseen b.Shown
    | Some _, Some _, Some _ -> Keep

/// What the pull does with an item, from the post-decision state: the first matching rule, per kind, in the order of
/// the design (D4). A `pin-edit` item whose row has a base but whose base view is absent is treated as having no base.
let reconcile (i: ReconcileInput) : Reconciled =
    match i.Item.Kind with
    | NewPinItem -> newPin i i.Entry
    | PinEditItem -> pinEdit i i.Entry
