# Git sync, milestone 0b-1: record format, clock and validation

Date: 2026-11-15. Milestone 0b-1 of the git sync feature tracked in #93 (0a merged as #100). The umbrella
design is `2026-11-15-git-sync-design.md` (see "Repository layout", "Reading the repository safely" and
"Ordering: a hybrid logical clock"); the draft criteria are in `2026-11-15-git-sync-acceptance-criteria.md`.
The umbrella's milestone 0b is split in three: **0b-1 (this document)**, 0b-2 (the pure merge decisions) and
0b-3 (migration 18, the sync tables and `GitSyncConfig`). This spec covers 0b-1 only. It adds pure logic, one
codec and their tests, and wires no sync.

## Problem

Every later milestone reads and writes the same repository file and orders changes by the same clock, so the
format, the clock and the rules for accepting a record must exist once, tested hard, before anything uses
them:

- a **record**: the JSON file that holds one memory in the sync repository, in a form that is byte-stable (an
  unchanged record is a byte-identical file) and that can be **patched** (change one field group and leave
  every other byte, unknown fields included);
- a **hybrid logical clock**: stamps that order changes correctly even when two machines' clocks disagree;
- **validation**: the rules by which a record read from the repository is accepted or rejected (it is a trust
  boundary: anyone holding the user's credentials can write to it), and the limits the writer enforces so it
  never produces a record every reader would reject.

## Goals

1. A `SyncRecord` model and a `Stamp` type, with strict parse and canonical format for stamps.
2. The clock: `issue`, `observe`, a drift guard, and a startup guard for the machine's own persisted clock, all
   pure, with the ordering properties of the umbrella spec proven by tests (including property tests).
3. Validation of a record read from the repository (returning a typed record or a reason, never throwing) and
   the writer-side limits.
4. A JSON codec: parse a record file into a validated record while keeping what it does not understand,
   write a record canonically, and patch a record's field groups.
5. Everything AOT-safe and covered by tests; no existing behavior changes.

## Non-goals

- The merge decisions (whole-entry, no-base, field-wise, tier events, sticky review): 0b-2.
- Migration 18, the sync tables, `GitSyncConfig` and loading it: 0b-3.
- Reading git objects, the scan, the cycle, machine-id generation, the manifest file: milestone 1a and later.
- Anything that touches `IStore`, the importers or MCP tools.

## Decided

- **The pure logic is F# in `TotalRecall.Core`** (the repo's rule: pure functions live in Core), as three new
  files after `Types.fs`: `SyncStamp.fs`, `SyncRecord.fs`, `SyncValidation.fs`. The JSON codec is C# in
  `TotalRecall.Infrastructure` (namespace `TotalRecall.Infrastructure.GitSync`, class `RecordCodec`).
- The record's wire format is the umbrella spec's. This document fixes what it left open: `created_at` and
  `updated_at` are ISO-8601 UTC with milliseconds (`yyyy-MM-ddTHH:mm:ss.fffZ`); `entry_type` values are the
  lowercase DU names (`correction`, `preference`, `decision`, `surfaced`, `imported`, `compacted`,
  `ingested`) and are **not** the PascalCase strings the SQLite store keeps; `tier` values are `hot`, `warm`,
  `cold`; `source` values are the seven tool names `SourceToolMapping` writes (`claude-code`, `copilot-cli`,
  `opencode`, `cursor`, `cline`, `hermes`, `manual`).
- Stamps are required on a record: `content_stamp`, `placement.tier_stamp` and `placement.sticky_stamp`.
- `updated_at` is **derived**: it is the time of `content_stamp`. The model has no field for it. On read it
  must parse (a bad one is rejected) but its value is otherwise ignored; on write it is always the content
  stamp's time. For a canonical file the two agree, so the round trip is byte-identical.
- Tags are a set. The codec writes them **sorted by UTF-16 code-unit order (F# string `compare`; this differs from UTF-8 byte order for supplementary characters) and de-duplicated**, so the same set is always the
  same bytes. `Parse` accepts any order and duplicates and the model holds them sorted and de-duplicated.

## Design

### Stamps (`SyncStamp.fs`, module `TotalRecall.Core.SyncStamp`)

```fsharp
type MachineId                                   // 8 lowercase hex chars; MachineId.tryCreate : string -> MachineId option
type Stamp = { TimeMs: int64; Counter: int; Machine: MachineId }
type Clock = { ClockMs: int64; ClockCounter: int }       // the machine's persisted clock
```

- **Domain.** `TimeMs` is in `[0, 253_402_300_799_999]` (1970-01-01 to 9999-12-31T23:59:59.999Z, the range the
  text form can express); `Counter` is in `[0, 999_999]`. `tryParse` and `tryDeserialize` reject anything
  outside it, so a value of these types is always in range. `nowMs` arguments below are clamped into the range.
- **Text form:** `<yyyy-MM-ddTHH:mm:ss.fffZ>.<counter, 6 digits>.<machine>`, for example
  `2026-11-12T09:14:03.512Z.000003.4f2a9c1e`. `Stamp.format` and `Stamp.tryParse` are strict inverses.
  `tryParse` accepts exactly that shape, with ASCII digits `0` to `9` only in fixed positions (so no sign,
  space, `+`, non-ASCII digit or extra text), a valid calendar date and time, and a machine of exactly eight
  lowercase hex characters, and returns `None` otherwise; it never throws.
- **Order:** `Stamp.compare` orders by (`TimeMs`, `Counter`, `Machine` compared ordinally). Stamps from two
  different machines are never equal.
- **Constants:** `maxCounter = 999_999`, `maxTimeMs = 253_402_300_799_999`, `driftLimitMs = 86_400_000`.
- `Clock.issue (machine: MachineId) (nowMs: int64) (clock: Clock option) (baseStamp: Stamp option) :
  Result<IssueResult, ClockError>` with `IssueResult = { Stamp: Stamp; Clock: Clock option; FarFuture: bool }`.
  Let `t` be the largest of the (clamped) `nowMs`, the clock's time and the base stamp's time. The counter is
  `0` when neither the clock nor the base stamp has time `t`, else one more than the larger counter among
  those that do. If the counter would exceed `maxCounter`, the time becomes `t + 1` and the counter `0`. If
  the result would pass `maxTimeMs`, the result is `Error Exhausted` (reachable only through a hostile stamp
  at the very top of the range; the caller treats the entry as blocked). Otherwise the new stamp is strictly
  greater than the clock and than the base stamp. `FarFuture` is true when the stamp's time is more than
  `driftLimitMs` ahead of `nowMs`; then `Clock` is the **input clock unchanged** (a far-future stamp is used
  for ordering but never adopted), else `Clock` is the new stamp's time and counter.
- `Clock.observe (nowMs: int64) (clock: Clock option) (seen: Stamp) : ObserveResult` with
  `ObserveResult = { Clock: Clock option; FarFuture: bool }`: the clock becomes the greater of the current
  clock and the seen stamp's (time, counter), unless the seen stamp is more than `driftLimitMs` ahead of
  `nowMs`: then the clock is unchanged and `FarFuture` is true.
- `Clock.guardStartup (nowMs: int64) (clock: Clock option) : GuardResult` with
  `GuardResult = { Clock: Clock option; Reset: bool }`: a persisted clock more than `driftLimitMs` ahead of
  `nowMs` becomes `{ ClockMs = nowMs; ClockCounter = 0 }` with `Reset = true`; otherwise unchanged.
- `Clock.serialize` / `Clock.tryDeserialize` give the clock the text form `<time ms>.<counter>` for `_meta`
  (0b-3 stores it); `tryDeserialize` rejects out-of-range or malformed values.

### Record model (`SyncRecord.fs`, module `TotalRecall.Core.SyncRecord`)

```fsharp
type Placement = { Tier: Tier; TierStamp: Stamp; Sticky: bool; StickyStamp: Stamp }
type Origin = { Tool: string; Hash: string }
type SyncRecord =
    { Id: string; Content: string; EntryType: EntryType; Tags: string list      // Tags: sorted, unique
      Source: SourceTool option; CreatedAtMs: int64
      Project: string option; Scope: string option
      Placement: Placement; ContentStamp: Stamp
      Origin: Origin option; WrittenBy: string option }
```

with `Tier`, `EntryType` and `SourceTool` the existing Core types. Helpers in the module: `entryTypeName` /
`tryParseEntryType`, `sourceToolName` / `tryParseSourceTool` (the lowercase names above; `None` for an unknown
name) and `tierName` / `tryParseTier` / `tierRank` (hot is the warmest). Each is tested.

### Validation (`SyncValidation.fs`, module `TotalRecall.Core.SyncValidation`)

**Id and path.**
- `isEligibleId id` is a character-wise check: length 8 to 64, the first character `0-9a-f`, every other
  character `0-9a-f` or `-` (ASCII only; no regular expression, so a trailing newline, NUL, space or Unicode
  digit is not accepted).
- `shard id` is the first two characters; `recordPath id` is `memories/<shard>/<id>.json`; `idOfPath path`
  returns the id for a path of exactly that form with an eligible id whose shard matches, and `None`
  otherwise (so a wrong shard, a nested path, a traversal, a different extension or a different directory
  give `None`).

**Reading.** The codec extracts a `RawSyncRecord` from JSON without applying any rule. Each field is a
`RawField<'a> = Absent | WrongType | Value of 'a`, where `WrongType` means the JSON value had the wrong type for
that field (the codec tells apart nothing else). The fields are: `Format`, `Id`, `Content`, `EntryType`,
`CreatedAt`, `UpdatedAt` (strings); `Tags` (an array of strings: any non-array or non-string element makes the
whole field `WrongType`); `Tier`, `TierStamp`, `StickyStamp`, `ContentStamp` (strings); `Sticky` (a boolean);
optional `Source`, `Project`, `Scope`, `WrittenBy` (strings) and `Origin` (an object of two strings; the codec reports `WrongType` named `ext.total-recall.origin` when the object lacks `tool` or `hash` or one is not a string). The
`ext` and `placement` objects are not fields: a missing or non-object one makes the fields inside it `Absent`
(or `WrongType` for a present non-object), named by their path, for example `ext.total-recall.placement.tier`.

`validate (path: string) (sizeBytes: int) (raw: RawSyncRecord) : Result<ValidRecord, Reject>` applies, in this
order, and returns the first failure:
1. `sizeBytes` at most 1,048,576, else `Oversize`;
2. `Format`: `Absent` is `MissingField`, `WrongType` is `WrongType`, a string other than
   `total-recall/memory/v1` is `UnknownFormat` (so a future-format record is reported as such before any of its
   other fields are looked at);
3. `Id`: required; eligible, **and** equal to `idOfPath path`, else `BadId` or `IdPathMismatch`;
4. in this order, each **required** (`MissingField` if `Absent`, `WrongType` if wrong): `Content`;
   `EntryType` (one of the seven names, else `BadEntryType`); `Tags` (at most 256 entries, each at most 256
   UTF-16 code units, else `BadTags`); `Tier` (hot, warm or cold, else `BadTier`); `Sticky`;
   `CreatedAt` and `UpdatedAt` (parse as `yyyy-MM-ddTHH:mm:ss.fffZ` with ASCII digits in fixed positions,
   else `BadTimestamp field`); `ContentStamp`, `TierStamp`, `StickyStamp` (`Stamp.tryParse`, else
   `BadStamp field`);
5. the optional fields: a present value of the wrong type is `WrongType`; `Source` that is a string but not one
   of the seven names is **dropped** (becomes `None`), not an error.

`ValidRecord = { Record: SyncRecord; Notes: Note list }` where `Note` is `StickyNormalisedToHot` (the record said
`sticky: true` with a colder tier; the model's tier is hot) or `UnknownSourceDropped of string`. `Reject` is
`Oversize | UnknownFormat of string | MissingField of string | WrongType of string | BadId | IdPathMismatch |
BadEntryType of string | BadTags | BadTier of string | BadTimestamp of string | BadStamp of string |
NotJson of string | DuplicateKey of string`. Every string that enters a `Reject` or a `Note` comes from the
record, so it is **truncated to 64 UTF-16 code units and has control characters replaced by `?`** before it is
stored. `Reject.describe` gives a one-line message for `status`. `validate` never throws, for any input.

**Writing.** `checkWritable (record: SyncRecord) (serializedBytes: int) : Block option` is pure and returns a
block reason when the entry may not be pushed: `TooLarge` (serialized over 262,144 bytes), `TooManyTags` (over
64), `TagTooLong` (a tag over 128 UTF-16 code units), `IneligibleId`, or `NotEncodable` (any string of the
record holds a lone surrogate, or a stamp or `CreatedAtMs` is outside its domain; `checkWritable` scans for
these itself). Else `None`. `Block` also has `ClockExhausted` for `Clock.issue`'s `Error Exhausted`. The codec's
`Write` enforces **none** of the size or tag limits (the caller runs `checkWritable` on the record and on the
byte count `Write` returned); it only fails with `NotEncodable`.

**Domain of the other numbers.** `CreatedAtMs` is in `[-62_135_596_800_000, 253_402_300_799_999]` (year 1 to
9999, what the text form can express); `Stamp` and `Clock` values built by hand outside their domain are
`NotEncodable` on write, never an exception.

**Truncation of text from a record** (used by `Reject` and `Note`): at most 64 UTF-16 code units, never
cutting inside a surrogate pair (the pair is dropped whole) and with a lone surrogate replaced by `?`, with every Unicode category Cc character
(U+0000 to U+001F, U+007F to U+009F) and U+2028 and U+2029 replaced by `?`.

### The codec (`RecordCodec.cs`, namespace `TotalRecall.Infrastructure.GitSync`)

**Reading.** `Parse(ReadOnlySpan<byte> utf8, string path) : ParseResult`, a valid `ParsedRecord` or a `Reject`.
It runs, in this order, and never throws (it catches the exceptions of the reader and of string decoding):
1. **size first**: more than 1,048,576 bytes is `Oversize`, before any parsing;
2. the text is UTF-8 without a byte-order mark, a single JSON object followed by nothing but whitespace, with
   nesting no deeper than 64; anything else is `NotJson`;
3. a pre-pass of `Utf8JsonReader` over the whole document, which: rejects a **duplicate property name
   anywhere** (in a known object, in unknown content, inside `ext`) as `DuplicateKey`, comparing the
   **decoded** names ordinally (so `"content"` and `"cont\u0065nt"` collide) so a hostile file cannot show
   the validator one value and carry another through a patch; decodes **every string value and property name
   anywhere, known or unknown**, and rejects a lone surrogate or invalid UTF-8 as `NotJson` (so every record
   `Parse` accepts can be written back); and takes the text of a number only from a `Number` token, which the
   reader has already checked against the JSON number grammar;
4. a `RawSyncRecord` is built and `validate` is called. Structural errors (steps 1 to 3: `Oversize`, `NotJson`,
   `DuplicateKey`) take precedence over every `validate` reason, including `UnknownFormat`.

A `ParsedRecord` holds the validated `SyncRecord` (whose `Tier` is the normalised one), the `Notes` of the
validation, the **stored tier** (`StoredTier`, the tier exactly as the file says, which differs from the model's
only for a `sticky: true` record that names a colder tier) and the **unknown content**, each item a detached
`JsonElement` clone with its position (its original order within its object): every top-level property other
than the nine known ones; every `ext` namespace other than `total-recall`; every property of
`ext["total-recall"]` other than the known ones; every property of `placement` and of `origin` other than the
known ones; and the **raw text of an unknown `source`** (the model holds `None` for it, but the parsed record
keeps the string, and `Write` and `Patch` write it back unless a `ContentPatch` names a new `Source`). It is
immutable, so it can be reused for any number of patches.

**Writing.** `Write(SyncRecord) : WriteResult` and `Write(ParsedRecord) : WriteResult`
(`WriteResult = Bytes of byte[] | NotEncodable`; the second includes the unknown content, so
`Write(Parse(bytes))` is the round trip: it writes `StoredTier`, not the normalised model tier, and writes `updated_at` as the time of the content stamp, a hand-edited `updated_at` that disagrees being corrected; `Write(SyncRecord)` has no stored tier and writes the model's). The canonical form is UTF-8 without a BOM, two-space indent, LF line
endings, one trailing newline, the properties in the order of the umbrella spec's example (top level:
`format`, `id`, `content`, `entry_type`, `tags`, `source` (omitted when absent), `created_at`, `updated_at`,
`ext`; inside `ext["total-recall"]`: `project`, `scope`, `placement` (`tier`, `tier_stamp`, `sticky`,
`sticky_stamp`), `content_stamp`, `origin`, `written_by`; absent optional properties are omitted), then, in
each object (top level, `ext["total-recall"]`, `placement`, `origin`), its unknown properties in their original
order; unknown `ext` namespaces follow `total-recall`. An unknown `source` keeps its normal place.

**Layout.** Two-space indentation, one property per line, `": "` between a name and its value, one array
element per line, an empty array written `[]` and an empty object `{}`, `,` at the end of every line but the
last in its container, and nested containers indented one level deeper than their property. (The umbrella's
example puts a short array and object on one line; it is illustrative.)
An empty `tags` array is written `[]`. Unknown values are written recursively with the same rules; numbers are
copied as their original text; strings are re-escaped by the rule below.

**Escaping is ours, not the framework's.** The writer is a small hand-written one (the repository already has
hand-written JSON precedent in `Sync/SyncPayload.cs`) so the bytes do not depend on a runtime's Unicode tables or
encoder defaults. A string is written in double quotes with exactly these escapes: `\"`, `\\`, `\n`, `\r`,
`\t`, `\b`, `\f`, and `\u00XX` (lowercase hex) for every other control character U+0000 to U+001F. **Every
other character is written literally as UTF-8**, including U+007F, accented, CJK and supplementary-plane
characters (emoji). A lone surrogate cannot be encoded: the result is `NotEncodable`.

**Patching.** `Patch(ParsedRecord, RecordPatch) : PatchResult` (`PatchResult = Patched of byte[] * SyncRecord | NotEncodable | MissingTierStamp`):

```csharp
record RecordPatch(
    ContentPatch? Content,   // see below; its ContentStamp always sets updated_at
    IReadOnlyList<string>? Tags,
    StickyPatch? Sticky,     // Sticky, StickyStamp, TierStampIfRaised
    TierPatch? Tier);        // Tier, TierStamp
```

`ContentPatch` has a required `Content` string, a required `EntryType`, a required `ContentStamp`, and for
`Project`, `Scope` and `Source` a `Change<'a> = Keep | Set of 'a | Clear` (so a text edit that does not name
`Source` keeps an unknown source, and one can clear a project). `TierStampIfRaised` is ignored when a `Tier` part
is present. `Patch` returns the bytes **and the resulting `SyncRecord`** (`Patched of byte[] * SyncRecord`), so
the caller runs `checkWritable` without parsing again. A patch with no content part still writes `updated_at` as
the time of the file's `content_stamp`.

An absent part leaves its properties exactly as they were, **as they are in the file** (including a stored
tier that normalisation would raise, and unknown content), so a patch never rewrites a group it does not name. The one exception is tags: `Write` and `Patch` always write the tags sorted and unique, so a hand-edited record with unsorted tags is normalised by any write, as its formatting is.
`created_at` and `written_by` are never changed by a patch. `Patch` does not validate its output: the caller
runs `checkWritable` on the record and on the byte count. A patch of a record in canonical form changes only
the bytes of the properties it names (plus `updated_at` for a content patch); a patch of a non-canonical
(hand-edited) record also normalises its formatting once. The tier group (`tier`, `tier_stamp`) and the sticky
group (`sticky`, `sticky_stamp`) are written like this, by which parts are present, where `stored tier` is the
tier in the file and `raised` means a pin is being set (`Sticky = true`) while the stored tier is colder than
hot:

| Parts present | `tier`, `tier_stamp` written | `sticky`, `sticky_stamp` written |
|---|---|---|
| neither | as stored | as stored |
| `Sticky` only, not raised | as stored | the patch's |
| `Sticky` only, raised | hot, with `TierStampIfRaised` (absent: result `MissingTierStamp`) | the patch's |
| `Tier` only, record sticky in the file | hot, with the patch's `TierStamp` | as stored |
| `Tier` only, record not sticky | the patch's | as stored |
| `Sticky` and `Tier`, resulting sticky | hot, with the patch's `TierStamp` | the patch's |
| `Sticky` and `Tier`, resulting not sticky | the patch's | the patch's |

## Testing

F# tests in `tests/TotalRecall.Core.Tests` (Expecto, with FsCheck for properties), three files
(`SyncStampTests`, `SyncRecordTests`, `SyncValidationTests`):
- **Stamps:** format and parse are inverses over the whole domain (property); each malformed shape is rejected
  (bad date, two-digit year, five- or seven-digit counter, upper-case, short or long machine, trailing text,
  a sign, a leading or trailing space, a non-ASCII digit); `MachineId.tryCreate`; order by time, then counter,
  then machine; equal times on two machines order by machine.
- **Clock:** `issue` is strictly greater than the clock and the base stamp for every in-domain input
  (property) and `Clock` never goes backwards; the slow-clock case (A pins at 10:00:00.500, B observes it and
  unpins by a clock reading 10:00:00.200: the unpin sorts after the pin); a fast clock; counter overflow
  (clock, base, and both at counter 999,999) advances the time by one millisecond; the top of the range gives
  `Error Exhausted` and nothing wraps; a base stamp exactly 24 hours ahead is adopted and one millisecond more
  is not (`FarFuture`), for `issue` and `observe`; `guardStartup` resets a clock a year ahead, leaves one
  exactly 24 hours ahead alone, and `Clock.serialize`/`tryDeserialize` round-trip and reject out-of-range or
  malformed text.
- **Record helpers:** each name function and its `tryParse` round-trip; unknown names give `None`; `tierRank`
  orders hot before warm before cold.
- **Validation (truncation):** a lone surrogate in a `Reject` string is replaced.
- **Validation:** the id rule (eligible ids; uppercase, one character, 65 characters, a leading dash, a
  trailing `\n`, NUL, a space, a Unicode digit); `recordPath` and `idOfPath` for good, wrong-shard, nested,
  traversal, wrong-extension and wrong-directory paths; each rule of `validate` in the stated order (oversize
  before format, an unknown format before a missing field, then id and path, each required field absent and
  wrong-typed, bad tier, entry type, tags at 256/257 entries and 256/257 code units, bad timestamps and
  stamps); an unknown `source` is dropped with a note; `sticky: true` with a colder tier is normalised to hot
  with a note; strings in `Reject` and `Note` are truncated at 64 code units without splitting a surrogate pair and have Cc characters, U+2028 and U+2029 replaced; a random raw record never makes
  `validate` throw (property); `checkWritable` at each limit (exactly at and one over) including
  `NotEncodable`.

C# tests in `tests/TotalRecall.Infrastructure.Tests/GitSync/RecordCodecTests.cs` (xUnit, no Integration trait):
- **Golden bytes:** a fixed set of records (one with a `"` and a backslash, a control character, an accented
  word, CJK, an emoji, CRLF inside the content, an empty tag list, every optional field present and absent)
  written to **literal expected bytes** committed in the test, the same on every operating system. This is the
  guard for byte-stability across platforms and runtime versions.
- Round trip: `Write(Parse(bytes))` is byte-identical for canonical files, with an unknown top-level
  property, an unknown `ext` namespace, an unknown property in `ext["total-recall"]`, in `placement` and in `origin`, and an unknown `source` value (which also survives a tags patch) (including a nested
  object, an array, numbers like `1.50` and `1e3`, `true`, `null` and an escaped string), and no BOM, LF
  endings and a trailing newline.
- Patch: a table-driven test applies each part alone and diffs the old and new bytes line by line, expecting
  only the named properties (and `updated_at` for content) to differ; an absent part changes nothing; a
  sticky patch on a colder record writes tier hot and needs `TierStampIfRaised`; sticky plus tier together
  stays hot; a patch of a hand-edited record yields the canonical form; each row of the tier/sticky table; a content-only patch of a record stored as `sticky: true` with a warm tier leaves that tier and its stamp as stored; the same `ParsedRecord` patched twice
  gives the same bytes (it is not mutated).
- `Parse` rejects, without throwing: an oversize file (a deeply nested or huge one is rejected before it is
  parsed), invalid JSON, an array root, a BOM, trailing content, nesting over 64, `tags` as a string or with a
  non-string element, a number for `content`, a missing stamp, an id that does not match the path, a
  **duplicate key** at the top level, inside `ext`, inside unknown content, an escaped
  duplicate (`"cont\u0065nt"` next to `"content"`), nesting of exactly 64 (accepted) and 65 (rejected), invalid
  UTF-8 bytes, a lone-surrogate escape (`"\ud800"`) in an id, in content, in a tag, in an unknown value and in
  an unknown property name. `Write` of a record with a lone surrogate gives
  `NotEncodable`.
- Accepts CRLF, emoji, accented and CJK content and writes it back identically; a record whose content is
  100,000 U+0001 characters (600,000 bytes serialized) is accepted by `Parse` (under 1 MiB) and blocked by
  `checkWritable` (over 256 KiB).
- Structure (codec level): an `origin` missing `tool` or `hash` is `WrongType`; a record that is both of an unknown format and has a duplicate key reports `DuplicateKey` (structure first).
- Tags and patch details: `Parse` accepts unsorted and duplicated tags and `Write`/`Patch` write them sorted and
  unique in UTF-16 code-unit order (a tag with U+FFFD sorts after one with an emoji, where UTF-8 byte order
  would put it before); a file stored as `sticky: true` with a warm tier round-trips through
  `Write(Parse(bytes))` unchanged (the stored tier is written, and `Notes` holds the normalisation); a
  hand-edited `updated_at` that disagrees with the content stamp is rewritten to the stamp's time on a no-content
  patch; a content patch without `Source` keeps an unknown source, `Set` replaces it, `Clear` removes it;
  `TierStampIfRaised` is ignored when a `Tier` part is present; `Patch` returns the post-patch record.
- Parity: the codec's `source` names equal the strings `SourceToolMapping.ToDbValue` writes for every case, and
  its `entry_type` names equal `EntryTypeMapping.ToDbValue(x).ToLowerInvariant()` for every case.

The Core and Infrastructure test projects run in `.github/workflows/dotnet-ci.yml` (on `ubuntu-latest`);
Windows and macOS are exercised only by the release publish, so cross-platform byte-stability rests on the
golden-bytes test being platform-independent. The AOT check is a local
`dotnet publish -c Release -r linux-x64 -p:PublishAot=true` that adds no `IL` warning (the release workflow
publishes all four runtimes).

## Risks

- A hand-written writer is more code than `Utf8JsonWriter`; the golden-bytes test and the round-trip test are
  its guard, and it is small (escape rule above plus indentation).
- Preserving unknown content must not reorder, re-escape or drop it; the round-trip and table-driven patch
  tests are the guard.
- `tests/TotalRecall.Core.Tests` is an F# project with an explicit compile list; the three new source files
  and three new test files are added to the respective project files in dependency order.
- `Stamp` and `Clock` use distinct field names (`TimeMs`, `ClockMs`) so F# record inference stays unambiguous.

## Documentation

- `src/TotalRecall.Core/AGENTS.md`: three rows in the module table (`SyncStamp.fs`, `SyncRecord.fs`,
  `SyncValidation.fs`); `src/TotalRecall.Infrastructure/AGENTS.md`: one row for `GitSync/`.
- `docs/superpowers/specs/2026-11-15-git-sync-design.md` (the umbrella): the record example shows `created_at`
  and `updated_at` without milliseconds and the manifest example is unchanged; this milestone fixes the record
  timestamps to milliseconds, `entry_type` to lowercase and `updated_at` as derived, so the umbrella's example
  and its "Repository layout" text are corrected to match.
- `CHANGELOG.md` is **not** changed: nothing user-visible happens in this milestone.
