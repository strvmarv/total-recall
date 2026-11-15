# Git sync, milestone 1a-1: the git client, the lock and the tree reader

Date: 2026-11-15. Milestone 1a-1 of the git sync feature tracked in #93 (0a is #100, 0b-1 is #105, 0b-2 is #111, 0b-3 is #118).
The umbrella design is `2026-11-15-git-sync-design.md` (see "Isolating git from the user's configuration", "Reading the
repository safely", "The sync cycle" (the lock, steps 2 to 4), "History: append-only, never rewritten", "Failure handling" and
"Testing"). Milestone 1a is split in three; this is the first and holds only the plumbing that talks to `git`, to the
operating system's file lock and to the repository's file tree. 1a-2 (the repository guard, the identity check
`not-joined`, `OpenGit` and the `config_set` denylist) and 1a-3 (the `sync init --url`, `join`, `leave` and `status` verbs)
build on it. Nothing here is reachable by a user yet.

## Problem

Every later step of git sync runs `git` against a repository the user owns, in the background, on machines whose git
configuration was written for interactive use. If that call can prompt, hang, sign a commit, run a hook, rewrite a path,
operate on the wrong repository, follow a link out of the clone, or force-push, then sync can freeze a session, damage
the user's other repositories or rewrite the history that is the user's backup. Those failures are hard to see in a
review of the cycle, so the one place that runs `git` is written first, small, and tested against a real repository
before any cycle exists.

## Goals

1. **`GitClient`**: the one way sync runs `git`. It speaks only a closed set of typed commands, never a free-form
   argument list, so "never force, never rewrite shared history, never run a shell" is a property of the type and not of
   each call site.
2. **Isolation**: every call runs with the umbrella's configuration overrides and environment, a hard wall-clock limit
   that kills the process and its children, no prompt of any kind, and a classified, credential-free failure.
3. **The OS file lock** (`SyncLock`) at the umbrella's path, with a bounded wait.
4. **The tree reader**: parse `git ls-tree` and `git diff-tree` output and decide, for each entry under `memories/`,
   whether it is an acceptable record file (a regular file, at the right path, with a legal id and a size within the
   limit) or a rejected one, with the reason. Symlinks, submodules and every other kind of entry are rejected.
5. **Paths**: where the clone, the lock and the empty hooks directory live.
6. **The `SensitiveContentScanner` split**, so the secret block of milestone 1b-iii can run the token and private-key
   detectors without the email detector.
7. No change to what a user sees or can do. Nothing calls any of this yet.

## Non-goals

- Anything that decides what to fetch, apply or push: the cycle is 1b. `GitClient` has no notion of a record, a stamp or
  a base.
- Parsing the progress lines of `git fetch` into `SyncProgress`: milestone 3. This milestone only delivers the lines as
  they arrive.
- The public-repository check, the manifest and `repo_id`, `not-joined`: 1a-2. `gh` and repository creation: milestone 4.
- Reading and writing record files: 0b-1 (done). This milestone returns the bytes of a blob and nothing more.
- The `sync` verbs, `OpenGit`, the storage-mode filter and the config denylist: 1a-2 and 1a-3.
- Writing `.gitattributes` or `manifest.json`: 1a-3.

## Decided here

The umbrella fixes the behavior; these are the calls it leaves open or that this milestone refines. Each marked
**(beyond)** departs from, or goes past, the umbrella's text and is justified.

1. **A closed set of typed commands (beyond).** The umbrella says "git is run with an argument list, `--` before any
   user-supplied value". This milestone goes one step further: `GitCommand` is a discriminated union in `TotalRecall.Core`
   with one case per operation sync needs (listed under "Commands"), and a pure `toArgs` turns a case into its argument
   list. There is no case that takes free arguments. So the rule "`push` is never given `--force`, `-f` or
   `--force-with-lease`" holds because no case can produce them, and a test over every case checks it, instead of every
   caller being reviewed for it.
2. **Every clone-scoped command names its repository explicitly (beyond).** A command that works on the clone is run as
   `git --git-dir=<clone>/.git --work-tree=<clone> …`, never by changing directory and letting git search upwards. If the
   clone's `.git` is missing or damaged, git reports "not a git repository" instead of finding a parent repository
   (the data directory can sit inside the user's own project through `TOTAL_RECALL_DB_PATH`), and `reset --hard` and
   `clean` can never touch anything but the clone. The client also refuses to run a destructive command (`reset --hard`,
   `clean`) when `<clone>/.git` is not a directory.
3. **The hooks directory is outside the clone (beyond).** The umbrella puts the empty `core.hooksPath` directory under
   the clone's `.git`. That does not exist before the first clone and is lost when a corrupt clone is renamed aside.
   The client uses one empty directory, `<directory of the database file>/sync/no-hooks`, for every command, created on
   first use. `clean -fd` runs inside the clone, so it cannot touch it either. `init` and `clone` also pass `--template=`
   (empty) so nothing from the user's template directory is copied into the clone.
4. **The environment is scrubbed of repository selectors (beyond).** Besides the umbrella's `GIT_TERMINAL_PROMPT=0` and
   the `GIT_ASKPASS` removal, every call removes `GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`,
   `GIT_ALTERNATE_OBJECT_DIRECTORIES`, `GIT_COMMON_DIR`, `GIT_NAMESPACE` and `GIT_PREFIX` (a host that runs inside a git hook
   exports them), removes `SSH_ASKPASS`, and sets `LC_ALL=C` and `LANGUAGE=C` so the messages the failure classifier reads
   are in English. Everything else (`PATH`, `HOME`, `SSH_AUTH_SOCK`, the credential helpers) is passed through: the user's
   credentials are how sync authenticates.
5. **The ssh command.** `GIT_SSH_COMMAND` is set to `<user's command> -o BatchMode=yes -o ConnectTimeout=8`, where the
   user's command is the first of the process's own `GIT_SSH_COMMAND`, `git config --get core.sshCommand` (one read per
   process, cached, run with the scrubbed environment and none of the `-c` overrides), else `ssh`. When none of
   those is set but `GIT_SSH` is, the environment is left alone (a `GIT_SSH` program such as `plink` does not take
   OpenSSH options) and the hard limit is the only protection; that is stated in the docs of 1a-3. If the user's command
   already sets `BatchMode` or `ConnectTimeout`, ssh keeps the first value it sees, which is theirs; the hard limit still
   applies.
6. **Remote addresses.** A remote is accepted when `GitSyncSettings.isUsableRemote` holds (milestone 0b-3) **and** it does
   not begin with a transport-helper prefix (`<scheme>::`, such as `ext::` or `fd::`, which make git run a program).
   `file://` URLs and local paths are accepted: they are how the tests and some users reach a repository. IPv6 literals in
   a URL (`https://[::1]/x`) are not helper prefixes. A refused address is an error before any process starts.
7. **Branches** are accepted when `GitSyncSettings.isUsableBranch` holds. A revision is accepted when it is a 40- or
   64-hex object id, `HEAD`, `refs/heads/<usable branch>`, `refs/remotes/origin/<usable branch>` or
   `refs/total-recall/last-synced`. There is no other free text in a command except a commit message (a single argument
   after `-m`, never interpreted) and paths from a fixed set (`memories`, `manifest.json`, `.gitattributes`, or a record
   path built by `SyncValidation.recordPath`).
8. **The fetch refspec is forced, the push refspec cannot be.** The cycle must survive a remote whose history was rewritten
   by hand, so `fetch` updates the local tracking ref with `+refs/heads/<b>:refs/remotes/origin/<b>`. That `+` only
   concerns a ref inside the disposable clone. `push` is exactly `push origin HEAD:refs/heads/<b>`, with no `+`, no force
   option, no delete form and no mirror. "Forced" never appears in a push; a test over every command checks it.
9. **`ls-tree` is run with `-l`.** The umbrella's step 3 lists paths with `--name-only`; the tree reader needs the mode
   (to see a symlink or a submodule) and the size (to refuse an oversized file before reading it), so the listing is
   `ls-tree -r -z -l <rev> -- memories`. The path set the cycle uses is the same.
10. **Timeouts are the caller's.** Each call takes a wall-clock limit and a cancellation token. The client has defaults
    (30 seconds for a local command, 120 for one that uses the network) and a hard kill of the process tree at the limit.
    The stall and soft limits of `session_start` are milestone 2's and sit above this.
11. **Output is bounded.** Standard output of a call is captured up to 64 MiB and standard error up to 64 KiB (the last
    part is kept). A larger output ends the call as `OutputTooLarge`. A blob read is further bounded by the caller's
    byte limit, which the tree reader's size check makes the normal case.
12. **Failures are classified and redacted.** A pure function turns the exit code, the standard error and whether the call
    timed out into one of `NoGit`, `GitTooOld`, `TimedOut`, `Cancelled`, `Auth`, `RemoteNotFound`, `Network`,
    `NonFastForward`, `HostRejected`, `RemoteBranchMissing`, `NotARepository`, `OutputTooLarge` or `Other` (the rows of the
    umbrella's "Failure handling"). The text kept for the user is redacted: the user-info part of any URL (`https://user:token@host`)
    is replaced, and the text is cut to 2 KiB. No log line, exception message or result ever carries the remote's
    credentials.
13. **The minimum git is 2.28** (`init -b`). A version below it is `GitTooOld`; a missing executable is `NoGit`. Nothing
    else is feature-detected.
14. **Real-git tests run in the standard verify (beyond).** The umbrella tags tests that need `git` as
    `Category=Integration`, which the repository's verify and CI exclude, so they would never run. Here they are ordinary
    tests that skip themselves, with a reason, when no usable `git` is found. CI's ubuntu runner and a contributor's machine
    have one, so they run; the skip count shows when they did not. They use a local bare repository as the remote and
    never the network.
15. **The lock holds no diagnostic text.** The OS lock is taken on a `FileStream` opened with `FileShare.None`, which the
    operating system releases when the process dies. A second taker cannot read the file on Windows, so no pid is written;
    `busy` does not say who holds it.

## Design

New files, all in the existing projects.

`TotalRecall.Core` (pure; F#):

- `SyncGit.fs`, module `TotalRecall.Core.SyncGit`: `GitCommand`, `toArgs`, `validate`, the fixed overrides and environment
  lists, `composeSshCommand`, `classifyFailure`, `redact`, `cloneKey`.
- `SyncTree.fs`, module `TotalRecall.Core.SyncTree`: `parseLsTree`, `parseDiffTree`, `classifyRecordEntry`.

`TotalRecall.Infrastructure` (C#):

- `GitSync/GitProcess.cs`: the one place a process is started.
- `GitSync/GitClient.cs`: the typed operations.
- `GitSync/GitSyncPaths.cs`, `GitSync/SyncLock.cs`.
- `Eval/SensitiveContentScanner.cs`: the detector selection.

### Commands

`GitCommand` has exactly these cases. The column "scope" says whether the call runs with `--git-dir`/`--work-tree` (the
clone) or on its own (a remote address or no repository). Remote addresses and revisions are validated as above.

| Case | Argument list (after the overrides) | Scope |
|---|---|---|
| `Version` | `--version` | none |
| `ConfigGetSshCommand` | `config --get core.sshCommand` | none (global) |
| `Init(branch)` | `init -b <branch> --template= -- <clone>` | none |
| `Clone(url, branch)` | `clone --depth=1 --no-tags --single-branch --branch <branch> --template= -- <url> <clone>` | none |
| `LsRemote(remote, branch)` | `ls-remote -- <remote> refs/heads/<branch>` (`remote` is a URL, or `origin` in a clone) | none or clone |
| `SetOrigin(url)` | `remote add origin -- <url>` when absent, else `remote set-url origin -- <url>` | clone |
| `Fetch(branch)` | `fetch --no-tags --progress origin +refs/heads/<branch>:refs/remotes/origin/<branch>` | clone |
| `ResetHard(rev)` | `reset --hard <rev>` | clone |
| `Clean` | `clean -fd` | clone |
| `IsAncestor(a, b)` | `merge-base --is-ancestor <a> <b>` | clone |
| `RevParse(rev)` | `rev-parse --verify --quiet <rev>^{commit}` | clone |
| `UpdateRef(name, sha)` | `update-ref <name> <sha>`, `name` only `refs/total-recall/last-synced` | clone |
| `DiffTree(from, to)` | `diff-tree -r -z --no-renames --raw <from> <to> -- memories` | clone |
| `LsTree(rev)` | `ls-tree -r -z -l <rev> -- memories` | clone |
| `LsTreeRoot(rev)` | `ls-tree -z -l <rev>` (the top level only, for the manifest and attributes) | clone |
| `CatBlob(sha)` | `cat-file blob <sha>` | clone |
| `Stage(paths)` | `add -A -- <paths>` | clone |
| `HasStaged` | `diff --cached --quiet --exit-code` (0: none, 1: some) | clone |
| `Commit(message)` | `commit --no-verify -m <message>` | clone |
| `Push(branch)` | `push origin HEAD:refs/heads/<branch>` | clone |

No other `git` invocation exists in sync code. `Fetch` carries `--progress` so the lines that later milestones parse are
produced even when the standard error is not a terminal. `Commit` is run with the umbrella's identity and signing
overrides, so it needs nothing from the user's configuration. The "remove stale lock files" step of the umbrella is not
a `git` command: it is a method of the client that deletes files named `*.lock` under `<clone>/.git` and nothing else.

### The overrides

Every call carries, before the subcommand, the umbrella's `-c` list: `core.autocrlf=false`, `core.symlinks=false`,
`core.longpaths=true`, `commit.gpgsign=false`, `core.hooksPath=<no-hooks>`, `user.name=total-recall`,
`user.email=noreply@total-recall.local`, `credential.interactive=never`, plus `core.quotepath=false`,
`protocol.ext.allow=never`, `protocol.fd.allow=never`, `http.lowSpeedLimit=1000` and `http.lowSpeedTime=20`, and
`gc.auto=0` and `maintenance.auto=false` so a background commit never starts a repack that outlives the call. `Version` and
`ConfigGetSshCommand` take none.

### `GitProcess`

`GitProcess.RunAsync(command, options, cancellation)` builds the argument list (`ProcessStartInfo.ArgumentList`, so there
is no shell and no quoting), starts the executable named by the **caller** (so unit tests can point it at a stub, as the
umbrella requires), closes standard input, reads both output streams concurrently, and returns
`GitResult { ExitCode, Stdout (bytes), StderrText (redacted, bounded), Failure (kind or none), Elapsed }`. At the wall-clock
limit, or when cancelled, it kills the whole process tree (`Process.Kill(entireProcessTree: true)`) and returns
`TimedOut` or `Cancelled`. A process that cannot start is `NoGit`. An optional callback receives each standard-error line
as it arrives. An interface, `IGitProcess`, sits in front of it so a test (and later the integration suites) can record
every invocation without running it.

### `GitClient`

A thin typed layer over `GitProcess`: one method per case above, each returning `Result`-style values (a typed success, or
the `GitResult` failure). It resolves nothing about repositories or remotes by itself; it is given the executable, the
`GitSyncPaths` and the clone directory. It owns the ssh-command composition (the cached `ConfigGetSshCommand` read), the
check that `<clone>/.git` is a directory before `ResetHard` and `Clean`, and `RemoveStaleLocks`.

### The tree reader

`parseLsTree` reads the NUL-separated records of `ls-tree -r -z -l` (`<mode> SP <type> SP <sha> SP+ <size> TAB <path>`,
the size being `-` for a non-blob) and `parseDiffTree` those of `diff-tree -r -z --raw` (`:<old mode> <new mode> <old sha>
<new sha> <status> NUL <path> NUL`, statuses `A`, `M`, `D` and `T`). Both are total: a malformed record is an `Error`
naming it, never an exception. `classifyRecordEntry` takes one entry and answers

- `Accept(id, sha, size)` for a regular file (`100644` or `100755`; the executable bit is not an error) whose path is
  exactly `memories/<shard>/<id>.json` with an eligible id (`SyncValidation.idOfPath`) and whose size is at most 1 MiB
  (`SyncValidation.maxFileBytes`);
- `Reject(path, reason)` otherwise, with a reason of `symlink` (`120000`), `submodule` (`160000`, type `commit`),
  `not-a-regular-file` (any other mode or type), `bad-path`, `bad-shard`, `bad-id` or `too-large`.

A path that is not valid UTF-8, or that contains a control character, is `bad-path`. The caller counts rejected entries and
lists them in `status` (1b); nothing here reads a blob. `parseDiffTree` keeps the status so the caller can tell an added,
a changed and a removed path apart, and a type change (`T`) to or from a symlink is classified by its new mode.

### `SyncLock`

`SyncLock.TryAcquireAsync(path, wait, cancellation)` creates the directory, opens `<path>` (`FileMode.OpenOrCreate`,
`FileAccess.ReadWrite`, `FileShare.None`) and returns a handle that releases on `Dispose`; when the file is held it retries
every 50 ms until `wait` ends or the token is cancelled and then returns `null` (busy). Any other failure (a missing parent
that cannot be created, a permission error) throws, so "busy" is never confused with "broken". The same process cannot take
the lock twice: the operating system refuses a second open on every platform .NET supports. The lock file is never deleted.

### `GitSyncPaths`

For a database file `D`, the sync directory is `<directory of D>/sync`. The clone is `sync/<cloneKey remote>/repo`, where
`cloneKey` is the first 12 lowercase hex characters of the SHA-256 of the remote's UTF-8 bytes, the lock is `sync/lock`,
and the hooks directory is `sync/no-hooks`. The class only builds and creates paths; it never deletes. A path derived from
anything but `D` and a validated remote does not exist.

### `SensitiveContentScanner`

The detectors become selectable: a flags enum `Detectors { Email, Token, PrivateKey }`, an overload
`Scan(text, internalTerms, detectors)` and a `ScanSecrets(text)` that runs `Token | PrivateKey` and no internal terms. The
existing `Scan(text, internalTerms)` runs all three and returns the same reasons in the same order as before, so
`eval_grow` is unchanged.

## Testing

Pure (Core): every `GitCommand` case against its expected argument list; no case's argument list, over every case and every
legal parameter shape, contains `--force`, `-f`, `--force-with-lease`, `--delete`, `--mirror`, `--prune`, `--no-verify` outside
`Commit`, a `+` outside the fetch refspec, or an argument that starts with `-` that the table does not list; `validate` for
addresses (leading dash, whitespace, control, `ext::x`, `fd::1`, `https://[::1]/x`, `file:///tmp/r`, `C:\r`, a scp-like
`git@host:o/r.git`), branches, revisions and paths; the overrides and environment; `composeSshCommand` for each source of the
user's command; `classifyFailure` on a real message for each kind and a property that it is total; `redact` on user-info in
every URL shape and on a property that no output contains an input's credentials; `cloneKey` against a fixed vector;
`parseLsTree` and `parseDiffTree` on real output (including paths with spaces, quotes and non-ASCII, and a malformed record)
and `classifyRecordEntry` on every verdict.

Infrastructure, with real `git` (a bare repository as the remote, skipped with a reason when `git` is missing): isolation
(a global config with `commit.gpgsign=true`, `core.hooksPath` to a hook that writes a file, `core.autocrlf=true`,
`init.defaultBranch=trunk` and an `ext` helper has no effect on `Init`, `Commit` and `Clone`); the hard kill (a remote that
accepts and never answers, through a local script, is killed at the limit with its child); no prompt (an `https` remote that
asks for credentials fails at once as `Auth`); the clone, `LsRemote` (a head, no head on an empty remote), incremental `Fetch`
into a shallow clone, `ResetHard`, `IsAncestor` after a remote rewritten by hand, `RevParse`, `UpdateRef`, `LsTree`,
`DiffTree` (added, changed and removed; no rename), `CatBlob`, `Stage`/`HasStaged`/`Commit`/`Push` and a second clone seeing
it, a push after the remote moved on (`NonFastForward`), a push that never forces (a recording `IGitProcess` over the whole
suite asserts on every invocation); a clone directory with no `.git` inside another repository (`ResetHard` and `Clean`
refuse, the other repository is intact); `GIT_DIR` and `GIT_WORK_TREE` in the environment are ignored; a repository holding
a symlink, a submodule entry, a file in the wrong shard, a file named by a path-like id and an oversized file under
`memories/` is listed with each one rejected for the right reason (built with plumbing commands in the test); the same on
a Windows-style `core.autocrlf` does not alter a blob's bytes (the test sets it in its global config); `RemoveStaleLocks`
removes a planted `index.lock` and nothing outside `.git`; the stderr callback sees fetch progress.

`SyncLock`: a second taker waits and then reports busy; release on `Dispose`; the lock is released when the holding process
is killed (a child process holds it; skipped with a reason where one cannot be started); independent paths do not
interact; a cancelled wait returns promptly; a non-lock error throws.

`GitSyncPaths`: two databases, and two spellings of one remote, give different clones; one database and one remote give the
same path every time; nothing outside `sync/` is produced.

`SensitiveContentScanner`: tokens and private keys are found by `ScanSecrets`; `git@github.com`, `noreply@x.com` and ordinary
prose are not; `Scan` returns exactly what it did, which the existing tests check.

AOT: the new code builds under `PublishAot` with no new warning (no reflection serializer, no `%A`).

## Acceptance criteria

The ticket carries the user-level criteria; this is the mapping the implementation must satisfy. Each is checked by one or
more tests above.

1. `git` missing or older than 2.28 is reported distinctly and does not throw.
2. A user's git configuration (signing, hooks, line endings, default branch, transport helpers) does not change what sync's
   commits, clones and branches look like.
3. A call that does not finish is killed with its children at its limit, and a cancelled call stops.
4. A remote that wants a password, a passphrase or a host-key answer fails at once, without prompting, and is classified as an
   authentication failure.
5. An unreachable host, a refused connection and a missing repository are classified apart from authentication.
6. A remote address that starts with a dash, holds whitespace or a control character, or names a transport helper is refused
   before `git` starts; so is a branch the design does not allow.
7. A shallow clone of a bare repository holds one commit; the remote's head can be read without a fetch; an empty remote has none.
8. An incremental fetch brings only new objects and leaves the clone shallow; the clone can be reset to the fetched head.
9. A remote whose history was rewritten by hand still fetches, and the clone can tell the old head is not an ancestor.
10. A commit pushed from one clone appears in another; a push after the remote moved on is reported as non-fast-forward.
11. No `git` invocation that sync can issue carries a force, delete or mirror option or a `+` push refspec.
12. A damaged clone inside another repository never causes a command to touch that other repository.
13. Repository-selecting environment variables of the host are ignored.
14. Symlinks, submodules, other non-regular entries, wrong paths, wrong shards, bad ids and oversized files under `memories/`
    are each rejected with a reason, and a regular file at the right path is accepted with its id and size.
15. A diff between two commits lists added, changed and removed paths, with no rename detection.
16. A blob is returned byte for byte, and one over the caller's limit is refused.
17. A stale `*.lock` file in the clone's `.git` is removed, and nothing outside it.
18. A second process that wants the lock waits its bounded time and is told it is busy; the lock is freed when the holder
    exits or is killed.
19. Two databases never share a clone, and one remote spelled two ways gets two clones.
20. No message, result or exception carries a credential from a remote address.
21. The secret scan can run the token and private-key detectors without the email detector, and the benchmark-corpus guard
    behaves as before.
22. The existing automated checks and the release build pass, with no new build warning.
23. The contributor guides describe the new pieces.
