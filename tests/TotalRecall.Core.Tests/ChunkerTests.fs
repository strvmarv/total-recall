module TotalRecall.Core.Tests.ChunkerTests

open Expecto
open TotalRecall.Core
open TotalRecall.Core.Chunker
open TotalRecall.Core.Parsers

let private opts : ChunkerOptions = { MaxTokens = 500; OverlapTokens = None }

[<Tests>]
let chunkerTests =
    testList "Chunker" [
        testCase "empty content -> empty list" <| fun _ ->
            Expect.isEmpty (chunkFile "" "/path/to/file.md" opts) "empty content should produce no chunks"

        testCase "whitespace-only content -> empty list" <| fun _ ->
            Expect.isEmpty (chunkFile "   \n\n  " "/path/to/file.txt" opts) "whitespace should produce no chunks"

        testCase ".md dispatches to markdown parser (heading path populated)" <| fun _ ->
            let content = "# Title\n\nSome text.\n"
            let chunks = chunkFile content "doc.md" opts
            Expect.equal chunks.Length 1 "should produce 1 chunk"
            Expect.equal chunks.[0].HeadingPath (Some ["Title"]) "heading path should be Some"
            Expect.equal chunks.[0].Name None "name should be None for markdown"
            Expect.equal chunks.[0].Kind None "kind should be None for markdown"

        testCase ".mdx is treated as markdown" <| fun _ ->
            let chunks = chunkFile "# Hi\nbody" "page.mdx" opts
            Expect.equal chunks.[0].HeadingPath (Some ["Hi"]) "mdx should produce heading path"

        testCase ".ts dispatches to code parser (function detected)" <| fun _ ->
            let chunks = chunkFile "function foo() { return 1; }\n" "src/foo.ts" opts
            Expect.equal chunks.Length 1 "should produce 1 chunk"
            Expect.equal chunks.[0].Name (Some "foo") "name should be foo"
            Expect.equal chunks.[0].Kind (Some Function) "kind should be Function"
            Expect.equal chunks.[0].HeadingPath None "heading path should be None for code"

        testCase ".py dispatches to code parser (def detected)" <| fun _ ->
            let chunks = chunkFile "def hello():\n    return 1\n" "main.py" opts
            Expect.equal chunks.Length 1 "should produce 1 chunk"
            Expect.equal chunks.[0].Name (Some "hello") "name should be hello"
            Expect.equal chunks.[0].Kind (Some Function) "kind should be Function"

        testCase ".go dispatches to code parser" <| fun _ ->
            let chunks = chunkFile "func DoStuff() {}\n" "main.go" opts
            Expect.equal chunks.[0].Name (Some "DoStuff") "name should be extracted"

        testCase "unknown extension falls back to paragraph splitting" <| fun _ ->
            let content = "First paragraph.\n\nSecond paragraph.\n\nThird paragraph.\n"
            let chunks = chunkFile content "notes.txt" opts
            Expect.isNonEmpty chunks "should produce at least 1 chunk"
            // Paragraph splitting produces no heading path / name / kind
            for c in chunks do
                Expect.equal c.HeadingPath None "fallback chunks have no heading path"
                Expect.equal c.Name None "fallback chunks have no name"
                Expect.equal c.Kind None "fallback chunks have no kind"

        testCase "no extension falls back to paragraph splitting" <| fun _ ->
            let chunks = chunkFile "Just some text.\n" "README" opts
            Expect.equal chunks.Length 1 "should produce 1 chunk"
            Expect.equal chunks.[0].HeadingPath None "no heading path expected"

        testCase "extension comparison is case-insensitive" <| fun _ ->
            let chunks = chunkFile "# Hi\nbody" "doc.MD" opts
            Expect.equal chunks.[0].HeadingPath (Some ["Hi"]) ".MD should be treated as markdown"
    ]
