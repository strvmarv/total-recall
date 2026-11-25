module TotalRecall.Core.Tests.SyncPinTests

open System.Security.Cryptography
open System.Text
open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncPin

let private hex (bytes: byte[]) = System.Convert.ToHexString(bytes).ToLowerInvariant()

[<Tests>]
let shownHashTests =
    testList "SyncPin.shownHash" [
        testCase "known answer from the stated byte layout" <| fun _ ->
            let bytes =
                Array.concat
                    [ Encoding.UTF8.GetBytes "total-recall/review/pin/v1\n"
                      [| 0x01uy; 0uy; 0uy; 0uy; 1uy; byte 'x' |]
                      [| 0x00uy |]
                      [| 0x01uy; 0uy; 0uy; 0uy; 0uy |] ]
            Expect.equal (shownHash "x" None (Some "")) (hex (SHA256.HashData bytes)) "layout"

        testCase "none and empty differ in each optional field" <| fun _ ->
            Expect.notEqual (shownHash "x" None None) (shownHash "x" (Some "") None) "project"
            Expect.notEqual (shownHash "x" None None) (shownHash "x" None (Some "")) "scope"
            Expect.notEqual (shownHash "x" (Some "") None) (shownHash "x" None (Some "")) "which field"

        testCase "fields are length-framed" <| fun _ ->
            Expect.notEqual (shownHash "a" (Some "bc") None) (shownHash "ab" (Some "c") None) "framing"

        testCase "result is 64 lowercase hex characters" <| fun _ ->
            let h = shownHash "text" (Some "o/r") (Some "team")
            Expect.equal h.Length 64 "length"
            Expect.isTrue (h |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) "lowercase hex"
    ]

[<Tests>]
let arrivalTests =
    testList "SyncPin.arrival" [
        testCase "no item opens" <| fun _ -> Expect.equal (arrival None "h") OpenItem "none"

        testCase "approved and declined items open again, whatever the hash" <| fun _ ->
            Expect.equal (arrival (Some(Approved, "h")) "h") OpenItem "approved same"
            Expect.equal (arrival (Some(Approved, "g")) "h") OpenItem "approved different"
            Expect.equal (arrival (Some(Declined, "h")) "h") OpenItem "declined same"
            Expect.equal (arrival (Some(Declined, "g")) "h") OpenItem "declined different"

        testCase "open and unseen items stay with the same hash and turn unseen with another" <| fun _ ->
            Expect.equal (arrival (Some(Open, "h")) "h") LeaveItem "open same"
            Expect.equal (arrival (Some(Open, "g")) "h") UnseenItem "open different"
            Expect.equal (arrival (Some(Unseen, "h")) "h") LeaveItem "unseen same"
            Expect.equal (arrival (Some(Unseen, "g")) "h") UnseenItem "unseen different"
    ]

let private newPin state hash : PinItemView = { Kind = NewPinItem; State = state; Hash = hash }
let private pinEdit state hash : PinItemView = { Kind = PinEditItem; State = state; Hash = hash }

let private entry sticky contentHash shown : EntryView =
    { Sticky = sticky; ContentHash = contentHash; Shown = shown }

let private row hasBase pending scanned : PinRowView =
    { State = Active; HasBase = hasBase; PendingContent = pending; ScannedContentHash = scanned }

let private baseView shown sticky : BaseView = { Shown = shown; Sticky = sticky }

/// A new-pin input in which no rule fires before rule 5: entry present, unpinned, record present, base pinned,
/// the shown hash equals the item's.
let private newPinInput state : ReconcileInput =
    { Item = newPin state "h"
      Entry = Some(entry false "c" "h")
      Row = Some(row true false (Some "c"))
      Base = Some(baseView "b" true)
      RecordPresent = true }

/// A pin-edit input in which no rule fires before rule 5/6: entry pinned, base shown differs from the entry's,
/// no local edit.
let private pinEditInput state : ReconcileInput =
    { Item = pinEdit state "h"
      Entry = Some(entry true "c" "e")
      Row = Some(row true false (Some "c"))
      Base = Some(baseView "h" true)
      RecordPresent = true }

[<Tests>]
let reconcileTests =
    testList "SyncPin.reconcile" [
        // new-pin
        testCase "new-pin 1: entry absent removes" <| fun _ ->
            Expect.equal (reconcile { newPinInput Open with Entry = None }) Remove "absent"

        testCase "new-pin 1: row forgotten or left removes" <| fun _ ->
            let i = newPinInput Open
            Expect.equal (reconcile { i with Row = Some { row true false (Some "c") with State = Forgotten } }) Remove "forgotten"
            Expect.equal (reconcile { i with Row = Some { row true false (Some "c") with State = Left } }) Remove "left"

        testCase "new-pin 2: entry sticky removes" <| fun _ ->
            Expect.equal (reconcile { newPinInput Open with Entry = Some(entry true "c" "h") }) Remove "pinned here"
            Expect.equal (reconcile { newPinInput Approved with Entry = Some(entry true "c" "h") }) Remove "approved and pinned"

        testCase "new-pin 3: record absent removes" <| fun _ ->
            Expect.equal (reconcile { newPinInput Open with RecordPresent = false }) Remove "record absent"

        testCase "new-pin 3: base not sticky or missing removes" <| fun _ ->
            Expect.equal (reconcile { newPinInput Open with Base = Some(baseView "b" false) }) Remove "base unpinned"
            Expect.equal (reconcile { newPinInput Open with Base = None }) Remove "no base"

        testCase "new-pin 3 before 4: record absent wins over a changed shown hash" <| fun _ ->
            let i = { newPinInput Open with RecordPresent = false; Entry = Some(entry false "c" "other") }
            Expect.equal (reconcile i) Remove "rule 3"

        testCase "new-pin 4: open and unseen with a different shown hash turn unseen" <| fun _ ->
            let e = Some(entry false "c" "new")
            Expect.equal (reconcile { newPinInput Open with Entry = e }) (MarkUnseen "new") "open"
            Expect.equal (reconcile { newPinInput Unseen with Entry = e }) (MarkUnseen "new") "unseen"

        testCase "new-pin 5: declined and approved keep, open with the same hash keeps" <| fun _ ->
            let e = Some(entry false "c" "new")
            Expect.equal (reconcile { newPinInput Declined with Entry = e }) Keep "declined"
            Expect.equal (reconcile { newPinInput Approved with Entry = e }) Keep "approved"
            Expect.equal (reconcile (newPinInput Open)) Keep "open same hash"

        testCase "new-pin 1: no row at all removes" <| fun _ ->
            Expect.equal (reconcile { newPinInput Open with Row = None }) Remove "no row"

        // Rules 1-3 before 4: each condition holds together with "the entry's shown hash differs from the item's"
        // (live item), which alone would give MarkUnseen. A reordering that tried rule 4 earlier would fail these.
        testCase "new-pin 1 before 4: a forgotten or left row or no row removes although the shown hash changed" <| fun _ ->
            let i = { newPinInput Open with Entry = Some(entry false "c" "changed") }
            Expect.equal (reconcile { i with Row = Some { row true false (Some "c") with State = Forgotten } }) Remove "forgotten"
            Expect.equal (reconcile { i with Row = Some { row true false (Some "c") with State = Left } }) Remove "left"
            Expect.equal (reconcile { i with Row = None }) Remove "no row"
            let u = { i with Item = newPin Unseen "h" }
            Expect.equal (reconcile { u with Row = Some { row true false (Some "c") with State = Forgotten } }) Remove "unseen, forgotten"

        testCase "new-pin 2 before 4: a sticky entry removes although the shown hash changed" <| fun _ ->
            let i = { newPinInput Open with Entry = Some(entry true "c" "changed") }
            Expect.equal (reconcile i) Remove "open"
            Expect.equal (reconcile { i with Item = newPin Unseen "h" }) Remove "unseen"

        testCase "new-pin 3 before 4: a base that is not pinned or missing removes although the shown hash changed" <| fun _ ->
            let i = { newPinInput Open with Entry = Some(entry false "c" "changed") }
            Expect.equal (reconcile { i with Base = Some(baseView "b" false) }) Remove "base not sticky"
            Expect.equal (reconcile { i with Base = None }) Remove "no base"
            Expect.equal (reconcile { i with RecordPresent = false; Base = Some(baseView "b" false) }) Remove "both"
            Expect.equal (reconcile { i with Item = newPin Unseen "h"; Base = Some(baseView "b" false) }) Remove "unseen"

        // pin-edit
        testCase "pin-edit 1: entry absent removes" <| fun _ ->
            Expect.equal (reconcile { pinEditInput Open with Entry = None }) Remove "absent"

        testCase "pin-edit 1: row forgotten, left, with no base or absent removes" <| fun _ ->
            let i = pinEditInput Open
            Expect.equal (reconcile { i with Row = Some { row true false (Some "c") with State = Forgotten } }) Remove "forgotten"
            Expect.equal (reconcile { i with Row = Some { row true false (Some "c") with State = Left } }) Remove "left"
            Expect.equal (reconcile { i with Row = Some(row false false (Some "c")) }) Remove "row without base"
            Expect.equal (reconcile { i with Row = None }) Remove "no row"

        testCase "pin-edit 1: a row with a base but no base view counts as no base" <| fun _ ->
            let i = { pinEditInput Open with Base = None }
            Expect.equal (reconcile i) Remove "no base view counts as no base"

        testCase "pin-edit 2: entry shown equals base shown removes, for approved and with a pending stamp" <| fun _ ->
            let e = Some(entry true "c" "h")
            Expect.equal (reconcile { pinEditInput Open with Entry = e }) Remove "open"
            Expect.equal (reconcile { pinEditInput Approved with Entry = e }) Remove "approved"
            Expect.equal (reconcile { pinEditInput Open with Entry = e; Row = Some(row true true (Some "c")) }) Remove "pending stamp"

        // Rules 2-4 before 5: each condition holds together with "the base's shown hash differs from the item's"
        // (live item, entry shown "e", base shown "newer", item hash "h"), which alone would give MarkUnseen.
        testCase "pin-edit 2 before 4: an unpinned entry whose shown hash equals the base's removes without applying the base" <| fun _ ->
            let i = { pinEditInput Open with Entry = Some(entry false "c" "h") }
            Expect.equal (reconcile i) Remove "unpinned, shown = base shown"
            Expect.equal (reconcile { i with Item = pinEdit Unseen "h"; Base = Some(baseView "h" false) }) Remove "unseen, base not sticky"

        testCase "pin-edit 2 before 4: with a pending stamp and an unpinned entry equal to the base it still removes" <| fun _ ->
            let i = { pinEditInput Open with Entry = Some(entry false "c" "h"); Row = Some(row true true (Some "other")) }
            Expect.equal (reconcile i) Remove "rules 2, 3 and 4 all apply"

        testCase "pin-edit 2 before 5: shown equal to the base's removes although the base differs from the item's" <| fun _ ->
            let i = { pinEditInput Open with Entry = Some(entry true "c" "newer"); Base = Some(baseView "newer" true) }
            Expect.equal (reconcile i) Remove "open"
            Expect.equal (reconcile { i with Item = pinEdit Unseen "h" }) Remove "unseen"

        testCase "pin-edit 3 before 5: a local edit removes although the base differs from the item's" <| fun _ ->
            let b = Some(baseView "newer" true)
            Expect.equal (reconcile { pinEditInput Open with Base = b; Row = Some(row true true (Some "c")) }) Remove "pending"
            Expect.equal (reconcile { pinEditInput Open with Base = b; Row = Some(row true false (Some "other")) }) Remove "scanned differs"
            Expect.equal (reconcile { pinEditInput Unseen with Base = b; Row = Some(row true true (Some "other")) }) Remove "unseen, both"

        testCase "pin-edit 4 before 5: an unpinned entry applies the base although the base differs from the item's" <| fun _ ->
            let i = { pinEditInput Open with Entry = Some(entry false "c" "e"); Base = Some(baseView "newer" true) }
            Expect.equal (reconcile i) ApplyBaseThenRemove "open"
            Expect.equal (reconcile { i with Item = pinEdit Unseen "h" }) ApplyBaseThenRemove "unseen"

        testCase "pin-edit 3: a pending content stamp removes" <| fun _ ->
            Expect.equal (reconcile { pinEditInput Open with Row = Some(row true true (Some "c")) }) Remove "pending"

        testCase "pin-edit 3: a scanned content hash that differs removes" <| fun _ ->
            Expect.equal (reconcile { pinEditInput Open with Row = Some(row true false (Some "other")) }) Remove "differs"
            Expect.equal (reconcile { pinEditInput Open with Row = Some(row true false None) }) Remove "never scanned"

        testCase "pin-edit 3 before 4: an edit and an unpin at once, the edit stands" <| fun _ ->
            let i =
                { pinEditInput Open with
                    Entry = Some(entry false "c" "e")
                    Row = Some(row true true (Some "c")) }
            Expect.equal (reconcile i) Remove "rule 3, no base applied"

        testCase "pin-edit 4: an unpinned entry applies the base then removes" <| fun _ ->
            Expect.equal (reconcile { pinEditInput Open with Entry = Some(entry false "c" "e") }) ApplyBaseThenRemove "unpinned"

        testCase "pin-edit 5: a base shown hash that differs from the item's turns open and unseen items unseen" <| fun _ ->
            let b = Some(baseView "newer" true)
            Expect.equal (reconcile { pinEditInput Open with Base = b }) (MarkUnseen "newer") "open"
            Expect.equal (reconcile { pinEditInput Unseen with Base = b }) (MarkUnseen "newer") "unseen"

        testCase "pin-edit 6: declined, approved and an absent record with a pinned entry keep" <| fun _ ->
            let b = Some(baseView "newer" true)
            Expect.equal (reconcile { pinEditInput Declined with Base = b }) Keep "declined"
            Expect.equal (reconcile { pinEditInput Approved with Base = b }) Keep "approved"
            Expect.equal (reconcile { pinEditInput Open with RecordPresent = false }) Keep "record absent, same hash"
    ]

[<Tests>]
let namesTests =
    testList "SyncPin.names" [
        testCase "kind and state names round-trip" <| fun _ ->
            Expect.equal (kindName NewPinItem) "new-pin" "new-pin"
            Expect.equal (kindName PinEditItem) "pin-edit" "pin-edit"
            Expect.equal (tryKind "new-pin") (Some NewPinItem) "parse new-pin"
            Expect.equal (tryKind "pin-edit") (Some PinEditItem) "parse pin-edit"
            Expect.equal (tryKind "x") None "unknown kind"
            for s in [ Open; Unseen; Approved; Declined ] do
                Expect.equal (tryState (stateName s)) (Some s) "round trip"
            Expect.equal (stateName Open) "open" "open"
            Expect.equal (stateName Declined) "declined" "declined"
            Expect.equal (tryState "x") None "unknown state"

        testCase "entryView hashes the content group and the shown fields" <| fun _ ->
            let l : LocalEntry =
                { Content =
                    { Text = "t"
                      Project = Some "p"
                      Scope = None
                      EntryType = Correction
                      Source = None
                      CreatedAtMs = 1L }
                  Tags = []
                  Tier = Hot
                  Sticky = true }
            let v = entryView l
            Expect.isTrue v.Sticky "sticky"
            Expect.equal v.ContentHash (SyncScan.contentHash l.Content) "content hash"
            Expect.equal v.Shown (shownHash "t" (Some "p") None) "shown"
    ]
