/// Git sync, milestone 1b-ii: the pure rules of deletions. A deletion is carried out only inside the guard
/// (decisions D3 to D5) and a decision that the guard or an unproven apply stops becomes a held item whose cause
/// is recorded (D7). This module holds what is decided without touching anything: the guard's limit and verdict,
/// the conversion of a deletion decision into a hold, the hold's cause word, and the text form of the
/// `repush_groups` column. Pure and total: no I/O, no clock.
module TotalRecall.Core.SyncDeletion

open TotalRecall.Core.SyncMerge

// ---------------------------------------------------------------------------------------------
// The delete guard

/// The most deletions one direction may carry out in a pull: `max 10 (active * percent / 100)` with integer
/// division. `percent` is clamped to 0..100 and a negative `active` counts as 0.
let guardLimit (percent: int) (active: int) : int =
    let p = max 0 (min 100 percent)
    let n = max 0 active
    max 10 (n * p / 100)

/// A direction trips the guard when its count is strictly greater than the limit.
let guardTrips (count: int) (limit: int) : bool = count > limit

// ---------------------------------------------------------------------------------------------
// Holds

/// The decision with a deletion turned into a held item: `DeleteEntry` becomes a remote-deletion hold and
/// `PushDeletion` a local-deletion hold, with no notes and no row state change (the row stays as it is).
/// Every other outcome is returned unchanged.
let holdInstead (d: MergeDecision) : MergeDecision =
    match d.Entry with
    | DeleteEntry -> { d with Entry = HoldDeletion RemoteDeletion; Notes = []; RowState = None }
    | PushDeletion -> { d with Entry = HoldDeletion LocalDeletion; Notes = []; RowState = None }
    | _ -> d

/// The word stored for a direction: `remote` or `local`.
let directionName (d: Direction) : string =
    match d with
    | RemoteDeletion -> "remote"
    | LocalDeletion -> "local"

/// Why a deletion is held: `guard` when the guard converted it; else `unproven` when the apply is not proven;
/// else `sticky` for a remote deletion (a proven remote hold is a pin) and `unproven` for a local one (`decide`
/// holds a local deletion only when it is unproven).
let holdCause (proven: bool) (converted: bool) (direction: Direction) : string =
    if converted then "guard"
    elif not proven then "unproven"
    else
        match direction with
        | RemoteDeletion -> "sticky"
        | LocalDeletion -> "unproven"

// ---------------------------------------------------------------------------------------------
// The `repush_groups` column

/// The group names in the order `content,sticky,tier` joined by `,`; no text when no group is flagged.
module RepushGroups =

    /// `None` when no group is flagged.
    let encode (f: GroupFlags) : string option =
        let names =
            [ if f.Content then "content"
              if f.Sticky then "sticky"
              if f.Tier then "tier" ]
        if names.IsEmpty then None else Some(String.concat "," names)

    /// Total: null, blank and any text that is not exactly a comma list of distinct names from
    /// `content|sticky|tier` in the fixed order read as no group flagged.
    let decode (text: string) : GroupFlags =
        let none : GroupFlags = { Content = false; Sticky = false; Tier = false }
        let all =
            [ for c in [ false; true ] do
                for s in [ false; true ] do
                    for t in [ false; true ] -> ({ Content = c; Sticky = s; Tier = t } : GroupFlags) ]
        match all |> List.tryFind (fun f -> encode f = Option.ofObj text) with
        | Some f -> f
        | None -> none
