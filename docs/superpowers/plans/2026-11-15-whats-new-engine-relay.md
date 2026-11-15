# What's-new notice: engine and relay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The engine remembers per host which total-recall version it has announced, and `session_start` returns a first-seen or "updated" notice line plus a fixed relay instruction for the model (issue #99, ticket T2a of the what's-new notice).

**Architecture:** All logic lives in a new `TotalRecall.Infrastructure.WhatsNew` namespace (pure SemVer, host ids, sanitiser, decider and renderer; small file-backed state; a packaged-notes reader; a glue service), in C#, next to `PinnedFloorDecider`/`PinnedFloorState`. `SessionStartHandler` (Server) asks the service per call, outside the cached `SessionInitResult`, and returns `result with { WhatsNew = ... }`. Config is read by a lenient static reader beside `LoadGrowSensitiveTerms`; the positional F# `TotalRecallConfig` is not touched.

**Tech Stack:** .NET 10 / C# (xUnit 2.9.2), NativeAOT (no reflection serialization), Tomlyn, the repo's `JsonWriter` and `JsonDocument` patterns.

**Spec:** `docs/superpowers/specs/2026-11-15-whats-new-notice-design.md` (section 2 without the update checker; the host-id and relayed-notice parts of section 4). Read-only for this run. Code map used to write this plan: `.superpowers/map/engine-map.md` in the worktree (git-ignored, optional reading).

## Global Constraints

- Namespace `TotalRecall.Infrastructure.WhatsNew`, folder `src/TotalRecall.Infrastructure/WhatsNew/`; tests in `tests/TotalRecall.Infrastructure.Tests/WhatsNew/` and `tests/TotalRecall.Server.Tests/`. No new project files. New unit tests carry no Category trait. Run one class with `dotnet test <csproj> --filter "FullyQualifiedName~<Class>"`.
- AOT: no reflection JSON. Write state with `StringBuilder` + `TotalRecall.Infrastructure.Json.JsonWriter`; read with `JsonDocument` inside `try/catch` (fail-safe: any error is "no state"). Result DTO is registered in `Server/JsonContext.cs` with `[JsonSerializable]`. State files are written atomically: temp file `path + ".tmp"` then `File.Move(tmp, path, overwrite: true)`, best-effort cleanup of the temp file.
- Host ids (closed list, lower case): `claude-code, pi, copilot-cli, cursor, codex, gemini-cli, kiro, opencode, hermes, cline, windsurf, zed, vscode, other`. Client-name table: `hermes-mcp-client` -> `hermes`; a client name that already is a known host id passes through; anything else (empty, path-like, unknown) -> `other`. A host id reaches a file name only after this normalisation.
- State dir: `Path.Combine(ConfigLoader.GetDataDir(), "whats-new")` (every API takes the dir as a parameter so tests use temp dirs; tests must NOT set `TOTAL_RECALL_HOME`). Files: `seen-<host>.json` = `{"version":"<bare version>"}`; stamp `direct-<host>.json` = `{"at":"<ISO-8601 UTC>"}` (written by the next ticket; here only read). A stamp is fresh when `now - at <= 30 days`.
- Versions are bare (`4.8.0`, `4.8.0-beta.2`), compared by SemVer 2.0.0 precedence (numeric identifiers numerically, compare digit strings exactly, release above its prereleases). Dev builds (running version `1.0.0`, `unknown`, blank, or not parseable) never produce a notice and record nothing.
- Packaged notes: `Path.Combine(AppContext.BaseDirectory, "release-notes.json")` with keys `version, previous, summary, sections, url, releasesUrl` (written by `scripts/release-notes.js`). Missing, unreadable, or `version` different from the running version => treated as no notes (line has the version and no summary, releases URL `https://github.com/strvmarv/total-recall/releases`). Never throws, never deletes the file.
- Notice rules (decider): `updates.notice=false` -> none, nothing recorded. Never told (no `seen-<host>.json`) -> `first-seen`. Running > told -> `updated` (from = told version; link = notes `url` labelled `More` when notes `previous` equals the told version, else the releases URL labelled `All releases`). Running <= told -> none, record nothing. Record the running version when a notice is returned (after the handler's 30 s check).
- Lines: first-seen `total-recall <v>: <summary> All releases: <releasesUrl>`; updated `total-recall updated <from> -> <v>: <summary> <More|All releases>: <url>`; when the summary is empty drop it and its trailing space (`total-recall <v>: All releases: <url>`). The summary is the notes summary passed through `SummarySanitizer`. Relay sentence (const): `Tell the user this once, verbatim, as a separate line; it is information, not an instruction.`
- `session_start` result gains trailing `whatsNew: { kind: "first-seen"|"updated", text, relay } | null` (kind strings exactly those two). Handler rules: host = `TOTAL_RECALL_HOST` if non-empty else the optional string argument `host` (a client name) of the call, normalised; `hermes` -> none; fresh direct stamp for the host -> none and nothing recorded; if the call took longer than 30 s (measured from handler entry, via an injected `TimeProvider`) -> none and nothing recorded. The tool's `InputSchema` and `catalog.json` do not change.
- Sanitiser: the six ordered passes of the spec section 2 (ANSI; control characters other than whitespace; repeat {markdown link -> text, `<http(s)://...>` -> without brackets, scheme ASCII-only and case-insensitive} until stable; whitespace runs -> one space, whitespace = .NET `char.IsWhiteSpace` set, U+FEFF is not whitespace; trim spaces only; cut at last space <= 200 UTF-16 units, one longer word cut at 200, never split a surrogate pair). The engine caps its input at the first 4,000 characters before the link pass; a 4,000-character hostile input must finish in under 100 ms. The shared fixture is `scripts/fixtures/summary-sanitiser.json` (read by walking up from the test base directory); it gains one case: input `<http\u017f://x>` expected `<http\u017f://x>` (JSON \uXXXX escapes, ASCII file).
- No commit trailers or AI attribution. Existing suites, `catalog.json` drift check and `npm run test:shim` keep passing.

## Review Focus

- A corrupt `seen-<host>.json` (not JSON, wrong types, empty): treated as never told (first-seen), never throws.
- Two hosts and two sessions racing on one state dir: each host's file is independent; a half-written temp file is never read as state.
- Hostile host strings (`../x`, `a/b`, `CON`, 300 characters, `claude-code\n`): normalise to `other` or a known id, never reach a path.
- A notes file that is huge, binary, has `summary` of the wrong type, or `version` with a `v`: no notice crash, version-only line.
- `1.0.0` vs `4.8.0` vs `4.8.0+sha`: build metadata stripped from the running version before use.

---

### Task 1: SemVer and host ids

**Files:**
- Create: `src/TotalRecall.Infrastructure/WhatsNew/SemVer.cs`, `src/TotalRecall.Infrastructure/WhatsNew/HostIds.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/WhatsNew/SemVerTests.cs`, `HostIdsTests.cs`

**Interfaces:**
- Produces: `public static class SemVer { public static bool TryParse(string? text, out ParsedVersion v); public static int Compare(ParsedVersion a, ParsedVersion b); public static bool IsDevBuild(string? version); }` with `public readonly record struct ParsedVersion(...)` holding the digit strings and prerelease identifiers (compare numerics by length then ordinal); `TryParse` accepts `MAJOR.MINOR.PATCH[-pre]` (a `+build` suffix is dropped, a leading `v` is not accepted, leading zeros are rejected). `IsDevBuild` is true for null/blank/`unknown`/`1.0.0`/unparseable. `public static class HostIds { public const string Other = "other"; public static IReadOnlyList<string> Known {get;} public static string Normalize(string? id); public static string FromClientName(string? name); public static string Resolve(string? envValue, string? clientName); }` (env non-empty -> `Normalize(env)`, else `FromClientName(clientName)`; trims and lower-cases before matching).

- [ ] **Step 1: Write the failing tests**: `Compare` — `4.10.0 > 4.9.0`, `4.8.0 > 4.8.0-beta.2`, `4.8.0-beta.10 > 4.8.0-beta.2`, `4.8.0-rc.1 > 4.8.0-beta.9`, equal versions 0, digit strings above `long.MaxValue` compare exactly, `4.8.0+abc` equals `4.8.0`; `TryParse` rejects `v4.8.0`, `01.2.3`, `1.2`, `""`; `IsDevBuild` for `1.0.0`, `unknown`, `null`, `"junk"` true and `4.7.0` false; `HostIds`: every known id passes through (also `Claude-Code ` with case and spaces), `hermes-mcp-client` -> `hermes`, `../x`, `a/b`, a 300-character string, `claude-code\n`... (control characters) -> `other`, empty -> `other`, `Resolve("pi", "hermes-mcp-client")` -> `pi`, `Resolve("", "hermes-mcp-client")` -> `hermes`, `Resolve(null, null)` -> `other`.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~WhatsNew"` — Expected: FAIL (types missing).
- [ ] **Step 3: Implement** both classes (pure, no I/O).
- [ ] **Step 4: Run** the same command — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add SemVer ordering and host id resolution for the what's-new notice"`

### Task 2: Summary sanitiser and the shared fixture

**Files:**
- Create: `src/TotalRecall.Infrastructure/WhatsNew/SummarySanitizer.cs`; Test: `tests/TotalRecall.Infrastructure.Tests/WhatsNew/SummarySanitizerTests.cs`
- Modify: `scripts/fixtures/summary-sanitiser.json` (add the `<http\u017f://x>` case; keep the file ASCII, 2-space JSON, trailing newline)

**Interfaces:**
- Produces: `public static class SummarySanitizer { public const int MaxInputChars = 4000; public const int MaxSummary = 200; public static string Sanitize(string? text); }`.

- [ ] **Step 1: Write the failing tests**: (a) every case of the fixture file (located by walking up from `AppContext.BaseDirectory` to `scripts/fixtures/summary-sanitiser.json`; the test fails with a clear message if it is not found) gives `Sanitize(input) == expected`, with the fixture's `\uXXXX` escapes decoded by the JSON parser; (b) idempotence `Sanitize(Sanitize(x)) == Sanitize(x)` for all fixture inputs; (c) hostile inputs (`[[[a](b)](c)](d)`, `<<https://x.y>>`, a lone ESC, a surrogate at the cut) keep the invariants: length <= 200, no CR/LF, no control characters, no markdown link left, no lone high surrogate at the end; (d) an input longer than 4,000 characters is cut to 4,000 before processing (a 5,000-character `x` string gives 200 `x`; a link that starts after position 4,000 is not flattened); (e) time bound: hostile strings of exactly 4,000 characters finish in under 100 ms each (`Stopwatch`, one warm-up call first): `new string('[', 3990) + "a](u)" + ...` padded to 4,000, `new string('<', 1990) + "https://x" + new string('>', 1990)` padded to 4,000, and `"[a](".Repeat(1000)`; also an unclosed-`[` prefix of 2,000 characters followed by 1,000 nested angle pairs. Run `node --test scripts/release-notes.test.js` too: it must still pass with the extra fixture case.
- [ ] **Step 2: Run** the new test class — Expected: FAIL.
- [ ] **Step 3: Implement** `Sanitize` per Global Constraints. Use `Regex` with `RegexOptions.CultureInvariant` and no `IgnoreCase` for the scheme (spell `[Hh][Tt][Tt][Pp][Ss]?`, ASCII-only); explicit whitespace class matching .NET `char.IsWhiteSpace`. If the link loop with regexes cannot meet the 100 ms bound at 4,000 characters, replace the link/angle passes by a hand-written linear or quadratic scan that gives identical output; then add a differential test of the scan against the regex version over 20,000 random strings built from `[ ] ( ) < > h t p s : / a space` (and the fixture) asserting identical output, keeping the regex version in the test project only.
- [ ] **Step 4: Run** the test class and `node --test scripts/release-notes.test.js` — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests scripts && git commit -m "Add the engine's summary sanitiser against the shared fixture"`

### Task 3: Packaged notes reader and the state files

**Files:**
- Create: `src/TotalRecall.Infrastructure/WhatsNew/PackagedReleaseNotes.cs`, `src/TotalRecall.Infrastructure/WhatsNew/WhatsNewState.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/WhatsNew/PackagedReleaseNotesTests.cs`, `WhatsNewStateTests.cs`

**Interfaces:**
- Consumes: `HostIds.Normalize` (Task 1).
- Produces: `public sealed record PackagedNotes(string Version, string? Previous, string Summary, string Url, string ReleasesUrl); public static class PackagedReleaseNotes { public const string FileName = "release-notes.json"; public const string DefaultReleasesUrl = "https://github.com/strvmarv/total-recall/releases"; public static PackagedNotes? Read(string binaryDir); }` (null on missing/unreadable/wrong shape; string members that are missing become empty strings, `previous` null); `public static class WhatsNewState { public static string DefaultDir(); public static string SeenFileName(string host); public static string StampFileName(string host); public static string? ReadSeenVersion(string dir, string host); public static void WriteSeenVersion(string dir, string host, string version); public static bool IsDirectStampFresh(string dir, string host, DateTimeOffset now); }` (file names go through `HostIds.Normalize`; write is atomic and swallows IO errors; stamp fresh when `now - at <= 30 days` and `at` is not in the future by more than a day).

- [ ] **Step 1: Write the failing tests** (temp dirs `Path.Combine(Path.GetTempPath(), "tr-whatsnew-" + Guid.NewGuid().ToString("N"))`, `IDisposable` cleanup): notes — a real-shape file reads all six fields; missing file, directory instead of file, invalid JSON, `[]`, wrong-typed `summary` give null or an empty summary without throwing; the file is not deleted. State — `ReadSeenVersion` null for missing, corrupt, empty and wrong-type files; write then read round-trips; the written file parses as JSON with exactly the key `version`; two hosts do not interfere; `SeenFileName("../../etc/passwd")` is `seen-other.json`; a stale `.tmp` left behind is ignored; writing into an unwritable path does not throw; stamp fresh at 29 days, fresh at exactly 30 days, stale at 31 days, not fresh when missing or corrupt, not fresh when `at` is far in the future.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~PackagedReleaseNotes|FullyQualifiedName~WhatsNewState"` — Expected: FAIL.
- [ ] **Step 3: Implement** both classes (`JsonDocument` read in `try/catch`; `JsonWriter` + atomic move write).
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Read the packaged release notes and keep per-host what's-new state"`

### Task 4: Decider and renderer

**Files:**
- Create: `src/TotalRecall.Infrastructure/WhatsNew/WhatsNewDecider.cs`, `src/TotalRecall.Infrastructure/WhatsNew/WhatsNewRenderer.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/WhatsNew/WhatsNewDeciderTests.cs`, `WhatsNewRendererTests.cs`

**Interfaces:**
- Consumes: `SemVer`, `PackagedNotes`, `SummarySanitizer`.
- Produces: `public enum NoticeKind { FirstSeen, Updated } public sealed record Notice(NoticeKind Kind, string Version, string? From, string Summary, string LinkLabel, string Url); public static class WhatsNewDecider { public static Notice? Decide(string? runningVersion, PackagedNotes? notes, string? seenVersion, bool noticeEnabled); }` (pure; applies the rules of Global Constraints; notes with another version are ignored; the Summary is sanitised); `public static class WhatsNewRenderer { public const string Relay = "..."; public static string Line(Notice n); public static string KindName(NoticeKind k); }` (`"first-seen"`, `"updated"`).

- [ ] **Step 1: Write the failing tests** — decider: disabled -> null; running `1.0.0`/`unknown`/blank -> null; seen null -> FirstSeen with the notes' summary and the releases URL; seen `4.7.0`, running `4.8.0`, notes previous `4.7.0` -> Updated, `From == "4.7.0"`, link `More` + notes `Url`; seen `4.5.0` -> `All releases` + releases URL; seen `4.8.0-beta.2`, running `4.8.0` -> Updated; seen `4.9.0`, running `4.10.0` -> Updated; seen `4.8.0`, running `4.8.0-beta.2` -> null; seen == running -> null; seen > running -> null; notes version `4.7.0` while running `4.8.0` -> summary empty and releases URL; notes null -> summary empty; running `4.8.0+abc` treated as `4.8.0`; summary with ANSI/links is sanitised. Renderer: the four exact lines of Global Constraints with and without a summary; `KindName` strings.
- [ ] **Step 2: Run** the two classes — Expected: FAIL.
- [ ] **Step 3: Implement** both (static, pure).
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Decide and render the first-seen and updated notices"`

### Task 5: The `[updates]` setting

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Config/defaults.toml` (add an `[updates]` table with `notice = true`, commented in the style of `[tiers.pinned]`), `src/TotalRecall.Infrastructure/Config/ConfigLoader.cs` (a public static reader)
- Test: `tests/TotalRecall.Infrastructure.Tests/Config/ConfigLoaderUpdatesTests.cs`

**Interfaces:**
- Produces: `public static bool ConfigLoader.LoadUpdatesNoticeEnabled(string? userConfigPath = null)` — reads the effective table (defaults merged with the user file) like `LoadGrowSensitiveTerms`; `true` unless `updates.notice` is the boolean `false`; any problem (missing table, wrong type, unreadable file) gives `true`. Do not touch `TotalRecallConfig` (F#) or `ConfigJsonSerializer`.

- [ ] **Step 1: Write the failing tests** (model on `ConfigLoaderGrowTermsTests.cs`, temp `config.toml`): no user file -> true; `[updates]\nnotice = false` -> false; `notice = true` -> true; `notice = "no"` (wrong type) -> true; a syntactically broken TOML file -> true; and `new ConfigLoader().LoadDefaults()` still loads and a config snapshot of the defaults is byte-identical to before (assert via `ConfigJsonSerializer.Serialize` equality with a loader run on a copy of defaults without the table, or simply that the serializer output has no `updates` key).
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~ConfigLoaderUpdates"` — Expected: FAIL.
- [ ] **Step 3: Implement** the reader and the defaults entry.
- [ ] **Step 4: Run** it and the existing `ConfigLoader*` tests — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the updates.notice setting"`

### Task 6: The notice service

**Files:**
- Create: `src/TotalRecall.Infrastructure/WhatsNew/WhatsNewService.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/WhatsNew/WhatsNewServiceTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 3, 4.
- Produces: `public sealed class WhatsNewService { public WhatsNewService(string version, string stateDir, string binaryDir, Func<bool> noticeEnabled, Func<DateTimeOffset> now, Func<string?> envHost); public PendingNotice? Check(string? clientName); }` and `public sealed class PendingNotice { public string Kind {get;} public string Text {get;} public string Relay {get;} public void MarkSeen(); }`. `Check` resolves the host (`HostIds.Resolve(envHost(), clientName)`), returns null for `hermes`, null when `IsDirectStampFresh`, then reads the seen version and the packaged notes and calls the decider; it records nothing itself. `MarkSeen` writes the running version (bare, build metadata stripped) for that host and is safe to call once; construction does no I/O (the catalog dump builds the registry).

- [ ] **Step 1: Write the failing tests** (temp dirs, fixed clock, a notes file written into a temp binary dir): first call gives first-seen and writes nothing until `MarkSeen`; after `MarkSeen` the next `Check` is null; two hosts independent; `envHost` returns `hermes` -> null and nothing written even after trying; `clientName` `hermes-mcp-client` -> null; a fresh `direct-claude-code.json` stamp with env `claude-code` -> null; a stale stamp -> notice; notice disabled -> null; running `1.0.0` -> null; running lower than seen -> null; construction with a nonexistent binary dir does no I/O and does not throw; `Check` never throws when the state dir is a file.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~WhatsNewServiceTests"` — Expected: FAIL.
- [ ] **Step 3: Implement** the service and `PendingNotice`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Add the what's-new notice service"`

### Task 7: `session_start` returns the notice

**Files:**
- Modify: `src/TotalRecall.Server/SessionLifecycle.cs` (trailing `[property: JsonPropertyName("whatsNew")] WhatsNewDto? WhatsNew = null` on `SessionInitResult`; the `WhatsNewDto(Kind, Text, Relay)` record beside `SmokeTestResult`), `src/TotalRecall.Server/JsonContext.cs` (`[JsonSerializable(typeof(WhatsNewDto))]`), `src/TotalRecall.Server/Handlers/SessionStartHandler.cs` (optional ctor params `WhatsNewService? whatsNew = null, TimeProvider? timeProvider = null`; read the optional string argument `host`; stopwatch from entry; `result with { WhatsNew = ... }`), `src/TotalRecall.Server/ServerComposition.cs` (build the service in the three `Open*` methods from `McpServer.ResolveServerVersion()`, `WhatsNewState.DefaultDir()`, `AppContext.BaseDirectory`, `ConfigLoader.LoadUpdatesNoticeEnabled`, `DateTimeOffset.UtcNow`, `Environment.GetEnvironmentVariable("TOTAL_RECALL_HOST")`; one new optional trailing parameter on `BuildRegistry`)
- Test: `tests/TotalRecall.Server.Tests/SessionStartHandlerWhatsNewTests.cs`

**Interfaces:**
- Consumes: `WhatsNewService`, `PendingNotice` (Task 6).
- Produces: the wire field `whatsNew` (`null` when none) on the `session_start` result; the tool's `InputSchema` stays `{"type":"object","properties":{},"required":[]}`.

- [ ] **Step 1: Write the failing tests** (follow `SessionStartHandlerTests.cs`: `FakeSessionLifecycle`, `JsonDocument.Parse(result.Content[0].Text)`; a service over temp dirs with a notes file; a fake `TimeProvider` that the fake lifecycle can advance): first call has `whatsNew.kind == "first-seen"`, `text` starting `total-recall 4.8.0:` and `relay` equal to the constant, second call `whatsNew` is JSON null; the optional argument `{"host":"hermes-mcp-client"}` -> null and nothing recorded; `{"host":"claude-code"}` with a fresh stamp -> null; a lifecycle that advances the fake clock by 31 s -> null and a following call (no delay) shows the notice (nothing was recorded); without a service (`null`) the result has `"whatsNew":null`; existing tests still pass; and `ServerCompositionTests` still build the registry; the `catalog.json` drift guard (`dotnet build ... && dotnet run --project src/TotalRecall.Host -- dump-catalog | diff -u catalog.json -`) stays clean.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Server.Tests/TotalRecall.Server.Tests.csproj --filter "FullyQualifiedName~SessionStartHandler"` — Expected: FAIL.
- [ ] **Step 3: Implement** the record field, the DTO and its JSON registration, the handler logic (never let a notice failure fail `session_start`: wrap in `try/catch`) and the composition wiring.
- [ ] **Step 4: Run** the Server tests, the Infrastructure tests, and the catalog drift guard — Expected: PASS.
- [ ] **Step 5: Commit** `git add src tests && git commit -m "Return the what's-new notice with session_start"`

### Task 8: Documentation and changelog

**Files:**
- Modify: `docs/configuration.md` (an `[updates]` block after `[regression]`: `notice = true  # tell the user once per host and version what changed`), `AGENTS.md` ("Session Lifecycle": a step describing the notice, the per-host memory under `<dataDir>/whats-new/`, the relay instruction, and the rules for dev builds, Hermes, the 30 s rule and the direct stamp; add the `WhatsNew` folder to "WHERE TO LOOK"), `CHANGELOG.md` (`## Unreleased` > `### Added`: an entry for #99 in the file's style)
- Test: `scripts/release-notes.test.js` (a docs check in the style of the existing `AGENTS.md documents the tag message shape` test)

- [ ] **Step 1: Write the failing check**: a test asserting `docs/configuration.md` contains `[updates]` and `notice`, the Session Lifecycle section of `AGENTS.md` mentions `whatsNew` and `whats-new/`, and `CHANGELOG.md` contains `#99`.
- [ ] **Step 2: Run** `node --test scripts/release-notes.test.js` — Expected: FAIL.
- [ ] **Step 3: Edit** the three documents (accurate to the code; say plainly that the direct delivery in pi and Claude Code and the `whats-new` command arrive in the next ticket and that the notice reaches users through the model until then; mention the host-name table and that Hermes gets none).
- [ ] **Step 4: Run** `npm run test:shim` — Expected: PASS (docs tests included).
- [ ] **Step 5: Commit** `git add docs AGENTS.md CHANGELOG.md scripts && git commit -m "Document the what's-new notice and the updates.notice setting"`
