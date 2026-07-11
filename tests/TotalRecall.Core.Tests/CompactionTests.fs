module TotalRecall.Core.Tests.CompactionTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.Compaction

// Tests for the Compaction vocabulary types. No logic to test —
// this module is data types only. We verify each DU variant can be
// constructed and pattern-matched, and that the CompactionInput record
// holds the expected fields.

let private describeDecision (d: CompactionDecision) : string =
    match d with
    | CarryForward -> "carry_forward"
    | Compact None -> "compact (no summary)"
    | Compact (Some _) -> "compact (with summary)"
    | Discard _ -> "discard"

let private sampleEntry : Entry = {
    Id = "abc"
    Content = "hello"
    Summary = None
    Source = None
    SourceTool = None
    Project = None
    Tags = []
    CreatedAt = 1L
    UpdatedAt = 1L
    LastAccessedAt = 1L
    AccessCount = 0
    DecayScore = 1.0
    ParentId = None
    CollectionId = None
    Scope = ""
    EntryType = Preference
    MetadataJson = "{}"
    TimesInjected = 0
}

[<Tests>]
let compactionTests =
    testList "Compaction" [
        testCase "CarryForward variant constructs and pattern-matches" <| fun _ ->
            let d = CarryForward
            Expect.equal (describeDecision d) "carry_forward" "CarryForward should match"

        testCase "Compact with no summary" <| fun _ ->
            let d = Compact None
            Expect.equal (describeDecision d) "compact (no summary)" "Compact None should match"

        testCase "Compact with summary string" <| fun _ ->
            let d = Compact (Some "key insights")
            Expect.equal (describeDecision d) "compact (with summary)" "Compact (Some _) should match"
            match d with
            | Compact (Some s) -> Expect.equal s "key insights" "summary should be extractable"
            | _ -> failtest "should be Compact (Some _)"

        testCase "Discard with reason" <| fun _ ->
            let d = Discard "duplicate content"
            Expect.equal (describeDecision d) "discard" "Discard should match"
            match d with
            | Discard reason -> Expect.equal reason "duplicate content" "reason should be extractable"
            | _ -> failtest "should be Discard"

        testCase "CompactionInput holds entries and timestamp" <| fun _ ->
            let input = { HotEntries = [sampleEntry]; NowMs = 1234567890L }
            Expect.equal input.HotEntries.Length 1 "should have one hot entry"
            Expect.equal input.NowMs 1234567890L "should have the right timestamp"
            Expect.equal input.HotEntries.Head.Id "abc" "entry id should match"

        testCase "decision equality is structural" <| fun _ ->
            Expect.equal CarryForward CarryForward "same decisions should be equal"
            Expect.equal (Compact None) (Compact None) "Compact None equals itself"
            Expect.equal (Compact (Some "a")) (Compact (Some "a")) "Compact (Some) equality"
            Expect.notEqual (Compact (Some "a")) (Compact (Some "b")) "different summaries differ"
            Expect.notEqual CarryForward (Discard "x") "different variants differ"
    ]
