module TotalRecall.Core.Tests.ParsersTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.Parsers

// Tests for the regex-based code and markdown parsers.
// Ported from src-ts/ingestion/code-parser.ts and markdown-parser.ts.
// Verifies basic shape; not exhaustive — sufficient for the F# port to
// be confidence-checked against representative inputs.

let private codeOpts = { MaxTokens = 500 }
let private mdOpts = { MaxTokens = 500; OverlapTokens = None }

[<Tests>]
let parsersTests =
    testList "Parsers" [
        // --- code parser ---

        testCase "parseCode empty input -> empty list" <| fun _ ->
            let chunks = parseCode "typescript" "" codeOpts
            Expect.isEmpty chunks "empty source should produce no chunks"

        testCase "parseCode whitespace only -> empty list" <| fun _ ->
            let chunks = parseCode "typescript" "   \n\n  " codeOpts
            Expect.isEmpty chunks "whitespace-only source should produce no chunks"

        testCase "parseCode typescript single function" <| fun _ ->
            let src = "function foo() {\n  return 1;\n}\n"
            let chunks = parseCode "typescript" src codeOpts
            Expect.equal chunks.Length 1 "single function should produce 1 chunk"
            let c = chunks.[0]
            Expect.equal c.Name "foo" "function name should be extracted"
            Expect.equal c.Kind Function "kind should be Function"

        testCase "parseCode typescript single class" <| fun _ ->
            let src = "class Bar {\n  baz() {}\n}\n"
            let chunks = parseCode "typescript" src codeOpts
            Expect.equal chunks.Length 1 "single class should produce 1 chunk"
            Expect.equal chunks.[0].Name "Bar" "class name should be extracted"
            Expect.equal chunks.[0].Kind Class "kind should be Class"

        testCase "parseCode typescript imports + function" <| fun _ ->
            let src =
                "import { foo } from \"./foo\";\n" +
                "import { bar } from \"./bar\";\n" +
                "\n" +
                "function main() {\n" +
                "  return foo() + bar();\n" +
                "}\n"
            let chunks = parseCode "typescript" src codeOpts
            Expect.equal chunks.Length 2 "should produce import chunk + function chunk"
            Expect.equal chunks.[0].Kind Import "first chunk should be the import block"
            Expect.equal chunks.[0].Name "imports" "import chunk name should be 'imports'"
            Expect.equal chunks.[1].Kind Function "second chunk should be the function"
            Expect.equal chunks.[1].Name "main" "function name should be 'main'"

        testCase "parseCode python def" <| fun _ ->
            let src = "def hello():\n    return 1\n"
            let chunks = parseCode "python" src codeOpts
            Expect.equal chunks.Length 1 "single def should produce 1 chunk"
            Expect.equal chunks.[0].Name "hello" "def name should be extracted"
            Expect.equal chunks.[0].Kind Function "kind should be Function"

        testCase "parseCode python class" <| fun _ ->
            let src = "class Foo:\n    pass\n"
            let chunks = parseCode "python" src codeOpts
            Expect.equal chunks.Length 1 "single class should produce 1 chunk"
            Expect.equal chunks.[0].Name "Foo" "class name should be extracted"
            Expect.equal chunks.[0].Kind Class "kind should be Class"

        testCase "parseCode go func" <| fun _ ->
            let src = "func DoStuff() error {\n    return nil\n}\n"
            let chunks = parseCode "go" src codeOpts
            Expect.equal chunks.Length 1 "single func should produce 1 chunk"
            Expect.equal chunks.[0].Name "DoStuff" "func name should be extracted"
            Expect.equal chunks.[0].Kind Function "go always classifies as Function"

        testCase "parseCode rust struct" <| fun _ ->
            let src = "struct Point {\n    x: i32,\n    y: i32,\n}\n"
            let chunks = parseCode "rust" src codeOpts
            Expect.equal chunks.Length 1 "single struct should produce 1 chunk"
            Expect.equal chunks.[0].Name "Point" "struct name should be extracted"
            Expect.equal chunks.[0].Kind Class "rust struct kind should be Class"

        testCase "parseCode multiple top-level constructs preserves order and line numbers" <| fun _ ->
            let src =
                "function alpha() {\n" +     // line 1
                "  return 1;\n" +             // line 2
                "}\n" +                       // line 3
                "\n" +                        // line 4
                "function beta() {\n" +       // line 5
                "  return 2;\n" +             // line 6
                "}\n"                         // line 7
            let chunks = parseCode "typescript" src codeOpts
            Expect.equal chunks.Length 2 "should produce 2 function chunks"
            Expect.equal chunks.[0].Name "alpha" "first should be alpha"
            Expect.equal chunks.[1].Name "beta" "second should be beta"
            Expect.equal chunks.[0].StartLine 1 "alpha starts at line 1"
            Expect.equal chunks.[1].StartLine 5 "beta starts at line 5"

        // --- markdown parser ---

        testCase "parseMarkdown empty -> empty list" <| fun _ ->
            let chunks = parseMarkdown "" mdOpts
            Expect.isEmpty chunks "empty source should produce no chunks"

        testCase "parseMarkdown no headings -> single chunk" <| fun _ ->
            let src = "Just some plain text.\nWith two lines.\n"
            let chunks = parseMarkdown src mdOpts
            Expect.equal chunks.Length 1 "no-heading source should produce 1 chunk"
            Expect.isEmpty chunks.[0].HeadingPath "heading path should be empty"

        testCase "parseMarkdown single H1 -> chunk with one-element heading path" <| fun _ ->
            let src = "# Title\n\nSome content.\n"
            let chunks = parseMarkdown src mdOpts
            Expect.equal chunks.Length 1 "should produce 1 chunk"
            Expect.equal chunks.[0].HeadingPath ["Title"] "heading path should be [Title]"

        testCase "parseMarkdown nested headings -> nested heading paths" <| fun _ ->
            let src =
                "# Top\n" +
                "intro\n" +
                "## Sub\n" +
                "sub content\n" +
                "### Deep\n" +
                "deep content\n"
            let chunks = parseMarkdown src mdOpts
            Expect.equal chunks.Length 3 "3 sections expected"
            Expect.equal chunks.[0].HeadingPath ["Top"] "first chunk under Top"
            Expect.equal chunks.[1].HeadingPath ["Top"; "Sub"] "second chunk under Top/Sub"
            Expect.equal chunks.[2].HeadingPath ["Top"; "Sub"; "Deep"] "third chunk under Top/Sub/Deep"

        testCase "parseMarkdown sibling H2 sections share H1 prefix" <| fun _ ->
            let src =
                "# Root\n" +
                "## A\n" +
                "first\n" +
                "## B\n" +
                "second\n"
            let chunks = parseMarkdown src mdOpts
            Expect.equal chunks.Length 3 "3 sections expected"
            Expect.equal chunks.[0].HeadingPath ["Root"] "Root chunk"
            Expect.equal chunks.[1].HeadingPath ["Root"; "A"] "A chunk under Root"
            Expect.equal chunks.[2].HeadingPath ["Root"; "B"] "B chunk under Root (sibling to A)"
    ]
