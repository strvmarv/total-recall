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

        testCase "countTokens returns correct token count for simple text" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let count = Tokenizer.countTokens vocab "hello world"
            Expect.isGreaterThanOrEqual count 3 "should be at least CLS + tokens + SEP"
            Expect.isLessThanOrEqual count 10 "should be reasonable for short text"

        testCase "countTokens returns 0 for empty string" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let count = Tokenizer.countTokens vocab ""
            Expect.equal count 0 "empty string should have 0 tokens"

        testCase "countTokens returns 0 for whitespace only" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let count = Tokenizer.countTokens vocab "   "
            Expect.equal count 0 "whitespace should have 0 tokens"

        testCase "countTokens grows with longer text" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let short = Tokenizer.countTokens vocab "hello"
            let long = Tokenizer.countTokens vocab "hello world this is a longer piece of text"
            Expect.isGreaterThan long short "longer text should have more tokens"

        testCase "truncateToTokens respects max token budget" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let text = "hello world hello world hello world hello world hello world hello world"
            let truncated = Tokenizer.truncateToTokens vocab 6 text
            let count = Tokenizer.countTokens vocab truncated
            Expect.isLessThanOrEqual count 6 "truncated text should be within token budget"
            Expect.isGreaterThanOrEqual count 2 "should have at least some tokens"

        testCase "truncateToTokens returns empty string for zero maxTokens" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let result = Tokenizer.truncateToTokens vocab 0 "hello world"
            Expect.equal result "" "zero maxTokens should return empty string"

        testCase "truncateToTokens returns original text when maxTokens >= MaxSeqLen" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let text = "short text"
            let result = Tokenizer.truncateToTokens vocab 600 text
            Expect.equal result text "oversized maxTokens should return original text"

        testCase "truncateToTokens produces valid text fragments" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let text = "The quick brown fox. Jumps over the lazy dog. Another sentence here."
            let truncated = Tokenizer.truncateToTokens vocab 10 text
            Expect.isTrue (truncated.Length > 0) "should produce non-empty result"
            Expect.isTrue (text.StartsWith(truncated.Trim())) "truncated should be a prefix of original"

        // GUARD against a silent change to a KNOWN GAP — not an endorsement of
        // accent->UNK as desired behavior. The F# tokenizer does not perform
        // canonical BERT NFD + Mn accent stripping, so accented words collapse
        // to UNK. With the bert-base-uncased vocab that bge-small reuses,
        // "café"/"résumé" miss every vocab entry (their stripped forms
        // "cafe"=7668 / "resume"=13746 DO exist) and each whole word collapses
        // to a single UNK (100): the result is [CLS=101; UNK; UNK; SEP=102].
        // This is a known fidelity gap — pre-existing, and it affected the prior
        // model equally — pinned here so that any future change to accent
        // handling (e.g. adding NFD + Mn removal, as canonical BERT
        // preprocessing does) is a conscious decision rather than a silent shift
        // of token IDs across the embedding pipeline.
        testCase "tokenize accented input falls through to UNK (known gap vs canonical BERT accent-stripping)" <| fun _ ->
            let vocab = FixtureLoader.loadVocab()
            let actual = Tokenizer.tokenize vocab "café résumé" |> List.toArray
            Expect.equal actual [| 101; 100; 100; 102 |]
                "accented words must fall through to UNK (no accent stripping)"
    ]
