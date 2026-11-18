module TotalRecall.Core.Tests.SyncPushTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord
open TotalRecall.Core.SyncMerge
open TotalRecall.Core.SyncPush

let private m1 = (MachineId.tryCreate "4f2a9c1e").Value
let private st (n: int) : Stamp = { TimeMs = 1_700_000_000_000L + int64 n * 1000L; Counter = 0; Machine = m1 }
let private noStamps : GroupStamps = { GroupStamps.Content = None; Sticky = None; Tier = None }

// ---- candidates ----

let private row : RowFacts =
    { State = Active
      HasBase = true
      BaseTagsHash = Some "h1"
      ScannedTagsHash = Some "h1"
      Pending = noStamps }

[<Tests>]
let candidateTests =
    testList "SyncPush.isCandidate (decision 4)" [
        testCase "a row with a base and nothing pending is not a candidate" <| fun () ->
            Expect.isFalse (isCandidate row) "clean"
        testCase "a row with no base is a candidate" <| fun () ->
            Expect.isTrue (isCandidate { row with HasBase = false; BaseTagsHash = None }) "no base"
        testCase "each pending group alone makes a candidate" <| fun () ->
            Expect.isTrue (isCandidate { row with Pending = { noStamps with Content = Some(st 1) } }) "content"
            Expect.isTrue (isCandidate { row with Pending = { noStamps with Sticky = Some(st 1) } }) "sticky"
            Expect.isTrue (isCandidate { row with Pending = { noStamps with Tier = Some(st 1) } }) "tier"
        testCase "different tags hashes make a candidate, equal or a NULL one do not" <| fun () ->
            Expect.isTrue (isCandidate { row with ScannedTagsHash = Some "h2" }) "different"
            Expect.isFalse (isCandidate { row with ScannedTagsHash = None }) "scanned NULL"
            Expect.isFalse (isCandidate { row with BaseTagsHash = None }) "base unreadable"
        testCase "a forgotten or left row is never a candidate" <| fun () ->
            for s in [ Forgotten; Left ] do
                Expect.isFalse (isCandidate { row with State = s; HasBase = false }) (sprintf "%A" s)
    ]

// ---- exclusions ----

let private none : ExclusionFacts =
    { HasIssueOrSkip = false
      RejectedNamesId = false
      HasBase = true
      RecordAccepted = true
      RecordUnreadable = false
      BaseUnreadable = false
      PathOccupied = false
      BlockedElsewhere = false
      EntryUnreadable = false }

[<Tests>]
let exclusionTests =
    testList "SyncPush.exclusion (decision 5)" [
        testCase "a plain changed entry and a plain new entry are not excluded" <| fun () ->
            Expect.isNone (exclusion none) "changed"
            Expect.isNone (exclusion { none with HasBase = false; RecordAccepted = false }) "new"
        testCase "every reason alone" <| fun () ->
            let cases =
                [ { none with HasIssueOrSkip = true }, Issue
                  { none with RejectedNamesId = true }, RejectedRecord
                  { none with HasBase = false }, NotReconciled
                  { none with RecordAccepted = false }, RecordMissing
                  { none with RecordUnreadable = true }, RecordUnreadable
                  { none with BaseUnreadable = true }, UnreadableBase
                  { none with PathOccupied = true }, PathOccupied
                  { none with BlockedElsewhere = true }, BlockedElsewhere
                  { none with EntryUnreadable = true }, UnreadableEntry ]
            for (facts, expected) in cases do
                Expect.equal (exclusion facts) (Some expected) (exclusionName expected)
        testCase "each adjacent pair follows the order of decision 5" <| fun () ->
            let all =
                { HasIssueOrSkip = true
                  RejectedNamesId = true
                  HasBase = false
                  RecordAccepted = true
                  RecordUnreadable = true
                  BaseUnreadable = true
                  PathOccupied = true
                  BlockedElsewhere = true
                  EntryUnreadable = true }
            Expect.equal (exclusion all) (Some Issue) "issue first"
            let a = { all with HasIssueOrSkip = false }
            Expect.equal (exclusion a) (Some RejectedRecord) "rejected next"
            let b = { a with RejectedNamesId = false }
            Expect.equal (exclusion b) (Some NotReconciled) "not-reconciled next (no base, record accepted)"
            let c = { b with HasBase = true }
            Expect.equal (exclusion c) (Some RecordUnreadable) "record-unreadable next"
            let d = { c with RecordUnreadable = false }
            Expect.equal (exclusion d) (Some UnreadableBase) "unreadable-base next"
            let e = { d with BaseUnreadable = false }
            Expect.equal (exclusion e) (Some PathOccupied) "path-occupied next"
            let f = { e with PathOccupied = false }
            Expect.equal (exclusion f) (Some BlockedElsewhere) "blocked-elsewhere next"
            let g = { f with BlockedElsewhere = false }
            Expect.equal (exclusion g) (Some UnreadableEntry) "unreadable-entry last"
            Expect.isNone (exclusion { g with EntryUnreadable = false }) "none"
            Expect.equal (exclusion { { none with RecordAccepted = false } with PathOccupied = true }) (Some RecordMissing) "record-missing before path-occupied"
        testCase "the reason words are the spec's" <| fun () ->
            Expect.equal
                ([ Issue; RejectedRecord; NotReconciled; RecordMissing; RecordUnreadable; UnreadableBase; PathOccupied; BlockedElsewhere; UnreadableEntry ]
                 |> List.map exclusionName)
                [ "issue"; "rejected-record"; "not-reconciled"; "record-missing"; "record-unreadable"; "unreadable-base"; "path-occupied"; "blocked-elsewhere"; "unreadable-entry" ]
                "names"
    ]

// ---- the path rule ----

[<Tests>]
let pathTests =
    testList "SyncPush.pathOccupied (decision 5)" [
        let id = "abcdef12"
        let own = "memories/ab/abcdef12.json"
        testCase "the record's own exact path alone is no collision" <| fun () ->
            Expect.isFalse (pathOccupied id [ own ]) "own"
        testCase "a case-folded shard directory collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/AB/x.json" ]) "shard folded"
        testCase "a case variant of the record's own path collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/ab/ABCDEF12.json" ]) "file folded"
        testCase "a blob at the shard directory or at memories collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/ab" ]) "shard blob"
            Expect.isTrue (pathOccupied id [ "memories" ]) "memories blob"
        testCase "a path under the record's own path collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ own + "/x" ]) "directory at the record"
        testCase "siblings with the same directories never collide" <| fun () ->
            Expect.isFalse (pathOccupied id [ own; "memories/ab/other.json"; "memories/cd/other.json" ]) "siblings"
        testCase "a deeper nested path under a folded shard collides" <| fun () ->
            Expect.isTrue (pathOccupied id [ "memories/AB/sub/x.json" ]) "nested"
        testCase "a root-level case variant is not compared (the listing never has it)" <| fun () ->
            Expect.isFalse (pathOccupied id [ "MEMORIES/x.json" ]) "root variant"
        testCase "an empty listing and an ineligible id are false, with no exception" <| fun () ->
            Expect.isFalse (pathOccupied id []) "empty"
            Expect.isFalse (pathOccupied "XYZ" [ "memories/xy/x.json" ]) "ineligible"
    ]

// ---- block marks ----

[<Tests>]
let markTests =
    testList "SyncPush.markAction (decision 8)" [
        testCase "every combination of reason, violation and candidate" <| fun () ->
            let tooLarge = Some SyncValidation.TooLarge
            let table =
                [ // reason, violation, candidate, expected
                  None, None, true, KeepMark
                  None, None, false, KeepMark
                  None, tooLarge, true, SetMark "too-large"
                  None, tooLarge, false, KeepMark
                  Some "too-large", None, true, ClearMark
                  Some "too-large", None, false, ClearMark
                  Some "too-large", tooLarge, true, SetMark "too-large"
                  Some "too-large", tooLarge, false, KeepMark
                  Some "secret", None, true, KeepMark
                  Some "secret", None, false, KeepMark
                  Some "secret", tooLarge, true, KeepMark
                  Some "secret", tooLarge, false, KeepMark ]
            for (reason, violation, candidate, expected) in table do
                Expect.equal (markAction reason violation candidate) expected (sprintf "%A %A %b" reason violation candidate)
        testCase "blockName names every Block case, and only the first five are the writer's" <| fun () ->
            let names =
                [ SyncValidation.TooLarge; SyncValidation.TooManyTags; SyncValidation.TagTooLong
                  SyncValidation.IneligibleId; SyncValidation.NotEncodable; SyncValidation.ClockExhausted ]
                |> List.map blockName
            Expect.equal names [ "too-large"; "too-many-tags"; "tag-too-long"; "ineligible-id"; "not-encodable"; "clock-exhausted" ] "names"
            Expect.equal (List.filter isWriterReason names) writerReasons "writer reasons"
    ]
