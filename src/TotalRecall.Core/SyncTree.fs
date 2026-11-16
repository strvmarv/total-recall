/// Reads the listings `git ls-tree` and `git diff-tree` print (milestone 1a-1) and judges every entry as a possible
/// record file. Pure: bytes in, values out. A path is untrusted text: nothing here turns it into a file system path.
module TotalRecall.Core.SyncTree

open System
open System.Text
open TotalRecall.Core

// ---------------------------------------------------------------------------
// decoding

let private strict = UTF8Encoding(false, true)
let private lossy = UTF8Encoding(false, false)

/// `(text, valid)`: valid when the bytes are UTF-8 and the text holds no control character.
let private decodePath (bytes: byte[]) : string * bool =
    try
        let text = strict.GetString bytes
        let control = text |> Seq.exists (fun c -> c < ' ' || c = '\u007f')
        text, not control
    with _ ->
        lossy.GetString bytes, false

/// The `\0`-terminated records of a `-z` listing. A non-empty buffer that does not end in `\0` is an error.
let private splitRecords (buffer: byte[]) : Result<byte[] list, string> =
    if isNull buffer || buffer.Length = 0 then Ok []
    elif buffer.[buffer.Length - 1] <> 0uy then Error "the listing does not end with a NUL"
    else
        let records = ResizeArray<byte[]>()
        let mutable start = 0
        for i in 0 .. buffer.Length - 1 do
            if buffer.[i] = 0uy then
                records.Add(Array.sub buffer start (i - start))
                start <- i + 1
        Ok(List.ofSeq records)

let private isOctalMode (s: string) = s.Length = 6 && s |> Seq.forall (fun c -> c >= '0' && c <= '7')

let private isHexSha (s: string) =
    (s.Length = 40 || s.Length = 64) && s |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

let private ascii = Encoding.ASCII

// ---------------------------------------------------------------------------
// ls-tree

type TreeEntry =
    { Mode: string
      Kind: string
      Sha: string
      Size: int64 option
      Path: string
      PathValid: bool }

let private parseLsRecord (record: byte[]) : Result<TreeEntry, string> =
    let tab = Array.IndexOf(record, 9uy)
    if tab < 0 then Error "no tab"
    else
        let meta = ascii.GetString(record, 0, tab)
        let path, valid = decodePath (Array.sub record (tab + 1) (record.Length - tab - 1))
        match meta.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
        | [| mode; kind; sha; size |] ->
            if not (isOctalMode mode) then Error "bad mode"
            elif kind.Length = 0 || not (kind |> Seq.forall Char.IsAsciiLetterLower) then Error "bad type"
            elif not (isHexSha sha) then Error "bad object id"
            else
                let parsedSize =
                    if size = "-" then Ok None
                    else
                        match Int64.TryParse(size, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                        | true, n -> Ok(Some n)
                        | _ -> Error "bad size"
                match parsedSize with
                | Ok sz -> Ok { Mode = mode; Kind = kind; Sha = sha; Size = sz; Path = path; PathValid = valid }
                | Error e -> Error e
        | _ -> Error "wrong number of fields"

/// Parses `ls-tree -r -z -l` output. An error names the index of the first bad record; nothing throws.
let parseLsTree (buffer: byte[]) : Result<TreeEntry list, string> =
    match splitRecords buffer with
    | Error e -> Error e
    | Ok records ->
        let results = records |> List.mapi (fun i r -> i, (try parseLsRecord r with _ -> Error "unreadable"))
        match results |> List.tryPick (fun (i, r) -> match r with Error e -> Some(sprintf "record %d: %s" i e) | Ok _ -> None) with
        | Some e -> Error e
        | None -> Ok(results |> List.choose (fun (_, r) -> match r with Ok v -> Some v | Error _ -> None))

// ---------------------------------------------------------------------------
// diff-tree

type DiffStatus =
    | Added
    | Modified
    | Deleted
    | TypeChanged

type DiffEntry =
    { OldMode: string
      NewMode: string
      OldSha: string
      NewSha: string
      Status: DiffStatus
      Path: string
      PathValid: bool }

let private parseDiffPair (meta: byte[]) (pathBytes: byte[]) : Result<DiffEntry, string> =
    let text = ascii.GetString meta
    if not (text.StartsWith(":", StringComparison.Ordinal)) then Error "no leading colon"
    else
        match text.Substring(1).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
        | [| oldMode; newMode; oldSha; newSha; status |] ->
            if not (isOctalMode oldMode && isOctalMode newMode) then Error "bad mode"
            elif not (isHexSha oldSha && isHexSha newSha) then Error "bad object id"
            else
                let parsed =
                    match status with
                    | "A" -> Some Added
                    | "M" -> Some Modified
                    | "D" -> Some Deleted
                    | "T" -> Some TypeChanged
                    | _ -> None
                match parsed with
                | None -> Error "unexpected status"
                | Some st ->
                    let path, valid = decodePath pathBytes
                    Ok { OldMode = oldMode; NewMode = newMode; OldSha = oldSha; NewSha = newSha; Status = st; Path = path; PathValid = valid }
        | _ -> Error "wrong number of fields"

/// Parses `diff-tree -r -z --no-renames --raw` output: a metadata record then a path record, repeated.
let parseDiffTree (buffer: byte[]) : Result<DiffEntry list, string> =
    match splitRecords buffer with
    | Error e -> Error e
    | Ok records ->
        if records.Length % 2 <> 0 then Error(sprintf "record %d: a metadata record has no path" (records.Length / 2))
        else
            let pairs = records |> List.chunkBySize 2
            let results = pairs |> List.mapi (fun i p -> i, (try parseDiffPair p.[0] p.[1] with _ -> Error "unreadable"))
            match results |> List.tryPick (fun (i, r) -> match r with Error e -> Some(sprintf "record %d: %s" i e) | Ok _ -> None) with
            | Some e -> Error e
            | None -> Ok(results |> List.choose (fun (_, r) -> match r with Ok v -> Some v | Error _ -> None))

// ---------------------------------------------------------------------------
// verdicts

type RejectReason =
    | Symlink
    | Submodule
    | NotARegularFile
    | BadPath
    | BadShard
    | BadId
    | TooLarge

module RejectReason =
    /// The word used in logs and reviews.
    let describe (r: RejectReason) : string =
        match r with
        | Symlink -> "symlink"
        | Submodule -> "submodule"
        | NotARegularFile -> "not-a-regular-file"
        | BadPath -> "bad-path"
        | BadShard -> "bad-shard"
        | BadId -> "bad-id"
        | TooLarge -> "too-large"

/// The path of a rejection is raw untrusted text (it may hold newlines or escape characters): pass it through
/// `SyncValidation.sanitizeText` before logging or showing it.
type Verdict =
    | Accept of id: string * sha: string * size: int64
    | Reject of path: string * RejectReason

type DiffVerdict =
    | Changed of id: string * sha: string * DiffStatus
    | Gone of id: string
    | RejectedDiff of path: string * RejectReason

let private isHexLower (c: char) = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

/// `Ok (shardDir, stem)` for exactly `memories/<two hex>/<stem>.json` with a valid path; else `BadPath`.
let private shape (path: string) (valid: bool) : Result<string * string, RejectReason> =
    if isNull path || not valid || path |> Seq.exists (fun c -> c < ' ' || c = '\u007f') then Error BadPath
    else
        match path.Split('/') with
        | [| "memories"; dir; file |] when
            dir.Length = 2
            && isHexLower dir.[0]
            && isHexLower dir.[1]
            && file.Length > 5
            && file.EndsWith(".json", StringComparison.Ordinal)
            ->
            Ok(dir, file.Substring(0, file.Length - 5))
        | _ -> Error BadPath

/// The stem is judged before the shard (Interpretation 11i), so `memories/4f/x.json` is `BadId`.
let private identify (dir: string, stem: string) : Result<string, RejectReason> =
    if not (SyncValidation.isEligibleId stem) then Error BadId
    elif SyncValidation.shard stem <> dir then Error BadShard
    else Ok stem

/// The mode check for a file that will be read: a regular file, executable or not.
let private modeReason (mode: string) (kind: string) : RejectReason option =
    if kind = "commit" || mode = "160000" then Some Submodule
    elif mode = "120000" then Some Symlink
    elif kind <> "blob" || (mode <> "100644" && mode <> "100755") then Some NotARegularFile
    else None

/// Judges one `ls-tree` entry. The first reason that applies is reported, in the order bad path, mode, bad id, bad
/// shard, too large.
let classifyRecordEntry (e: TreeEntry) : Verdict =
    let reject r = Reject((if isNull e.Path then "" else e.Path), r)
    match shape e.Path e.PathValid with
    | Error r -> reject r
    | Ok(dir, stem) ->
        match modeReason e.Mode e.Kind with
        | Some r -> reject r
        | None ->
            match identify (dir, stem) with
            | Error r -> reject r
            | Ok id ->
                match e.Size with
                | None -> reject NotARegularFile
                | Some n when n > int64 SyncValidation.maxFileBytes -> reject TooLarge
                | Some n -> Accept(id, e.Sha, n)

/// Judges one `diff-tree` entry. A deletion at a well-formed record path is `Gone` whatever the old mode was; the
/// size of an added or modified file is not in a diff, so the reader checks it after `cat-file`.
let classifyDiffEntry (e: DiffEntry) : DiffVerdict =
    let reject r = RejectedDiff((if isNull e.Path then "" else e.Path), r)
    match shape e.Path e.PathValid with
    | Error r -> reject r
    | Ok(dir, stem) ->
        match e.Status with
        | Deleted ->
            match identify (dir, stem) with
            | Ok id -> Gone id
            | Error r -> reject r
        | st ->
            match modeReason e.NewMode "blob" with
            | Some r -> reject r
            | None ->
                match identify (dir, stem) with
                | Ok id -> Changed(id, e.NewSha, st)
                | Error r -> reject r
