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

// ------------------------------------------------------------------------------------------------
// the group rules, reached through W4 and W5 (an entry and a record both exist)

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

        testCase "FC5 a record that changed only another field still takes the whole content group over a pending text edit (whole-group last-writer-wins; no conflict is opened)" <| fun _ ->
            let i =
                input |> theirText "base" (theirs 3) |> withTheirs (fun r -> { r with Project = Some "p" })
                |> withLocal (withText "edit") |> pendingContent (ours 2)
            let d = decide i
            expectContent d TakeTheirs ClearPending "the later stamp takes the group"
            Expect.isEmpty d.Reviews "the umbrella opens a conflict only when both sides changed the text"

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
// the whole-entry rows without ordered lists, and held-delete closing

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

        testCase "W1 a sticky record adds a new-pin review" <| fun _ ->
            let i = { input with Row = None } |> noEntry |> withTheirs (withSticky true (theirs 1))
            let d = decide i
            Expect.equal d.Entry InsertFromRecord "insert"
            Expect.equal d.Reviews [ NewPin ] "new pin"

        testCase "W2 a left row ignores the record, with or without a base" <| fun _ ->
            for b in [ input.Base; None ] do
                for p in bothApplies do
                    let d = decide ({ input with Row = Some Left; Base = b } |> noEntry |> proven p)
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

[<Tests>]
let wholeEntryEdgeTests =
    testList "SyncMerge whole-entry rows, edge cases" [
        testCase "Row = None ignores a supplied base: W7 and W13 are not W6 and W10" <| fun _ ->
            let newEntry = decide ({ input with Row = None } |> noRecord)
            Expect.equal newEntry.Entry LeaveEntry "W7 with a stale base"
            Expect.isNone newEntry.RowState "no row change"
            Expect.equal (decide ({ input with Row = None } |> noEntry |> noRecord)).Entry Ignore "W13 with a stale base"

        testCase "W3 insert: a sticky record adds a new-pin review" <| fun _ ->
            let i = { input with Row = Some Forgotten } |> noEntry |> theirText "remote" (theirs 3) |> withTheirs (withSticky true (theirs 3))
            let d = decide i
            Expect.equal d.Entry InsertFromRecord "insert"
            Expect.equal d.Reviews [ NewPin ] "new pin"

        testCase "W3 and W9: an own stamp through Pending alone counts for W3 (own) and not for W9 (Pushing only)" <| fun _ ->
            let forgotten = { input with Row = Some Forgotten } |> noEntry |> theirText "remote" (ours 3) |> pendingContent (ours 3)
            Expect.equal (decide forgotten).Entry Ignore "W3: own through Pending is not a remote edit"
            let w9 = { input with Base = None } |> noEntry |> pendingContent (theirs 1)
            let d = decide w9
            Expect.equal d.Entry Ignore "W9 needs Pushing"
            Expect.equal d.RowState (Some Forgotten) "remembered as deleted"

        testCase "held-delete: a LocalDeletion item closes when the record's edit brings the entry back" <| fun _ ->
            let w3 = { input with Row = Some Forgotten; HeldDelete = Some LocalDeletion } |> noEntry |> theirText "remote" (theirs 3)
            let d = decide w3
            Expect.equal d.Entry InsertFromRecord "insert"
            Expect.isTrue d.CloseHeldDelete "closed with the insert"
            let w1 = { input with Row = None; HeldDelete = Some LocalDeletion } |> noEntry
            Expect.isTrue (decide w1).CloseHeldDelete "closed with the insert (W1)"
            let w2 = { input with Row = Some Left; HeldDelete = Some LocalDeletion } |> noEntry
            Expect.isFalse (decide w2).CloseHeldDelete "an ignored record leaves the item open"
    ]

// ------------------------------------------------------------------------------------------------
// the ordered lists W6 (record absent, entry present) and W8 (record present, entry gone)

/// W6.1's trigger: a pending change of the text (local text differs from the base's text).
let private pendingEdit (i: MergeInput) : MergeInput = i |> withLocal (withText "edit") |> pendingContent (ours 4)
let private item (d: Direction) (i: MergeInput) : MergeInput = { i with HeldDelete = Some d }

[<Tests>]
let absentRecordTests =
    testList "SyncMerge W6: the record is gone, the entry is here" [
        let w6 = noRecord input

        testCase "W6.1 a pending change of the text: leave, publish again with a fresh stamp, tell the user" <| fun _ ->
            for p in bothApplies do
                for sticky in [ false; true ] do
                    let i = w6 |> pendingEdit |> proven p
                    let i = if sticky then localSticky i else i
                    let d = decide i
                    Expect.equal d.Entry LeaveEntry "leave"
                    Expect.isTrue d.FreshContentStamp "fresh stamp"
                    Expect.equal d.Notes [ EditBeatsDeletion ] "note"
                    Expect.equal d.Base (everyBase KeepBase) "base kept: the push writes the record from the base"
                    Expect.isNone d.RowState "row unchanged"

        testCase "W6.1 closes an open item of either direction" <| fun _ ->
            for dir in [ RemoteDeletion; LocalDeletion ] do
                Expect.isTrue (decide (w6 |> pendingEdit |> item dir)).CloseHeldDelete (sprintf "%A item" dir)
            Expect.isFalse (decide (w6 |> pendingEdit)).CloseHeldDelete "no item"

        testCase "a held pin-edit is not a pending change: the deletion is held, not republished" <| fun _ ->
            // The base became the remote text when the pin-edit was held; the local pin text differs from it, and
            // the content stamp was cleared.
            let i = w6 |> localSticky |> withBase (fun r -> { r with Content = "remote" }) |> withLocal (withText "pin text")
            let d = decide i
            Expect.equal d.Entry (HoldDeletion RemoteDeletion) "held"
            Expect.isFalse d.FreshContentStamp "not republished"
            Expect.isEmpty d.Notes "no note"

        testCase "a pending content stamp with the base's text is not a text change (a project-only edit)" <| fun _ ->
            let i = w6 |> pendingContent (ours 4) |> withLocal (fun l -> { l with Content = { l.Content with Project = Some "p" } })
            let d = decide i
            Expect.equal d.Entry DeleteEntry "deleted"
            Expect.equal d.Notes [ PendingChangesDropped ] "the project change is dropped, and the user is told"

        testCase "W6.2 a sticky entry or a pending sticky stamp holds the deletion" <| fun _ ->
            for p in bothApplies do
                Expect.equal (decide (w6 |> localSticky |> proven p)).Entry (HoldDeletion RemoteDeletion) "sticky"
                Expect.equal (decide (w6 |> pendingSticky (ours 4) |> proven p)).Entry (HoldDeletion RemoteDeletion) "pending pin"
            let d = decide (w6 |> localSticky)
            Expect.isFalse d.FreshContentStamp "no fresh stamp"

        testCase "W6.2 an open RemoteDeletion item is not reopened; a LocalDeletion item is replaced" <| fun _ ->
            let d = decide (w6 |> localSticky |> item RemoteDeletion)
            Expect.equal d.Entry LeaveEntry "already held"
            Expect.isFalse d.CloseHeldDelete "the item stays"
            let r = decide (w6 |> localSticky |> item LocalDeletion)
            Expect.equal r.Entry (HoldDeletion RemoteDeletion) "held in the other direction"
            Expect.isTrue r.CloseHeldDelete "replaces the open item"

        testCase "W6.3 an open RemoteDeletion item with nothing pending stays" <| fun _ ->
            let d = decide (w6 |> item RemoteDeletion)
            Expect.equal d.Entry LeaveEntry "leave"
            Expect.isFalse d.CloseHeldDelete "the item stays"

        testCase "W6.4 a proven apply deletes the entry and forgets the row" <| fun _ ->
            let d = decide w6
            Expect.equal d.Entry DeleteEntry "delete"
            Expect.equal d.RowState (Some Forgotten) "forgotten"
            Expect.equal d.Base (everyBase KeepBase) "base kept"
            Expect.isEmpty d.Notes "nothing was pending"
            Expect.isFalse d.FreshContentStamp "no fresh stamp"

        testCase "W6.4 a pending tag, tier or non-text change is dropped with a note; a colder local tier alone is not" <| fun _ ->
            let note (what: string) (i: MergeInput) =
                let d = decide i
                Expect.equal d.Entry DeleteEntry (what + ": delete")
                Expect.equal d.Notes [ PendingChangesDropped ] what
            note "tags" (w6 |> withLocal (fun l -> { l with Tags = [ "x" ] }))
            note "pending tier" (w6 |> pendingTier (ours 4))
            note "project" (w6 |> withLocal (fun l -> { l with Content = { l.Content with Project = Some "p" } }))
            note "scope" (w6 |> withLocal (fun l -> { l with Content = { l.Content with Scope = Some "global" } }))
            note "entry type" (w6 |> withLocal (fun l -> { l with Content = { l.Content with EntryType = Correction } }))
            let colder = decide (w6 |> withBase (withTier Hot (theirs 1)) |> withLocal (fun l -> { l with Tier = Cold }))
            Expect.equal colder.Entry DeleteEntry "a local demotion"
            Expect.isEmpty colder.Notes "a local demotion never syncs and is not pending"

        testCase "W6.5 an unproven apply holds the deletion" <| fun _ ->
            let d = decide (w6 |> proven false)
            Expect.equal d.Entry (HoldDeletion RemoteDeletion) "held"
            Expect.isNone d.RowState "row unchanged"

        testCase "a held entry keeps its pending stamps; a deleted one does not" <| fun _ ->
            for i in [ w6 |> localSticky; w6 |> pendingSticky (ours 4); w6 |> proven false ] do
                Expect.isFalse (clearsPending (decide i).Entry true) "held"
            Expect.isTrue (clearsPending (decide w6).Entry true) "deleted"
            let deferred = decide ({ w6 with Row = Some Forgotten } |> pendingSticky (ours 4) |> pendingTier (ours 4))
            Expect.equal deferred.Entry Defer "an impossible snapshot is deferred"
            Expect.isFalse (clearsPending deferred.Entry true) "and its pending stamps are kept"

        testCase "deletion needs proof: never a sticky entry, an unproven apply, a pending pin or text, an open item" <| fun _ ->
            let never (what: string) (i: MergeInput) = Expect.notEqual (decide i).Entry DeleteEntry what
            never "sticky" (w6 |> localSticky)
            never "unproven" (w6 |> proven false)
            never "pending pin" (w6 |> pendingSticky (ours 4))
            never "pending text" (w6 |> pendingEdit)
            never "an open item" (w6 |> item RemoteDeletion)
    ]

/// The base and the local entry of the neutral input, for the decision a review answer carries out.
let private neutralBase = record "base"
let private neutralLocal = local "base"

[<Tests>]
let remoteDeletionTests =
    testList "SyncMerge.remoteDeletion" [
        testCase "is W6.4's decision: decide's proven deletion is exactly it" <| fun _ ->
            Expect.equal (decide (noRecord input)) (remoteDeletion false neutralBase neutralLocal) "no note"
            let tagged = noRecord input |> withLocal (fun l -> { l with Tags = [ "x" ] }) |> pendingTier (ours 4)
            Expect.equal (decide tagged) (remoteDeletion true neutralBase tagged.Local.Value) "with the note"

        testCase "deletes the entry and forgets the row, keeping the base, for a sticky entry too" <| fun _ ->
            for sticky in [ false; true ] do
                let d = remoteDeletion false neutralBase { neutralLocal with Sticky = sticky }
                Expect.equal d.Entry DeleteEntry (sprintf "sticky %b: delete" sticky)
                Expect.equal d.RowState (Some Forgotten) "forgotten"
                Expect.equal d.Base (everyBase KeepBase) "base kept"
                Expect.isEmpty d.Notes "nothing was pending"
                Expect.isFalse d.FreshContentStamp "no fresh stamp"
                Expect.isFalse d.CloseHeldDelete "the caller removes the item"
                Expect.isEmpty d.Reviews "no review"

        testCase "a tag difference, a pending tier, a project or a scope difference each add PendingChangesDropped" <| fun _ ->
            let note (what: string) (pendingTier: bool) (l: LocalEntry) =
                let d = remoteDeletion pendingTier neutralBase l
                Expect.equal d.Entry DeleteEntry (what + ": delete")
                Expect.equal d.Notes [ PendingChangesDropped ] what
            note "tags" false { neutralLocal with Tags = [ "x" ] }
            note "pending tier" true neutralLocal
            note "project" false { neutralLocal with Content = { neutralLocal.Content with Project = Some "p" } }
            note "scope" false { neutralLocal with Content = { neutralLocal.Content with Scope = Some "global" } }
            note "sticky with tags" false { neutralLocal with Sticky = true; Tags = [ "x" ] }

        testCase "a text difference alone is no note (the text goes with the entry)" <| fun _ ->
            Expect.isEmpty (remoteDeletion false neutralBase (withText "other" neutralLocal)).Notes "no note"
    ]

[<Tests>]
let presentRecordTests =
    testList "SyncMerge W8: the record is here, the entry is gone" <| [
        let w8 = noEntry input

        testCase "W8.1 an edit of the text beats the local deletion" <| fun _ ->
            for p in bothApplies do
                let d = decide (w8 |> theirText "remote" (theirs 3) |> proven p)
                Expect.equal d.Entry InsertFromRecord "insert"
                Expect.equal d.RowState (Some Active) "row"
                Expect.equal d.Notes [ EditBeatsDeletion ] "note"
                Expect.equal d.Base (everyBase BaseBecomesTheirs) "base"
                Expect.isEmpty d.Reviews "unpinned"
            let sticky = decide (w8 |> theirText "remote" (theirs 3) |> withTheirs (withSticky true (theirs 3)))
            Expect.equal sticky.Reviews [ NewPin ] "a sticky record adds a new-pin review"

        testCase "W8.1 closes an open item of either direction" <| fun _ ->
            for dir in [ RemoteDeletion; LocalDeletion ] do
                let d = decide (w8 |> theirText "remote" (theirs 3) |> item dir)
                Expect.equal d.Entry InsertFromRecord "insert"
                Expect.isTrue d.CloseHeldDelete (sprintf "%A item" dir)

        testCase "an unchanged record, or one changed outside the text, does not bring the entry back" <| fun _ ->
            let cases =
                [ "unchanged", w8
                  "tier", w8 |> withTheirs (withTier Hot (theirs 3))
                  "tags", w8 |> withTheirs (fun r -> { r with Tags = [ "x" ] })
                  "pin", w8 |> withTheirs (withSticky true (theirs 3))
                  "project", w8 |> theirText "base" (theirs 3) |> withTheirs (fun r -> { r with Project = Some "p" })
                  "scope", w8 |> theirText "base" (theirs 3) |> withTheirs (fun r -> { r with Scope = Some "global" })
                  "entry type", w8 |> theirText "base" (theirs 3) |> withTheirs (fun r -> { r with EntryType = Correction })
                  "an older text", w8 |> theirText "old" (theirs 0)
                  "our own push", w8 |> theirText "remote" (ours 3) |> pushingContent (ours 3) ]
            for what, i in cases do
                Expect.equal (decide (proven true i)).Entry PushDeletion (what + ": proven, the deletion stands")
                Expect.equal (decide (proven false i)).Entry (HoldDeletion LocalDeletion) (what + ": unproven, held")

        testCase "W8.2 an open LocalDeletion item is kept and nothing is pushed" <| fun _ ->
            for p in bothApplies do
                let d = decide (w8 |> item LocalDeletion |> proven p)
                Expect.equal d.Entry Ignore "ignore"
                Expect.isFalse d.CloseHeldDelete "the user has not answered"

        testCase "W8.3 and W8.4 replace an open RemoteDeletion item" <| fun _ ->
            let d3 = decide (w8 |> item RemoteDeletion)
            Expect.equal d3.Entry PushDeletion "proven"
            Expect.isTrue d3.CloseHeldDelete "the remote item is moot"
            let d4 = decide (w8 |> item RemoteDeletion |> proven false)
            Expect.equal d4.Entry (HoldDeletion LocalDeletion) "unproven"
            Expect.isTrue d4.CloseHeldDelete "replaced"

        testCase "W8 updates the base per group: an older group keeps it, a newer one takes the record's, tags always" <| fun _ ->
            let i = w8 |> withTheirs (withTier Hot (theirs 0) >> withSticky true (theirs 3))
            let d = decide i
            Expect.equal d.Base { Content = KeepBase; Tags = BaseBecomesTheirs; Sticky = BaseBecomesTheirs; Tier = KeepBase } "per group"

        testCase "W8.4 a held local deletion clears the pending stamps of the entry that is gone" <| fun _ ->
            Expect.isTrue (clearsPending (decide (w8 |> proven false)).Entry false) "HoldDeletion LocalDeletion clears"

        testCase "a reset to a pre-edit commit does not re-insert a forgotten memory with older text" <| fun _ ->
            let i = { input with Row = Some Forgotten } |> withBase (fun r -> { r with Content = "edit"; ContentStamp = theirs 5 }) |> noEntry
            Expect.equal (decide (i |> theirText "base" (theirs 1))).Entry Ignore "the old record is older than the base"

        testCase "a machine-driven promotion does not undo a forget or a local deletion" <| fun _ ->
            let promoted = withTheirs (withTier Hot (theirs 3))
            Expect.equal (decide ({ input with Row = Some Forgotten } |> noEntry |> promoted)).Entry Ignore "forgotten (W3)"
            Expect.notEqual (decide (noEntry input |> promoted)).Entry InsertFromRecord "deleted (W8)"
    ]

[<Tests>]
let deletionEdgeTests =
    testList "SyncMerge W6 and W8, edge cases" [
        testCase "W8.2 to W8.4 update the base per group" <| fun _ ->
            let w8 = noEntry input |> withTheirs (withTier Hot (theirs 0) >> withSticky true (theirs 3))
            let expected = { Content = KeepBase; Tags = BaseBecomesTheirs; Sticky = BaseBecomesTheirs; Tier = KeepBase }
            Expect.equal (decide w8).Base expected "W8.3 proven"
            Expect.equal (decide (proven false w8)).Base expected "W8.4 unproven"
            Expect.equal (decide (item LocalDeletion w8)).Base expected "W8.2 open item"

        testCase "W8 an own content stamp takes the record's base for the content group" <| fun _ ->
            let i = noEntry input |> theirText "remote" (ours 3) |> pushingContent (ours 3)
            let d = decide i
            Expect.equal d.Entry PushDeletion "the deletion stands"
            Expect.equal d.Base.Content BaseBecomesTheirs "own"

        testCase "W6.4 a pending content stamp with nothing changed gives no note" <| fun _ ->
            let d = decide (noRecord input |> pendingContent (ours 4))
            Expect.equal d.Entry DeleteEntry "delete"
            Expect.isEmpty d.Notes "nothing was dropped"

        testCase "W6.2 a pending pin on an unpinned entry holds the deletion without republishing" <| fun _ ->
            let d = decide (noRecord input |> pendingSticky (ours 4))
            Expect.equal d.Entry (HoldDeletion RemoteDeletion) "held"
            Expect.isFalse d.FreshContentStamp "not republished"
            Expect.isEmpty d.Notes "no note"

        testCase "a deletion pushed first and an edit written back: both machines end with the memory and the edited text" <| fun _ ->
            // Machine B edits while A's deletion is pushed. B republishes (W6.1); the record then carries B's text.
            let b = decide (noRecord input |> pendingEdit)
            Expect.equal b.Entry LeaveEntry "B keeps its entry"
            Expect.isTrue b.FreshContentStamp "B publishes the edit again"
            let editRecord = theirText "edit" (st 5 m1)       // B's republished record, stamped by B (machine m1 here)
            // A, order 1: its deletion was pushed first; the row is forgotten.
            let a1 = decide ({ input with Row = Some Forgotten } |> noEntry |> editRecord)
            // A, order 2: the edit arrives while A's deletion is still local.
            let a2 = decide (noEntry input |> editRecord)
            for a in [ a1; a2 ] do
                Expect.equal a.Entry InsertFromRecord "A gets the memory back, from the record's text"
                Expect.equal a.Base.Content BaseBecomesTheirs "the base is the record, so the text is \"edit\""
            // B, on the next cycle, sees its own record: its entry stays and its text is untouched.
            let bNext =
                input |> editRecord |> withLocal (withText "edit") |> pendingContent (st 5 m1) |> pushingContent (st 5 m1)
            let d = decide bNext
            Expect.equal (groups d).Content (LeaveContent, ClearPending) "own record: nothing applied"
    ]

// ------------------------------------------------------------------------------------------------
// generators, the reference interpreter and the properties

let private genStamp : Gen<Stamp> = Gen.elements [ for n in 0 .. 5 do yield ours n; yield theirs n ]
let private genOptStamp : Gen<Stamp option> = Gen.frequency [ 2, Gen.constant None; 3, genStamp |> Gen.map Some ]
let private genTier : Gen<Tier> = Gen.elements [ Hot; Warm; Cold ]
let private genText : Gen<string> = Gen.elements [ "base"; "a"; "b" ]
let private genTags : Gen<string list> = Gen.elements [ []; [ "x" ]; [ "y" ]; [ "x"; "y" ]; [ "x"; "y"; "z" ] ]
let private genProject : Gen<string option> = Gen.elements [ None; Some "p" ]
let private genScope : Gen<string option> = Gen.elements [ None; Some "global" ]
let private genEntryType : Gen<EntryType> = Gen.elements [ Decision; Correction ]
let private genBool : Gen<bool> = Gen.elements [ true; false ]

let private genRecord : Gen<SyncRecord> =
    gen {
        let! text = genText
        let! tags = genTags
        let! project = genProject
        let! scope = genScope
        let! entryType = genEntryType
        let! contentStamp = genStamp
        let! tier = genTier
        let! tierStamp = genStamp
        let! sticky = genBool
        let! stickyStamp = genStamp
        return
            { record text with
                Tags = tags
                Project = project
                Scope = scope
                EntryType = entryType
                ContentStamp = contentStamp
                Placement = placement tier tierStamp sticky stickyStamp }
    }

let private genEntry : Gen<LocalEntry> =
    gen {
        let! text = genText
        let! tags = genTags
        let! project = genProject
        let! scope = genScope
        let! entryType = genEntryType
        let! tier = genTier
        let! sticky = genBool
        return
            { LocalEntry.Content = { fields text with Project = project; Scope = scope; EntryType = entryType }
              Tags = tags
              Tier = tier
              Sticky = sticky }
    }

let private genStamps : Gen<GroupStamps> =
    gen {
        let! c = genOptStamp
        let! s = genOptStamp
        let! t = genOptStamp
        return { GroupStamps.Content = c; Sticky = s; Tier = t }
    }

let private genOpt (g: Gen<'a>) : Gen<'a option> = Gen.frequency [ 1, Gen.constant None; 3, g |> Gen.map Some ]

let private genInput : Gen<MergeInput> =
    gen {
        let! row = Gen.elements [ None; Some Active; Some Forgotten; Some Left ]
        let! b = genOpt genRecord
        let! t = genOpt genRecord
        let! l = genOpt genEntry
        let! pending = genStamps
        let! pushing = genStamps
        let! isProven = genBool
        let! held = Gen.elements [ None; Some RemoteDeletion; Some LocalDeletion ]
        return { Row = row; Base = b; Theirs = t; Local = l; Pending = pending; Pushing = pushing; ApplyProven = isProven; HeldDelete = held }
    }

let private config = { FsCheckConfig.defaultConfig with maxTest = 2000 }
let private forAllInputs (name: string) (f: MergeInput -> bool) =
    testPropertyWithConfig config name (Prop.forAll (Arb.fromGen genInput) f)

/// `decide` ignores a base when there is no row.
let private effectiveBase (i: MergeInput) : SyncRecord option = if i.Row.IsSome then i.Base else None

// ---- the reference interpreter: apply a decision to a model of the entry, the row and the held item ----

let private contentOf (r: SyncRecord) : ContentFields =
    { Text = r.Content; Project = r.Project; Scope = r.Scope; EntryType = r.EntryType; Source = r.Source; CreatedAtMs = r.CreatedAtMs }

/// The base composed group by group (the spec's composition rule).
let private composeBase (oldB: SyncRecord option) (t: SyncRecord) (bu: BaseUpdate) : SyncRecord option =
    match oldB with
    | None -> Some t
    | Some b ->
        let theirsOf c = c = BaseBecomesTheirs
        let pick (c: BaseChange) (o: 'a) (n: 'a) : 'a = if theirsOf c then n else o
        Some
            { b with
                Id = t.Id
                Origin = t.Origin
                WrittenBy = t.WrittenBy
                Content = pick bu.Content b.Content t.Content
                EntryType = pick bu.Content b.EntryType t.EntryType
                Source = pick bu.Content b.Source t.Source
                CreatedAtMs = pick bu.Content b.CreatedAtMs t.CreatedAtMs
                Project = pick bu.Content b.Project t.Project
                Scope = pick bu.Content b.Scope t.Scope
                ContentStamp = pick bu.Content b.ContentStamp t.ContentStamp
                Tags = pick bu.Tags b.Tags t.Tags
                Placement =
                    { Tier = pick bu.Tier b.Placement.Tier t.Placement.Tier
                      TierStamp = pick bu.Tier b.Placement.TierStamp t.Placement.TierStamp
                      Sticky = pick bu.Sticky b.Placement.Sticky t.Placement.Sticky
                      StickyStamp = pick bu.Sticky b.Placement.StickyStamp t.Placement.StickyStamp } }

let private keepAllBases : BaseUpdate = { Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }

let private bump (s: Stamp) : Stamp = { s with Counter = s.Counter + 1 }

let private apply (i: MergeInput) (d: MergeDecision) : MergeInput =
    let oldBase = effectiveBase i
    let localAfter =
        match d.Entry, i.Local, i.Theirs with
        | InsertFromRecord, _, Some r -> Some { LocalEntry.Content = contentOf r; Tags = r.Tags; Tier = r.Placement.Tier; Sticky = false }
        | MergeGroups m, Some l, Some r ->
            let content =
                match fst m.Content with
                | TakeTheirs -> contentOf r
                | AdoptCreatedAt -> { l.Content with CreatedAtMs = r.CreatedAtMs }
                | LeaveContent
                | HeldAsPinEdit -> l.Content
            Some
                { l with
                    Content = content
                    Tags = (if m.Tags.ApplyLocal then m.Tags.Result else l.Tags)
                    Sticky = (if fst m.Sticky = UnpinLocally then false else l.Sticky)
                    Tier = (match fst m.Tier with Some t -> t | None -> l.Tier) }
        | DeleteEntry, _, _ -> None
        | _ -> i.Local
    let pending =
        if clearsPending d.Entry i.Local.IsSome then noStamps
        else
            match d.Entry, i.Theirs with
            | MergeGroups m, Some r ->
                let act (a: StampAction) (old: Stamp option) (t: Stamp) : Stamp option =
                    match a with
                    | KeepPending -> old
                    | ClearPending -> None
                    | RestampPending -> Some (bump t)
                { GroupStamps.Content = act (snd m.Content) i.Pending.Content r.ContentStamp
                  Sticky = act (snd m.Sticky) i.Pending.Sticky r.Placement.StickyStamp
                  Tier = act (snd m.Tier) i.Pending.Tier r.Placement.TierStamp }
            | _ -> i.Pending
    let row =
        match d.Entry with
        | DropRow -> None
        | PushDeletion -> Some Forgotten                  // the push lands: the file is removed
        | _ -> (match d.RowState with Some s -> Some s | None -> i.Row)
    let baseAfter =
        match d.Entry, i.Theirs with
        | DropRow, _ -> None
        | _, Some r -> if d.Base = keepAllBases then oldBase else composeBase oldBase r d.Base
        | _, None -> oldBase
    let closed = if d.CloseHeldDelete then None else i.HeldDelete
    let held =
        match d.Entry with
        | HoldDeletion x when closed.IsNone -> Some x
        | _ -> closed
    { i with
        Row = row
        Base = baseAfter
        Theirs = (if d.Entry = PushDeletion then None else i.Theirs)
        Local = localAfter
        Pending = pending
        HeldDelete = held }

let private show (x: 'a) : string = sprintf "%A" x

/// A row whose pending stamps are coherent with its pushing stamps. A stamp reaches the repository only after
/// it was recorded in `pushing_stamps`, so a record carrying one of this row's pending stamps has it in
/// `Pushing` too. Without that, clearing the pending stamp of an entry that is gone (the Row-effects rule)
/// would also clear the only proof that the record is the row's own, and a later cycle would read the same
/// record as a remote edit: a state the push protocol cannot produce. A held-delete item exists only on an
/// active row with a base.
let private coherent (i: MergeInput) : bool =
    let ok (pending: Stamp option) (pushing: Stamp option) (recordStamp: Stamp option) (baseStamp: Stamp option) =
        let carried = [ recordStamp; baseStamp ] |> List.choose id
        match pending with
        | Some p when List.contains p carried -> pushing = Some p
        | _ -> true
    // Only an entry that is gone has its pending stamps cleared by the decision (the Row-effects rule); with an
    // entry present a pending-only proof is still there on the next cycle, so those rows are not restricted.
    let ok pending pushing recordStamp baseStamp = i.Local.IsSome || ok pending pushing recordStamp baseStamp
    let groupOk (select: GroupStamps -> Stamp option) (stamp: SyncRecord -> Stamp) =
        ok (select i.Pending) (select i.Pushing) (i.Theirs |> Option.map stamp) (effectiveBase i |> Option.map stamp)
    // A held-delete item is opened only by W6 and W8, which need an active row with a base.
    let heldOk = i.HeldDelete.IsNone || (i.Row = Some Active && (effectiveBase i).IsSome)
    heldOk
    && groupOk (fun g -> g.Content) (fun r -> r.ContentStamp)
    && groupOk (fun g -> g.Sticky) (fun r -> r.Placement.StickyStamp)
    && groupOk (fun g -> g.Tier) (fun r -> r.Placement.TierStamp)

/// A decision that changes nothing further: used by the idempotence property.
let private isNeutral (d: MergeDecision) : bool =
    let groupsNeutral (m: Merged) =
        m.Content = (LeaveContent, KeepPending) && m.Sticky = (LeaveSticky, KeepPending) && m.Tier = (None, KeepPending) && not m.Tags.ApplyLocal
    let entryOk =
        match d.Entry with
        | LeaveEntry | Ignore | PushDeletion | Defer -> true      // PushDeletion repeats until the push lands
        | MergeGroups m -> groupsNeutral m                         // Repush and PushNeeded may repeat
        | InsertFromRecord | DeleteEntry | HoldDeletion _ | DropRow -> false
    let notesOk = if d.FreshContentStamp then d.Notes = [ EditBeatsDeletion ] || d.Notes.IsEmpty else d.Notes.IsEmpty
    entryOk && d.Reviews.IsEmpty && notesOk && not d.CloseHeldDelete

[<Tests>]
let propertyTests =
    testList "SyncMerge properties" [
        forAllInputs "total and deterministic: any input gives the same decision twice and never throws" (fun i -> decide i = decide i)

        testCase "total over the exhaustive small domain" <| fun _ ->
            let records = [ None; Some (record "base"); Some (record "remote" |> withContentStamp (theirs 3)) ]
            let entries = [ None; Some (local "base"); Some { local "base" with Sticky = true } ]
            let mutable n = 0
            for row in [ None; Some Active; Some Forgotten; Some Left ] do
                for t in records do
                    for l in entries do
                        for b in [ None; Some (record "base") ] do
                            for isProven in bothApplies do
                                for held in [ None; Some RemoteDeletion; Some LocalDeletion ] do
                                    for pendingPin in [ None; Some (ours 4) ] do
                                        let i =
                                            { Row = row; Base = b; Theirs = t; Local = l; Pending = { noStamps with Sticky = pendingPin }
                                              Pushing = noStamps; ApplyProven = isProven; HeldDelete = held }
                                        ignore (decide i)
                                        n <- n + 1
            Expect.equal n (4 * 3 * 3 * 2 * 2 * 3 * 2) "every combination was decided"

        forAllInputs "deletion needs proof: DeleteEntry only for a proven, unheld deletion of an unpinned entry with nothing pending" (fun i ->
            match (decide i).Entry with
            | DeleteEntry ->
                match i.Local, effectiveBase i with
                | Some l, Some bb ->
                    let pendingText = i.Pending.Content.IsSome && l.Content.Text <> bb.Content
                    i.Row = Some Active && i.Theirs.IsNone && not l.Sticky && i.Pending.Sticky.IsNone && not pendingText
                    && i.ApplyProven && i.HeldDelete <> Some RemoteDeletion
                | _ -> false
            | _ -> true)

        forAllInputs "a pin is never taken from a record" (fun i ->
            let d = decide i
            let after = apply i d
            match d.Entry with
            | MergeGroups m ->
                let apply = fst m.Sticky
                let l = i.Local.Value
                (apply = ReviewNewPin) = List.contains NewPin d.Reviews
                && (apply <> UnpinLocally || l.Sticky)
                && (not (after.Local.Value.Sticky) || l.Sticky)
            | InsertFromRecord ->
                (match after.Local with Some e -> not e.Sticky | None -> false)
                && (List.contains NewPin d.Reviews = i.Theirs.Value.Placement.Sticky)
            | _ -> true)

        forAllInputs "never from an older stamp: an older group is left alone, its base is kept, and it is re-pushed exactly without a pending stamp" (fun i ->
            match decide i, i.Theirs, effectiveBase i with
            | { Entry = MergeGroups m; Base = bu }, Some t, Some b ->
                let older (ts: Stamp) (bs: Stamp) = Stamp.compare ts bs < 0
                let contentOk =
                    not (older t.ContentStamp b.ContentStamp)
                    || (m.Content = (LeaveContent, KeepPending) && bu.Content = KeepBase && m.Repush.Content = i.Pending.Content.IsNone)
                let stickyOk =
                    not (older t.Placement.StickyStamp b.Placement.StickyStamp)
                    || (m.Sticky = (LeaveSticky, KeepPending) && bu.Sticky = KeepBase && m.Repush.Sticky = i.Pending.Sticky.IsNone)
                let tierOk =
                    not (older t.Placement.TierStamp b.Placement.TierStamp)
                    || (m.Tier = (None, KeepPending) && bu.Tier = KeepBase && m.Repush.Tier = i.Pending.Tier.IsNone)
                let repushOk =
                    (not m.Repush.Content || (older t.ContentStamp b.ContentStamp && i.Pending.Content.IsNone))
                    && (not m.Repush.Sticky || (older t.Placement.StickyStamp b.Placement.StickyStamp && i.Pending.Sticky.IsNone))
                    && (not m.Repush.Tier || (older t.Placement.TierStamp b.Placement.TierStamp && i.Pending.Tier.IsNone))
                contentOk && stickyOk && tierOk && repushOk
            | _ -> true)

        testPropertyWithConfig config
            "idempotent: applying a decision and deciding again changes nothing further (for a coherent row)"
            (Prop.forAll (Arb.fromGen (Gen.filter coherent genInput)) (fun i ->
                let d1 = decide i
                let i2 = apply i d1
                let d2 = decide i2
                if isNeutral d2 then true
                else failwithf "first: %s\nsecond input: %s\nsecond: %s" (show d1) (show i2) (show d2)))

        testCase "a PushDeletion repeats in a proven apply until the push lands" <| fun _ ->
            let i = noEntry input
            let d = decide i
            Expect.equal d.Entry PushDeletion "first"
            Expect.equal (decide i).Entry PushDeletion "the same input decides the same: the deletion is pushed again"
            Expect.equal (decide (apply i d)).Entry Ignore "once the push lands the record is gone and nothing is left to do"

        testPropertyWithConfig config
            "order of arrival for content is independent of the order (no own stamps, the same sticky group in both versions)"
            (Prop.forAll
                (Arb.fromGen (gen {
                    let! b = genRecord
                    let! l = genEntry
                    let! v1 = genRecord
                    let! v2 = genRecord
                    let! isProven = genBool
                    return b, l, { v2 with Placement = { v2.Placement with Sticky = v1.Placement.Sticky; StickyStamp = v1.Placement.StickyStamp } }, v1, isProven }))
                (fun (b, l, v2, v1, isProven) ->
                    (v1.ContentStamp <> v2.ContentStamp) ==> lazy (
                        let s0 : MergeInput =
                            { Row = Some Active; Base = Some b; Theirs = None; Local = Some l; Pending = noStamps; Pushing = noStamps
                              ApplyProven = isProven; HeldDelete = None }
                        let step (s: MergeInput) (v: SyncRecord) : MergeInput =
                            let i = { s with Theirs = Some v }
                            apply i (decide i)
                        let final (a: SyncRecord) (c: SyncRecord) = step (step s0 a) c
                        let x = (final v1 v2).Local.Value.Content
                        let y = (final v2 v1).Local.Value.Content
                        x.Text = y.Text && x.Project = y.Project)))

        testPropertyWithConfig config
            "tier is monotone in either order, and order independent when the greater tier stamp's tier is not colder"
            (Prop.forAll
                (Arb.fromGen (gen {
                    let! b = genRecord
                    let! l = genEntry
                    let! v1 = genRecord
                    let! v2 = genRecord
                    let! isProven = genBool
                    return b, l, v1, v2, isProven }))
                (fun (b, l, v1, v2, isProven) ->
                    let s0 : MergeInput =
                        { Row = Some Active; Base = Some b; Theirs = None; Local = Some l; Pending = noStamps; Pushing = noStamps
                          ApplyProven = isProven; HeldDelete = None }
                    let step (s: MergeInput) (v: SyncRecord) : MergeInput =
                        let i = { s with Theirs = Some v }
                        apply i (decide i)
                    let final (a: SyncRecord) (c: SyncRecord) = step (step s0 a) c
                    let rank (s: MergeInput) = tierRank s.Local.Value.Tier
                    let monotone = rank (final v1 v2) <= tierRank l.Tier && rank (final v2 v1) <= tierRank l.Tier
                    let greater, other = if Stamp.compare v1.Placement.TierStamp v2.Placement.TierStamp > 0 then v1, v2 else v2, v1
                    let ordered =
                        v1.Placement.TierStamp = v2.Placement.TierStamp
                        || tierRank greater.Placement.Tier > tierRank other.Placement.Tier
                        || (final v1 v2).Local.Value.Tier = (final v2 v1).Local.Value.Tier
                    monotone && ordered))

        forAllInputs "a decision never lowers a tier" (fun i ->
            match i.Local, (apply i (decide i)).Local with
            | Some before, Some after when i.Theirs.IsSome -> tierRank after.Tier <= tierRank before.Tier
            | _ -> true)
    ]
