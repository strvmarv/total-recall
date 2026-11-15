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

// ------------------------------------------------------------------------------------------------
// Task 2: the group rules, reached through W4 and W5 (an entry and a record both exist)

let private pendingContent (s: Stamp) (i: MergeInput) : MergeInput = { i with Pending = { i.Pending with Content = Some s } }
let private pendingSticky (s: Stamp) (i: MergeInput) : MergeInput = { i with Pending = { i.Pending with Sticky = Some s } }
let private pendingTier (s: Stamp) (i: MergeInput) : MergeInput = { i with Pending = { i.Pending with Tier = Some s } }
let private pushingContent (s: Stamp) (i: MergeInput) : MergeInput = { i with Pushing = { i.Pushing with Content = Some s } }
let private pushingSticky (s: Stamp) (i: MergeInput) : MergeInput = { i with Pushing = { i.Pushing with Sticky = Some s } }
let private pushingTier (s: Stamp) (i: MergeInput) : MergeInput = { i with Pushing = { i.Pushing with Tier = Some s } }
let private noBase (i: MergeInput) : MergeInput = { i with Base = None }
let private localSticky (i: MergeInput) : MergeInput = withLocal (fun l -> { l with Sticky = true }) i
/// The record's text and content stamp (newer than the base's by default).
let private theirText (text: string) (s: Stamp) : MergeInput -> MergeInput =
    withTheirs (fun r -> { r with Content = text; ContentStamp = s })

let private stickyOf (d: MergeDecision) : StickyApply * StampAction = (groups d).Sticky
let private tierOf (d: MergeDecision) : Tier option * StampAction = (groups d).Tier
let private expectContent (d: MergeDecision) (apply: ContentApply) (action: StampAction) (what: string) =
    Expect.equal (groups d).Content (apply, action) what

[<Tests>]
let settledFirstTests =
    testList "SyncMerge settled-first groups" [
        testCase "own content, pending equals the record's stamp: leave and clear; base becomes theirs" <| fun _ ->
            let i =
                { input with Theirs = Some { record "mine" with ContentStamp = ours 3 }; Local = Some (local "mine") }
                |> pendingContent (ours 3) |> pushingContent (ours 3)
            let d = decide i
            expectContent d LeaveContent ClearPending "own, p = t"
            Expect.equal d.Base.Content BaseBecomesTheirs "own takes the record's base"
            Expect.isEmpty d.Reviews "no review"

        testCase "own content, a newer pending change is kept" <| fun _ ->
            let i =
                { input with Theirs = Some { record "mine" with ContentStamp = ours 3 }; Local = Some (local "edit") }
                |> pendingContent (ours 5) |> pushingContent (ours 3)
            expectContent (decide i) LeaveContent KeepPending "own, p > t"

        testCase "an own stamp with a base newer than the record is not own: older, base kept, re-pushed" <| fun _ ->
            let i =
                { input with Theirs = Some { record "mine" with ContentStamp = ours 3 } }
                |> withBase (withContentStamp (theirs 9)) |> pushingContent (ours 3)
            let d = decide i
            expectContent d LeaveContent KeepPending "older"
            Expect.equal d.Base.Content KeepBase "older keeps the base"
            Expect.isTrue (groups d).Repush.Content "no pending stamp: re-push the base's value"

        testCase "a stamp with this machine's id that matches nothing remembered is an ordinary remote change" <| fun _ ->
            let i = input |> theirText "x" (ours 3)
            expectContent (decide i) TakeTheirs KeepPending "taken"

        testCase "older content with no pending stamp: leave, keep the base, re-push" <| fun _ ->
            let d = decide (input |> theirText "old" (theirs 0))
            expectContent d LeaveContent KeepPending "older"
            Expect.equal d.Base.Content KeepBase "base kept"
            Expect.isTrue (groups d).Repush.Content "repush"

        testCase "older content with a pending change: no re-push, base kept" <| fun _ ->
            let i = input |> theirText "old" (theirs 0) |> withLocal (withText "edit") |> pendingContent (ours 4)
            let d = decide i
            expectContent d LeaveContent KeepPending "older"
            Expect.isFalse (groups d).Repush.Content "a pending change goes through the normal path"
            Expect.equal d.Base.Content KeepBase "base kept"

        testCase "unchanged content: leave, keep pending, base kept, no re-push" <| fun _ ->
            let d = decide input
            expectContent d LeaveContent KeepPending "unchanged"
            Expect.equal d.Base.Content KeepBase "base kept"
            Expect.isFalse (groups d).Repush.Content "no repush"

        testCase "own sticky and own tier are settled the same way" <| fun _ ->
            let i =
                { input with Theirs = input.Theirs |> Option.map (withSticky true (ours 3) >> withTier Hot (ours 4)) }
                |> pendingSticky (ours 3) |> pushingSticky (ours 3) |> pendingTier (ours 4) |> pushingTier (ours 4)
            let d = decide i
            Expect.equal (stickyOf d) (LeaveSticky, ClearPending) "own sticky"
            Expect.equal (tierOf d) (None, ClearPending) "own tier"
            Expect.equal d.Base.Sticky BaseBecomesTheirs "sticky base"
            Expect.equal d.Base.Tier BaseBecomesTheirs "tier base"

        testCase "older sticky and older tier keep their bases and re-push" <| fun _ ->
            let i = { input with Theirs = input.Theirs |> Option.map (withSticky true (theirs 0) >> withTier Hot (theirs 0)) }
            let d = decide i
            Expect.equal (stickyOf d) (LeaveSticky, KeepPending) "older sticky"
            Expect.equal (tierOf d) (None, KeepPending) "older tier"
            Expect.equal d.Base.Sticky KeepBase "sticky base kept"
            Expect.equal d.Base.Tier KeepBase "tier base kept"
            Expect.isTrue (groups d).Repush.Sticky "sticky repush"
            Expect.isTrue (groups d).Repush.Tier "tier repush"

        testCase "tags always take the record's base" <| fun _ ->
            Expect.equal (decide input).Base.Tags BaseBecomesTheirs "tags"
    ]

[<Tests>]
let contentRuleTests =
    testList "SyncMerge content rules" [
        testCase "FC1 sameContent: leave; clear a pending stamp" <| fun _ ->
            let i = input |> theirText "same" (theirs 3) |> withLocal (withText "same")
            expectContent (decide i) LeaveContent KeepPending "no pending"
            expectContent (decide (pendingContent (ours 2) i)) LeaveContent ClearPending "pending cleared"

        testCase "FC2 a sticky local entry holds the record's text as a pin-edit" <| fun _ ->
            let i = input |> localSticky |> theirText "remote" (theirs 3)
            let d = decide i
            expectContent d HeldAsPinEdit ClearPending "held"
            Expect.equal d.Reviews [ PinEdit ] "one pin-edit"
            Expect.equal d.Base.Content BaseBecomesTheirs "the base becomes the record, so the held text is not a local change"
            expectContent (decide (pendingContent (ours 2) i)) HeldAsPinEdit ClearPending "an older pending stamp is superseded"

        testCase "FC2 a sticky local entry holds a project-only or a scope-only difference" <| fun _ ->
            let sticky = input |> localSticky |> theirText "base" (theirs 3)
            let p = sticky |> withTheirs (fun r -> { r with Project = Some "p" })
            let s = sticky |> withTheirs (fun r -> { r with Scope = Some "global" })
            for i in [ p; s ] do
                let d = decide i
                expectContent d HeldAsPinEdit ClearPending "held"
                Expect.equal d.Reviews [ PinEdit ] "pin-edit"

        testCase "FC2 an entry type difference alone is taken directly" <| fun _ ->
            let i = input |> localSticky |> theirText "base" (theirs 3) |> withTheirs (fun r -> { r with EntryType = Correction })
            let d = decide i
            expectContent d TakeTheirs KeepPending "taken"
            Expect.isEmpty d.Reviews "no review"

        testCase "FC3 no pending change: take theirs" <| fun _ ->
            let d = decide (input |> theirText "remote" (theirs 3))
            expectContent d TakeTheirs KeepPending "taken"
            Expect.isEmpty d.Reviews "no review"

        testCase "FC4 the local side is later: kept; a conflict when both changed the text (remote loses)" <| fun _ ->
            let i = input |> theirText "remote" (theirs 3) |> withLocal (withText "edit") |> pendingContent (ours 5)
            let d = decide i
            expectContent d LeaveContent KeepPending "local wins"
            Expect.equal d.Reviews [ Conflict { Side = Remote; Text = "remote"; Stamp = Some (theirs 3) } ] "remote loser"

        testCase "FC4 the record changed only another content field: no conflict" <| fun _ ->
            let i =
                input |> theirText "base" (theirs 3) |> withTheirs (fun r -> { r with Project = Some "p" })
                |> withLocal (withText "edit") |> pendingContent (ours 5)
            let d = decide i
            expectContent d LeaveContent KeepPending "local wins"
            Expect.isEmpty d.Reviews "the text was changed on one side only"

        testCase "FC5 the record is later: taken; a conflict when both changed the text (local loses)" <| fun _ ->
            let i = input |> theirText "remote" (theirs 3) |> withLocal (withText "edit") |> pendingContent (ours 2)
            let d = decide i
            expectContent d TakeTheirs ClearPending "remote wins"
            Expect.equal d.Reviews [ Conflict { Side = Side.Local; Text = "edit"; Stamp = Some (ours 2) } ] "local loser"

        testCase "a conflicting text edit gives exactly one conflict, the older side loses, in both stamp orders" <| fun _ ->
            for pending, loserSide in [ ours 5, Remote; ours 2, Side.Local ] do
                let d = decide (input |> theirText "remote" (theirs 3) |> withLocal (withText "edit") |> pendingContent pending)
                let conflicts = d.Reviews |> List.choose (function Conflict c -> Some c.Side | _ -> None)
                Expect.equal conflicts [ loserSide ] "one conflict"

        testCase "a record that unpins and edits a pinned entry at once: the text is taken, no pin-edit" <| fun _ ->
            let i =
                input |> localSticky |> withBase (withSticky true (theirs 1))
                |> theirText "remote" (theirs 3) |> withTheirs (withSticky false (theirs 3))
            let d = decide i
            Expect.equal (stickyOf d) (UnpinLocally, KeepPending) "unpinned"
            expectContent d TakeTheirs KeepPending "text taken directly"
            Expect.isEmpty d.Reviews "no pin-edit for an entry that is no longer pinned"

        testCase "a sticky local entry whose own edit is later keeps its text and opens no pin-edit" <| fun _ ->
            let i = input |> localSticky |> theirText "remote" (theirs 3) |> withLocal (withText "edit") |> pendingContent (ours 5)
            let d = decide i
            expectContent d LeaveContent KeepPending "local wins"
            Expect.isFalse (List.contains PinEdit d.Reviews) "nothing is held"

        testCase "a tag edit on one side and a text edit on the other do not conflict" <| fun _ ->
            let i = input |> theirText "remote" (theirs 3) |> withLocal (fun l -> { l with Tags = [ "a" ] })
            let d = decide i
            expectContent d TakeTheirs KeepPending "text taken"
            Expect.isEmpty d.Reviews "no conflict"
            Expect.equal (groups d).Tags.Result [ "a" ] "the local tag is kept"

        testCase "NC1 no base, same content: adopt created_at" <| fun _ ->
            let d = decide (input |> noBase |> theirText "same" (theirs 3) |> withLocal (withText "same"))
            expectContent d AdoptCreatedAt ClearPending "adopt"
            Expect.equal d.Base.Content BaseBecomesTheirs "base"

        testCase "NC2 no base, a sticky local entry holds different content" <| fun _ ->
            let d = decide (input |> noBase |> localSticky |> theirText "remote" (theirs 3) |> withLocal (withText "mine"))
            expectContent d HeldAsPinEdit ClearPending "held"
            Expect.equal d.Reviews [ PinEdit ] "pin-edit"

        testCase "NC3 no base, same text, another field differs: take theirs" <| fun _ ->
            let d = decide (input |> noBase |> withTheirs (fun r -> { r with Project = Some "p" }))
            expectContent d TakeTheirs ClearPending "taken"
            Expect.isEmpty d.Reviews "no review"

        testCase "NC4 no base, different text: the record wins and the local text goes to review" <| fun _ ->
            let i = input |> noBase |> theirText "remote" (theirs 3) |> withLocal (withText "mine") |> pendingContent (ours 2)
            let d = decide i
            expectContent d TakeTheirs ClearPending "taken"
            Expect.equal d.Reviews [ Conflict { Side = Side.Local; Text = "mine"; Stamp = Some (ours 2) } ] "local loser"

        testCase "no base: an own content stamp is settled first" <| fun _ ->
            let i =
                input |> noBase |> withLocal (withText "mine")
                |> pendingContent (theirs 1) |> pushingContent (theirs 1)
            expectContent (decide i) LeaveContent ClearPending "own"

        testCase "Row = None ignores a supplied base and behaves as no base, creating the row" <| fun _ ->
            let i = input |> theirText "remote" (theirs 3) |> withLocal (withText "mine")
            let a = decide { i with Row = None }
            let b = decide { i with Base = None }
            Expect.equal a.Entry b.Entry "same outcome as no base"
            Expect.equal a.Base b.Base "same base update"
            Expect.equal a.RowState (Some Active) "the caller creates the row"
            Expect.isNone b.RowState "an existing row is unchanged"
    ]

[<Tests>]
let stickyRuleTests =
    testList "SyncMerge sticky rules" [
        let withBaseSticky (v: bool) = withBase (withSticky v (theirs 1))
        let recSticky (v: bool) (s: Stamp) = withTheirs (withSticky v s)

        testCase "FS1 pending set and equal values: leave, clear" <| fun _ ->
            let d = decide (input |> localSticky |> recSticky true (theirs 3) |> pendingSticky (ours 2))
            Expect.equal (stickyOf d) (LeaveSticky, ClearPending) "FS1"

        testCase "FS2 the local change is later: leave, keep" <| fun _ ->
            let d = decide (input |> withBaseSticky true |> localSticky |> recSticky false (theirs 3) |> pendingSticky (ours 5))
            Expect.equal (stickyOf d) (LeaveSticky, KeepPending) "FS2"

        testCase "FS3 a later remote unpin over an older local pin change: unpin locally" <| fun _ ->
            let d = decide (input |> withBaseSticky true |> localSticky |> recSticky false (theirs 3) |> pendingSticky (ours 2))
            Expect.equal (stickyOf d) (UnpinLocally, ClearPending) "FS3"

        testCase "FS4 a later remote pin over an older local change: a new-pin review, the entry stays unpinned" <| fun _ ->
            let d = decide (input |> recSticky true (theirs 3) |> pendingSticky (ours 2))
            Expect.equal (stickyOf d) (ReviewNewPin, ClearPending) "FS4"
            Expect.equal d.Reviews [ NewPin ] "new pin"

        testCase "FS5 no pending and equal values: leave" <| fun _ ->
            let d = decide (input |> recSticky false (theirs 3))
            Expect.equal (stickyOf d) (LeaveSticky, KeepPending) "FS5"

        testCase "FS6 no pending, the record unpins a sticky local entry: unpin locally" <| fun _ ->
            let d = decide (input |> withBaseSticky true |> localSticky |> recSticky false (theirs 3))
            Expect.equal (stickyOf d) (UnpinLocally, KeepPending) "FS6"

        testCase "FS7 no pending, the record pins a local entry that is not sticky: a new-pin review" <| fun _ ->
            let d = decide (input |> recSticky true (theirs 3))
            Expect.equal (stickyOf d) (ReviewNewPin, KeepPending) "FS7"
            Expect.equal d.Reviews [ NewPin ] "new pin"

        testCase "stamps decide, not values: a pin, an unpin and a re-pin elsewhere changes nothing for a pinned entry" <| fun _ ->
            let d = decide (input |> withBaseSticky true |> localSticky |> recSticky true (theirs 5))
            Expect.equal (stickyOf d) (LeaveSticky, KeepPending) "same value"
            Expect.isEmpty d.Reviews "no review"

        testCase "stamps decide, not values: an old offline unpin does not unpin" <| fun _ ->
            let d = decide (input |> withBaseSticky true |> localSticky |> recSticky false (theirs 0))
            Expect.equal (stickyOf d) (LeaveSticky, KeepPending) "older"
            Expect.isTrue (groups d).Repush.Sticky "the repository is caught up from the base"
            Expect.equal d.Base.Sticky KeepBase "base kept"

        testCase "NS1 no base, equal: leave, clear" <| fun _ ->
            let d = decide (input |> noBase |> localSticky |> recSticky true (theirs 1))
            Expect.equal (stickyOf d) (LeaveSticky, ClearPending) "NS1"

        testCase "NS2 no base, a local pin over an unpinned record is kept and pushed" <| fun _ ->
            let sticky = input |> noBase |> localSticky
            Expect.equal (stickyOf (decide (sticky |> pendingSticky (ours 5)))) (LeaveSticky, KeepPending) "pending later"
            Expect.equal (stickyOf (decide sticky)) (LeaveSticky, RestampPending) "no pending: restamp"
            Expect.equal (stickyOf (decide (sticky |> pendingSticky (ours 0)))) (LeaveSticky, RestampPending) "pending older: restamp"

        testCase "NS3 no base, a pinned record over an unpinned local entry: a new-pin review" <| fun _ ->
            let d = decide (input |> noBase |> recSticky true (theirs 1))
            Expect.equal (stickyOf d) (ReviewNewPin, ClearPending) "NS3"
            Expect.equal d.Reviews [ NewPin ] "new pin"

        testCase "a remote pin never pins and a remote unpin never applies to an unpinned entry" <| fun _ ->
            for hasBase in [ true; false ] do
                for ls in [ true; false ] do
                    for rs in [ true; false ] do
                        for p in [ None; Some (ours 2); Some (ours 5) ] do
                            let i0 = if hasBase then input else noBase input
                            let i1 = i0 |> withLocal (fun l -> { l with Sticky = ls }) |> recSticky rs (theirs 3)
                            let i = match p with Some s -> pendingSticky s i1 | None -> i1
                            let apply = fst (stickyOf (decide i))
                            if apply = UnpinLocally then Expect.isTrue ls "unpin only a pinned entry"
                            Expect.equal (apply = ReviewNewPin) (List.contains NewPin (decide i).Reviews) "review and apply agree"
    ]

[<Tests>]
let tierRuleTests =
    testList "SyncMerge tier rules" [
        let recTier (t: Tier) (s: Stamp) = withTheirs (withTier t s)
        let localTier (t: Tier) = withLocal (fun l -> { l with Tier = t })

        testCase "FR1 a warmer record promotes" <| fun _ ->
            Expect.equal (tierOf (decide (input |> recTier Hot (theirs 3)))) (Some Hot, ClearPending) "FR1"

        testCase "FR2 pending set and equal tiers: clear" <| fun _ ->
            let d = decide (input |> localTier Hot |> recTier Hot (theirs 3) |> pendingTier (ours 2))
            Expect.equal (tierOf d) (None, ClearPending) "FR2"

        testCase "FR3 a warmer local tier with a pending promotion: kept if later, restamped if not" <| fun _ ->
            let i = input |> localTier Hot |> recTier Warm (theirs 3)
            Expect.equal (tierOf (decide (i |> pendingTier (ours 5)))) (None, KeepPending) "pending later"
            Expect.equal (tierOf (decide (i |> pendingTier (ours 2)))) (None, RestampPending) "pending older"

        testCase "FR4 otherwise: nothing; a colder record never lowers the tier" <| fun _ ->
            Expect.equal (tierOf (decide (input |> localTier Hot |> recTier Warm (theirs 3)))) (None, KeepPending) "local warmer, no pending"
            Expect.equal (tierOf (decide (input |> recTier Cold (theirs 3)))) (None, KeepPending) "record colder"

        testCase "no base: equal clears, a warmer record promotes, a warmer local tier is pushed" <| fun _ ->
            let nb = noBase input
            Expect.equal (tierOf (decide nb)) (None, ClearPending) "same tier"
            Expect.equal (tierOf (decide (nb |> recTier Hot (theirs 1)))) (Some Hot, ClearPending) "record warmer"
            Expect.equal (tierOf (decide (nb |> localTier Hot))) (None, RestampPending) "local warmer, no pending"
            Expect.equal (tierOf (decide (nb |> localTier Hot |> pendingTier (ours 5)))) (None, KeepPending) "pending later"
    ]

[<Tests>]
let tagTests =
    testList "SyncMerge tags" [
        let tags (bs: string list) (ls: string list) (ts: string list) (proven: bool) =
            { input with ApplyProven = proven }
            |> withBase (fun r -> { r with Tags = bs })
            |> withLocal (fun l -> { l with Tags = ls })
            |> withTheirs (fun r -> { r with Tags = ts })

        testCase "three-way: additions and removals from both sides apply" <| fun _ ->
            let t = (groups (decide (tags [ "a"; "b" ] [ "b" ] [ "a"; "b"; "c" ] true))).Tags
            Expect.equal t { Result = [ "b"; "c" ]; ApplyLocal = true; PushNeeded = true } "removal one side, addition the other"

        testCase "three-way: both remove, one removes, both add" <| fun _ ->
            let r bs ls ts = (groups (decide (tags bs ls ts true))).Tags
            Expect.equal (r [ "a"; "b" ] [ "a" ] [ "a" ]) { Result = [ "a" ]; ApplyLocal = false; PushNeeded = false } "both remove"
            Expect.equal (r [ "a"; "b" ] [ "a" ] [ "a"; "b" ]) { Result = [ "a" ]; ApplyLocal = false; PushNeeded = true } "one removes"
            Expect.equal (r [] [ "c" ] [ "c" ]) { Result = [ "c" ]; ApplyLocal = false; PushNeeded = false } "both add"

        testCase "an unproven apply unites the tags even with a base (a reset is not a removal)" <| fun _ ->
            let t = (groups (decide (tags [ "a" ] [ "a" ] [] false))).Tags
            Expect.equal t { Result = [ "a" ]; ApplyLocal = false; PushNeeded = true } "union"

        testCase "no base: the union" <| fun _ ->
            let i = noBase input |> withLocal (fun l -> { l with Tags = [ "a" ] }) |> withTheirs (fun r -> { r with Tags = [ "b" ] })
            let t = (groups (decide i)).Tags
            Expect.equal t { Result = [ "a"; "b" ]; ApplyLocal = true; PushNeeded = true } "union"

        testCase "a tag-only difference leaves the stamped groups alone" <| fun _ ->
            let m = groups (decide (tags [] [] [ "x" ] true))
            Expect.equal m.Content (LeaveContent, KeepPending) "content"
            Expect.equal m.Sticky (LeaveSticky, KeepPending) "sticky"
            Expect.equal m.Tier (None, KeepPending) "tier"
            Expect.equal m.Tags.Result [ "x" ] "tags taken"

        testCase "an own push with another machine's tag change in between keeps the tag" <| fun _ ->
            let i =
                { input with Theirs = Some { record "mine" with ContentStamp = ours 3; Tags = [ "x" ] }; Local = Some (local "mine") }
                |> pendingContent (ours 3) |> pushingContent (ours 3)
            let m = groups (decide i)
            Expect.equal m.Content (LeaveContent, ClearPending) "own"
            Expect.isTrue m.Tags.ApplyLocal "the tag is applied"
            Expect.equal m.Tags.Result [ "x" ] "tag kept"
    ]

[<Tests>]
let entryPlumbingTests =
    testList "SyncMerge W4 and W5 plumbing" [
        testCase "W5: the row is unchanged, nothing is stamped, no note" <| fun _ ->
            let d = decide input
            Expect.isNone d.RowState "row"
            Expect.isFalse d.FreshContentStamp "no fresh stamp"
            Expect.isEmpty d.Notes "no note"
            Expect.isFalse d.CloseHeldDelete "no item"

        testCase "W4: a row without a base merges by the no-base rules and keeps its state" <| fun _ ->
            let d = decide (noBase input)
            Expect.isNone d.RowState "Active stays Active"
            Expect.equal d.Base { Content = BaseBecomesTheirs; Tags = BaseBecomesTheirs; Sticky = BaseBecomesTheirs; Tier = BaseBecomesTheirs } "all from the record"

        testCase "an open held-delete item is closed when the record is present" <| fun _ ->
            for item in [ RemoteDeletion; LocalDeletion ] do
                Expect.isTrue (decide { input with HeldDelete = Some item }).CloseHeldDelete "closed"
    ]

[<Tests>]
let mutationResistanceTests =
    testList "SyncMerge group rules, edge cases" [
        testCase "own content with a base differs from a non-own stamp: own via Pushing alone, via Pending alone" <| fun _ ->
            // The record carries our stamp 3 with text "theirs-text"; the local entry has other text.
            let i = { input with Theirs = Some { record "theirs-text" with ContentStamp = ours 3 } } |> withLocal (withText "local-text")
            let viaPushing = decide (i |> pushingContent (ours 3))
            expectContent viaPushing LeaveContent KeepPending "own via Pushing: nothing applied, no pending to clear"
            Expect.isEmpty viaPushing.Reviews "no conflict for an own stamp"
            Expect.equal viaPushing.Base.Content BaseBecomesTheirs "base"
            let viaPending = decide (i |> pendingContent (ours 3))
            expectContent viaPending LeaveContent ClearPending "own via Pending: the pending stamp equals the record's and is cleared"
            expectContent (decide i) TakeTheirs KeepPending "without either, the same record is an ordinary remote change"

        testCase "own content with a later pending change: no conflict review" <| fun _ ->
            let i =
                { input with Theirs = Some { record "mine" with ContentStamp = ours 3 }; Local = Some (local "edit") }
                |> pendingContent (ours 5) |> pushingContent (ours 3)
            Expect.isEmpty (decide i).Reviews "own"

        testCase "unchanged groups keep a pending stamp, the base and do not re-push" <| fun _ ->
            let i = input |> withLocal (withText "edit") |> pendingContent (ours 4) |> pendingSticky (ours 4) |> pendingTier (ours 4)
            let d = decide i
            let m = groups d
            Expect.equal m.Content (LeaveContent, KeepPending) "content"
            Expect.equal m.Sticky (LeaveSticky, KeepPending) "sticky"
            Expect.equal m.Tier (None, KeepPending) "tier"
            Expect.equal m.Repush { Content = false; Sticky = false; Tier = false } "no repush"
            Expect.equal (d.Base.Content, d.Base.Sticky, d.Base.Tier) (KeepBase, KeepBase, KeepBase) "bases kept"

        testCase "FR2 needs a pending stamp: equal tiers with none is FR4" <| fun _ ->
            let d = decide (input |> withLocal (fun l -> { l with Tier = Hot }) |> withTheirs (withTier Hot (theirs 3)))
            Expect.equal (tierOf d) (None, KeepPending) "FR4"

        testCase "no conflict when the pending stamp is set but the local text is the base's text" <| fun _ ->
            // FC4 side and FC5 side: a pending project-only change, a remote text edit.
            for pending in [ ours 5; ours 2 ] do
                let i = input |> theirText "remote" (theirs 3) |> pendingContent pending
                Expect.isEmpty (decide i).Reviews "one side only changed the text"

        testCase "no conflict when both sides made the same text edit and differ in another field" <| fun _ ->
            for pending in [ ours 5; ours 2 ] do
                let i =
                    input |> theirText "same-edit" (theirs 3) |> withTheirs (fun r -> { r with Project = Some "p" })
                    |> withLocal (withText "same-edit") |> pendingContent pending
                Expect.isEmpty (decide i).Reviews "the texts are equal"

        testCase "NC2 no base, a sticky local entry holds a project-only and a scope-only difference" <| fun _ ->
            let sticky = input |> noBase |> localSticky
            for i in [ sticky |> withTheirs (fun r -> { r with Project = Some "p" })
                       sticky |> withTheirs (fun r -> { r with Scope = Some "global" }) ] do
                let d = decide i
                expectContent d HeldAsPinEdit ClearPending "held"
                Expect.equal d.Reviews [ PinEdit ] "pin-edit"
    ]

// ------------------------------------------------------------------------------------------------
// Task 3: the whole-entry rows without ordered lists, and held-delete closing

let private everyBase (c: BaseChange) : BaseUpdate = { Content = c; Tags = c; Sticky = c; Tier = c }
let private bothApplies : bool list = [ true; false ]
let private proven (b: bool) (i: MergeInput) : MergeInput = { i with ApplyProven = b }

/// W1/W2/W3/W9 shape: a record, no local entry.
let private noEntry (i: MergeInput) : MergeInput = { i with Local = None }
/// W6/W7 shape: an entry, no record.
let private noRecord (i: MergeInput) : MergeInput = { i with Theirs = None }

[<Tests>]
let wholeEntryTests =
    testList "SyncMerge whole-entry rows" [
        testCase "W1 a record with no entry and no row: insert non-sticky, row Active, base from the record" <| fun _ ->
            for p in bothApplies do
                let d = decide ({ input with Row = None; Base = None } |> noEntry |> proven p)
                Expect.equal d.Entry InsertFromRecord "insert"
                Expect.equal d.RowState (Some Active) "row"
                Expect.equal d.Base (everyBase BaseBecomesTheirs) "base"
                Expect.isEmpty d.Reviews "no review"
                Expect.isFalse d.FreshContentStamp "no fresh stamp"

        testCase "W1 a sticky record adds a new-pin review; a stale base is ignored when there is no row" <| fun _ ->
            let i = { input with Row = None } |> noEntry |> withTheirs (withSticky true (theirs 1))
            let d = decide i
            Expect.equal d.Entry InsertFromRecord "insert"
            Expect.equal d.Reviews [ NewPin ] "new pin"

        testCase "W2 a left row ignores the record, with or without a base" <| fun _ ->
            for b in [ input.Base; None ] do
                let d = decide ({ input with Row = Some Left; Base = b } |> noEntry)
                Expect.equal d.Entry Ignore "ignore"
                Expect.isNone d.RowState "unchanged"

        testCase "W3 a forgotten row comes back only for a newer, different text that is not our own push" <| fun _ ->
            let forgotten = { input with Row = Some Forgotten } |> noEntry
            for p in bothApplies do
                let d = decide (forgotten |> theirText "remote" (theirs 3) |> proven p)
                Expect.equal d.Entry InsertFromRecord "insert"
                Expect.equal d.RowState (Some Active) "row Active"
                Expect.equal d.Notes [ EditBeatsDeletion ] "note"
                Expect.equal d.Base (everyBase BaseBecomesTheirs) "base"
            let ignored (what: string) (i: MergeInput) = Expect.equal (decide i).Entry Ignore what
            ignored "only the tier changed" (forgotten |> withTheirs (withTier Hot (theirs 3)))
            ignored "only the tags changed" (forgotten |> withTheirs (fun r -> { r with Tags = [ "x" ] }))
            ignored "only the pin changed" (forgotten |> withTheirs (withSticky true (theirs 3)))
            ignored "an older text" (forgotten |> theirText "remote" (theirs 0))
            ignored "our own push" (forgotten |> theirText "remote" (ours 3) |> pushingContent (ours 3))
            ignored "a newer stamp with the same text" (forgotten |> theirText "base" (theirs 3))
            ignored "no base" ({ forgotten with Base = None } |> theirText "remote" (theirs 3))
            Expect.isNone (decide (forgotten |> theirText "remote" (theirs 0))).RowState "the row stays"

        testCase "W7 a new local entry with no record: leave it for the caller to push" <| fun _ ->
            for row, b in [ None, None; Some Active, None ] do
                let d = decide ({ input with Row = row; Base = b } |> noRecord)
                Expect.equal d.Entry LeaveEntry "leave"
                Expect.isNone d.RowState "no row change"
                Expect.equal d.Base (everyBase KeepBase) "base"

        testCase "W9 no base, entry gone: our own pushed record is deleted; a foreign one is not resurrected" <| fun _ ->
            let w9 = { input with Base = None } |> noEntry
            for p in bothApplies do
                let own = decide (w9 |> pushingContent (theirs 1) |> proven p)
                Expect.equal own.Entry PushDeletion "own push"
                Expect.equal own.Base (everyBase BaseBecomesTheirs) "base"
                Expect.isNone own.RowState "row unchanged until the push completes"
                let foreign = decide (w9 |> proven p)
                Expect.equal foreign.Entry Ignore "foreign"
                Expect.equal foreign.RowState (Some Forgotten) "remembered as deleted"
                Expect.equal foreign.Base (everyBase BaseBecomesTheirs) "base"

        testCase "W9 an own tier stamp over a foreign text stamp is not a proof of the deletion" <| fun _ ->
            let d = decide ({ input with Base = None } |> noEntry |> pushingTier (theirs 1))
            Expect.equal d.Entry Ignore "ignore"
            Expect.equal d.RowState (Some Forgotten) "forgotten"
            let other = decide ({ input with Base = None } |> noEntry |> pushingContent (ours 9))
            Expect.equal other.Entry Ignore "another stamp is not the record's"

        testCase "chain: a deleted base-less row with a foreign record is not resurrected until a newer, different text" <| fun _ ->
            let first = decide ({ input with Base = None } |> noEntry)
            Expect.equal first.Entry Ignore "W9"
            let next = { input with Row = first.RowState; Base = input.Theirs } |> noEntry
            Expect.equal (decide next).Entry Ignore "W3, same record"
            let newer = next |> theirText "newer" (theirs 5)
            Expect.equal (decide newer).Entry InsertFromRecord "a newer, different text brings it back"

        testCase "W10 the record is gone and so is the entry: forget, base kept" <| fun _ ->
            for row in [ Active; Forgotten ] do
                for p in bothApplies do
                    let d = decide ({ input with Row = Some row } |> noEntry |> noRecord |> proven p)
                    Expect.equal d.Entry Ignore "ignore"
                    Expect.equal d.RowState (Some Forgotten) "forgotten"
                    Expect.equal d.Base (everyBase KeepBase) "base kept"

        testCase "W11 W12 W13 nothing anywhere" <| fun _ ->
            let none = { input with Base = None } |> noEntry |> noRecord
            Expect.equal (decide none).Entry DropRow "W11 an active row that never reached the repository is dropped"
            Expect.isNone (decide none).RowState "the row goes with DropRow"
            Expect.equal (decide { none with Row = Some Forgotten }).Entry Ignore "W12 forgotten, no base"
            Expect.equal (decide { none with Row = Some Left }).Entry Ignore "W12 left, no base"
            Expect.equal (decide ({ input with Row = Some Left } |> noEntry |> noRecord)).Entry Ignore "W12 left, with a base"
            Expect.equal (decide { none with Row = None }).Entry Ignore "W13 no row"
            Expect.isNone (decide { none with Row = Some Left }).RowState "unchanged"

        testCase "W14 a forgotten or left row with an entry is deferred, with or without a record" <| fun _ ->
            for row in [ Forgotten; Left ] do
                for theirsPresent in [ true; false ] do
                    let i = { input with Row = Some row; HeldDelete = Some RemoteDeletion }
                    let i = if theirsPresent then i else noRecord i
                    let d = decide i
                    Expect.equal d.Entry Defer "defer"
                    Expect.isFalse d.CloseHeldDelete "defer closes nothing"
                    Expect.isNone d.RowState "row unchanged"
                    Expect.equal d.Base (everyBase KeepBase) "base kept"

        testCase "held-delete: a RemoteDeletion item closes when the record is present or the entry is gone, not otherwise" <| fun _ ->
            let item = Some RemoteDeletion
            Expect.isTrue (decide { input with HeldDelete = item }).CloseHeldDelete "record and entry present"
            Expect.isTrue (decide ({ input with HeldDelete = item } |> noEntry |> noRecord)).CloseHeldDelete "entry gone"
            Expect.isFalse (decide ({ input with Base = None; HeldDelete = item } |> noRecord)).CloseHeldDelete "record absent, entry present (W7)"

        testCase "held-delete: a LocalDeletion item closes when the entry is back or the record is gone, not while the deletion stands" <| fun _ ->
            let item = Some LocalDeletion
            Expect.isTrue (decide { input with HeldDelete = item }).CloseHeldDelete "entry present"
            Expect.isTrue (decide ({ input with HeldDelete = item } |> noEntry |> noRecord)).CloseHeldDelete "record gone"
            Expect.isFalse (decide ({ input with Base = None; HeldDelete = item } |> noEntry)).CloseHeldDelete "record present, entry gone (W9 ignore)"

        testCase "held-delete: no item, never closed" <| fun _ ->
            for i in [ input; noEntry input; noRecord input; { input with Row = None } ] do
                Expect.isFalse (decide i).CloseHeldDelete "no item"

        testCase "clearsPending states the Row-effects paragraph" <| fun _ ->
            for o in [ InsertFromRecord; DeleteEntry; DropRow; PushDeletion; HoldDeletion LocalDeletion ] do
                Expect.isTrue (clearsPending o true) (sprintf "%A clears with an entry" o)
                Expect.isTrue (clearsPending o false) (sprintf "%A clears without an entry" o)
            Expect.isTrue (clearsPending Ignore false) "ignore without an entry"
            Expect.isFalse (clearsPending Ignore true) "ignore with an entry"
            let merged = groups (decide input)
            for o in [ HoldDeletion RemoteDeletion; LeaveEntry; Defer; MergeGroups merged ] do
                Expect.isFalse (clearsPending o true) (sprintf "%A keeps pending stamps" o)
                Expect.isFalse (clearsPending o false) (sprintf "%A keeps pending stamps" o)

        testCase "every row value returns a decision" <| fun _ ->
            for row in [ None; Some Active; Some Forgotten; Some Left ] do
                for t in [ true; false ] do
                    for l in [ true; false ] do
                        let i = { input with Row = row }
                        let i = if t then i else noRecord i
                        let i = if l then i else noEntry i
                        ignore (decide i)
    ]
