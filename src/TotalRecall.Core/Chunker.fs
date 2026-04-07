module TotalRecall.Core.Chunker

// File-extension-based chunking dispatcher.
// Ported from src-ts/ingestion/chunker.ts.
//
// Dispatches to:
//   - parseMarkdown for .md, .mdx, .markdown
//   - parseCode for .ts, .tsx, .js, .jsx, .py, .go, .rs (with the right language)
//   - splitByParagraphs for everything else (plain text fallback)
//
// Produces a unified Chunk record that's a superset of MarkdownChunk and
// CodeChunk shapes (HeadingPath/Name/Kind are optional).

open System
open System.Text.RegularExpressions
open TotalRecall.Core.Parsers

/// A unified chunk produced by chunkFile. The optional fields populate
/// based on which underlying parser produced it.
type Chunk = {
    Content: string
    HeadingPath: string list option
    Name: string option
    Kind: CodeChunkKind option
    StartLine: int
    EndLine: int
}

type ChunkerOptions = {
    MaxTokens: int
    OverlapTokens: int option
}

let private markdownExtensions = Set.ofList [".md"; ".mdx"; ".markdown"]

let private codeLanguageMap : Map<string, string> =
    Map.ofList [
        ".ts", "typescript"
        ".tsx", "typescript"
        ".js", "javascript"
        ".jsx", "javascript"
        ".py", "python"
        ".go", "go"
        ".rs", "rust"
    ]

let private getExtension (filePath: string) : string =
    let baseName =
        let lastSlash = max (filePath.LastIndexOf('/')) (filePath.LastIndexOf('\\'))
        if lastSlash = -1 then filePath
        else filePath.Substring(lastSlash + 1)
    let dotIdx = baseName.LastIndexOf('.')
    if dotIdx = -1 then ""
    else baseName.Substring(dotIdx).ToLowerInvariant()

let private wordSplitRe = Regex(@"\s+", RegexOptions.Compiled)
let private blankLineRe = Regex(@"\n\n+", RegexOptions.Compiled)

let private estimateTokens (text: string) : int =
    let trimmed = text.Trim()
    if trimmed.Length = 0 then 0
    else
        let words =
            wordSplitRe.Split(trimmed)
            |> Array.filter (fun w -> w.Length > 0)
        int (ceil (float words.Length * 0.75))

let private splitByParagraphs (content: string) (maxTokens: int) : Chunk list =
    let paragraphs = blankLineRe.Split(content)
    let chunks = ResizeArray<Chunk>()

    let mutable currentParts = ResizeArray<string>()
    let mutable currentStartLine = 1
    let mutable lineCount = 1

    let flushCurrent () =
        if currentParts.Count > 0 then
            let content = String.Join("\n\n", currentParts)
            let contentLines = content.Split('\n').Length
            chunks.Add({
                Content = content
                HeadingPath = None
                Name = None
                Kind = None
                StartLine = currentStartLine
                EndLine = currentStartLine + contentLines - 1
            })
            currentParts <- ResizeArray<string>()

    for para in paragraphs do
        let paraLines = para.Split('\n').Length
        let paraTokens = estimateTokens para
        let currentTokens =
            if currentParts.Count = 0 then 0
            else estimateTokens (String.Join("\n\n", currentParts))

        if currentParts.Count = 0 then
            currentParts.Add(para)
            currentStartLine <- lineCount
        elif currentTokens + paraTokens <= maxTokens then
            currentParts.Add(para)
        else
            flushCurrent()
            currentParts.Add(para)
            currentStartLine <- lineCount

        lineCount <- lineCount + paraLines + 1  // +1 for blank line separator

    flushCurrent()
    chunks |> List.ofSeq

let chunkFile (content: string) (filePath: string) (opts: ChunkerOptions) : Chunk list =
    if String.IsNullOrWhiteSpace(content) then []
    else
        let ext = getExtension filePath
        if Set.contains ext markdownExtensions then
            let mdOpts : MarkdownParserOptions = { MaxTokens = opts.MaxTokens; OverlapTokens = opts.OverlapTokens }
            parseMarkdown content mdOpts
            |> List.map (fun c -> {
                Content = c.Content
                HeadingPath = Some c.HeadingPath
                Name = None
                Kind = None
                StartLine = c.StartLine
                EndLine = c.EndLine
            })
        else
            match Map.tryFind ext codeLanguageMap with
            | Some language ->
                let codeOpts : CodeParserOptions = { MaxTokens = opts.MaxTokens }
                parseCode language content codeOpts
                |> List.map (fun c -> {
                    Content = c.Content
                    HeadingPath = None
                    Name = Some c.Name
                    Kind = Some c.Kind
                    StartLine = c.StartLine
                    EndLine = c.EndLine
                })
            | None ->
                splitByParagraphs content opts.MaxTokens
