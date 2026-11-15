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
  `TotalRecall.Infrastructure` (namespace `TotalRecall.Infrastructure.GitSync`, `RecordCodec`), because it
  needs `System.Text.Json`.
- The record's wire format is the umbrella spec's. This document fixes what it left open: timestamps are
  ISO-8601 UTC with milliseconds (`yyyy-MM-ddTHH:mm:ss.fffZ`) for `created_at` and `updated_at`; `entry_type`
  values are the lowercase DU names (`correction`, `preference`, `decision`, `surfaced`, `imported`,
  `compacted`, `ingested`); `tier` values are `hot`, `warm`, `cold`; `source` values are the seven tool names
  `SourceToolMapping` writes (`claude-code`, `copilot-cli`, `opencode`, `cursor`, `cline`, `hermes`,
  `manual`).
- Stamps are required on a record: `content_stamp`, `placement.tier_stamp` and `placement.sticky_stamp`. A
  record with a missing or unparsable stamp is rejected.

## Design

### Stamps (`SyncStamp.fs`, module `TotalRecall.Core.SyncStamp`)

```fsharp
type Stamp = { TimeMs: int64; Counter: int; Machine: string }   // Machine: 8 lowercase hex chars
type Clock = { TimeMs: int64; Counter: int }                    // the machine's persisted clock
```

- **Format:** `<yyyy-MM-ddTHH:mm:ss.fffZ>.<counter, 6 digits>.<machine>`, for example
  `2026-11-12T09:14:03.512Z.000003.4f2a9c1e`. `Stamp.format` and `Stamp.tryParse` are strict inverses:
  `tryParse` accepts exactly that shape (a four-digit year, a valid date, a six-digit counter 000000 to
  999999, a machine of exactly eight lowercase hex characters) and returns `None` for anything else; it never
  throws.
- **Order:** `Stamp.compare` orders by (`TimeMs`, `Counter`, `Machine` compared ordinally). Two distinct
  machines' stamps are never equal.
- **Constants:** `maxCounter = 999_999`, `driftLimitMs = 86_400_000` (24 hours).
- `Clock.issue (machine: string) (nowMs: int64) (clock: Clock option) (baseStamp: Stamp option) : IssueResult`
  where `IssueResult = { Stamp: Stamp; Clock: Clock; FarFuture: bool }`. Let `t` be the largest of `nowMs`,
  the clock's time and the base stamp's time. The counter is `0` when neither the clock nor the base stamp has
  time `t`, else one more than the larger counter among those that do. If the counter would exceed
  `maxCounter`, the time becomes `t + 1` and the counter `0`. The new stamp is therefore strictly greater than
  the clock and than the base stamp. `Clock` in the result is the new stamp's time and counter, **unless**
  that time is more than `driftLimitMs` ahead of `nowMs`: then the input clock is returned unchanged
  (`None` becomes a clock of `nowMs`, counter 0) and `FarFuture` is true. (This is how a field whose base
  stamp is far in the future can still be superseded without dragging the machine's clock into the future.)
- `Clock.observe (nowMs: int64) (clock: Clock option) (seen: Stamp) : ObserveResult` with
  `ObserveResult = { Clock: Clock; FarFuture: bool }`: the clock becomes the greater of the current clock and
  the seen stamp's (time, counter), unless the seen stamp is more than `driftLimitMs` ahead of `nowMs`: then
  the clock is unchanged and `FarFuture` is true.
- `Clock.guardStartup (nowMs: int64) (clock: Clock option) : Clock option * bool`: a persisted clock more than
  `driftLimitMs` ahead of `nowMs` is reset to `{ TimeMs = nowMs; Counter = 0 }` and the flag is true.
- `Clock.serialize`/`Clock.tryDeserialize` give the clock a string form (`<time ms>.<counter>`) for `_meta`
  (0b-3 stores it).

### Record model (`SyncRecord.fs`, module `TotalRecall.Core.SyncRecord`)

```fsharp
type Placement = { Tier: Tier; TierStamp: Stamp; Sticky: bool; StickyStamp: Stamp }
type Origin = { Tool: string; Hash: string }
type SyncRecord =
    { Id: string; Content: string; EntryType: EntryType; Tags: string list
      Source: SourceTool option; CreatedAtMs: int64; UpdatedAtMs: int64
      Project: string option; Scope: string option
      Placement: Placement; ContentStamp: Stamp
      Origin: Origin option; WrittenBy: string option }
```

with `Tier`, `EntryType` and `SourceTool` the existing Core types. Helpers in the module: `entryTypeName` / `tryParseEntryType`, `sourceToolName` / `tryParseSourceTool` (the
lowercase names above; `None` for an unknown name) and `tierName` / `tryParseTier` / `tierRank` (hot is the
warmest). `UpdatedAtMs` is, by definition, the
time of `ContentStamp`; the codec writes it that way.

### Validation (`SyncValidation.fs`, module `TotalRecall.Core.SyncValidation`)

**Id and path.**
- `isEligibleId id` is true when `id` matches `^[0-9a-f][0-9a-f-]{7,63}$`.
- `shard id` is the first two characters; `recordPath id` is `memories/<shard>/<id>.json`;
  `idOfPath path` returns the id for a path of exactly that form and `None` otherwise (so `memories/3f/3f9c….json`
  gives the id and `memories/../x.json`, a wrong shard, a nested path or a different extension give `None`).

**Reading.** A `RawSyncRecord` is what the codec extracts from JSON with every value still a string, number or
boolean as written (no rule applied). `validate (path: string) (sizeBytes: int) (raw: RawSyncRecord) :
Result<ValidRecord, Reject>` applies, in this order, and returns the first failure:
1. `sizeBytes` at most 1 MiB (1,048,576), else `Oversize`;
2. `format` equals `total-recall/memory/v1`, else `UnknownFormat`;
3. `id` is eligible, **and** equals `idOfPath path` (so the id, the file name and the shard agree), else
   `BadId` or `IdPathMismatch`;
4. `tier` is `hot`, `warm` or `cold`, else `BadTier`; `entry_type` is one of the seven names, else
   `BadEntryType`;
5. `tags` has at most 256 entries, each at most 256 UTF-16 code units, else `BadTags`;
6. `created_at`, `updated_at` parse as `yyyy-MM-ddTHH:mm:ss.fffZ` and `content_stamp`, `tier_stamp`,
   `sticky_stamp` parse with `Stamp.tryParse`, else `BadTimestamp field` or `BadStamp field`; a missing one is
   `MissingField name`;
7. `source`, if present and not one of the seven names, is **dropped** (becomes `None`), not an error.

`ValidRecord = { Record: SyncRecord; Notes: Note list }` where `Note` is `StickyNormalisedToHot` (the record said
`sticky: true` with a colder tier; the record's tier is raised to hot) or `UnknownSourceDropped of string`.
`Reject` is a union of the failures above plus `WrongType of string` (the codec reports a JSON value of the
wrong type for a field, for example `tags` not an array of strings) and `NotJson of string`; `Reject.describe`
gives a one-line human message (used by `status` later). `validate` never throws, for any input.

**Writing.** `checkWritable (record: SyncRecord) (serializedBytes: int) : Block option` returns a block reason
when the entry may not be pushed: `TooLarge` (serialized over 262,144 bytes), `TooManyTags` (over 64),
`TagTooLong` (a tag over 128 UTF-16 code units), or `IneligibleId`. Else `None`.

### The codec (`RecordCodec.cs`, namespace `TotalRecall.Infrastructure.GitSync`)

- `Parse(ReadOnlySpan<byte> utf8, string path) : ParseResult` where `ParseResult` is a valid record plus the
  parsed JSON it came from (kept so a patch can preserve what it does not understand) or a `Reject`. Invalid
  JSON, a non-object root, a wrong JSON type for a known field, a BOM, and trailing content after the root
  object are all rejections (never exceptions). It builds a `RawSyncRecord` and calls `validate`.
- `Write(SyncRecord) : byte[]` writes the canonical form: UTF-8 without a BOM, two-space indent, LF line
  endings, a trailing newline, properties in the order of the umbrella spec's example (top level:
  `format`, `id`, `content`, `entry_type`, `tags`, `source` (omitted when absent), `created_at`, `updated_at`,
  `ext`; inside `ext["total-recall"]`: `project`, `scope` (each omitted when absent), `placement`
  (`tier`, `tier_stamp`, `sticky`, `sticky_stamp`), `content_stamp`, `origin` (omitted when absent),
  `written_by` (omitted when absent)). The JSON encoder keeps non-ASCII text as UTF-8 (the relaxed encoder),
  so a record is readable on GitHub and identical on every platform.
- **Unknown content is kept.** A top-level property, an `ext` namespace other than `total-recall`, or a
  property inside `ext["total-recall"]` that the codec does not know is carried in the parsed value and
  written back after the known properties of its object, in the order it was read.
- `Patch(ParsedRecord, RecordPatch) : byte[]` changes only the groups named by the patch and writes the
  canonical form. `RecordPatch` has optional parts: `Content` (content, project, scope, entry type, source,
  created-at; with its new `content_stamp`, which also sets `updated_at` to the stamp's time), `Tags`,
  `Sticky` (the new flag and its `sticky_stamp`; when it sets sticky on a record whose tier is colder than hot
  it also sets the tier to hot with a supplied `tier_stamp`), and `Tier` (a tier and its `tier_stamp`). A part
  that is absent leaves its properties exactly as they were, including unknown content. A patch on a record
  already in canonical form changes only the bytes of the patched properties; a patch on a hand-edited
  (non-canonical) record also normalises its formatting once.
- Round trip: for a record file in canonical form, `Write(Parse(bytes))` with unknown content included returns
  the same bytes. The codec uses `Utf8JsonWriter` and `JsonDocument`/`JsonNode`, no reflection serialization
  (AOT-safe).

## Testing

F# tests in `tests/TotalRecall.Core.Tests` (Expecto, with FsCheck for properties):
- `SyncStampTests`: format and parse are inverses (property); strict rejection of each malformed shape (bad
  date, two-digit year, five- or seven-digit counter, upper-case or short machine, trailing text); order by
  time, then counter, then machine; `issue` is strictly greater than the clock and the base stamp for every
  input (property) and the clock never goes backwards; a slow clock (the "A pins at 10:00:00.500, B unpins at
  .200 by a slow clock" case) still orders the unpin after the pin; a fast clock; equal times on two machines
  order by machine; counter overflow advances the time; a far-future stamp is not adopted into the clock
  (`issue` and `observe`) and sets `FarFuture`; `guardStartup` resets a clock a year ahead and leaves a sane
  one alone; clock serialize and deserialize round-trip.
- `SyncValidationTests`: the id rule (eligible and ineligible ids, including path characters, uppercase, one
  character, 65 characters, a leading dash); `recordPath` and `idOfPath` for good, wrong-shard, nested,
  traversal and wrong-extension paths; each rule of `validate` in order (oversize first, then format, id and
  path, tier, entry type, tags, timestamps, stamps, missing fields); an unknown `source` is dropped with a
  note; `sticky: true` with a colder tier is normalised to hot with a note; a random raw record never makes
  `validate` throw (property); `checkWritable` at each limit (exactly at and one over).

C# tests in `tests/TotalRecall.Infrastructure.Tests/GitSync/RecordCodecTests.cs` (xUnit, no Integration trait):
- a canonical record round-trips byte-identically, with unknown top-level fields, an unknown `ext` namespace
  and an unknown property in `ext["total-recall"]` included, and the bytes have no BOM, LF endings and a
  trailing newline;
- every patch part changes only its own properties and leaves every other byte (a table-driven test that
  diffs old and new bytes line by line), a sticky patch on a colder record also writes tier hot, an absent
  part changes nothing, and a patch of a hand-edited record yields the canonical form;
- `Parse` rejects, without throwing: invalid JSON, an array root, a BOM, trailing content, `tags` as a
  string or with a non-string element, a number for `content`, a missing stamp, an id that does not match the
  path; accepts a valid record with CRLF and non-ASCII (emoji, accented, CJK) content and writes it back
  identically; a 100,000-character content record is accepted by `Parse` and flagged by `checkWritable` when
  its serialized size passes 256 KiB;
- parity: the codec's `source` and `entry_type` names equal the strings `SourceToolMapping` and
  `EntryTypeMapping` write for every case.

The projects build under `PublishAot` with no new warning.

## Risks

- `Utf8JsonWriter` options (`Indented`, `IndentSize = 2`, `NewLine = "\n"`) must give the same bytes on
  Windows and Linux; the tests run on the CI matrix's platforms, and the round-trip test is the guard.
- Preserving unknown properties through `JsonNode` must not reorder or re-escape known ones; the table-driven
  patch test is the guard.
- `tests/TotalRecall.Core.Tests` is an F# project with an explicit compile list; the three new files and
  three test files are added to the respective project files in dependency order.

## Documentation

- `src/TotalRecall.Core/AGENTS.md`: three rows in the module table (`SyncStamp.fs`, `SyncRecord.fs`,
  `SyncValidation.fs`); `src/TotalRecall.Infrastructure/AGENTS.md`: one row for `GitSync/`.
- `CHANGELOG.md` is **not** changed: nothing user-visible happens in this milestone.
