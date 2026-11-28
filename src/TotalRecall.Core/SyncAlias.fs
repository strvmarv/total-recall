/// Git sync, milestone 1b-iii part 5 (#165): the pure rules of alias detection and convergence. Pure and total: no I/O.
module TotalRecall.Core.SyncAlias

open System
open TotalRecall.Core.SyncMerge

let private ordinal (xs: string list) : string list =
    xs |> List.distinct |> List.sortWith (fun x y -> String.CompareOrdinal(x, y))

/// The ordinal minimum. `invalidArg` on an empty list.
let canonical (ids: string list) : string =
    match ordinal ids with
    | [] -> invalidArg "ids" "canonical needs at least one id"
    | first :: _ -> first

type DetectInput =
    { RecordId: string
      /// Existing, eligible local memory ids whose import_log rows carry the record's origin hash, without RecordId.
      Targets: string list
      /// (canonical, other) of every `open` git_sync_alias row with the same origin hash.
      OpenRows: (string * string) list
      /// The ids of OpenRows that exist here as a memory.
      LocalMembers: Set<string>
      /// (canonical, other) of every git_sync_alias row, any state.
      ExistingRows: Set<string * string> }

type Detection =
    { Canonical: string
      /// The union of RecordId, Targets and both ids of OpenRows, distinct, ordinal.
      Members: string list
      /// (Canonical, x) for every member x that is RecordId, a Target or a LocalMember, x <> Canonical,
      /// and not in ExistingRows; ordinal by x.
      Rows: (string * string) list }

/// None when Targets is empty (no alias: the record is inserted as usual).
let detect (input: DetectInput) : Detection option =
    if List.isEmpty input.Targets then
        None
    else
        let members =
            ordinal (
                [ input.RecordId ]
                @ input.Targets
                @ (input.OpenRows |> List.collect (fun (c, o) -> [ c; o ]))
            )
        let canon = canonical members
        let present = Set.ofList (input.RecordId :: input.Targets) |> Set.union input.LocalMembers
        let rows =
            members
            |> List.filter (fun x -> x <> canon && present.Contains x && not (input.ExistingRows.Contains((canon, x))))
            |> List.map (fun x -> (canon, x))
        Some { Canonical = canon; Members = members; Rows = rows }

/// The plain Ignore: Entry Ignore, Base all KeepBase, RowState None, Reviews [], Notes [],
/// CloseHeldDelete false, FreshContentStamp false.
let ignored : MergeDecision =
    { Entry = Ignore
      Base = { BaseUpdate.Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }
      RowState = None
      Reviews = []
      Notes = []
      CloseHeldDelete = false
      FreshContentStamp = false }

/// D1: an InsertFromRecord becomes `ignored`; every other decision is returned unchanged.
let skipInstead (d: MergeDecision) : MergeDecision =
    match d.Entry with
    | InsertFromRecord -> ignored
    | _ -> d

/// D3 condition 3: the ordinally lowest candidate in `parses` (accepted at R and parsing); None when none.
let target (candidates: string list) (parses: Set<string>) : string option =
    candidates |> ordinal |> List.tryFind parses.Contains

/// One local id meeting conditions 1 to 3 and 7, with its target (None: no candidate parses) and whether the
/// target id is free here (condition 5).
type Candidate = { Id: string; Target: string option; TargetFree: bool }

type Choice = { Converging: Map<string, string>; Ignored: Set<string> }

/// D2/D3: a candidate with no target or a target that is not free is ignored; of the candidates that share a
/// target the ordinally lowest converges onto it and the others are ignored (condition 6).
let choose (candidates: Candidate list) : Choice =
    let usable = candidates |> List.filter (fun c -> c.Target.IsSome && c.TargetFree)
    let winners =
        usable
        |> List.groupBy (fun c -> c.Target.Value)
        |> List.map (fun (t, group) -> (canonical (group |> List.map (fun c -> c.Id)), t))
    let converging = Map.ofList winners
    let ignored =
        candidates
        |> List.map (fun c -> c.Id)
        |> List.filter (fun id -> not (converging.ContainsKey id))
        |> Set.ofList
    { Converging = converging; Ignored = ignored }
