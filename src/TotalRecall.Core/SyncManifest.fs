/// The rules for the repository's `manifest.json` (milestone 1a-2): its format versions, the shape of its
/// `repo_id` and the exact bytes of the file. Pure: reading the file and parsing its JSON are in
/// `TotalRecall.Infrastructure.GitSync.ManifestFile`.
module TotalRecall.Core.SyncManifest

open System
open System.Text

/// The format of a manifest this build writes and reads.
[<Literal>]
let FormatV1 = "total-recall/v1"

/// The most a `v1` manifest may hold, in bytes.
[<Literal>]
let MaxManifestBytes = 4096

/// The most the caller reads of a manifest blob, so a newer format can still be recognised.
[<Literal>]
let MaxLookBytes = 65536

/// How a `format` text reads: this build's format, a newer version of it, or not a total-recall format.
type ManifestFormat =
    | Supported
    | Newer of int
    | Unrecognised

[<Literal>]
let private Prefix = "total-recall/v"

/// `total-recall/v1` is supported; `total-recall/v<N>` with `N` a positive integer without leading zeros, at most
/// nine ASCII digits and greater than 1 is newer; everything else is unrecognised.
let classifyFormat (format: string) : ManifestFormat =
    if isNull format || not (format.StartsWith(Prefix, StringComparison.Ordinal)) then Unrecognised
    else
        let digits = format.Substring Prefix.Length
        let isAsciiDigit (c: char) = c >= '0' && c <= '9'
        if digits.Length = 0 || digits.Length > 9 || digits.[0] = '0' || not (Seq.forall isAsciiDigit digits) then Unrecognised
        else
            let n = int digits
            if n = 1 then Supported else Newer n

/// A `repo_id` is 128 random bits as exactly 32 lowercase hex digits.
let isValidRepoId (id: string) : bool =
    not (isNull id) && id.Length = 32 && id |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

let private isTimestamp (s: string) : bool =
    // YYYY-MM-DDTHH:MM:SSZ
    not (isNull s)
    && s.Length = 20
    && s |> Seq.mapi (fun i c ->
        match i with
        | 4 | 7 -> c = '-'
        | 10 -> c = 'T'
        | 13 | 16 -> c = ':'
        | 19 -> c = 'Z'
        | _ -> c >= '0' && c <= '9')
       |> Seq.forall id

let private appendJsonString (sb: StringBuilder) (s: string) =
    sb.Append('"') |> ignore
    let mutable i = 0
    while i < s.Length do
        let c = s.[i]
        if Char.IsHighSurrogate c then
            if i + 1 < s.Length && Char.IsLowSurrogate s.[i + 1] then
                sb.Append(c).Append(s.[i + 1]) |> ignore
                i <- i + 1
            else invalidArg "createdBy" "a lone surrogate cannot be written as UTF-8"
        elif Char.IsLowSurrogate c then invalidArg "createdBy" "a lone surrogate cannot be written as UTF-8"
        else
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\r' -> sb.Append("\\r") |> ignore
            | '\t' -> sb.Append("\\t") |> ignore
            | '\b' -> sb.Append("\\b") |> ignore
            | '\012' -> sb.Append("\\f") |> ignore
            | c when c < ' ' -> sb.Append("\\u").Append((int c).ToString("x4")) |> ignore
            | c -> sb.Append(c) |> ignore
        i <- i + 1
    sb.Append('"') |> ignore

/// The bytes of a `v1` manifest: one line of JSON with the keys in this order, one trailing `\n`, UTF-8 without a
/// byte-order mark. Throws `ArgumentException` for an invalid `repoId`, a `createdAt` that is not
/// `YYYY-MM-DDTHH:MM:SSZ`, or a `createdBy` holding a lone surrogate.
let render (repoId: string) (createdBy: string) (createdAt: string) : byte[] =
    if not (isValidRepoId repoId) then invalidArg "repoId" "a repo id is 32 lowercase hex digits"
    if not (isTimestamp createdAt) then invalidArg "createdAt" "expected YYYY-MM-DDTHH:MM:SSZ"
    if isNull createdBy then invalidArg "createdBy" "required"
    let sb = StringBuilder()
    sb.Append("{\"format\":\"").Append(FormatV1).Append("\",\"repo_id\":\"").Append(repoId).Append("\",\"created_by\":") |> ignore
    appendJsonString sb createdBy
    sb.Append(",\"created_at\":\"").Append(createdAt).Append("\"}\n") |> ignore
    UTF8Encoding(false).GetBytes(sb.ToString())
