# Git sync cycle (milestone 1b-i-4) implementation plan

> For agentic workers: implement task by task; steps use checkbox (`- [ ]`) syntax. The spec is
> `docs/superpowers/specs/2026-11-18-git-sync-cycle-design.md` (read-only during delivery); "decision N" and "AC N" refer to
> it. Issue #143, branch `feat/git-sync-cycle`.

**Goal:** `GitSyncCycle.RunAsync(ct)`: take the sync lock, check identity, then scan, fetch, apply and push in order with
bounded retries, a damaged-clone recovery and recording of `git_sync_last_ok` / `git_sync_last_error`, returning one typed
`CycleOutcome`. No caller, command, schema or config change.

**Architecture:** the rules that need no I/O are in `Core/SyncCycle.fs` (F#, pure, total). `Infrastructure/GitSync/GitSyncCycle*.cs`
orchestrates the existing parts (`SyncLock`, `GitSyncScanner`, `GitSyncFetcher`, `GitSyncApplier`, `GitSyncPusher`,
`CloneSideline`, `GitClient`). Three tiny public surface changes: `RepoGuard.ConfiguredRemote`, `GitClient.CloneIsSound`,
`GitSyncFetcher.CheckIdentityAsync`.

**Tech:** .NET 10, C# shell, F# core, xUnit. Tests use real git against a bare remote (`[GitFact]`), the rigs in
`tests/TotalRecall.Infrastructure.Tests/GitSync/` (`PullRig`, `PushRig`, `FaultingGitProcess`, `RecordingGitProcess`).

**Test commands**
- Core: `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`
- Infrastructure: `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~<X>"` (the `NU1903` warning is expected)

**Ground rules for every task**
- No commit trailers, `Co-authored-by` lines or AI attribution anywhere.
- Never edit the spec. A deviation goes in "Interpretations" at the end of this file.
- The cycle never prints to standard output or error except `ExceptionLogger.LogChain` (decision 12). Nothing outside
  `GitSyncCycle*.cs` and its tests may name `GitSyncCycle` (AC 17).
- Windows quirks: tests must tolerate read-only pack files (`GitTestEnvironment.ForceDelete`) and path separators.

---

### Task 1: `Core/SyncCycle.fs`, the pure rules

**Files:** create `src/TotalRecall.Core/SyncCycle.fs`; add `<Compile Include="SyncCycle.fs" />` after `SyncPush.fs` in
`src/TotalRecall.Core/TotalRecall.Core.fsproj`; create `tests/TotalRecall.Core.Tests/SyncCycleTests.fs` (add to that fsproj).

Module `TotalRecall.Core.SyncCycle` (all total, no I/O, no clock):

- `type Step = Lock | Identity | Locks | Scan | Clone | Fetch | Apply | Push | Record` and `stepName : Step -> string`
  (lowercase: `lock`, `identity`, `locks`, `scan`, `clone`, `fetch`, `apply`, `push`, `record`).
- `maxAttempts = 3`.
- `type PushKind = Pushed | NothingToPush | NotReady | NonFastForward | PushFailed` and
  `type Next = Finish | Fail | Retry`; `nextAfterPush (attempt: int) (kind: PushKind) : Next`: `Pushed` and `NothingToPush`
  give `Finish`; `NotReady` and `PushFailed` give `Fail`; `NonFastForward` gives `Retry` while `attempt < maxAttempts`, else
  `Fail` (`attempt` is 1-based: the number of pushes made so far including this one). An `attempt` of 0 or less is treated as 1.
- `type PushGate = Run | Skip of reason: string` and `pushDecision (fetchWentOn: bool) (applyDone: bool) (complete: bool)`:
  `Run` only when all three are true; else `Skip` with `"pull-incomplete"` when the fetch went on and the apply was `Done`
  but not complete, and `"pull-not-done"` otherwise (the cycle never reports the second as a skip: it ends the cycle
  earlier; the word exists so the function is total).
- `type Probe = Healthy | NoCommit | ProbeFailed of SyncGit.FailureKind` and
  `cloneDamage (kind: SyncGit.FailureKind) (probe: Probe) : bool`: true for `NotARepository` (probe ignored); for `Other`
  true when the probe is `NoCommit`, or `ProbeFailed k` with `k` not in `Cancelled | TimedOut | OutputTooLarge`; false for
  `Other` with `Healthy` or an inconclusive probe. **`RemoteNotFound` is treated exactly like `Other`** (see Interpretations:
  the fetcher's first clone-scoped call is `ls-remote -- origin`, which on an invalid `.git` runs outside a repository and says
  "'origin' does not appear to be a git repository", which the classifier maps to `RemoteNotFound`); false for every other kind.
- `parseKind (name: string) : SyncGit.FailureKind`: case-sensitive, by the union's case name (`NoGit`, `GitTooOld`, `TimedOut`,
  `Cancelled`, `Auth`, `RemoteNotFound`, `Network`, `NonFastForward`, `RemoteBranchMissing`, `NotARepository`,
  `OutputTooLarge`, `Other`); null, empty, an unknown name and the pusher's `"Clone"` give `Other`. (`FailureKind` is an F#
  union, not an enum: there is no `Enum.TryParse`.)
- `okValue (now: DateTimeOffset) : string`: UTC `yyyy-MM-ddTHH:mm:ss.fffZ`, invariant culture.
- `errorLine (now) (step: string) (kind: string) (message: string) : string`: `"<okValue now> <step> <kind>: <message>"`
  with control characters (`Char.IsControl`) and U+2028 / U+2029 in the message replaced by one space each, the whole line cut
  to 500 UTF-16 units without splitting a surrogate pair (cut before a high surrogate that would be left alone), an empty
  message giving `"<time> <step> <kind>:"` (no trailing space), `null` message treated as empty.

- [ ] **Step 1: tests first** (`SyncCycleTests.fs`): `nextAfterPush` for every attempt 0..4 and every kind; `pushDecision`
  all 8 combinations; `cloneDamage` for every `FailureKind` (use a list of all cases; `RemoteNotFound` behaves like `Other`) with `Healthy`, `NoCommit` and each
  `ProbeFailed k`; `parseKind` for every case name, `"Clone"`, `""`, null, a wrong-case name; `okValue` fixed instants and a non-UTC offset input; `errorLine` for a newline, a tab, U+2028, U+2029, a
  600-character message (total length exactly 500, prefix intact), a message with a surrogate pair straddling the cut, an
  empty message, a null message, and a round trip: the first 24 characters parse back to the instant; FsCheck-style
  totality: a loop over generated strings (reuse the style of `SyncPushTests.fs`) never throws and never yields a control
  character or a line longer than 500. Run: expect compile failure.
- [ ] **Step 2: implement** until green. Run the Core tests (544 + new).
- [ ] **Step 3: commit** `Add the pure rules of the git sync cycle`.

### Task 2: three small public surfaces

**Files:** `src/TotalRecall.Infrastructure/GitSync/RepoGuard.Public.cs`, `GitClient.cs`, `GitSyncFetcher.cs`; tests in
`RepoGuardIdentityTests.cs`, `GitClientSetupTests.cs`, `GitSyncFetcherTests.cs`.

- `RepoGuard`: make `ConfiguredRemote` a public `string?` property (doc comment: "the remote the guard was built with; the
  cycle compares its own address with it").
- `GitClient`: make `CloneIsSound()` public (doc: "true when the clone directory, its parent and its `.git` are real
  directories, not links; the cycle uses it to tell a missing or damaged clone from a usable one"). No behaviour change.
- `GitSyncFetcher`: new `public abstract record IdentityCheck` with nested `Joined`, `NotJoined(string Text)`,
  `Refused(string Text)`; new `public async Task<IdentityCheck> CheckIdentityAsync(CancellationToken ct = default)` that runs
  `_guard.EvaluateIdentityAsync(null, ct)` and maps: `JoinState.NotJoined` with reason `NeverJoined` to `NotJoined(reasonText)`;
  every other `NotJoined` reason to `Refused(reasonText)`; `HostUnverified` to `Refused("host identity could not be determined")`;
  else `Joined`. The fetcher's own first step (`FetchCoreAsync` step 2) calls it and maps both `NotJoined` and `Refused` to its
  existing `FetchOutcome.Refused(text)`, so every existing fetcher test keeps its text and behaviour. `NotConfigured` is a
  `Refused` (the cycle's constructor makes it unreachable).

- [ ] **Step 1: tests** (failing first): `CheckIdentityAsync` for a never-joined machine (`NotJoined`), a changed remote, a
  moved data directory, an unverified host (each `Refused`, text unchanged), a joined machine (`Joined`), and no network
  command recorded in any case; `CloneIsSound` true for a real clone, false for a missing directory, a directory without
  `.git`, and a symlinked `.git` (skip the symlink case when the platform refuses to create one); `ConfiguredRemote` equals the
  configured text and is null with none.
- [ ] **Step 2: implement**; run the fetcher, guard and client test classes and the Core tests: all existing tests unchanged.
- [ ] **Step 3: commit** `Expose the pieces the git sync cycle needs`.

### Task 3: `PullRig` and `PushRig` can configure a real remote

**Files:** `tests/TotalRecall.Infrastructure.Tests/GitSync/PullTestSupport.cs`, `PushTestSupport.cs`.

- `PullRig` gets an optional constructor argument `remoteText` (default the existing constant, which stays public as
  `RemoteText` for the existing tests); an instance property `Remote` holds the text actually used by `Paths`, the guard,
  `Join` and `NewGuard`. All existing tests keep passing with the default.
- `PushRig` gets a constructor option `configuredRemote: true`, which creates the bare remote first and gives the
  `PullRig` the remote's `Url` as its text, so a clone made by the cycle (`CloneAsync(Address)`) really clones the bare remote
  and the guard, paths and address agree. The default stays as it is.
- `CycleRig` (new `CycleTestSupport.cs`, created in Task 4 because it needs the cycle to compile; Task 3 only changes
  `PullRig` and `PushRig`) wraps a `PushRig` made with `configuredRemote: true`: `Cycle(settings?)` builds a `GitSyncCycle` with the rig's
  client, guard, store, `CountingEmbedder`, paths, `CycleSettings(Address: rig.Pull.Remote, Branch: "main", MachineLabel:
  "testbox", HotMaxContentChars: 0, LockWait: TimeSpan.Zero)` and two fixed clocks; `Steps` collects `OnStep` names in
  order; helpers `Run()` (a cycle then `RunAsync`), `LastOk` / `LastError` (the `_meta` values), `Calls(kind)` (recorded
  command counts), `HoldLock()` (opens `SyncLock.TryAcquireAsync(Paths.LockPath, Zero)` and returns the handle),
  `PlantIndexLock()`, `DeleteClone()` (`GitTestEnvironment.ForceDelete` on the clone directory), `CorruptClone()` (overwrite
  `.git/HEAD` with garbage and delete `.git/objects` content so `rev-parse HEAD` fails), `SidelinedClones()` (directories
  matching `repo.broken-*` / `repo.partial-*` beside the clone).
- [ ] **Step 1:** change `PullRig` and `PushRig` as above. Run the whole `GitSync` test namespace: green and unchanged.
- [ ] **Step 2: commit** `Let the sync test rigs configure a real remote`.

### Task 4: `GitSyncCycle` skeleton: shape, lock, identity, scan, recording

**Files:** create `src/TotalRecall.Infrastructure/GitSync/GitSyncCycle.cs` (types, constructor, `RunAsync`, step wrapper,
recording) and `GitSyncCycleTests.cs` (+ `CycleTestSupport.cs` finished).

Types (decisions 1, 2): `CycleSettings(string Address, string Branch, string MachineLabel, int HotMaxContentChars, TimeSpan
LockWait)`; `CycleSummary` (`Attempts`, `ScanSummary Scan`, `FetchResult? Fetch`, `ApplySummary? Apply`, `PushOutcome? Push`,
`string? PushSkipped`, `bool Cloned`, `string? SidelinedClone`); `CycleOutcome` = `Done(CycleSummary)`, `NotJoined`,
`Refused(string Reason, string Step)` (the step that refused: `identity`, `fetch` or `clone`), `Busy`, `Failed(string Step, string Kind, string Message, SyncPush.Published Published)`,
`Cancelled` (private constructor, nested sealed records as in `FetchOutcome`).

`GitSyncCycle(GitClient client, RepoGuard guard, SqliteStore store, IEmbedder embedder, GitSyncPaths paths, CycleSettings
settings, Func<DateTimeOffset> clock, Func<long> nowMs)`; constructor throws `ArgumentNullException` for a null argument,
`ArgumentException` when `Address` is null or empty, when it is not ordinally equal to `guard.ConfiguredRemote`, or when
`paths.CloneDirectory` differs from `client.CloneDirectory` (compare full paths, ordinal ignore-case on Windows;
`paths.CloneDirectory` throws `InvalidOperationException` for paths made by `ForDatabase`: the constructor catches it and
rethrows an `ArgumentException`). Internal
seams: `Action<string>? OnStep`, `Action<string, string?>? BeforeMetaWrite`.

`RunAsync(ct)` in this task: step 0 (the stored `CloneUnusable` failure; the field and its small in-process lock exist now
and are set in Task 5), cancellation check, `OnStep("lock")`, `SyncLock.TryAcquireAsync(paths.LockPath, LockWait, ct)`
(`OperationCanceledException` gives `Cancelled`; `null` gives `Busy`; any other exception gives `Failed(lock, Unexpected, ...)`
recorded without the lock), then under the handle: `OnStep("identity")` and `fetcher.CheckIdentityAsync` (`NotJoined` gives
`NotJoined`, nothing recorded; `Refused` gives `Refused`, recorded), `OnStep("locks")` and `client.RemoveStaleLocks()`,
`OnStep("scan")` and `new GitSyncScanner(store.Connection, nowMs).Scan()` (`NotJoined` gives `NotJoined`; `Failed(reason)`
gives `Failed(scan, Store, reason, No)`), then the attempt body, which this task leaves as a call to a private
`RunAttemptAsync` returning `Done` with the scan summary and nothing else (Tasks 5 and 6 fill it in). Every step runs inside
one try/catch in a private `StepAsync(step, ...)` wrapper: `OperationCanceledException` gives `Cancelled`; any other exception
except `OutOfMemoryException` is logged with `ExceptionLogger.LogChain("[total-recall] sync cycle", ex)` and gives
`Failed(step, Unexpected, ex.Message, published)` where `published` is `Unknown` for `push` and `No` otherwise. The token is
checked after each part returns, before the result is used (decision 5): a cancelled token gives `Cancelled`; **for the push
this check is skipped once the push command has succeeded** (decision 11: the pusher finalizes and returns `Pushed`, which is `Done`). `OnStep` is
called inside the step's try, so a throwing `OnStep` is an unexpected exception of that step.

Recording (decision 10), `Record(outcome)` as the last act under the lock; `OnStep("record")` fires first, **only when
something is written** (`Done`, `Failed`, `Refused`), and a throwing `OnStep` there is swallowed like a failed write: `Done` deletes `last_error`, then sets `last_ok = SyncCycle.okValue(clock())`; `Failed` and `Refused`
set `last_error = SyncCycle.errorLine(clock(), step, kind, message)` (`Refused` uses its own `Step`; kind `Refused`); `Busy`, `NotJoined`, `Cancelled` write nothing. Every write goes through
`BeforeMetaWrite?.Invoke(key, value)` and is inside a try/catch that swallows `Exception` except `OutOfMemoryException`.

- [ ] **Step 1: tests** (CycleRig, `[GitFact]`): constructor throws for an empty address, a different address, a different
  clone directory; a never-joined machine is `NotJoined`, records nothing and runs no scan (no scanner write: the `OnStep`
  list stops at `identity`); each early refusal (remote changed, data directory moved, host unverified) is `Refused`, no
  stamp written (store rows untouched), no network command recorded, `last_error` set to a line starting with a UTC time and
  `identity Refused:`; a held lock gives `Busy` after `LockWait`, nothing run, nothing written, a second cycle of the same
  process while the first holds the lock is `Busy`, and the lock is released after every outcome (a new `HoldLock()` succeeds);
  the scan's own `NotJoined` (remote and repo id stored, no machine id) is `NotJoined`; an exception thrown by `OnStep` at
  `scan` is `Failed(scan, Unexpected)` with the lock released and `last_error` recorded; a failing meta write
  (`BeforeMetaWrite` throws) is swallowed and the outcome unchanged; the write order of a `Done` (`BeforeMetaWrite` log:
  delete `last_error`, then set `last_ok`); `Failed(lock)`: with `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` set (the test class
  joins `ProcessEnvironmentCollection` and restores the variable) the outcome is `Failed(lock, Unexpected, ..., No)`, recorded
  without a lock and no other step runs; a lock wait cancelled mid-wait (`LockWait` 5 s, a held lock, the token cancelled after
  100 ms) is `Cancelled`, nothing recorded; `Failed(scan, Store)` (a store failure the scanner reports as
  `ScanOutcome.Failed`, for example a dropped `git_sync_entry` table), with `last_error` naming `scan`; a cancelled token before the lock is `Cancelled` and
  records nothing; a stale `index.lock` planted before the call is gone by the time `scan` starts.
- [ ] **Step 2: implement** (with `CycleTestSupport.cs`), run the new tests and the whole `GitSync` namespace.
- [ ] **Step 3: commit** `Add the git sync cycle: lock, identity, scan and recording`.

### Task 5: the clone, the fetch, the apply and the push gate

**Files:** create `GitSyncCycle.Clone.cs` and `GitSyncCycle.Pull.cs` (partial class); tests `GitSyncCycleCloneTests.cs`,
`GitSyncCyclePullTests.cs`.

`EnsureCloneAsync` (decision 8), run as step `clone`, called after the scan of every attempt:

- `client.CloneIsSound()` true: nothing to do (no `OnStep("clone")`).
- directory missing: `OnStep("clone")`, `CloneAsync(Address, Branch, ct)`.
- directory present but not sound: `OnStep("clone")`, `CloneSideline.Move(dir, "broken", clock())`, count the damaged clone
  after the move succeeded (a move that throws is `Failed(clone, Unexpected, ...)`, nothing counted), then clone. The
  second damaged clone in the process gives `Failed(clone, CloneUnusable, message)` **and stores it**; every later `RunAsync`
  returns the stored failure at step 0 (no lock, no read, no write). The count and the stored failure are guarded by a private
  lock object. The sidelined path is kept for `CycleSummary.SidelinedClone`.
- a clone that fails or is cancelled: `CloneAsync` failure kinds `RemoteBranchMissing` give `Refused("the remote has no
  branch ...")`; `Cancelled`, or a cancelled token, gives `Cancelled`; any other kind is `Failed(clone, kind, text, No)`. In every
  failing case the partial directory the call left is removed with a best-effort recursive delete (read-only attributes
  cleared by an enumeration that skips links, `EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint,
  RecurseSubdirectories = true }`, as `RemoveStaleLocks` does, so a link is never followed; never cancelled; only when the
  directory did not exist before the call or was just moved aside), and when the delete
  fails it is moved aside with `CloneSideline.Move(dir, "partial", clock())`. The whole cleanup is wrapped in a try/catch
  that swallows non-`OutOfMemoryException` exceptions and keeps the original outcome (`Cancelled` or `Failed(clone, kind)`); a test
  makes both seams (`DeleteDirectory`, `MoveDirectory`) throw. Neither case counts as damage.
- after a successful clone, `Cloned = true`.

**Kind mapping.** `FetchOutcome.Failed.Kind` and `PushOutcome.Failed.Kind` are strings; the cycle maps them to
`SyncGit.FailureKind` with `SyncCycle.parseKind` (Task 1; an unknown value and the pusher's `"Clone"` map to `Other`) wherever `SyncCycle.cloneDamage` or the `Cancelled` check needs the enum. The kind **string** put into
`CycleOutcome.Failed` is the part's own string, unchanged.

`FetchStepAsync`: `OnStep("fetch")`, `fetcher.FetchAsync(ct)`; `Fetched` and `NothingNew` go on; `Refused(reason)` gives
`Refused(reason)`; `Failed(kind, message)` with kind `Cancelled`, or a cancelled token, gives `Cancelled`; damage (decision 8:
kind `NotARepository`, or kind `Other` or `RemoteNotFound` with a failing probe from `client.RevParseAsync(Revision.Head)` classified into
`SyncCycle.Probe`) is recovered as `EnsureClone` does for a damaged directory (sideline, count, clone) and the attempt restarts
from the scan without counting an attempt; any other kind gives `Failed(fetch, kind, message, No)`.

`ApplyStepAsync`: `OnStep("apply")`, `new GitSyncApplier(client, store, embedder, new ApplyOptions(HotMaxContentChars),
nowMs).ApplyAsync(fetchResult, scan, ct)`; the token decides `Cancelled` first; `ApplyOutcome.Failed(reason)` gives
`Failed(apply, Apply, reason, No)`. The gate is `SyncCycle.pushDecision`; `Skip` ends the cycle `Done` with `Push = null`,
`PushSkipped = reason`.

- [ ] **Step 1: tests** (CycleRig): a lost clone (directory deleted; remote head equal to `L` and not equal to `L`): re-clone,
  `Cloned`, the apply is unproven (a record that vanished from the remote is not deleted locally), nothing deleted from the
  store, pending local changes reach the remote when Task 6 lands (in this task assert the pull side only and that the
  push step is still the stub); a corrupt clone (`CorruptClone`; the test also asserts the failure kind the fetcher really reports first, expected
  `RemoteNotFound` from the `ls-remote` call, and fails loudly if git reports another kind that `cloneDamage` does not cover):
  the old directory is renamed to `repo.broken-<utc>` with its
  content kept, a new clone exists, `SidelinedClone` set; a network failure with a sound clone (clone untouched, never moved,
  `Failed(fetch, Network)`); the probe cases (`Other` with a clone `HEAD` that resolves is not damage; `Other` with a `HEAD`
  that does not resolve is damage; a probe that times out or is cancelled is never damage), using `Fault` on the fetch and the
  `rev-parse HEAD`; a second damaged clone in one process is `Failed(clone, CloneUnusable)`, and every later `RunAsync` on that
  instance returns the same failure with no lock taken, no command recorded and no `last_error` rewrite (`BeforeMetaWrite`
  not called), while a new instance starts clean; a lost clone followed by a damaged one (the damage is the first counted); a
  clone killed by a timeout and by a cancel (a canned `Fault` result runs no git, so the fault lambda itself creates a
  partial `repo` directory with a read-only file and, for the cancel case, cancels the token): the partial directory is deleted, or is moved aside as `repo.partial-*`
  when it cannot be deleted (force it by making the delete seam throw: an internal `Action<string>? DeleteDirectory` seam), is
  never counted (two cancelled clones in a row never give `CloneUnusable`), and a later cycle succeeds; a `RemoteBranchMissing`
  at clone is `Refused`; a failed move (`CloneSideline.Move` seam: internal `Action<string,string>? MoveDirectory`) is `Failed(clone,
  Unexpected)` with nothing counted and the next cycle trying again; a refused fetch (replaced repository at the fetched head,
  newer format, missing branch) is `Refused` after the scan with nothing applied and the push command never recorded, the
  refusal recorded (`last_error` starts with a UTC time and `fetch Refused:`, `last_ok` unchanged, the lock released); a failed
  fetch (kinds `Auth`, `Network`: `Failed(fetch, kind, ..., No)`, `last_error` names `fetch`, a later `Done` clears it) and a
  failed apply (`Failed(apply, Apply, reason, No)`, forced by a store failure the applier reports as `ApplyOutcome.Failed`) never
  reach the push (no push command recorded); a plain failed clone (`Failed(clone, Network, ..., No)`, retried by the next cycle,
  `CloneUnusable` not set); an incomplete pull (a deletion the pull postpones:
  a record removed on the remote for a row this machine holds with a base) is `Done`, `PushSkipped = "pull-incomplete"`,
  `last_ok` written; entry-level apply failures (an unreadable record the pull cannot take) do not stop the cycle (`Done`).
- [ ] **Step 2: implement**; run the new tests and the `GitSync` namespace.
- [ ] **Step 3: commit** `Add the clone recovery and the pull to the git sync cycle`.

### Task 6: the push step, the retry and the ordering

**Files:** create `GitSyncCycle.Push.cs`, `GitSyncCyclePushTests.cs`; extend `RunAttemptAsync`.

`PushStepAsync`: `OnStep("push")`, `new GitSyncPusher(client, store, Branch, MachineLabel).PushAsync(apply, scan, ct)`. Mapping
(decision 6): `Pushed` and `NothingToPush` give `Done`; `NotReady(reason)` gives `Failed(push, NotReady, reason, No)`;
`Failed(kind, message, published)` gives `Failed(push, kind, message, published)` (kind `Clone` is mapped to `Other` for
`SyncCycle.cloneDamage` when the clone probe also fails: damage, recovered as in Task 5, restart from the scan, no attempt
counted); kind `Cancelled` gives `Cancelled` unless the push command already succeeded (the pusher then finalizes and returns
`Pushed`, so `Done`); `NonFastForward` goes through `SyncCycle.nextAfterPush`: `Retry` restarts the attempt from the scan
(lock kept, `Attempts` increments), `Fail` ends `Failed(push, NonFastForward, ..., No)`. `Attempts` counts pushes made.
An unexpected exception at this step is `published = Unknown` (the wrapper of Task 4).
`CycleSummary` is filled from the last attempt.

- [ ] **Step 1: tests** (CycleRig; AC 1, 6 to 9, 13): one cycle that pushes a new and an edited memory and applies another
  machine's change (`Done`, the other change in the store, one commit with this machine's changes on the remote, no pending
  stamps, `last_ok` set, `last_error` absent, `Attempts == 1`, `OnStep` order `lock identity locks scan fetch apply push record`
  with `clone` only after a lost clone); a cycle with nothing to do (`Done`, no commit and no push command recorded); a stale
  `index.lock` removed before the fetch and before any writing command (the recorded commands show no `reset`, `add`, `commit`
  or `push` before the lock file is gone; the guard's read-only reads may run first); a non-fast-forward once (the `Fault` lambda, on seeing the push command, calls `MoveRemote` with a distinct file name per
  call and returns null, so the real push runs and is rejected; the second attempt succeeds: `Attempts == 2`, the remote holds
  both machines' work) and three in a row (the lambda moves the remote on each of three pushes) (`Failed(push, NonFastForward, No)`, everything still pending, the next cycle succeeds and `last_error` is
  cleared); a failure of each kind at the push (`Auth`, `Network`, `TimedOut`, `Other`) with the right `published` and a
  `last_error` line naming `push`; the push never reached after a `Refused`, `Failed` or incomplete pull; `NotReady` as
  `Failed(push, NotReady)`; a push reset failing with kind `Clone` plus a failing probe: the clone is sidelined and the next
  attempt recovers; an exception thrown at `push` (`OnStep`) is `Failed(push, Unexpected, Unknown)`; a cancel before each
  step (`OnStep` cancels the token) is `Cancelled`, records nothing, releases the lock and leaves rows and clone consistent
  (a following clean cycle succeeds); a cancel in the middle of the fetch, the apply (an incomplete `Done`, a failed tree read
  and a failed ref write under a cancelled token: all `Cancelled`) and the push; `NotReady` (the `OnStep("push")` hook changes `_meta` `git_sync_last_sha` so it no longer equals the pull's head);
  AC 10 and AC 11 end to end: a lost clone (remote head equal to `L`, and not) and a corrupt-clone sideline, each followed
  by the pending new and edited memories landing on the remote in the same cycle (`Done`, `Cloned` or `SidelinedClone` set,
  nothing deleted from the store); entry-level apply failures (an entry with an `apply-failed` issue: the other entries are
  pushed, the cycle is `Done`); a cancel after the push command succeeded
  (the hook is the existing `rig.Pull.After` of `PullRig`, set to cancel the token when the command is the push and to return its result unchanged; no production seam) is `Done` with `last_ok` written; a later `Done` clears `last_error`.
- [ ] **Step 2: implement**; run the new tests and the `GitSync` namespace.
- [ ] **Step 3: commit** `Run the push in the git sync cycle with bounded retries`.

### Task 7: guides, the caller check, and the full verify

**Files:** `AGENTS.md`, `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`; test
`GitSyncCycleCallerTests.cs` (or one more test in an existing class).

- [ ] **Step 1:** update the three guides: the "WHERE TO LOOK" table (cycle, `SyncCycle.fs`, `CheckIdentityAsync`, the cycle
  recording keys), the layer description (what the cycle owns, that it has no caller until 1b-iii, the once-per-process
  `CloneUnusable` state, `last_ok` written for an incomplete pull, `last_error` line format), the Core file list, and
  the Infrastructure `GitSync` section; milestone wording stays accurate ("1b-i-4 cycle: done; 1b-ii and 1b-iii next").
  Mention that `GitSyncPusher` is called only by the cycle and its tests.
- [ ] **Step 2:** a repo-scan test in the style of `StaleLineageReferenceTests` (`FindRepoRoot`, `EnumerateFiles` over
  `src/**/*.cs` and `src/**/*.fs`, skipping `bin` and `obj`; no `git grep`, no markdown): every code file that names
  `GitSyncCycle` is a `GitSyncCycle*.cs` file, and every code file outside the pusher's own files (`GitSyncPusher*.cs`,
  `SyncPush.fs`) that names `GitSyncPusher` is a `GitSyncCycle*.cs` file.
- [ ] **Step 3: full verify** from the worktree: `npm ci --ignore-scripts` (remove the `node_modules` symlink first), fetch the
  bge model if missing, `dotnet test src/TotalRecall.sln --filter "Category!=Integration"`, `npm run test:shim`, the Host
  Release build plus `dotnet run --project src/TotalRecall.Host -- dump-catalog | diff catalog.json -`, `node
  scripts/host-docs.test.js`, and a linux-x64 NativeAOT publish with no new warning.
- [ ] **Step 4: commit** `Document the git sync cycle`.

## Interpretations

- `SyncCycle.PushKind.Failed` and `SyncCycle.Probe.Failed` of the spec are named `PushFailed` and `ProbeFailed`, so the case
  names do not shadow `Failed` of other unions opened in the same files.
- `CycleOutcome.Refused` carries the refusing step (`Refused(Reason, Step)`) so the recording can name it; the spec's
  `Refused(reason)` is otherwise unchanged.
- `OnStep("record")` fires only when something is written.
- `cloneDamage` also treats `RemoteNotFound` like `Other` (a local probe decides). The spec lists `NotARepository` and `Other`;
  a corrupt `.git` makes the fetcher's `ls-remote -- origin` report `RemoteNotFound`, so without this AC 11 could not hold. A
  healthy clone whose remote really is gone still probes `Healthy` and is never moved aside.
- The AC 17 check is a source scan over code files, not `git grep`, so it does not depend on `git` and does not count the
  contributor guides.
