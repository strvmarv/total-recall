# Git sync, milestone 1b-i-4: the cycle

Date: 2026-11-18. Status: draft for review. Part of #93 (git sync). The umbrella's milestone 1b-i ("cycle core") was split
into the scan (1b-i-1, merged), the pull (1b-i-2, merged), the push (1b-i-3, merged) and **the cycle** (this document), which
runs the three under the lock. The umbrella design is `2026-11-15-git-sync-design.md` (the sections "The sync cycle", "Failure
handling" and "Recovery guarantees"); the parts' designs are `2026-11-17-git-sync-scan-design.md`,
`2026-11-17-git-sync-pull-design.md` and `2026-11-17-git-sync-push-design.md`. Where this document is more precise it wins for
this milestone, and every departure is listed under "Departures from the umbrella design".

## Goal

Give git sync its **cycle**: one function that takes the sync lock, checks that this machine may sync, and then runs the
four steps in the umbrella's order, **scan, fetch, apply, push**, returning one typed result. It is the piece a later
command, a timer or a session hook will call; it never prints anything and never needs a person.

Success: a machine with local changes and a repository that others also changed ends one cycle with the other machines'
changes in the store, its own changes in the repository and the sync bookkeeping saying so; a cycle never pushes after a
pull that was refused, failed or incomplete; another machine winning the race for the branch costs a bounded number of
retries and nothing else; a damaged or missing local copy of the repository is replaced without ever deleting the
damaged one and without ever deleting a memory; every failure is reported as data and recorded, and the next cycle
recovers.

## Non-goals

Left to later milestones and **not** built here:

- Any caller: the hidden `sync now` command and the `status` block (1b-iii), the timer, `session_start` and `session_end`
  integration, the wait budget, progress counts and notifications (milestones 2 and 3). Like the scan, the pull and the push,
  the cycle has no command and no host integration. **Until 1b-iii (the secret scan) has merged nothing may call it**,
  because it calls the push.
- Deletions, held deletions, the delete guard (1b-ii); pin and conflict review, secrets, aliases (1b-iii). A pull that holds
  one of them is incomplete (pull design), so the cycle does not push on such a machine; that is intended until those parts
  land.
- Reading the configuration. The cycle receives its settings as values (address, branch, machine label, content limit, lock
  wait); the caller that reads `[git_sync]` and the user config is a later part.
- `sync init` and `sync join` running a first cycle (milestone 4 and the verbs' wiring in 1b-iii).
- Anything the user sees: no standard output, no standard error text except the exception log line of decision 12.

## What exists and what is new

Built already: the lock (`SyncLock.TryAcquireAsync`, an OS file lock outside the clone), `GitSyncPaths` (clone and lock
paths), the git client (clone, ls-remote, fetch, reset, rev-parse, `RemoveStaleLocks`, the sound-clone check, the
protective ref), `RepoGuard` (identity and manifest checks), `CloneSideline.Move` (rename, never delete), the scan
(`GitSyncScanner`), the pull (`GitSyncFetcher`, `GitSyncApplier`) and the push (`GitSyncPusher`), each returning a typed
outcome, and the `_meta` keys `git_sync_last_ok` and `git_sync_last_error` (reserved, cleared by `leave`/`join` for a
different repository, written by nothing yet). New:

- `Core.SyncCycle` (F#, pure): the retry decision, the push gate, the clone-damage classification, the text of the two
  `_meta` values.
- `Infrastructure.GitSync.GitSyncCycle`: the orchestration over the existing parts, the clone recovery and the recording.
- One small public method on the fetcher, `CheckIdentityAsync`, so the cycle can refuse before the scan (decision 3).
- `GitClient`'s sound-clone check (private today) becomes public, so the cycle can tell a missing or damaged clone from a usable one (decision 8).

## Decisions

### Shape and result

1. **`GitSyncCycle`** is built with the git client, the repository guard, the store, the embedder, the paths, the settings
   (`CycleSettings(Address, Branch, MachineLabel, HotMaxContentChars, LockWait)`) and a clock. One instance is meant to
   live as long as the process (decision 9 keeps a per-process state). `RunAsync(ct)` returns a `CycleOutcome` and never
   throws for the state of the store, the repository, the file system or the network.
2. **`CycleOutcome`** is one of: `Done(CycleSummary)`; `NotJoined` (the scan says this machine has no sync identity);
   `Refused(reason)` (the guard or the remote says this machine must not sync: not joined, data directory moved or copied,
   remote or repository changed, newer repository format, no such branch); `Busy` (another process or task holds the lock);
   `Failed(step, kind, message, published)`; `Cancelled`. `step` is one of `lock`, `identity`, `scan`, `clone`, `fetch`,
   `apply`, `push`; `kind` is the git failure kind name where there is one, else a word of the failing part (`Busy`, `Store`,
   `Apply`, `NotReady`, `NonFastForward`, `CloneUnusable`, `Unexpected`); `published` is the push's own (`No`, `Yes`,
   `Unknown`) and `No` for every other failure. `CycleSummary` is plain data: `Attempts`, the `ScanSummary`, `FetchResult`
   and `ApplySummary` of the last attempt, the push's `PushOutcome` or null with `PushSkipped = "pull-incomplete"`,
   `Cloned` (a clone was made) and `SidelinedClone` (the path a damaged clone was moved to, else null).

### The order

3. **Under the lock, in this order**: (a) take the lock, waiting at most `LockWait` (`Busy` when it stays held; a failure to
   take it other than "held" is `Failed(lock, ...)`); (b) the identity half of the guard (no clone, no network:
   `GitSyncFetcher.CheckIdentityAsync`), `Refused` when it refuses, **before anything is stamped**, because a data
   directory that was copied to another machine must not issue stamps with the first machine's id; (c) remove stale
   `*.lock` files of the clone's `.git` (`RemoveStaleLocks`, which does nothing when the clone is not sound); (d) the scan
   (`NotJoined` and a scan failure end the cycle: `Failed(scan, Store, ...)`); (e) make sure the clone is usable (decision
   8); (f) the fetch; (g) the apply; (h) the push gate; (i) the push. A step starts only when the earlier ones succeeded.
   The lock is held until the outcome is recorded (decision 10) and released on every path, including a thrown exception.
   Two cycles in one process exclude each other through the same lock (the second is `Busy`).
4. **The scan and the fetch are not repeated inside a step**, but the whole of (d) to (i) is one *attempt*, and the cycle
   may run attempts (decision 6). `OnStep(string)`, a test seam that is also the hook the later progress work will use, is
   called with `lock`, `identity`, `locks`, `scan`, `clone` (only when a clone is made or moved), `fetch`, `apply`,
   `push` and `record` as each begins.

### The pull and the push gate

5. **The fetch** maps `Fetched` and `NothingNew` to "go on", `Refused(reason)` to `Refused`, `Failed(kind, message)` to
   `Failed(fetch, kind, ...)` (a cancelled fetch is `Cancelled`, a clone failure goes to decision 8). **The apply** runs
   after both a fetch and a "nothing new" (it retries deferred and failed entries); `ApplyOutcome.Failed(reason)` is
   `Failed(apply, Apply, reason)`. **The push gate** (`SyncCycle.pushDecision`, pure): the push runs only when the fetch
   went on **and** the apply returned `Done` **and** `Complete` is true. An incomplete pull (a held deletion, a review, a
   re-push, a deferral, a postponement) is not an error: the cycle ends `Done` with the push not run and
   `PushSkipped = "pull-incomplete"`. A `Refused`, a `Failed` or a `Cancelled` never reaches the push.
6. **After the push.** `Pushed` and `NothingToPush` end the cycle `Done`. `NotReady` (a complete pull whose last synced
   commit is not the pull's head: not expected) is `Failed(push, NotReady, reason)`. `Failed(kind, message, published)` is
   `Failed(push, kind, message, published)`. `NonFastForward` (another machine pushed first; nothing was published and
   every change is still pending) starts **another attempt from the scan** (decision 4's (d)): **at most three pushes per
   cycle** (`SyncCycle.nextAfterPush attempt max outcome`, pure); the third rejection ends the cycle
   `Failed(push, NonFastForward, ..., No)` with everything pending, and the next cycle tries again. The lock stays held
   between attempts. A cancellation between attempts is `Cancelled`.

### The clone

7. **Stale lock files** are removed once per cycle under the lock (decision 3), so a git process killed in an earlier run
   (an `index.lock`) cannot fail the reset of every push.
8. **A missing or damaged clone is replaced, never deleted.** Step (e) checks the clone with the client's sound-clone
   check. Missing: clone the configured address and branch (`CloneAsync`; no sideline, nothing to keep). A directory that is
   not a sound clone: move it aside with `CloneSideline.Move(clone, "broken", now)` (the rename works while the lock, which
   is outside the clone, is held), then clone. After a fresh clone the cycle goes on to the fetch, which finds `L` (the
   history is all there) but no protective ref, so the apply is **unproven**: it deletes nothing and a record that vanished
   is left for 1b-ii. **Damage found later**: a failed fetch of kind `NotARepository`, or of kind `Other` followed by a
   failing local `rev-parse HEAD` (the probe `join` uses, `SyncCycle.cloneDamage`), or a push that failed with kind `Clone`
   (its reset, clean or `HEAD` check failed) followed by the same failing probe, is damage; a network, authentication,
   timeout or cancelled failure never is, so a clone is never moved aside because the network was down. Damage is recovered
   as step (e) does and the attempt restarts from the scan; a recovery does not count as an attempt.
9. **Once per process.** The cycle moves one damaged clone aside per process. If a clone it has just made is also unusable
   (damage again after a recovery), it returns `Failed(clone, CloneUnusable, ...)` and **keeps that state**: every later
   `RunAsync` on the instance returns the same `Failed` at once, touching nothing, so a repository that cannot be checked out
   does not fill the disk with sidelined copies. A restart of the process tries again. A failed clone (network, auth) is a
   plain `Failed(clone, kind, ...)`, retried by the next cycle, and does not set this state.

### Recording and cancellation

10. **`git_sync_last_ok` and `git_sync_last_error`** are written by the cycle, under the lock, as the last step (`record`):
    `Done` writes `last_ok` (UTC, `yyyy-MM-ddTHH:mm:ss.fffZ`, from the clock) and deletes `last_error`, **including a `Done`
    whose pull was incomplete** (nothing failed: the owner's call, the result still says the push did not run);
    `Failed` and `Refused` write `last_error` as `<same time format> <step> <kind>: <message>` (one line: control
    characters and U+2028/U+2029 become a space, at most 500 characters; `SyncCycle.errorLine`, pure) and leave `last_ok`;
    `Busy`, `NotJoined` and `Cancelled` write nothing (`Busy` is not this process's error, `NotJoined` has no sync state).
    A failure to write is swallowed: it never changes the outcome and never throws. The cycle writes no other key of its own.
11. **Cancellation.** `ct` is checked before each step and passed to every part. `OperationCanceledException`, a part
    reporting its own cancellation (the fetch's `Cancelled` kind, the push's `Failed("Cancelled", ...)`) and a cancelled lock
    wait all end the cycle `Cancelled`, with nothing recorded. The parts already leave the store and the clone consistent
    on a cancel (their designs); once the push command has succeeded the push finalizes whatever the token says and the
    cycle reports `Done`, never `Cancelled`.
12. **Unexpected exceptions.** Anything the parts do not turn into an outcome (an `IOException`, an `InvalidOperationException`,
    a `NotSupportedException` from the lock when file locking is disabled) is caught around each step, logged with
    `ExceptionLogger.LogChain("[total-recall] sync cycle", ex)` (standard error, like every other boundary), and returned as
    `Failed(<step>, Unexpected, <message>, No)` and recorded as a failure. `OutOfMemoryException` is not caught.

## Pure rules (`Core/SyncCycle.fs`)

`Step` and `stepName`; `maxAttempts = 3`; `nextAfterPush : attempt:int -> PushKind -> Next` (`Finish` for success, `Fail` for a
failure, `Retry` for `NonFastForward` while `attempt < maxAttempts`, else `Finish` with a non-fast-forward failure);
`pushDecision : fetchWentOn:bool -> applyDone:bool -> complete:bool -> PushGate` (`Run`, or `Skip` with a reason word);
`cloneDamage : FailureKind -> localProbeFailed:bool -> bool`; `okValue : DateTimeOffset -> string`;
`errorLine : DateTimeOffset -> step:string -> kind:string -> message:string -> string`. All total, no I/O, no clock.

## Requirements on the later parts

- **1b-ii and 1b-iii** make the pull complete in more cases and replace the "pull-incomplete" skip for the cases they handle;
  1b-ii must make the push skip an entry with an open `held-delete` item (push design). 1b-iii adds `sync now` and the
  `status` block (which reads `last_ok` and `last_error` and the summary), the secret scan, and is the milestone before which
  nothing calls the cycle.
- **Milestone 2** (timer, `session_start`, `session_end`) keeps one `GitSyncCycle` per process (decision 9) and reads
  `OnStep` for progress; it reads the configuration and gives the cycle its settings.

## Interfaces (for the plan)

- `Core.SyncCycle` as above. `Infrastructure.GitSync`: `CycleSettings`, `CycleSummary`, `CycleOutcome`, `GitSyncCycle`
  (constructor as decision 1, `RunAsync(CancellationToken)`, internal seams: `OnStep`, `Fault`-style hooks through the
  injected git process of the tests), and `GitSyncFetcher.CheckIdentityAsync(CancellationToken) : Task<string?>` (the
  refusal text or null; the fetcher's own first step uses it).
- No schema change, no config key, no command.

## Tests

Core (`SyncCycleTests.fs`): `nextAfterPush` for every attempt and outcome; `pushDecision` for every combination;
`cloneDamage` for every failure kind with and without a failing probe; `okValue`; `errorLine` (control characters, a
newline, U+2028, a 600-character message cut at 500, an empty message, round trip of the time prefix); totality over
generated input.

Infrastructure (the pull's and the push's rigs: a joined machine, a bare remote, real git, the faulting process):
a cycle that pushes a new and a changed memory and applies another machine's change in one run (`Done`, `last_ok`, no
`last_error`, the order of `OnStep`); a cycle with nothing to do (no commit, no push command); not joined and each refusal
(no scan, no stamp, no git, no network call recorded; `Refused` recorded as `last_error`, `NotJoined` recorded nowhere); a held
lock (`Busy`, nothing run, nothing written; the same process's second cycle); a stale `index.lock` removed before git
runs; an incomplete pull (a deletion the pull postpones: `Done`, push not run, `PushSkipped`, `last_ok` written, the
push command never recorded); the push never reached after a refused fetch, a failed fetch and a failed apply; a
non-fast-forward at the push (a second attempt succeeds: `Attempts = 2`, the remote holds both machines' work) and three
in a row (`Failed(push, NonFastForward)`, everything pending, the next cycle succeeds); a failure of each kind at each
step (the right `Failed` step, kind and `published`; `last_error` text; a later `Done` clears it); a failure to record is
swallowed; a lost clone (directory deleted: re-clone, unproven apply, nothing deleted, push of the pending changes); a
corrupt clone (`.git` garbage: renamed to `repo.broken-<utc>`, kept with its content, re-cloned, `SidelinedClone` set);
a network failure with a sound clone (clone untouched, never moved); damage a second time in one process
(`CloneUnusable`, the later cycle fails at once and touches nothing); damage reported by a failing push reset; a cancel
before each step and in the middle of the fetch, the apply and the push (`Cancelled`, nothing recorded, lock released,
the clone and rows consistent); a cancel after the push command succeeded (`Done`); an exception thrown by a part
(`Failed(step, Unexpected)`, lock released); `OnStep` order; and `git grep` shows that nothing outside the cycle and its
tests calls it, and nothing but the cycle and the push's tests calls the pusher.

## Departures from the umbrella design

- **A retry starts from the scan.** The umbrella goes back to the fetch; the scan is local and cheap, and the apply and the
  push take its summary, which should describe the same moment as the store.
- **The identity check comes before the scan** (the umbrella lists the scan first but also says nothing runs on a machine
  that is not joined or whose data directory was copied).
- **`last_ok` is written for a cycle whose pull was incomplete**, which the umbrella does not say; the result says what
  did not run.
- **`last_error` carries a time and the failing step**, as one line, so `status` (1b-iii) can say "failed 5 minutes ago in
  fetch" without another key.
- **A damaged clone is told apart from a network failure by a local probe**, as `join` does, instead of "a git failure that
  survives stale-lock removal".

## Acceptance criteria

The ticket's criteria, numbered here for the tests to name them.

1. Given a joined machine with a new memory, an edited memory and a repository that another machine also changed, when a
   cycle runs, then it ends `Done`, the other machine's change is in the store, this machine's changes are in the
   repository as one commit, nothing is pending for them, and the last-ok time is recorded.
2. Given a machine that has not joined, or whose guard refuses (moved or copied data directory, a different remote or
   repository, a newer format, a missing branch), when a cycle runs, then nothing is stamped, no git or network command runs
   before the refusal, the outcome names the reason, and a refusal is recorded as the last error.
3. Given another process holding the sync lock, when a cycle runs, then it ends `Busy` after the configured wait with
   nothing run and nothing recorded; the lock is released on every ending.
4. Given a stale lock file left in the local repository copy by a killed git, when a cycle runs, then it is removed first and
   the cycle succeeds.
5. Given a pull that is refused, failed or incomplete, when the cycle runs, then no push is attempted; an incomplete pull ends
   `Done` saying the push did not run, and records the last-ok time.
6. Given the remote branch moving between the pull and the push, when the push is rejected, then the cycle fetches, applies
   and pushes again, up to three pushes in all; the third rejection ends `Failed` with every change still pending, and a later
   cycle succeeds.
7. Given a failure in any step (network, authentication, a store failure, a push refused by the host), when a cycle ends,
   then the outcome names the step, the failure and whether the push may have reached the remote, one line is recorded as the
   last error, nothing is left half done, and the next cycle recovers; a success clears the last error.
8. Given a missing local copy of the repository, when a cycle runs, then it is cloned again, nothing is deleted from the
   store, and pending changes are pushed.
9. Given a damaged local copy, when a cycle runs, then it is renamed (never deleted) and cloned again, the outcome says where
   the old one went, and nothing is deleted from the store; a network failure never causes this.
10. Given a second damaged copy in the same process, when a cycle runs, then it fails with a clear reason, and every later
    cycle in that process fails at once without touching the disk.
11. Given a cancelled cycle, when it ends, then it reports `Cancelled`, records nothing, releases the lock and leaves the
    store and the local copy consistent; a cancel after the push was accepted still finishes the bookkeeping and reports
    success.
12. Given an unexpected error inside a step, when the cycle ends, then it returns a failure for that step instead of throwing,
    logs the cause and releases the lock.
13. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then
    they pass and the release build shows no new warning; given the contributor guides (`AGENTS.md` and the two in
    `src/TotalRecall.Core` and `src/TotalRecall.Infrastructure`), then they describe the new pieces; and nothing outside the
    cycle and its tests calls it.
