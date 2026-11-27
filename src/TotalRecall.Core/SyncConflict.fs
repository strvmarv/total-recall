/// Git sync, conflict review (Part 4): the pure rules of a conflict item (a `conflict` row of `git_sync_review`).
/// The pull no longer postpones a text conflict; it applies the winning text, keeps the losing text as an alternative
/// in the item and, in the same transaction, reconciles the item with what it did (`reconcile`). `shownHash` is the
/// `record_hash` of an item: the hash of the applied text and the kept alternatives, so a change to anything else
/// (a stamp, a machine) never makes an answer stale. Pure and total: no I/O, no clock.
module TotalRecall.Core.SyncConflict

open System.Text
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncScan

/// `Unseen`: what the item shows changed since it was shown; every reader treats it as open.
type ItemState =
    | Open
    | Unseen

/// `open` or `unseen`, as stored in the `state` column.
let stateName (s: ItemState) : string =
    match s with
    | Open -> "open"
    | Unseen -> "unseen"

let tryState (s: string) : ItemState option =
    match s with
    | "open" -> Some Open
    | "unseen" -> Some Unseen
    | _ -> None

/// `local` or `remote`, as stored in a loser's `side`.
let sideName (s: Side) : string =
    match s with
    | Local -> "local"
    | Remote -> "remote"

let trySide (s: string) : Side option =
    match s with
    | "local" -> Some Local
    | "remote" -> Some Remote
    | _ -> None

/// A kept alternative: the text that lost, the side it came from, its stamp in text form (`""`: none) and the machine
/// (8 hex digits) of that stamp, or this machine's when there was none.
type Loser =
    { Side: Side
      Text: string
      Stamp: string
      Machine: string }

/// The most alternatives an item keeps: a ninth drops the oldest.
let maxLosers : int = 8

/// The kept form of the loser a merge decision carried: its stamp's text and machine, else `""` and `thisMachine`.
let loserOf (thisMachine: string) (l: ConflictLoser) : Loser =
    match l.Stamp with
    | Some s ->
        { Side = l.Side
          Text = l.Text
          Stamp = Stamp.format s
          Machine = MachineId.value s.Machine }
    | None ->
        { Side = l.Side
          Text = l.Text
          Stamp = ""
          Machine = thisMachine }

// ---------------------------------------------------------------------------------------------
// The shown hash

/// SHA-256 (lowercase hex) of `total-recall/review/conflict/v1\n`, the applied text as a field (a presence byte, a
/// 4-byte big-endian length, the UTF-8 bytes; `None`, the entry absent while a deletion is held, is the byte `0x00`),
/// the 4-byte big-endian count of the losers and each loser's text as a field, in order. Stamps and machines are not
/// hashed.
let shownHash (applied: string option) (losers: string list) : string =
    let buf = ResizeArray<byte>()
    addBytes buf (Encoding.UTF8.GetBytes "total-recall/review/conflict/v1\n")
    addField buf applied
    addBytes buf (lengthBytes losers.Length)
    for t in losers do
        addField buf (Some t)
    sha256Hex buf

// ---------------------------------------------------------------------------------------------
// Appending a loser

/// The one append rule: a loser whose text equals a kept one is not added again (the same text from either side is one
/// alternative); otherwise it goes at the end and the oldest are dropped while there are more than `maxLosers`.
let append (losers: Loser list) (loser: Loser) : Loser list =
    if losers |> List.exists (fun l -> l.Text = loser.Text) then
        losers
    else
        let all = losers @ [ loser ]
        if all.Length > maxLosers then List.skip (all.Length - maxLosers) all else all

// ---------------------------------------------------------------------------------------------
// Whether a deletion is held after the decision

/// Whether the id has a held deletion after the decision: a decision that holds one does; one that carries out or
/// pushes a deletion does not; any other keeps an open or approved item (`heldOpenOrApproved`, read before the
/// decision) unless the decision closes it.
let heldAfter (d: MergeDecision) (heldOpenOrApproved: bool) : bool =
    match d.Entry with
    | HoldDeletion _ -> true
    | PushDeletion
    | DeleteEntry -> false
    | _ -> heldOpenOrApproved && not d.CloseHeldDelete

// ---------------------------------------------------------------------------------------------
// The reconcile

/// A conflict item as the reconcile sees it: its state, its stored `record_hash` and its kept losers.
type ItemView =
    { State: ItemState
      Hash: string
      Losers: Loser list }

type ReconcileInput =
    { Item: ItemView
      /// `None`: no entry with this id (held deletion or gone).
      EntryText: string option
      /// `None`: no row for this id.
      Row: RowState option
      /// Whether the id has a held deletion after the decision (`heldAfter`).
      HeldAfter: bool }

/// What to do with the item. `Rewrite(hash, losers)`: state `unseen`, this hash and these losers.
type Reconciled =
    | Keep
    | Remove
    | Rewrite of hash: string * losers: Loser list

/// What the pull does with an item, from the post-decision state: the first matching rule, in the order of the design (D4).
let reconcile (i: ReconcileInput) : Reconciled =
    match i.Row, i.EntryText with
    | Some Forgotten, _
    | Some Left, _ -> Remove
    | _, None -> if i.HeldAfter then Keep else Remove
    | _, Some entry ->
        let kept = i.Item.Losers |> List.filter (fun l -> l.Text <> entry)
        if List.isEmpty kept then
            Remove
        else
            let hash = shownHash (Some entry) (kept |> List.map (fun l -> l.Text))
            if hash <> i.Item.Hash then Rewrite(hash, kept) else Keep
