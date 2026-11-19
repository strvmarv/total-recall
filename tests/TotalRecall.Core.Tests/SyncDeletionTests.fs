module TotalRecall.Core.Tests.SyncDeletionTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncDeletion

let private keepAll : BaseUpdate = { BaseUpdate.Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }

let private plain : MergeDecision =
    { Entry = Ignore
      Base = keepAll
      RowState = None
      Reviews = []
      Notes = []
      CloseHeldDelete = false
      FreshContentStamp = false }

let private noRepush : GroupFlags = { GroupFlags.Content = false; Sticky = false; Tier = false }

let private merged : Merged =
    { Merged.Content = (LeaveContent, KeepPending)
      Tags = { Result = []; ApplyLocal = false; PushNeeded = false }
      Sticky = (LeaveSticky, KeepPending)
      Tier = (None, KeepPending)
      Repush = noRepush }

let private flags (c: bool) (s: bool) (t: bool) : GroupFlags = { GroupFlags.Content = c; Sticky = s; Tier = t }

let private allFlags : GroupFlags list =
    [ for c in [ false; true ] do
        for s in [ false; true ] do
            for t in [ false; true ] -> flags c s t ]

[<Tests>]
let guardTests =
    testList "SyncDeletion guard (decisions D3 to D5)" [
        testCase "guardLimit_is_ten_for_small_stores" <| fun () ->
            Expect.equal (guardLimit 20 0) 10 "empty store"
            Expect.equal (guardLimit 20 49) 10 "49 rows"
            Expect.equal (guardLimit 20 50) 10 "50 rows"
            Expect.equal (guardLimit 20 100) 20 "100 rows"
            Expect.equal (guardLimit 20 109) 21 "109 rows"
        testCase "guardLimit_clamps_percent" <| fun () ->
            Expect.equal (guardLimit -5 1000) 10 "below 0"
            Expect.equal (guardLimit 0 1000) 10 "0"
            Expect.equal (guardLimit 100 1000) 1000 "100"
            Expect.equal (guardLimit 250 1000) 1000 "above 100"
        testCase "guardLimit counts a negative active as zero" <| fun () ->
            Expect.equal (guardLimit 20 -500) 10 "negative"
        testCase "guardTrips_is_strictly_greater" <| fun () ->
            Expect.isFalse (guardTrips 10 10) "10 of 10"
            Expect.isTrue (guardTrips 11 10) "11 of 10"
            Expect.isFalse (guardTrips 0 10) "0 of 10"
    ]

[<Tests>]
let holdTests =
    testList "SyncDeletion holds (decision D16)" [
        testCase "holdInstead_converts_delete_and_push" <| fun () ->
            let del =
                { plain with
                    Entry = DeleteEntry
                    RowState = Some Forgotten
                    Notes = [ PendingChangesDropped ] }
            let held = holdInstead del
            Expect.equal held.Entry (HoldDeletion RemoteDeletion) "remote hold"
            Expect.equal held.RowState None "row state cleared"
            Expect.equal held.Notes [] "notes cleared"
            let theirs = { keepAll with Content = BaseBecomesTheirs; Tier = BaseBecomesTheirs }
            let push = { plain with Entry = PushDeletion; Base = theirs; RowState = Some Forgotten; Notes = [ EditBeatsDeletion ] }
            let heldPush = holdInstead push
            Expect.equal heldPush.Entry (HoldDeletion LocalDeletion) "local hold"
            Expect.equal heldPush.Base theirs "base update kept"
            Expect.equal heldPush.RowState None "row state cleared"
            Expect.equal heldPush.Notes [] "notes cleared"
        testCase "holdInstead_leaves_everything_else" <| fun () ->
            let others =
                [ InsertFromRecord
                  MergeGroups merged
                  MergeGroups { merged with Repush = flags true true true }
                  LeaveEntry
                  HoldDeletion RemoteDeletion
                  HoldDeletion LocalDeletion
                  Ignore
                  DropRow
                  Defer ]
            for e in others do
                let d =
                    { plain with
                        Entry = e
                        RowState = Some Active
                        Notes = [ EditBeatsDeletion ]
                        CloseHeldDelete = true
                        FreshContentStamp = true }
                Expect.equal (holdInstead d) d (sprintf "%A" e)
        testCase "directionName" <| fun () ->
            Expect.equal (directionName RemoteDeletion) "remote" "remote"
            Expect.equal (directionName LocalDeletion) "local" "local"
        testCase "holdCause_table" <| fun () ->
            for dir in [ RemoteDeletion; LocalDeletion ] do
                Expect.equal (holdCause true true dir) "guard" (sprintf "proven converted %A" dir)
                Expect.equal (holdCause false true dir) "guard" (sprintf "unproven converted %A" dir)
                Expect.equal (holdCause false false dir) "unproven" (sprintf "unproven %A" dir)
            Expect.equal (holdCause true false RemoteDeletion) "sticky" "proven remote"
            Expect.equal (holdCause true false LocalDeletion) "unproven" "proven local"
    ]

[<Tests>]
let repushGroupsTests =
    testList "SyncDeletion.RepushGroups" [
        testCase "repushGroups_roundtrip" <| fun () ->
            for f in allFlags do
                Expect.equal (RepushGroups.decode (defaultArg (RepushGroups.encode f) null)) f (sprintf "%A" f)
            Expect.equal (RepushGroups.encode noRepush) None "all false is no text"
            Expect.equal (RepushGroups.encode (flags true true true)) (Some "content,sticky,tier") "all"
            Expect.equal (RepushGroups.encode (flags true false true)) (Some "content,tier") "content and tier"
            Expect.equal (RepushGroups.encode (flags false true false)) (Some "sticky") "sticky"
        testCase "repushGroups_decode_rejects_other_text" <| fun () ->
            for text in [ "sticky,content"; "content,content"; "Content"; "tier "; ""; null; "  "; "content,"; ",tier"; "tags"; "content;tier" ] do
                Expect.equal (RepushGroups.decode text) noRepush (sprintf "%A" text)
    ]
