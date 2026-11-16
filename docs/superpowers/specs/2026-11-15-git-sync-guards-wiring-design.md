# Git sync, milestone 1a-2: the repository guards, the `git` storage mode and the settings protection

Date: 2026-11-15. Milestone 1a-2 of the git sync feature tracked in #93 (0a is #100, 0b-1 is #105, 0b-2 is #111, 0b-3 is #118,
1a-1 is #120, delivered by PR #126). The umbrella design is `2026-11-15-git-sync-design.md` (see "Architecture", "Configuration", "Repository layout",
"Remote identity scopes all of it", "Safety checks (`RepoGuard`)", "Failure handling" and "Testing"). Milestone 1a is split in
three; this is the second. 1a-1 built the transport (`GitClient`, `SyncLock`, the tree reader). This milestone adds the
checks that say whether a repository and a data directory may be synced, the new `git` storage mode, and the protection of the
sync settings. 1a-3 adds the `sync init --url`, `join`, `leave` and `status` verbs on top. After this milestone a hand-edited
`[storage] mode = "git"` opens and behaves like the local mode; nothing syncs and nothing is advertised.

## Problem

Three things must be true before any cycle runs, and each is easy to get subtly wrong.

1. **A sync must never start against the wrong place.** A typo in the remote, a config edit, a repository deleted and recreated
   under the same name, or a data directory copied to another machine would otherwise make the next cycle push this
   machine's memories into a repository that is not the user's sync repository, or apply someone else's records. The
   umbrella's answer is an identity (`git_sync_remote`, `git_sync_repo_id`, `git_sync_host`) that must match before a cycle
   runs. It needs a manifest to carry the `repo_id`, a rule that compares the pieces, and a host fingerprint that survives a
   hostname change but not a copy.
2. **Memories must not go to a public repository.** The umbrella asks GitHub, without credentials, whether the repository is
   public, caches a private answer for 24 hours and never blocks on an error.
3. **The agent must not be able to redirect the sync.** A planted memory could tell the agent to call `config_set` on
   `git_sync.remote` or `storage.mode`. Those keys are changed only by the user.

Also, a store opened in the new mode must come up fast, with no network and no clone, because it is on the MCP boot path,
and a configuration that cannot work must fall back to the local store with a label instead of failing the session.

## Goals

1. **The manifest** (`manifest.json`): the pure rules for its format and `repo_id`, and a codec that parses and renders it
   byte-exactly. A manifest written by a newer format is recognised as such and never misread.
2. **The identity rule**: a pure `JoinState` that, from the configured settings, the stored identity, the current host
   fingerprint and the repository's `repo_id` when it is known, says `joined` or `not-joined` with the reason, and the
   host fingerprint itself.
3. **`RepoGuard`**: the identity evaluation against a real store and clone with no network command, the format check, and
   the public-repository check with its cache.
4. **`OpenGit`** and the storage-mode wiring: the `git` case in `ServerComposition`, the fallback label
   `sqlite (git failed)`, and the `ReindexEmbeddingsCommand` edit.
5. **The `config_set` denylist** on every surface that writes configuration (MCP, the web UI and the CLI).
6. No change to what a user can do, apart from the refusal in goal 5. Nothing starts a cycle.

## Non-goals

- Writing `manifest.json` into a repository, generating a `repo_id` or a machine id, clearing sync state, renaming a clone to a
  respelled URL's path, and the verbs themselves: 1a-3. This milestone only reads the identity and judges it.
- Anything that fetches, applies or pushes: 1b. The guards return verdicts; nothing here calls `Fetch`, `Clone` or `Push`.
- The scheduler, the `sync` block in `status` and `session_start`, and the `not-joined` text in the startup instructions:
  milestone 2. This milestone returns the verdicts and the wording constants they will use.
- `gh`, repository creation and `sync doctor`: milestone 4. A GitHub Enterprise or other host is not probed (see 9).
- Encryption, a bypass for the public check, or any way for the agent to change a sync setting.
- Changing the Postgres or Cortex paths. Postgres has no sync tables, and `OpenGit` builds the plain local store.

## Decided here

The umbrella fixes the behavior; these are the calls it leaves open. Each marked **(beyond)** goes past its text and is justified.

1. **The manifest is a single line of JSON, byte-exact (beyond).** The umbrella shows the fields; this fixes the bytes so every
   machine writes and reads the same file: `{"format":"total-recall/v1","repo_id":"<32 lowercase hex>","created_by":"<text>",
   "created_at":"<YYYY-MM-DDTHH:MM:SSZ>"}` followed by one `\n`, UTF-8 with no byte-order mark, keys in that order. `repo_id` is
   128 random bits as 32 lowercase hex digits (1a-3 generates it). `created_by` is escaped as JSON text: `"`, `\` and every
   character below U+0020 are escaped (`\n`, `\t`, `\uXXXX`), everything else is written as UTF-8, and a lone surrogate is refused
   (`render` throws `ArgumentException`: the text comes from the app). `render` lives in Core only; the C# side calls it. The
   reader accepts any key order, ignores unknown keys, refuses a duplicate key, a file over 4 KiB, a byte-order mark or invalid
   UTF-8, a `repo_id` that is not exactly 32 lowercase hex digits, and a `format` that is not a string. `created_by` and
   `created_at` are optional on read and carried as text. **A newer format wins over every refusal except non-JSON and invalid
   UTF-8** (decision 2): the blob is read up to 64 KiB, and a permissive first look for a root `format` string (a byte-order mark
   ignored, the first of a duplicated key used) that names a newer format returns `Newer` whatever else is wrong; only after that
   are the 4 KiB cap and the `v1` rules applied.
2. **Format classification.** A `format` of exactly `total-recall/v1` is supported. `total-recall/v<N>` with `N` a positive integer
   without leading zeros, at most 9 digits and greater than 1 is **newer** and is reported as `format-too-new` before any other
   field is validated (a newer manifest may have changed shape). Anything else (including `total-recall/v0`, `total-recall/v01`
   and an unrelated text) is **unrecognised**: the repository is not a sync repository, which is different from a newer one.
3. **The manifest is read from the local clone only, at a revision the caller names.** `RepoGuard.ReadManifest(revision)` and
   `EvaluateIdentity(revision)` take a `SyncGit.Revision`, default `Head`. The cycle (1b) runs the guard after its fetch and before
   `reset --hard`, when `HEAD` is still the old commit, so it passes the fetched `refs/remotes/origin/<branch>`; the verbs pass
   `Head`. It reads the root listing with `LsTreeRoot`, requires `manifest.json` to be a regular file (mode `100644` or `100755`; a
   symlink, a submodule or a directory of that name is `Invalid`), reads the blob with `CatBlob` (64 KiB cap), and never fetches.
   A missing clone is "no manifest known", not an error: the identity
   rule then cannot compare `repo_id` yet and the cycle (1b) compares it after its fetch. This is how the umbrella's "no git
   network call" holds for every `not-joined` case that can be known locally. `ReadManifest` returns `NoClone`, `NoManifest`,
   `Invalid of reason`, `Unreadable of reason` (a git failure: an unborn `HEAD`, a corrupt clone, a blob over the cap, no `git`),
   `Newer of int` or `Ok of manifest`, and `EvaluateIdentity` returns a `GuardVerdict` holding that and the `JoinState`. The
   `JoinState` is evaluated with the manifest's `repo_id` only for `Ok`; for every other outcome it has no `repo_id` to compare.
   `GuardVerdict.CanSync` is true only when the state is `joined` and the manifest outcome is `Ok` or `NoClone` (`NoClone`: the
   cycle will clone, then evaluate again with the fetched manifest); a clone whose manifest is missing, invalid, unreadable or newer
   therefore never syncs. A caller reports a newer format before `not-joined` (a newer repository must never be written to,
   whoever joined it).
4. **The `JoinState` rule and its order.** The first check that fails decides, so the reason is stable:
   (a) no usable `[git_sync] remote`: `not-configured`; (b) no stored remote (`git_sync_remote` absent) or no stored `repo_id` (a join that did not finish): `never-joined`;
   (c) the configured remote text differs from the stored text (ordinal comparison, no normalisation): `remote-changed`;
   (d) a stored host fingerprint (`git_sync_host`) that is absent or differs from the current one: `data-directory-moved`;
   (e) a known manifest `repo_id` that differs from the stored `git_sync_repo_id`: `repository-replaced`; otherwise `joined`. A hostname change alone changes none of the inputs. A conflict
   between these and the umbrella's table is resolved in favour of "refuse": every case that is doubtful is `not-joined`.
5. **The host fingerprint.** `SHA-256("total-recall/host/v1\n" + osMachineId + "\n" + canonicalDatabasePath)`, the first 32 lowercase
   hex digits, where `osMachineId` is the first of: Linux `/etc/machine-id` then `/var/lib/dbus/machine-id`; macOS
   `IOPlatformUUID` from `/usr/sbin/ioreg -rd1 -c IOPlatformExpertDevice` (an absolute path, no shell, 3 seconds); Windows
   `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography\MachineGuid` (64-bit view). A value is used when it is 1 to 128 printable
   characters after trimming. A source that does not exist or holds nothing usable is **absent**: the empty text is used, so a copy
   to **another path** is still caught and a copy to the same path on another machine is not (stated in the code and the docs of
   1a-3). A source that exists but **fails** (the `ioreg` timeout, a registry error, an unreadable file) makes the fingerprint
   **unavailable** for this process: `evaluate` then skips check (d) (a transient failure must not read as a moved directory) and
   the verdict carries `HostUnverified`, which 1a-3's `init` and `join` treat as "try again" and never stamp. The canonical path is
   the full path of the database file with every existing component resolved through its symbolic links (walked one component at a
   time with `ResolveLinkTarget`, so macOS `/var` and a symlinked home directory give one spelling), trailing separators removed,
   and lower-cased on Windows and macOS (default case-insensitive file systems). It is computed once per process. It never uses
   the hostname.
6. **Identity is read through `IMetaStore`.** `GitSyncIdentity` reads `git_sync_remote`, `git_sync_repo_id` and `git_sync_host`
   (the key names, with `git_sync_machine`, are constants in Core, used by 1a-3 to write them; the rule does not use the machine id). A stored value that is empty is absent.
   This milestone writes none of them; its tests set them with `SetMeta`.
7. **The public check, step by step.** The remote is parsed by a pure function into a GitHub `owner/repo` or "not GitHub". GitHub
   shapes: `https://`, `http://`, `git://`, `ssh://` and `git+ssh://` URLs and the scp-like `user@github.com:owner/repo`, host
   `github.com` compared without regard to case, any user-info, port and trailing `/` ignored, `repo` with an optional `.git`
   suffix, owner and repo made of letters, digits, `.`, `_` and `-` (not `.` or `..`), and exactly two path segments. Everything
   else (another host, an unresolved ssh alias, a local path, a third path segment) is **skipped**. User-info is never sent.
   The probe is `GET https://api.github.com/repos/<owner>/<repo>`: no `Authorization` header and no cookies, `Accept:
   application/vnd.github+json`, a `User-Agent` of `total-recall`, redirects followed at most 3 times, a 5-second limit, and
   the body is never read. `200` is **public** (refused). `404` is **private or missing** (proceeds). Anything else (`401`, `403`,
   `429`, `5xx`, a redirect loop, a timeout, a network error, or a final response that did not come from `api.github.com`) is
   **unknown**: the caller warns once per process and proceeds. A skipped host is told once per process. The two notices are
   returned as text on the verdict (the verbs and the cycle print them). **There is no in-process memo of the verdict**: every
   call goes through the cache of decision 8 and, on a miss, the probe; only the notices are once per `RepoGuard` instance (the
   composition makes one per process, a test makes its own). An unknown answer is not cached, so a caller that wants to avoid
   re-probing an offline machine (5 seconds each time) decides its own cadence; 1b calls it on its first cycle and then
   only after an unknown answer or 24 hours.
   The probe is built so no credential can ride along: `UseCookies` off, `UseDefaultCredentials` off, `MaxAutomaticRedirections` 3,
   `ResponseHeadersRead`, the system proxy as the user has it, and a base address passed in (so a test can point the real probe at
   a loopback listener). The slug parser takes the host after the **last** `@` of the authority (`https://github.com@evil.com/o/r` is
   `evil.com`), and a repository named `.git`, or one that is `.` or `..` after the `.git` suffix is removed, is refused.
8. **The cache.** Only a `404` answer is cached, in `_meta.git_sync_public_check` as `v1|<first 16 hex of the SHA-256 of
   "<owner>/<repo>" lower-cased>|<unix seconds>`. A cached answer is used when it is for the same repository and no more than
   24 hours old, and a timestamp more than 5 minutes in the future is ignored (a clock set wrong must not pin "private"). A
   `200` is never cached (a repository made public later must be refused), and neither is an unknown answer.
9. **Only `github.com` is probed.** GitHub Enterprise hosts and SSH host aliases are skipped with the umbrella's notice ("keep the
   repository private"). A probe against an arbitrary host would send the user's repository name to a server the user did not
   name, and an alias cannot be resolved without reading `~/.ssh/config`.
10. **`OpenGit` validates, it does not run (beyond).** It fails (so the caller falls back) when `[git_sync] remote` is not set
    or is refused by `SyncGit.validateRemote`, when the branch is unusable, or when no `git` executable is found on the `PATH`
    (`GitExecutable.TryFind`, a file-system search; it starts no process, so it cannot hang the boot). It does not check the git
    version (the first cycle and `sync doctor` do, with `GitClient.VersionAsync`). It prints the settings' `Warnings` to stderr
    once, opens exactly what the local path opens (a plain `SqliteStore`, no decorator), and reports `storage: "git"`. A
    store that is not joined opens normally: `not-joined` is a state the cycle reports, not a boot failure.
11. **The fallback filter** (`catch ... when (configuredMode is "cortex" or "postgres")`) gains `"git"`, so a failure reports
    `sqlite (git failed)` and writes the line `[total-recall] git storage failed, falling back to sqlite: <reason>` to stderr.
    The line never contains a remote's user-info (the reason text is redacted with the 1a-1 `SyncGit.redact`).
12. **`ReindexEmbeddingsCommand.IsPostgres`** treats `"git"` like `"local"` (a local store). An unknown mode with a connection
    string is still Postgres, as before.
13. **The denylist is one pure predicate, called from three places.** `SyncSettingsGuard.isProtectedKey` (Core) is true for a key
    whose first dot-separated segment is `git_sync` (any depth, and bare `git_sync`, which would replace the table), for bare
    `storage` (which would replace the table), and for `storage.mode` and **`storage.connection_string` (beyond)**: with no `mode`
    set, which is the shipped default, a connection string alone selects Postgres (`OpenProduction`, `IsPostgres`), so leaving it
    writable would let the agent change the storage backend by the other door; the web UI already lists both keys as read-only.
    Segments are trimmed of whitespace and compared without regard to case, and
    a key with an empty segment is also refused (it cannot be a real setting, and refusing it is the safe side). The MCP
    `ConfigSetHandler` and the CLI `SetCommand` call it before anything is read, snapshotted or written; the web UI reaches the
    same handler through `ToolAllowlist`, so one check covers MCP and web, and a test asserts that route. The refusal names the key and
    says: "This setting is changed by you, in `config.toml` or with `total-recall sync init`, `sync join` and `sync leave`; an agent
    cannot change it." The CLI exits 2; the MCP call fails with an `ArgumentException` (the existing "bad argument" path of
    `ErrorTranslator`).
14. **A behavior change, announced.** `total-recall config set storage.mode cortex` (and `storage.connection_string`) worked before and
    is refused now (the web UI already shows the keys as read-only). The CHANGELOG says so and where to set them instead.

## Design

New files go after `Config.fs` in `TotalRecall.Core.fsproj`, in this order.

### Core (pure, F#)

- `SyncManifest.fs`: `ManifestFormat = Supported | Newer of int | Unrecognised`, `classifyFormat`, `isValidRepoId`, `render`
  (the bytes of decision 1 from its fields), and the constants `FormatV1 = "total-recall/v1"` and `MaxManifestBytes = 4096`.
- `SyncIdentity.fs`: the `_meta` key names, `StoredIdentity`, `JoinState`/`NotJoinedReason`, `evaluate` (decision 4; its host input is
  a fingerprint option, `None` meaning unavailable), the wording constants of each `not-joined` reason, and
  `hostFingerprint osMachineId canonicalPath` (decision 5, the hash only; the file reading is C#).
- `SyncPublic.fs`: `parseGitHubSlug` (decision 7), `PublicVerdict = Private | Public | Unknown of string | Skipped of string`,
  `ofStatus` (the status-to-verdict table), the cache value's `format`/`parse`, and `isFresh now cachedAt`.
- `SyncSettingsGuard.fs`: `isProtectedKey` and the refusal text.

### Infrastructure (C#)

- `GitSync/ManifestFile.cs`: parse over the Core rules (`System.Text.Json` reader with duplicate-key detection; render is the Core
  function).
- `GitSync/HostIdentity.cs`: `MachineIdReader` (the three sources, each behind a seam so a test supplies the text) and the canonical
  database path; `Current` is computed once per process.
- `GitSync/GitSyncIdentityStore.cs`: reads the three keys.
- `GitSync/PublicRepoProbe.cs`: the `HttpClient` call behind `IPublicRepoProbe` (`Task<int?> GetStatusAsync`), so every test runs
  with no network.
- `GitSync/RepoGuard.cs`: `EvaluateIdentity()` (reads the settings, the identity and, when a clone exists, its manifest, then calls
  `SyncIdentity.evaluate`; no network command), `ReadManifest()` and `CheckPublicAsync(ct)` (parse, cache, probe, cache write,
  once-per-process notices). It takes a clock, the store's `IMetaStore`, a `GitClient` and the probe as constructor arguments.

### Server, CLI, Web

- `ServerComposition.OpenGit` (internal, taking the configuration and a database path so a test can call it), the `"git"` case
  and the catch filter; `FriendlyName("git")` is `git`.
- `ConfigSetHandler` and `SetCommand` call `SyncSettingsGuard.isProtectedKey` first.
- `ReindexEmbeddingsCommand.IsPostgres`.

### Documentation

`src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md` (the GitSync paragraph and the file table),
`src/TotalRecall.Server/AGENTS.md` if it lists storage modes, and the root `AGENTS.md` (the rows of the layout and the session
section that mention the storage modes). The CHANGELOG `Changed` entry of decision 14. `docs/` gets no page: the feature is not
advertised until 1b-iii.

## Testing

Pure (Core, Expecto and FsCheck): `classifyFormat` for `total-recall/v1`, `v2`, `v10`, `v999999999`, `v1000000000`, `v0`, `v01`,
`v-1`, `v1.0`, an empty string, a different prefix and upper case; `isValidRepoId`; `render` against a fixed vector and a round
trip; `evaluate` for every reason, the order of decision 4 (a case failing two checks reports the earlier), and a property that
it is total and that `joined` needs every input to agree; a hostname-only change leaves `joined`; `hostFingerprint` against a
fixed vector, and a property that a different path or machine id gives a different value; `parseGitHubSlug` over the shapes of
decision 7 (an https URL with a token as user-info, an scp-like address, a port, `.git`, a trailing slash, an upper-case host, an
ssh alias, `gitlab.com`, a local path, three segments, `..`, `https://github.com@evil.com/o/r`, a repository named `.git`); `ofStatus` for 200, 404, 301, 401, 403, 429, 500 and none; cache
parse, format and `isFresh` (exactly 24 hours, one second over, a timestamp in the future by 4 and by 6 minutes);
`isProtectedKey` for the keys of decision 13 and for look-alikes that must stay writable (`storage`-prefixed look-alikes such as
`storagex.mode`, `tiers.hot.max_entries`, `git_syncx.remote`, `embedding.provider`).

Infrastructure (real `git` where a clone is needed, through the 1a-1 support code, skipped with a reason when `git` is missing):
`ManifestFile` over valid and every refused input of decision 1 (duplicate key, exactly 4096 bytes accepted and 4097 refused, BOM,
invalid UTF-8, wrong `repo_id`, non-string `format`, a newer format with otherwise broken fields, a newer format larger than
4 KiB and one with a BOM, both `Newer`), and `ReadManifest` for a symlink, a directory and a submodule named `manifest.json`, an
unborn `HEAD` and a corrupt clone (`Unreadable`); `RepoGuard.EvaluateIdentity` against a store whose `_meta` is
set to each state and a clone holding a manifest, asserting through a recording `IGitProcess` that no `clone`, `ls-remote`,
`fetch` or `push` started in any case; a hostname change leaves `joined`; a copied data directory (a different path) is
`data-directory-moved`; the same database reached through a symlinked directory gives the same fingerprint; a failing machine-id
source gives `HostUnverified` and never `data-directory-moved`; a joined store with no clone is `joined` and starts no process; `CheckPublicAsync` with a stub probe for 200, 404, 403, a timeout and a non-GitHub remote, asserting the
request has no `Authorization` header (against a local loopback listener for the real `PublicRepoProbe`), the cache hit inside
24 hours and the second request after, that a 200 is never cached, and that the notice appears once; `HostIdentity` with each
machine-id source supplied as text (including an empty one and 129 characters).

Server and CLI: `OpenGit` with a valid configuration returns the local tool set and `storage` `git` and starts no process
(a recording seam); with no remote, a refused remote and no `git` on a `PATH` it falls back with `sqlite (git failed)`;
the `ReindexEmbeddingsCommand` accepts a `git` store; `ConfigSetHandler` and `SetCommand` refuse every protected key without
touching the file or taking a snapshot, and still write an ordinary key; the web `config_set` route refuses `git_sync.remote` (the test points `TOTAL_RECALL_HOME` at a temporary directory, or uses the
handler's table-provider and path seam, so a missing guard cannot write the real configuration).

The existing suites, the shim tests, the catalog check and the AOT publish run unchanged (a new handler or tool would change the
catalog; none is added).

## Acceptance criteria

1. Given a repository manifest in the `total-recall/v1` format, when it is read, then its `repo_id` is returned; given one with a
   newer format, then it is reported as written by a newer total-recall before anything else in it is checked; given one that is
   not valid JSON, is over 4 KiB, repeats a key, has a bad `repo_id` or an unrelated format, then it is rejected with a reason.
2. Given the same field values, when the manifest is rendered, then the bytes are identical on every platform.
3. Given a configured remote that was never joined, a remote that differs from the joined one, a repository whose `repo_id`
   differs from the joined one, and a data directory that was moved or copied, when sync is checked, then each reports
   `not-joined` with its own reason and no git network command is run.
4. Given a clone whose manifest names a newer format, when sync is checked, then it reports `format-too-new`; given a clone whose
   manifest is missing, invalid or unreadable, then it is not allowed to sync; given a joined store with no clone yet, then it is
   joined and no command is run; and given a hostname change only, then it is still joined.
5. Given the stored identity, the configured remote, the host and the repository all agreeing, when sync is checked, then it
   is joined.
6. Given a GitHub remote, when the public check runs and GitHub answers 200, then the repository is refused as public; given
   404, then it proceeds; given any other answer, a timeout or no network, then it proceeds with one warning per process.
7. Given a remote on another host, an ssh alias or a local path, when the public check runs, then no request is made and the user
   is told once that the repository must be kept private.
8. Given a private answer less than 24 hours old for the same repository, when the check runs again, then no second request is
   made; given an older answer, a different repository or an answer stamped in the future, then it asks again; given a public
   answer, then it is never reused.
9. Given any public check, then the request carries no credentials and no part of the remote's user-info.
10. Given `[storage] mode = "git"` with a usable remote and `git` installed, when the app opens its store, then it opens as the local
    mode does, reports storage `git`, and runs no git command and no network call; given settings that were corrected, then each
    correction is written once to the error output.
11. Given `[storage] mode = "git"` and no remote, a refused remote, an unusable branch or no `git` on the `PATH`, when the app opens
    its store, then it falls back to the local store, reports `sqlite (git failed)`, and the message names the reason without
    any credential from the remote.
12. Given a store in `git` mode, when `reindex-embeddings` runs, then it treats it as a local store.
13. Given a request to set `storage.mode`, `storage.connection_string` or any `git_sync` setting (in any letter case, with spaces around a part, or the bare
    table name), when it comes from the MCP tool, the web UI or the `config set` command, then it is refused with a message
    saying the user changes it, the config file is untouched and no snapshot is taken; given an ordinary key, then it is
    written as before.
14. Given the existing automated checks, the shim tests, the tool catalog check and the release build, when they run, then they
    pass and the release build shows no new warning; and given the contributor guides and the changelog, then they describe the
    new pieces and the refusal of `config set storage.mode`.
