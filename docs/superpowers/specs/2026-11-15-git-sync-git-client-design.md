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
- Writing `.gitattributes` or `manifest.json`, and renaming an existing clone to a respelled URL's path on a re-join: 1a-3.
- The identity check on a copied or moved data directory (`git_sync_host`), the storage-mode fallback filter and the
  `ReindexEmbeddingsCommand` edit (the other items of the umbrella's 1a row): 1a-2.
- A batch blob read (`cat-file --batch`) for a first join of thousands of records: `CatBlob` reads one blob per process, and
  milestone 1b-i adds a batch case to the closed set if the measurement asks for it.

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
   The client uses one empty directory, `no-hooks`, and one empty file, `empty`, both in the database's sync directory
   (see "`GitSyncPaths`"), for every command, created on first use. `clean -fd` runs inside the clone, so it cannot touch
   them. `init` and `clone` also pass `--template=` (empty) so nothing from the user's template directory is copied into
   the clone. The empty file is given as `core.attributesFile` and `core.excludesFile`, so a global attributes filter or
   ignore list cannot rewrite or hide the bytes of a record (the repository's own `.gitattributes`, `* -text`, still applies).
4. **The environment keeps only the git variables sync needs (beyond).** Every call removes **every** variable whose name
   begins with `GIT_` (compared without regard to case) except a named allowlist: `GIT_SSH`, `GIT_SSH_COMMAND` (rewritten, see 5),
   `GIT_SSH_VARIANT`, `GIT_SSL_*`, `GIT_PROXY_COMMAND`, `GIT_CONFIG_GLOBAL`, `GIT_CONFIG_SYSTEM` and `GIT_CONFIG_NOSYSTEM` (the user's
   choice of which configuration files exist). That drops the repository selectors a host that runs inside a git hook exports
   (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`, `GIT_OBJECT_DIRECTORY`, `GIT_COMMON_DIR`, …), injected configuration
   (`GIT_CONFIG_PARAMETERS`, `GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_<n>`), the author and committer identity (`GIT_AUTHOR_*`,
   `GIT_COMMITTER_*`) and every tracing variable (`GIT_TRACE*`, `GIT_CURL_VERBOSE`: their output can carry `Authorization:` headers).
   The call then sets `GIT_TERMINAL_PROMPT=0`, removes `SSH_ASKPASS`, and sets `LC_ALL=C` and `LANGUAGE=C` so the messages the
   failure classifier reads are in English. Everything else (`PATH`, `HOME`, `SSH_AUTH_SOCK`, the credential helpers) is passed
   through: the user's credentials are how sync authenticates. A test seam, `GitRunOptions.EnvironmentOverrides`, sets variables
   **after** this scrub, so a test can point `HOME`, `XDG_CONFIG_HOME` and `USERPROFILE` at a throwaway configuration (the
   minimum git is 2.28, so `GIT_CONFIG_GLOBAL`, which needs 2.32, cannot be relied on).
5. **The ssh command.** `GIT_SSH_COMMAND` is set to `<user's command> -o BatchMode=yes -o ConnectTimeout=8`, where the
   user's command is the first of the process's own `GIT_SSH_COMMAND`, `git config --get core.sshCommand` (one read per
   process, cached, run with the scrubbed environment and none of the `-c` overrides), else `ssh`. The configuration read is
   `config --global --get core.sshCommand` and, when that is unset (exit 1), `config --system --get core.sshCommand`: never a
   plain `config --get`, which would read the local configuration of whatever repository the process happens to be in. A
   `core.sshCommand` set inside the clone itself is not read (the user does not edit the clone). When none of
   those is set but `GIT_SSH` is, the environment is left alone (a `GIT_SSH` program such as `plink` does not take
   OpenSSH options) and the hard limit is the only protection; that is stated in the docs of 1a-3. If the user's command
   already sets `BatchMode` or `ConnectTimeout`, ssh keeps the first value it sees, which is theirs; the hard limit still
   applies.
6. **Remote addresses.** A remote is accepted when `GitSyncSettings.isUsableRemote` holds (milestone 0b-3) **and** it is of one
   of these shapes, decided by a pure `classifyRemote`: a **URL** (`<scheme>://…`, scheme letters, digits, `+`, `.` and `-`,
   including `file://`), an **scp-like address** (`[user@]host:path`, the host having at least two characters and no `/` before
   the colon), or an **absolute local path** (starts with `/`, `\\`, or a drive letter and `:\` or `:/`). Everything else is
   refused: a transport-helper prefix (`<scheme>::`, such as `ext::` or `fd::`, which make git run a program), and a **relative
   local path** (`repo`, `../x`, `./x`), which git would resolve against whatever directory the process runs in, so the same
   configuration could name a different repository each session. IPv6 literals in a URL (`https://[::1]/x`) are not helper
   prefixes. A refused address is an error before any process starts. (A bare repository path is how the tests and some users
   reach a repository; `file://` is how a test asks for a shallow clone, because git ignores `--depth` for a plain-path clone.)
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
    `NonFastForward`, `RemoteBranchMissing`, `NotARepository`, `OutputTooLarge` or `Other` (the rows of the umbrella's
    "Failure handling"; there is no kind for a host rejecting a push for its content, which the umbrella says is not parsed:
    it is `Other` and carries the redacted text). The classification is by **case-insensitive fragments of the standard
    error, tried in this order, the first match wins**, so a specific message beats the generic trailer `Could not read from
    remote repository` that git prints after many different failures:

    | Order | Kind | Fragments |
    |---|---|---|
    | 1 | `Auth` | `terminal prompts disabled`, `could not read Username`, `could not read Password`, `Authentication failed`, `Permission denied (`, `Host key verification failed`, `HTTP 401`, `HTTP 403`, `error: 401`, `error: 403`, `Invalid username or password` |
    | 2 | `RemoteNotFound` | `does not appear to be a git repository`, `Repository not found`, `repository '...' not found` (the text `not found` after `repository`), `HTTP 404`, `error: 404` |
    | 3 | `RemoteBranchMissing` | `Remote branch`+`not found`, `couldn't find remote ref` |
    | 4 | `NonFastForward` | `[rejected]` together with `non-fast-forward` or `fetch first`, or `Updates were rejected` |
    | 5 | `Network` | `Could not resolve host`, `Connection refused`, `Connection timed out`, `Operation timed out`, `Network is unreachable`, `No route to host`, `Failed to connect`, `Connection reset`, `early EOF`, `RPC failed`, `unable to access` (with a `curl` code in the same text), `kex_exchange_identification`, `Connection closed by` |
    | 6 | `NotARepository` | `not a git repository` |
    | 7 | `Other` | anything else, including the bare trailer |

    A timeout, a cancellation, a process that cannot start (`NoGit`), a version below the minimum (`GitTooOld`) and an output
    over the limit (`OutputTooLarge`) are decided before any fragment is read. The text kept for the user is redacted: the
    user-info part of any URL (`https://user:token@host`) is replaced, and the text is cut to its **last** 2 KiB after
    progress output is dropped (git's `fetch --progress` overwrites a line with `\r`; for each line only the text after the last
    `\r` is kept). No log line, exception message or result ever carries the remote's credentials.
13. **The minimum git is 2.28** (`init -b`). A version below it is `GitTooOld`; a missing executable is `NoGit`. Nothing
    else is feature-detected.
14. **Real-git tests run in the standard verify (beyond).** The umbrella tags tests that need `git` as
    `Category=Integration`, which the repository's verify and CI exclude, so they would never run. Here they are ordinary
    tests that skip themselves, with a reason, when no usable `git` is found. CI's ubuntu runner and a contributor's machine
    have one, so they run; the skip count shows when they did not. They use a local bare repository as the remote and
    never the network.
15. **The lock holds no diagnostic text.** The OS lock is taken on a `FileStream` opened with `FileShare.None`, which the
    operating system releases when the process dies. A second taker cannot read the file on Windows, so no pid is written;
    `busy` does not say who holds it. "Busy" is told apart from a real error by the open failure's `HResult`: a sharing
    violation on Windows (the low 16 bits are 32 or 33) and `EAGAIN`/`EWOULDBLOCK` on Unix (11 on Linux, 35 on macOS) are busy;
    anything else is a real error and throws. .NET disables its Unix file locking when `DOTNET_SYSTEM_IO_DISABLEFILELOCKING` is
    `1` or `true`; the lock would then silently exclude no one, so `TryAcquireAsync` detects that variable and throws
    `NotSupportedException` instead of pretending.
16. **Exit codes have a defined meaning (beyond).** The client maps them, so the cycle never reads a raw code: `IsAncestor` is
    `Yes` (0), `No` (1) or, for any other code (128 for a commit the shallow clone does not hold), `Unknown`, which the cycle
    reads as "unproven", not "corrupt"; `RevParse` is a sha (0), `Absent` (1) or a failure; `ConfigGet` is a value (0) or
    `Unset` (1); `HasStaged` is `None` (0) or `Some` (1); `LsRemote` returns the head or `None` (also for an empty remote or a
    missing branch); `Stage` treats git's "pathspec did not match any files" (128) as success, because a scope with nothing
    tracked or present stages nothing; every other non-zero code is a failure classified as above.
17. **`ls-remote` is read by exact ref.** `ls-remote` matches a pattern by suffix, so `refs/heads/main` also lists
    `refs/heads/foo/refs/heads/main`. The parser (`SyncGit.parseLsRemote`) takes only the line whose ref **equals**
    `refs/heads/<branch>`.

## Design

New files, all in the existing projects.

`TotalRecall.Core` (pure; F#):

- `SyncGit.fs`, module `TotalRecall.Core.SyncGit`: `GitCommand`, `toArgs`, `validate`, `classifyRemote`, the fixed overrides
  and environment rules, `composeSshCommand`, `classifyFailure`, `redact`, `parseLsRemote`, `cloneKey`.
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
| `ConfigGetSshCommand(scope)` | `config --global --get core.sshCommand`, or `config --system --get core.sshCommand` | none |
| `Init(branch)` | `init -b <branch> --template= -- <clone>` | none |
| `Clone(url, branch)` | `clone --depth=1 --no-tags --single-branch --branch <branch> --template= -- <url> <clone>` | none |
| `LsRemote(remote, branch)` | `ls-remote -- <remote> refs/heads/<branch>` (`remote` is a URL, or `origin` in a clone) | none or clone |
| `AddOrigin(url)` | `remote add origin -- <url>` | clone |
| `SetOriginUrl(url)` | `remote set-url origin -- <url>` | clone |
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
| `Stage(scope)` | `add -A -- <scope>`, `scope` one of `memories`, `manifest.json`, `.gitattributes` (never a list of records: a first sync of thousands would overflow the Windows command-line limit) | clone |
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
`protocol.ext.allow=never`, `protocol.fd.allow=never`, `core.askPass=` (a global askpass program would otherwise still run), `core.attributesFile=<empty>`,
`core.excludesFile=<empty>`, `core.fsmonitor=false`, `http.lowSpeedLimit=1000` and `http.lowSpeedTime=20`, and
`gc.auto=0` and `maintenance.auto=false` so a background commit never starts a repack that outlives the call. `Version` and
`ConfigGetSshCommand` take none of these.

### `GitProcess`

`GitProcess.RunAsync(command, options, cancellation)`, where the options carry the executable, the wall-clock limit, the
output byte limits, the optional `EnvironmentOverrides` (test seam) and the optional standard-error callback, builds the argument list (`ProcessStartInfo.ArgumentList`, so there
is no shell and no quoting), starts the executable named by the **caller** (so unit tests can point it at a stub, as the
umbrella requires), closes standard input, reads both output streams concurrently, and returns
`GitResult { ExitCode, Stdout (bytes), StderrText (redacted, bounded), Failure (kind or none), Elapsed }`. At the wall-clock
limit, or when cancelled, it kills the whole process tree (`Process.Kill(entireProcessTree: true)`) and returns
`TimedOut` or `Cancelled`. A process that cannot start is `NoGit`. An optional callback receives each standard-error segment
as it arrives, split on `\r` as well as `\n` (git's progress rewrites a line with `\r`). An interface, `IGitProcess`, sits in front of it so a test (and later the integration suites) can record
every invocation without running it.

### `GitClient`

A thin typed layer over `GitProcess`: one method per case above, each returning `Result`-style values (a typed success, or
the `GitResult` failure). It resolves nothing about repositories or remotes by itself; it is given the executable, the
`GitSyncPaths` and the clone directory. It owns the ssh-command composition (the cached `ConfigGetSshCommand` reads), the
check that `<clone>/.git` is a directory before `ResetHard` and `Clean`, the exit-code mapping of decision 16, a
`SetOrigin(url)` that runs `AddOrigin` and, on git's "remote origin already exists" (exit 3), `SetOriginUrl`, and
`RemoveStaleLocks`. `CatBlob` takes the caller's byte limit and refuses a larger blob.

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
every 50 ms until `wait` ends or the token is cancelled and then returns `null` (busy; see decision 15 for how busy is told
from a real error). Any other failure (a missing parent that cannot be created, a permission error) throws, so "busy" is
never confused with "broken". The same process cannot take the lock twice: .NET's `FileShare.None` refuses a second open on
Windows and, through `flock`, on Unix (measured on Linux; a test pins it on whichever platform runs). The lock file is never deleted.

### `GitSyncPaths`

For a database file `D`, the **sync directory** is `<directory of D>/sync/<dbKey>`, where `dbKey` is the first 12 lowercase
hex characters of the SHA-256 of the UTF-8 bytes of D's **file name** (not its directory). It holds the lock (`lock`), the
empty hooks directory (`no-hooks`), the empty file (`empty`) and one directory per remote: the clone is
`<sync directory>/<cloneKey remote>/repo`, where `cloneKey` is the first 12 lowercase hex characters of the SHA-256 of the
remote's UTF-8 bytes. **This departs from the umbrella (beyond)**, which puts the clone at `<directory of D>/sync/<cloneKey>/repo`
and the lock at `sync/lock`: two database files in one directory (`TOTAL_RECALL_DB_PATH=/x/a.db` and `/x/b.db`, or the
shared temporary directory tests use) would then share a clone, the protective ref `refs/total-recall/last-synced` and the
lock, and every cycle would read the other database's reference as a restored backup. Keying on the file name separates
them; keying on the directory too is not needed, because two files with the same name are in different directories, and
moving the whole data directory keeps its clone. The class only builds and creates paths; it never deletes. A path derived
from anything but `D` and a validated remote does not exist.

### `SensitiveContentScanner`

The detectors become selectable: a flags enum `Detectors { Email, Token, PrivateKey }`, an overload
`Scan(text, internalTerms, detectors)` and a `ScanSecrets(text)` that runs `Token | PrivateKey` and no internal terms. The
existing `Scan(text, internalTerms)` runs all three and returns the same reasons in the same order as before, so
`eval_grow` is unchanged.

### Documentation

`src/TotalRecall.Core/AGENTS.md` gets a row each for `SyncGit.fs` and `SyncTree.fs`; `src/TotalRecall.Infrastructure/AGENTS.md`
gets a paragraph on the `GitSync` namespace's git client, lock and paths (the closed command set, the isolation, the sync
directory layout and that real-git tests run in the standard verify); the root `AGENTS.md` "WHERE TO LOOK" table gets a row for
the git client and tree reader. Each states that nothing calls them yet.

## Testing

Pure (Core): every `GitCommand` case against its expected argument list; no case's argument list, over every case and every
legal parameter shape, contains `--force`, `-f`, `--force-with-lease`, `--delete`, `--mirror`, `--prune`, `--no-verify` outside
`Commit`, a `+` outside the fetch refspec, or an argument that starts with `-` that the table does not list; `classifyRemote`
and `validate` for addresses (leading dash, whitespace, control, `ext::x`, `fd::1`, `https://[::1]/x`, `file:///tmp/r`, `C:\r`,
`/abs/path`, `\\server\share\r`, a scp-like `git@host:o/r.git`, and the refused relative `repo`, `../x`, `./x`, `a:b`),
branches, revisions and paths; the overrides and the environment rule (every `GIT_` variable but the allowlist is dropped,
case-insensitively, including `GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_0`, `GIT_AUTHOR_NAME`, `GIT_TRACE`, `GIT_CURL_VERBOSE`);
`composeSshCommand` for each source of the user's command (including a command that already carries `BatchMode`);
`classifyFailure` with one real message per row of the table in decision 12, the precedence cases (`Permission denied`
followed by the generic trailer is `Auth`, `Connection refused` followed by it is `Network`, `does not appear to be a git
repository` followed by it is `RemoteNotFound`, the bare trailer alone is `Other`), and a property that it is total; `redact` on
user-info in every URL shape, the `\r` progress handling and the last-2-KiB rule, and a property that no output contains an
input's credentials; `parseLsRemote` including the `refs/heads/foo/refs/heads/main` case; `cloneKey` against a fixed vector;
`parseLsTree` and `parseDiffTree` on real output (including paths with spaces, quotes, non-ASCII and a newline, and a malformed
record) and `classifyRecordEntry` on every verdict (including mode `100755`, an unexpected `100664`, and a symlink in a nested
directory).

Infrastructure, with real `git` (a bare repository as the remote, reached by a `file://` URL where a shallow clone is wanted,
because git ignores `--depth` for a plain-path clone; skipped with a reason when `git` is missing). The tests redirect the
user's global configuration through `EnvironmentOverrides` (`HOME`, `XDG_CONFIG_HOME`, `USERPROFILE`) to a throwaway directory.
Isolation: a global config with `commit.gpgsign=true`, `core.hooksPath` to a hook that writes a file, `core.autocrlf=true`,
`init.defaultBranch=trunk`, `core.askPass` to a script that writes a file, a `core.attributesFile` with a `filter` and a
`core.sshCommand`, has no effect on `Init`, `Commit`, `Stage` and `Clone` (the global `core.sshCommand` is the one thing that
is read, and used); a local `core.sshCommand` in the repository the process runs in is **not** read; the `ext` transport is
refused by running `git` directly with the overrides and an `ext::` address (the typed commands refuse it earlier, which
another test shows); `GIT_CONFIG_COUNT`, `GIT_AUTHOR_NAME` and `GIT_TRACE` set in the process environment have no effect. The
hard kill: a stub executable that starts a long-running child kills both at the limit, and a real `git` pointed at a
`git://127.0.0.1:<port>` listener that accepts and never answers is killed at the limit (a cancelled call stops within two
seconds). No prompt: an `http://127.0.0.1:<port>` remote that answers `401` fails within ten seconds as `Auth` with no prompt
and no credential in the result; a fake `GIT_SSH_COMMAND` script run through an `ssh://` remote receives `-o BatchMode=yes` and
`-o ConnectTimeout=8`. Operations: the clone, `LsRemote` (a head, no head on an empty remote), incremental `Fetch`
into a shallow clone, `ResetHard`, `IsAncestor` after a remote rewritten by hand, `RevParse`, `UpdateRef`, `LsTree`,
`DiffTree` (added, changed and removed; no rename), `CatBlob` (and a blob over the caller's limit, and an output over the
process limit, are refused as `OutputTooLarge`), `Stage` (a scope with nothing in it stages nothing and succeeds)/`HasStaged`/
`Commit`/`Push` and a second clone seeing it, the exit-code mapping of decision 16 (`IsAncestor` `Unknown` for a commit a fresh
shallow clone does not hold), a push after the remote moved on (`NonFastForward`), a push that never forces (a recording `IGitProcess` over the whole
suite asserts on every invocation); a clone directory with no `.git` inside another repository (`ResetHard` and `Clean`
refuse, the other repository is intact); `GIT_DIR` and `GIT_WORK_TREE` in the environment are ignored; a repository holding
a symlink, a submodule entry, a file in the wrong shard, a file named by a path-like id and an oversized file under
`memories/` is listed with each one rejected for the right reason (built with plumbing commands in the test); the same on
a Windows-style `core.autocrlf` does not alter a blob's bytes (the test sets it in its global config); `RemoveStaleLocks`
removes a planted `index.lock` and nothing outside `.git`; the stderr callback sees fetch progress segments split on `\r`.

`SyncLock`: a second taker, in the same process, waits and then reports busy; release on `Dispose`; the lock is released when
the holding process is killed (the holder is `flock <file> sleep 60` where `flock(1)` exists, which uses the same `flock(2)`
that .NET does, and the test is skipped with a reason elsewhere; macOS and Windows rely on the platform's guarantee, which
the in-process test shows is the mechanism in use); independent paths do not interact; a cancelled wait returns promptly; a
missing-permission error throws and is not reported as busy; `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` makes it throw.

`GitSyncPaths`: two database files in one directory, and two spellings of one remote, give different clones and different
locks; one database and one remote give the same path every time; a database in a different directory with the same file
name gives the same `dbKey` (its own directory keeps it apart); nothing outside `<directory of D>/sync/` is produced.

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
5. An unreachable host or a refused connection (`Network`) and a missing repository (`RemoteNotFound`) are each classified apart
   from an authentication failure.
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
19. Two databases never share a clone or a lock, even when their files are in one directory, and one remote spelled two ways
    gets two clones.
20. No message, result or exception carries a credential from a remote address.
21. The secret scan can run the token and private-key detectors without the email detector, and the benchmark-corpus guard
    behaves as before.
22. The existing automated checks and the release build pass, with no new build warning.
23. The Core, Infrastructure and root contributor guides describe the new pieces (see "Documentation").
