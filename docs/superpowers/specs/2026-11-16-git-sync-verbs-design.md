# Git sync, milestone 1a-3: the `sync init`, `join`, `leave` and `status` commands

Date: 2026-11-16. Milestone 1a-3 of the git sync feature tracked in #93 (0a is #100, 0b-1 is #105, 0b-2 is #111, 0b-3 is #118,
1a-1 is #120 / PR #126, 1a-2 is #128 / PR #131). The umbrella design is `2026-11-15-git-sync-design.md` (see "Setup flow and the setup
skill", "Remote identity scopes all of it", "Safety checks (`RepoGuard`)", "Configuration", "Failure handling" and "Testing").
Milestone 1a was split in three. 1a-1 built the transport (`GitClient`, `SyncLock`, the tree reader), 1a-2 the guards and the
`git` storage mode. This milestone adds the four commands that move a machine into and out of a sync repository. After it a user
can set a machine up against a repository by hand; **nothing syncs yet** (the cycle is milestone 1b) and **nothing is advertised**
(the commands are hidden from `--help`, and no user-facing document or message mentions them, until milestone 1b-iii has merged).

## Problem

A sync repository is only useful if a machine can be pointed at it safely. Pointing is the dangerous moment: the first machine
must create a repository's identity (a random `repo_id` in `manifest.json`) without ever writing into a repository that already
holds something else, and every later machine must adopt that identity without carrying one repository's bookkeeping into
another's. The checks exist (1a-2) and the transport exists (1a-1); no command uses them, and no command writes the identity
they read. Until one does, `[storage] mode = "git"` can only be reached by editing the config by hand and inventing a
`repo_id`.

## Goals

1. `total-recall sync init --url <repository>` (the first machine): checks the preconditions, refuses a public GitHub
   repository and a repository that is not empty, creates the identity and the first commit on an explicitly named branch, pushes
   it, and records the identity and the settings.
2. `total-recall sync join <repository>` (every other machine): clones, reads and judges the manifest, and records the identity
   and the settings; the same repository under a respelled address, or after the data directory moved, keeps its state.
3. `total-recall sync leave`: back to the local mode, deleting nothing.
4. `total-recall sync status`: what this machine knows about its sync setup, with no network call, no write and nothing created.
5. The `git_sync.branch` rule agrees with git: a branch named `HEAD` is refused (deferred from 0b-3).
6. The commands are hidden: not in any help text, user-facing document or refusal message, until milestone 1b-iii.

## Non-goals

- Fetching, applying, pushing records, the first cycle, `sync now`, `sync review`, `sync unblock`: milestone 1b. A machine that
  has run `init` or `join` is configured and holds a clone; it does not exchange a single memory. (The cycle of 1b must point
  `origin` at the configured remote before it fetches, as `join` does here; it is idempotent.)
- `gh` (creating the repository, the default name `total-recall-sync`, `--name`, `--use-existing`, the `owner/name`
  shorthand for `join`), `sync doctor`, `--json` and the distinct exit codes, the `sync-setup` skill and `docs/git-sync.md`:
  milestone 4. The commands here take a repository address only, print plain text, and exit 0 (done), 1 (refused or failed) or
  2 (usage). Each refusal carries a stable name (decision 3) so milestone 4 can map it to a code without changing this code.
- The fuller `status` of the umbrella (last success, blocked entries, pins awaiting review, conflicts, held deletions):
  milestone 1b-iii adds them to this command.
- Any change to a refusal text, a help text, the catalog, the web UI or a user-facing document that would make the feature
  visible. In particular the `config set` refusal keeps its 1a-2 text: it does not name the `sync` commands yet.
- Making the config write atomic or mode-preserving: the verbs write `config.toml` exactly as `config set` does today (one in-place
  write of one rendered text). A hardening of that writer is its own change.
- Stopping an already running MCP server when `leave` runs (the server keeps the settings it started with; nothing syncs in this
  build, and the next start picks the change up), encryption, more than one remote per data directory, deleting a remote or a
  clone.

## Decided here

1. **The set of commands.** Group `sync`, leaves `init`, `join`, `leave`, `status`, in
   `src/TotalRecall.Cli/Commands/Sync/`. `ICliCommand` gains a default member `bool Hidden => false`. A hidden command still
   dispatches (`total-recall sync status`) and answers its own `--help`, but is left out of the top-level help, and a group whose
   commands are all hidden is left out too; the group help (`total-recall sync --help`) lists the commands: it is only
   reached by someone who typed `sync`. The commands are thin: argument parsing and output only. Everything else is the
   `SyncSetupService` in `src/TotalRecall.Infrastructure/GitSync/`, whose collaborators are all injected (the store opener, the
   settings, the user config path, the git process, the executable lookup, the public probe, the clock, a random source, the
   host identity, the app version, the case-sensitivity flag), so a test drives a command against a real bare repository and a
   real store without touching the environment. In production the case flag is `OperatingSystem.IsWindows() ||
   OperatingSystem.IsMacOS()` (as `HostIdentity.ForCurrentMachine` does), so every milestone computes the same paths.
2. **Arguments.** `sync init --url <repository> [--branch <name>]`, `sync join <repository> [--branch <name>]`, `sync leave`,
   `sync status`. The branch is `--branch`, else the configured `git_sync.branch`, else `main` when none is configured; a
   configured branch that the settings reader refused (`Branch = None`) is `bad-branch` unless `--branch` is given. An unknown
   flag, a missing `--url` or address, or a second positional is a usage error (exit 2, usage on standard error). There are no environment
   overrides of the sync settings (the umbrella); the variables every command already honours (`PATH`, `TOTAL_RECALL_HOME`,
   `TOTAL_RECALL_DB_PATH`) are honoured.
3. **Output and refusals.** Plain text, no colour. Results go to standard output. Standard error carries the
   notices and git's forwarded progress (decision 15) as they happen (the service reports them through a `report` callback, not in
   its result), then **one refusal line** `refused: <name>: <text>` (exit 1), then any
   `note:` lines (decision 11). The names are `no-git`, `git-too-old`, `wrong-mode`, `bad-remote`, `bad-branch`, `public-repo`,
   `repo-exists`, `not-empty`, `nothing-to-join`, `not-a-sync-repo`, `invalid-manifest`, `format-too-new`, `host-unverified`,
   `busy`, `needs-auth`, `unreachable` and `failed`. `status` is exempt from this catch-all: it always exits 0 and prints `unknown (<reason>)` for a fact it could not read
   (decision 8). For `init`, `join` and `leave`, any exception that is not one of these cases (a malformed `config.toml`, a
   bad `TOTAL_RECALL_DB_PATH`, a missing native library, file locking switched off, a config value the TOML writer cannot
   write) is caught at the phase that ran, logged with `ExceptionLogger.LogChain`, and reported as `failed` with the phase name
   and the exception's message; no address appears in them (git failures are already redacted by the client; the other exceptions
   carry paths). The refusal text never holds an address other than its display form (decision 14).
4. **Preconditions, in this order, for `init` and `join`** (cheap and local first; the network last). A failure at any step ends
   the command with the named refusal before the next step runs:
   1. the address is usable: `SyncGit.validateRemote(OperatingSystem.IsWindows(), address)`, else `bad-remote`; and it carries
      **no credential** (`SyncSetup.addressRefusal`, with the one reader `SyncSetup.userInfoOf` that `displayRemote` shares). For a
      `scheme://` address the authority ends at the first `/`, `?`, `#`, quote or whitespace after `://`, and the user-info is the
      text before the **last** `@` of the authority; for an scp-like address it is the text before the first `@` when that `@`
      comes before the first `:`; a path has none. A `http`, `https`, `git` or `file` address with any user-info is refused, and
      so is any other address whose user-info holds a `:` (a login such as `git@` stays allowed), with `bad-remote` and "an
      address must not hold a password or token: use an ssh key or a git credential helper", because the address is stored in
      `config.toml`, `_meta` and the clone's `.git/config` and the umbrella says total-recall stores no token. A `scheme://`
      address containing `?` or `#` is refused too (`bad-remote`, "an address must not contain a query or fragment"). An `@` after
      the authority (`https://host/o/repo@v2.git`) or after the first `:` of an scp-like address (`host:team/repo@2.git`) is not
      user-info and is accepted. The branch is usable (`GitSyncSettings.isUsableBranch`, decision 13), else `bad-branch`. The new
      config text is **dry-run rendered** here (`IUserConfigFile.Render` with the three keys, result discarded, no side effect), so
      a value the TOML writer cannot write fails now, before any network call, as `failed`;
   2. the **mode**: the effective mode (`SyncSetup.effectiveMode`, the same reduction `ServerComposition.OpenConfigured` makes: the
      configured mode, else `postgres` when only a connection string is set, else `local`) must be exactly `local` or `git`;
      anything else (`cortex`, `postgres`, any other text, any other letter case) is `wrong-mode` with the mode named and "set
      `storage.mode = "local"` in config.toml first, then run this command again" (decided with the owner: leaving Cortex or
      Postgres is always the user's own act, because under Postgres the memories are not in the local store that git sync reads
      and under Cortex the Cortex sync would stop);
   3. `git` is found (`GitExecutable.TryFind`) and runs (`VersionAsync`): `no-git`, `git-too-old`;
   4. the host fingerprint is available (`HostIdentity.Fingerprint` is not null), else `host-unverified` (a machine whose
      identity source fails could never sync);
   5. **the lock**: `GitSyncPaths.EnsureCreated`, then `SyncLock.TryAcquireAsync` on `LockPath` with a 3-second wait, else `busy`.
      Everything from here on runs under the lock, and the lock is released when the command ends, however it ends. **The store is
      opened after the lock** (decision 16), so a refused precondition never creates or migrates the database; the stored identity
      is read then.
   6. **the public check**: `RepoGuard.CheckPublicAsync` for the address, on a guard the service builds in memory for the
      address and branch being set up (the 1a-2 guard takes its settings from a `GitSyncConfig` value; the user's config is not
      changed until the record step). `Public` is `public-repo`; `Private` goes on; `Unknown` and `Skipped` print their notices
      (one line each) and go on. It runs before any git network command, so a public repository is never even contacted by git.
      A `Private` answer is cached in `_meta` (`git_sync_public_check`, 1a-2): that row is the one thing a later refusal may
      leave behind in the database (decision 11).
5. **`init` after the preconditions.**
   1. **Look at the remote without cloning it** with one call: `GitClient.LsRemoteRefsAsync(url)` (decision 12) returns every
      ref name. A failure is classified by `SyncGit.classifyFailure`: `Auth` is `needs-auth`, `Network` and `TimedOut` are
      `unreachable`, `RemoteNotFound` is `unreachable` with the text "the repository was not found or is not readable" (an
      existing repository the user cannot read looks the same, by design), `OutputTooLarge` is `not-empty`, anything else `failed`.
   2. A ref `refs/heads/<branch>` is among them: `repo-exists` ("this repository already has a `<branch>` branch; if it is your
      sync repository, run `sync join` instead"). It does not clone to see what is there; the umbrella's richer report is
      `sync doctor`, milestone 4. Any other ref (another branch, a tag, a pull-request ref; `HEAD` alone is ignored) is
      `not-empty`. Only a repository with **no ref at all** goes on.
   3. **Build the first commit locally.** An existing clone directory for this address is moved aside (decision 9, label
      `unpushed`). Then `InitAsync(branch)` (an explicit `-b <branch>`, so `init.defaultBranch` never matters),
      `SetOriginAsync(address)`, the files `.gitattributes` (the bytes `* -text` and a newline) and `manifest.json`
      (`SyncManifest.render repoId createdBy createdAt`, with a fresh `repoId` = 16 bytes from the injected random source as 32
      lowercase hex digits; `createdBy` = `total-recall <version>`; `createdAt` = the injected clock's UTC time to the second)
      written into the clone directory with exact bytes, `StageAsync(Attributes)`, `StageAsync(Manifest)`,
      `CommitAsync("Initialise the total-recall sync repository")`. The client's own `-c` overrides (no hooks, no signing, fixed
      author, `core.autocrlf=false`) apply, so the files in the commit are the same on every platform whatever the user's global
      git settings are.
   4. **Push.** `PushAsync(branch)`: `HEAD:refs/heads/<branch>`, never forced. A `NonFastForward` (someone pushed between the look
      and the push) is `repo-exists`. Other failures are classified as in step 1; after a `TimedOut` or `Network` failure the
      text adds "the push may have completed: if running this again says the repository already exists, run `sync join`". After
      any failure nothing is recorded and the config is untouched; the local clone, holding one unpushed commit, stays and is
      moved aside by the next `init`.
   5. **Record** (decision 10). The result says the repository was initialised and that **syncing memories is not part of this
      build yet** (a plain sentence; it is true, and it is what a user who found the hidden command needs to read).
   6. A failure to record after a successful push (the database or the config file cannot be written) is `failed` with the text
      "the repository was initialised but this machine could not record it: run `sync join <address>`" (the repository now has
      its manifest, so `join` is exactly the remedy).
6. **`join` after the preconditions.**
   1. `LsRemoteAsync(Url, branch)`: null is `nothing-to-join` ("the repository has no `<branch>` branch: use `sync init` on a
      repository you want to start, or check the branch name"); failures are classified as `init` step 1, except that `OutputTooLarge` is `failed`.
   2. **Get a clone** at `GitSyncPaths.For(db, address, caseFlag).CloneDirectory`. None there: `CloneAsync(address, branch)`. The
      clone directory absent means clone. One **present** (a second `join` of the same address) is used only when its `.git` is
      sound: `RemoveStaleLocks()`, `SetOriginAsync(address)`,
      `FetchAsync(branch)`. A failed fetch of kind `Network`, `TimedOut` or `Auth` is `unreachable` or `needs-auth` (as step 1) and
      the clone is kept untouched; `RemoteNotFound` is `unreachable` and `RemoteBranchMissing` is `nothing-to-join` (the same classification as step 1 of `init`, except that `OutputTooLarge` is `failed`); any other kind
      (`NonFastForward`, `OutputTooLarge`, `Cancelled`) is `failed` with the clone kept. A **damaged** clone (a directory without a sound `.git`, a
      failure of `SetOriginAsync` or of the later `ResetHardAsync`, or a fetch of kind `NotARepository` or `Other`) is moved aside
      (decision 9, label `broken`) and cloned afresh. Whether a clone that was already there is **being used** is remembered (`reusedClone`: false for a fresh clone, and false after a
      damaged one was moved aside and cloned afresh).
   3. **Read the manifest from the clone at the remote's revision**: `RepoGuard.ReadManifestAsync(RemoteBranch branch)` on the
      in-memory guard of decision 4.6. `NoManifest` is `not-a-sync-repo`; `Invalid` and `Unreadable` are `invalid-manifest` with
      the guard's reason; `Newer` is `format-too-new` ("this repository was written by a newer total-recall; update
      total-recall"). Only `Ok` goes on. After a clone that was then refused, the clone stays (a cache) and nothing is recorded.
   4. `ResetHardAsync(RemoteBranch branch)` makes the clone's working tree and `HEAD` equal the remote branch (a clone made by
      `CloneAsync` already is; a refreshed one is brought level, so `EvaluateIdentityAsync` at `Head` reads the same manifest).
      No `clean` runs: nothing in the clone is deleted by hand.
   5. **Same repository or not.** The manifest's `repo_id` is compared with the stored `git_sync_repo_id`, and the
      branch with the previously configured one (`git_sync.branch`, `main` when unset): `SyncSetup.isSameRepository storedId
      manifestId previousBranch branch` (`previousBranch` is an option: `None`, when the configured branch was refused, is a different
      repository). Both equal means the same repository (a respelled address, or this data directory moved or
      copied: the rows are kept, decision 10); anything else, including nothing stored and a different branch (the last-synced
      commit belongs to the old branch), means a different repository (the state is cleared). When it is a different repository **and `reusedClone`** (an earlier clone of this very address, which may hold the
      old repository's `refs/total-recall/last-synced`), that clone is moved aside (decision 9, label `left`) and the address is
      cloned afresh and read again (a manifest whose id then differs from the first read is `failed`: "the repository changed
      while joining"). A different repository at a different address leaves its old clone to the record step.
   6. **Record** (decision 10, which also places the clone), then the same plain sentence as `init`.
7. **`leave`.** Its paths come from the database path alone (decision 17), so it works when `[git_sync] remote` is missing or
   unusable. If the effective mode is not exactly `git`: print "this machine is not in git mode; nothing changed" and exit 0.
   Otherwise take the lock (3-second wait, `busy`), write `[storage] mode = "local"` (decision 10's config write), print what
   it did and what it left ("your memories, this machine's sync state and the repository are untouched; `[git_sync]` is kept, so
   `sync join <address>` later restores git mode"), exit 0. It does not read the repository, the clone or the network, and it
   works with `git` missing.
8. **`status`.** Read-only: no network, no lock, **nothing created** (no database file, no directory; a database that does not
   exist is opened by no one and its facts print as `not recorded`), works in every mode and before any setup. It prints, one per
   line, the labelled facts: the effective storage mode; whether `git` is found and its version; the configured remote (display
   form) and branch; the stored remote (display form), `repo_id` and machine id, and whether the host check matches
   (`HostIdentity` against the stored `git_sync_host`: `matches`, `differs`, `unavailable`, or `not recorded`); the join state
   with its reason (`SyncIdentity.reasonText`), from `RepoGuard.EvaluateIdentityAsync` (so a newer or damaged manifest in the clone
   is shown, and no git network command runs); whether a clone exists and where (only asked when a remote is configured: with
   none, `no clone`); and "last sync: never (this build does not run the sync cycle)". Exit 0 whatever it finds; 2 for usage. A
   failure of any single probe prints that line as `unknown (<reason>)` and carries on.
9. **Never delete: sidelining.** A clone directory that must not be used (an earlier `init`'s unpushed clone, a damaged clone, a
   different repository's clone, the redundant fresh clone of a same-repository join) is **renamed** to
   `<name>.<label>-<UTC yyyyMMddHHmmss>` next to it (`label` is `unpushed`, `broken`, `left` or `superseded`; a suffix `-2`, `-3`
   makes the name free); nothing is removed. The lock lives outside the clone (1a-1), so the rename works on Windows. Nothing
   deletes those directories; `docs/git-sync.md` (milestone 4) will say they may be removed. A rename that fails (a locked
   directory) in `init` step 3 or `join` step 2 or 5 is `failed` before anything is recorded; in decision 10's placement step it is
   a `note:` line (below).
10. **Record.** In this order, and the order is the safety:
    1. **Place the clone** (never run for `leave`). (a) A different repository than the stored one, with `git_sync_remote` set,
       another address and an existing clone directory for it: that old clone is moved aside (label `left`), so a machine never
       carries one repository's `L` into another's pull. (b) The same repository under a **different address spelling** than the
       stored `git_sync_remote`, with an old clone that exists: whatever is at the new address's clone directory (a fresh clone, or
       a reused earlier one) is moved aside (label `superseded`), the old clone directory is renamed to the new address's clone path
       (`Directory.Move`; the two `<cloneKey>` directories are siblings under the sync directory, and the new one exists or is
       created first), and `SetOriginAsync(newAddress)` re-points it, so the commit `L` and its protective ref survive. A failure of
       any of these steps is a `note:` line and not a failure (the identity is still recorded, and a later cycle with no `L` is an
       unproven apply that deletes nothing); a `SetOriginAsync` failure after the move is noted too, and `join` re-points `origin`
       every time it reuses a clone (step 6.2), so a retry repairs it. The placement runs **before** the identity is written so the
       stored remote still names the old clone and a crash here is repaired by running the command again.
    2. **The database, in one SQLite transaction** on the store's connection (`BeginTransaction` of `Microsoft.Data.Sqlite`
       directly: the store is opened by this command alone, so the guard of `SqliteStore.BeginTransaction` is not needed):
       - **`init`, or `join` of a different repository:** delete every row of `git_sync_entry`, `git_sync_issue` and
         `git_sync_alias`, every row of `git_sync_review` whose `kind` is not `conflict` (a conflict row holds the only copy of a
         losing text: it is kept and stays listed), and the `_meta` keys `git_sync_last_sha`, `git_sync_last_ok`,
         `git_sync_last_error` and `git_sync_arrivals` (per-repository state; `git_sync_hlc` and `git_sync_clock_warning` are kept);
         write `git_sync_remote`, `git_sync_repo_id`, `git_sync_host` (`HostIdentity.Fingerprint`) and `git_sync_machine` (a new
         random id).
       - **`join` of the same repository:** clear nothing; write `git_sync_remote` and `git_sync_host`; keep `git_sync_repo_id`;
         keep `git_sync_machine` only when one is stored **and** the stored `git_sync_host` equals the current fingerprint (a
         respelled address on the same machine), and write a **new** machine id otherwise (none stored, or the host differs or is not
         recorded: a moved or copied data directory is a different clone; the rows and the last-synced commit are kept, the machine
         id is not shared).
       The machine id is **8 lowercase hex digits** (4 random bytes, the shape `SyncStamp.MachineId.tryCreate` accepts); the repository
       id is 32. `SyncIdentity.MetaKeys` gains the constants `LastSha`, `LastOk`, `LastError` and `Arrivals`, and every key name used
       here comes from it. No memory table is touched.
    3. **The config, last:** the user config text is rendered **now** from the file's current content (`IUserConfigFile.Render`:
       so a `config set` that ran meanwhile is kept) and written once (`IUserConfigFile.Write`: it creates the config directory when missing, then `File.WriteAllText`, as
       `ConfigWriter.SaveUserOverride` does today). `init` and `join` set three keys (`storage.mode` = `git`, `git_sync.remote` =
       the address as given, `git_sync.branch`); `leave` sets `storage.mode` = `local`. The three keys change in one write. The file is
       rewritten by the existing hand-rolled TOML writer, which does not keep comments (`config set` already behaves this way; the
       contributor guide notes it).
    Crash windows: after 1 but before 2, the old state is intact except for a moved clone, and running the command again repairs it
    (the stored remote still names the old clone). After 2 but before 3, the stored identity names the new repository while the
    config still names the old address, branch or mode: the 1a-2 guard reports `not-joined` (`RemoteChanged`) or, in the local mode,
    nothing runs; running the command again completes it (for `init`, whose push already happened, `sync join` is the command to run). A cycle needs the identity, the config and the manifest to agree (1a-2),
    so no state here lets one act wrongly.
11. **What a refusal changes.** Before the lock (decision 4.5) nothing is changed. After the lock and before the record, the only
    possible changes are the sync directory (its lock, `no-hooks` and `empty` files), the database file itself (created and migrated
    when the store is opened), the clone directory (a created, refreshed, re-pointed or sidelined clone: a cache), the public-check
    cache row (a `Private` answer), and, for `init`, the remote repository after a successful push. A refusal says which of these
    happened in `note:` lines after it (`note: a clone was kept at <path>`, `note: the repository was initialised`). A snapshot of
    the memory and sync tables taken before and after a refusal differs at most in `git_sync_public_check`.
12. **`LsRemoteRefs`.** A new case of the closed `SyncGit.GitCommand` set: `LsRemoteRefs of url: string`, arguments
    `["ls-remote"; "--"; url]` (no `--heads`, so tags and other refs count), a network command (the same timeouts, environment and
    output bound as `LsRemote`), not clone-scoped, address checked as `Clone`'s. `SyncGit.parseLsRemoteRefs` returns the ref names
    (lines that are not `<sha>\t<ref>` are ignored). `GitClient.LsRemoteRefsAsync(url)` returns them. The all-commands
    `ForceFlags.AssertNone` test covers it. It is how `init` tells a repository with no ref at all from every other.
13. **The branch rule.** `GitSyncSettings.isUsableBranch` also refuses the exact text `HEAD`: it is the one name the rule accepts
    that `git check-ref-format --branch` rejects, and a branch named like the symbolic ref is ambiguous. A `[git_sync] branch =
    "HEAD"` is then corrected like any unusable branch (`Branch = None`, one warning), `--branch HEAD` is `bad-branch`, and
    `OpenGit`'s existing branch check refuses it. A test compares `isUsableBranch` with `git check-ref-format --branch` over a table
    of names (real git, `[GitFact]`): every name `isUsableBranch` accepts must be accepted by git, so no other name can pass here
    and fail later.
14. **Display form of an address.** `SyncSetup.displayRemote` (any letter case of the scheme, any scheme including `ftp` and `ftps`): the user-info is read as in
    decision 4.1. A user-info holding `:` is shown as
    `<text before the colon>:***`; a user-info without `:` on an `http`, `https`, `git`, `ftp` or `ftps` address is shown as `***`;
    any other form (`ssh://git@host/...`, `git@host:o/r`, a path) is shown as written. `init` and `join` refuse such addresses
    (decision 4.1), so this protects the values a user hand-edited into `config.toml`. Every line that prints an address, in any
    command, uses it. Together with the 1a-1 redaction of git's standard error, no command prints a credential.
15. **Progress.** Git's own redacted standard error lines for the network steps are forwarded as they come (the client's
    `onStderr`), prefixed with two spaces, to standard error. Only `fetch` prints a progress meter (the command set adds no
    `--progress` to `clone` or `push`: they print one or two lines), so a slow first clone is quiet; nothing else is designed here.
    A failure to write to standard output (a closed pipe) is ignored: it never changes the exit code of the operation.
16. **Where the store is opened.** `init` and `join` open the SQLite database **after the lock** (decision 4.5) at
    `ConfigLoader.GetDbPath()` (the same file as every other command, honouring `TOTAL_RECALL_DB_PATH`) with
    `SqliteConnection.Open` and `MigrationRunner.RunMigrations`, and wrap the connection in the borrowed-connection constructor of
    `SqliteStore`. They do **not** use `new SqliteStore(dbPath)`, whose `CleanupOrphanRows` sweep deletes content rows that lack a
    vector row: these commands touch no memory table, not even to repair one. They do not use `MemoryComponents.OpenProduction` (it
    would open the embedder and, in `git` mode, run `OpenGit`). Before opening, they refuse with `failed` ("this database has not
    been migrated yet: start total-recall once first") in the states `AutoMigrationGuard` would still act on, so the schema runner
    never stamps a database that has not been migrated from the old TypeScript format: (a) the file exists, has at least one table
    and no `_schema_version` table; (b) `<db>.ts-backup` exists and the file is absent, or `_meta` lacks the
    `migration_from_ts_complete` key (`MigrationRunner.MigrationCompleteMarkerKey`). The probe is plain read-only SQL in
    Infrastructure (the guard itself lives in the Server project, which the CLI does not reference). `leave` needs no store.
    `status` never opens the database read-write (it would migrate, sweep and switch the journal mode): when the file exists and
    neither `<db>-wal` nor `<db>-shm` does, it opens it `immutable=1` (read-only and creating nothing, even in a read-only
    directory); when a sidecar exists a writer is live, so it opens `Mode=ReadOnly` (which adds nothing); both with `Pooling=False`
    and plain `Microsoft.Data.Sqlite` (no extension, pragma or migration). It reads `_meta` with plain SQL through a
    `ReadOnlyMeta : IMetaStore` whose `SetMeta` and `DeleteMeta` throw, and treats a missing file, a missing `_meta` table or any
    read error as `not recorded` or `unknown (<reason>)`; with no file it passes the guard an empty `ReadOnlyMeta`. The service
    takes a store opener, so tests pass a temporary one.
17. **Paths without a remote.** `GitSyncPaths` gains `ForDatabase(dbPath, caseInsensitive)`: the same `SyncDirectory`, `LockPath`,
    `NoHooksDirectory`, `EmptyFile` and `EnsureCreated` as `For`, and no clone directory (`CloneDirectory` throws
    `InvalidOperationException`). `leave` uses it for the lock. `status` builds its `GitClient` (it needs one for `git --version`,
    and `GitClient` reads the clone directory on every call) over `GitSyncPaths.For(db, remote ?? "none", caseFlag)`, which creates
    nothing, and probes the clone and the manifest only when a remote is configured. `init` and `join` use `For`.
18. **Seams for tests.** The service takes `IUserConfigFile { string Render(IReadOnlyList<(string Key, object Value)> keys); void
    Write(string text); }` (production: `ConfigWriter.RenderUserOverrides(path, keys)`, which has no side effect, and `Write`, which
    creates the parent directory and then calls `File.WriteAllText`, as `SaveUserOverride` does) and a fault hook
    `Func<string, Task>` called with the names `after-placement` and `after-database`, so a test can fail the config write and
    throw between the record steps without relying on file permissions (which do nothing when tests run as root).

## Design

### Core (pure, F#)

- `SyncSetup.fs` (new, after `SyncSettingsGuard.fs`): `effectiveMode (mode: string option) (connectionString: string option) :
  string`, `modeVerdict (mode: string) : Result<unit, string>` (decision 4.2), `userInfoOf (address: string) : string option`, `addressRefusal (address: string) : string option`
  (4.1), `displayRemote (address: string) : string` (14), `isSameRepository (storedId: string option) (manifestId: string) (previousBranch: string option) (branch: string) : bool`,
  `keepMachineId (storedMachine: string option) (storedHost: string option) (currentHost: string) : bool`, `newRepoId (bytes: byte[]) : string` (16 bytes to 32
  lowercase hex; throws on another length), `newMachineId (bytes: byte[]) : string` (4 bytes to 8 lowercase hex; throws on another
  length), the refusal names as a DU `Refusal` with `name : Refusal -> string`, and the status rendering `renderStatus
  (StatusFacts) : string list` (a record of option/DU values the service fills in).
- `SyncGit.fs`: the `LsRemoteRefs` command, its argument list, an explicit entry in `isCloneScoped`'s **false** group (the match ends
  in `| _ -> true`, so a forgotten case would silently be clone-scoped), the address check, `parseLsRemoteRefs`.
- `SyncIdentity.fs`: the four new `MetaKeys` constants.
- `Config.fs`: `isUsableBranch` refuses `HEAD`.

### Infrastructure (C#)

- `GitSync/SyncSetupService.cs`: `InitAsync`, `JoinAsync`, `LeaveAsync`, `StatusAsync`. Results are records (`SetupResult`: the
  `Refusal?` with its text, the `note:` lines, the lines to print); notices and git progress go through a `report` callback as they
  happen; the service prints nothing itself, the commands do.
- `GitSync/GitSyncStateStore.cs`: `ClearForNewRepository(tx)` and `WriteIdentity(tx, ...)`, plain SQL on the store's connection;
  table names from one place.
- `GitSync/CloneSideline.cs`: `Move(directory, label, clock)`; never deletes; returns the new path.
- `GitSync/GitSyncPaths.cs`: `ForDatabase`.
- `GitSync/GitClient.cs`: `LsRemoteRefsAsync`.
- `Config/ConfigWriter.cs`: `RenderUserOverrides(path, keys)`, which `SaveUserOverride` now also uses (render, then the same
  `File.WriteAllText`); the denylist stays in the callers of `config set`, and the verbs are the only other writers, writing only
  the keys above.
- `GitSync/ReadOnlyMeta.cs`: the read-only `IMetaStore` of decision 16; `GitSync/LegacyDatabaseProbe.cs`: its read-only migration probe.

### CLI

- `Commands/Sync/{Init,Join,Leave,Status}Command.cs`, registered in `CliApp.BuildRegistry`; `ICliCommand.Hidden`; `PrintTopLevelHelp`
  and `PrintGroupHelp` honour it as in decision 1.

### Documentation

`src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md` and the root `AGENTS.md` ("WHERE TO LOOK" and the git sync
paragraphs) describe the new pieces and say the commands are hidden until milestone 1b-iii (contributor guides, which are not
user-facing). `CHANGELOG.md` gets **no entry** (nothing is advertised and no existing behaviour changes); the `Unreleased` section is left as it is. No file under `docs/` or `README.md` changes, and
`scripts/host-docs.test.js` still passes.

## Testing

Real git throughout, `[GitFact]`, against bare repositories in temporary directories (a local path and a `file://` address), a
temporary store, the stubbed public probe of 1a-2, an injected clock and random source, and a user git configuration made hostile
with `GitTestEnvironment.WriteGitConfig` (`commit.gpgsign=true`, `core.hooksPath=<a directory with a failing hook>`,
`init.defaultBranch=trunk`, `core.autocrlf=true`, `user.useConfigOnly=true`).

- **init**: an empty bare repository gets exactly one commit on the branch with `.gitattributes` and a manifest that
  `ManifestFile.Parse` accepts and whose `repo_id` is the stored one; the config file has the three keys; the identity rows exist
  and `RepoGuard.EvaluateIdentityAsync` is `joined` with `CanSync` true; the same files with a hostile global configuration;
  `repo-exists` for a repository with the branch (nothing pushed, nothing recorded); `not-empty` for a repository with only another
  branch, one with only a tag, and one with only a `refs/pull/1/head`; a push that fails (a bare repository whose `pre-receive`
  hook rejects) leaves the remote empty, records nothing and a second `init` against a good repository succeeds and sidelines the
  first clone; a non-fast-forward race (a second `init` pushed between the look and the push, driven with a recording process
  wrapper) is `repo-exists`; a fake process reporting `Auth` is `needs-auth`, `TimedOut` is `unreachable` with the "may have
  completed" text, a nonexistent path is `unreachable` with the not-found text.
- **join**: a repository made by `init` (from a second data directory) is joined: clone, manifest read, identity stored, config
  written, the machine id is 8 hex digits, `SyncStamp.MachineId.tryCreate` accepts it and it differs from the first machine's; no
  branch, no manifest, an invalid manifest and a newer-format manifest are refused with nothing recorded and the config untouched;
  a different repository replaces one already joined (rows cleared, conflict rows kept, memory rows untouched, the old clone
  sidelined as `left`); **a repository recreated at the same address** (a different `repo_id` there) sidelines the reused clone,
  clones afresh, and the protective ref of the old clone is not in the new one; the same repository under a respelled address
  keeps the rows, `git_sync_repo_id` and machine id, moves the old clone to the new path and keeps `L` (a commit written to the
  protective ref survives) with `origin` re-pointed; a data directory that moved (host stored differently) keeps the rows and
  `L`, re-stamps the host and **takes a new machine id**; a second `join` of the same address refreshes the clone; a fetch that
  fails with `Network`, `Auth` or `TimedOut` keeps the clone and reports `unreachable`/`needs-auth`, while a damaged clone is
  sidelined as `broken` and cloned afresh; a crash simulated after the placement and after the transaction (the fault hook of decision 18) and a failing
  config write (a fake `IUserConfigFile`) are each repaired by running the command again; a fetch against a clone with a stale
  `.git/index.lock` works (stale locks are removed first); a reused clone whose `.git` is missing, or whose `reset` fails, is
  sidelined as `broken`; **a same-repository `join --branch <other>`** clears the bookkeeping like a different repository.
- **public / mode / host / address**: `public-repo` (a 200 from the stub) with no git network command run (the recording process
  shows only `--version`); 404 and unknown proceed, the unknown with its one notice; `wrong-mode` for `cortex`, `postgres`, a
  connection string alone, an upper-case mode and an unknown text, with nothing changed, and `local` and `git` accepted;
  `host-unverified` when the machine-id source fails; `busy` with the lock held by another handle, and the lock released after
  success and after every refusal; `no-git` with an empty `PATH`; an address with `https://token@host/o/r`,
  `https://user:pass@host/o/r`, `ssh://git:ghp_x@host/o/r`, `file://tok:pw@host/tmp/x.git` and `https://host/o/r?token=x` is
  `bad-remote`; `git@host:o/r`, `ssh://git@host/o/r`, `https://host/o/repo@v2.git` and `host:team/repo@2.git` are accepted. A
  refusal of steps 4.1 to 4.4 leaves no database file, no migration and no sync directory; `init` and `join` refuse a database in
  the old TypeScript format (a file with tables and no `_schema_version`, and a `.ts-backup` beside an absent or unmarked database)
  without changing a byte of it; an `init` or `join` leaves a content row that has no vector row in place (no orphan sweep).
- **leave / status**: `leave` in git mode writes `local` and leaves every row, `_meta` key, clone and the repository as they were
  (a snapshot comparison); not in git mode it changes nothing; it works with `git` missing and with `mode = "git"` and no remote
  configured. `status` before any setup (no database file, no sync directory: both still absent afterwards; the git
  version still printed) and on an existing database that is closed cleanly (the directory listing and the file's bytes identical
  before and after: no `-wal`, `-shm`, migration or orphan sweep), also in a read-only directory, and on a database in the old
  TypeScript format, after `init`, after
  `leave`, with a damaged manifest in the clone, with a changed remote and with a moved data directory prints the matching join
  state, runs no git network command and never prints a credential (a hand-edited address `https://token@host/x`, and the same in
  every refusal, note and forwarded progress line of `init` and `join`).
- **CLI**: argument errors exit 2; `--help` of a command prints usage; the top-level help is byte-identical to before (a test
  records it); `total-recall sync` dispatches; the help of every other command, the `config set` refusal text and the `session_start` result contain no
  `sync init`, `sync join`, `sync leave` or `sync status` (the 1a-2 `reasonText` is only shown by `sync status` in this build); the catalog is unchanged; a malformed `config.toml`, a bad database path and file
  locking switched off give `failed` for `init`, `join` and `leave`, never an unhandled exception, and `unknown (...)` with exit 0 for
  `status`.
- **Config write**: `RenderUserOverrides` plus one write gives the same file as `SaveUserOverride` did for a single key; three keys give
  the three-key file; `config set` still behaves as before.
- **Branch rule**: the `HEAD` case in `GitSyncSettings` tests and the `git check-ref-format --branch` table.
- **No force**: a recording wrapper around every real run asserts, with the existing `ForceFlags.AssertNone` rules, that no `git`
  call of any verb carries `--force`, `-f` (other than the pinned `add -f` of `Stage`), `--force-with-lease`, `--delete`,
  `--mirror`, a `+` refspec on a push or a `:`-delete.
- **Platform**: tests that need symbolic links or `sh` keep the 1a-1 attributes; the rest run on Windows (paths from
  `Path.Combine`, `file://` addresses built from `Uri`).

## Acceptance criteria

1. Given an empty repository (no branch, tag or other ref), when `sync init --url <it>` runs, then it gets one commit on the chosen branch holding a
   `.gitattributes` and a valid `manifest.json` with a new repository id; this machine's config is set to the `git` mode with that
   address and branch; the identity is stored; and sync reports `joined`. The files in the commit are the same whatever the user's
   global git settings say (signing, hooks, default branch, line endings), without any prompt.
2. Given a repository that already has the branch, or has any other ref, when `sync init` runs, then it refuses (`repo-exists`,
   `not-empty`), and nothing is pushed, recorded or configured.
3. Given a repository made by `init` on another machine, when `sync join <it>` runs, then the manifest is validated, the identity
   (its repository id, a new machine id, the host) is stored and the mode and settings are written; given a repository with no
   such branch (`nothing-to-join`), no manifest (`not-a-sync-repo`), an invalid manifest (`invalid-manifest`) or a newer format
   (`format-too-new`), then it is refused with that name and nothing is recorded or configured.
4. Given a public GitHub repository, when `init` or `join` runs, then it is refused before git contacts it; given a private
   answer it proceeds; given an unknown answer it proceeds with the one notice.
5. Given a machine in the `cortex` or `postgres` mode, or any mode other than `local` or `git`, when `init` or `join` runs, then
   it is refused naming the mode and telling the user to set `local` first, and nothing changes; given `local` or `git`, it
   proceeds. Given an address that carries a password or token, it is refused.
6. Given a machine that already joined one repository, when `init` or `join` points it at a different repository (at another
   address, or at the same address after the repository was recreated), then the sync bookkeeping is cleared except conflict
   items, the memories are untouched, and the old repository's clone is moved aside, not deleted; given the same repository under a
   respelled address, then the bookkeeping, the machine id and the last-synced commit are kept and the clone follows the new
   address; given the same repository after the data directory was moved or copied, then the bookkeeping and the last-synced
   commit are kept and the machine gets a new machine id.
7. Given a failed push, an unreachable or unreadable repository, an authentication failure or a refused manifest, when the command
   ends, then no identity or setting was recorded, a retry works, and the output names the reason (`unreachable`, `needs-auth`, ...)
   without any credential or address user-info; a transient network failure while refreshing an existing clone keeps that clone.
8. Given `init`, `join` or `leave` (in git mode) running, when another of them runs for the same data directory, then the second
   reports `busy`; the lock is free after every ending.
9. Given a machine in the `git` mode, when `sync leave` runs, then the mode becomes `local`, every memory, sync row, clone and file
   in the repository is untouched, and the command says so; given any other mode it changes nothing; it needs neither `git`, the
   network nor a usable remote setting.
10. Given any machine, when `sync status` runs, then it reports the mode, whether `git` is found, the configured and stored remote and
    branch with any credential hidden, the repository and machine ids, the host check, the join state with its reason and the state
    of the clone; it makes no network call, creates no file or directory, and fails on nothing: an unreadable part is shown as
    unknown.
11. Given a branch named `HEAD`, when the settings are read or `--branch` is given, then it is refused; and every branch name the
    rule accepts is accepted by git itself.
12. Given anyone who never types `sync`, then `total-recall --help`, every document and every message are as before and the tool
    catalog is unchanged; and given any command, then it never overwrites or deletes anything in the repository: its only push is
    one plain, non-forced push of the new branch, and it deletes no clone, no memory and nothing in the repository (git's own stale
    lock files inside a clone aside).
13. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then they pass
    and the release build shows no new warning; and given the contributor guides, then they describe the new pieces.
