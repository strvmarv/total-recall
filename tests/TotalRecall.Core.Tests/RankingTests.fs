module TotalRecall.Core.Tests.RankingTests

open Expecto
open TotalRecall.Core

// Tests for TotalRecall.Core.Ranking.hybridScore.
// Formula (from src-ts/memory/search.ts): vector + ftsWeight * fts.

[<Tests>]
let rankingTests =
    testList "Ranking" [
        testCase "ftsWeight = 0 yields vectorScore alone" <| fun _ ->
            let s = Ranking.hybridScore 0.7 0.9 0.0
            Expect.floatClose Accuracy.medium s 0.7
                "with ftsWeight=0, score should equal vectorScore"

        testCase "ftsScore = 0 yields vectorScore alone" <| fun _ ->
            let s = Ranking.hybridScore 0.5 0.0 0.5
            Expect.floatClose Accuracy.medium s 0.5
                "with ftsScore=0, score should equal vectorScore"

        testCase "vectorScore = 0 yields ftsWeight * ftsScore" <| fun _ ->
            let s = Ranking.hybridScore 0.0 0.8 0.25
            Expect.floatClose Accuracy.medium s (0.25 * 0.8)
                "with vectorScore=0, score should equal ftsWeight * ftsScore"

        testCase "additive: vector=0.6 fts=0.4 weight=0.5 -> 0.8" <| fun _ ->
            let s = Ranking.hybridScore 0.6 0.4 0.5
            Expect.floatClose Accuracy.medium s 0.8
                "0.6 + 0.5 * 0.4 = 0.8"

        testCase "monotonic in vectorScore (fts and weight fixed)" <| fun _ ->
            let fts = 0.5
            let w = 0.3
            let scores = [0.0; 0.1; 0.5; 0.9; 1.0]
                         |> List.map (fun v -> Ranking.hybridScore v fts w)
            let sorted = scores |> List.sort
            Expect.equal scores sorted
                "scores should be monotonically increasing with vectorScore"

        testCase "monotonic in ftsScore when ftsWeight > 0" <| fun _ ->
            let v = 0.5
            let w = 0.4
            let scores = [0.0; 0.1; 0.5; 0.9; 1.0]
                         |> List.map (fun fts -> Ranking.hybridScore v fts w)
            let sorted = scores |> List.sort
            Expect.equal scores sorted
                "scores should be monotonically increasing with ftsScore when ftsWeight > 0"
    ]
