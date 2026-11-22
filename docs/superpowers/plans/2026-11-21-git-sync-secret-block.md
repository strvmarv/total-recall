# Git sync secret block (milestone 1b-iii part 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A token or private key in a memory's text, tags, project or scope is never written to the sync repository: the scan stamps `secret-detected` on that memory, the push re-checks every memory it is about to write, a hidden `sync unblock <id>` releases one, and `sync status` lists the blocked ones.

**Architecture:** A pure Core module `SyncSecret` holds the key and the decision table (spec D1, D3). The C# scanner runs the detectors after its plan and stamps the row it already holds; the pusher's preparation runs the detectors outside the plan transaction and `PlanOne` only compares keys and blocks (D4, D5). `ScanSecrets` widens by reusing `Redact`'s pattern definitions plus today's own as legacy entries, with a fail-closed timeout (D9). `unblock` is a new `SyncSetupService` partial and a hidden CLI command (D7); `sync status` gains a blocked section (D8).

**Tech Stack:** .NET 10, C# (Infrastructure, Cli) and F# (Core), xUnit, SQLite, a bare git repository in tests.

**Spec:** `docs/superpowers/specs/2026-11-21-git-sync-secret-block-design.md` (read it first; its decisions are quoted below as "D<n>"). Parent: `docs/superpowers/specs/2026-11-21-git-sync-review-secrets-design.md` (Part 1 and the Shared decisions). Companions: `2026-11-17-git-sync-{scan,push}-design.md`, umbrella `2026-11-15-git-sync-design.md`.

## Global Constraints

- The approved spec is read-only for the whole run: `git diff --quiet 2353d726c1e0d48188e9115f4a4ef6b4e0633f4c HEAD -- docs/superpowers/specs/2026-11-21-git-sync-secret-block-design.md` must exit 0 before every push.
- No schema change, no migration. The columns `blocked_hash`, `blocked_reason`, `allowed_hash` already exist.
- Nothing outside `GitSyncCycle*`, `GitSyncPusher*`, `GitSyncApplier*` and their tests may name the cycle or the pusher; the new `unblock` service and command must not name either. `GitSyncCycleCallerTests` stays unchanged and green. `sync unblock` is hidden (`SyncCommandBase.Hidden`) and must appear in no doc, README, catalog or hook (`HiddenCommandTests` scans for it).
- No `--force` on any `git push` in the suite or the code. No AI attribution or co-author trailer in any commit message. Never commit `TestResults` hang dumps.
- The reason text is exactly `secret-detected`. The key prefix is exactly `total-recall/scan/secret/v1\n`. Detector category labels stay `API token or secret` and `private key block`; the timeout label is `scan timed out`. Nothing prints, logs or stores a matched value.
- `Redact`'s pattern set, output and `RedactionVersion` do not change; its tests stay green unchanged.
- Test projects: Core tests (`*.fs`) in `tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`; C# tests in `tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj` (CLI tests in `tests/TotalRecall.Cli.Tests`). A `--filter` applies to the project its test names live in.
- Dev loop (each task): `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~Sync"` and `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync|FullyQualifiedName~SensitiveContent"`; the release build adds no warning (`dotnet build src/TotalRecall.sln -c Release`).
- Where a task changes behaviour an existing test pins, change that test in the same task and say so in the commit message.

## Review Focus

1. A token that arrives through the apply (a merged tag union) after the scan has run: the push must not write it (Task 4).
2. A blocked memory that also breaks a writer limit (`too-large`): the secret reason replaces the writer's, a clean re-scan lifts it, and the writer's reason is then re-marked by the next push, never silently dropped (Tasks 3, 4).
3. `unblock` racing an edit: the text changes after the block, so the key differs from `blocked_hash` and `unblock` refuses without writing (Task 5).
4. A forgotten or knowledge-retyped memory that kept its `blocked_*` columns: it is not listed by `status`, and `unblock` refuses it (Tasks 5, 6).
5. A string only the legacy `sk-` pattern matches (`mask-abcdefghijklmnopqrst`) and `-----BEGIN PGP PRIVATE KEY BLOCK-----`: both are held back, while `git@github.com` and `noreply@example.com` are not (Task 2).

---

## File map

| File | Change |
|---|---|
| `src/TotalRecall.Core/SyncScan.fs` | the four hash helpers become `internal` (no behaviour change) |
| `src/TotalRecall.Core/SyncSecret.fs` (new) + fsproj | key, `precheck`, `markAfterScan`, `blockReason` |
| `src/TotalRecall.Core/SyncSetup.fs` | refusals `NotBlocked`, `UnknownEntry`, `UnreadableEntry`, `ChangedSinceBlocked`; `StatusFacts.Blocked` and its rendering |
| `src/TotalRecall.Infrastructure/Eval/SensitiveContentScanner.cs` | widened `ScanSecrets`, timeout overload, fail closed, comments |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncScanner*.cs` (+ `GitSyncScanner.Secrets.cs` new) | the secret pass, `ScanSummary.Blocked`/`Unblocked`, a detector seam |
| `src/TotalRecall.Infrastructure/GitSync/GitSyncPusher.Prepare.cs`, `.Plan.cs`, `.cs` | prepared secret checks, `PlanOne` block, reason naming |
| `src/TotalRecall.Infrastructure/GitSync/SyncSetupService.Unblock.cs` (new), `SyncSetupService.LeaveStatus.cs` | `UnblockAsync`; the blocked query for status |
| `src/TotalRecall.Cli/Commands/Sync/UnblockCommand.cs` (new), `CliApp.cs` | the hidden command |
| tests under `tests/TotalRecall.Core.Tests`, `tests/TotalRecall.Infrastructure.Tests` (`Eval`, `GitSync`), `tests/TotalRecall.Cli.Tests` | per task |
| `AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md` | Task 6 |

---

### Task 1: Core `SyncSecret` (pure key and decisions)

**Files:**
- Modify: `src/TotalRecall.Core/SyncScan.fs` (make `lengthBytes`, `addBytes`, `addField`, `sha256Hex` `internal`)
- Create: `src/TotalRecall.Core/SyncSecret.fs` (add to `TotalRecall.Core.fsproj` right after `SyncScan.fs`), `tests/TotalRecall.Core.Tests/SyncSecretTests.fs` (add to the tests fsproj after `SyncScanTests.fs`)

**Interfaces:**
- Produces (module `TotalRecall.Core.SyncSecret`):
  - `blockReason : string` = `"secret-detected"`
  - `secretKey (content: SyncMerge.ContentFields) (tags: string list) : string` (D1): SHA-256 lowercase hex of `total-recall/scan/secret/v1\n`, then `addField` of text, project, scope (the same optional-string encoding `contentHash` uses), then the normalised tags exactly as `tagsHash` encodes them (4-byte count, each tag length-prefixed). Entry type, source and `created_at` are not in it.
  - `type Precheck = Allowed | StillBlocked | NeedsScan`
  - `precheck (reason: string option) (blockedHash: string option) (allowedHash: string option) (key: string) : Precheck` (D3 rows 1, 2 and the rest): `Allowed` when `allowedHash = Some key`; else `StillBlocked` when `reason = Some blockReason` and `blockedHash = Some key`; else `NeedsScan`.
  - `type MarkChange = SetBlock of key: string | ClearBlock | KeepMark`
  - `afterScan (reason: string option) (hit: bool) (key: string) : MarkChange` (D3 rows 3 to 5): `hit` gives `SetBlock key` whatever the reason; no hit gives `ClearBlock` when `reason = Some blockReason`, else `KeepMark`.
  - `afterAllowed (reason: string option) : MarkChange`: `ClearBlock` when `reason = Some blockReason`, else `KeepMark`.

- [ ] **Step 1: Write the failing tests** in `SyncSecretTests.fs`, module `TotalRecall.Core.Tests.SyncSecretTests`: `secretKey` changes when each of text, project, scope or any tag changes; is unchanged by entry type, source tool, `created_at` and by tag order/duplicates; two different tag splits (`["ab"]` vs `["a";"b"]`) differ; `precheck` for each D3 row (allowed wins over a matching blocked hash; `StillBlocked` only for reason `secret-detected` with the equal key; a writer reason `too-large` with an equal `blockedHash` is `NeedsScan`); `afterScan` (hit replaces `too-large`; no hit clears `secret-detected`; no hit keeps `too-large` and `None`); `afterAllowed` (clears only `secret-detected`).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~SyncSecret"`. Expected: FAIL (module not defined).
- [ ] **Step 3: Implement** `SyncSecret.fs` with the signatures above; reuse `SyncScan`'s now-internal helpers and `SyncRecord.normaliseTags`. Update both fsproj files.
- [ ] **Step 4: Run** the same filter, then the whole Core `Sync` filter. Expected: PASS, nothing else changed.
- [ ] **Step 5: Commit** `git add src/TotalRecall.Core tests/TotalRecall.Core.Tests && git commit -m "Add the git sync secret key and block decisions"`.

---

### Task 2: The detector (`ScanSecrets` widening, timeout, fail closed)

**Files:**
- Modify: `src/TotalRecall.Infrastructure/Eval/SensitiveContentScanner.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/Eval/SensitiveContentScannerTests.cs` (add a class `SensitiveContentScannerSecretsTests` in a new file `SensitiveContentScannerSecretsTests.cs` in the same folder)

**Interfaces:**
- Produces:
  - `public static IReadOnlyList<string> ScanSecrets(string? text)` (signature unchanged) now equals `ScanSecrets(text, SecretScanTimeout)` with `SecretScanTimeout = TimeSpan.FromSeconds(1)`.
  - `internal static IReadOnlyList<string> ScanSecrets(string? text, TimeSpan matchTimeout)` (test seam).
  - `Scan(text, terms, detectors)` keeps its signature; its Token and PrivateKey detectors use the same widened implementation and its three built-in detectors use the same 1 s bound.
  - On a `RegexMatchTimeoutException` the result contains the reason `scan timed out` (fail closed: callers treat any reason as a hit).

- [ ] **Step 1: Write the failing tests** (`ScanSecrets` unless noted): `sk-ant-api03-` + 40 letters, `sk-proj-` + 40 letters, `ghp_` + 20 letters, `github_pat_` + 30 letters, `AKIA` + 16 caps each give `["API token or secret"]`; `-----BEGIN PGP PRIVATE KEY BLOCK-----`, `-----BEGIN RSA PRIVATE KEY-----` and `-----BEGIN OPENSSH PRIVATE KEY-----` each give `["private key block"]`; `mask-abcdefghijklmnopqrst` gives `["API token or secret"]` (the named legacy false positive); `git@github.com`, `noreply@example.com`, `""`, `null` and ordinary prose give `[]`; a pathological text with the `ScanSecrets(text, TimeSpan.FromTicks(1))` overload on a 200 000-character `sk-` followed by repeated `a-` run returns exactly `["scan timed out"]` (if the regex engine finishes within one tick on this machine the test must be made to time out another way: use `new string('a', 2_000_000)` after `sk-`; the test must fail if no timeout is produced and also if the result is empty); `Scan(text, null, Detectors.Token)` on the same text also contains `scan timed out`; the existing `SensitiveContentScannerTests` and `SensitiveContentScannerRedactTests` still pass unchanged (run both files).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SensitiveContentScanner"`. Expected: FAIL on the new tests only.
- [ ] **Step 3: Implement.** Reuse the existing `RedactionPatterns` class for the shared definitions: add a second static instance built with `SecretScanTimeout` and take its `Tokens` and `PrivateKeyBegin`; add two legacy `Regex` entries with the same timeout (the unbounded `sk-[A-Za-z0-9]{16,}` and `-----BEGIN [A-Z ]*PRIVATE KEY-----`, copied from today's `_detectors`); build the three built-in `_detectors` regexes with the timeout too. `Redact` and `RedactionPatterns` are not otherwise touched. Reasons keep today's order (email, token, private key, internal term); a timeout adds `scan timed out` once and stops scanning that call. Correct the two stale comments (the "runs only on eval_grow accept" one and the header note that `Scan` and `ScanSecrets` do not use `Redact`'s patterns).
- [ ] **Step 4: Run** the filter from step 2 plus `FullyQualifiedName~BenchmarkCandidates` and `FullyQualifiedName~EvalGrow`. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Widen the secret detector to Redact's shapes and fail closed on a timeout"`.

---

### Task 3: The scanner stamps the secret block

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/GitSyncScanner.Secrets.cs`
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncScanner.cs` (`ScanSummary` and the seam), `GitSyncScanner.Plan.cs` (call the pass at the end of `BuildPlan`)
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncScannerSecretTests.cs` (new); rename and adjust `AnUpdate_KeepsTheBaseAndEveryColumnItDoesNotOwn` in `GitSyncScannerStampTests.cs`; fix the `ScanSummary` constructor call in `GitSyncApplierPullTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SyncSecret.secretKey`, `precheck`, `afterScan`, `afterAllowed`, `blockReason`), Task 2 (`SensitiveContentScanner.ScanSecrets`).
- Produces:
  - `ScanSummary` gains two last positional parameters `IReadOnlyList<string> Blocked, IReadOnlyList<string> Unblocked` (ids whose secret block was set / lifted by this scan, ordinal); `ScanSummary.Empty` and every positional constructor call follow.
  - `internal Func<string?, IReadOnlyList<string>> SecretDetector { get; set; }` on `GitSyncScanner`, default `SensitiveContentScanner.ScanSecrets` (the test seam that counts detector calls).
  - `private void ApplySecretPass(Snapshot snap, ScanPlan plan, List<string> blocked, List<string> unblocked)` called at the end of `BuildPlan`, before the summary is built (D4).

- [ ] **Step 1: Write the failing tests** (names in `GitSyncScannerSecretTests`, using the existing scanner test support and a counting `SecretDetector`): `A_TextWithAToken_IsBlockedWithTheKey` (row has `BlockedReason = "secret-detected"`, `BlockedHash = SyncSecret.secretKey(...)`, listed in `Summary.Blocked`); `A_TokenInATag_ProjectOrScope_IsBlocked` (one case each); `AnEntryTheMainLoopCallsNoChange_IsStillChecked` (an active row whose scanned hashes match the entry and whose columns are all null, entry text holds a token: blocked, exactly one row written for the id); `ABlockedUnchangedEntry_IsNotScannedAgain` (detector call count 0 on the second scan); `AnAllowedUnchangedEntry_IsNotScanned_AndKeepsItsAllowedHash`; `ACleanEdit_LiftsTheBlock_AndListsItUnblocked`; `ATokenAddedInATagAfterAnUnblock_IsBlockedAgain` (row with `AllowedHash = key(before)`); `ASecretReplacesAWriterReason` (row had `too-large`, now `secret-detected`, and a later clean re-scan leaves reason null, not `too-large`); `AWriterReasonIsKept_WhenThereIsNoHit` (`too-large` stays); `AReactivatedEntry_StartsClean_ThenIsChecked` (a `forgotten` row with an entry whose text holds a token: the fresh row is blocked in the same write); `ForgottenAndLeftRowsWithoutAnEntry_AreNotEvaluated`; `OneWritePerId` (an entry whose content also changed produces a single row write carrying both the new stamp and the block); `email_only_text_is_not_blocked` (`git@github.com`). Rename `AnUpdate_KeepsTheBaseAndEveryColumnItDoesNotOwn` to `AnUpdate_KeepsTheBaseAndTheColumnsTheScanDoesNotChange` and keep its `"secret"` seed as "a reason that is neither the writer's nor a secret mark" (add that comment).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSyncScanner"`. Expected: FAIL (compile error then red).
- [ ] **Step 3: Implement** `ApplySecretPass`: for every id in `snap.Entries` not in `snap.SkippedIds`, take the row the plan will write (the last `plan.Rows` entry for the id, else `snap.Rows[id]` when it is `active`; a `forgotten`/`left` row without a plan row is skipped), compute `SyncSecret.secretKey(entry.View.Content, entry.View.Tags)`, call `precheck`; on `NeedsScan` run `SecretDetector` on the text, project, scope and each tag (a hit is any non-empty result); apply `SetBlock`/`ClearBlock` as `BlockedReason`/`BlockedHash` (`SetBlock` sets both; `ClearBlock` nulls both) and `Allowed` through `afterAllowed`; when the resulting row differs from the one held, replace the plan's row for that id or add it (never two entries for one id); record the id in `blocked` or `unblocked` when the mark newly appears or disappears. `allowed_hash` is never written here.
- [ ] **Step 4: Run** the step 2 filter, then the whole `GitSync` filter. Expected: PASS; existing scanner tests that seed `"secret"`, `bh`, `ah` still pass.
- [ ] **Step 5: Commit** `git commit -m "Stamp the secret block in the git sync scan"` (mention the renamed test and the `ScanSummary` change).

---

### Task 4: The push re-checks and names the reason

**Files:**
- Modify: `src/TotalRecall.Infrastructure/GitSync/GitSyncPusher.Prepare.cs` (`PushPrepared.Secrets`, the preparation), `GitSyncPusher.Plan.cs` (`PlanOne`, `skipped` naming), `GitSyncPusher.cs` (the detector seam, if the summary needs no change nothing else)
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/GitSyncPusherSecretTests.cs` (new); adjust `GitSyncPusherPrepareTests.cs` / `GitSyncPusherPlanTests.cs` expectations only where the secret reason name changes them

**Interfaces:**
- Consumes: Task 1 and Task 2.
- Produces:
  - `internal sealed record SecretCheck(string Key, bool Hit)` and `IReadOnlyDictionary<string, SecretCheck> Secrets` on `PushPrepared` (the check of each candidate whose entry was readable when the preparation read it, before the plan transaction).
  - `internal Func<string?, IReadOnlyList<string>> SecretDetector { get; set; }` on `GitSyncPusher`, default `SensitiveContentScanner.ScanSecrets`.
  - Skip reason string `secret-detected` for an excluded candidate whose row carries that mark (other non-writer reasons keep `blocked-elsewhere`); `PushItem(id, "secret-detected")` in `PushSummary.Blocked` for an entry the push blocked itself.

- [ ] **Step 1: Write the failing tests** (`GitSyncPusherSecretTests`, using the existing push test support): `ANewMemoryWithAToken_IsNotWritten_AndIsListedBlocked` (no record in the commit, other new memories are written, row marked `secret-detected` with the key, `Blocked` holds `secret-detected`); `AnAllowedMemory_IsWritten` (`AllowedHash` = key); `ATextTheApplyChangedToHoldAToken_IsBlocked_AtThePush` (a merged tag union with a token tag: scan stamps nothing, the push blocks it and writes nothing for it); `ABlockedRow_IsSkippedByName` (row already `secret-detected`: `Skipped` reason `secret-detected`; a row with `"secret"` still reads `blocked-elsewhere`); `AnEntryChangedAfterThePreparation_IsDeferredChangedSinceScan` (use the existing `BeforePlanTransaction` seam to edit the text); `ACandidateWithNoPreparedKey_IsDeferred` (entry in two tiers: `unreadable-entry`); `TheDetectorNeverRunsInsideThePlanTransaction` (the `SecretDetector` seam records whether the store is inside a transaction, or counts calls made after `BeforePlanTransaction` fired: zero); `AFinalizeAndACleanPushOfOtherEntries_KeepTheMark` (the row still has `secret-detected` after a push that wrote another entry); `TheWriterNeverClearsASecretMark` (the clearable list and `markAction` leave it; add the F# case in `SyncPushTests.fs` if the C# one cannot reach it); `ALiftedSecretOnATooLargeMemory_IsMarkedTooLargeByTheNextPush` (secret lifted by a clean scan, text still over the limit: the push marks `too-large`).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSyncPusher"`. Expected: FAIL.
- [ ] **Step 3: Implement.** In the preparation, after the candidates are known and before `PrepareResult.Ready`, read each candidate's entry through `LocalEntryReader.Read(t => _store.Get(t, ContentType.Memory, id), () => _store.IsSticky(ContentType.Memory, id))`; for a `Present` entry compute `SyncSecret.secretKey` and the hit (precheck with the row's reason, blocked and allowed hashes; `NeedsScan` runs `SecretDetector` over text, project, scope and each tag; `Allowed` and `StillBlocked` need no scan: `Hit` is false for allowed and true for still-blocked) and store `SecretCheck(key, hit)`. In `PlanOne`, right after the `AgreesWithRow` check: no prepared check gives `deferred` `unreadable-entry` when the re-read is not `Present`, else `changed-since-scan`; a re-read key that differs from the prepared key gives `changed-since-scan`; a prepared `Hit` writes the row with `BlockedReason = secret-detected` and `BlockedHash = key` (through `GitSyncRows.Write` in the transaction), adds `PushItem(id, "secret-detected")` to `blocked`, and returns before any record is built. The pusher never clears or lifts a secret mark. Name the skip reason at the place `skipped` is built from `prepared.Excluded`: when the exclusion is `BlockedElsewhere` and the row's reason is `secret-detected` use that name.
- [ ] **Step 4: Run** the step 2 filter, then the whole `GitSync` filter and the Core `Sync` filter. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Re-check for secrets when the git sync push writes a memory"`.

---

### Task 5: `sync unblock <id>`

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/SyncSetupService.Unblock.cs`, `src/TotalRecall.Cli/Commands/Sync/UnblockCommand.cs`
- Modify: `src/TotalRecall.Core/SyncSetup.fs` (the four refusals and their `refusalName`s `not-blocked`, `unknown-entry`, `unreadable-entry`, `changed-since-blocked`), `src/TotalRecall.Cli/CliApp.cs` (register `new Commands.Sync.UnblockCommand()` after `StatusCommand`)
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/SyncSetupUnblockTests.cs` (new), `tests/TotalRecall.Cli.Tests/Commands/Sync/SyncCommandTests.cs` (add cases), `tests/TotalRecall.Cli.Tests/HiddenCommandTests.cs` (the verb list becomes `["init","join","leave","status","unblock"]` and the phrase regex `sync\s+(init|join|leave|status|unblock)\b`; assert `unblock` is not in top-level help)

**Interfaces:**
- Consumes: Task 1 (`secretKey`, `blockReason`, `afterAllowed`), Task 2.
- Produces: `public Task<SetupResult> UnblockAsync(string entryId, CancellationToken ct = default)` on `SyncSetupService`; `UnblockCommand : SyncCommandBase` with `Name => "unblock"`, usage `Usage: total-recall sync unblock <id>`, the id carried in the `Address` slot of `Parse`'s tuple (exactly one positional argument, no flags; anything else is a usage error, exit 2). The service may take a detector seam for tests (`internal Func<string?, IReadOnlyList<string>> SecretDetector`), default `SensitiveContentScanner.ScanSecrets`.

- [ ] **Step 1: Write the failing tests** (`SyncSetupUnblockTests`, same environment as `SyncSetupLeaveStatusTests`): releases a blocked memory (`AllowedHash` = key, mark cleared, output `unblocked <id>` plus the categories, never the value) and the next pusher run writes it (use the pusher directly; tests may name it); refuses `unknown-entry` for an id with no entry; `unreadable-entry` for an id in two tiers (even with a row); `not-blocked` for no row, for a `forgotten` or `left` row, and for a row whose reason is `too-large` or `"secret"`; `changed-since-blocked` after the text is edited following the block, writing nothing (row byte-for-byte unchanged); a later edit to a tag after the release is blocked again by the next scan; refuses `busy` and writes nothing while another process holds the sync lock; opens the database read-write with migrations (a database at an older schema still works); the output of every refusal and success contains no part of the token. CLI tests: exit 0 on success, 1 on a refusal with `refused: <name>: ...` on stderr, 2 for no argument and for two arguments, `unblock --help` answers, and `unblock` is absent from top-level help.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SyncSetupUnblock"` and `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj --filter "FullyQualifiedName~Sync|FullyQualifiedName~HiddenCommand"`. Expected: FAIL.
- [ ] **Step 3: Implement** `UnblockAsync` as the spec's D7 orders it: take the sync lock like `LeaveAsync` does (refuse `Busy` when it cannot), open the database read-write like `init` does (`LegacyDatabaseProbe.Check`, `SqliteConnection.Open(path, pooling: false)`, `MigrationRunner.RunMigrations`, a borrowed `SqliteStore`), then in one immediate transaction: read the entry (`Absent` gives `UnknownEntry`; `Duplicate` or `Unreadable` give `UnreadableEntry`), then the row (none, not `active`, or a reason other than `secret-detected` gives `NotBlocked`), compute the key (a key different from `blocked_hash` gives `ChangedSinceBlocked`), write the row with `AllowedHash = key`, `BlockedReason = null`, `BlockedHash = null`, commit. Only after the commit run the detector on the content already read and build the output lines (`unblocked <id>` and one line of categories, or a line saying the current content holds no secret). Map `Refusal` text to messages in the spec's words.
- [ ] **Step 4: Run** the step 2 commands, then `GitSyncCycleCallerTests` and the whole `GitSync` filter. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the hidden sync unblock command"`.

---

### Task 6: `sync status` lists blocked memories; two-machine proof; guides

**Files:**
- Modify: `src/TotalRecall.Core/SyncSetup.fs` (`StatusFacts.Blocked` and `renderStatus`), `src/TotalRecall.Infrastructure/GitSync/SyncSetupService.LeaveStatus.cs` (the query), `AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `docs/superpowers/specs/2026-11-15-git-sync-acceptance-criteria.md`
- Test: `tests/TotalRecall.Core.Tests/SyncSetupTests.fs` (rendering), `tests/TotalRecall.Infrastructure.Tests/GitSync/SyncSetupLeaveStatusTests.cs` (the query), `tests/TotalRecall.Infrastructure.Tests/GitSync/TwoMachineTests.cs` (the proof)

**Interfaces:**
- Produces: `StatusFacts` gains a last field `Blocked: (int * (string * string) list) option` (total count and the shown `(id, reason)` pairs, at most 20, ordinal by id; `None` when the table cannot be read); `renderStatus` prints a `blocked:` line with the total and one `  <id>  <reason>` line per shown pair, then `  and N more` when the total exceeds the shown; for `None` it prints `blocked: unknown (...)` like the other unknown facts.

- [ ] **Step 1: Write the failing tests.** Core: `renderStatus` with zero, three, and 25 blocked (20 shown, `and 5 more`), and `None`. Infrastructure status: lists only `active` rows with a non-null reason, ordered by id (a test asserts which 20 of 25 are shown), omits a `forgotten` and a `left` row that kept their reason, tolerates a row with an unknown `state` text, an unmigrated database (no table) gives `None`, creates nothing, takes no lock. `TwoMachineTests.ATokenMemoryOnA_IsInNoObjectOfTheRepository_AndBGetsTheRest`: machine A has a token memory, a `git@github.com` memory and an ordinary one; run A's scan and push; assert over every object in the bare repository (`git cat-file --batch-all-objects --batch`) that the token's bytes never appear, that the token memory is listed blocked, and that B, after its pull, has the other two memories; `ABlockedMemory_StillReceivesARemoteEdit` (B edits a memory that is blocked on A; A's pull applies it, A's push still does not write it while it holds a secret).
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~SyncSetup"` and `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SyncSetupLeaveStatus|FullyQualifiedName~TwoMachine"`. Expected: FAIL.
- [ ] **Step 3: Implement** the field and rendering in Core, and the read-only query on the connection `ReadOnlyDatabase.Open` already gives (`SELECT entry_id, blocked_reason FROM git_sync_entry WHERE state = 'active' AND blocked_reason IS NOT NULL ORDER BY entry_id`, a `COUNT(*)` for the total, guarded for a missing table and never through `GitSyncRows.ReadAll`).
- [ ] **Step 4: Documentation and measurement.** Update the three guides (the scan now writes `blocked_*`/`allowed_hash`; the push re-checks and names `secret-detected`; `sync unblock` is a hidden verb and a release is per machine; the detector is widened; the cost note) and add a "1b-iii part 1" list under the 1b-iii heading of the criteria file from the ticket's criteria. Measure, with a throwaway test that is not committed, the time of one scan pass over 10 000 clean active entries of 2 000 characters each and write the number in the Infrastructure guide.
- [ ] **Step 5: Verify everything.** Run, in order, the profile verify commands of `.github/develop.md` (the controller runs them; the implementer runs `dotnet build src/TotalRecall.sln -c Release`, the Core `Sync` filter, the Infrastructure `GitSync|SensitiveContent|TwoMachine` filter, the Cli `Sync|HiddenCommand` filter and `GitSyncCycleCallerTests`). Expected: all pass, no new warning.
- [ ] **Step 6: Commit** `git commit -m "List blocked memories in sync status and prove the block across two machines"`.
