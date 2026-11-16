module TotalRecall.Core.Tests.SyncScanTests

open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncScan

let private m1 = (MachineId.tryCreate "4f2a9c1e").Value

let private st (n: int) : Stamp = { TimeMs = 1_700_000_000_000L + int64 n * 1000L; Counter = 0; Machine = m1 }

let private fields : ContentFields =
    { Text = "Use bun, not npm."
      Project = None
      Scope = None
      EntryType = Correction
      Source = None
      CreatedAtMs = 1_700_000_000_000L }

let private localEntry : LocalEntry = { LocalEntry.Content = fields; Tags = [ "tooling" ]; Tier = Warm; Sticky = false }

let private noBase : BaseFacts =
    { Tier = None; ContentStamp = None; StickyStamp = None; TierStamp = None }

/// A row that matches `localEntry` exactly: nothing to do.
let private settled : RowView =
    { State = Active
      Base = None
      HasPushingStamps = false
      ContentHash = Some(contentHash localEntry.Content)
      TagsHash = Some(tagsHash localEntry.Tags)
      LocalSticky = Some false
      LocalTier = Some Warm
      PendingContent = None
      PendingSticky = None
      PendingTier = None }

let private decideRow (r: RowView) (l: LocalEntry option) (knowledge: bool) = decide (Some r) l knowledge

let private changes (a: ScanAction) : RowChanges =
    match a with
    | UpdateRow c -> c
    | other -> failtestf "expected UpdateRow, got %A" other

[<Tests>]
let hashTests =
    testList "SyncScan hashes (decision 5)" [
        testCase "the content hash golden vectors (computed independently with Python hashlib)" <| fun _ ->
            let a = { fields with Text = "Use bun, not npm." }
            Expect.equal (contentHash a) "b00db27deb3dd59a9ce9fbc157ae4fa7b7ee29799768e96ef205fa4da3f3d555" "A"
            let b =
                { Text = "Use bun, not npm."
                  Project = Some "owner/repo"
                  Scope = Some "owner/repo"
                  EntryType = Preference
                  Source = Some ClaudeCode
                  CreatedAtMs = 1_700_000_000_000L }
            Expect.equal (contentHash b) "8784f24f7c15de98e269095d90e4aac6cfe913b936a8171806739ebfb9da1074" "B"
            let c = { Text = ""; Project = None; Scope = None; EntryType = Imported; Source = None; CreatedAtMs = 0L }
            Expect.equal (contentHash c) "31359dca578b1c21fc1a393c63c229a88e010f17c363b1668e0177dd66946fc3" "C"
            Expect.equal
                (contentHash { c with Project = Some "" })
                "ac9d1ec71fa87889d01c78a210a142ebe0b1211244839d5de10520aa5e6d42e7"
                "an absent project hashes differently from Some \"\""

        testCase "the tags hash golden vectors" <| fun _ ->
            Expect.equal (tagsHash []) "fa1fe07c4559a10134239fa0a1ddfd44cb9ebe62e082284aa674591f50b06625" "empty"
            Expect.equal (tagsHash [ "a"; "b" ]) "53889f8670d446d8752914136774e43e12f3811fe286f7800c82e1b38ef9e689" "a b"
            Expect.equal (tagsHash [ "b"; "a"; "a" ]) (tagsHash [ "a"; "b" ]) "tag order and duplicates do not change the tags hash"

        testCase "each field of the content group changes the content hash" <| fun _ ->
            let h = contentHash fields
            let differs (c: ContentFields) = Expect.notEqual (contentHash c) h (sprintf "%A" c)
            differs { fields with Text = "other" }
            differs { fields with Project = Some "p" }
            differs { fields with Scope = Some "s" }
            differs { fields with EntryType = Decision }
            differs { fields with Source = Some Cursor }
            differs { fields with CreatedAtMs = 1L }

        // Fields that never sync (summary, metadata, usage counters, decay, updated_at) cannot change a hash: the two
        // functions take only the content fields and the tag list, which the type signatures enforce.
    ]

[<Tests>]
let floorTests =
    testList "SyncScan floorStamp and ClockWarning" [
        testCase "floorStamp takes the greater of two stamps, or the one there is" <| fun _ ->
            Expect.equal (floorStamp (Some(st 1)) (Some(st 2))) (Some(st 2)) "second"
            Expect.equal (floorStamp (Some(st 3)) (Some(st 2))) (Some(st 3)) "first"
            Expect.equal (floorStamp None (Some(st 2))) (Some(st 2)) "only second"
            Expect.equal (floorStamp (Some(st 2)) None) (Some(st 2)) "only first"
            Expect.equal (floorStamp None None) None "none"

        testCase "addKinds with nothing to add never writes (a clean scan leaves the warning key absent)" <| fun _ ->
            Expect.equal (ClockWarning.addKinds None []) None "none stored"
            Expect.equal (ClockWarning.addKinds (Some "clock: far-future") []) None "stored"

        testCase "addKinds writes the sorted set, and only when it grows" <| fun _ ->
            Expect.equal
                (ClockWarning.addKinds None [ ClockWarning.ClockReset; ClockWarning.Exhausted ])
                (Some "clock: clock-reset,exhausted")
                "new"
            Expect.equal (ClockWarning.addKinds (Some "clock: clock-reset,exhausted") [ ClockWarning.Exhausted ]) None "already stored"
            Expect.equal
                (ClockWarning.addKinds (Some "clock: exhausted") [ ClockWarning.FarFuture; ClockWarning.Exhausted ])
                (Some "clock: exhausted,far-future")
                "union"
            Expect.equal
                (ClockWarning.addKinds (Some "something else") [ ClockWarning.ClockUnreadable ])
                (Some "clock: clock-unreadable")
                "an unreadable stored text is replaced"
            Expect.equal
                (ClockWarning.kindName ClockWarning.ClockUnreadable, ClockWarning.kindName ClockWarning.FarFuture)
                ("clock-unreadable", "far-future")
                "names"
    ]

[<Tests>]
let decideTests =
    testList "SyncScan decide (decisions 6 to 10)" [
        testCase "decision 6: no row and a local entry is a new row" <| fun _ ->
            Expect.equal (decide None (Some localEntry) false) NewRow "new"

        testCase "no row and no entry is nothing (a knowledge entry without a row is never ours)" <| fun _ ->
            Expect.equal (decide None None false) NoChange "none"
            Expect.equal (decide None None true) NoChange "knowledge"

        testCase "an active row that matches the entry is no change" <| fun _ ->
            Expect.equal (decideRow settled (Some localEntry) false) NoChange "settled"

        testCase "decision 7: a changed content hash issues a content stamp above the greater of the base and pending stamps" <| fun _ ->
            let r =
                { settled with
                    ContentHash = Some "old"
                    Base = Some { noBase with ContentStamp = Some(st 5) }
                    PendingContent = Some(st 7) }
            let c = changes (decideRow r (Some localEntry) false)
            Expect.equal c.Content (IssueStamp(Some(st 7))) "floor is the greater"
            Expect.equal c.Tags Unchanged "tags"
            Expect.equal c.Sticky Unchanged "sticky"
            Expect.equal c.Tier Unchanged "tier"
            let r2 = { r with PendingContent = Some(st 2) }
            Expect.equal (changes (decideRow r2 (Some localEntry) false)).Content (IssueStamp(Some(st 5))) "base is the greater"
            let r3 = { r with Base = None; PendingContent = None }
            Expect.equal (changes (decideRow r3 (Some localEntry) false)).Content (IssueStamp None) "no floor"

        testCase "a changed tags hash only stores the value" <| fun _ ->
            let c = changes (decideRow { settled with TagsHash = Some "old" } (Some localEntry) false)
            Expect.equal c.Tags StoreValue "tags"
            Expect.equal c.Content Unchanged "content"
            Expect.equal c.Sticky Unchanged "sticky"
            Expect.equal c.Tier Unchanged "tier"

        testCase "a sticky flag that differs from local_sticky issues a sticky stamp, floored by the sticky stamps" <| fun _ ->
            let r =
                { settled with
                    LocalSticky = Some true
                    Base = Some { noBase with StickyStamp = Some(st 4); ContentStamp = Some(st 9) }
                    PendingSticky = Some(st 3) }
            let c = changes (decideRow r (Some localEntry) false)
            Expect.equal c.Sticky (IssueStamp(Some(st 4))) "the sticky floor comes from the sticky stamps"
            let r2 = { r with PendingSticky = Some(st 6) }
            Expect.equal (changes (decideRow r2 (Some localEntry) false)).Sticky (IssueStamp(Some(st 6))) "the pending stamp is the greater"

        testCase "a tier warmer than local_tier with no base issues a tier stamp, floored by the tier stamps" <| fun _ ->
            let hot = { localEntry with Tier = Hot }
            let r = { settled with PendingTier = Some(st 6); Base = None }
            Expect.equal (changes (decideRow r (Some hot) false)).Tier (IssueStamp(Some(st 6))) "pending is the floor"

        testCase "a tier warmer than local_tier and warmer than the base issues a stamp from the base's tier stamp" <| fun _ ->
            let r =
                { settled with
                    LocalTier = Some Cold
                    Base = Some { noBase with Tier = Some Cold; TierStamp = Some(st 8) } }
            Expect.equal (changes (decideRow r (Some localEntry) false)).Tier (IssueStamp(Some(st 8))) "warm is warmer than cold"

        testCase "a tier colder than local_tier only stores it (demotion stays local)" <| fun _ ->
            let cold = { localEntry with Tier = Cold }
            Expect.equal (changes (decideRow settled (Some cold) false)).Tier StoreValue "colder"

        testCase "decision 7, clearing test first: a tier that rose above local_tier but not above the base's stores the value and issues nothing" <| fun _ ->
            let r =
                { settled with
                    LocalTier = Some Cold
                    Base = Some { noBase with Tier = Some Warm } }
            Expect.equal (changes (decideRow r (Some localEntry) false)).Tier StoreValue "not warmer than the base"

        testCase "an unchanged tier that is not warmer than the base is no change" <| fun _ ->
            let r = { settled with Base = Some { noBase with Tier = Some Warm } }
            Expect.equal (decideRow r (Some localEntry) false) NoChange "settled with a base"

        testCase "a pending tier stamp is dropped when the tier is not warmer than the base's (undone promotion), storing the tier" <| fun _ ->
            let cold = { localEntry with Tier = Cold }
            let r =
                { settled with
                    LocalTier = Some Hot
                    PendingTier = Some(st 4)
                    Base = Some { noBase with Tier = Some Warm } }
            Expect.equal (changes (decideRow r (Some cold) false)).Tier DropPendingStamp "colder"
            let same =
                { settled with
                    PendingTier = Some(st 4)
                    Base = Some { noBase with Tier = Some Warm } }
            Expect.equal (changes (decideRow same (Some localEntry) false)).Tier DropPendingStamp "unchanged and not warmer than the base"

        testCase "an unreadable base (tier unknown) never clears a pending tier stamp" <| fun _ ->
            let r = { settled with PendingTier = Some(st 4); Base = Some noBase }
            Expect.equal (decideRow r (Some localEntry) false) NoChange "no clearing"

        testCase "decision 7: a NULL hash, flag or tier is nothing seen: the value is stored and no stamp is issued" <| fun _ ->
            let c = changes (decideRow { settled with ContentHash = None; TagsHash = None; LocalSticky = None; LocalTier = None } (Some localEntry) false)
            Expect.equal c.Content StoreValue "content"
            Expect.equal c.Tags StoreValue "tags"
            Expect.equal c.Sticky StoreValue "sticky"
            Expect.equal c.Tier StoreValue "tier"

        testCase "decision 9: a forgotten or left row whose entry exists again is reactivated" <| fun _ ->
            Expect.equal (decideRow { settled with State = Forgotten } (Some localEntry) false) Reactivate "forgotten"
            Expect.equal (decideRow { settled with State = Left } (Some localEntry) false) Reactivate "left"

        testCase "decision 8: an active row with no memory but a knowledge entry moves to left" <| fun _ ->
            Expect.equal (decideRow settled None true) MoveToLeft "retype"

        testCase "decision 8: an active row with no entry anywhere is reported, with the two flags" <| fun _ ->
            for hasBase in [ false; true ] do
                for pushing in [ false; true ] do
                    let r = { settled with Base = (if hasBase then Some noBase else None); HasPushingStamps = pushing }
                    Expect.equal (decideRow r None false) (ReportDeletion(hasBase, pushing)) (sprintf "%b %b" hasBase pushing)

        testCase "decision 9: a forgotten or left row with no entry stays as it is" <| fun _ ->
            for st' in [ Forgotten; Left ] do
                Expect.equal (decideRow { settled with State = st' } None false) NoChange "no entry"
                Expect.equal (decideRow { settled with State = st' } None true) NoChange "knowledge"
    ]

// ---- totality ----

let private genStamp : Gen<Stamp> = Gen.elements [ for n in 0 .. 4 -> st n ]
let private genOpt (g: Gen<'a>) : Gen<'a option> = Gen.frequency [ 1, Gen.constant None; 3, g |> Gen.map Some ]
let private genTier : Gen<Tier> = Gen.elements [ Hot; Warm; Cold ]
let private genBool : Gen<bool> = Gen.elements [ true; false ]

let private genEntry : Gen<LocalEntry> =
    gen {
        let! text = Gen.elements [ "a"; "b" ]
        let! tags = Gen.elements [ []; [ "x" ]; [ "x"; "y" ] ]
        let! tier = genTier
        let! sticky = genBool
        return { LocalEntry.Content = { fields with Text = text }; Tags = tags; Tier = tier; Sticky = sticky }
    }

let private genBase : Gen<BaseFacts> =
    gen {
        let! tier = genOpt genTier
        let! c = genOpt genStamp
        let! s = genOpt genStamp
        let! t = genOpt genStamp
        return { Tier = tier; ContentStamp = c; StickyStamp = s; TierStamp = t }
    }

let private genRow : Gen<RowView> =
    gen {
        let! state = Gen.elements [ Active; Forgotten; Left ]
        let! b = genOpt genBase
        let! pushing = genBool
        let! ch = genOpt (Gen.elements [ contentHash fields; "old" ])
        let! th = genOpt (Gen.elements [ tagsHash []; "old" ])
        let! sticky = genOpt genBool
        let! tier = genOpt genTier
        let! pc = genOpt genStamp
        let! ps = genOpt genStamp
        let! pt = genOpt genStamp
        return
            { State = state
              Base = b
              HasPushingStamps = pushing
              ContentHash = ch
              TagsHash = th
              LocalSticky = sticky
              LocalTier = tier
              PendingContent = pc
              PendingSticky = ps
              PendingTier = pt }
    }

[<Tests>]
let totalityTests =
    testList "SyncScan decide is total" [
        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "decide never throws over rows, local entries and the knowledge flag"
            (Prop.forAll (Arb.fromGen (Gen.map3 (fun r l k -> r, l, k) (genOpt genRow) (genOpt genEntry) genBool)) (fun (r, l, k) ->
                decide r l k |> ignore
                true))
    ]
