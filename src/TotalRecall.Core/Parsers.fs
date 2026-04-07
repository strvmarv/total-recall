module TotalRecall.Core.Parsers

// Regex-based code and markdown parsers for KB ingestion.
// Ported from src-ts/ingestion/code-parser.ts and markdown-parser.ts.

type CodeChunkKind =
    | Import
    | Function
    | Class
    | Block

type CodeChunk = {
    Content: string
    Name: string
    Kind: CodeChunkKind
    StartLine: int
    EndLine: int
}

/// Parse source code into CodeChunks using language-specific patterns.
let parseCode (language: string) (source: string) : CodeChunk list =
    failwith "TotalRecall.Core.Parsers.parseCode not yet implemented (Plan 2 Task 2.10)"

type MarkdownChunk = {
    Content: string
    HeadingPath: string list
    StartLine: int
    EndLine: int
}

/// Parse markdown into chunks segmented by heading hierarchy.
let parseMarkdown (source: string) : MarkdownChunk list =
    failwith "TotalRecall.Core.Parsers.parseMarkdown not yet implemented (Plan 2 Task 2.10)"
