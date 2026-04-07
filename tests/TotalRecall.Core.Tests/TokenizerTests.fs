module TotalRecall.Core.Tests.TokenizerTests

open Expecto
open TotalRecall.Core

// Behavioral tests for TotalRecall.Core.Tokenizer.
//
// Per the Plan 2 Option-A pivot (after Task 2.1's empirical findings showed
// the spec's "Microsoft is more canonical" framing was wrong on inspection):
// the F# Tokenizer port matches the EXISTING TS WordPieceTokenizer's output,
// not Microsoft.ML.Tokenizers's. The fixture at
// tests/fixtures/embeddings/tokenizer-reference.json is the spike's
// 544-pair (input, tokenIds) record produced by running the TS tokenizer
// (src-ts/embedding/tokenizer.ts) over a representative corpus.
//
// The F# port must match those numbers byte-for-byte. The "tokenizer
// correctness" question (whether TS or Microsoft is closer to the model's
// training distribution) is deferred to a later plan when we can compare
// embedding quality side by side, not just token IDs.
//
// Once Tokenizer.tokenize is implemented in Task 2.4, these tests go green.

[<Tests>]
let tokenizerTests =
    testList "Tokenizer" [
        testCase "loads fixture without crashing" <| fun _ ->
            let fixture = FixtureLoader.loadTokenizerFixtures()
            Expect.isGreaterThan fixture.Entries.Length 10
                "fixture should have at least 10 entries"

        testCase "loads vocab without crashing" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            Expect.isGreaterThan vocab.Count 1000 "vocab should have > 1000 entries"
            Expect.isTrue (Map.containsKey "[CLS]" vocab) "vocab should contain [CLS]"

        testCase "fixture is well-formed" <| fun _ ->
            let fixture = FixtureLoader.loadTokenizerFixtures()
            for entry in fixture.Entries do
                Expect.isNotNull entry.Input "entry.Input should not be null"
                Expect.isNotNull entry.TokenIds "entry.TokenIds should not be null"
                // Non-empty inputs should produce at least CLS + SEP (2 tokens)
                if entry.Input.Length > 0 && entry.Input.Trim().Length > 0 then
                    Expect.isGreaterThanOrEqual entry.TokenIds.Length 2
                        (sprintf "entry %A should have at least 2 tokens" entry.Input)

        testCase "tokenize matches fixture for all entries" <| fun _ ->
            let fixture = FixtureLoader.loadTokenizerFixtures()
            let vocab = FixtureLoader.loadVocab()
            let failures = ResizeArray<string>()
            for entry in fixture.Entries do
                let actual = Tokenizer.tokenize vocab entry.Input |> List.toArray
                if actual <> entry.TokenIds then
                    failures.Add(
                        sprintf "input=%A\n  expected=%A\n  actual=%A"
                            entry.Input entry.TokenIds actual)
            if failures.Count > 0 then
                let msg =
                    sprintf "Tokenizer mismatch on %d/%d inputs (showing first 10):\n%s"
                        failures.Count fixture.Entries.Length
                        (failures |> Seq.truncate 10 |> String.concat "\n")
                failtest msg
    ]
