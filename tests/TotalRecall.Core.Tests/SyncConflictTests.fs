module TotalRecall.Core.Tests.SyncConflictTests

open System.Security.Cryptography
open System.Text
open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncConflict

// `SyncMergeTests` is not touched by this part: the merge decision is read, never changed.

let private hex (bytes: byte[]) = System.Convert.ToHexString(bytes).ToLowerInvariant()

[<Tests>]
let shownHashTests =
    testList "SyncConflict.shownHash" [
        testCase "known answer from the stated byte layout" <| fun _ ->
            let bytes =
                Array.concat
                    [ Encoding.UTF8.GetBytes "total-recall/review/conflict/v1\n"
                      [| 0x01uy; 0uy; 0uy; 0uy; 1uy; byte 'a' |]
                      [| 0uy; 0uy; 0uy; 2uy |]
                      [| 0x01uy; 0uy; 0uy; 0uy; 1uy; byte 'x' |]
                      [| 0x01uy; 0uy; 0uy; 0uy; 0uy |] ]
            Expect.equal (shownHash (Some "a") [ "x"; "" ]) (hex (SHA256.HashData bytes)) "layout"

        testCase "an absent applied text differs from an empty one" <| fun _ ->
            Expect.notEqual (shownHash None [ "x" ]) (shownHash (Some "") [ "x" ]) "none vs empty"

        testCase "the applied text and the first loser are length-framed" <| fun _ ->
            Expect.notEqual (shownHash (Some "ab") [ "c" ]) (shownHash (Some "a") [ "bc" ]) "framing"

        testCase "the order of the losers counts" <| fun _ ->
            Expect.notEqual (shownHash (Some "a") [ "x"; "y" ]) (shownHash (Some "a") [ "y"; "x" ]) "order"

        testCase "the count of the losers counts" <| fun _ ->
            Expect.notEqual (shownHash (Some "a") [ "x" ]) (shownHash (Some "a") [ "x"; "" ]) "count"

        testCase "result is 64 lowercase hex characters" <| fun _ ->
            let h = shownHash (Some "text") [ "x"; "y" ]
            Expect.equal h.Length 64 "length"
            Expect.isTrue (h |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) "lowercase hex"
    ]

let private loser side text : Loser =
    { Side = side; Text = text; Stamp = ""; Machine = "0000000a" }

[<Tests>]
let appendTests =
    testList "SyncConflict.append" [
        testCase "maxLosers is 8" <| fun _ -> Expect.equal maxLosers 8 "bound"

        testCase "appends to an empty list" <| fun _ ->
            let l = loser Local "a"
            Expect.equal (append [] l) [ l ] "one"

        testCase "appends at the end" <| fun _ ->
            let a = loser Local "a"
            let b = loser Remote "b"
            Expect.equal (append [ a ] b) [ a; b ] "order"

        testCase "the same text from the other side is not added" <| fun _ ->
            let a = loser Local "a"
            let b = loser Remote "b"
            Expect.equal (append [ a; b ] (loser Remote "a")) [ a; b ] "dup of the first"
            Expect.equal (append [ a; b ] (loser Local "b")) [ a; b ] "dup of the last"

        testCase "the ninth drops the oldest and keeps the order" <| fun _ ->
            let eight = [ for i in 1..8 -> loser Remote (sprintf "t%d" i) ]
            let nine = append eight (loser Local "t9")
            Expect.equal (nine |> List.map (fun l -> l.Text)) [ "t2"; "t3"; "t4"; "t5"; "t6"; "t7"; "t8"; "t9" ] "texts"
            Expect.equal nine.Length 8 "bounded"

        testCase "a duplicate of the ninth-dropped text is added again" <| fun _ ->
            let eight = [ for i in 1..8 -> loser Remote (sprintf "t%d" i) ]
            let nine = append eight (loser Local "t9")
            let again = append nine (loser Local "t1")
            Expect.equal (again |> List.map (fun l -> l.Text)) [ "t3"; "t4"; "t5"; "t6"; "t7"; "t8"; "t9"; "t1" ] "re-added"

        testCase "a duplicate at the bound changes nothing" <| fun _ ->
            let eight = [ for i in 1..8 -> loser Remote (sprintf "t%d" i) ]
            Expect.equal (append eight (loser Local "t1")) eight "no drop"
    ]

[<Tests>]
let loserOfTests =
    testList "SyncConflict.loserOf" [
        testCase "with a stamp: its text form and its machine" <| fun _ ->
            let st =
                { TimeMs = 1_700_000_000_000L
                  Counter = 3
                  Machine = (MachineId.tryCreate "deadbeef").Value }
            let l = loserOf "00000001" { Side = Remote; Text = "t"; Stamp = Some st }
            Expect.equal l { Side = Remote; Text = "t"; Stamp = Stamp.format st; Machine = "deadbeef" } "loser"

        testCase "without a stamp: empty text form and this machine" <| fun _ ->
            let l = loserOf "00000001" { Side = Local; Text = "t"; Stamp = None }
            Expect.equal l { Side = Local; Text = "t"; Stamp = ""; Machine = "00000001" } "loser"
    ]

[<Tests>]
let sideStateTests =
    testList "SyncConflict names" [
        testCase "side and state names round trip" <| fun _ ->
            Expect.equal (sideName Local) "local" "local"
            Expect.equal (sideName Remote) "remote" "remote"
            Expect.equal (trySide "local") (Some Local) "try local"
            Expect.equal (trySide "remote") (Some Remote) "try remote"
            Expect.equal (trySide "Local") None "case"
            Expect.equal (stateName Open) "open" "open"
            Expect.equal (stateName Unseen) "unseen" "unseen"
            Expect.equal (tryState "open") (Some Open) "try open"
            Expect.equal (tryState "unseen") (Some Unseen) "try unseen"
            Expect.equal (tryState "approved") None "approved is not a conflict state"
    ]

let private decision entry closeHeld : MergeDecision =
    { Entry = entry
      Base =
        { Content = KeepBase
          Tags = KeepBase
          Sticky = KeepBase
          Tier = KeepBase }
      RowState = None
      Reviews = []
      Notes = []
      CloseHeldDelete = closeHeld
      FreshContentStamp = false }

[<Tests>]
let heldAfterTests =
    testList "SyncConflict.heldAfter" [
        testCase "a hold is held whatever else is true" <| fun _ ->
            for dir in [ LocalDeletion; RemoteDeletion ] do
                for held in [ false; true ] do
                    for close in [ false; true ] do
                        Expect.isTrue (heldAfter (decision (HoldDeletion dir) close) held) (sprintf "%A %b %b" dir held close)

        testCase "a pushed deletion and a remote deletion carried out are not held, approved item or not" <| fun _ ->
            for e in [ PushDeletion; DeleteEntry ] do
                for held in [ false; true ] do
                    for close in [ false; true ] do
                        Expect.isFalse (heldAfter (decision e close) held) (sprintf "%A %b %b" e held close)

        testCase "other outcomes keep an open or approved held item unless the decision closes it" <| fun _ ->
            let others =
                [ InsertFromRecord; LeaveEntry; Ignore; DropRow; Defer
                  MergeGroups
                      { Content = (LeaveContent, KeepPending)
                        Tags = { Result = []; ApplyLocal = false; PushNeeded = false }
                        Sticky = (LeaveSticky, KeepPending)
                        Tier = (None, KeepPending)
                        Repush = { GroupFlags.Content = false; Sticky = false; Tier = false } } ]
            for e in others do
                Expect.isTrue (heldAfter (decision e false) true) (sprintf "%A held" e)
                Expect.isFalse (heldAfter (decision e true) true) (sprintf "%A held, closed" e)
                Expect.isFalse (heldAfter (decision e false) false) (sprintf "%A not held" e)
                Expect.isFalse (heldAfter (decision e true) false) (sprintf "%A not held, closed" e)
    ]

let private item state hash (texts: string list) : ItemView =
    { State = state
      Hash = hash
      Losers = texts |> List.map (loser Remote) }

let private active : RowState option = Some Active

/// An input in which only rule 4 applies: entry "w" present, loser "x" kept, the hash is the shown hash.
let private quiet : ReconcileInput =
    { Item = item Open (shownHash (Some "w") [ "x" ]) [ "x" ]
      EntryText = Some "w"
      Row = active
      HeldAfter = false }

[<Tests>]
let reconcileTests =
    testList "SyncConflict.reconcile" [
        // Rule 1: a forgotten or left row, or an absent entry that is not held, removes. The cases below add, one at a
        // time, a condition that alone would give another outcome (a changed hash: Rewrite through rule 3; a loser equal
        // to the entry's text: Rewrite through rule 2; a held deletion: Keep), and rule 1 still decides.
        testCase "rule 1: a forgotten or left row removes" <| fun _ ->
            Expect.equal (reconcile { quiet with Row = Some Forgotten }) Remove "forgotten"
            Expect.equal (reconcile { quiet with Row = Some Left }) Remove "left"

        testCase "rule 1 before 3: a forgotten row removes although the shown hash changed" <| fun _ ->
            let i = { quiet with Item = item Open "stale" [ "x" ] }
            Expect.equal (reconcile { i with Row = Some Forgotten }) Remove "forgotten"
            Expect.equal (reconcile { i with Row = Some Left }) Remove "left"

        testCase "rule 1 before 2: a forgotten row removes although a loser equals the entry (would Rewrite)" <| fun _ ->
            let i = { quiet with Item = item Open "stale" [ "w"; "x" ] }
            Expect.equal (reconcile { i with Row = Some Forgotten }) Remove "forgotten"
            Expect.equal (reconcile { i with Row = Some Left }) Remove "left"

        testCase "rule 1: a forgotten row beats a held deletion" <| fun _ ->
            let i = { quiet with EntryText = None; HeldAfter = true; Item = item Open "stale" [ "x" ] }
            Expect.equal (reconcile { i with Row = Some Forgotten }) Remove "forgotten, held"
            Expect.equal (reconcile { i with Row = Some Left }) Remove "left, held"

        testCase "rule 1: entry absent and not held removes" <| fun _ ->
            Expect.equal (reconcile { quiet with EntryText = None; HeldAfter = false }) Remove "gone"
            Expect.equal (reconcile { quiet with EntryText = None; HeldAfter = false; Row = None }) Remove "gone, no row"
            Expect.equal (reconcile { quiet with EntryText = None; HeldAfter = false; Item = item Unseen "stale" [ "x" ] }) Remove "gone, changed hash"

        testCase "entry absent and held keeps, whatever the hash" <| fun _ ->
            let i = { quiet with EntryText = None; HeldAfter = true }
            Expect.equal (reconcile i) Keep "held"
            Expect.equal (reconcile { i with Item = item Open (shownHash None [ "x" ]) [ "x" ] }) Keep "held, hash of none"
            Expect.equal (reconcile { i with Item = item Open "stale" [ "x" ] }) Keep "held, different hash"
            Expect.equal (reconcile { i with Item = item Unseen "stale" [ "x"; "y" ] }) Keep "held, unseen"
            Expect.equal (reconcile { i with Row = None }) Keep "held, no row"

        testCase "rule 2: a loser equal to the entry's text is dropped and the item is rewritten" <| fun _ ->
            let i = { quiet with Item = item Open (shownHash (Some "w") [ "w"; "x" ]) [ "w"; "x" ] }
            Expect.equal (reconcile i) (Rewrite(shownHash (Some "w") [ "x" ], [ loser Remote "x" ])) "dropped"

        testCase "rule 2: the rewrite keeps the order and the stamps of the losers kept" <| fun _ ->
            let a = { loser Remote "a" with Stamp = "s1"; Machine = "0000000b" }
            let b = { loser Local "w" with Stamp = "s2" }
            let c = { loser Remote "c" with Stamp = "s3"; Machine = "0000000c" }
            let i = { quiet with Item = { State = Open; Hash = "any"; Losers = [ a; b; c ] } }
            Expect.equal (reconcile i) (Rewrite(shownHash (Some "w") [ "a"; "c" ], [ a; c ])) "kept as they were"

        testCase "rule 2: every loser equal removes" <| fun _ ->
            let i = { quiet with Item = item Open "any" [ "w" ] }
            Expect.equal (reconcile i) Remove "all equal"
            Expect.equal (reconcile { i with Item = item Open "any" [ "w"; "w" ] }) Remove "all equal, twice"

        testCase "rule 2 before 3: with every loser equal a changed hash does not Rewrite" <| fun _ ->
            Expect.equal (reconcile { quiet with Item = item Unseen "stale" [ "w" ] }) Remove "remove, not rewrite"

        testCase "rule 3: the applied text changed, the losers stay" <| fun _ ->
            let i = { quiet with Item = item Open (shownHash (Some "old") [ "x" ]) [ "x" ] }
            Expect.equal (reconcile i) (Rewrite(shownHash (Some "w") [ "x" ], [ loser Remote "x" ])) "rewrite"
            Expect.equal (reconcile { i with Item = item Unseen (shownHash (Some "old") [ "x" ]) [ "x" ] }) (Rewrite(shownHash (Some "w") [ "x" ], [ loser Remote "x" ])) "unseen"

        testCase "rule 3: the stored hash covers other losers" <| fun _ ->
            let i = { quiet with Item = item Open (shownHash (Some "w") [ "x"; "y" ]) [ "x" ] }
            Expect.equal (reconcile i) (Rewrite(shownHash (Some "w") [ "x" ], [ loser Remote "x" ])) "losers differ from the hash"

        testCase "rule 4: an unseen item with the same hash is kept" <| fun _ ->
            Expect.equal (reconcile { quiet with Item = { quiet.Item with State = Unseen } }) Keep "unseen, same hash"

        testCase "a row of none with the entry present does not remove" <| fun _ ->
            Expect.equal (reconcile { quiet with Row = None }) Keep "no row, quiet"
            let i = { quiet with Row = None; Item = item Open "stale" [ "x" ] }
            Expect.equal (reconcile i) (Rewrite(shownHash (Some "w") [ "x" ], [ loser Remote "x" ])) "no row, rewrite"

        testCase "rule 4: nothing changed keeps" <| fun _ ->
            Expect.equal (reconcile quiet) Keep "quiet"

        testCase "HeldAfter does not matter while the entry is present" <| fun _ ->
            Expect.equal (reconcile { quiet with HeldAfter = true }) Keep "held and present"
            let i = { quiet with HeldAfter = true; Item = item Open "any" [ "w" ] }
            Expect.equal (reconcile i) Remove "held and present, all equal"
    ]
