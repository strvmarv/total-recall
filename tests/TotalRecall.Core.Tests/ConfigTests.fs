module TotalRecall.Core.Tests.ConfigTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.Config

[<Tests>]
let configTests =
    testList "Config" [
        testCase "isSafeKey rejects __proto__" <| fun _ ->
            Expect.isFalse (isSafeKey "__proto__") "__proto__ should be unsafe"

        testCase "isSafeKey rejects constructor" <| fun _ ->
            Expect.isFalse (isSafeKey "constructor") "constructor should be unsafe"

        testCase "isSafeKey rejects prototype" <| fun _ ->
            Expect.isFalse (isSafeKey "prototype") "prototype should be unsafe"

        testCase "isSafeKey accepts normal keys" <| fun _ ->
            Expect.isTrue (isSafeKey "tiers") "tiers should be safe"
            Expect.isTrue (isSafeKey "max_entries") "max_entries should be safe"

        testCase "deepMerge: source overrides target at top level" <| fun _ ->
            let target = Map.ofList ["a", box 1; "b", box 2]
            let source = Map.ofList ["b", box 99]
            let merged = deepMerge target source
            Expect.equal (Map.find "a" merged) (box 1) "untouched key preserved"
            Expect.equal (Map.find "b" merged) (box 99) "overridden key updated"

        testCase "deepMerge: source-only key added to result" <| fun _ ->
            let target = Map.ofList ["a", box 1]
            let source = Map.ofList ["b", box 2]
            let merged = deepMerge target source
            Expect.equal (Map.find "a" merged) (box 1) "target key kept"
            Expect.equal (Map.find "b" merged) (box 2) "new key added"

        testCase "deepMerge: nested maps merged recursively" <| fun _ ->
            let target =
                Map.ofList [
                    "tiers",
                    box (Map.ofList ["hot", box 1; "warm", box 2])
                ]
            let source =
                Map.ofList [
                    "tiers",
                    box (Map.ofList ["warm", box 99; "cold", box 3])
                ]
            let merged = deepMerge target source
            let tiers = Map.find "tiers" merged :?> Map<string, obj>
            Expect.equal (Map.find "hot" tiers) (box 1) "hot preserved from target"
            Expect.equal (Map.find "warm" tiers) (box 99) "warm overridden from source"
            Expect.equal (Map.find "cold" tiers) (box 3) "cold added from source"

        testCase "deepMerge: __proto__ key skipped" <| fun _ ->
            let target = Map.ofList ["safe", box 1]
            let source = Map.ofList ["__proto__", box "evil"; "safe", box 2]
            let merged = deepMerge target source
            Expect.equal (Map.find "safe" merged) (box 2) "safe key updated"
            Expect.isFalse (Map.containsKey "__proto__" merged) "__proto__ rejected"

        testCase "setNestedKey: simple key" <| fun _ ->
            let result = setNestedKey Map.empty "foo" (box 42)
            Expect.equal (Map.find "foo" result) (box 42) "should set foo to 42"

        testCase "setNestedKey: nested key creates intermediate map" <| fun _ ->
            let result = setNestedKey Map.empty "tiers.hot" (box 100)
            let tiers = Map.find "tiers" result :?> Map<string, obj>
            Expect.equal (Map.find "hot" tiers) (box 100) "should set tiers.hot to 100"

        testCase "setNestedKey: deeply nested key" <| fun _ ->
            let result = setNestedKey Map.empty "a.b.c.d" (box "deep")
            let a = Map.find "a" result :?> Map<string, obj>
            let b = Map.find "b" a :?> Map<string, obj>
            let c = Map.find "c" b :?> Map<string, obj>
            Expect.equal (Map.find "d" c) (box "deep") "should set a.b.c.d"

        testCase "setNestedKey: throws on unsafe key segment" <| fun _ ->
            Expect.throws
                (fun () -> setNestedKey Map.empty "a.__proto__.x" (box 1) |> ignore)
                "should throw on __proto__ segment"

        testCase "TotalRecallConfig record can be constructed" <| fun _ ->
            let cfg : TotalRecallConfig = {
                Tiers = {
                    Hot = { MaxEntries = 50; TokenBudget = 8000; CarryForwardThreshold = 0.5 }
                    Warm = { MaxEntries = 200; RetrievalTopK = 10; SimilarityThreshold = 0.5; ColdDecayDays = 30 }
                    Cold = { ChunkMaxTokens = 500; ChunkOverlapTokens = 50; LazySummaryThreshold = 100 }
                }
                Compaction = {
                    DecayHalfLifeHours = 24.0
                    WarmThreshold = 0.5
                    PromoteThreshold = 0.7
                    WarmSweepIntervalDays = 7
                }
                Embedding = { Model = "all-MiniLM-L6-v2"; Dimensions = 384; Provider = None; Endpoint = None; BedrockRegion = None; BedrockModel = None; ModelName = None; ApiKey = None }
                Regression = None
                Search = Some { FtsWeight = Some 0.3 }
                Storage = None
                User = None
            }
            Expect.equal cfg.Tiers.Hot.MaxEntries 50 "field access works"
            Expect.equal cfg.Embedding.Model "all-MiniLM-L6-v2" "nested field access works"
            Expect.equal cfg.Search (Some { FtsWeight = Some 0.3 }) "optional record works"
    ]
