module TotalRecall.Core.Tests.SyncMergeTests

open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge

let private m1 = (MachineId.tryCreate "0000000a").Value // this machine
let private m2 = (MachineId.tryCreate "0000000b").Value // another machine

let private st (n: int) (m: MachineId) : Stamp =
    { TimeMs = 1_700_000_000_000L + int64 n * 1000L; Counter = 0; Machine = m }

/// Our stamp at second n, and a remote stamp at second n.
let private ours n = st n m1
let private theirs n = st n m2

let private placement (tier: Tier) (tierStamp: Stamp) (sticky: bool) (stickyStamp: Stamp) : Placement =
    { Placement.Tier = tier; TierStamp = tierStamp; Sticky = sticky; StickyStamp = stickyStamp }

/// A record at stamp 1 of the other machine: warm, not sticky, no tags.
let private record (text: string) : SyncRecord =
    { Id = "3f9c2a7e"
      Content = text
      EntryType = Decision
      Tags = []
      Source = None
      CreatedAtMs = 1_700_000_000_000L
      Project = None
      Scope = None
      Placement = placement Warm (theirs 1) false (theirs 1)
      ContentStamp = theirs 1
      Origin = None
      WrittenBy = None }

let private fields (text: string) : ContentFields =
    { Text = text; Project = None; Scope = None; EntryType = Decision; Source = None; CreatedAtMs = 1_700_000_000_000L }

let private local (text: string) : LocalEntry =
    { LocalEntry.Content = fields text; Tags = []; Tier = Warm; Sticky = false }

let private noStamps : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

/// The neutral input: an active row, base = record = local text "base", everything at stamp 1, proven apply.
let private input : MergeInput =
    { Row = Some Active
      Base = Some (record "base")
      Theirs = Some (record "base")
      Local = Some (local "base")
      Pending = noStamps
      Pushing = noStamps
      ApplyProven = true
      HeldDelete = None }

let private withTheirs (f: SyncRecord -> SyncRecord) (i: MergeInput) : MergeInput = { i with Theirs = i.Theirs |> Option.map f }
let private withBase (f: SyncRecord -> SyncRecord) (i: MergeInput) : MergeInput = { i with Base = i.Base |> Option.map f }
let private withLocal (f: LocalEntry -> LocalEntry) (i: MergeInput) : MergeInput = { i with Local = i.Local |> Option.map f }
let private withText (t: string) (e: LocalEntry) : LocalEntry = { e with Content = { e.Content with Text = t } }
let private withContentStamp (s: Stamp) (r: SyncRecord) : SyncRecord = { r with ContentStamp = s }

let private withSticky (sticky: bool) (s: Stamp) (r: SyncRecord) : SyncRecord =
    { r with Placement = { r.Placement with Sticky = sticky; StickyStamp = s } }

let private withTier (tier: Tier) (s: Stamp) (r: SyncRecord) : SyncRecord =
    { r with Placement = { r.Placement with Tier = tier; TierStamp = s } }

/// The `Merged` of a decision that must be `MergeGroups`.
let private groups (d: MergeDecision) : Merged =
    match d.Entry with
    | MergeGroups m -> m
    | other -> failtestf "expected MergeGroups, got %A" other

let private applyToContent (m: Merged) : ContentApply = fst m.Content
let private pendingOfContent (m: Merged) : StampAction = snd m.Content

[<Tests>]
let typeTests =
    testList "SyncMerge types" [
        testCase "the neutral input builds" <| fun _ ->
            Expect.equal input.Row (Some Active) "row"
            Expect.equal (input.Local |> Option.map (fun l -> l.Content.Text)) (Some "base") "local text"
            Expect.isTrue (Stamp.compare (ours 1) (theirs 1) <> 0) "the two machines' stamps differ"
    ]
