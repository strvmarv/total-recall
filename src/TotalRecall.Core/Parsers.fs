module TotalRecall.Core.Parsers

// Regex-based code and markdown parsers ported from
// src-ts/ingestion/code-parser.ts and src-ts/ingestion/markdown-parser.ts.
//
// Pure functions over input text and options.

open System
open System.Text.RegularExpressions

// --- shared helpers ---

let private wordSplitRe = Regex(@"\s+", RegexOptions.Compiled)

let private estimateTokens (text: string) : int =
    let trimmed = text.Trim()
    if trimmed.Length = 0 then 0
    else
        let words =
            wordSplitRe.Split(trimmed)
            |> Array.filter (fun w -> w.Length > 0)
        int (ceil (float words.Length * 0.75))

// --- code parser ---

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

type CodeParserOptions = {
    MaxTokens: int
}

type private LanguagePatterns = {
    Boundary: Regex
    ImportLine: Regex
    ExtractName: string -> string
    ClassifyKind: string -> CodeChunkKind
}

let private rxOpts = RegexOptions.Compiled

let private extractByPatterns (patterns: Regex list) (line: string) : string =
    patterns
    |> List.tryPick (fun re ->
        let m = re.Match(line)
        if m.Success && m.Groups.Count > 1 then Some m.Groups.[1].Value
        else None)
    |> Option.defaultValue ""

let private classifyTsJs (line: string) : CodeChunkKind =
    if Regex.IsMatch(line, @"class\s+") then Class
    elif Regex.IsMatch(line, @"function\s+|=\s*(async\s+)?\(|=\s*(async\s+)?function") then Function
    else Block

let private classifyPython (line: string) : CodeChunkKind =
    if Regex.IsMatch(line, @"^class\s+") then Class
    elif Regex.IsMatch(line, @"(?:async\s+)?def\s+") then Function
    else Block

let private classifyRust (line: string) : CodeChunkKind =
    if Regex.IsMatch(line, @"struct\s+") || Regex.IsMatch(line, @"impl\s+") then Class
    elif Regex.IsMatch(line, @"fn\s+") then Function
    else Block

let rec private patternsFor (language: string) : LanguagePatterns =
    match language with
    | "typescript" | "javascript" ->
        let importLine =
            if language = "javascript" then
                Regex(@"^\s*import\s|^\s*const\s+\w+\s*=\s*require\(", rxOpts)
            else
                Regex(@"^\s*import\s", rxOpts)
        let classBoundary =
            if language = "javascript" then "class"
            else "(abstract\\s+)?class"
        {
            Boundary =
                Regex(
                    sprintf "^(export\\s+)?(async\\s+)?function\\s+\\w+|^(export\\s+)?(%s)\\s+\\w+|^(export\\s+)?const\\s+\\w+\\s*=\\s*(async\\s+)?\\(|^(export\\s+)?const\\s+\\w+\\s*=\\s*(async\\s+)?function" classBoundary,
                    rxOpts)
            ImportLine = importLine
            ExtractName = extractByPatterns [
                Regex(@"function\s+(\w+)", rxOpts)
                Regex(@"class\s+(\w+)", rxOpts)
                Regex(@"const\s+(\w+)", rxOpts)
            ]
            ClassifyKind = classifyTsJs
        }
    | "python" ->
        {
            Boundary = Regex(@"^(async\s+)?def\s+\w+|^class\s+\w+", rxOpts)
            ImportLine = Regex(@"^\s*import\s|^\s*from\s+\S+\s+import\s", rxOpts)
            ExtractName = extractByPatterns [ Regex(@"(?:def|class)\s+(\w+)", rxOpts) ]
            ClassifyKind = classifyPython
        }
    | "go" ->
        {
            Boundary = Regex(@"^func\s+", rxOpts)
            ImportLine = Regex(@"^\s*import\s|^\s*""[\w/]+""", rxOpts)
            ExtractName = extractByPatterns [ Regex(@"func\s+(?:\(\w+\s+\*?\w+\)\s+)?(\w+)", rxOpts) ]
            ClassifyKind = fun _ -> Function
        }
    | "rust" ->
        {
            Boundary = Regex(@"^(pub\s+)?(async\s+)?fn\s+\w+|^(pub\s+)?struct\s+\w+|^(pub\s+)?impl\s+\w+", rxOpts)
            ImportLine = Regex(@"^\s*use\s", rxOpts)
            ExtractName = extractByPatterns [
                Regex(@"fn\s+(\w+)", rxOpts)
                Regex(@"struct\s+(\w+)", rxOpts)
                Regex(@"impl\s+(\w+)", rxOpts)
            ]
            ClassifyKind = classifyRust
        }
    | _ -> patternsFor "typescript"

// findNonImportStart: index of first non-import, non-blank line at the top.
// "Blank lines following imports" are absorbed into the import block.
let private findNonImportStart (lines: string[]) (patterns: LanguagePatterns) : int =
    let mutable lastImportOrBlank = 0
    let mutable seenImport = false
    let mutable stop = false
    let mutable i = 0
    while i < lines.Length && not stop do
        let line = lines.[i]
        if line.Trim() = "" then
            if seenImport then lastImportOrBlank <- i + 1
            i <- i + 1
        elif patterns.ImportLine.IsMatch(line) then
            seenImport <- true
            lastImportOrBlank <- i + 1
            i <- i + 1
        else
            stop <- true
    lastImportOrBlank

let private splitAtBlankLines
    (lines: string[])
    (startIdx: int)
    (name: string)
    (kind: CodeChunkKind)
    (maxTokens: int)
    : CodeChunk list =
    let chunks = ResizeArray<CodeChunk>()
    let mutable currentLines = ResizeArray<string>()
    let mutable currentOffset = 0

    let flush () =
        if currentLines.Count > 0 && (currentLines |> Seq.exists (fun l -> l.Trim() <> "")) then
            chunks.Add({
                Content = String.Join("\n", currentLines)
                Name = name
                Kind = kind
                StartLine = startIdx + currentOffset + 1
                EndLine = startIdx + currentOffset + currentLines.Count
            })
        currentLines <- ResizeArray<string>()

    for i in 0 .. lines.Length - 1 do
        let line = lines.[i]
        currentLines.Add(line)
        if line.Trim() = "" then
            let tokens = estimateTokens (String.Join("\n", currentLines))
            if tokens >= maxTokens then
                flush()
                currentOffset <- i + 1
    flush()

    if chunks.Count > 0 then
        chunks |> List.ofSeq
    else
        [{
            Content = String.Join("\n", lines)
            Name = name
            Kind = kind
            StartLine = startIdx + 1
            EndLine = startIdx + lines.Length
        }]

type private Segment = {
    Lines: string[]
    StartIdx: int
    Name: string
    Kind: CodeChunkKind
}

let parseCode (language: string) (source: string) (opts: CodeParserOptions) : CodeChunk list =
    if String.IsNullOrWhiteSpace(source) then []
    else
        let patterns = patternsFor language
        let maxTokens = opts.MaxTokens
        let lines = source.Split('\n')
        let nonImportStartIdx = findNonImportStart lines patterns

        // Pass 2: collect boundary segments
        let segments = ResizeArray<Segment>()
        let mutable currentLines = ResizeArray<string>()
        let mutable currentStart = nonImportStartIdx
        let mutable currentName = ""
        let mutable currentKind = Block

        let flushSegment () =
            if currentLines.Count > 0 && (currentLines |> Seq.exists (fun l -> l.Trim() <> "")) then
                segments.Add({
                    Lines = currentLines.ToArray()
                    StartIdx = currentStart
                    Name = currentName
                    Kind = currentKind
                })

        for i in nonImportStartIdx .. lines.Length - 1 do
            let line = lines.[i]
            if patterns.Boundary.IsMatch(line) then
                flushSegment()
                currentLines <- ResizeArray<string>()
                currentLines.Add(line)
                currentStart <- i
                currentName <- patterns.ExtractName line
                currentKind <- patterns.ClassifyKind line
            else
                currentLines.Add(line)
        flushSegment()

        let chunks = ResizeArray<CodeChunk>()

        // Emit import chunk
        if nonImportStartIdx > 0 then
            let importLines =
                lines
                |> Array.take nonImportStartIdx
                |> String.concat "\n"
            chunks.Add({
                Content = importLines
                Name = "imports"
                Kind = Import
                StartLine = 1
                EndLine = nonImportStartIdx
            })

        // Emit code segments, splitting oversized at blank lines
        for seg in segments do
            let segText = String.Join("\n", seg.Lines)
            if estimateTokens segText <= maxTokens then
                chunks.Add({
                    Content = segText
                    Name = seg.Name
                    Kind = seg.Kind
                    StartLine = seg.StartIdx + 1
                    EndLine = seg.StartIdx + seg.Lines.Length
                })
            else
                let subChunks = splitAtBlankLines seg.Lines seg.StartIdx seg.Name seg.Kind maxTokens
                chunks.AddRange(subChunks)

        chunks |> List.ofSeq

// --- markdown parser ---

type MarkdownChunk = {
    Content: string
    HeadingPath: string list
    StartLine: int
    EndLine: int
}

type MarkdownParserOptions = {
    MaxTokens: int
    OverlapTokens: int option
}

type private MarkdownSection = {
    HeadingPath: string list
    Lines: string[]
    StartLine: int
}

type private AtomicBlock = {
    Lines: string[]
    LineOffset: int
}

let private headingRe = Regex(@"^(#{1,6})\s+(.+)$", rxOpts)
let private codeFenceRe = Regex(@"^```", rxOpts)
let private codeFenceCloseRe = Regex(@"^```\s*$", rxOpts)

let private splitSection (section: MarkdownSection) (maxTokens: int) : MarkdownChunk list =
    let lines = section.Lines
    let startLine = section.StartLine
    let headingPath = section.HeadingPath

    let blocks = ResizeArray<AtomicBlock>()
    let mutable i = 0
    while i < lines.Length do
        let line = lines.[i]
        if codeFenceRe.IsMatch(line) then
            let blockLines = ResizeArray<string>()
            blockLines.Add(line)
            let offset = i
            i <- i + 1
            let mutable closed = false
            while i < lines.Length && not closed do
                let inner = lines.[i]
                blockLines.Add(inner)
                i <- i + 1
                if codeFenceCloseRe.IsMatch(inner) then closed <- true
            blocks.Add({ Lines = blockLines.ToArray(); LineOffset = offset })
        else
            let blockLines = ResizeArray<string>()
            let offset = i
            let mutable stop = false
            while i < lines.Length && not stop && not (codeFenceRe.IsMatch(lines.[i])) do
                blockLines.Add(lines.[i])
                i <- i + 1
                if blockLines.[blockLines.Count - 1].Trim() = "" then stop <- true
            if blockLines.Count > 0 then
                blocks.Add({ Lines = blockLines.ToArray(); LineOffset = offset })

    let chunks = ResizeArray<MarkdownChunk>()
    let mutable currentBlockLines = ResizeArray<string>()
    let mutable currentOffset = 0

    let flushChunk () =
        if currentBlockLines.Count > 0 then
            let content = String.Join("\n", currentBlockLines)
            chunks.Add({
                Content = content
                HeadingPath = headingPath
                StartLine = startLine + currentOffset
                EndLine = startLine + currentOffset + currentBlockLines.Count - 1
            })
            currentBlockLines <- ResizeArray<string>()

    for block in blocks do
        let blockText = String.Join("\n", block.Lines)
        let blockTokens = estimateTokens blockText
        let currentTokens =
            if currentBlockLines.Count = 0 then 0
            else estimateTokens (String.Join("\n", currentBlockLines))

        if currentBlockLines.Count = 0 then
            currentBlockLines <- ResizeArray<string>(block.Lines)
            currentOffset <- block.LineOffset
        elif currentTokens + blockTokens <= maxTokens then
            currentBlockLines.AddRange(block.Lines)
        else
            flushChunk()
            currentBlockLines <- ResizeArray<string>(block.Lines)
            currentOffset <- block.LineOffset
    flushChunk()

    chunks |> List.ofSeq

let parseMarkdown (source: string) (opts: MarkdownParserOptions) : MarkdownChunk list =
    if String.IsNullOrWhiteSpace(source) then []
    else
        let maxTokens = opts.MaxTokens
        let allLines = source.Split('\n')

        let sections = ResizeArray<MarkdownSection>()
        let mutable currentHeadingPath: string list = []
        let mutable currentLines = ResizeArray<string>()
        let mutable currentStartLine = 1

        let flushSection () =
            if currentLines.Count > 0 then
                sections.Add({
                    HeadingPath = currentHeadingPath
                    Lines = currentLines.ToArray()
                    StartLine = currentStartLine
                })

        for i in 0 .. allLines.Length - 1 do
            let line = allLines.[i]
            let m = headingRe.Match(line)
            if m.Success then
                flushSection()
                let level = m.Groups.[1].Value.Length
                let title = m.Groups.[2].Value.Trim()
                // Truncate path to (level - 1), then append title at index (level - 1)
                let truncated = currentHeadingPath |> List.truncate (level - 1)
                let padded =
                    if truncated.Length < level - 1 then
                        truncated @ List.replicate (level - 1 - truncated.Length) ""
                    else truncated
                currentHeadingPath <- padded @ [title]
                currentLines <- ResizeArray<string>()
                currentLines.Add(line)
                currentStartLine <- i + 1
            else
                currentLines.Add(line)
        flushSection()

        let chunks = ResizeArray<MarkdownChunk>()
        for section in sections do
            let sectionText = String.Join("\n", section.Lines)
            if estimateTokens sectionText <= maxTokens then
                chunks.Add({
                    Content = sectionText
                    HeadingPath = section.HeadingPath
                    StartLine = section.StartLine
                    EndLine = section.StartLine + section.Lines.Length - 1
                })
            else
                let subChunks = splitSection section maxTokens
                chunks.AddRange(subChunks)

        chunks |> List.ofSeq
