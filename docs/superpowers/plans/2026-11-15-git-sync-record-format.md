# Git sync 0b-1: record format, clock and validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the pure sync-record logic (stamps, hybrid clock, validation) in F# Core and the JSON codec (read, canonical write, patch) in C# Infrastructure, with tests, wired into nothing.

**Architecture:** Three F# modules after `Types.fs` in `TotalRecall.Core` (`SyncStamp`, `SyncRecord`, `SyncValidation`) hold every rule that needs no I/O. A C# `GitSync` folder in `TotalRecall.Infrastructure` holds a reader (size check, strict pre-pass, raw extraction), a hand-written canonical writer and a patcher, all on `Utf8JsonReader`/`JsonDocument` (no reflection serialization).

**Tech Stack:** F# (Expecto, FsCheck 2.16.6) in `tests/TotalRecall.Core.Tests`; C# net10 NativeAOT (xUnit) in `tests/TotalRecall.Infrastructure.Tests`.

**Spec:** `docs/superpowers/specs/2026-11-15-git-sync-record-format-design.md` (read it first; this plan only records what the spec leaves to the implementer). The umbrella is `docs/superpowers/specs/2026-11-15-git-sync-design.md`. Issue #102.

## Global Constraints

- The spec file is read-only for the whole run. Commits carry no trailer and no AI attribution; commit message style is plain imperative sentences (see `git log`).
- Core has no I/O and no mutable state; Infrastructure code has `Nullable` enabled and is AOT-safe (no `JsonSerializer`, no reflection). No new package.
- Constants, verbatim from the spec: `maxCounter = 999_999`; `maxTimeMs = 253_402_300_799_999`; `driftLimitMs = 86_400_000`; `CreatedAtMs` domain `[-62_135_596_800_000, 253_402_300_799_999]`; stamp `TimeMs` domain `[0, maxTimeMs]`; reader cap 1,048,576 bytes; JSON depth 64 (root object is level 1); tags on read at most 256 entries of at most 256 UTF-16 code units; writer limits 262,144 bytes, 64 tags, 128 code units; reject text truncated to 64 UTF-16 code units; format strings `total-recall/memory/v1`, `yyyy-MM-ddTHH:mm:ss.fffZ`, stamp `<timestamp>.<6-digit counter>.<8 lowercase hex>`.
- Naming: the spec writes `Stamp.format`, `Clock.issue` for readability. In code the three modules are `SyncStamp`, `SyncRecord`, `SyncValidation` (as the spec's file/module names say), with the types declared inside them and functions named as in each task's Interfaces block.
- New F# source files go right after `Types.fs` in `TotalRecall.Core.fsproj`; new F# test files go before `Program.fs` in `TotalRecall.Core.Tests.fsproj`. C# tests use no `Integration` trait.
- Test commands: `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj` and `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync"`. A fresh worktree needs `npm ci --ignore-scripts` once (sqlite-vec native library) before the Infrastructure tests build.

## Review Focus

Inputs the spec implies but no single task's tests would otherwise exercise; each is pinned in the task named in brackets.

1. An id in upper case or with a trailing newline in a file name on a case-insensitive file system (`3F9C...json`): must be `BadId`, never matched to a lower-case record [Task 3, 5].
2. A hand-edited file with CRLF line endings, a BOM or stray whitespace between tokens: `Parse` accepts the CRLF/whitespace file (whitespace is insignificant JSON), rejects the BOM, and the next write normalises it [Task 5, 6].
3. A number in unknown content that is hostile or odd (`-0`, `1E400`, `1.50`): copied as its raw text, never reformatted, never evaluated [Task 6].
4. A record exactly at each limit (1 MiB file, 256 tags, 256 code units, 64 tags written, 262,144 bytes written) and one over [Task 4, 5].
5. The same record written on two operating systems or two runtime versions: byte-identical (golden literal bytes) [Task 6].

---

### Task 1: Timestamps, machine ids and stamps

**Files:**
- Create: `src/TotalRecall.Core/SyncStamp.fs`; modify `src/TotalRecall.Core/TotalRecall.Core.fsproj` (compile entry after `Types.fs`)
- Create: `tests/TotalRecall.Core.Tests/SyncStampTests.fs`; modify `TotalRecall.Core.Tests.fsproj` (entry before `Program.fs`)

**Interfaces:**
- Produces, `module TotalRecall.Core.SyncStamp`:
  - `Timestamp.minMs/maxMs`, `Timestamp.format : int64 -> string` (precondition in range, else `ArgumentOutOfRangeException`), `Timestamp.tryParse : string -> int64 option` (strict `yyyy-MM-ddTHH:mm:ss.fffZ`, ASCII digits in fixed positions, real calendar date and time, year 1 to 9999, no second 60), `Timestamp.inRange : int64 -> bool`.
  - `type MachineId` (private case) with `MachineId.tryCreate : string -> MachineId option` (exactly 8 chars of `0-9a-f`) and `MachineId.value`.
  - `type Stamp = { TimeMs: int64; Counter: int; Machine: MachineId }` (field order gives the structural ordering the spec wants); `maxCounter`, `maxTimeMs`; `Stamp.format : Stamp -> string`, `Stamp.tryParse : string -> Stamp option` (also rejects `TimeMs < 0` and counter out of `0..999_999`), `Stamp.compare : Stamp -> Stamp -> int`, `Stamp.inDomain : Stamp -> bool`. In F# declare modules with the same name as their type (`module Stamp`) after the type.

- [ ] **Step 1: Write the failing tests** in `SyncStampTests.fs` (module `TotalRecall.Core.Tests.SyncStampTests`, `testList "SyncStamp"`):
  - `Stamp.format` of `{TimeMs = <2026-11-12T09:14:03.512Z>; Counter = 3; Machine = 4f2a9c1e}` is exactly `2026-11-12T09:14:03.512Z.000003.4f2a9c1e`; property: `tryParse (format s) = Some s` for generated in-domain stamps; same for `Timestamp` over the whole `CreatedAtMs` domain.
  - Each of these gives `None` and never throws: `2026-13-12T...` (bad month), `2026-02-30T...`, `26-11-12T...`, counter `00003`/`0000003`, machine upper case / 7 chars / 9 chars, trailing text, leading or trailing space, `+`/`-` sign in the counter, a fullwidth digit, `...:60.000Z`, year 1969 (stamp only), a lower-case `t` or `z`.
  - `MachineId.tryCreate "4F2A9C1E"` is `None`; `"4f2a9c1e"` is `Some`.
  - Order: later time wins; same time, higher counter wins; same time and counter, machine compares ordinally (`"0000000a"` before `"0000000b"`); two stamps with different machines never compare 0.
- [ ] **Step 2: Run** the Core tests; expected: compile failure (module missing).
- [ ] **Step 3: Implement** `SyncStamp.fs` per the Interfaces block. Parse by fixed positions (length 40 for a stamp, 24 for a timestamp); build the millisecond value from validated parts (e.g. `DateTime(y,m,d,h,mi,s,DateTimeKind.Utc)` after an explicit month/day check, plus milliseconds), never `DateTime.Parse`.
- [ ] **Step 4: Run** the Core tests; expected: all pass.
- [ ] **Step 5: Commit** `Add sync timestamps, machine ids and stamps`.

### Task 2: The hybrid logical clock

**Files:** modify `src/TotalRecall.Core/SyncStamp.fs`, `tests/TotalRecall.Core.Tests/SyncStampTests.fs`.

**Interfaces:**
- Consumes: Task 1 types.
- Produces (same module): `type Clock = { ClockMs: int64; ClockCounter: int }`; `type ClockError = Exhausted`; `type IssueResult = { Stamp: Stamp; Clock: Clock option; FarFuture: bool }`; `type ObserveResult = { Clock: Clock option; FarFuture: bool }`; `type GuardResult = { Clock: Clock option; Reset: bool }`; `Clock.issue : MachineId -> int64 -> Clock option -> Stamp option -> Result<IssueResult, ClockError>`; `Clock.observe : int64 -> Clock option -> Stamp -> ObserveResult`; `Clock.guardStartup : int64 -> Clock option -> GuardResult`; `Clock.serialize : Clock -> string` (`<ms>.<counter>`); `Clock.tryDeserialize : string -> Clock option` (rejects malformed, signs, spaces, out-of-range). `driftLimitMs`. The record types sharing field name `Clock` need type annotations at construction.

- [ ] **Step 1: Write the failing tests** (all in `SyncStampTests.fs`):
  - Property (FsCheck, generators restricted to the domain and to `nowMs` in `[0, maxTimeMs]`): for any clock option and base option, `Clock.issue` is `Ok r` and `r.Stamp` is strictly greater than the clock (compared as time, then counter) and than the base stamp; the returned clock is never behind the input clock.
  - Slow clock: machine A issues at `now = T+500` a stamp S; machine B calls `Clock.observe (T+200) None S` then `Clock.issue B (T+200) clock (Some S)`: the result sorts after `S`.
  - Fast clock, equal times on two machines, counter 0 when neither clock nor base has the winning time, counter+1 when one does, the larger of both when both do.
  - Overflow: clock at `(t, 999_999)`, base at `(t, 999_999)`, both, and `now = t`: result has time `t+1`, counter 0.
  - Top of range: base `(maxTimeMs, 999_999)` gives `Error Exhausted`; base `(maxTimeMs, 0)` gives `Ok` with counter 1.
  - Drift: base exactly `now + 86_400_000` ms ahead is adopted (clock moves, `FarFuture = false`); one ms more gives `FarFuture = true` and the returned clock equals the input clock (and stays `None` when the input was `None`); the same two cases for `Clock.observe`.
  - `guardStartup`: a clock a year ahead resets to `{ClockMs = now; ClockCounter = 0}` with `Reset = true`; one exactly 24 hours ahead is left alone; `None` stays `None`.
  - `serialize`/`tryDeserialize` round-trip; reject `"12"`, `"-1.0"`, `"1.1000000"`, `"1. 2"`, a time above `maxTimeMs`, and `""`.
- [ ] **Step 2: Run** the Core tests; expected: new tests fail to compile.
- [ ] **Step 3: Implement** to the spec's rules. Algorithm for `issue`: clamp `now` into `[0, maxTimeMs]`; `t` = max of now, clock time, base time; `c` = 0 if neither the clock nor the base has time `t`, else 1 + the larger counter among those that do; if `c > maxCounter` then `(t+1, 0)`; if the time exceeds `maxTimeMs` return `Error Exhausted`; `FarFuture` = time > `now + driftLimitMs`; the returned clock is the input clock when `FarFuture`, else the new `(t, c)`.
- [ ] **Step 4: Run** the Core tests; expected: all pass.
- [ ] **Step 5: Commit** `Add the hybrid logical clock for sync stamps`.

### Task 3: Record model, name helpers, ids and paths

**Files:**
- Create: `src/TotalRecall.Core/SyncRecord.fs`, `src/TotalRecall.Core/SyncValidation.fs` (the id/path/text part); modify `TotalRecall.Core.fsproj` (after `SyncStamp.fs`, in that order)
- Create: `tests/TotalRecall.Core.Tests/SyncRecordTests.fs`, `tests/TotalRecall.Core.Tests/SyncValidationTests.fs`; modify the tests fsproj

**Interfaces:**
- Consumes: `SyncStamp.Stamp`; `Tier`, `EntryType`, `SourceTool` from `Types.fs`.
- Produces, `module TotalRecall.Core.SyncRecord`: `Placement`, `Origin`, `SyncRecord` exactly as in the spec's record-model block (Tags sorted ordinal and unique); `entryTypeName`/`tryParseEntryType` (lowercase names), `sourceToolName`/`tryParseSourceTool` (the seven wire names), `tierName`/`tryParseTier`, `tierRank` (`Hot` 0, `Warm` 1, `Cold` 2), `normaliseTags : string list -> string list` (sort by `String.CompareOrdinal`, distinct).
- Produces, `module TotalRecall.Core.SyncValidation` (first part): `isEligibleId : string -> bool`, `shard : string -> string`, `recordPath : string -> string`, `idOfPath : string -> string option`, `sanitizeText : string -> string` (the spec's "Truncation of text from a record"), `hasLoneSurrogate : string -> bool`.

- [ ] **Step 1: Write the failing tests.**
  - `SyncRecordTests`: every name function round-trips for all cases; unknown names (`"Correction"`, `""`, `"windsurf"`, `"HOT"`) give `None`; `tierRank` order; `normaliseTags ["b";"a";"b";"\uFFFD";"\U0001F600"]` is `["a";"b";"\U0001F600";"\uFFFD"]` (UTF-16 code-unit order puts the surrogate pair before U+FFFD).
  - `SyncValidationTests`, id rule: eligible: `3f9c2a7e`, a 64-char id, a UUID; ineligible: 7 chars, 65 chars, `3F9C2A7E`, `-3f9c2a7e`, `3f9c2a7e\n`, `3f9c2a7e\u0000`, `3f9c 2a7e`, `3f9c2a7e` with an Arabic-Indic digit, `../etc/pass`. Path: `recordPath "3f9c2a7e-1111"` is `memories/3f/3f9c2a7e-1111.json`; `idOfPath` returns the id for that and `None` for a wrong shard (`memories/aa/3f9c2a7e-1111.json`), nested, `memories/../x.json`, `memories/3f/3f9c2a7e-1111.JSON`, a different directory, a backslash path.
  - `sanitizeText`: 64 code units kept, 65 cut to 64; a surrogate pair straddling the cut is dropped whole; a lone surrogate becomes `?`; `\u0007`, `\u007f`, `\u009b`, `\u2028`, `\u2029` become `?`.
- [ ] **Step 2: Run** the Core tests; expected: compile failure.
- [ ] **Step 3: Implement** `SyncRecord.fs` and the first part of `SyncValidation.fs`. `isEligibleId` is a character loop (no regex). `idOfPath` splits on `/` and requires exactly `memories`, a two-character shard, and `<id>.json` with `isEligibleId id` and `shard id = shard`.
- [ ] **Step 4: Run** the Core tests; expected: all pass.
- [ ] **Step 5: Commit** `Add the sync record model, id rules and text sanitiser`.

### Task 4: Validation and writer limits

**Files:** modify `src/TotalRecall.Core/SyncValidation.fs`, `tests/TotalRecall.Core.Tests/SyncValidationTests.fs`.

**Interfaces:**
- Consumes: Tasks 1 and 3.
- Produces (same module): `RawField<'a> = Absent | WrongType | Value of 'a`; `RawOrigin = { Tool: string; Hash: string }`; `RawSyncRecord` with one `RawField` per field of the spec's list (`Format`, `Id`, `Content`, `EntryType`, `CreatedAt`, `UpdatedAt`, `Tags: RawField<string list>`, `Tier`, `Sticky: RawField<bool>`, `TierStamp`, `StickyStamp`, `ContentStamp`, `Source`, `Project`, `Scope`, `WrittenBy`, `Origin: RawField<RawOrigin>`); `Reject` (the spec's union, strings sanitised through `sanitizeText` by `validate`); `Note = StickyNormalisedToHot | UnknownSourceDropped of string`; `Block = TooLarge | TooManyTags | TagTooLong | IneligibleId | NotEncodable | ClockExhausted`; `ValidRecord = { Record: SyncRecord; Notes: Note list; StoredTier: Tier }`; `validate : string -> int -> RawSyncRecord -> Result<ValidRecord, Reject>` (path, size in bytes, raw); `Reject.describe : Reject -> string`; `checkWritable : SyncRecord -> int -> Block option`. Field paths named in `WrongType`/`MissingField`/`BadTimestamp`/`BadStamp` are the JSON paths (`format`, `id`, `ext.total-recall.placement.tier`, ...).

- [ ] **Step 1: Write the failing tests** (`SyncValidationTests`): a helper `goodRaw` (a valid raw record for path `memories/3f/3f9c2a7e-1111.json`) and one test per rule, each changing one field of `goodRaw`:
  - order: oversize (1,048,577) is `Oversize` even when the format is also wrong; an unknown format with a missing id is `UnknownFormat`; format `WrongType`/`Absent`; id `BadId` vs `IdPathMismatch`; then each required field absent and wrong-typed in the spec's order (`Content`, `EntryType`, `Tags`, `Tier`, `Sticky`, `CreatedAt`, `UpdatedAt`, `ContentStamp`, `TierStamp`, `StickyStamp`), and the first failure wins when two fields are bad.
  - boundaries: size exactly 1,048,576 accepted; 256 tags of 256 code units accepted, 257 tags or a 257-unit tag is `BadTags`.
  - `Source = Value "windsurf"` accepted with `Record.Source = None` and `[UnknownSourceDropped "windsurf"]`; a 100-character unknown source is truncated to 64 in the note; `Sticky = true` with `Tier = "warm"` gives `Record.Placement.Tier = Hot`, `StoredTier = Warm`, `[StickyNormalisedToHot]`; an unsorted duplicated tag list is stored sorted and unique.
  - `Reject.describe` of every case is a non-empty single line without control characters.
  - Property: `validate` never throws for random raw records (strings from FsCheck including lone surrogates and control characters).
  - `checkWritable`: serialized bytes 262,144 `None` and 262,145 `TooLarge`; 64 tags `None`, 65 `TooManyTags`; a 128-unit tag `None`, 129 `TagTooLong`; an ineligible id `IneligibleId`; a lone surrogate in content, in a tag, in the id, in project, in scope, in `WrittenBy` and in `Origin` each `NotEncodable`; `CreatedAtMs` one below the domain minimum and one above the maximum, and a stamp with a counter 1,000,000, `NotEncodable`; the first matching block in the order TooLarge, TooManyTags, TagTooLong, IneligibleId, NotEncodable is returned.
- [ ] **Step 2: Run** the Core tests; expected: new tests fail to compile.
- [ ] **Step 3: Implement** `validate` as ordered steps with an early return (`result { }`-style or nested matches); required-field and type handling through one small helper taking the field path.
- [ ] **Step 4: Run** the Core tests; expected: all pass.
- [ ] **Step 5: Commit** `Add sync record validation and writer limits`.

### Task 5: The reader (`RecordReader`, `ParsedRecord`)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/ParsedRecord.cs`, `RecordReader.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/RecordReaderTests.cs`

**Interfaces:**
- Consumes: Task 4's `RawSyncRecord`, `validate`, `Reject`, `Note`; Task 3's names.
- Produces (namespace `TotalRecall.Infrastructure.GitSync`):
  - `public sealed class ParsedRecord` with `SyncRecord Record`, `FSharpList<Note> Notes`, `Tier StoredTier`, `string? UnknownSource`, and internal unknown content: ordered `(string Name, JsonElement Value)` lists for the top level, `ext` namespaces other than `total-recall`, `ext["total-recall"]`, `placement` and `origin` (each `JsonElement` a `Clone()` from a disposed document; the class is immutable).
  - `public sealed class ParseResult` with `ParsedRecord? Parsed` and `Reject? Rejected`; `public static class RecordReader { public static ParseResult Parse(ReadOnlySpan<byte> utf8, string path); }`.

- [ ] **Step 1: Write the failing tests** (xUnit; build inputs with a small helper that renders the canonical sample record, see Task 6 for the sample; until Task 6 exists, tests use hand-written JSON strings):
  - A valid record parses; its `Record`, `StoredTier`, `Notes` and `UnknownSource` are right; CRLF line endings and extra whitespace parse; emoji, accented and CJK content parse.
  - Rejections (each: no exception, the expected `Reject` case): 1,048,577 bytes of valid JSON padded with whitespace is `Oversize`; 1,048,577 bytes that are not JSON, and a 5,000-level nest of `[`, are `Oversize` / `NotJson` without being parsed deeply; a BOM prefix; invalid JSON; an array root; trailing content (`{}x`, `{} {}`); depth 64 accepted and 65 `NotJson` (root object = level 1, nest inside an unknown property); a duplicate key at the top level, inside `ext`, inside `placement`, inside unknown content, and an escaped duplicate (`"cont\u0065nt"`); invalid UTF-8 bytes in a string; a lone `\ud800` in an id, content, a tag, an unknown value and an unknown property name; `tags` as a string and with a non-string element; `content` as a number; `origin` with only `tool`; a missing stamp; an id that does not match the path; a record that has both an unknown `format` and a duplicate key reports `DuplicateKey`.
  - Unknown content: unknown properties at all five levels and an unknown `source` string are present in the `ParsedRecord` in file order.
- [ ] **Step 2: Run** the filtered Infrastructure tests; expected: compile failure.
- [ ] **Step 3: Implement** `RecordReader.Parse`: (1) length check; (2) BOM check; (3) a `Utf8JsonReader` pre-pass (`MaxDepth = 64`, trailing content disallowed) that keeps a stack of per-object name sets compared after decoding (`GetString()`), decodes every property name and string value inside try/catch (`JsonException`, `InvalidOperationException`, `ArgumentException`), and rejects a lone surrogate found by `SyncValidation.hasLoneSurrogate`; (4) `JsonDocument.Parse` with `MaxDepth = 64`, extract the `RawSyncRecord` (each field `Absent` / `WrongType` / `Value`; `ext`, `ext["total-recall"]`, `placement` or `origin` that is present but not an object makes the fields inside it `WrongType`; an `origin` without two string members is `WrongType`), collect unknown content, and call `validate path sizeBytes raw`; (5) wrap in `ParsedRecord` (copy `StoredTier`, `Notes`, the raw unknown `source` string when `Notes` holds `UnknownSourceDropped`).
- [ ] **Step 4: Run** the filtered Infrastructure tests; expected: pass.
- [ ] **Step 5: Commit** `Add the sync record reader`.

### Task 6: The canonical writer

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/RecordWriter.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/RecordWriterTests.cs`

**Interfaces:**
- Consumes: Task 5's `ParsedRecord`; Task 3/4 types.
- Produces: `public sealed record WriteResult(byte[]? Bytes)` with `bool NotEncodable => Bytes is null`; `public static class RecordWriter { Write(SyncRecord record); Write(ParsedRecord parsed); }`. `Write(ParsedRecord)` writes `StoredTier`, the unknown content and the unknown `source` text (when `Record.Source` is `None`), and `updated_at` as `Timestamp.format` of the content stamp's `TimeMs`. `Write(SyncRecord)` has no unknown content and writes the model's tier. Both write tags via `normaliseTags` and fail with `NotEncodable` for a lone surrogate or an out-of-domain stamp or `CreatedAtMs`; neither enforces size or tag limits.

- [ ] **Step 1: Write the failing tests:**
  - **Golden bytes:** a table of records, each with its expected output as a C# raw string literal committed in the test (LF line endings; the test asserts the file has no `\r`): minimal (no optional fields); every optional field present (`source`, `project`, `scope`, `origin`, `written_by`); content with `"` and `\`; content with `\n`, `\r\n`, a tab and `\u0001` (written `\u0001`, lower-case hex); U+007F (literal); `é`, CJK and an emoji (all literal UTF-8); an empty tag list written `[]`; unsorted duplicated tags written sorted and unique. The expected layout is the spec's (two-space indent, one property per line, `": "`, nested containers one level deeper, trailing newline). Assert the bytes equal `Encoding.UTF8.GetBytes(expected)` and that there is no BOM.
  - **Round trip:** for each golden text, `RecordWriter.Write(RecordReader.Parse(bytes, path).Parsed)` returns identical bytes; also for a record with unknown top-level properties, an unknown `ext` namespace, unknown properties in `ext["total-recall"]`, `placement` and `origin`, and nested unknown values (`{}`, `[]`, `[1,{"a":null}]`, `true`, `null`, `-0`, `1.50`, `1E400`, and an escaped string: a hand-written `"\u0041"` comes back as `A`, because unknown strings are re-escaped by the writer's rule, so that case is asserted against its own expected text and is not a byte-identical round trip). A file stored as `sticky: true` with `tier: warm` round-trips unchanged and `Notes` holds `StickyNormalisedToHot`. A hand-edited `updated_at` that disagrees with the content stamp comes back corrected.
  - `NotEncodable`: a `SyncRecord` with a lone surrogate in content, in a tag and in the id; an out-of-domain `CreatedAtMs`; a stamp counter of 1,000,000.
  - A record with 100,000 U+0001 characters writes to 600,000+ bytes (so `checkWritable` blocks it at 262,144) and `Parse` accepts it (under 1 MiB).
  - Parity: for every `SourceTool` case `RecordWriter`'s name equals `SourceToolMapping.ToDbValue`; for every `EntryType` case it equals `EntryTypeMapping.ToDbValue(x).ToLowerInvariant()`.
- [ ] **Step 2: Run** the filtered Infrastructure tests; expected: compile failure.
- [ ] **Step 3: Implement** a `StringBuilder`-based writer that applies the spec's layout and escape rules (no `Utf8JsonWriter`, no `JavaScriptEncoder`), writes unknown `JsonElement`s recursively with the same layout (numbers via `GetRawText()`), and encodes with `new UTF8Encoding(false, throwOnInvalidBytes: true)`, catching `EncoderFallbackException` as `NotEncodable`.
- [ ] **Step 4: Run** the filtered Infrastructure tests; expected: pass.
- [ ] **Step 5: Commit** `Add the canonical sync record writer`.

### Task 7: Patching

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/RecordPatcher.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/RecordPatcherTests.cs`

**Interfaces:**
- Consumes: Tasks 5 and 6.
- Produces: `Change<T>` (Keep / Set(value) / Clear); `ContentPatch(string Content, EntryType EntryType, Stamp ContentStamp, Change<string> Project, Change<string> Scope, Change<SourceTool> Source)`; `StickyPatch(bool Sticky, Stamp StickyStamp, Stamp? TierStampIfRaised)`; `TierPatch(Tier Tier, Stamp TierStamp)`; `RecordPatch(ContentPatch? Content, IReadOnlyList<string>? Tags, StickyPatch? Sticky, TierPatch? Tier)`; `PatchFailure { None, NotEncodable, MissingTierStamp }`; `PatchResult(byte[]? Bytes, SyncRecord? Record, PatchFailure Failure)`; `RecordPatcher.Patch(ParsedRecord, RecordPatch)`. Written tier follows the spec's seven-row table; the returned `Record` has `Tier = Hot` when the resulting `Sticky` is true, else the written tier. `Patch` never mutates its input and never validates its output; `created_at` and `written_by` are unchanged; `updated_at` is always the content stamp's time (the patch's when a `Content` part is present, else the file's).

- [ ] **Step 1: Write the failing tests:**
  - **Line diff:** a helper that diffs old and new text line by line. For a canonical sample, each part alone changes only: content part: `content`, `entry_type`, `project`, `scope`, `source`, `content_stamp`, `updated_at` lines; tags part: only the `tags` lines; sticky part: `sticky`, `sticky_stamp` lines (and, raised, `tier`, `tier_stamp`); tier part: `tier`, `tier_stamp` lines. An all-`null` patch returns the original bytes.
  - Each of the seven rows of the spec's table (including `Sticky` raised without `TierStampIfRaised` giving `MissingTierStamp`; `TierStampIfRaised` ignored when a `Tier` part is present; a `Tier` part on a record stored sticky gives tier hot with the patch's tier stamp).
  - A content-only patch of a file stored `sticky: true` with `tier: warm` keeps `tier: warm` and its stamp and leaves `sticky` untouched; the returned record's tier is hot.
  - Content patch without `Source` keeps an unknown source text; `Set` replaces it; `Clear` removes it; `Project` `Clear` removes the property.
  - Unknown content at every level survives every part; patching the same `ParsedRecord` twice gives the same bytes; a hand-edited file (extra whitespace, CRLF, unsorted tags) yields canonical bytes with tags sorted; a patch that would write a lone surrogate gives `NotEncodable`; the returned `Record` equals `RecordReader.Parse(result.Bytes).Parsed.Record`.
- [ ] **Step 2: Run** the filtered Infrastructure tests; expected: compile failure.
- [ ] **Step 3: Implement** by building a changed copy of the parsed state (record fields, stored tier, stamps, unknown source) and calling `RecordWriter.Write` on it; the table is one `switch` on (`Sticky` present, `Tier` present, resulting sticky, raised).
- [ ] **Step 4: Run** the filtered Infrastructure tests; expected: pass.
- [ ] **Step 5: Commit** `Add the sync record patcher`.

### Task 8: Documentation and whole-branch verification

**Files:** modify `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`.

- [ ] **Step 1:** Add three rows to the module table of `src/TotalRecall.Core/AGENTS.md` (`SyncStamp.fs`, `SyncRecord.fs`, `SyncValidation.fs`) and one `GitSync/` row (reader, writer, patcher; note that wire names are lower-case and not the DB strings) to `src/TotalRecall.Infrastructure/AGENTS.md`. Do not touch `CHANGELOG.md`.
- [ ] **Step 2:** Run `dotnet test src/TotalRecall.sln --filter "Category!=Integration"` and `npm run test:shim`; expected: pass (three tests in `TotalRecall.Integration.Tests` that need a live server are outside this filter).
- [ ] **Step 3:** Run the AOT check `dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true`; expected: succeeds with no `IL` warning that is new compared with `origin/main`.
- [ ] **Step 4: Commit** `Document the sync record modules`.
