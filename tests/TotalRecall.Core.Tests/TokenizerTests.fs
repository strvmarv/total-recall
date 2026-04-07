module TotalRecall.Core.Tests.TokenizerTests

open Expecto

// First behavioral test for the canonical BERT tokenizer.
//
// This test is INTENTIONALLY RED in Plan 1. Plan 2 implements
// TotalRecall.Core.Tokenizer and turns it green.
//
// The test asserts canonical BERT BasicTokenization behavior:
// "snake_case" should tokenize as a single wordpiece chain, NOT
// split on the underscore. The current TS implementation splits on
// underscores (non-canonical); the .NET rewrite uses the canonical
// behavior, which matches the model's training distribution.
//
// Once TotalRecall.Core.Tokenizer.tokenize is implemented in Plan 2,
// remove the skiptest call and the test should go green automatically.

[<Tests>]
let tokenizerTests =
    testList "Tokenizer" [
        testCase "snake_case is not split on underscore (canonical BERT)" <| fun _ ->
            // Pending: TotalRecall.Core.Tokenizer.tokenize does not exist yet.
            // Plan 2 implements it.
            skiptest "TotalRecall.Core.Tokenizer not yet implemented (Plan 2)"

            // When Plan 2 lands, the body becomes:
            //
            //   let tokens = TotalRecall.Core.Tokenizer.tokenize "snake_case"
            //   let tokenStrings = tokens |> List.map (fun t -> t.Text)
            //   Expect.equal tokenStrings ["snake_case"]
            //       "snake_case should tokenize as a single wordpiece chain"
    ]
