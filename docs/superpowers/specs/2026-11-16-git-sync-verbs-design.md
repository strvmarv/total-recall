# Git sync, milestone 1a-3: the `sync init`, `join`, `leave` and `status` commands

Date: 2026-11-16. Milestone 1a-3 of the git sync feature tracked in #93 (0a is #100, 0b-1 is #105, 0b-2 is #111, 0b-3 is #118,
1a-1 is #120 / PR #126, 1a-2 is #128 / PR #131). The umbrella design is `2026-11-15-git-sync-design.md` (see "Setup flow and the setup
skill", "Remote identity scopes all of it", "Safety checks (`RepoGuard`)", "Configuration", "Failure handling" and "Testing").
Milestone 1a was split in three. 1a-1 built the transport (`GitClient`, `SyncLock`, the tree reader), 1a-2 the guards and the
`git` storage mode. This milestone adds the four commands that move a machine into and out of a sync repository. After it a user
can set a machine up against a repository by hand; **nothing syncs yet** (the cycle is milestone 1b) and **nothing is advertised**
(the commands are hidden from `--help`, and no documentation mentions them, until milestone 1b-iii has merged).

## Problem

A sync repository is only useful if a machine can be pointed at it safely. Pointing is the dangerous moment: the first machine
must create a repository's identity (a random `repo_id` in `manifest.json`) without ever writing into a repository that already
holds something else, and every later machine must adopt that identity without carrying one repository's bookkeeping into
another's. The checks exist (1a-2) and the transport exists (1a-1); no command uses them, and no command writes the identity
they read. Until one does, `[storage] mode = "git"` can only be reached by editing three files by hand and inventing a
`repo_id`.

## Goals

1. `total-recall sync init --url <repository>` (the first machine): checks the preconditions, refuses a public GitHub
   repository and a repository that is not empty, creates the identity and the first commit on an explicitly named branch, pushes
   it, and records the identity and the settings.
2. `total-recall sync join <repository>` (every other machine): clones, reads and judges the manifest, and records the identity
   and the settings; the same repository under a respelled address, or after the data directory moved, keeps its state.
3. `total-recall sync leave`: back to the local mode, deleting nothing.
4. `total-recall sync status`: what this machine knows about its sync setup, with no network call.
5. The `git_sync.branch` rule agrees with git: a branch named `HEAD` is refused (deferred from 0b-3).
6. The commands are hidden: not in any help text and not in any document, until milestone 1b-iii.

## Non-goals

- Fetching, applying, pushing records, the first cycle, `sync now`, `sync review`, `sync unblock`: milestone 1b. A machine that
  has run `init` or `join` is configured and holds a clone; it does not exchange a single memory.
- `gh` (creating the repository, the default name `total-recall-sync`, `--name`, `--use-existing`, the `owner/name`
  shorthand for `join`), `sync doctor`, `--json` and the distinct exit codes, the `sync-setup` skill and `docs/git-sync.md`:
  milestone 4. The commands here take a repository address only, print plain text, and exit 0 (done), 1 (refused or failed) or
  2 (usage). Each refusal carries a stable name (below) so milestone 4 can map it to a code without changing this code.
- The fuller `status` of the umbrella (last success, blocked entries, pins awaiting review, conflicts, held deletions):
  milestone 1b-iii adds them to this command.
- Any change to a refusal text, a help text, the catalog, the web UI or a document that would make the feature visible. In
  particular the `config set` refusal keeps its 1a-2 text: it does not name the `sync` commands yet.
- Encryption, more than one remote per data directory, deleting a remote or a clone.

## Decided here

1. **The set of commands.** Group `sync`, leaves `init`, `join`, `leave`, `status`, in
   `src/TotalRecall.Cli/Commands/Sync/`. `ICliCommand` gains a default member `bool Hidden => false`. A hidden command still
   dispatches (`total-recall sync status`) and answers its own `--help`, but is left out of the top-level help, and a group whose
   commands are all hidden is left out too; the group help (`total-recall sync --help`) lists the commands: it is only
   reached by someone who typed `sync`. The commands are thin: argument parsing and output only. Everything else is the
   `SyncSetupService` in `src/TotalRecall.Infrastructure/GitSync/`, whose collaborators are all injected (the store, the
   settings, the user config path, the git process, the executable lookup, the public probe, the clock, a random source, the
   host identity), so a test drives a command against a real bare repository and a real store without touching the
   environment.
2. **Arguments.** `sync init --url <repository> [--branch <name>]`, `sync join <repository> [--branch <name>]`, `sync leave`,
   `sync status`. The branch is `--branch`, else the configured `git_sync.branch`, else `main`. An unknown flag, a missing
   `--url` or address, or a second positional is a usage error (exit 2, usage on standard error). Nothing is read from the
   environment (the umbrella: no environment overrides).
3. **Refusals have stable names**, printed as the first line `refused: <name>: <text>` on standard error and exit 1:
   `no-git`, `git-too-old`, `wrong-mode`, `bad-remote`, `bad-branch`, `public-repo`, `repo-exists`, `not-empty`,
   `nothing-to-join`, `not-a-sync-repo`, `invalid-manifest`, `format-too-new`, `host-unverified`, `busy`, `needs-auth`,
   `unreachable`, `failed`. The text never holds the repository address or any part of it other than its redacted display form
   (decision 14). A refusal changes nothing that this command had not already changed and said so (decision 11).
4. **Preconditions, in this order, for `init` and `join`** (cheap and local first; the network last):
   1. the address is usable: `SyncGit.validateRemote(OperatingSystem.IsWindows(), address)`, else `bad-remote`; the branch is
      usable (decision 13), else `bad-branch`;
   2. the **mode**: the effective mode (the same reduction `ServerComposition.OpenConfigured` makes: the configured mode, else
      `postgres` when only a connection string is set, else `local`) must be exactly `local` or `git`; anything else (`cortex`,
      `postgres`, any other text) is `wrong-mode` with the mode named and "set `storage.mode = "local"` in config.toml first,
      then run this command again" (decided with the owner: leaving Cortex or Postgres is always the user's own act, because under
      Postgres the memories are not in the local store that git sync reads and under Cortex the Cortex sync would stop);
   3. `git` is found (`GitExecutable.TryFind`) and runs (`VersionAsync`): `no-git`, `git-too-old`;
   4. the host fingerprint is available (`HostIdentity.Fingerprint` is not null), else `host-unverified` (a machine whose
      identity source fails could never sync);
   5. **the lock**: `SyncLock.TryAcquireAsync` on `GitSyncPaths.LockPath` with a 3-second wait, else `busy`. Everything from
      here on runs under the lock, and the lock is released when the command ends, however it ends. (`SyncDirectory` is created
      first with `GitSyncPaths.EnsureCreated`.)
   6. **the public check**: `RepoGuard.CheckPublicAsync` for the address, on a guard the service builds in memory for the
      address and branch being set up (the 1a-2 guard takes its settings from a `GitSyncConfig` value; the user's config is not
      changed until the record step). `Public` is `public-repo`; `Private` goes on; `Unknown` and `Skipped` print their notices
      (one line each) and go on. It runs before any git network command, so a public repository is never even contacted by git.
5. **`init` after the preconditions.**
   1. **Look at the remote without cloning it.** `GitClient.LsRemoteAsync(Url, branch)` names the branch's head or null, and a
      new command `LsRemoteHeads` (decision 12) lists every branch. A failure is classified by `SyncGit.classifyFailure`:
      `Auth` is `needs-auth`, `Network` and `TimedOut` are `unreachable`, `RemoteNotFound` is `unreachable` with the text "the
      repository was not found or is not readable" (an existing repository the user cannot read looks the same, by design),
      anything else `failed`.
   2. The branch exists on the remote: `repo-exists` ("this repository already has a `<branch>` branch; if it is your sync
      repository, run `sync join` instead"). It does not clone to see what is there; the umbrella's richer report is
      `sync doctor`, milestone 4. The branch is absent but another branch exists: `not-empty`. Only a repository with **no branch at
      all** goes on.
   3. **Build the first commit locally.** An existing clone directory for this address is moved aside (decision 9). Then
      `InitAsync(branch)` (an explicit `-b <branch>`, so `init.defaultBranch` never matters), `SetOriginAsync(address)`, the files
      `.gitattributes` (the bytes `* -text` and a newline) and `manifest.json` (`SyncManifest.render repoId createdBy createdAt`,
      with a fresh `repoId` = 16 bytes from the injected random source, as 32 lowercase hex digits; `createdBy` =
      `total-recall <version>`; `createdAt` = the injected clock's UTC time to the second) written into the clone directory with
      exact bytes, `StageAsync(Attributes)`, `StageAsync(Manifest)`, `CommitAsync("Initialise the total-recall sync repository")`.
      The client's own `-c` overrides (no hooks, no signing, fixed author, `core.autocrlf=false`) apply, so the bytes are the
      same on every platform whatever the user's global git settings are.
   4. **Push.** `PushAsync(branch)`: `HEAD:refs/heads/<branch>`, never forced. A `NonFastForward` (someone pushed between the look
      and the push) is `repo-exists`. Other failures are classified as in step 1. After any failure nothing is recorded and the
      config is untouched; the local clone, holding one unpushed commit, stays and is moved aside by the next `init`.
   5. **Record** (decision 10). The result says the repository was initialised and that **syncing memories is not part of this
      build yet** (a plain sentence; it is true, and it is what a user who found the hidden command needs to read).
   6. A failure to record after a successful push (the database or the config file cannot be written) is `failed` with the text
      "the repository was initialised but this machine could not record it: run `sync join <address>`" (the repository now has
      its manifest, so `join` is exactly the remedy).
6. **`join` after the preconditions.**
   1. `LsRemoteAsync(Url, branch)`: null is `nothing-to-join` ("the repository has no `<branch>` branch: use `sync init` on a
      repository you want to start, or check the branch name"); failures are classified as `init` step 1.
   2. **Get a clone** at `GitSyncPaths.For(db, address).CloneDirectory`. None there: `CloneAsync(address, branch)`. One there
      (a second `join` of the same address): `FetchAsync(branch)`; a failed fetch (a damaged or unusable clone) moves the
      clone aside (decision 9) and clones. The clone is a disposable cache of the remote.
   3. **Read the manifest from the clone at the remote's revision**: `RepoGuard.ReadManifestAsync(RemoteBranch branch)` built
      for this address and branch (the 1a-2 guard takes its settings from a `GitSyncConfig` value, so the service builds one in
      memory for the address being joined: the user's config is not changed yet). `NoManifest` is `not-a-sync-repo`; `Invalid`
      and `Unreadable` are `invalid-manifest` with the guard's reason; `Newer` is `format-too-new` ("this repository was written
      by a newer total-recall; update total-recall"). Only `Ok` goes on. After a clone that was then refused, the clone stays
      (a cache) and nothing is recorded.
   4. `ResetHardAsync(RemoteBranch branch)` and `CleanAsync()` make the working tree equal the remote branch (a clone made by
      `CloneAsync` already is; a refreshed one is brought level).
   5. **Same repository or not.** The manifest's `repo_id` is compared with the stored `git_sync_repo_id`
      (`SyncSetup.isSameRepository`): equal means the same repository (a respelled address, or this data directory moved: the
      state is kept, decision 10); anything else, including nothing stored, means a different repository (the state is cleared).
   6. **Record** (decision 10, which also places the clone), then the same plain sentence as `init`.
7. **`leave`.** If the effective mode is not exactly `git`: print "this machine is not in git mode; nothing changed" and exit 0.
   Otherwise take the lock (3-second wait, `busy`), write `[storage] mode = "local"` (decision 10's single config write), print
   what it did and what it left ("your memories, this machine's sync state and the repository are untouched; `[git_sync]` is kept,
   so `sync join <address>` later restores git mode"), exit 0. It does not read the repository, the clone or the network, and it
   works with `git` missing.
8. **`status`.** Read-only, no network, no lock, works in every mode and before any setup. It prints, one per line, the labelled
   facts: the effective storage mode; whether `git` is found and its version; the configured remote (display form) and branch;
   the stored remote (display form), `repo_id` and machine id, and whether the host check matches (`HostIdentity` against the
   stored `git_sync_host`: `matches`, `differs`, `unavailable`, or `not recorded`); the join state with its reason
   (`SyncIdentity.reasonText`), from `RepoGuard.EvaluateIdentityAsync` (so a newer or damaged manifest in the clone is shown, and no
   git network command runs); whether a clone exists and where; and "last sync: never (this build does not run the sync cycle)".
   Exit 0 whatever it finds; 2 for usage. A failure of any single probe prints that line as `unknown (<reason>)` and carries on.
9. **Never delete: sidelining.** A clone directory that must not be used (an earlier `init`'s unpushed clone, a damaged clone,
   the redundant fresh clone of a same-repository join) is **renamed** to `<name>.<label>-<UTC yyyyMMddHHmmss>` next to it
   (`label` is `unpushed`, `broken` or `superseded`; a suffix `-2`, `-3` makes the name free); nothing is removed. A clone of an
   old repository (decision 10's last step) is renamed the same way with the label `left`. The lock lives outside the clone (1a-1), so
   the rename works on Windows. Nothing deletes those directories; `docs/git-sync.md` (milestone 4) will say they may be removed.
10. **Record.** One call, in this order, and the order is the safety:
    1. **The database, in one SQLite transaction** on the store's connection (`BeginTransaction` of `Microsoft.Data.Sqlite`
       directly: the store is opened by this command alone, so the guard of `SqliteStore.BeginTransaction` is not needed):
       - **`init`, or `join` of a different repository:** delete every row of `git_sync_entry`, `git_sync_issue` and
         `git_sync_alias`, every row of `git_sync_review` whose `kind` is not `conflict` (a conflict row holds the only copy of a
         losing text: it is kept and stays listed), and the `_meta` key `git_sync_last_sha`; write `git_sync_remote`,
         `git_sync_repo_id`, `git_sync_host` (`HostIdentity.Fingerprint`) and `git_sync_machine` (a new random 32-hex id).
       - **`join` of the same repository:** clear nothing; write `git_sync_remote` and `git_sync_host`; keep `git_sync_repo_id`
         and `git_sync_machine`.
       The `_meta` keys are the `SyncIdentity.MetaKeys` names. No memory table is touched.
    2. **The config, in one write:** `ConfigWriter` gains `SaveUserOverrides(path, IReadOnlyList<(string key, object value)>)`
       that reads the user file once, applies every key, and writes once with the existing writer
       (so `storage.mode`, `git_sync.remote` and `git_sync.branch` change together or not at all). `init` and `join` write all
       three (`git`, the address as given, the branch); `leave` writes `storage.mode = "local"`. The file is rewritten by the
       existing hand-rolled TOML writer, which does not keep comments (`config set` already behaves this way; the contributor guide
       notes it).
    3. **Only after both, the clone is placed.** (a) A different repository than the stored one, with `git_sync_remote` set, a
       different address and an existing clone directory for it: that old clone is moved aside (label `left`), so a machine never
       carries one repository's `L` into another's pull. (b) The same repository under a **different address spelling** than the
       stored `git_sync_remote`, with an old clone that exists: the freshly made clone for the new address is moved aside (label
       `superseded`), the old clone directory is renamed to the new address's clone path (`Directory.Move`, same parent directory),
       and `SetOriginAsync(newAddress)` re-points it, so the commit `L` and its protective ref survive. A move that fails
       (a locked directory on Windows) is a `note:` line and not a failure: the identity is recorded, and a later cycle with no `L`
       is an unproven apply that deletes nothing.
    A crash between 1 and 2 leaves the identity stored and the mode unchanged: the store is not in git mode, so nothing runs, and
    running the command again repairs it. A crash between 2 and 3 leaves the old clone where it is, which the next `join` or
    `init` of the same repository handles through the same rules. Nothing here leaves a state a cycle could act on wrongly:
    a cycle needs the identity, the config and the manifest to agree (1a-2).
11. **What a refusal changes.** Before step 4's lock nothing is changed. After the lock and before the record, the only
    possible changes are the clone directory (a created, refreshed or sidelined clone: a cache) and, for `init`, the remote
    repository after a successful push. A refusal says which of these happened in its last line (`note: a clone was kept at
    <path>`, `note: the repository was initialised`).
12. **`LsRemoteHeads`.** A new case of the closed `SyncGit.GitCommand` set: `LsRemoteHeads of url: string`, arguments
    `["ls-remote"; "--heads"; "--"; url]`, a network command (the same timeouts, environment and output bound as `LsRemote`), not
    clone-scoped, address checked as `Clone`'s. `SyncGit.parseLsRemoteHeads` returns the branch names (`refs/heads/<name>`, lines
    that are not `<sha>\trefs/heads/<name>` ignored). `GitClient.LsRemoteHeadsAsync(url)` returns them. The all-commands
    `ForceFlags.AssertNone` test covers it. It is how `init` tells an empty repository from one that holds another branch.
13. **The branch rule.** `GitSyncSettings.isUsableBranch` also refuses the exact text `HEAD` (git refuses `git branch HEAD`, and
    `git init -b HEAD` fails). A `[git_sync] branch = "HEAD"` is then corrected like any unusable branch (`Branch = None`, one
    warning), `--branch HEAD` is `bad-branch`, and `OpenGit`'s existing branch check refuses it. A test compares
    `isUsableBranch` with `git check-ref-format --branch` over a table of names (real git, `[GitFact]`): any name `isUsableBranch`
    accepts must be accepted by git, so no other name can pass here and fail later.
14. **Display form of an address.** `SyncSetup.displayRemote`: for `http`, `https`, `git`, `ftp` and `ftps` schemes the whole
    userinfo (`user:password@` or a bare token) is replaced by `***@`; other forms (`ssh://git@host/...`, `git@host:o/r`, a path)
    are shown as written, because their user part is a login name, not a secret. Every line that prints an address, in any
    command, uses it. Together with the 1a-1 redaction of git's standard error, no command prints a credential.
15. **Output.** Plain text, one fact or sentence per line, no colour, standard output for results, standard error for refusals,
    notices and progress. Progress for the network steps is git's own redacted standard error line forwarded as it comes (the
    client's `onStderr`), prefixed with two spaces, so a slow clone is not silent; there is nothing else to design here.
16. **Where the store is opened.** Each command opens the SQLite store at `ConfigLoader.GetDbPath()` (the same file as every
    other command, honouring `TOTAL_RECALL_DB_PATH`) with the migrations run, directly through `SqliteConnection` and
    `SqliteStore`, not through `MemoryComponents.OpenProduction` (which would open the embedder and, in `git` mode, run `OpenGit`).
    `leave` and `status` open it only when they need `_meta`; `leave` needs no store at all. The service takes the store as a
    constructor argument, so tests pass a temporary one.

## Design

### Core (pure, F#)

- `SyncSetup.fs` (new, after `SyncSettingsGuard.fs`): `modeVerdict (mode: string) : Result<unit, string>` (decision 4.2),
  `effectiveMode (mode: string option) (connectionString: string option) : string`, `RemoteShape = Empty | BranchPresent |
  OtherBranches` and `classifyRemote (head: string option) (branches: string list) : RemoteShape` (decision 5),
  `isSameRepository (stored: string option) (manifestId: string) : bool`, `newRepoId (bytes: byte[]) : string` (16 bytes to 32
  lowercase hex; throws on another length), `machineId (bytes: byte[]) : string` (the same shape), `displayRemote (address:
  string) : string` (decision 14), the refusal names as a DU `Refusal` with `name : Refusal -> string`, and the status
  rendering `renderStatus (StatusFacts) : string list` (a record of option/DU values the service fills in).
- `SyncGit.fs`: the `LsRemoteHeads` command, its argument list, `isNetwork`, address check, `parseLsRemoteHeads`.
- `Config.fs`: `isUsableBranch` refuses `HEAD`.

### Infrastructure (C#)

- `GitSync/SyncSetupService.cs`: `InitAsync`, `JoinAsync`, `LeaveAsync`, `StatusAsync`. Results are records (`SetupResult`:
  `Refusal?` with its text and notes, lines to print); the service prints nothing itself, the commands do.
- `GitSync/GitSyncStateStore.cs`: `ClearForNewRepository(tx)` and `WriteIdentity(tx, ...)`, plain SQL on the store's connection, the
  table and key names from one place.
- `GitSync/CloneSideline.cs`: `Move(directory, label, clock)`; never deletes; returns the new path.
- `GitSync/GitClient.cs`: `LsRemoteHeadsAsync`.
- `Config/ConfigWriter.cs`: `SaveUserOverrides` (the refusals stay in the callers; the new verbs are the only other callers of the
  writer, and they write only the three keys above).

### CLI

- `Commands/Sync/{Init,Join,Leave,Status}Command.cs`, registered in `CliApp.BuildRegistry`; `ICliCommand.Hidden`; `PrintTopLevelHelp`
  and `PrintGroupHelp` honour it as in decision 1.

### Documentation

`src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md` and the root `AGENTS.md` ("WHERE TO LOOK" and the
git sync paragraphs) describe the new pieces and say the commands are hidden until milestone 1b-iii. `CHANGELOG.md` gets
**no entry** (nothing is advertised and no existing behaviour changes); the `Unreleased` section is left as it is. No file under
`docs/` or `README.md` changes, and `scripts/host-docs.test.js` still passes.

## Testing

Real git throughout, `[GitFact]`, against bare repositories in temporary directories (a local path and a `file://` address),
a temporary store, the stubbed public probe of 1a-2, an injected clock and random source, and a user git configuration made
hostile with `GitTestEnvironment.WriteGitConfig` (`commit.gpgsign=true`, `core.hooksPath=<a directory with a failing hook>`,
`init.defaultBranch=trunk`, `core.autocrlf=true`, `user.useConfigOnly=true`).

- **init**: an empty bare repository gets exactly one commit on the branch with `.gitattributes` and a manifest that
  `ManifestFile.Parse` accepts and whose `repo_id` is the stored one; the config file has the three keys; the identity rows exist
  and `RepoGuard.EvaluateIdentityAsync` is `joined` with `CanSync` true; the same bytes with a hostile global configuration;
  `repo-exists` for a repository with the branch (nothing pushed, nothing recorded); `not-empty` for a repository with only
  another branch; a push that fails (a bare repository whose `pre-receive` hook rejects) leaves the remote empty, records nothing
  and a second `init` against a good repository succeeds and sidelines the first clone; a non-fast-forward race (a second
  `init` pushed between the look and the push, driven with a recording process wrapper) is `repo-exists`.
- **join**: a repository made by `init` (from a second data directory) is joined: clone, manifest read, identity stored, config
  written, machine id different from the first; no branch, no manifest, an invalid manifest and a newer-format manifest are
  refused with nothing recorded and the config untouched; a different repository replaces one already joined (rows
  cleared, conflict rows kept, memory rows untouched, the old clone sidelined as `left`); the same repository under a respelled
  address keeps the rows, `git_sync_repo_id` and machine id, moves the old clone to the new path and keeps `L` (a commit written
  to the protective ref survives); a data directory that moved (host stored differently) re-stamps the host and keeps the
  rows; a second `join` of the same address refreshes the clone.
- **public / mode / host**: `public-repo` (a 200 from the stub) with no git network command run (the recording process shows only
  `--version`); 404 and unknown proceed, the unknown with its one notice; `wrong-mode` for `cortex`, `postgres`, a connection
  string alone and an unknown text, with nothing changed, and `local` and `git` accepted; `host-unverified` when the machine-id
  source fails; `busy` with the lock held by another handle, and the lock released after success and after every refusal; `no-git`
  with an empty `PATH`.
- **leave / status**: `leave` in git mode writes `local` and leaves every row, `_meta` key, clone and the repository as they were
  (a snapshot comparison); not in git mode it changes nothing; it works with `git` missing. `status` before any setup, after
  `init`, after `leave`, with a damaged manifest in the clone, with a changed remote and with a moved data directory prints the
  matching join state, runs no git network command and never prints a credential (an address with `https://token@host/...`).
- **CLI**: argument errors exit 2; `--help` of a command prints usage; the top-level help and the output of every existing help
  text are byte-identical to before (a test records them); `total-recall sync` dispatches; the catalog is unchanged.
- **Branch rule**: the `HEAD` case in `GitSyncSettings` tests and the `git check-ref-format --branch` table.
- **No force**: the all-commands argument test and a recording wrapper around every real run assert that no `git` call of any
  verb carries `--force`, `-f` (other than the `add -f` of `Stage`, which is pinned), `--force-with-lease`, `--delete`, `--mirror`.
- **Platform**: tests that need symbolic links or `sh` keep the 1a-1 attributes; the rest run on Windows (paths from
  `Path.Combine`, `file://` addresses built from `Uri`).

## Acceptance criteria

1. Given an empty repository, when `sync init --url <it>` runs, then it gets one commit on the chosen branch holding a
   `.gitattributes` and a valid `manifest.json` with a new repository id; this machine's config is set to the `git` mode with that
   address and branch; the identity is stored; and sync reports `joined`. The same commit bytes result whatever the user's global
   git settings say (signing, hooks, default branch, line endings), without any prompt.
2. Given a repository that already has the branch, or has other branches, when `sync init` runs, then it refuses (`repo-exists`,
   `not-empty`), and nothing is pushed, recorded or configured.
3. Given a repository made by `init` on another machine, when `sync join <it>` runs, then the manifest is validated, the
   identity (its repository id, a new machine id, the host) is stored and the mode and settings are written; given a repository
   with no such branch, no manifest, an invalid manifest or a newer format, then it is refused with the matching name and nothing is
   recorded or configured.
4. Given a public GitHub repository, when `init` or `join` runs, then it is refused before git contacts it; given a private or
   unknown answer, it proceeds, with the notice for an unknown one.
5. Given a machine in the `cortex` or `postgres` mode (or any mode other than `local` or `git`), when `init` or `join` runs, then
   it is refused naming the mode and telling the user to set `local` first, and nothing changes; given `local` or `git`, it proceeds.
6. Given a machine that already joined one repository, when `init` or `join` points it at a different repository, then the
   sync bookkeeping is cleared except conflict items, memories are untouched, and the old repository's clone is moved aside, not
   deleted; given the same repository under a respelled address or after the data directory moved, then the bookkeeping, the
   machine id and the last-synced commit are kept and the clone follows the new address.
7. Given a failed push, an unreachable or unreadable repository, or a refused manifest, when the command ends, then no identity
   or setting was recorded, a retry works, and the output names the reason without any credential or address user-info.
8. Given any of the commands, when another is already running for the same data directory, then it reports `busy`; the lock is
   free after every ending.
9. Given a machine in `git` mode, when `sync leave` runs, then the mode becomes `local` and every memory, sync row, clone and
   remote file is untouched, with that said; given any other mode, it changes nothing; it needs neither `git` nor the network.
10. Given any machine, when `sync status` runs, then it reports the mode, `git`, the configured and stored remote and branch with any
    credential hidden, the repository and machine ids, the host check, the join state with its reason and the clone's state, runs no
    network command and fails on nothing; an unreadable part is shown as unknown.
11. Given a branch named `HEAD`, when settings are read or `--branch` is given, then it is refused; and every branch name the rule
    accepts is accepted by `git check-ref-format --branch`.
12. Given the commands, then they are absent from `total-recall --help`, no document or refusal text names them, the tool catalog
    is unchanged, and no git call of any command forces, deletes or mirrors.
13. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then they pass
    and the release build shows no new warning; and given the contributor guides, then they describe the new pieces.
