module TotalRecall.Core.Tests.SyncApplyTests

open Expecto
open FsCheck
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncApply

let private m1 = (MachineId.tryCreate "4f2a9c1e").Value

let private st (n: int) : Stamp = { TimeMs = 1_700_000_000_000L + int64 n * 1000L; Counter = 0; Machine = m1 }

let private noStamps : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

let private record (text: string) (tier: Tier) (sticky: bool) : SyncRecord =
    { Id = "3f9c2a7e"
      Content = text
      EntryType = Decision
      Tags = []
      Source = None
      CreatedAtMs = 1_700_000_000_000L
      Project = None
      Scope = None
      Placement = { Placement.Tier = tier; TierStamp = st 1; Sticky = sticky; StickyStamp = st 1 }
      ContentStamp = st 1
      Origin = None
      WrittenBy = None }

let private keepAll : BaseUpdate = { BaseUpdate.Content = KeepBase; Tags = KeepBase; Sticky = KeepBase; Tier = KeepBase }

/// A decision that changes nothing.
let private plain : MergeDecision =
    { Entry = Ignore
      Base = keepAll
      RowState = None
      Reviews = []
      Notes = []
      CloseHeldDelete = false
      FreshContentStamp = false }

let private tagsOutcome : TagsOutcome = { Result = []; ApplyLocal = false; PushNeeded = false }
let private noRepush : GroupFlags = { GroupFlags.Content = false; Sticky = false; Tier = false }

let private merged : Merged =
    { Merged.Content = (LeaveContent, KeepPending)
      Tags = tagsOutcome
      Sticky = (LeaveSticky, KeepPending)
      Tier = (None, KeepPending)
      Repush = noRepush }

let private mergeWith (f: Merged -> Merged) : MergeDecision = { plain with Entry = MergeGroups(f merged) }

let private fields (text: string) : ContentFields =
    { Text = text; Project = None; Scope = None; EntryType = Decision; Source = None; CreatedAtMs = 1_700_000_000_000L }

let private local (text: string) (tier: Tier) (sticky: bool) : LocalEntry =
    { LocalEntry.Content = fields text; Tags = []; Tier = tier; Sticky = sticky }

let private isNone (r: PostponeReason option) = r.IsNone

// ---- postponement ----

[<Tests>]
let postponementTests =
    testList "SyncApply.postponement (decision 8; milestone 1b-ii D19, staged)" [
        testCase "PushDeletion and a local-deletion hold are carried out, also when they close an item" <| fun () ->
            for e in [ PushDeletion; HoldDeletion LocalDeletion ] do
                Expect.isNone (postponement { plain with Entry = e }) (sprintf "%A" e)
                Expect.isNone (postponement { plain with Entry = e; CloseHeldDelete = true }) (sprintf "%A closing" e)

        testCase "DeleteEntry, a remote-deletion hold, a note, FreshContentStamp, CloseHeldDelete and a forgotten row state are carried out" <| fun () ->
            Expect.isNone (postponement { plain with Entry = DeleteEntry; RowState = Some Forgotten; Notes = [ PendingChangesDropped ] }) "delete"
            Expect.isNone (postponement { plain with Entry = HoldDeletion RemoteDeletion }) "remote hold"
            Expect.isNone (postponement { plain with Notes = [ EditBeatsDeletion ] }) "note"
            Expect.isNone (postponement { plain with Notes = [ PendingChangesDropped ] }) "note 2"
            Expect.isNone (postponement { plain with Entry = LeaveEntry; FreshContentStamp = true }) "fresh stamp"
            Expect.isNone (postponement { plain with CloseHeldDelete = true }) "close"
            Expect.isNone (postponement { plain with RowState = Some Forgotten }) "forgotten"
            Expect.isNone (postponement { plain with Entry = InsertFromRecord; Notes = [ EditBeatsDeletion ]; CloseHeldDelete = true }) "a reinsert that closes"

        testCase "any review is review, and a record that pins (InsertFromRecord with NewPin) is review" <| fun () ->
            Expect.equal (postponement { plain with Reviews = [ NewPin ] }) (Some Review) "new pin"
            Expect.equal (postponement { plain with Reviews = [ PinEdit ] }) (Some Review) "pin edit"
            let conflict = Conflict { Side = Side.Local; Text = "x"; Stamp = None }
            Expect.equal (postponement { plain with Reviews = [ conflict ] }) (Some Review) "conflict"
            Expect.equal (postponement { plain with Entry = InsertFromRecord; Reviews = [ NewPin ] }) (Some Review) "insert of a pin"

        testCase "a Repush flag is repush, one test per group" <| fun () ->
            Expect.equal (postponement (mergeWith (fun m -> { m with Repush = { noRepush with Content = true } }))) (Some Repush) "content"
            Expect.equal (postponement (mergeWith (fun m -> { m with Repush = { noRepush with Sticky = true } }))) (Some Repush) "sticky"
            Expect.equal (postponement (mergeWith (fun m -> { m with Repush = { noRepush with Tier = true } }))) (Some Repush) "tier"

        testCase "precedence is review over repush" <| fun () ->
            let repush = mergeWith (fun m -> { m with Repush = { noRepush with Tier = true } })
            Expect.equal (postponement { repush with Reviews = [ NewPin ] }) (Some Review) "review over repush"
            Expect.equal (postponement { plain with Entry = PushDeletion; Reviews = [ NewPin ] }) (Some Review) "a review on any outcome"
            Expect.equal (postponement { repush with Reviews = [ NewPin ]; Notes = [ EditBeatsDeletion ] }) (Some Review) "a note does not postpone"

        testCase "Defer, Ignore, DropRow, LeaveEntry, an insert and a plain merge are carried out" <| fun () ->
            for e in [ Defer; Ignore; DropRow; LeaveEntry; InsertFromRecord ] do
                Expect.isTrue (isNone (postponement { plain with Entry = e })) (sprintf "%A" e)
            let m =
                mergeWith (fun m ->
                    { m with
                        Content = (TakeTheirs, ClearPending)
                        Tags = { tagsOutcome with ApplyLocal = true; Result = [ "x" ] }
                        Sticky = (UnpinLocally, ClearPending)
                        Tier = (Some Hot, ClearPending) })
            Expect.isTrue (isNone (postponement m)) "content, tags, unpin and promotion"
    ]

[<Tests>]
let reasonNameTests =
    testList "SyncApply.reasonName" [
        testCase "the words are review and repush" <| fun () ->
            Expect.equal [ reasonName Review; reasonName Repush ] [ "review"; "repush" ] "words"
    ]

// ---- pushing_stamps ----

let private text (s: Stamp) = Stamp.format s

[<Tests>]
let pushingStampsTests =
    testList "SyncApply.PushingStamps (decision 11)" [
        testCase "null, empty, whitespace, {} and [] are none" <| fun () ->
            for t in [ null; ""; "   "; "{}"; "[]"; " { } " ] do
                Expect.equal (PushingStamps.decode t) noStamps (sprintf "%A" t)

        testCase "text that is not JSON, a non-object root, garbage values, a duplicate key and a non-string value read as none for that group" <| fun () ->
            Expect.equal (PushingStamps.decode "not json") noStamps "not json"
            Expect.equal (PushingStamps.decode "[1,2]") noStamps "array"
            Expect.equal (PushingStamps.decode "\"x\"") noStamps "string root"
            Expect.equal (PushingStamps.decode "{\"content\":\"garbage\"}") noStamps "garbage"
            Expect.equal (PushingStamps.decode "{\"content\":5}") noStamps "number"
            let dup = sprintf "{\"content\":\"%s\",\"content\":\"%s\",\"tier\":\"%s\"}" (text (st 1)) (text (st 2)) (text (st 3))
            Expect.equal (PushingStamps.decode dup) { noStamps with Tier = Some(st 3) } "a duplicate key is none, the rest is read"
            Expect.equal (PushingStamps.decode "{\"content\":\"\ud800\"}") noStamps "a lone surrogate"

        testCase "an unknown key is ignored" <| fun () ->
            let t = sprintf "{\"content\":\"%s\",\"other\":1}" (text (st 1))
            Expect.equal (PushingStamps.decode t) { noStamps with Content = Some(st 1) } "unknown key"

        testCase "encode of none is None, and one, two and three stamps round trip" <| fun () ->
            Expect.isNone (PushingStamps.encode noStamps) "none"
            for s in [ { noStamps with Sticky = Some(st 2) }
                       { noStamps with Content = Some(st 1); Tier = Some(st 3) }
                       { GroupStamps.Content = Some(st 1); Sticky = Some(st 2); Tier = Some(st 3) } ] do
                let encoded = (PushingStamps.encode s).Value
                Expect.equal (PushingStamps.decode encoded) s encoded

        testCase "keys come in the order content, sticky, tier with no spaces" <| fun () ->
            let all = { GroupStamps.Content = Some(st 1); Sticky = Some(st 2); Tier = Some(st 3) }
            Expect.equal
                (PushingStamps.encode all)
                (Some(sprintf "{\"content\":\"%s\",\"sticky\":\"%s\",\"tier\":\"%s\"}" (text (st 1)) (text (st 2)) (text (st 3))))
                "canonical text"
    ]

// ---- rejection key ----

[<Tests>]
let rejectionKeyTests =
    testList "SyncApply.rejectionKey (decision 15)" [
        testCase "it is sanitizeText of the path and cuts at 64 units" <| fun () ->
            let long = "memories/ab/" + String.replicate 80 "x" + ".json"
            Expect.equal (rejectionKey long) (SyncValidation.sanitizeText long) "same function"
            Expect.equal (rejectionKey long).Length 64 "cut at 64"
            Expect.equal (rejectionKey "a\nb") "a?b" "control characters are replaced"

        testCase "two paths that share their first 64 units share a key" <| fun () ->
            let a = String.replicate 64 "x" + "1"
            let b = String.replicate 64 "x" + "2"
            Expect.equal (rejectionKey a) (rejectionKey b) "same key"
    ]

// ---- hot limit ----

let private promote (t: Tier) = mergeWith (fun m -> { m with Tier = (Some t, ClearPending) })
let private takeText = mergeWith (fun m -> { m with Content = (TakeTheirs, ClearPending) })
let private long = String.replicate 11 "x"

[<Tests>]
let hotLimitTests =
    testList "SyncApply.demotesToWarm (decision 9, the hot limit)" [
        testCase "a hot insert over the limit demotes, at the limit does not, and a warm insert never does" <| fun () ->
            let ins = { plain with Entry = InsertFromRecord }
            Expect.isTrue (demotesToWarm 10 ins (record long Hot false) None) "over"
            Expect.isFalse (demotesToWarm 10 ins (record "xxxxxxxxxx" Hot false) None) "at the limit"
            Expect.isFalse (demotesToWarm 10 ins (record long Warm false) None) "warm"

        testCase "a promotion into hot over the limit demotes" <| fun () ->
            Expect.isTrue (demotesToWarm 10 (promote Hot) (record "short" Hot false) (Some(local long Warm false))) "promotion of a long entry"
            Expect.isFalse (demotesToWarm 10 (promote Hot) (record "short" Hot false) (Some(local "short" Warm false))) "short"
            Expect.isFalse (demotesToWarm 10 (promote Warm) (record "short" Warm false) (Some(local long Cold false))) "into warm"

        testCase "a text change on a hot entry over the limit demotes; on a warm entry that stays warm it does not" <| fun () ->
            Expect.isTrue (demotesToWarm 10 takeText (record long Hot false) (Some(local "short" Hot false))) "hot"
            Expect.isFalse (demotesToWarm 10 takeText (record long Warm false) (Some(local "short" Warm false))) "warm"

        testCase "a project-only TakeTheirs on a long hot entry does not demote" <| fun () ->
            Expect.isFalse (demotesToWarm 10 takeText (record long Hot false) (Some(local long Hot false))) "same text"

        testCase "a tags-only change on a hot long entry does not demote" <| fun () ->
            let tags = mergeWith (fun m -> { m with Tags = { tagsOutcome with ApplyLocal = true; Result = [ "x" ] } })
            Expect.isFalse (demotesToWarm 10 tags (record long Hot false) (Some(local long Hot false))) "tags"

        testCase "a result that stays sticky is exempt, and UnpinLocally with a text change over the limit demotes" <| fun () ->
            Expect.isFalse (demotesToWarm 10 takeText (record long Hot true) (Some(local "short" Hot true))) "sticky"
            let unpinAndText = mergeWith (fun m -> { m with Content = (TakeTheirs, ClearPending); Sticky = (UnpinLocally, ClearPending) })
            Expect.isTrue (demotesToWarm 10 unpinAndText (record long Hot false) (Some(local "short" Hot true))) "unpin"
            let unpinOnly = mergeWith (fun m -> { m with Sticky = (UnpinLocally, ClearPending) })
            Expect.isFalse (demotesToWarm 10 unpinOnly (record long Hot false) (Some(local long Hot true))) "unpin alone changes neither text nor tier"

        testCase "promotion and text change together demote" <| fun () ->
            let both = mergeWith (fun m -> { m with Content = (TakeTheirs, ClearPending); Tier = (Some Hot, ClearPending) })
            Expect.isTrue (demotesToWarm 10 both (record long Hot false) (Some(local "short" Warm false))) "both"

        testCase "a limit of 0 or less is no limit" <| fun () ->
            let ins = { plain with Entry = InsertFromRecord }
            Expect.isFalse (demotesToWarm 0 ins (record long Hot false) None) "zero"
            Expect.isFalse (demotesToWarm -1 ins (record long Hot false) None) "negative"

        testCase "other outcomes never demote" <| fun () ->
            for e in [ LeaveEntry; Ignore; DropRow; Defer ] do
                Expect.isFalse (demotesToWarm 10 { plain with Entry = e } (record long Hot false) (Some(local long Hot false))) (sprintf "%A" e)
    ]

// ---- visit set ----

let private emptyInput : VisitInput =
    { Unproven = false
      Accepted = Set.empty
      Changed = Set.empty
      Present = Set.empty
      NoBaseRows = Set.empty
      ApplyFailed = Set.empty
      Revisit = Set.empty
      ActiveWithBase = Set.empty
      LocalDeletions = Set.empty }

let private ids (l: string list) = Set.ofList l

[<Tests>]
let visitSetTests =
    testList "SyncApply.visitSet (decision 5)" [
        testCase "nothing is nothing" <| fun () -> Expect.equal (visitSet emptyInput) [] "empty"

        testCase "proven: only the changed ids; unproven: every accepted record" <| fun () ->
            let v = { emptyInput with Accepted = ids [ "a"; "b"; "c" ]; Changed = ids [ "b" ] }
            Expect.equal (visitSet v) [ "b" ] "proven"
            Expect.equal (visitSet { v with Unproven = true }) [ "a"; "b"; "c" ] "unproven"

        testCase "a row with no base counts only when its id has an accepted record" <| fun () ->
            let v = { emptyInput with NoBaseRows = ids [ "a"; "z" ]; Accepted = ids [ "a" ] }
            Expect.equal (visitSet v) [ "a" ] "a only"

        testCase "open apply-failed ids and revisited rejections are always in" <| fun () ->
            Expect.equal (visitSet { emptyInput with ApplyFailed = ids [ "q"; "p" ]; Revisit = ids [ "r" ] }) [ "p"; "q"; "r" ] "both bullets"

        testCase "an active row with a base is added only when its id is absent from the listing, so a rejected id is never added" <| fun () ->
            let v = { emptyInput with ActiveWithBase = ids [ "gone"; "rejected"; "there" ]; Present = ids [ "rejected"; "there" ] }
            Expect.equal (visitSet v) [ "gone" ] "gone only"

        testCase "local deletions are always in, whatever R holds" <| fun () ->
            let v = { emptyInput with LocalDeletions = ids [ "d" ]; Present = ids [ "d" ]; Accepted = ids [ "d" ] }
            Expect.equal (visitSet v) [ "d" ] "d"

        testCase "the result is ordinal-sorted and distinct" <| fun () ->
            let v =
                { emptyInput with
                    Unproven = true
                    Accepted = ids [ "b"; "B"; "a" ]
                    ApplyFailed = ids [ "a" ]
                    LocalDeletions = ids [ "B" ] }
            Expect.equal (visitSet v) [ "B"; "a"; "b" ] "ordinal: uppercase before lowercase"
    ]

// ---- totality ----

let private genSet : Gen<Set<string>> =
    Gen.listOf (Gen.elements [ "a"; "b"; "c"; "D"; "e" ]) |> Gen.map Set.ofList

let private genVisit : Gen<VisitInput> =
    gen {
        let! unproven = Gen.elements [ true; false ]
        let! accepted = genSet
        let! changed = genSet
        let! present = genSet
        let! noBase = genSet
        let! failed = genSet
        let! revisit = genSet
        let! activeBase = genSet
        let! deletions = genSet
        return
            { Unproven = unproven
              Accepted = accepted
              Changed = changed
              Present = present
              NoBaseRows = noBase
              ApplyFailed = failed
              Revisit = revisit
              ActiveWithBase = activeBase
              LocalDeletions = deletions }
    }

let private genOutcome : Gen<EntryOutcome> =
    gen {
        let! flags = Gen.elements [ true; false ]
        let! c = Gen.elements [ LeaveContent; AdoptCreatedAt; TakeTheirs; HeldAsPinEdit ]
        let! s = Gen.elements [ LeaveSticky; UnpinLocally; ReviewNewPin ]
        let! action = Gen.elements [ KeepPending; ClearPending; RestampPending ]
        let! tier = Gen.elements [ None; Some Hot; Some Warm; Some Cold ]
        let! apply = Gen.elements [ true; false ]
        let m =
            { Merged.Content = (c, action)
              Tags = { Result = [ "x" ]; ApplyLocal = apply; PushNeeded = false }
              Sticky = (s, action)
              Tier = (tier, action)
              Repush = { GroupFlags.Content = flags; Sticky = false; Tier = flags && apply } }
        return!
            Gen.elements
                [ InsertFromRecord; MergeGroups m; LeaveEntry; DeleteEntry; HoldDeletion RemoteDeletion
                  HoldDeletion LocalDeletion; PushDeletion; Ignore; DropRow; Defer ]
    }

let private genDecision : Gen<MergeDecision> =
    gen {
        let! entry = genOutcome
        let! reviews = Gen.elements [ []; [ NewPin ]; [ PinEdit ] ]
        let! notes = Gen.elements [ []; [ EditBeatsDeletion ] ]
        let! rowState = Gen.elements [ None; Some Active; Some Forgotten; Some Left ]
        let! close = Gen.elements [ true; false ]
        let! fresh = Gen.elements [ true; false ]
        return { plain with Entry = entry; Reviews = reviews; Notes = notes; RowState = rowState; CloseHeldDelete = close; FreshContentStamp = fresh }
    }

[<Tests>]
let totalityTests =
    testList "SyncApply is total" [
        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "postponement and demotesToWarm never throw over generated decisions"
            (Prop.forAll (Arb.fromGen genDecision) (fun d ->
                postponement d |> ignore
                demotesToWarm 10 d (record long Hot false) (Some(local "short" Warm false)) |> ignore
                demotesToWarm 10 d (record long Hot false) None |> ignore
                true))

        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "only a review or a repush flag postpones"
            (Prop.forAll (Arb.fromGen genDecision) (fun d ->
                let repush =
                    match d.Entry with
                    | MergeGroups m -> m.Repush.Content || m.Repush.Sticky || m.Repush.Tier
                    | _ -> false
                let expected = not d.Reviews.IsEmpty || repush
                (postponement d).IsSome = expected))

        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "visitSet never throws and returns a sorted, distinct list"
            (Prop.forAll (Arb.fromGen genVisit) (fun v ->
                let r = visitSet v
                r = (r |> List.distinct |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b)))))
    ]

// ---- planRow ----

let private rowBefore : RowBefore =
    { State = Some Active
      Pending = noStamps
      PushingText = None
      BaseTier = Some Warm
      BaseContentStamp = None }

let private issuer (n: int) : Stamp option -> Stamp option =
    fun floor ->
        match floor with
        | Some f -> Some { f with TimeMs = f.TimeMs + 1L; Counter = 0 }
        | None -> Some(st n)

let private never : Stamp option -> Stamp option = fun _ -> None

let private plan d before theirs stored hadEntry colder issue =
    match planRow d before theirs stored hadEntry colder issue with
    | Ok(Some r) -> r
    | other -> failwithf "expected a row, got %A" other

let private theirsRec = record "t" Warm false

[<Tests>]
let planRowTests =
    testList "SyncApply.planRow (decision 12.3)" [
        testCase "KeepPending keeps the group's pending stamp, ClearPending drops it" <| fun () ->
            let before = { rowBefore with Pending = { GroupStamps.Content = Some(st 5); Sticky = Some(st 5); Tier = Some(st 5) } }
            let d = mergeWith (fun m -> { m with Content = (LeaveContent, KeepPending); Sticky = (LeaveSticky, ClearPending); Tier = (None, KeepPending) })
            let r = plan d before (Some theirsRec) (Some(local "a" Warm false)) true false (issuer 9)
            Expect.equal r.Pending { GroupStamps.Content = Some(st 5); Sticky = None; Tier = Some(st 5) } "per group"

        testCase "RestampPending issues above the greater of the record's group stamp and the pending stamp" <| fun () ->
            let seen = ResizeArray<Stamp option>()
            let issue floor =
                seen.Add floor
                Some(st 20)
            let before = { rowBefore with Pending = { noStamps with Content = Some(st 7); Tier = Some(st 0) } }
            let d = mergeWith (fun m -> { m with Content = (LeaveContent, RestampPending); Tier = (None, RestampPending) })
            let r = plan d before (Some theirsRec) (Some(local "a" Warm false)) true false issue
            Expect.equal (List.ofSeq seen) [ Some(st 7); Some(st 1) ] "content floor is the pending stamp (7 > 1), tier floor the record's (1 > 0)"
            Expect.equal r.Pending { noStamps with Content = Some(st 20); Tier = Some(st 20) } "issued stamps"

        testCase "a clock that cannot issue is an Error" <| fun () ->
            let d = mergeWith (fun m -> { m with Sticky = (LeaveSticky, RestampPending) })
            match planRow d rowBefore (Some theirsRec) (Some(local "a" Warm false)) true false never with
            | Error _ -> ()
            | other -> failtestf "expected an error, got %A" other

        testCase "outcomes that clear pending stamps: insert, DropRow and Ignore with no entry; keep: LeaveEntry, Defer" <| fun () ->
            let before = { rowBefore with Pending = { GroupStamps.Content = Some(st 5); Sticky = Some(st 5); Tier = Some(st 5) } }
            let pendingAfter e hadEntry = (plan { plain with Entry = e } before None None hadEntry false (issuer 9)).Pending
            Expect.equal (pendingAfter InsertFromRecord false) noStamps "insert"
            Expect.equal (pendingAfter Ignore false) noStamps "ignore, no entry"
            Expect.equal (pendingAfter DropRow false) noStamps "drop"
            Expect.equal (pendingAfter LeaveEntry true) before.Pending "leave"
            Expect.equal (pendingAfter Defer true) before.Pending "defer"

        testCase "Ok None: no row exists and the decision creates none" <| fun () ->
            let noRow = { rowBefore with State = None }
            for e in [ Ignore; LeaveEntry; Defer; DropRow ] do
                Expect.equal (planRow { plain with Entry = e } noRow None (Some(local "a" Warm false)) true false (issuer 9)) (Ok None) (sprintf "%A" e)
            Expect.isSome (match planRow { plain with Entry = InsertFromRecord; RowState = Some Active } noRow (Some theirsRec) None false false (issuer 9) with Ok r -> r | _ -> None) "insert makes a row"
            Expect.isSome (match planRow (mergeWith id) noRow (Some theirsRec) (Some(local "a" Warm false)) true false (issuer 9) with Ok r -> r | _ -> None) "a merge makes a row"
            Expect.isSome (match planRow { plain with Entry = Ignore; RowState = Some Left } noRow None None false false (issuer 9) with Ok r -> r | _ -> None) "a decision that names a row state makes a row"

        testCase "the state: Active for a new row, the row's own, and the decision's wins" <| fun () ->
            let noRow = { rowBefore with State = None }
            Expect.equal (plan { plain with Entry = InsertFromRecord } noRow (Some theirsRec) None false false (issuer 9)).State Active "new"
            Expect.equal (plan { plain with Entry = LeaveEntry } { rowBefore with State = Some Left } None None true false (issuer 9)).State Left "kept"
            Expect.equal (plan { plain with Entry = Ignore; RowState = Some Active } { rowBefore with State = Some Left } None None false false (issuer 9)).State Active "decision wins"

        testCase "the comparison columns come from the stored entry; none leaves them" <| fun () ->
            let l = { local "a" Hot false with Tags = [ "x" ] }
            let r = plan (mergeWith id) rowBefore (Some theirsRec) (Some l) true false (issuer 9)
            Expect.equal r.Comparison (Some { ContentHash = SyncScan.contentHash l.Content; TagsHash = SyncScan.tagsHash l.Tags; Sticky = false; Tier = Hot }) "from the stored entry"
            Expect.isNone (plan { plain with Entry = Ignore } rowBefore None None false false (issuer 9)).Comparison "no entry"

        testCase "pushing_stamps: unchanged when no base became theirs, a group's key removed when it did, NULL when none is left" <| fun () ->
            let text = PushingStamps.encode { GroupStamps.Content = Some(st 1); Sticky = Some(st 2); Tier = None }
            let before = { rowBefore with PushingText = text }
            let withBase b = { mergeWith id with Base = b }
            let after b = (plan (withBase b) before (Some theirsRec) (Some(local "a" Warm false)) true false (issuer 9)).PushingText
            Expect.equal (after keepAll) text "unchanged"
            Expect.equal (after { keepAll with Content = BaseBecomesTheirs }) (PushingStamps.encode { noStamps with Sticky = Some(st 2) }) "content removed"
            Expect.isNone (after { keepAll with Content = BaseBecomesTheirs; Sticky = BaseBecomesTheirs }) "none left"
            Expect.equal (after { keepAll with Tags = BaseBecomesTheirs }) text "tags carry no stamp"

        testCase "unreadable pushing text is dropped only when a group is removed" <| fun () ->
            let before = { rowBefore with PushingText = Some "garbage" }
            let d b = { mergeWith id with Base = b }
            Expect.equal (plan (d keepAll) before (Some theirsRec) None true false (issuer 9)).PushingText (Some "garbage") "kept"
            Expect.isNone (plan (d { keepAll with Tier = BaseBecomesTheirs }) before (Some theirsRec) None true false (issuer 9)).PushingText "dropped"

        testCase "the hot limit: a pending tier stamp is cleared when the stored tier is not warmer than the base's, kept when warmer or when colderTier is false" <| fun () ->
            let before = { rowBefore with Pending = { noStamps with Tier = Some(st 5) }; BaseTier = Some Warm }
            let d = mergeWith (fun m -> { m with Tier = (None, KeepPending) })
            let tierAfter stored colder = (plan d before (Some theirsRec) (Some stored) true colder (issuer 9)).Pending.Tier
            Expect.isNone (tierAfter (local "a" Warm false) true) "stored warm = base warm: cleared"
            Expect.isNone (tierAfter (local "a" Cold false) true) "stored colder than base: cleared"
            Expect.equal (tierAfter (local "a" Hot false) true) (Some(st 5)) "stored hot is warmer than the base: kept"
            Expect.equal (tierAfter (local "a" Warm false) false) (Some(st 5)) "not limited: kept"

        testCase "the hot limit with an unknown base tier keeps the pending tier stamp (the scan never clears then)" <| fun () ->
            let before = { rowBefore with Pending = { noStamps with Tier = Some(st 5) }; BaseTier = None }
            let d = mergeWith (fun m -> { m with Tier = (None, KeepPending) })
            let r = plan d before (Some theirsRec) (Some(local "a" Cold false)) true true (issuer 9)
            Expect.equal r.Pending.Tier (Some(st 5)) "kept"

        testCase "an empty scope on the stored entry hashes as the stored view does" <| fun () ->
            let l = { local "a" Warm false with Content = { fields "a" with Scope = None } }
            let r = plan (mergeWith id) rowBefore (Some theirsRec) (Some l) true false (issuer 9)
            Expect.equal r.Comparison.Value.ContentHash (SyncScan.contentHash l.Content) "from the view, not from the record"

        testCase "planRow_fresh_content_stamp_issues_above_pending_and_base" <| fun () ->
            let fresh = { plain with Entry = LeaveEntry; FreshContentStamp = true; Notes = [ EditBeatsDeletion ] }
            let run pending baseStamp =
                let seen = ResizeArray<Stamp option>()
                let issue floor =
                    seen.Add floor
                    Some(st 50)
                let before =
                    { rowBefore with
                        Pending = { GroupStamps.Content = pending; Sticky = Some(st 4); Tier = Some(st 6) }
                        PushingText = Some "{}"
                        BaseContentStamp = baseStamp }
                let r = plan fresh before None (Some(local "a" Warm false)) true false issue
                List.ofSeq seen, r
            let seen, r = run (Some(st 7)) (Some(st 9))
            Expect.equal seen [ Some(st 9) ] "the base's stamp is the greater"
            Expect.equal r.Pending { GroupStamps.Content = Some(st 50); Sticky = Some(st 4); Tier = Some(st 6) } "only the content stamp changes"
            Expect.equal r.PushingText (Some "{}") "pushing_stamps unchanged"
            Expect.equal r.State Active "the row stays active"
            let seen, _ = run (Some(st 12)) (Some(st 9))
            Expect.equal seen [ Some(st 12) ] "the pending stamp is the greater"
            let seen, _ = run None (Some(st 3))
            Expect.equal seen [ Some(st 3) ] "no pending stamp: the base's"
            match planRow fresh { rowBefore with BaseContentStamp = Some(st 3) } None (Some(local "a" Warm false)) true false never with
            | Error _ -> ()
            | other -> failtestf "a clock that cannot issue is an Error, got %A" other

        testCase "planRow_delete_entry_forgotten_clears_pending" <| fun () ->
            let before =
                { rowBefore with
                    Pending = { GroupStamps.Content = Some(st 5); Sticky = Some(st 5); Tier = Some(st 5) }
                    PushingText = PushingStamps.encode { noStamps with Content = Some(st 2) } }
            let d = { plain with Entry = DeleteEntry; RowState = Some Forgotten; Notes = [ PendingChangesDropped ] }
            let r = plan d before None None true false never
            Expect.equal r.State Forgotten "forgotten"
            Expect.equal r.Pending noStamps "pending stamps cleared"
            Expect.isNone r.PushingText "pushing_stamps cleared"
            Expect.isNone r.Comparison "no stored entry: the comparison columns are left alone"

        testCase "the hot limit uses the record's tier when the tier group's base became the record's" <| fun () ->
            let before = { rowBefore with Pending = { noStamps with Tier = Some(st 5) }; BaseTier = Some Cold }
            let d = { mergeWith (fun m -> { m with Tier = (None, KeepPending) }) with Base = { keepAll with Tier = BaseBecomesTheirs } }
            let r = plan d before (Some(record "t" Hot false)) (Some(local "a" Warm false)) true true (issuer 9)
            Expect.isNone r.Pending.Tier "the new base is the record's hot tier; the stored warm is not warmer than it, so the stamp is cleared"
    ]

[<Tests>]
let planRowTotalityTests =
    testList "SyncApply.planRow is total" [
        testPropertyWithConfig
            { FsCheckConfig.defaultConfig with maxTest = 2000 }
            "planRow never throws over generated decisions, rows and clocks"
            (Prop.forAll
                (Arb.fromGen (Gen.map4 (fun d st hadEntry (colder, clock) -> d, st, hadEntry, colder, clock) genDecision (Gen.elements [ None; Some Active; Some Left ]) (Gen.elements [ true; false ]) (Gen.zip (Gen.elements [ true; false ]) (Gen.elements [ true; false ]))))
                (fun (d, state, hadEntry, colder, clockWorks) ->
                    let before = { rowBefore with State = state; PushingText = Some "{}" }
                    let issue = if clockWorks then issuer 9 else never
                    planRow d before (Some theirsRec) (Some(local "a" Warm false)) hadEntry colder issue |> ignore
                    planRow d before None None hadEntry colder issue |> ignore
                    true))
    ]
