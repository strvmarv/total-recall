module TotalRecall.Core.Tests.SyncRecordTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.SyncRecord

let private allEntryTypes = [ Correction; Preference; Decision; Surfaced; Imported; Compacted; Ingested ]
let private allSources = [ ClaudeCode; CopilotCli; Opencode; Cursor; Cline; Hermes; ManualSource ]
let private allTiers = [ Hot; Warm; Cold ]

[<Tests>]
let recordTests =
    testList "SyncRecord" [
        testCase "entry type names round-trip and are lower case" <| fun _ ->
            for t in allEntryTypes do
                Expect.equal (tryParseEntryType (entryTypeName t)) (Some t) (sprintf "%A" t)
                Expect.equal (entryTypeName t) ((entryTypeName t).ToLowerInvariant()) "lower case"
            Expect.equal (allEntryTypes |> List.map entryTypeName)
                [ "correction"; "preference"; "decision"; "surfaced"; "imported"; "compacted"; "ingested" ] "the seven names"

        testCase "source names round-trip" <| fun _ ->
            for t in allSources do
                Expect.equal (tryParseSourceTool (sourceToolName t)) (Some t) (sprintf "%A" t)
            Expect.equal (allSources |> List.map sourceToolName)
                [ "claude-code"; "copilot-cli"; "opencode"; "cursor"; "cline"; "hermes"; "manual" ] "the seven names"

        testCase "tier names round-trip" <| fun _ ->
            for t in allTiers do
                Expect.equal (tryParseTier (tierName t)) (Some t) (sprintf "%A" t)
            Expect.equal (allTiers |> List.map tierName) [ "hot"; "warm"; "cold" ] "three names"

        testCase "unknown names give None" <| fun _ ->
            for s in [ "Correction"; ""; "windsurf"; "HOT"; "pinned"; null ] do
                Expect.isNone (tryParseEntryType s) (sprintf "entry type %A" s)
                Expect.isNone (tryParseSourceTool s) (sprintf "source %A" s)
                Expect.isNone (tryParseTier s) (sprintf "tier %A" s)

        testCase "tierRank orders hot before warm before cold" <| fun _ ->
            Expect.isLessThan (tierRank Hot) (tierRank Warm) "hot < warm"
            Expect.isLessThan (tierRank Warm) (tierRank Cold) "warm < cold"

        testCase "normaliseTags sorts by UTF-16 code unit and removes duplicates" <| fun _ ->
            // U+1F600 is the surrogate pair D83D DE00, which sorts before U+FFFD in code-unit order
            // (and after it in UTF-8 byte order).
            Expect.equal (normaliseTags [ "b"; "a"; "b"; "\uFFFD"; "\U0001F600" ]) [ "a"; "b"; "\U0001F600"; "\uFFFD" ] "ordinal"
            Expect.equal (normaliseTags []) [] "empty"
    ]
