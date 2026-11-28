module TotalRecall.Core.Tests.SyncAliasTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncAlias

let private a = "aaaa0001"
let private b = "bbbb0002"
let private c = "cccc0003"

[<Tests>]
let canonicalTests =
    testList "SyncAlias.canonical" [
        testCase "the lowest of two" <| fun _ -> Expect.equal (canonical [ b; a ]) a "lowest"
        testCase "a single id" <| fun _ -> Expect.equal (canonical [ b ]) b "single"
        testCase "ordinal, not culture" <| fun _ ->
            Expect.equal (canonical [ "a1aaaaaa"; "a-aaaaaa" ]) "a-aaaaaa" "'-' is below '1'"
        testCase "the empty list throws" <| fun _ ->
            Expect.throwsT<System.ArgumentException> (fun () -> canonical [] |> ignore) "empty"
    ]

let private noInput (recordId: string) (targets: string list) : DetectInput =
    { RecordId = recordId
      Targets = targets
      OpenRows = []
      LocalMembers = Set.empty
      ExistingRows = Set.empty }

[<Tests>]
let detectTests =
    testList "SyncAlias.detect" [
        testCase "no targets is no alias" <| fun _ ->
            Expect.isNone (detect (noInput a [])) "none"
            Expect.isNone (detect { noInput a [] with OpenRows = [ (b, c) ]; LocalMembers = set [ b ] }) "none with open rows"

        testCase "the record lower than the local id" <| fun _ ->
            let d = (detect (noInput a [ b ])).Value
            Expect.equal d.Canonical a "canonical"
            Expect.equal d.Rows [ (a, b) ] "rows"

        testCase "the record higher than the local id" <| fun _ ->
            let d = (detect (noInput b [ a ])).Value
            Expect.equal d.Canonical a "canonical"
            Expect.equal d.Rows [ (a, b) ] "rows"

        testCase "three members" <| fun _ ->
            let d = (detect (noInput c [ a; b ])).Value
            Expect.equal d.Canonical a "canonical"
            Expect.equal d.Members [ a; b; c ] "members"
            Expect.equal d.Rows [ (a, b); (a, c) ] "rows"

        testCase "an existing row of the pair is not listed" <| fun _ ->
            let d = (detect { noInput c [ a; b ] with ExistingRows = set [ (a, b) ] }).Value
            Expect.equal d.Rows [ (a, c) ] "only the new pair"

        testCase "a later lower arrival" <| fun _ ->
            let input =
                { RecordId = "0000aaaa"
                  Targets = [ b ]
                  OpenRows = [ (a, b) ]
                  LocalMembers = set [ b ]
                  ExistingRows = set [ (a, b) ] }
            let d = (detect input).Value
            Expect.equal d.Canonical "0000aaaa" "canonical"
            Expect.equal d.Members [ "0000aaaa"; a; b ] "members"
            Expect.equal d.Rows [ ("0000aaaa", b) ] "no row for the non-local, non-record member"

        testCase "an open-row member that is local gets a row" <| fun _ ->
            let input =
                { RecordId = "0000aaaa"
                  Targets = [ c ]
                  OpenRows = [ (a, b) ]
                  LocalMembers = set [ a; b ]
                  ExistingRows = set [ (a, b) ] }
            let d = (detect input).Value
            Expect.equal d.Rows [ ("0000aaaa", a); ("0000aaaa", b); ("0000aaaa", c) ] "rows"

        testCase "members are distinct and ordinal for unsorted, repeated input" <| fun _ ->
            let input =
                { RecordId = c
                  Targets = [ b; a; b ]
                  OpenRows = [ (b, a); (a, c) ]
                  LocalMembers = set [ a; b ]
                  ExistingRows = Set.empty }
            let d = (detect input).Value
            Expect.equal d.Members [ a; b; c ] "members"
            Expect.equal d.Rows [ (a, b); (a, c) ] "rows"
    ]

let private m2 = (MachineId.tryCreate "0000000b").Value
let private st : Stamp = { TimeMs = 1_700_000_000_000L; Counter = 0; Machine = m2 }
let private m1 = (MachineId.tryCreate "0000000a").Value

let private record (sticky: bool) : SyncRecord =
    { Id = "3f9c2a7e"
      Content = "text"
      EntryType = Decision
      Tags = []
      Source = None
      CreatedAtMs = 1_700_000_000_000L
      Project = None
      Scope = None
      Placement = { Placement.Tier = Warm; TierStamp = st; Sticky = sticky; StickyStamp = st }
      ContentStamp = st
      Origin = None
      WrittenBy = None }

let private fields (text: string) : ContentFields =
    { Text = text; Project = None; Scope = None; EntryType = Decision; Source = None; CreatedAtMs = 1_700_000_000_000L }

let private local (text: string) : LocalEntry =
    { LocalEntry.Content = fields text; Tags = []; Tier = Warm; Sticky = false }

let private noStamps : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

let private input : MergeInput =
    { Row = Some Active
      Base = Some (record false)
      Theirs = Some (record false)
      Local = Some (local "text")
      Pending = noStamps
      Pushing = noStamps
      ApplyProven = true
      HeldDelete = None }

let private keepAll : BaseUpdate = { BaseUpdate.Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }

[<Tests>]
let skipInsteadTests =
    testList "SyncAlias.skipInstead" [
        testCase "an insert becomes the plain ignore" <| fun _ ->
            let inserted = insertFrom (record true) false
            Expect.equal inserted.Entry InsertFromRecord "precondition"
            Expect.isNonEmpty inserted.Reviews "precondition: the sticky record carries a NewPin"
            Expect.equal (skipInstead inserted) ignored "exactly ignored"
            Expect.equal (skipInstead (insertFrom (record false) true)) ignored "edit-beats-deletion insert too"

        testCase "ignored is the plain ignore" <| fun _ ->
            Expect.equal ignored.Entry Ignore "entry"
            Expect.equal ignored.Base keepAll "base"
            Expect.equal ignored.RowState None "row state"
            Expect.equal ignored.Reviews [] "reviews"
            Expect.equal ignored.Notes [] "notes"
            Expect.isFalse ignored.CloseHeldDelete "close"
            Expect.isFalse ignored.FreshContentStamp "stamp"

        testCase "every other decision is unchanged" <| fun _ ->
            let defer = decide { input with Row = Some Left }
            let leave = decide { input with Theirs = None; Local = Some { local "text" with Sticky = true }; HeldDelete = Some RemoteDeletion }
            let del = decide { input with Theirs = None }
            let groupsDecision = decide { input with Theirs = Some { record false with Content = "other"; ContentStamp = { st with TimeMs = st.TimeMs + 1000L } } }
            Expect.equal defer.Entry Defer "defer precondition"
            Expect.equal leave.Entry LeaveEntry "leave precondition"
            Expect.equal del.Entry DeleteEntry "delete precondition"
            match groupsDecision.Entry with
            | MergeGroups _ -> ()
            | other -> failtestf "expected MergeGroups, got %A" other
            for d in [ defer; leave; del; groupsDecision ] do
                Expect.equal (skipInstead d) d (sprintf "%A" d.Entry)
    ]

[<Tests>]
let targetTests =
    testList "SyncAlias.target" [
        testCase "the lowest parsing candidate" <| fun _ ->
            Expect.equal (target [ c; a; b ] (set [ b; c ])) (Some b) "lowest parsing"
        testCase "none parses" <| fun _ -> Expect.equal (target [ a; b ] Set.empty) None "none"
        testCase "no candidates" <| fun _ -> Expect.equal (target [] (set [ a ])) None "empty"
        testCase "duplicates are ignored" <| fun _ ->
            Expect.equal (target [ b; b; a; a ] (set [ a; b ])) (Some a) "dups"
    ]

let private cand id t free : Candidate = { Id = id; Target = t; TargetFree = free }

[<Tests>]
let chooseTests =
    testList "SyncAlias.choose" [
        testCase "empty" <| fun _ ->
            let r = choose []
            Expect.equal r.Converging Map.empty "converging"
            Expect.equal r.Ignored Set.empty "ignored"
        testCase "no target is ignored" <| fun _ ->
            let r = choose [ cand b None true ]
            Expect.equal r.Converging Map.empty "converging"
            Expect.equal r.Ignored (set [ b ]) "ignored"
        testCase "a target that is not free is ignored" <| fun _ ->
            let r = choose [ cand b (Some a) false ]
            Expect.equal r.Converging Map.empty "converging"
            Expect.equal r.Ignored (set [ b ]) "ignored"
        testCase "of two with one target the lowest converges" <| fun _ ->
            let r = choose [ cand b (Some a) true; cand c (Some a) true ]
            Expect.equal r.Converging (Map.ofList [ (b, a) ]) "converging"
            Expect.equal r.Ignored (set [ c ]) "ignored"
        testCase "the same in reverse order" <| fun _ ->
            let r = choose [ cand c (Some a) true; cand b (Some a) true ]
            Expect.equal r.Converging (Map.ofList [ (b, a) ]) "converging"
            Expect.equal r.Ignored (set [ c ]) "ignored"
        testCase "different targets both converge" <| fun _ ->
            let r = choose [ cand b (Some "aaaa0000") true; cand c (Some a) true ]
            Expect.equal r.Converging (Map.ofList [ (b, "aaaa0000"); (c, a) ]) "converging"
            Expect.equal r.Ignored Set.empty "ignored"
        testCase "a target that is not free does not stop another with a free target" <| fun _ ->
            let r = choose [ cand b (Some a) false; cand c (Some "aaaa0000") true ]
            Expect.equal r.Converging (Map.ofList [ (c, "aaaa0000") ]) "converging"
            Expect.equal r.Ignored (set [ b ]) "ignored"
        testCase "a candidate that is not free does not take the target from the next" <| fun _ ->
            let r = choose [ cand b (Some a) false; cand c (Some a) true ]
            Expect.equal r.Converging (Map.ofList [ (c, a) ]) "converging"
            Expect.equal r.Ignored (set [ b ]) "ignored"
    ]
