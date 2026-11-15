module TotalRecall.Core.SyncValidation

// Rules for accepting a record read from the sync repository (a trust boundary) and for
// the limits the writer enforces (milestone 0b-1). Pure.

open System
open System.Text

let private isLowerHex (c: char) = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

// ---- ids and paths ----

/// Length 8 to 64; the first character `0-9a-f`; every other `0-9a-f` or `-`. ASCII only, no regex.
let isEligibleId (id: string) : bool =
    if isNull id || id.Length < 8 || id.Length > 64 then false
    else
        let mutable ok = isLowerHex id.[0]
        for i in 1 .. id.Length - 1 do
            if not (isLowerHex id.[i] || id.[i] = '-') then ok <- false
        ok

/// The first two characters of an eligible id.
let shard (id: string) : string = id.Substring(0, 2)

/// `memories/<shard>/<id>.json`; the id must be eligible.
let recordPath (id: string) : string = "memories/" + shard id + "/" + id + ".json"

/// The id of a path of exactly the form `recordPath` writes (an eligible id whose shard matches), else `None`.
let idOfPath (path: string) : string option =
    if isNull path then None
    else
        match path.Split('/') with
        | [| "memories"; dir; file |] when file.EndsWith(".json", StringComparison.Ordinal) ->
            let id = file.Substring(0, file.Length - 5)
            if isEligibleId id && shard id = dir then Some id else None
        | _ -> None

// ---- text taken from a record ----

/// True when the string has a surrogate that is not half of a valid pair.
let hasLoneSurrogate (s: string) : bool =
    let mutable lone = false
    let mutable i = 0
    while i < s.Length do
        let c = s.[i]
        if Char.IsHighSurrogate c && i + 1 < s.Length && Char.IsLowSurrogate s.[i + 1] then i <- i + 2
        else
            if Char.IsSurrogate c then lone <- true
            i <- i + 1
    lone

let private isReplaced (c: char) =
    Char.GetUnicodeCategory c = Globalization.UnicodeCategory.Control || c = '\u2028' || c = '\u2029'

/// Text from a record shown in a reason: at most 64 UTF-16 code units, never cutting inside a
/// surrogate pair (the pair is dropped whole and the text ends there), a lone surrogate and every
/// Cc character, U+2028 and U+2029 replaced by `?`.
let sanitizeText (s: string) : string =
    if isNull s then ""
    else
        let sb = StringBuilder()
        let mutable i = 0
        let mutable stop = false
        while not stop && i < s.Length && sb.Length < 64 do
            let c = s.[i]
            if Char.IsHighSurrogate c && i + 1 < s.Length && Char.IsLowSurrogate s.[i + 1] then
                if sb.Length + 2 > 64 then stop <- true
                else
                    sb.Append(c).Append(s.[i + 1]) |> ignore
                    i <- i + 2
            else
                sb.Append(if Char.IsSurrogate c || isReplaced c then '?' else c) |> ignore
                i <- i + 1
        sb.ToString()
