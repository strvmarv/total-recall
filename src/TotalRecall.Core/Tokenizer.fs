module TotalRecall.Core.Tokenizer

// WordPiece tokenizer ported from src-ts/embedding/tokenizer.ts.
//
// Pure function over input text → list of token IDs. The implementation
// mirrors the TS algorithm exactly (Plan 2 Option-A pivot — TS is the
// oracle for the F# port; tokenizer-correctness research is deferred).
//
// Algorithm:
// 1. Normalize: drop control chars, pad CJK with spaces, lowercase.
// 2. Pre-tokenize: split on whitespace and punctuation.
// 3. WordPiece: greedy longest-match against vocab with ## continuation prefix.
// 4. Prepend CLS (101), append SEP (102), truncate to MAX_SEQ_LEN (512).
// 5. OOV words → UNK (100). Words longer than MAX_INPUT_CHARS_PER_WORD (100) → UNK.

let [<Literal>] private ClsTokenId = 101
let [<Literal>] private SepTokenId = 102
let [<Literal>] private UnkTokenId = 100
let [<Literal>] private MaxSeqLen = 512
let [<Literal>] private MaxInputCharsPerWord = 100

type Vocab = Map<string, int>

// --- character classification ---

let private isWhitespace (cp: int) : bool =
    cp = 0x20 || cp = 0x09 || cp = 0x0A || cp = 0x0D

let private isControl (cp: int) : bool =
    if cp = 0x09 || cp = 0x0A || cp = 0x0D then false
    else
        // Mirrors the TS charCategory() function: Cc and Cf categories.
        cp <= 0x1F
        || (cp >= 0x7F && cp <= 0x9F)
        || cp = 0xAD
        || (cp >= 0x600 && cp <= 0x605)
        || cp = 0x61C
        || cp = 0x6DD
        || cp = 0x70F
        || cp = 0xFEFF
        || (cp >= 0xFFF9 && cp <= 0xFFFB)
        || (cp >= 0x200B && cp <= 0x200F)
        || (cp >= 0x202A && cp <= 0x202E)
        || (cp >= 0x2060 && cp <= 0x2064)
        || (cp >= 0x2066 && cp <= 0x2069)

let private isCjk (cp: int) : bool =
    (cp >= 0x4E00 && cp <= 0x9FFF)
    || (cp >= 0x3400 && cp <= 0x4DBF)
    || (cp >= 0x20000 && cp <= 0x2A6DF)
    || (cp >= 0x2A700 && cp <= 0x2B73F)
    || (cp >= 0x2B740 && cp <= 0x2B81F)
    || (cp >= 0x2B820 && cp <= 0x2CEAF)
    || (cp >= 0xF900 && cp <= 0xFAFF)
    || (cp >= 0x2F800 && cp <= 0x2FA1F)

let private isPunctuation (cp: int) : bool =
    // ASCII punctuation ranges (matches TS isPunctuation exactly).
    if (cp >= 33 && cp <= 47)
       || (cp >= 58 && cp <= 64)
       || (cp >= 91 && cp <= 96)
       || (cp >= 123 && cp <= 126)
    then true
    else
        // Unicode P category (matches TS /^\p{P}$/u check).
        let s = System.Char.ConvertFromUtf32(cp)
        if s.Length = 1 then
            System.Char.IsPunctuation(s.[0])
        else
            // Surrogate pair. Use the rune-based check.
            let rune = System.Text.Rune(cp)
            System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value)
            |> fun cat ->
                cat = System.Globalization.UnicodeCategory.ConnectorPunctuation
                || cat = System.Globalization.UnicodeCategory.DashPunctuation
                || cat = System.Globalization.UnicodeCategory.OpenPunctuation
                || cat = System.Globalization.UnicodeCategory.ClosePunctuation
                || cat = System.Globalization.UnicodeCategory.InitialQuotePunctuation
                || cat = System.Globalization.UnicodeCategory.FinalQuotePunctuation
                || cat = System.Globalization.UnicodeCategory.OtherPunctuation

// --- code-point iteration helper ---

let private codePoints (text: string) : seq<int> =
    seq {
        let mutable i = 0
        while i < text.Length do
            if System.Char.IsHighSurrogate(text.[i]) && i + 1 < text.Length then
                yield System.Char.ConvertToUtf32(text.[i], text.[i + 1])
                i <- i + 2
            else
                yield int text.[i]
                i <- i + 1
    }

// --- normalize ---

let private normalize (text: string) : string =
    let sb = System.Text.StringBuilder()
    for cp in codePoints text do
        if isControl cp && not (isWhitespace cp) then
            () // drop
        elif isCjk cp then
            sb.Append(' ') |> ignore
            sb.Append(System.Char.ConvertFromUtf32(cp)) |> ignore
            sb.Append(' ') |> ignore
        else
            sb.Append(System.Char.ConvertFromUtf32(cp)) |> ignore
    sb.ToString().ToLowerInvariant()

// --- pre-tokenize ---

let private preTokenize (text: string) : string list =
    let tokens = ResizeArray<string>()
    let current = System.Text.StringBuilder()
    let flushCurrent () =
        if current.Length > 0 then
            tokens.Add(current.ToString())
            current.Clear() |> ignore
    for cp in codePoints text do
        let ch = System.Char.ConvertFromUtf32(cp)
        if isWhitespace cp then
            flushCurrent()
        elif isPunctuation cp then
            flushCurrent()
            tokens.Add(ch)
        else
            current.Append(ch) |> ignore
    flushCurrent()
    tokens |> List.ofSeq

// --- wordpiece ---

let private wordPiece (vocab: Vocab) (word: string) : int list =
    if word.Length > MaxInputCharsPerWord then
        [UnkTokenId]
    else
        let ids = ResizeArray<int>()
        let mutable start = 0
        let mutable failed = false
        while start < word.Length && not failed do
            let mutable matched = false
            let mutable endIdx = word.Length
            while start < endIdx && not matched do
                let substr =
                    if start = 0 then
                        word.Substring(0, endIdx)
                    else
                        "##" + word.Substring(start, endIdx - start)
                match Map.tryFind substr vocab with
                | Some id ->
                    ids.Add(id)
                    start <- endIdx
                    matched <- true
                | None ->
                    endIdx <- endIdx - 1
            if not matched then
                failed <- true
        if failed then [UnkTokenId]
        else ids |> List.ofSeq

// --- public tokenize ---

let tokenize (vocab: Vocab) (text: string) : int list =
    let normalized = normalize text
    let words = preTokenize normalized
    let ids = ResizeArray<int>()
    ids.Add(ClsTokenId)
    let mutable stop = false
    for word in words do
        if not stop then
            if ids.Count >= MaxSeqLen - 1 then
                stop <- true
            else
                let subIds = wordPiece vocab word
                for id in subIds do
                    if ids.Count < MaxSeqLen - 1 then
                        ids.Add(id)
                    else
                        stop <- true
    ids.Add(SepTokenId)
    ids |> List.ofSeq
