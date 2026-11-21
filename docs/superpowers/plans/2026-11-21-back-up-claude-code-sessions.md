# Back up Claude Code sessions (Part 2 of the transcript archive) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** With the archive enabled, `total-recall transcripts backfill` reads Claude Code's session files and stores their dialogue in the archive, redacted, as text chunks (`embedded = 0`), resumable and idempotent, with per-project and per-kind counts in `transcripts status`.

**Architecture:** A pure `ClaudeCodeAdapter` streams host-neutral `TranscriptRecord`s from byte offsets; a pure `ExchangeBuilder` drops, redacts (`SensitiveContentScanner.Redact`), caps and folds them into numbered units; a pure `TranscriptChunker` turns units into chunk rows; `TranscriptIngester` decides the mode (fresh, resume, rewritten, changed) per session and is the only code that calls the store, whose `ApplyIngest` re-checks exclusion, cutoff and prior state in one `BEGIN IMMEDIATE` transaction. `TranscriptBackfill` loops the ingester over one adapter; the CLI command calls it.

**Tech Stack:** C# 14 / .NET 10 NativeAOT (Infrastructure, Cli), Microsoft.Data.Sqlite + FTS5, `System.Text.Json` (`JsonDocument`, `Utf8JsonWriter`), xunit, Node tests for docs.

**Spec:** `docs/superpowers/specs/2026-11-19-back-up-claude-code-sessions-design.md` (parent: `docs/superpowers/specs/2026-11-18-transcript-archive-design.md`, "Shared decisions"). Read the spec sections named in each task before starting it; the spec is the source of truth for every rule this plan names but does not repeat.

## Global Constraints

- Work only inside this worktree. One-line commit messages, no trailers of any kind (no `Co-Authored-By`, no AI attribution).
- NativeAOT: interpreted `Regex` only (no `RegexOptions.Compiled`, no `RegexOptions.NonBacktracking`: lookbehind is needed); no reflection-based JSON (read with `JsonDocument`, write with `Utf8JsonWriter`).
- Every redaction regex is constructed with a 5-second match timeout; a timeout fails closed (`[redacted:unprocessable]` for the whole text).
- Never edit `MigrationRunner`, the main migration array (stays at 18) or archive migration 1. Add archive migration 2 only. Nothing changes in the memory or KB tables, `SessionLifecycle`, the MCP tool list or `catalog.json`.
- Visibility (spec "What exists today"): `ITranscriptAdapter`, `SessionSource`, `SessionHead`, `TranscriptRecord`, `RecordKind`, `SessionEnumeration`, `ReadStats`, `ClaudeCodeAdapter`, `SessionIngestState`, `IngestCutoff`, `SessionIngest`, `IngestChunk`, `IngestOutcome`, `IngestOutcomeKind`, `ProjectKindCount`, `TranscriptBackfill`, `BackfillSummary`, `BackfillProgress` and `RedactionResult` are `public`; everything else this plan adds is `internal` (Infrastructure.Tests sees it through `InternalsVisibleTo`).
- Fixed values (spec): line limit 16 MiB (16,777,216 bytes); head reads at most 200 complete lines; `host_session_id` matches `^[A-Za-z0-9._-]{1,128}$`; stub detail 160, error 500, title 120, repeated question 300, chunk body 1,800, text cap 100,000 characters; automation length rule `> 1,500`; `RedactionVersion = 1`; leak window 8 characters; progress every 50 sessions; status shows at most 30 project/kind lines.
- "Characters" are UTF-16 code units; every cut goes through the one `TextCut` helper (Task 2) and never splits a surrogate pair or a `[redacted:<category>]` marker.
- Timestamps written to the archive (`ts`, `started_at`, `ended_at`) use `TranscriptTime.Iso` (Task 1): `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`, UTC, the format Part 1's store already writes for the cutoff.
- All SQL is parameterised; every write goes through `ITranscriptStore`; storage and parser boundary catches log through `ExceptionLogger.LogChain`.
- Committed fixtures never contain a secret literal (spec call 7): placeholders are filled at test time by `FakeSecrets`.
- Exit codes: 0 ok, 1 failure (any file failed, archive off, tables absent, malformed config, postgres), 2 usage error.
- Tests carry no `Integration` trait. Prefer pure seams (`ClaudeCodeAdapter.ResolveRoot(dir, profile)`) over mutating environment variables; a test that must mutate one uses `[Collection("process-environment")]` (Infrastructure.Tests) or `[Collection("ConsoleCapture")]` (Cli.Tests).
- Verify commands (run at Finish): the `sh verify` blocks of `.github/develop.md` (`npm ci --ignore-scripts`, the model fetch guard, `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`, `npm run test:shim`, Release build + `dump-catalog | diff -u catalog.json -`).

## Review Focus

Inputs the spec implies that a person meets first; each has a test in the task that owns the code.

1. A redaction marker or a surrogate pair exactly at a cut the spec does not single out (the 100,000 cap, the 300-character repeated question, the 120-character title, a last-resort split at 1,800): no stored text ends inside a marker, holds a secret prefix, or holds half a pair (Task 2 `Cut_AtEveryLimit_NeverEndsInsideMarkerOrPair`, Task 6 `Chunk_HardSplit_NeverInsideMarkerOrSurrogate`).
2. A session file that grows while it is read (a line half-written at the read, lines appended between the stat and the read): no partial record is parsed, the file is read again next pass, and the result equals the file ingested once (Task 3 `Reader_PartialLineCompletedLater_IsReadOnce`, Task 8 `GrownBetweenStatAndRead_IsReadAgainNextPass`).
3. A forget (of the session, of its project by key or by root, of everything) committed while a pass runs, and two backfills of one session at once: nothing of a forgotten session is written, nothing before the cutoff is written, and the end state equals one pass (Task 1 `ApplyIngest_*` outcome tests, Task 8 race tests).
4. Odd lines: CRLF endings, a UTF-8 BOM on the first line, blank or whitespace-only lines, invalid UTF-8, a lone surrogate escape, JSON nested deeper than 64, a `type` that is not a string: the line is parsed or skipped and counted, never fails the file, and offsets stay byte-exact (Task 3 `Reader_OddLines_CrlfBomBlank`, Task 4 `Reader_OddLines_SkippedAndCounted` and `Reader_OddLines_CrlfAndBom_Parse`).
5. Odd paths: a Windows `cwd` (`C:\Users\me\src\Repo\.claude\worktrees\x`), mixed separators, a trailing separator, a `cwd` that is the home directory, a filesystem root, or a home directory that is itself a git repository (dotfiles): keys and roots follow section 6 and a root is never `/`, `C:\`, home or an ancestor of home (Task 7 `Identity_OddPaths_*`).

---

### Task 1: Archive migration 2 and the ingest store methods

Spec: "What exists today", section 6 item 6 (the `ForgetProject` change), section 7 step 6, section 8.

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Storage/TranscriptSchema.cs` (add `Migration2_IngestState`; an internal `Run(conn, int targetVersion)` overload for tests)
- Modify: `src/TotalRecall.Infrastructure/Transcripts/ITranscriptStore.cs`, `TranscriptStore.cs`
- Create: `src/TotalRecall.Infrastructure/Transcripts/TranscriptIngestTypes.cs`, `TranscriptTime.cs`
- Modify tests: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptSchemaTests.cs`, `TranscriptStoreTests.cs`, `TranscriptOpenTests.cs`, `TranscriptForgetTests.cs`, `TranscriptTestDb.cs` (`AddSession` gains `string? projectRoot = null`)
- Create test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptApplyIngestTests.cs`

**Interfaces:**
- Produces (namespace `TotalRecall.Infrastructure.Transcripts`):
  - `internal static class TranscriptTime { public static string Iso(DateTimeOffset t); public static bool TryParse(string? text, out DateTimeOffset value); }` (the store's private `Iso`/`TryParseIso` move here).
  - `public sealed record SessionIngestState(long? SourceSize, long? SourceMtime, string? SourceFingerprint, long IngestedOffset, int ExchangeCount, int SettledUserTurns, string Kind, string ProjectKey, string ProjectKeySource, string? ProjectRoot, string? Cwd, string? Title, string? StartedAt, string? EndedAt, string? BuiltUnderCutoff, string? SourcePath)`. Value equality of the whole record is the "prior state" check of section 7 step 6(c).
  - `public sealed record IngestCutoff(DateTimeOffset At, string Raw)` (`Raw` is the `tx_state` text, compared verbatim).
  - `public sealed record IngestChunk(int ExchangeStart, int ExchangeEnd, int Part, string ChunkType, bool HasError, string? Ts, int RedactionVersion, string Content)`.
  - `public sealed record SessionIngest(string Host, string HostSessionId, string ProjectKey, string ProjectKeySource, string? ProjectRoot, string? Cwd, string? StartedAt, string? EndedAt, string Kind, string? Title, string SourcePath, long SourceSize, long SourceMtime, string? SourceFingerprint, long IngestedOffset, int ExchangeCount, int SettledUserTurns, string? BuiltUnderCutoff, SessionIngestState? ExpectedPrior, int FirstRebuiltUnit, IReadOnlyList<IngestChunk> Chunks)` (`ExpectedPrior` null means "no row expected").
  - `public enum IngestOutcomeKind { Applied, Excluded, Stale, Conflict }` and `public sealed record IngestOutcome(IngestOutcomeKind Kind, int ChunksNew = 0, int ChunksChanged = 0, int ChunksUnchanged = 0, int ChunksRemoved = 0, bool Created = false)`.
  - `public sealed record ProjectKindCount(string ProjectKey, string Kind, long Sessions, long Chunks)`; `TranscriptStatus` gains `public IReadOnlyList<ProjectKindCount> ByProjectAndKind { get; init; } = Array.Empty<ProjectKindCount>();` (an init property, so existing constructor calls compile).
  - `ITranscriptStore` gains `SessionIngestState? GetIngestState(string host, string hostSessionId)`, `IngestCutoff? IngestNotBefore()`, `IngestOutcome ApplyIngest(SessionIngest ingest)`.
- Migration 2 (exact): `ALTER TABLE tx_session ADD COLUMN settled_user_turns INTEGER NOT NULL DEFAULT 0`, `ALTER TABLE tx_session ADD COLUMN built_under_cutoff TEXT`, `ALTER TABLE tx_session ADD COLUMN project_root TEXT`, `CREATE INDEX IF NOT EXISTS idx_tx_session_project_kind ON tx_session(project_key, kind)`. `IsMigrated` stays `CurrentVersion >= 1`; `OpenStore` already runs the runner on every open, so a version-1 archive is migrated when opened, also with `create: false`.

- [ ] **Step 1: Update Part 1's schema and version tests (failing).** `FreshDatabase_HasExactlyTheSpecObjects` adds `"index:idx_tx_session_project_kind"`; `TxSession_HasTheSpecColumns` appends `Column("settled_user_turns","INTEGER",true,0,"0")`, `Column("built_under_cutoff","TEXT",false,0)`, `Column("project_root","TEXT",false,0)`; rename `Run_RecordsVersion1_AndIsIdempotent` to `Run_RecordsVersion2_AndIsIdempotent` (`CurrentVersion == 2`, twice); `Run_OnAMainSchemaDatabase_LeavesMainVersionAt18_AndMetaUntouched` expects archive version 2; `TranscriptStoreTests` and `TranscriptOpenTests` expect `SchemaVersion == 2`. New: `Run_UpgradesAVersion1Archive_KeepsRows` (run to version 1, insert a session and a chunk, `Run`: version 2, the rows are intact with `settled_user_turns = 0` and nulls); `Open_Version1Archive_WithoutCreate_IsMigratedTo2`.
- [ ] **Step 2: Failing tests for the reads and `ForgetProject`.** `GetIngestState_NoRow_IsNull`; `GetIngestState_ReadsEveryField` (a row written by raw SQL with every column set compares equal to the expected record); `IngestNotBefore_NoCutoff_IsNull`; `IngestNotBefore_AfterForgetAll_RawEqualsStatusText_AndAtParses`; `Status_ByProjectAndKind_OrderedByChunksDescThenKeyThenKind` (three keys, both kinds, ties broken by key then kind; sessions with no chunks listed with `Chunks = 0`). Update `ForgetProject_RemovesSessions_RecordsDistinctCwdRoots` to `ForgetProject_RemovesSessions_RecordsDistinctProjectRoots` (sessions with roots `/w/a`, `/w/a`, `/w/b` and one with a `cwd` but a null root give `Roots = ["/w/a","/w/b"]`); `ForgetProject_Repeat_MergesRoots` and `Status_ListsForgottenRecordsAndCutoff` set `projectRoot`; in `TranscriptForgetTests.ForgetProject_LeavesOnlyTheRootInTheExclusionRecord` the marked session gets a distinct root marker, which appears exactly once in database + WAL, and the `cwd` marker zero times.
- [ ] **Step 3: Failing tests for `ApplyIngest`** (`TranscriptApplyIngestTests`, real file-backed store via `TranscriptTestDb`, a helper `Ingest(...)` that builds a `SessionIngest` with chunks `IngestChunk(u, u, p, "dialogue", false, ts, 1, content)`):
  - `Fresh_WritesSessionThenChunks_CreatedTrue` (every `tx_session` column equals the ingest; `Applied`, `ChunksNew = n`, `Created = true`; chunks findable through `tx_chunk_fts MATCH`).
  - `SameIngestAgain_LeavesIdsAndEmbeddedUntouched` (set `embedded = 1` by raw SQL first; re-apply with `ExpectedPrior = GetIngestState(...)`: `ChunksUnchanged = n`, ids and `embedded` unchanged).
  - `ChangedChunk_UpdatedInPlace_EmbeddedResetToZero_FtsReindexed` (same id, new content found by `MATCH`, old word not).
  - `RebuiltRange_RemovesVanishedParts_AndKeepsEarlierUnits` (units 0..3 stored, unit 3 with parts 0..2; re-apply with `FirstRebuiltUnit = 3` and unit 3 parts 0..1: `ChunksRemoved = 1`, units 0..2 untouched).
  - `FirstRebuiltUnitZero_ReplacesEveryChunk`.
  - `EmptyIngest_DeletesChunks_AndResetsRow` (zero chunks, `ExchangeCount = 0`: no chunk left, row kept with offset 0, nulls for title and times, kind kept).
  - `ForgottenSessionSinceRead_IsExcluded_WritesNothing`; `ForgottenProjectByKey_IsExcluded`; `CwdUnderForgottenRoot_IsExcluded`.
  - `CutoffChangedSinceBuild_IsStale_WritesNothing` (`ForgetAll` after the state read; ingest built with `BuiltUnderCutoff = null`); `CutoffEqualRaw_IsApplied`.
  - `PriorStateChanged_IsConflict` (another `ApplyIngest` committed between read and apply); `FreshButRowAppeared_IsConflict`.
  - `QuotesAndPercentInData_AreStoredVerbatim`.
- [ ] **Step 4: Run them; expected: compile failures and failures.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~Transcripts"`.
- [ ] **Step 5: Implement.** Migration 2 as above. `ForgetProject` selects `DISTINCT project_root ... WHERE project_root IS NOT NULL AND project_root <> ''` instead of `cwd`. `ApplyIngest` opens `_conn.BeginTransaction()` (immediate), runs private transaction-aware variants `IsExcludedCore(tx, host, id, key, cwd)` (with the ingest's key and `cwd`), `ReadCutoffText(tx)` and `GetIngestStateCore(tx, ...)` (refactor the public methods onto them), returns `Excluded`/`Stale`/`Conflict` with nothing written, else upserts the session row (`INSERT ... ON CONFLICT (host, host_session_id) DO UPDATE`), then for each chunk compares the stored row by `(session_id, exchange_start, part)` on every column but `id` and `embedded` (unchanged: untouched; different: `UPDATE ... embedded = 0`; absent: `INSERT`), then deletes rows with `exchange_start >= FirstRebuiltUnit` that are not in the new set, then commits.
- [ ] **Step 6: Run the Transcripts tests and `dotnet test tests/TotalRecall.Cli.Tests --filter "FullyQualifiedName~Transcripts"`; expected: pass** (the CLI tests pass unchanged).
- [ ] **Step 7: Commit** `Add archive migration 2 and the ingest methods of the transcript store`.

### Task 2: Redaction with spans and the marker-safe cut

Spec: section 4 (all of it), section 3 "Characters".

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Eval/SensitiveContentScanner.cs`
- Create: `src/TotalRecall.Infrastructure/Transcripts/TextCut.cs`
- Create: `tests/TotalRecall.Infrastructure.Tests/Transcripts/Support/FakeSecrets.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Eval/SensitiveContentScannerRedactTests.cs`, `tests/TotalRecall.Infrastructure.Tests/Transcripts/TextCutTests.cs`

**Interfaces:**
- Produces (namespace `TotalRecall.Infrastructure.Eval`): `public sealed record RedactionResult(string Text, int Count, IReadOnlyList<string> Categories)`; `public const int RedactionVersion = 1;` `public static RedactionResult Redact(string? text)`; `internal static RedactionResult Redact(string? text, TimeSpan matchTimeout)` (test seam; the public overload uses 5 s and cached patterns). `Count` is the number of markers written; `Categories` are distinct, in the order of their first marker. Markers are exactly `[redacted:token]`, `[redacted:private-key]`, `[redacted:bearer]`, `[redacted:url-credentials]`, `[redacted:secret-assignment]`, `[redacted:unprocessable]`.
- Overlap rule (spec "Overlaps merge"): the union of overlapping spans becomes one marker with the category of the span that starts first; on an equal start, the table order token, private-key, bearer, url-credentials, secret-assignment decides.
- Produces (namespace `TotalRecall.Infrastructure.Transcripts`): `internal static class TextCut { public const string Ellipsis = "..."; public static int SafeLength(string text, int limit); public static string Cut(string text, int limit); public static string Cap(string text, int limit); public static string Fold(string text); }`. `SafeLength`: the largest `n <= limit` such that `text[..n]` does not end inside an exact marker or between a high and a low surrogate. `Cut`: `text` when `Length <= limit`, else `text[..SafeLength] + "..."`. `Cap`: `text` when `Length <= limit`, else `text[..n] + " [... " + (Length - n) + " more characters not stored]"` (invariant digits). `Fold`: newlines (`\r\n`, `\n`, `\r`) to spaces, runs of whitespace to one space, trimmed.
- Produces (test support, namespace `TotalRecall.TestSupport.Transcripts`, `internal`): `sealed class FakeSecrets(int seed)` with `string Get(string name)` for `github-token` (`ghp_` + 36), `github-pat` (`github_pat_` + 59 of `[A-Za-z0-9_]`), `gitlab-token` (`glpat-` + 20), `slack-token` (`xoxb-` + digits and letters, 40), `openai-key` (`sk-` + 48 alphanumerics), `anthropic-key` (`sk-ant-api03-` + 80 of `[A-Za-z0-9_-]`), `openai-project-key` (`sk-proj-` + 48 of `[A-Za-z0-9_-]`), `aws-key` (`AKIA` + 16 of `[0-9A-Z]`), `private-key` (a multi-line `-----BEGIN RSA PRIVATE KEY-----` block with base64-looking lines and its END line), `private-key-unterminated` (the same without END), `bearer-token` (32 alphanumerics), `url-password` (16 characters with `@`, `/`, `+`), `api-key-value` (24 alphanumerics with `,;&`). Values are assembled from parts at run time and are at least 12 characters.

- [ ] **Step 1: Write the failing table tests** (`SensitiveContentScannerRedactTests`; every secret comes from `FakeSecrets`, never a literal):
  - `Redact_TokenShapes_BecomeTokenMarker` (Theory over the eight token names; the output holds `[redacted:token]` and no 8-character window of the fake).
  - `Redact_SkWithoutLeftBoundary_IsKept` (`task-list-management-and-more`, `mask-` + 20 letters) and `Redact_ExtendedSkShortSuffix_IsKept` (`sk-proj-` + 19 characters).
  - `Redact_PrivateKey_BeginToEnd_RestKept` and `Redact_PrivateKeyWithoutEnd_ToEndOfText`.
  - `Redact_Bearer` (Theory: `Authorization: Bearer <v>`, `Authorization=Basic <v>`, `"Authorization": "Token <v>"`, `Authorization: <v>`, `x-api-key: <v>`, bare `Bearer <16+>`; the value only is replaced, the header name stays); `Redact_BearerTooShort_IsKept` (7 characters after `Authorization:`, 15 after a bare `Bearer`).
  - `Redact_UrlCredentials_PasswordOnly` (`mysql://root:P@ssw0rd@localhost` gives `mysql://root:[redacted:url-credentials]@localhost`; a password with `/` and `+` is one span; the user and host stay searchable).
  - `Redact_SecretAssignment` (Theory: `API_KEY=a,b;c&d)` whole run, `DB_PASSWORD="x\"y"` escaped quote kept inside, `\"api_key\": \"v\"` escaped JSON, `"client_secret": "v"`, `export GH_TOKEN=v`, `PASSWORD='$ecret!9'`, `PASSWORD=ab`, `TOKEN=$(echo hunter2)` and a backtick substitution, `--password hunter2`, `--api-key=v`, `TOKEN=${X:-literal}`, `PASSWORD='$Tr0ub4dor9'`); `Redact_ReferencesAndPlaceholders_AreKept` (Theory: `GH_TOKEN="$t" gh api`, `TOKEN=${GH_TOKEN}`, `PASSWORD=%PASS%`, `API_KEY=<your-key>`, `TOKEN={{ secrets.X }}`, `SECRET=true`, `TOKEN=null`, `KEY=` followed by a newline); each Theory row asserts the exact output.
  - `Redact_Emails_AreKept`; `Redact_Overlap_IsOneMarker_FirstStartWins` (`Authorization: Bearer <github-token>` gives one `[redacted:bearer]`, `Count = 1`); `Redact_NullOrEmpty_ReturnsSameWithCountZero`; `Redact_CountAndCategories_InOrderOfFirstMarker`.
  - `Redact_AdversarialInputs_FinishWellUnderASecond` (Theory over 1 MiB of: `a`, `=`, `:`, `"`, `\"`, `A_KEY=` repeated, `-----BEGIN ` repeated, `Authorization: ` repeated, `x://a:` repeated, `sk-` repeated; `Stopwatch` < 1,000 ms and no `unprocessable` marker).
  - `Redact_OnTimeout_FailsClosed` (internal overload with `TimeSpan.FromTicks(1)` on 1 MiB of mixed text: `Text == "[redacted:unprocessable]"`, `Count == 1`, `Categories == ["unprocessable"]`).
  - The existing `SensitiveContentScanner` tests are not edited.
- [ ] **Step 2: Write the failing `TextCutTests`.** `Cut_ShortText_Unchanged`; `Cut_AppendsEllipsis_PrefixAtMostLimit`; `Cut_LimitInsideMarker_CutsBeforeMarker` (Theory: the limit at every offset from the `[` to the `]` of a marker gives the text before the `[` plus `...`); `Cut_AtEveryLimit_NeverEndsInsideMarkerOrPair` (a text mixing markers, emoji surrogate pairs and plain words; for every limit 0..Length: `SafeLength <= limit`, the prefix is not inside a marker and does not end on a high surrogate); `Cap_Appends_ExactRemainderCount` (`Cap(new string('a', 100_005), 100_000)` ends with `" [... 5 more characters not stored]"`); `Cap_InsideMarker_CountsTheWholeMarkerAsNotStored`; `Fold_CollapsesNewlinesAndWhitespace`; `SafeLength_TextThatOnlyLooksLikeAMarker_IsPlainText` (`[redacted:other]` can be cut).
- [ ] **Step 3: Run them; expected: compile failures.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~Redact|FullyQualifiedName~TextCut"`.
- [ ] **Step 4: Implement** `Redact` with its own pattern set (do not change `_detectors`, `Scan`, `ScanSecrets`): collect `(start, end, category)` spans from every pattern, apply the whole-value exemption of section 4 to `secret-assignment` and `bearer` values, merge overlaps, rebuild the text with markers; catch `RegexMatchTimeoutException` and return the unprocessable result. Write patterns with bounded or unambiguous repetitions only (spec "Every repeated element is bounded or unambiguous"). Implement `TextCut` and `FakeSecrets`.
- [ ] **Step 5: Run them and `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~SensitiveContent"`; expected: pass.**
- [ ] **Step 6: Commit** `Add span-based secret redaction and the marker-safe text cut`.

### Task 3: The fixture corpus, the fixture home, and the Claude Code reader

Spec: section 10 (corpus and harness), section 2 "Enumeration", "Reading" and `ReadHead`, call 1. The harness lands here so that every later task's tests can place real session files.

**Files:**
- Create: `tests/fixtures/transcripts/claude-code/decision.jsonl`, `compaction.jsonl`, `error.jsonl`, `automated-sdk.jsonl`, `automated-opening.jsonl`, `secrets.jsonl`, `worktree-missing.jsonl`, `worktree-repo.jsonl`, `no-kept.jsonl`, `straddle.jsonl`
- Create: `tests/TotalRecall.Infrastructure.Tests/Transcripts/Support/ClaudeFixtureHome.cs`, `LeakScan.cs`
- Modify: `tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj` (`<Compile Include="..\TotalRecall.Infrastructure.Tests\Transcripts\Support\*.cs" LinkBase="TranscriptSupport" />`)
- Create: `src/TotalRecall.Infrastructure/Transcripts/ITranscriptAdapter.cs`, `ClaudeCodeAdapter.cs`, `JsonlLineReader.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/ClaudeFixtureHomeTests.cs`, `JsonlLineReaderTests.cs`, `ClaudeCodeAdapterTests.cs`

**Interfaces:**
- Produces (namespace `TotalRecall.Infrastructure.Transcripts`, public):
  ```csharp
  public interface ITranscriptAdapter
  {
      string Host { get; }
      string Root { get; }
      bool Detect();
      SessionEnumeration EnumerateSessions();
      SessionHead ReadHead(SessionSource source);
      bool IsLineStart(SessionSource source, long offset);   // offset 0, or the byte before it is 0x0A
      IEnumerable<TranscriptRecord> ReadRecords(SessionSource source, long startOffset, ReadStats stats, CancellationToken ct);
  }
  public sealed record SessionSource(string HostSessionId, string Path, long Size, long MtimeMs);
  public sealed record SessionEnumeration(IReadOnlyList<SessionSource> Sources, int Skipped);
  public sealed record SessionHead(string? Cwd, string? Entrypoint, DateTimeOffset? FirstTimestamp, string? Fingerprint);
  public sealed record TranscriptRecord(RecordKind Kind, long StartOffset, long EndOffset, DateTimeOffset? Ts, string? Text, string? ToolName);
  public enum RecordKind { UserText, AssistantText, ToolStub, ToolError, CompactionBoundary, CompactionSummary }
  public sealed class ReadStats { public int SkippedLines { get; set; } }
  ```
  `SessionEnumeration.Sources` holds every valid file, duplicates of an id included (choosing among them needs the store: Task 9); `Skipped` counts names that fail the id pattern.
- `public sealed class ClaudeCodeAdapter : ITranscriptAdapter { public const string HostName = "claude-code"; public const int MaxLineBytes = 16 * 1024 * 1024; public const int HeadLines = 200; public ClaudeCodeAdapter(string? projectsRoot = null); internal ClaudeCodeAdapter(string projectsRoot, int maxLineBytes); public static string ResolveRoot(string? claudeConfigDir, string userProfile); }` (null root: `ResolveRoot(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), UserProfile)`; `ResolveRoot` gives `<dir>/projects` when `dir` is non-empty after trimming, else `<userProfile>/.claude/projects`). In this task `ReadRecords` reads lines and yields no record (Task 4 adds the parsing and mapping).
- `internal static class JsonlLineReader { public static IEnumerable<JsonlLine> Read(Stream stream, long startOffset, int maxLineBytes, CancellationToken ct); }`, `internal readonly record struct JsonlLine(long Start, long End, ReadOnlyMemory<byte> Bytes, bool TooLong)` (`End` is the offset after the `0x0A`; `Bytes` exclude it; a trailing partial line is never yielded; `Bytes` is empty when `TooLong`).
- Produces (test support, namespace `TotalRecall.TestSupport.Transcripts`, `internal`):
  - `sealed class ClaudeFixtureHome : IDisposable` with `string Root`, `string ProjectsDir`, `FakeSecrets Secrets`, `DateTimeOffset BaseTime`, `PlacedSession Place(string fixture, string cwd, string? id = null)`, `byte[] Render(string fixture, string cwd, string id)`, `void Write(PlacedSession s, byte[] content)`, `void AppendLine(PlacedSession s, string jsonLine)`, `void AppendPartial(PlacedSession s, string text)`, `void AppendExchange(PlacedSession s, string userText, string assistantText, int secondsAfterBase)`, `void Replace(PlacedSession s, string fixture)`, `void Delete(PlacedSession s)`, `void SetMtime(PlacedSession s, DateTimeOffset t)`, `static string EncodeDir(string cwd)` (every character that is not an ASCII letter or digit becomes `-`), `static string FixturesDir` (found by walking up from `AppContext.BaseDirectory` to `tests/fixtures/transcripts/claude-code`); `sealed record PlacedSession(string Id, string Path, string Cwd)`.
  - Placeholders: `{{SECRET:<name>}}` (a `FakeSecrets` name), `{{CWD}}`, `{{ID}}`, `{{T+n}}` (ISO 8601 UTC, `n` seconds after `BaseTime`), `{{PADTO:n}}` (filler of `x` ending in one space so that the next character sits at decoded offset `n` of the JSON string that holds it). Every value is substituted after JSON-escaping.
  - `static class LeakScan { public static IReadOnlyList<string> Windows(string planted, int width = 8); public static void AssertNotStored(string dbPath, string planted); public static void AssertStored(string dbPath, string text); }` (`AssertNotStored`: no window, case-insensitive, in any text column of `tx_session` and `tx_chunk`, in `tx_chunk_fts` content, or in the bytes of the database, `-wal` and `-shm` files read shared).
- Fixture content (spec section 10 table), with unique lowercase markers the later tests assert on: `decision` (`entrypoint: cli`, three turns; stubs for `Bash`, `Read`, `Edit` whose `old_string` holds `zqxeditinput`, `AskUserQuestion` whose question holds `zqxquestion`, an unknown tool; `ExitPlanMode` with plan `zqxplan`; a successful `tool_result` holding `zqxtoolresult`; a `system-reminder` block holding `zqxreminder` inside a user prompt; a `thinking` block `zqxthinking`; an `isMeta` record `zqxmeta`; an `isSidechain` record `zqxsidechain`; `<command-name>/develop</command-name><command-args>148</command-args>`; a `task-notification` user record with `origin.kind` `task-notification` holding `zqxnotification`; a queued `attachment` prompt `zqxqueued`; a `<bash-input>ls -l</bash-input>` record; an interruption notice), `compaction` (two turns, `compact_boundary`, an `isCompactSummary` record of four paragraphs holding `zqxsummary`, two more turns), `error` (a failed `Bash` call whose error text is `Parse error: no such table: zqx_schema_version` followed by 700 more characters), `automated-sdk` (`entrypoint: sdk-py`, a 2,000-character prompt, a `StructuredOutput` answer `{"verdict":"zqxverdict","findings":2}`), `automated-opening` (`entrypoint: cli`, one prompt starting `Review this change for security`), `secrets` (one `{{SECRET:...}}` per name, in prompts, assistant text, a `Bash` stub, an error, the first prompt; `{{PADTO:150}}{{SECRET:github-token}}` in a `Bash` command and `{{PADTO:490}}{{SECRET:openai-key}}` in an error; `private-key-unterminated` at the end of a message; the literal kept forms `GH_TOKEN="$t" gh api` and `API_KEY=<your-key>`), `worktree-missing` (`cwd` `{{CWD}}` that the test points at a missing `.../.claude/worktrees/<name>`), `worktree-repo` (`cwd` `{{CWD}}` in a repository the test creates), `no-kept` (attachments, a sidechain, a meta record, a `system` hook record: nothing to keep), `straddle` (turns at `T+10`, `T+50`, `T+90` and an answer at `T+60` to the `T+50` question). Every user and assistant record carries `timestamp`, `cwd`, `sessionId` `{{ID}}`, `uuid` and `entrypoint`.

- [ ] **Step 1: Write the failing harness tests.** `EveryFixture_RendersToValidJsonl` (each line of each rendered fixture parses as a JSON object); `CommittedFixtures_HoldNoSecret` (`SensitiveContentScanner.Redact(File.ReadAllText(f)).Count == 0` for every committed file); `FakeSecrets_EachMatchesItsCategory` (Theory: `Redact(fake)` holds the category's marker and no 8-character window); `PrivateKey_SurvivesJsonEscaping` (the decoded string equals `Secrets.Get("private-key")`); `PadTo_PlacesTheSecretAtTheOffset` (decoded command string: the github token starts at index 150; the error's token at 490); `EncodeDir_ReplacesNonAlphanumerics` (`/home/u/my.repo` gives `-home-u-my-repo`); `AppendPartial_LeavesNoTrailingNewline`; `LeakScan_FindsAPlantedWindow` (a database with the secret in a chunk fails `AssertNotStored`: proves the scan can fail).
- [ ] **Step 2: Write the failing reader and adapter tests.**
  - `JsonlLineReaderTests`: `Reader_YieldsCompleteLinesWithByteOffsets`; `Reader_StartOffset_YieldsLinesFromThere`; `Reader_TrailingPartialLine_IsNotYielded`; `Reader_PartialLineCompletedLater_IsReadOnce` (read, append the rest plus `\n`, read from the last `End`: one line, the whole one); `Reader_LineOverLimit_IsTooLong_AndReadingContinues` (limit 1 KiB via the parameter); `Reader_OddLines_CrlfBomBlank` (a CRLF line's `End` counts the `\r` and `\n`; a BOM on the first line is in `Start..End` but not in `Bytes`; blank and whitespace-only lines are yielded empty and later ignored); `Reader_BufferBoundaries_DoNotSplitLines` (lines longer than the internal buffer).
  - `ClaudeCodeAdapterTests`: `ResolveRoot_UsesClaudeConfigDir_WhenNonEmpty` (Theory: `"/c"` gives `/c/projects`; `""`, `"  "` and null give `<profile>/.claude/projects`); `Detect_FalseWithoutTheDirectory`; `Enumerate_OneLevelOnly_SkipsBadNames` (`a b.jsonl` and a 129-character id counted in `Skipped`; `<id>/subagents/x.jsonl` and a `.txt` file not returned and not counted); `Enumerate_SymlinksBeneathRoot_AreNotRead` and `Enumerate_RootItselfASymlink_IsFollowed` (both skipped on Windows); `Enumerate_SameIdInTwoDirs_ReturnsBoth`; `Enumerate_SourceCarriesSizeAndMtime`; `ReadHead_FirstCwdEntrypointTimestamp_AndFingerprintOfFirstLine` (fingerprint = lowercase hex SHA-256 of the first line's bytes without `0x0A`); `ReadHead_NoCompleteLine_NullFingerprint`; `ReadHead_FirstLineOverLimit_NullFingerprint`; `ReadHead_StopsAfter200Lines` (a `cwd` first appearing on line 201 is not found); `ReadHead_GrowingFile_SameFingerprint`; `IsLineStart_TrueAtZeroAndAfterNewline_FalseMidLine`.
- [ ] **Step 3: Run them; expected: compile failures.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~ClaudeFixtureHome|FullyQualifiedName~JsonlLineReader|FullyQualifiedName~ClaudeCodeAdapter"`.
- [ ] **Step 4: Implement** the fixtures, the harness and `LeakScan`, the reader (one `FileStream` with `FileShare.ReadWrite | FileShare.Delete`, seeking to `startOffset`, scanning for `0x0A` in a pooled buffer, discarding an over-limit line up to its `0x0A`), the enumeration (`DirectoryInfo` with `LinkTarget` checks: no symlinked file or directory beneath the root, regular files only) and `ReadHead`.
- [ ] **Step 5: Run them, and `dotnet build tests/TotalRecall.Cli.Tests` (the linked files compile); expected: pass.**
- [ ] **Step 6: Commit** `Add the Claude Code fixture corpus, its harness and the session file reader`.

### Task 4: Claude Code record mapping and user-text cleaning

Spec: section 2 "Reading" (skip rules) and the record mapping table, section 3 dropped blocks, slash commands, `<bash-input>`, interruption notices, the stub detail field per tool, call 8, call 9.

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/ClaudeRecordMapper.cs`, `UserTextCleaner.cs`
- Modify: `src/TotalRecall.Infrastructure/Transcripts/ClaudeCodeAdapter.cs` (`ReadRecords` parses each line and maps it)
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/ClaudeRecordMapperTests.cs`, `UserTextCleanerTests.cs`; extend `ClaudeCodeAdapterTests.cs`

**Interfaces:**
- Consumes: Task 3 types and harness.
- Produces: `internal static class ClaudeRecordMapper { public static IReadOnlyList<TranscriptRecord> Map(JsonElement root, long start, long end); }` (throws `InvalidOperationException` on a string it cannot decode; the adapter catches it and counts the line); `internal static class UserTextCleaner { public static CleanedUserText Clean(string text); }`, `internal sealed record CleanedUserText(string? Text, IReadOnlyList<string> ShellInputs)`.
- Decisions this task fixes (the spec leaves them open): user `text` blocks are joined with `"\n\n"` into one `UserText`; each assistant block yields its own record in block order; `ToolStub.Text` and `ToolError.Text` carry the **raw** detail and error text (redaction, folding and cutting are the builder's, Task 5); a `<bash-input>` yields `ToolStub` with `ToolName = "Bash"`, emitted after the record's `UserText`; `SkippedLines` counts a line that is too long, not JSON (invalid UTF-8 and depth over 64 included), not an object, has a `type` that is absent or not a string, a `message` that is present and not an object, or a string that fails to decode; a string `type` outside the table yields nothing and is not counted; empty or whitespace-only lines are ignored, not counted. Extra context tags dropped like `<system-reminder>`: `<ide_opened_file>`, `<ide_selection>`, `<ide_diagnostics>` (any `<ide_[a-z_]+>` block).

- [ ] **Step 1: Write the failing mapper tests** (inline JSON lines, one Fact per table row): `UserString_IsUserText`; `UserTextBlocks_JoinedWithBlankLine`; `UserOriginHuman_IsUserText`; `UserOriginTaskNotification_IsNothing`; `QueuedHumanPromptAttachment_IsUserTextOfPrompt`; `QueuedAttachmentNotHumanOrNotPrompt_IsNothing` (Theory); `ToolResultError_StringAndTextBlocks_IsToolError`; `ToolResultSuccess_IsNothing`; `UserWithTextAndError_TextFirstThenError`; `AssistantText_IsAssistantText`; `AssistantBlocks_InOrder` (text, `tool_use`, text gives three records); `StructuredOutput_IsKeyValueAssistantText` (`{"verdict":"ok","n":3,"items":["a"]}` gives `"verdict: ok\nn: 3\nitems: [\"a\"]"`); `ExitPlanMode_IsPlanAsAssistantText`; `ToolUse_DetailByToolName` (Theory: `Bash`/`command`, `Read`/`Edit`/`Write`/`NotebookEdit`/`file_path`, `Grep`/`Glob`/`pattern`, `WebFetch`/`url`, `WebSearch`/`query`, `Task`/`Agent`/`description`, `AskUserQuestion`/`questions[0].question`); `UnknownTool_FirstStringFieldInDocumentOrder`; `ToolUse_MissingOrEmptyField_NullText`; `ThinkingImageOtherBlocks_AreNothing`; `CompactBoundary_IsBoundary`; `SystemOtherSubtype_IsNothing`; `IsCompactSummary_IsSummary_NotUserText`; `MetaAndSidechain_AreNothing` (Theory over user and assistant); `OtherRecordTypes_AreNothing` (Theory: `last-prompt`, `file-history-snapshot`, `cost-state`, `api-request-start`, another attachment type); `Timestamp_WithOffset_NormalisedToUtc`; `NoTimestamp_TsNull`; `Offsets_AreTheLines`.
- [ ] **Step 2: Write the failing cleaner tests.** `DropsEachListedBlockWithTags` (Theory over `system-reminder`, `task-notification`, `local-command-stdout`, `local-command-stderr`, `local-command-caveat`, `command-message`, `bash-stdout`, `bash-stderr`, `EXTREMELY_IMPORTANT`, `ide_selection`); `UnclosedBlockAtTextOrLineStart_DroppedToEnd`; `UnclosedBlockMidLine_IsKept`; `SlashCommand_BecomesPlainText` (`/develop 148`; without args `/develop`); `BashInput_BecomesShellInput_NotText`; `InterruptionNoticeOnly_IsNull` (`[Request interrupted by user]`, `[Request interrupted by user for tool use]`); `OnlyDroppedBlocks_IsNull`; `TextAroundBlocks_IsKeptAndTrimmed`; `ManyUnclosedTags_FinishesQuickly` (1 MiB of `<system-reminder>` without closers under 1 s).
- [ ] **Step 3: Extend the adapter tests (failing).** `ReadRecords_Decision_YieldsExpectedKindsInOrder` (the placed `decision` fixture gives the expected sequence of kinds and tool names; no record text holds `zqxtoolresult`, `zqxthinking`, `zqxreminder`, `zqxmeta`, `zqxsidechain`, `zqxnotification`); `Reader_OddLines_SkippedAndCounted` (Theory: invalid JSON, a JSON array, `"type": 5`, `"message": "x"`, a lone `\ud800` escape in user text, invalid UTF-8 bytes, 70 nested arrays: `SkippedLines == 1`, the next good line still yields its record); `Reader_OddLines_CrlfAndBom_Parse` (a CRLF file and a BOM-first file yield the same records as the LF file, with byte-exact offsets).
- [ ] **Step 4: Run them; expected: failures.**
- [ ] **Step 5: Implement** the mapper and cleaner (string scanning or bounded interpreted regexes; `JsonDocument.Parse` on `JsonlLine.Bytes`, which already exclude a BOM; `GetString()` failures caught as decode failures).
- [ ] **Step 6: Run `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~Transcripts"`; expected: pass.**
- [ ] **Step 7: Commit** `Map Claude Code records to transcript records and clean user text`.

### Task 5: The exchange builder

Spec: section 3 (stubs, errors, text cap), section 4 "Redaction runs on the full, untruncated text", section 5 "Units" and "Cutoff filter".

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/ExchangeBuilder.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/ExchangeBuilderTests.cs`

**Interfaces:**
- Consumes: `TranscriptRecord`, `RecordKind` (Task 3), `SensitiveContentScanner.Redact`, `TextCut` (Task 2).
- Produces:
  ```csharp
  internal enum BlockKind { User, Assistant, Stub, Error, Summary }
  internal sealed record UnitBlock(BlockKind Kind, string Text);   // rendered: "User: …", "Assistant: …", "[Bash: …]", "[error: …]", or the summary text
  internal sealed record ExchangeUnit(int Number, long StartOffset, DateTimeOffset? FirstTs, bool IsSummary,
      string? UserText, IReadOnlyList<UnitBlock> Blocks, int UserTurns);   // UserText: redacted and capped; UserTurns 0 or 1
  internal sealed record BuildResult(IReadOnlyList<ExchangeUnit> Units, int UserTurns, string? FirstUserText,
      DateTimeOffset? FirstTs, DateTimeOffset? LastTs);
  internal static class ExchangeBuilder
  {
      public const int StubLimit = 160, ErrorLimit = 500, TextCap = 100_000;
      public static BuildResult Build(IEnumerable<TranscriptRecord> records, DateTimeOffset? cutoff, int firstUnitNumber);
  }
  ```
  Stub text: `Redact(raw)` then `TextCut.Fold` then `TextCut.Cut(…, 160)`, wrapped `[<ToolName>: <detail>]`, or `[<ToolName>]` when the raw detail is null or empty. Error text: `Redact(raw)` then `TextCut.Cut(…, 500)` (newlines kept), wrapped `[error: <text>]`, or `[error]` when empty. User, assistant and summary text: `Redact` then `TextCut.Cap(…, 100_000)`. `FirstUserText` is the first unit's `UserText` that exists; `FirstTs`/`LastTs` are the smallest and largest `Ts` of the kept records.

- [ ] **Step 1: Write the failing tests.** `UserThenAssistantStubError_IsOneUnit`; `ConsecutiveUserTexts_EachStartAUnit`; `BoundaryClosesUnit_NextAssistantStartsAssistantOnlyUnit`; `Summary_IsItsOwnUnit_AndClosesTheOpenOne`; `ErrorWithNothingOpen_StartsAssistantOnlyUnit`; `ErrorWithUnitOpen_JoinsIt`; `BashInputStub_JoinsOpenUnit_IsNotAUserTurn`; `Numbers_StartAtFirstUnitNumber_AndAreConsecutive`; `Unit_StartOffsetIsFirstRecord_FirstTsIsFirstWithTs`; `Cutoff_RecordAtCutoffDropped_AfterKept` (a record with `Ts == cutoff` is dropped); `Cutoff_NullTsDroppedOnlyWhileCutoffExists`; `StraddlingExchange_KeepsTheAnswerAsAssistantOnlyUnit`; `NoKeptRecords_NoUnits`; `UserTurns_CountsUserTextOnly`; `Stub_RedactThenFoldThenCut160` (a multi-line command with an AWS key: the stub is `[Bash: ` + at most 160 characters + `...` + `]`, holds `[redacted:token]`, no newline); `Stub_EmptyDetail_IsBareName`; `Stub_TokenStraddling160_LeavesNoPrefix` (`{{PADTO:150}}`-style raw detail built in the test with `FakeSecrets`); `Error_Cut500WithEllipsis_NewlinesKept`; `Error_TokenStraddling500_LeavesNoPrefix`; `Error_Empty_IsBareError`; `Error_NotCappedAt100000_ButCutAt500`; `Text_Cap100000_AppendsRemainder` (a 100,010-character assistant text gives a block ending `[... 10 more characters not stored]`); `Text_SecretAtCapBoundary_RedactedBeforeCap`; `FirstUserText_IsRedacted`; `FirstAndLastTs`.
- [ ] **Step 2: Run them; expected: compile failures.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~ExchangeBuilder"`.
- [ ] **Step 3: Implement `Build`** as one pass over the records with an open-unit accumulator; a unit is emitted only when it holds a block.
- [ ] **Step 4: Run them; expected: pass.**
- [ ] **Step 5: Commit** `Build redacted exchange units from transcript records`.

### Task 6: The chunker

Spec: section 5 "Chunk text" and "Size", call 5, call 10.

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/TranscriptChunker.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptChunkerTests.cs`

**Interfaces:**
- Consumes: `ExchangeUnit`, `UnitBlock`, `BlockKind` (Task 5), `IngestChunk` (Task 1), `TextCut` (Task 2), `TranscriptTime.Iso` (Task 1), `SensitiveContentScanner.RedactionVersion`.
- Produces: `internal sealed record ChunkHeaderFacts(string Host, string HostSessionId, string ProjectKey, string Kind)`; `internal static class TranscriptChunker { public const int BodyLimit = 1_800, RepeatLimit = 300; public static IReadOnlyList<IngestChunk> Chunk(ExchangeUnit unit, ChunkHeaderFacts facts); }`.
- Header (exact): `$"{Host} session {first 8 characters of the id, or all of a shorter id} | project {ProjectKey} | {yyyy-MM-dd of FirstTs in UTC, or unknown} | {Kind} | {dialogue|compaction-summary} | exchanges {n}-{n}"`. `Content = header + "\n\n" + body`; blocks in a body are separated by `"\n\n"`; parts after the first begin with `"User (continued): " + TextCut.Cut(UserText, 300)` and a blank line, counted in the 1,800. `Ts = FirstTs is null ? null : TranscriptTime.Iso(FirstTs)`; `RedactionVersion = 1`; `HasError` when the part holds an error block or a piece of one; `ExchangeStart = ExchangeEnd = unit.Number`.

- [ ] **Step 1: Write the failing tests.** `SmallUnit_OnePart_ExactContent` (assert the whole content string for a unit with a user, an assistant, a stub and an error block, kind `interactive`, key `owner/repo`, id `0123456789abcdef`, `FirstTs` 2026-11-19T08:00:00Z: header `claude-code session 01234567 | project owner/repo | 2026-11-19 | interactive | dialogue | exchanges 4-4`); `SummaryUnit_NoLabels_TypeCompactionSummary`; `UnknownDate_HeaderSaysUnknown_TsNull`; `ShortId_UsedWhole`; `BodyExactly1800_IsOnePart`; `LongUnit_SplitsAtBlockBoundaries_EveryBodyAtMost1800`; `LaterParts_StartWithUserContinued_CutTo300`; `AssistantOnlyUnit_RepeatsNothing`; `LongBlock_SplitsOnParagraphThenLineThenWhitespaceThenHard` (four Theory rows, each body ≤ 1,800 and the split at the expected boundary); `LongBlock_UsesCapacityLeftInPart` (a block after a 1,000-character block is split at 800 minus the separator); `Chunk_HardSplit_NeverInsideMarkerOrSurrogate` (one 5,000-character word of markers and surrogate pairs: no part ends inside a marker or on a high surrogate); `HasError_OnlyOnPartsWithAnErrorPiece`; `Parts_Numbered0ToN_SameExchangeNumbers_EachWithHeader`; `NoTextLost` (the bodies, with the `User (continued):` lines removed and whitespace normalised, concatenate to the blocks joined by blank lines, whitespace normalised).
- [ ] **Step 2: Run them; expected: compile failures.**
- [ ] **Step 3: Implement `Chunk`** (greedy packing of blocks into parts; a block longer than the capacity left is split by the boundary order of section 5, the last resort at `TextCut.SafeLength`).
- [ ] **Step 4: Run `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~TranscriptChunker"`; expected: pass.**
- [ ] **Step 5: Commit** `Add the transcript chunker`.

### Task 7: Session kind, title and project identity

Spec: section 6 (all items), call 3, "Deviation" (kind from kept records).

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/SessionKindRule.cs`, `SessionTitle.cs`, `ProjectIdentityRule.cs`
- Modify: `src/TotalRecall.Infrastructure/Memory/ProjectResolver.cs` (add `ResolveRoot`; share the private walk)
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/SessionKindRuleTests.cs`, `SessionTitleTests.cs`, `ProjectIdentityRuleTests.cs`; extend `tests/TotalRecall.Infrastructure.Tests/Memory/ProjectResolverTests.cs`

**Interfaces:**
- Consumes: `TextCut` (Task 2), `ProjectResolver.Resolve`, `ClaudeCodeUsageImporter.DecodeProjectDirName` (internal, same assembly), `TranscriptStore.IsUnder` (internal).
- Produces:
  - `internal static class SessionKindRule { public const int LongPromptLimit = 1_500; public static readonly IReadOnlyList<string> Openings; public static string Decide(string? entrypoint, int settledUserTurns, int passUserTurns, string? passFirstUserText, string? storedKind); }` returning `"interactive"` or `"automated"`: entrypoint starting `sdk` (ordinal, ignore case) gives automated; with `settledUserTurns + passUserTurns == 1`: if `settledUserTurns == 1` the stored kind (or `interactive` when none), else automated when the trimmed text starts (ignore case) with one of `Review this change for security`, `You previously flagged`, `Scope:`, `You are reviewing`, `Read the full review brief`, or matches `^Round \d+` (ignore case), or is longer than 1,500; otherwise interactive.
  - `internal static class SessionTitle { public const int Limit = 120; public static string? From(string? redactedFirstTurn); }` (`TextCut.Cut(TextCut.Fold(text), 120)`; null or blank gives null).
  - `public string? ProjectResolver.ResolveRoot(string startDir)`: `.git` directory gives its parent; `.git` file with `commondir` gives the parent of the common git directory; `.git` file without `commondir` (submodule) gives the directory holding the file; a missing directory or no repository gives null. `Resolve` keeps its behaviour and tests.
  - `internal sealed record ProjectIdentity(string Key, string KeySource, string? Root, string? Cwd)`; `internal static class ProjectIdentityRule { public static ProjectIdentity Derive(string? headCwd, string projectDirName, ProjectResolver resolver, string? userHome); }` (items 1 to 6 of section 6; segments split on `/` and `\`; keys lower case; `KeySource` is `remote` when the key contains `/`, else `path`; the root is null when it is a filesystem root, equal to `userHome`, or an ancestor of it, compared with `TranscriptStore.IsUnder`). Item 7 (a `path` key never replaces a stored `remote` key) is the ingester's (Task 8).

- [ ] **Step 1: Write the failing kind and title tests.** `SdkEntrypoint_Automated` (Theory: `sdk-py`, `SDK-ts`, `sdk`); `CliEntrypoint_OneTurnOpening_Automated` (Theory over each opening, `round 3`, leading whitespace, lower case); `OneTurn1501Chars_Automated`; `OneTurn1500Chars_Interactive`; `TwoTurns_Interactive` (also with an opening); `NoTurns_Interactive`; `OneSettledTurn_KeepsStoredKind` (Theory: stored `automated` stays, stored null gives interactive); `Title_FoldsAndCuts120`; `Title_MarkerAtLimit_CutBeforeMarker`; `Title_NullOrBlank_IsNull`.
- [ ] **Step 2: Write the failing resolver and identity tests** (temporary directories built in the test: `.git/config` with `[remote "origin"] url = https://github.com/Owner/Repo.git`, a linked worktree as a `.git` file `gitdir: <main>/.git/worktrees/wt` whose directory holds `commondir` = `../..`, a submodule `.git` file without `commondir`):
  - `ResolveRoot_GitDirectory_IsItsParent`; `ResolveRoot_LinkedWorktree_IsMainRepo`; `ResolveRoot_Submodule_IsDirHoldingDotGitFile`; `ResolveRoot_NoRepoOrMissing_IsNull`.
  - `Identity_LiveRepoWithRemote_SlugRemoteRoot` (`owner/repo`, `remote`, root = repo dir); `Identity_LiveRepoNoRemote_FolderLowerPath`; `Identity_LiveWorktree_SameRootAsDeleted` (the worktree and, after deleting it, the same `cwd` give one root); `Identity_MissingWorktree_CutBackResolvesRepo` (`<repo>/.claude/worktrees/x` missing, repo present: key from the repo, root = repo); `Identity_MissingZoidWorktree_Same`; `Identity_MissingWorktreeAndRepo_LastSegmentLower` (`/gone/Repo/.claude/worktrees/x` gives key `repo`, `path`, root `/gone/Repo`); `Identity_MissingPlainDir_RootIsCwd`; `Identity_ExistingDirNoRepo_RootNull_KeyFolder`; `Identity_NoHeadCwd_DecodedDirName` (`-home-u-proj` gives `cwd` `/home/u/proj`); `Identity_NoCwdAtAll_Unknown` (empty dir name: key `unknown`, `path`, root null).
  - `Identity_OddPaths_*` (Review Focus 5): `WindowsCwd_SplitsOnBackslash` (`C:\Users\me\src\Repo\.claude\worktrees\x`, missing: key `repo`, root `C:\Users\me\src\Repo`); `MixedSeparatorsAndTrailingSeparator` (`/w/Repo\.claude/worktrees/x/` gives root `/w/Repo`); `CwdIsHome_RootNull` (with `userHome` = that directory, also when home holds a `.git` directory); `RootIsAncestorOfHome_RootNull` (a repository at `/tmp/x` with `userHome` `/tmp/x/home`); `FilesystemRoot_RootNull` (`/`, missing-path rule giving `/`).
- [ ] **Step 3: Run them; expected: compile failures.**
- [ ] **Step 4: Implement** the three rules and `ResolveRoot`.
- [ ] **Step 5: Run `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~SessionKind|FullyQualifiedName~SessionTitle|FullyQualifiedName~ProjectIdentity|FullyQualifiedName~ProjectResolver|FullyQualifiedName~Pinned"`; expected: pass** (the existing project-scoping tests still pass).
- [ ] **Step 6: Commit** `Add session kind, title and project identity rules for the archive`.

### Task 8: The per-session ingester

Spec: section 7 (steps 1 to 6 and the guarantees), section 6 item 7, section 1.

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/TranscriptIngester.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptIngesterTests.cs`, `TranscriptIngestRaceTests.cs`, `Support/ArchiveSnapshot.cs` (reads `tx_session` minus `id` and `source_mtime`, and `tx_chunk` minus `id`, as ordered row lists joined on `host_session_id`), `Support/HookAdapter.cs` (wraps an `ITranscriptAdapter`; counts calls per member; runs an `Action` before the first record of `ReadRecords` is yielded, once or every time)

**Interfaces:**
- Consumes: Tasks 1 to 7 (`ITranscriptStore.GetIngestState/IngestNotBefore/ApplyIngest/IsExcluded`, `ITranscriptAdapter`, `ExchangeBuilder.Build`, `TranscriptChunker.Chunk`, `SessionKindRule.Decide`, `SessionTitle.From`, `ProjectIdentityRule.Derive`).
- Produces: `internal enum SessionIngestStatus { New, Updated, Unchanged, Excluded, Empty, Skipped, Changed, Retry }`; `internal sealed record SessionIngestResult(SessionIngestStatus Status, int ChunksNew, int ChunksChanged, int ChunksUnchanged, int ChunksRemoved, int SkippedLines)`; `internal sealed class TranscriptIngester { public TranscriptIngester(ITranscriptStore store, ITranscriptAdapter adapter, ProjectResolver resolver, string? userHome); public SessionIngestResult IngestSession(SessionSource source, CancellationToken ct); }`. Exceptions propagate (the backfill logs and counts them).
- Decisions on top of section 7: the size, mtime and fingerprint written are `source.Size`, `source.MtimeMs` (observed before the read) and the head's; `projectDirName` for the decoded `cwd` is the file's parent directory name; a resumed pass whose build yields no unit is redone as a rewritten pass; the stored title is kept once set, else (`settled == 0`) `SessionTitle.From(FirstUserText)`; when the derived source is `path` and the stored one is `remote`, the stored key, source and root are kept (not a rewrite); status mapping: `Applied` + `Created` is `New`, other `Applied` with units is `Updated`, no kept content is `Empty` (a stored row is reset through `ApplyIngest`), `ApplyIngest` `Excluded` is `Excluded`, a second `Stale` or `Conflict` is `Retry`.

- [ ] **Step 1: Write the failing mode tests** (real store from `TranscriptTestDb`, `ClaudeFixtureHome`, `new ClaudeCodeAdapter(home.ProjectsDir)` wrapped in `HookAdapter`):
  - `Fresh_StoresSessionAndChunks_New` (`decision`: one row, kind `interactive`, title from the first prompt, `settled_user_turns` = turns before the last unit, `ingested_offset` = start of the last unit's first line, `exchange_count` = last unit + 1, `built_under_cutoff` null).
  - `SecondRun_Unchanged_NoRead_IdsAndEmbeddedKept` (set `embedded = 1` on every chunk; second pass `Unchanged`, `HookAdapter` saw no `ReadHead` and no `ReadRecords`, the snapshot including ids and `embedded` is equal).
  - `Grown_AtEveryCutPoint_EqualsIngestedOnce` (for `decision` and `compaction`: for every line boundary and one mid-line offset per line of the rendered bytes, write the prefix, ingest, write the whole file, ingest, and compare `ArchiveSnapshot` with a fresh database that ingested the whole file once; this covers a cut inside an exchange, between exchanges, just after a compaction summary and in the middle of a line).
  - `KindFlip_OnGrowth_RedoesAsRewritten` (`automated-opening` stored `automated`; `AppendExchange`; next pass gives `interactive`, every chunk header says `interactive`, equal to once-ingested).
  - `DeletedSource_KeepsRowAndChunks`.
  - `ForgottenSession_FileExists_NotOpened` (`Excluded`; no `ReadHead`); `ForgottenProjectByKey_AndByRoot_StayOut` (Theory).
  - `MtimeAtOrBeforeCutoff_Excluded_NotRead`.
  - `Straddle_OnlyRecordsAfterCutoff` (cutoff `T+50` set by writing `tx_state` through `ForgetAll(BaseTime + 50 s)`: the `T+50` question is gone, its `T+60` answer is an assistant-only unit, `T+90` kept; `built_under_cutoff` = the raw text).
  - `UnforgetCutoff_UnchangedFile_HiddenRecordsReturn` (after `ClearCutoff`, the same file is a rewritten pass and equals once-ingested with no cutoff).
  - `CutoffHidesEverything_RowReset_Empty` (a stored session, then a cutoff after its last record and a touched mtime: chunks deleted, row offset 0, `exchange_count` 0, kind kept; the next pass after unforget is rewritten).
  - `ReplacedFile_Changed_ChunksKept` (`Replace` with another fixture: new fingerprint); `TruncatedFile_Changed`; `OffsetNotAtLineStart_Changed` (same first line, larger file, a byte that is not `0x0A` before the stored offset).
  - `NoKeptContent_StoresNothing_Empty` (`no-kept`); `NoCompleteFirstLine_Skipped`.
  - `PartialTrailingLine_NotConsumed_ThenReadOnce` and `GrownBetweenStatAndRead_IsReadAgainNextPass` (the source's `Size` is from before an append that happens in the hook: the next pass is not `Unchanged` and the end state equals once-ingested).
  - `PathKeyNeverReplacesRemoteKey` (stored `owner/repo`/`remote`; repository deleted; resumed pass keeps key, source and root and is not rewritten).
- [ ] **Step 2: Write the failing race tests** (`TranscriptIngestRaceTests`): `ForgetSessionDuringPass_IsExcluded_NothingWritten` (hook calls `ForgetSession`); `ForgetProjectDuringPass_IsExcluded`; `ForgetAllDuringPass_IsStale_RedoStoresOnlyAfterCutoff` (hook once: final state holds only records after the cutoff); `CutoffChangingEveryRead_IsRetry` (hook every time: `Retry`, nothing written by the pass); `ConcurrentPass_IsConflict_EndsLikeOnePass` (hook once runs a second `TranscriptIngester.IngestSession` of the same source on another store connection opened on the same file: the outer pass redoes, and the snapshot equals one pass).
- [ ] **Step 3: Run them; expected: compile failures.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~TranscriptIngest"`.
- [ ] **Step 4: Implement `IngestSession`** following section 7 steps 1 to 6 in order, with the decisions above; the redo for a changed kind and the redo after `Stale`/`Conflict` each happen at most once per call.
- [ ] **Step 5: Run them; expected: pass.**
- [ ] **Step 6: Commit** `Add the per-session transcript ingester with resume and exclusion`.

### Task 9: The backfill loop and the corpus acceptance tests

Spec: section 1 (data flow, failures), section 2 duplicate ids, section 7 guarantees, section 10 assertions, the Acceptance list.

**Files:**
- Create: `src/TotalRecall.Infrastructure/Transcripts/TranscriptBackfill.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Transcripts/TranscriptBackfillTests.cs`, `TranscriptCorpusTests.cs`

**Interfaces:**
- Consumes: `TranscriptIngester` (Task 8), `ITranscriptAdapter`, `ITranscriptStore`, `ExceptionLogger.LogChain`.
- Produces (public):
  ```csharp
  public sealed record BackfillProgress(int Done, int Total);
  public sealed record BackfillSummary(int Seen, int New, int Updated, int Unchanged, int Excluded, int Empty,
      int Skipped, int Changed, int Retry, int Failed, int ChunksNew, int ChunksChanged, int ChunksUnchanged,
      int ChunksRemoved, int SkippedLines, IReadOnlyList<string> ChangedSessions, bool Cancelled);
  public sealed class TranscriptBackfill
  {
      public TranscriptBackfill(ITranscriptStore store, ITranscriptAdapter adapter, ProjectResolver? resolver = null, string? userHome = null);
      public BackfillSummary Run(CancellationToken ct, Action<BackfillProgress>? progress = null);
  }
  ```
  `Seen` = valid sources + names skipped by the enumeration. `ChangedSessions` entries are `<host>:<id>`. A `Detect()` of false gives an all-zero summary. Duplicate ids: the source whose path equals the stored `SourcePath` wins, else the newest `MtimeMs`; each loser counts `Skipped`. Order: newest `MtimeMs` first. Cancellation is checked between sessions (`Cancelled = true`, the summary so far). `progress` is called after every session. Each session runs in a `try`; an exception (not `OutOfMemoryException`) is logged with `ExceptionLogger.LogChain("[total-recall] transcripts backfill: " + source.Path, ex)` and counted `Failed`.

- [ ] **Step 1: Write the failing backfill tests.** `NoProjectsDir_ZeroSummary`; `NewestFirst_ProgressPerSession`; `DuplicateId_StoredSourcePathWins_ElseNewest_LoserSkipped`; `BadNames_CountedSkippedAndSeen`; `OneFailingSource_CountedFailed_OthersStored` (a `HookAdapter` that throws for one source); `Cancelled_StopsAfterCurrentSession` (cancel from the progress callback after the first session: `Cancelled`, one session stored); `ChangedSessions_ListHostAndId`.
- [ ] **Step 2: Write the failing corpus acceptance tests** (`TranscriptCorpusTests`: place every fixture under its own `cwd`, one backfill, then assertions on `Status()` and raw SQL):
  - `Backfill_Corpus_SessionsAndChunksPerProjectAndKind` (`ByProjectAndKind` holds the expected `(key, kind)` rows: `decision` interactive, `automated-sdk` and `automated-opening` automated, a repository with a remote under its slug).
  - `Corpus_SecondRun_ChangesNothing` (summary `Unchanged == stored sessions`, all chunk counts 0, snapshot with ids and `embedded` equal).
  - `Corpus_DroppedContent_NotStored` (no row holds `zqxtoolresult`, `zqxthinking`, `zqxreminder`, `zqxmeta`, `zqxsidechain`, `zqxnotification`, `zqxeditinput`); `Corpus_KeptContent_Stored` (`zqxqueued`, `/develop 148`, `[Bash: ls -l]`, `[AskUserQuestion: ` with `zqxquestion`, `zqxplan` and `zqxverdict` under `Assistant:`).
  - `Corpus_Error_StoredCutTo500` (the chunk holds `Parse error: no such table: zqx_schema_version`, `has_error = 1`, and the error block's text is at most 500 characters plus `...`).
  - `Corpus_CompactionSummary_OwnChunkType` (`chunk_type = 'compaction-summary'` holds `zqxsummary`, no `User:` label).
  - `Corpus_PlantedSecrets_OnlyMarkersStored` (for every planted value: `LeakScan.AssertNotStored`; the marker of its category is stored; the stub straddling 160 and the error straddling 500 hold their marker and no prefix; the session title holds a marker; `GH_TOKEN="$t" gh api` and `API_KEY=<your-key>` are stored verbatim; the byte scan runs once with the store open, so the `-wal` and `-shm` files exist, and once after it is disposed).
  - `Corpus_DeletedSource_KeepsChunks`; `Corpus_ForgottenSessionAndProject_StayOut`; `Corpus_Straddle_OnlyAfterCutoff`; `Corpus_UnforgetCutoff_HiddenRecordsStored`; `Corpus_ReplacedOrShortened_Changed_Kept`.
  - `Corpus_Worktree_MissingDirKeyFromRepo_AndRepoWithRemote` (the `worktree-missing` session under `<repo>/.claude/worktrees/gone` gets the repository's slug; `worktree-repo` gets `owner/repo`, `remote`).
- [ ] **Step 3: Run them; expected: compile failures.** `dotnet test tests/TotalRecall.Infrastructure.Tests --filter "FullyQualifiedName~TranscriptBackfill|FullyQualifiedName~TranscriptCorpus"`.
- [ ] **Step 4: Implement `TranscriptBackfill`.**
- [ ] **Step 5: Run the whole Transcripts filter; expected: pass.**
- [ ] **Step 6: Commit** `Add the transcript backfill loop and the corpus acceptance tests`.

### Task 10: `transcripts backfill` and the counts in `status`

Spec: section 9, call 2, the Acceptance bullets for the command (archive off, failed file, changed listing).

**Files:**
- Create: `src/TotalRecall.Cli/Commands/Transcripts/BackfillCommand.cs`
- Modify: `src/TotalRecall.Cli/CliApp.cs` (`BuildRegistry`), `src/TotalRecall.Cli/Commands/Transcripts/StatusCommand.cs`
- Test: `tests/TotalRecall.Cli.Tests/Commands/Transcripts/BackfillCommandTests.cs`; extend `StatusCommandTests.cs` and `TranscriptsCliTests.cs` (group help lists six verbs)

**Interfaces:**
- Consumes: `TranscriptsCommandBase` seams, `TranscriptStore.Open`, `TranscriptBackfill`, `ClaudeCodeAdapter`, `TranscriptStatus.ByProjectAndKind`, the linked `ClaudeFixtureHome` (Task 3).
- Produces: `public sealed class BackfillCommand : TranscriptsCommandBase` with `Name => "backfill"`, a production constructor, and a test constructor taking the base's nine seams plus `Func<string, ITranscriptAdapter> adapterFactory` and `CancellationToken cancellation` (production: `"claude-code"` gives `new ClaudeCodeAdapter()`, and Ctrl+C cancels through `Console.CancelKeyPress` with `e.Cancel = true`). `Usage => "Usage: total-recall transcripts backfill [--host claude-code]"`.
- Order (section 9): arguments, `ConfigReadable()`, `ReadSettings().Enabled` and storage mode not postgres, `ResolveLocation`, `TranscriptStore.Open(location, create: false)`, and only then `adapterFactory(host)`.
- Output (exact copy): archive off: `transcripts backfill: the transcript archive is off; run 'total-recall transcripts enable' in a terminal to turn it on` (exit 1); tables absent: `transcripts backfill: the archive tables do not exist at <Describe(location)>; 'total-recall transcripts enable' creates them` (exit 1); no projects directory: `no Claude Code sessions found at <adapter.Root>` (exit 0); progress every 50: `  <done>/<total> sessions`; summary:
  ```
  backfill claude-code: <seen> sessions seen
    new <n>, updated <n>, unchanged <n>, excluded <n>, empty <n>, skipped <n>, changed <n>, retry <n>, failed <n>
    chunks: new <n>, changed <n>, unchanged <n>, removed <n>
    unreadable lines: <n>                                  (only when > 0)
    changed (kept as stored): <host>:<id>                  (one line each)
    interrupted: the remaining sessions were not processed  (only when cancelled)
  ```
  Exit 0 when `Failed == 0`, else 1. `status` prints, after the `chunks:` line, `by project and kind:` and one line `  <key> <kind>: <s> sessions, <c> chunks` per row (at most 30, then `  and <n> more`), nothing when there are no rows.

- [ ] **Step 1: Write the failing tests** (`[Collection("ConsoleCapture")]`, `TranscriptsHarness` for the data directory, `ClaudeFixtureHome` for the projects tree, a `ThrowingAdapter` double whose every member throws, and a factory that records whether it was called):
  - `ArchiveOff_RefusesExit1_FactoryNeverCalled_NothingRead`; `Postgres_RefusesExit1_FactoryNeverCalled`; `TablesAbsent_SaysEnableCreatesThem_Exit1_FactoryNeverCalled`; `MalformedConfig_Exit1_FactoryNeverCalled`.
  - `UnknownHost_Exit2` (`--host pi`); `HostWithoutValue_Exit2`; `UnexpectedArgument_Exit2`; `NeedsNoTerminal` (`isInteractive = false` still runs).
  - `Backfill_Corpus_StoresAndPrintsSummary_Exit0` (counts in the summary lines; then `status` shows `by project and kind:` with the expected rows); `SecondRun_AllUnchanged`.
  - `Version1Archive_IsMigratedTo2` (the test turns a fresh archive back into version 1 with raw SQL: `DROP INDEX idx_tx_session_project_kind`, `ALTER TABLE tx_session DROP COLUMN` for the three new columns, `DELETE FROM tx_schema_version WHERE version = 2`; after the run `status` prints `schema version: 2`).
  - `OneFailingFile_CountedFailed_OthersStored_Exit1` (a wrapping adapter that throws in `ReadRecords` for one id).
  - `ReplacedFile_ListedAsChanged_ChunksKept` (line `changed (kept as stored): claude-code:<id>`).
  - `Cancelled_PrintsSummarySoFar` (pre-cancelled token: `interrupted:` line, exit 0).
  - `Progress_Every50Sessions` (55 tiny sessions: exactly one `  50/55 sessions` line).
  - `StatusCommandTests`: `Status_PrintsProjectKindRows`; `Status_MoreThan30Rows_PrintsAndNMore` (31 rows inserted by raw SQL: 30 lines and `  and 1 more`); `Status_KeepsSearchNotAvailableLine`.
  - `TranscriptsCliTests`: group help lists `backfill`.
- [ ] **Step 2: Run them; expected: compile failures.** `dotnet test tests/TotalRecall.Cli.Tests --filter "FullyQualifiedName~Transcripts"`.
- [ ] **Step 3: Implement** the command and the `status` rows; register `new Commands.Transcripts.BackfillCommand()` after `StatusCommand` in `BuildRegistry`.
- [ ] **Step 4: Run the Cli tests (including `HiddenCommandTests`); expected: pass.**
- [ ] **Step 5: Commit** `Add the transcripts backfill command and project counts in status`.

### Task 11: Documentation and the enable text

Spec: section 11, section 4 "Residual risk", call 8 (the known `AskUserQuestion` loss), "Deviation" (known limits).

**Files:**
- Modify: `docs/transcripts.md`, `docs/commands.md`, `PRIVACY.md`, `docs/configuration.md`, `README.md`, `CHANGELOG.md` (unreleased, internal), `src/TotalRecall.Infrastructure/AGENTS.md` (archive migration list), `AGENTS.md` (the transcript archive row of "WHERE TO LOOK"), `src/TotalRecall.Cli/Commands/Transcripts/EnableCommand.cs`
- Test: `tests/TotalRecall.Cli.Tests/Commands/Transcripts/EnableCommandTests.cs`; `scripts/host-docs.test.js` runs in `npm run test:shim`

**Interfaces:**
- Enable prompt (exact): `"Enable the transcript archive? total-recall will keep the dialogue of your agent sessions (your prompts and the assistant's replies) in " + The(location) + ", locally on this machine, with known secret shapes redacted, and never synced. Nothing is read until you run 'total-recall transcripts backfill', and the archive cannot be searched yet in this version. You can delete it at any time with 'total-recall transcripts forget'. Continue?"`
- Enable success line (exact, replacing "Nothing is stored yet in this version: …"): `"Run 'total-recall transcripts backfill' to back up your Claude Code sessions; searching the archive arrives in a later release."`

- [ ] **Step 1: Update the enable tests (failing).** The prompt test asserts `transcripts backfill` and `cannot be searched yet` and no `Nothing is stored yet`; the success test asserts the new line.
- [ ] **Step 2: Run `dotnet test tests/TotalRecall.Cli.Tests --filter "FullyQualifiedName~EnableCommand"`; expected: failures.**
- [ ] **Step 3: Change the two strings in `EnableCommand`; rerun; expected: pass.**
- [ ] **Step 4: Write the documents.** `docs/transcripts.md`: correct the status banner and the "What will be stored" section (the archive can be enabled, backed up with `backfill`, inspected and emptied; not searched yet; no part numbers); new sections "Backing up Claude Code sessions" (`transcripts backfill [--host claude-code]`, no terminal needed, refuses while off, where the files are: `$CLAUDE_CONFIG_DIR/projects` or `~/.claude/projects`, subagent files not read, the summary counts and exit codes, `changed` and the forget/unforget/backfill way to store a new version), "What is kept and what is dropped" (decision 3 list; `StructuredOutput` and `ExitPlanMode` stored as assistant text; the user's `AskUserQuestion` choice is lost; the 100,000-character cap; stubs 160, errors 500), "What is redacted and what is not" (the five categories and their markers, the reference/placeholder exemption, `unprocessable`, then the residual risk of section 4 verbatim in substance), "How resume works" (two lines: per-session byte watermark at the start of the last exchange; a grown file is read from there, a replaced or shortened one is kept as stored), and "Project and kind" (key from the repository, worktrees map to their repository, `interactive`/`automated` and the `entrypoint` rule); update the "Forgetting" section: a forgotten project records its sessions' repository roots, and a project without a repository is excluded by key only. `docs/commands.md`: the `backfill` row and the corrected intro sentence. `PRIVACY.md`: the stored-data row (after `backfill` the archive holds the dialogue of the user's Claude Code sessions, locally, with known secret shapes redacted, and what is not covered, linking `docs/transcripts.md`) and the retention paragraph (nothing the host deletes later is removed from the archive); keep every Rule 16 needle and avoid the forbidden phrases. `docs/configuration.md`, `README.md`: correct "nothing is stored". `CHANGELOG.md`: an unreleased internal entry, not an announcement. `src/TotalRecall.Infrastructure/AGENTS.md`: archive migration 2 (`settled_user_turns`, `built_under_cutoff`, `project_root`, `idx_tx_session_project_kind`). `AGENTS.md`: the row adds `Transcripts/ClaudeCodeAdapter.cs`, `ExchangeBuilder.cs`, `TranscriptChunker.cs`, `TranscriptIngester.cs`, `TranscriptBackfill.cs`, `Eval/SensitiveContentScanner.cs` (`Redact`), `Cli/Commands/Transcripts/BackfillCommand.cs`, `tests/fixtures/transcripts/claude-code/` and the harness path. No new document may match `sync\s+(init|join|leave|status)`.
- [ ] **Step 5: Run `npm run test:shim` and `dotnet test src/TotalRecall.sln --filter "FullyQualifiedName~HiddenCommand|FullyQualifiedName~Transcripts"`; expected: pass.**
- [ ] **Step 6: Run the full verify blocks of `.github/develop.md`; expected: all pass and `dump-catalog | diff -u catalog.json -` prints nothing.**
- [ ] **Step 7: Commit** `Document backing up Claude Code sessions into the transcript archive`.
