module TotalRecall.Core.Tests.TokenizerTests

open Expecto
open TotalRecall.Core

// Behavioral tests for TotalRecall.Core.Tokenizer.
//
// The F# port must match Microsoft.ML.Tokenizers.BertTokenizer running on
// the all-MiniLM-L6-v2 vocab, on every entry in tests/fixtures/embeddings/
// tokenizer-reference.json (generated in Plan 2 Task 2.1).
//
// Empirical finding from Task 2.1: Microsoft.ML.Tokenizers produces multiple
// WordPiece tokens for snake_case identifiers (NOT a single chain). The F#
// port's job is to match that output byte-for-byte — the fixture is the
// oracle, not the spec's aspirational framing.
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
                    sprintf "Tokenizer mismatch on %d/%d inputs:\n%s"
                        failures.Count fixture.Entries.Length
                        (String.concat "\n" failures)
                failtest msg

        testCase "snake_case_identifier matches fixture" <| fun _ ->
            // Empirical finding from Task 2.1: Microsoft.ML.Tokenizers produces
            // 8 content tokens for 'snake_case_identifier' (NOT a single chain).
            // The F# port's job is to match that output byte-for-byte, whatever
            // it is. The "canonical single chain" framing from the spec turned
            // out to be aspirational; the fixture is the actual oracle.
            let vocab = FixtureLoader.loadVocab()
            let fixture = FixtureLoader.loadTokenizerFixtures()
            let expected =
                fixture.Entries
                |> Array.tryFind (fun e -> e.Input = "snake_case_identifier")
                |> Option.map (fun e -> e.TokenIds)
            match expected with
            | Some exp ->
                let actual = Tokenizer.tokenize vocab "snake_case_identifier" |> List.toArray
                Expect.equal actual exp
                    "snake_case_identifier F# output should match fixture exactly"
            | None ->
                failtest "fixture missing 'snake_case_identifier' entry"
    ]
