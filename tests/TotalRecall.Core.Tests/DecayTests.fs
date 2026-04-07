module TotalRecall.Core.Tests.DecayTests

open Expecto
open TotalRecall.Core

// Tests for TotalRecall.Core.Decay.calculateDecayScore.
//
// Formula: timeFactor * freqFactor * typeWeight where
//   timeFactor = exp(-hoursSinceAccess / decayConstantHours)
//   freqFactor = 1 + log2(1 + accessCount)
//   typeWeight = per-EntryType (see Decay.typeWeight)

let private MS_PER_HOUR = 60L * 60L * 1000L

[<Tests>]
let decayTests =
    testList "Decay" [
        testCase "fresh entry just accessed has timeFactor ~1.0" <| fun _ ->
            // hoursSinceAccess = 0, so exp(0) = 1.
            // freqFactor with accessCount=0 -> 1 + log2(1) = 1.
            // typeWeight Decision = 1.0.
            // Expected: 1.0 * 1.0 * 1.0 = 1.0
            let now = 1_000_000_000L
            let score = Decay.calculateDecayScore now 0 Decision now 24.0
            Expect.floatClose Accuracy.medium score 1.0
                "fresh entry, 0 access, Decision type should give score 1.0"

        testCase "one decay constant elapsed gives timeFactor 1/e (~0.368)" <| fun _ ->
            let decayConstHours = 24.0
            let elapsedMs = int64 (decayConstHours * float MS_PER_HOUR)
            let now = 2_000_000_000L
            let lastAccessed = now - elapsedMs
            let score = Decay.calculateDecayScore lastAccessed 0 Decision now decayConstHours
            // timeFactor = exp(-1) ≈ 0.36788; freq=1, type=1; expected ≈ 0.36788
            Expect.floatClose Accuracy.medium score (1.0 / System.Math.E)
                "one decay-constant elapsed should give timeFactor 1/e"

        testCase "score decreases monotonically as time elapses" <| fun _ ->
            let now = 1_000_000_000L
            let decayConstHours = 24.0
            let elapsedHours = [0.0; 1.0; 6.0; 12.0; 24.0; 48.0; 168.0]
            let scores =
                elapsedHours
                |> List.map (fun h ->
                    let elapsedMs = int64 (h * float MS_PER_HOUR)
                    Decay.calculateDecayScore (now - elapsedMs) 0 Decision now decayConstHours)
            let sortedDesc = scores |> List.sortDescending
            Expect.equal scores sortedDesc
                "scores should be monotonically decreasing as elapsed hours grow"

        testCase "freqFactor increases with accessCount" <| fun _ ->
            let now = 1_000_000_000L
            let scoreAt count =
                Decay.calculateDecayScore now count Decision now 24.0
            let s0 = scoreAt 0
            let s5 = scoreAt 5
            let s50 = scoreAt 50
            Expect.isLessThan s0 s5 "5 accesses should outscore 0 accesses"
            Expect.isLessThan s5 s50 "50 accesses should outscore 5 accesses"

        testCase "typeWeight differentiates entry types" <| fun _ ->
            let now = 1_000_000_000L
            let weight et =
                Decay.calculateDecayScore now 0 et now 24.0
            // Per src-ts/memory/decay.ts TYPE_WEIGHTS:
            // correction: 1.5, preference: 1.3, decision: 1.0,
            // surfaced: 0.8, imported: 1.1, compacted: 1.0, ingested: 0.9
            Expect.floatClose Accuracy.medium (weight Correction) 1.5
                "Correction weight should be 1.5"
            Expect.floatClose Accuracy.medium (weight Preference) 1.3
                "Preference weight should be 1.3"
            Expect.floatClose Accuracy.medium (weight Decision) 1.0
                "Decision weight should be 1.0"
            Expect.floatClose Accuracy.medium (weight Surfaced) 0.8
                "Surfaced weight should be 0.8"
            Expect.floatClose Accuracy.medium (weight Imported) 1.1
                "Imported weight should be 1.1"
            Expect.floatClose Accuracy.medium (weight Compacted) 1.0
                "Compacted weight should be 1.0"
            Expect.floatClose Accuracy.medium (weight Ingested) 0.9
                "Ingested weight should be 0.9"
    ]
