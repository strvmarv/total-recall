module TotalRecall.Core.SyncValidation

// Rules for accepting a record read from the sync repository (a trust boundary) and for
// the limits the writer enforces (milestone 0b-1). Pure.

open System
open System.Text
open TotalRecall.Core.SyncStamp
open TotalRecall.Core.SyncRecord

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

/// The first two characters of an eligible id. Precondition: `isEligibleId id`.
let shard (id: string) : string = id.Substring(0, 2)

/// `memories/<shard>/<id>.json`. Precondition: `isEligibleId id`, otherwise `ArgumentException`
/// (so a hostile id can never become a path). Ids from an untrusted source enter only through `idOfPath`.
let recordPath (id: string) : string =
    if not (isEligibleId id) then invalidArg (nameof id) "the id is not eligible"
    "memories/" + shard id + "/" + id + ".json"

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

// ---- reading a record ----

/// A field of the JSON as the codec found it, before any rule is applied.
/// `Mistyped` means the JSON value had the wrong type for the field (or its parent object was not an object).
type RawField<'a> =
    | Absent
    | Mistyped
    | Value of 'a

type RawOrigin = { Tool: string; Hash: string }

/// What the codec extracts from a record's JSON. No rule has been applied to any value.
type RawSyncRecord =
    { Format: RawField<string>
      Id: RawField<string>
      Content: RawField<string>
      EntryType: RawField<string>
      CreatedAt: RawField<string>
      UpdatedAt: RawField<string>
      Tags: RawField<string list>
      Tier: RawField<string>
      Sticky: RawField<bool>
      TierStamp: RawField<string>
      StickyStamp: RawField<string>
      ContentStamp: RawField<string>
      Source: RawField<string>
      Project: RawField<string>
      Scope: RawField<string>
      WrittenBy: RawField<string>
      Origin: RawField<RawOrigin> }

/// Why a record read from the repository was not accepted. Text taken from the record is
/// sanitised (`sanitizeText`) before it is stored here.
type Reject =
    | Oversize
    | UnknownFormat of string
    | MissingField of string
    | WrongType of string
    | BadId
    | IdPathMismatch
    | BadEntryType of string
    | BadTags
    | BadTier of string
    | BadTimestamp of string
    | BadStamp of string
    | NotJson of string
    | DuplicateKey of string

module Reject =

    /// A one-line message for `status`.
    let describe (r: Reject) : string =
        match r with
        | Oversize -> "the file is larger than 1 MiB"
        | UnknownFormat f -> "unknown format '" + f + "' (this repository was written by a newer total-recall?)"
        | MissingField n -> "missing field " + n
        | WrongType n -> "wrong type for " + n
        | BadId -> "the id is not valid"
        | IdPathMismatch -> "the id does not match the file name and folder"
        | BadEntryType t -> "unknown entry type '" + t + "'"
        | BadTags -> "too many tags, or a tag is too long"
        | BadTier t -> "unknown tier '" + t + "'"
        | BadTimestamp n -> "malformed timestamp in " + n
        | BadStamp n -> "malformed stamp in " + n
        | NotJson why -> "not a valid record: " + why
        | DuplicateKey k -> "the property '" + k + "' appears twice"

/// Something the reader changed about an accepted record.
type Note =
    /// The record said `sticky: true` with a colder tier; its tier is hot.
    | StickyNormalisedToHot
    /// The record named a source tool this version does not know; it is dropped.
    | UnknownSourceDropped of string

/// `StoredTier` is the tier exactly as the file says; it differs from `Record.Placement.Tier` only
/// for the `StickyNormalisedToHot` case.
type ValidRecord =
    { Record: SyncRecord.SyncRecord
      Notes: Note list
      StoredTier: Tier }

/// Why an entry may not be pushed.
type Block =
    | TooLarge
    | TooManyTags
    | TagTooLong
    | IneligibleId
    | NotEncodable
    | ClockExhausted

[<Literal>]
let private memoryFormat = "total-recall/memory/v1"

/// The reader never looks at a file larger than this.
[<Literal>]
let maxFileBytes = 1_048_576

/// A record that serializes to more than this is not pushed.
[<Literal>]
let maxRecordBytes = 262_144

/// Tag limits when writing (counted as a set) and when reading.
[<Literal>]
let maxWriteTags = 64

[<Literal>]
let maxWriteTagUnits = 128

[<Literal>]
let maxReadTags = 256

[<Literal>]
let maxReadTagUnits = 256

let private pathTr = "ext.total-recall."
let private pathPlacement = "ext.total-recall.placement."

type private ResultBuilder() =
    member _.Bind(r: Result<'a, 'e>, f: 'a -> Result<'b, 'e>) = Result.bind f r
    member _.Return(x: 'a) : Result<'a, 'e> = Ok x
    member _.ReturnFrom(r: Result<'a, 'e>) = r
    member _.Zero() : Result<unit, 'e> = Ok()

let private result = ResultBuilder()

let private req (name: string) (f: RawField<'a>) : Result<'a, Reject> =
    match f with
    | Value v -> Ok v
    | Absent -> Error(MissingField name)
    | Mistyped -> Error(WrongType name)

let private opt (name: string) (f: RawField<'a>) : Result<'a option, Reject> =
    match f with
    | Value v -> Ok(Some v)
    | Absent -> Ok None
    | Mistyped -> Error(WrongType name)

let private ensure (ok: bool) (reject: Reject) : Result<unit, Reject> = if ok then Ok() else Error reject

let private parseWith (parse: string -> 'a option) (bad: Reject) (s: string) : Result<'a, Reject> =
    match parse s with
    | Some v -> Ok v
    | None -> Error bad

/// Applies the reading rules, in the order of the spec, and returns the first failure. Never throws.
let validate (path: string) (sizeBytes: int) (raw: RawSyncRecord) : Result<ValidRecord, Reject> =
    result {
        do! ensure (sizeBytes <= maxFileBytes) Oversize
        let! format = req "format" raw.Format
        do! ensure (format = memoryFormat) (UnknownFormat(sanitizeText format))
        let! id = req "id" raw.Id
        do! ensure (isEligibleId id) BadId
        do! ensure (idOfPath path = Some id) IdPathMismatch
        let! content = req "content" raw.Content
        let! entryTypeText = req "entry_type" raw.EntryType
        let! entryType = parseWith SyncRecord.tryParseEntryType (BadEntryType(sanitizeText entryTypeText)) entryTypeText
        let! tags = req "tags" raw.Tags
        do! ensure (not (isNull (box tags)) && tags.Length <= maxReadTags && tags |> List.forall (fun t -> not (isNull t) && t.Length <= maxReadTagUnits)) BadTags
        let! tierText = req (pathPlacement + "tier") raw.Tier
        let! tier = parseWith SyncRecord.tryParseTier (BadTier(sanitizeText tierText)) tierText
        let! sticky = req (pathPlacement + "sticky") raw.Sticky
        let! createdText = req "created_at" raw.CreatedAt
        let! createdAtMs = parseWith Timestamp.tryParse (BadTimestamp "created_at") createdText
        let! updatedText = req "updated_at" raw.UpdatedAt
        let! _updated = parseWith Timestamp.tryParse (BadTimestamp "updated_at") updatedText
        let stampField name (f: RawField<string>) =
            result {
                let! text = req name f
                return! parseWith Stamp.tryParse (BadStamp name) text
            }
        let! contentStamp = stampField (pathTr + "content_stamp") raw.ContentStamp
        let! tierStamp = stampField (pathPlacement + "tier_stamp") raw.TierStamp
        let! stickyStamp = stampField (pathPlacement + "sticky_stamp") raw.StickyStamp
        let! sourceText = opt "source" raw.Source
        let! project = opt (pathTr + "project") raw.Project
        let! scope = opt (pathTr + "scope") raw.Scope
        let! writtenBy = opt (pathTr + "written_by") raw.WrittenBy
        let! rawOrigin = opt (pathTr + "origin") raw.Origin
        let source, sourceNotes =
            match sourceText with
            | None -> None, []
            | Some s ->
                match SyncRecord.tryParseSourceTool s with
                | Some t -> Some t, []
                | None -> None, [ UnknownSourceDropped(sanitizeText s) ]
        let tierNotes, effectiveTier =
            if sticky && tier <> Hot then [ StickyNormalisedToHot ], Hot else [], tier
        let origin : SyncRecord.Origin option =
            rawOrigin |> Option.map (fun (o: RawOrigin) -> { Tool = o.Tool; Hash = o.Hash })
        let record : SyncRecord.SyncRecord =
            { Id = id
              Content = content
              EntryType = entryType
              Tags = SyncRecord.normaliseTags tags
              Source = source
              CreatedAtMs = createdAtMs
              Project = project
              Scope = scope
              Placement =
                { Tier = effectiveTier
                  TierStamp = tierStamp
                  Sticky = sticky
                  StickyStamp = stickyStamp }
              ContentStamp = contentStamp
              Origin = origin
              WrittenBy = writtenBy }
        return { Record = record; Notes = sourceNotes @ tierNotes; StoredTier = tier }
    }

// ---- writing a record ----

let private hasUnencodableText (r: SyncRecord.SyncRecord) : bool =
    let strings =
        [ yield r.Id
          yield r.Content
          yield! r.Tags
          yield! Option.toList r.Project
          yield! Option.toList r.Scope
          yield! Option.toList r.WrittenBy
          match r.Origin with
          | Some o ->
              yield o.Tool
              yield o.Hash
          | None -> () ]
    strings |> List.exists (fun s -> isNull s || hasLoneSurrogate s)

let private stampUnusable (s: Stamp) : bool =
    isNull (box s) || isNull (box s.Machine) || not (Stamp.inDomain s)

let private hasOutOfDomainNumber (r: SyncRecord.SyncRecord) : bool =
    not (Timestamp.inRange r.CreatedAtMs)
    || stampUnusable r.ContentStamp
    || stampUnusable r.Placement.TierStamp
    || stampUnusable r.Placement.StickyStamp

/// Whether the entry may be pushed. `serializedBytes` is the size of the record as written.
/// Tags are counted as a set (the writer writes them unique), so duplicates do not count twice.
/// The first matching reason, in the order of the spec's list, is returned.
let checkWritable (record: SyncRecord.SyncRecord) (serializedBytes: int) : Block option =
    if isNull (box record) || isNull (box record.Tags) || isNull (box record.Placement) then Some NotEncodable
    elif serializedBytes > maxRecordBytes then Some TooLarge
    elif (List.distinct record.Tags).Length > maxWriteTags then Some TooManyTags
    elif record.Tags |> List.exists (fun t -> not (isNull t) && t.Length > maxWriteTagUnits) then Some TagTooLong
    elif not (isEligibleId record.Id) then Some IneligibleId
    elif hasUnencodableText record || hasOutOfDomainNumber record then Some NotEncodable
    else None
