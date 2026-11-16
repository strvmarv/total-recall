# Git sync 1a-2: the repository guards, the git storage mode and the settings protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the manifest rules and codec, the identity rule (`not-joined`) with the host fingerprint, the public-repository check, the `RepoGuard`, the `git` storage mode (`OpenGit`, the fallback label, the re-embed edit) and the `config_set` denylist on every writing surface, with tests. Nothing starts a cycle.

**Architecture:** Pure rules go in four new F# files in `TotalRecall.Core` (`SyncManifest.fs`, `SyncIdentity.fs`, `SyncPublic.fs`, `SyncSettingsGuard.fs`). `TotalRecall.Infrastructure/GitSync/` gets `ManifestFile.cs`, `HostIdentity.cs`, `GitSyncIdentityStore.cs`, `PublicRepoProbe.cs` and `RepoGuard.cs` (+ `RepoGuard.Public.cs`). `ServerComposition` gets `OpenGit`; `ConfigSetHandler` and the CLI `SetCommand` call the Core denylist first. Everything reads through the 1a-1 `GitClient` and `IMetaStore`; no cycle code exists.

**Tech Stack:** F# (Expecto + FsCheck 2.16.6) for the pure part; C# net10 NativeAOT (xUnit 2.9.2, `System.Text.Json` `Utf8JsonReader`, `HttpClient`) for the rest. No new package.

**Spec:** `docs/superpowers/specs/2026-11-15-git-sync-guards-wiring-design.md` (read it first; this plan gives files, signatures and test lists, the spec gives the rules and the numbered decisions, cited as "decision N"). The umbrella is `2026-11-15-git-sync-design.md`. Issue #128.

## Global Constraints

- The spec file is read-only for the whole run. Commits carry no trailer and no AI attribution; messages are plain imperative sentences.
- Core has no I/O and no mutable state and uses no `%A` or reflection serialization. C# is AOT-safe: no reflection serializer (use `Utf8JsonReader`/`Utf8JsonWriter` or hand-built text), no `Regex` with `RegexOptions.Compiled`. No new package.
- New F# files are added to `TotalRecall.Core.fsproj` directly after `SyncTree.fs` (they use `Config.fs` and `SyncGit.fs`, which come earlier), and their test files after `SyncTreeTests.fs` in `TotalRecall.Core.Tests.fsproj` (before `Program.fs`). New C# files go in `src/TotalRecall.Infrastructure/GitSync/` (namespace `TotalRecall.Infrastructure.GitSync`) and tests in `tests/TotalRecall.Infrastructure.Tests/GitSync/`.
- A string, result or exception never carries user-info from a remote address: use `SyncGit.redact`.
- No test reads or writes the developer's real `~/.total-recall`, `~/.gitconfig` or the network. Config tests use a temporary path; git tests use the 1a-1 support code (`GitTestEnvironment`, `BareRemote`, `[GitFact]`); the probe tests use a stub `HttpMessageHandler` or a loopback `HttpListener`.
- Test commands: `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj`; `dotnet test tests/TotalRecall.Infrastructure.Tests/TotalRecall.Infrastructure.Tests.csproj --filter "FullyQualifiedName~GitSync"`; `dotnet test tests/TotalRecall.Server.Tests/TotalRecall.Server.Tests.csproj`; `dotnet test tests/TotalRecall.Cli.Tests/TotalRecall.Cli.Tests.csproj`; `dotnet test tests/TotalRecall.Web.Tests/TotalRecall.Web.Tests.csproj`. The worktree already ran `npm ci --ignore-scripts` and `sh scripts/fetch-bge-small.sh`.
- A test name quotes the spec's rule or the ticket's wording so a reviewer compares one to one.

## Interpretations (recorded here because the spec is read-only)

1. **Async names.** `GitClient` is async, so the spec's `ReadManifest(revision)`, `EvaluateIdentity(revision)` are `ReadManifestAsync` and `EvaluateIdentityAsync`; `revision` is a `SyncGit.Revision?` where `null` means `Head` (an F# union cannot be a default argument).
2. **Types.** Core: `SyncManifest.ManifestFormat = Supported | Newer of int | Unrecognised` (the parsed fields live in the C# record `ParsedManifest`, Task 5); `SyncIdentity.StoredIdentity = { Remote: string option; RepoId: string option; Host: string option }`; `SyncIdentity.IdentityInput = { ConfiguredRemote: string option; Stored: StoredIdentity; CurrentHost: string option; ManifestRepoId: string option }`; `SyncIdentity.NotJoinedReason = NotConfigured | NeverJoined | RemoteChanged | DataDirectoryMoved | RepositoryReplaced`; `SyncIdentity.JoinState = Joined | NotJoined of NotJoinedReason`; `SyncPublic.GitHubSlug = { Owner: string; Repo: string }`; `SyncPublic.PublicVerdict = Private | Public | Unknown of string | Skipped of string`. A C# test calls union cases as `SyncIdentity.JoinState.NewNotJoined(...)` and nullary cases as properties (`JoinState.Joined`).
3. **Notices.** The two notice texts (an unknown answer, a skipped host) are functions in `SyncPublic` (`unknownNotice reason`, `skippedNotice reason`); `RepoGuard` returns them on the result and never prints.
4. **The probe seam.** `IPublicRepoProbe.GetStatusAsync(string owner, string repo, CancellationToken)` returns the final HTTP status, or `null` for a network error, a timeout or a final response from a host other than the probe's base-address host.
5. **Where a limit is hit.** A manifest blob larger than the 64 KiB read cap makes `CatBlobAsync` fail, so it is `Unreadable` (decision 3); one that was read but is invalid, or over 4 KiB at `v1`, is `Invalid`.
6. **Composition seam.** `ServerComposition.OpenConfigured(TotalRecallConfig cfg, string? dbPath, string querySource, string? pathVariable)` is the existing body of `OpenProduction` after it loads the configuration; `OpenProduction` calls it with `pathVariable: null`. Tests call it with a config from `new ConfigLoader().LoadEffectiveConfig(tempConfigPath)`, so no process environment is changed.

## Review Focus

1. A newer-format manifest that is also over 4 KiB, has a BOM or has a bad `repo_id` is `Newer`, not `Invalid` [Task 5].
2. `config_set` of `Storage.Mode`, ` storage . connection_string `, `git_sync`, `GIT_SYNC.remote`, `storage.mode.x` and `storage` is refused on MCP, web and CLI, and `storagex.mode` and `embedding.provider` are not [Task 4].
3. A `https://github.com@evil.com/o/r` remote is not probed as GitHub; a remote with a token as user-info is probed without it and the token appears in no result [Tasks 3, 7].
4. A failing machine-id source never reads as a moved data directory, and a symlinked database directory gives one fingerprint [Tasks 2, 6].
5. A repository deleted and recreated after the fetch: `EvaluateIdentityAsync(RemoteBranch b)` sees the new `repo_id` while `HEAD` still shows the old one [Task 8].

---

### Task 1: Manifest rules (Core)

**Files:**
- Create: `src/TotalRecall.Core/SyncManifest.fs`; Test: `tests/TotalRecall.Core.Tests/SyncManifestTests.fs`
- Modify: both `.fsproj` files (compile order above).

**Interfaces:**
- Produces (module `TotalRecall.Core.SyncManifest`): `[<Literal>] FormatV1 = "total-recall/v1"`; `[<Literal>] MaxManifestBytes = 4096`; `[<Literal>] MaxLookBytes = 65536`; `ManifestFormat`; `classifyFormat : string -> ManifestFormat`; `isValidRepoId : string -> bool`; `render : repoId: string -> createdBy: string -> createdAt: string -> byte[]`.

- [ ] **Step 1: Write the failing tests** (`SyncManifestTests.fs`):
  - `classifyFormat`: `"total-recall/v1"` is `Supported`; `"total-recall/v2"` is `Newer 2`; `"total-recall/v10"` is `Newer 10`; `"total-recall/v999999999"` is `Newer 999999999`; each of `"total-recall/v1000000000"` (10 digits), `"total-recall/v0"`, `"total-recall/v01"`, `"total-recall/v-1"`, `"total-recall/v1.0"`, `""`, `"Total-Recall/v2"`, `"other/v2"` is `Unrecognised` (decision 2).
  - `isValidRepoId`: true for 32 lowercase hex digits; false for 31, 33, upper case, a non-hex letter, empty.
  - `render "0123456789abcdef0123456789abcdef" "total-recall 4.7.0" "2026-11-15T10:00:00Z"` equals the UTF-8 bytes of `{"format":"total-recall/v1","repo_id":"0123456789abcdef0123456789abcdef","created_by":"total-recall 4.7.0","created_at":"2026-11-15T10:00:00Z"}` + `"\n"` (no byte-order mark); `created_by` of `"a\"b\\c\n\t\u0001é"` renders as `a\"b\\c\n\t\u0001é` (é as UTF-8); a lone surrogate, a bad `repoId` and a `createdAt` that is not `YYYY-MM-DDTHH:MM:SSZ` each throw `ArgumentException`.
  - FsCheck: `classifyFormat` never throws.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Core.Tests/TotalRecall.Core.Tests.fsproj --filter "FullyQualifiedName~SyncManifest"`. Expected: build failure (module missing).
- [ ] **Step 3: Implement `SyncManifest.fs`** as above (decisions 1, 2). `classifyFormat` is a hand-written check of the `total-recall/v` prefix and a digit run, not a regex.
- [ ] **Step 4: Run the same command.** Expected: PASS.
- [ ] **Step 5: Commit** `git add src/TotalRecall.Core tests/TotalRecall.Core.Tests && git commit -m "Add the sync manifest rules"`.

### Task 2: Identity rule and host fingerprint (Core)

**Files:**
- Create: `src/TotalRecall.Core/SyncIdentity.fs`; Test: `tests/TotalRecall.Core.Tests/SyncIdentityTests.fs`
- Modify: both `.fsproj` files.

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces (module `TotalRecall.Core.SyncIdentity`): `MetaKeys` literals `Remote = "git_sync_remote"`, `RepoId = "git_sync_repo_id"`, `Machine = "git_sync_machine"`, `Host = "git_sync_host"`, `PublicCheck = "git_sync_public_check"`; `StoredIdentity`, `IdentityInput`, `NotJoinedReason`, `JoinState`; `evaluate : IdentityInput -> JoinState`; `reasonText : NotJoinedReason -> string`; `hostFingerprint : osMachineId: string -> canonicalPath: string -> string`.

- [ ] **Step 1: Write the failing tests:**
  - `evaluate` for each reason in decision 4's order: no configured remote is `NotJoined NotConfigured`; configured but no stored remote is `NeverJoined`; stored remote but no stored `RepoId` is `NeverJoined`; configured `"a"` vs stored `"A"` (and a trailing slash) is `RemoteChanged`; `CurrentHost = Some "h2"` vs stored host `Some "h1"` is `DataDirectoryMoved`; stored host `None` with `CurrentHost = Some _` is `DataDirectoryMoved`; `ManifestRepoId = Some "y"` vs stored `"x"` is `RepositoryReplaced`; everything agreeing is `Joined`; `CurrentHost = None` skips the host check (stored host `Some "h1"`, otherwise agreeing, is `Joined`); a case failing the remote check and the host check reports `RemoteChanged` (the earlier).
  - FsCheck: `evaluate` is total; with `CurrentHost = Some h` and every input agreeing, `Joined`; replacing any one input by a different `Some` value (or a different configured remote or stored text) is never `Joined`; replacing `CurrentHost` or `ManifestRepoId` by `None` skips that check and stays `Joined`.
  - `reasonText DataDirectoryMoved` equals ``"this data directory moved or was copied: run `total-recall sync join`"``; the other four texts are the wording of the spec's decision 4 table (the plan fixes them: `NotConfigured` "git sync has no [git_sync] remote configured", `NeverJoined` ``"this machine has not joined a sync repository: run `total-recall sync init` or `sync join`"``, `RemoteChanged` ``"[git_sync] remote differs from the repository this machine joined: run `total-recall sync join`"``, `RepositoryReplaced` ``"the sync repository was recreated (a different repo id): run `total-recall sync join`"``).
  - `hostFingerprint "m" "/a/db"` equals the first 32 lowercase hex digits of SHA-256 of the UTF-8 bytes `"total-recall/host/v1\nm\n/a/db"` (the test computes it with `SHA256` in the test); a different machine id or path gives a different value; the empty machine id is allowed.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~SyncIdentity"`. Expected: build failure.
- [ ] **Step 3: Implement `SyncIdentity.fs`** (decisions 4, 5, 6).
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the sync identity rule and the host fingerprint"`.

### Task 3: Public-repository rules (Core)

**Files:**
- Create: `src/TotalRecall.Core/SyncPublic.fs`; Test: `tests/TotalRecall.Core.Tests/SyncPublicTests.fs`
- Modify: both `.fsproj` files.

**Interfaces:**
- Produces (module `TotalRecall.Core.SyncPublic`): `GitHubSlug`; `parseGitHubSlug : string -> GitHubSlug option`; `PublicVerdict`; `ofStatus : int option -> PublicVerdict`; `slugKey : GitHubSlug -> string` (first 16 lowercase hex of SHA-256 of `owner/repo` lower-cased); `formatCache : GitHubSlug -> int64 -> string` (`v1|<slugKey>|<unix seconds>`); `parseCache : string -> (string * int64) option`; `isFresh : nowUnix: int64 -> cachedAt: int64 -> bool`; `unknownNotice : string -> string`; `skippedNotice : string -> string`.

- [ ] **Step 1: Write the failing tests:**
  - `parseGitHubSlug` returns `Some {Owner="o"; Repo="r"}` for `https://github.com/o/r`, `https://github.com/o/r.git`, `https://github.com/o/r/`, `http://GitHub.com/o/r`, `git://github.com/o/r.git`, `ssh://git@github.com/o/r.git`, `ssh://git@github.com:22/o/r.git`, `git+ssh://git@github.com/o/r`, `ssh+git://git@github.com/o/r`, `git@github.com:o/r.git`, `https://user:tok3n@github.com/o/r`; `None` for `https://github.com@evil.com/o/r`, `https://evil@github.com.evil.com/o/r`, `git@work-github:o/r.git`, `https://gitlab.com/o/r`, `/srv/git/r`, `C:\r`, `https://github.com/o`, `https://github.com/o/r/extra`, `https://github.com/o/.git`, `https://github.com/o/..`, `https://github.com/o/.`, `https://github.com//r`, `https://github.com/o/r%`, `https://ssh.github.com/o/r`, an empty string.
  - `ofStatus (Some 200) = Public`; `Some 404 = Private`; each of `Some 301`, `401`, `403`, `429`, `500`, `None` is `Unknown <non-empty reason>`.
  - `slugKey` is 16 lowercase hex and equal for `O/R` and `o/r`; `formatCache`/`parseCache` round trip and `parseCache` is `None` for `""`, `"v2|abc|1"`, `"v1|abc"`, `"v1|abc|x"`.
  - `isFresh`: `now - cachedAt = 86400` is true, `86401` is false; `cachedAt = now + 240` is true, `now + 301` is false (decision 8's five minutes).
  - `skippedNotice` and `unknownNotice` are non-empty, hold no URL and the skipped one says to keep the repository private.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~SyncPublic"`. Expected: build failure.
- [ ] **Step 3: Implement `SyncPublic.fs`** (decisions 7, 8). Parse by hand: find the scheme or the scp form, take the authority up to the first `/` (or `:` for scp), the host is what follows the **last** `@`, with an optional `:port` removed; compare the host to `github.com` ignoring case; split the path on `/` after removing one trailing `/`; require exactly two non-empty segments of `[A-Za-z0-9._-]`, strip one `.git` suffix from the second, then refuse an empty, `.` or `..` name.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the public repository rules"`.

### Task 4: The settings denylist, on every writing surface

**Files:**
- Create: `src/TotalRecall.Core/SyncSettingsGuard.fs`; Test: `tests/TotalRecall.Core.Tests/SyncSettingsGuardTests.fs`
- Modify: both `.fsproj` files; `src/TotalRecall.Server/Handlers/ConfigSetHandler.cs`; `src/TotalRecall.Cli/Commands/Config/SetCommand.cs`; `CHANGELOG.md`
- Test: `tests/TotalRecall.Server.Tests/ConfigSetHandlerTests.cs`, `tests/TotalRecall.Cli.Tests/Commands/Config/SetCommandTests.cs` (the existing file; add cases), `tests/TotalRecall.Web.Tests/ConfigSetGuardRouteTests.cs` (new)

**Interfaces:**
- Produces: `SyncSettingsGuard.isProtectedKey : string -> bool`; `SyncSettingsGuard.refusal : string -> string` (the message of decision 13 naming the key).

- [ ] **Step 1: Write the failing Core tests.** `isProtectedKey` is true for `"storage.mode"`, `"storage.connection_string"`, `"storage.mode.x"`, `"storage.connection_string.x"`, `"storage"`, `"git_sync"`, `"git_sync.remote"`, `"git_sync.a.b"`, `" Git_Sync . remote "`, `"STORAGE.MODE"`, `"storage . connection_string"`, `"storage..mode"`, `""`, `"."`, `"git_sync."`; false for `"storagex.mode"`, `"storage.other"`, `"git_syncx.remote"`, `"tiers.hot.max_entries"`, `"embedding.provider"`, `"x.storage.mode"`. `refusal "git_sync.remote"` contains the key and the words ``changed by you, in `config.toml`; an agent cannot change it`` and does not contain `sync init`.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~SyncSettingsGuard"`. Expected: build failure.
- [ ] **Step 3: Implement `SyncSettingsGuard.fs`** (decision 13): split on `.` (keeping empty segments), trim and lower-case (invariant) each; protected when any segment is empty, or the first is `git_sync`, or the first is `storage` with fewer than two segments, or with `mode` or `connection_string` as the second.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Write the failing surface tests.** `ConfigSetHandlerTests`: for each of `git_sync.remote`, `storage.mode`, `storage.connection_string`, `storage.mode.x`, `GIT_SYNC.interval_seconds`, `storage`, `ExecuteAsync` throws `ArgumentException` whose message contains the key, the config file does not exist afterwards, and a `tableProvider` that records calls was never called; `HappyPath` for `tiers.hot.max_entries` still writes. `SetCommandTests`: the same keys return exit code 2, stderr holds the refusal, the config file is not created and the snapshot store seam records no call; an ordinary key still returns 0. `ConfigSetGuardRouteTests` (Web): build a `ToolRegistry` with `new ConfigSetHandler(() => new TomlTable(), <temp config path>)`, start the app as `ApiIntegrationTests` does, POST `/api/tool/config_set` with `{"key":"git_sync.remote","value":"x"}` and the token, assert a non-success status or an error body naming the key and that the temp file does not exist; the same for `storage.mode`.
- [ ] **Step 6: Wire it in.** `ConfigSetHandler.ExecuteAsync`: after the non-empty `key` check, `if (SyncSettingsGuard.isProtectedKey(key)) throw new ArgumentException(SyncSettingsGuard.refusal(key))`, before the value is read. `SetCommand.Execute`: after the positional arguments are parsed and before the loader, the snapshot or the writer is touched, print `SyncSettingsGuard.refusal(key)` to stderr and return 2. Add to `CHANGELOG.md` a new `## Unreleased` section above `## 4.8.0` (the top section is already released) with a `### Changed` entry in the style of the existing ones: `total-recall config set` and the `config_set` tool now refuse `storage.mode`, `storage.connection_string` and every `git_sync` setting; change them by hand in `config.toml` (#128, part of #93).
- [ ] **Step 7: Run** the Server, Cli and Web test projects filtered to the new and changed classes. Expected: PASS.
- [ ] **Step 8: Deliberate break:** stop treating `mode` and `connection_string` as protected beneath `storage`; the `storage.mode` and `storage.connection_string` cases fail. Restore it.
- [ ] **Step 9: Commit** `git commit -m "Refuse changes to the storage mode and the sync settings from config set"`.

### Task 5: The manifest file codec (Infrastructure)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/ManifestFile.cs`; Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/ManifestFileTests.cs`

**Interfaces:**
- Consumes: `SyncManifest.classifyFormat`, `isValidRepoId`, `render`, `MaxManifestBytes`, `MaxLookBytes`.
- Produces: `public sealed record ParsedManifest(string Format, string RepoId, string? CreatedBy, string? CreatedAt)`; `public enum ManifestParseKind { Ok, Newer, Invalid }`; `public sealed record ManifestParse(ManifestParseKind Kind, ParsedManifest? Manifest, int NewerVersion, string? Reason)`; `public static class ManifestFile { public static ManifestParse Parse(ReadOnlySpan<byte> bytes); public static byte[] Render(string repoId, string createdBy, string createdAt); }` (`Render` calls the Core function).

- [ ] **Step 1: Write the failing tests** (`ManifestFileTests.cs`): valid single-line manifest is `Ok` with the fields; keys in another order and an unknown key are `Ok`; `created_by` and `created_at` absent are `Ok` with null; each of these is `Invalid` with a non-empty `Reason`: not JSON, a root that is an array, a duplicate `repo_id`, `repo_id` upper case, `repo_id` 31 digits, `format` a number, no `format`, no `repo_id`, `format` `total-recall/v0`, `other`, invalid UTF-8 bytes (`0xFF`), a BOM before a `v1` manifest, a `v1` manifest of exactly 4096 bytes is `Ok` (pad `created_by`) and 4097 is `Invalid`; each of these is `Newer` with `NewerVersion = 2`: `{"format":"total-recall/v2"}` alone, the same with a BOM, the same padded to 60000 bytes, with a duplicate key, with `repo_id` of the wrong shape, and `{"format":"total-recall/v2","repo_id":5}`; `{"format":"total-recall/v2","created_by":"<byte 0xFF>"}` (invalid UTF-8) is `Invalid` although it names a newer format; `Parse` of a span over 65536 bytes is `Invalid` (the cap is the caller's, `RepoGuard` never passes more); `Render` then `Parse` round trips.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~ManifestFileTests"`. Expected: build failure.
- [ ] **Step 3: Implement `ManifestFile.cs`** (decisions 1, 2): first validate the whole span as UTF-8 with a strict `UTF8Encoding(false, true)` decode (invalid UTF-8 is `Invalid`; `Utf8JsonReader` does not check it while scanning); then a permissive look (a `Utf8JsonReader` over the root object after skipping a leading BOM, reading only top-level property names, taking the first `format` string; any exception means no look) returning `Newer` when `classifyFormat` says so; otherwise the strict parse (reject a BOM, size over 4096, invalid UTF-8, duplicates by walking top-level property tokens with a `HashSet<string>`, a non-string `format`, `Unrecognised`, a bad `repo_id`).
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the manifest file codec"`.

### Task 6: Host identity and the stored identity (Infrastructure)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/HostIdentity.cs`, `src/TotalRecall.Infrastructure/GitSync/GitSyncIdentityStore.cs`; Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/HostIdentityTests.cs`, `GitSyncIdentityStoreTests.cs`

**Interfaces:**
- Consumes: `SyncIdentity.hostFingerprint`, `SyncIdentity.StoredIdentity`, `SyncIdentity.MetaKeys`, `IMetaStore`.
- Produces: `public enum MachineIdKind { Found, Absent, Failed }`; `public readonly record struct MachineIdResult(MachineIdKind Kind, string? Id)`; `public sealed class HostIdentity { public HostIdentity(Func<MachineIdResult> readMachineId, string databasePath, bool caseInsensitive); public static HostIdentity ForCurrentMachine(string databasePath); public string? Fingerprint { get; } /* null = unavailable; computed once */ public string? CanonicalPath { get; } /* null when resolution failed */ }`; `public static class MachineIdReader { public static MachineIdResult Read(); internal static MachineIdResult FromFileText(string? text, bool fileExists, bool readFailed); internal static string? ParseIoreg(string output); internal static string? Clean(string? raw); }`; `public sealed class GitSyncIdentityStore(IMetaStore meta) { public SyncIdentity.StoredIdentity Read(); }`.

- [ ] **Step 1: Write the failing tests:**
  - `Clean`: trims; `""`, whitespace and a value over 128 characters or with a control character are `null`; a 128-character value is kept. `ParseIoreg` reads `"IOPlatformUUID" = "AAAA-BBBB"` from realistic `ioreg` output and returns `null` for output without it. `FromFileText`: existing file with usable text is `Found`; missing file is `Absent`; existing with blank text is `Absent`; a read failure is `Failed`.
  - `HostIdentity`: with a `Found "m"` source the fingerprint equals `SyncIdentity.hostFingerprint "m" <canonical path>`; `Absent` uses the empty machine id; `Failed` gives a `null` fingerprint (and `HostUnverified` is what `RepoGuard` derives); the fingerprint is computed once (a counting source is read once across two reads). Canonical path: a database reached through a symlinked directory (`[Unix-only]` test, skipped on Windows) gives the same fingerprint as the real path; a trailing separator is removed; with `caseInsensitive: true` the path is lower-cased with the invariant culture, with `false` it is not; a path-resolution exception (a symlink loop, created on Unix) makes the fingerprint `null`; a `\\?\` prefix is stripped (a pure helper test on the string).
  - `GitSyncIdentityStore.Read` over a store whose `_meta` holds the three keys returns them; an empty value is `None`; missing keys are `None`. Use a real `SqliteStore` over an in-memory database with migrations, as other Infrastructure tests do.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~HostIdentityTests|FullyQualifiedName~GitSyncIdentityStoreTests"`. Expected: build failure.
- [ ] **Step 3: Implement** (decision 5): `MachineIdReader.Read` per operating system (Linux the two files in order, the first `Found` wins, an `Absent` file falls through to the next and a `Failed` file makes the result `Failed` (an internal overload `Read(Func<string, (bool Exists, string? Text, bool Failed)> readFile)` is the seam, tested for each combination); macOS `/usr/sbin/ioreg -rd1 -c IOPlatformExpertDevice` started with `ProcessStartInfo` (no shell, redirected output, 3-second kill; a non-zero exit, a timeout or an exception is `Failed`; a missing `/usr/sbin/ioreg` is `Absent`); Windows `Registry.LocalMachine` with `RegistryView.Registry64` guarded by `OperatingSystem.IsWindows()`). The canonical path walks the full path one component at a time, replacing a component by `ResolveLinkTarget(returnFinalTarget: true)` when it is a link, and returns `null`-fingerprint on any exception.
- [ ] **Step 4: Run.** Expected: PASS (the Unix-only tests skip on Windows with a reason).
- [ ] **Step 5: Commit** `git commit -m "Add the host identity and the stored identity reader"`.

### Task 7: The public check (Infrastructure)

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/PublicRepoProbe.cs`, `src/TotalRecall.Infrastructure/GitSync/RepoGuard.Public.cs` (the public half of the partial class `RepoGuard`; the other half is Task 8)
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/PublicRepoProbeTests.cs`, `RepoGuardPublicTests.cs`, and the new `LoopbackFact.cs` (a `FactAttribute` whose constructor tries to bind a loopback `HttpListener` and sets `Skip` when it cannot)

**Interfaces:**
- Consumes: Task 3 functions, `IMetaStore`, `SyncGit.redact`.
- Produces: `public interface IPublicRepoProbe { Task<int?> GetStatusAsync(string owner, string repo, CancellationToken cancellationToken); }`; `public sealed class PublicRepoProbe : IPublicRepoProbe, IDisposable { public PublicRepoProbe(Uri? baseAddress = null, HttpMessageHandler? handler = null); internal PublicRepoProbe(Uri? baseAddress, HttpMessageHandler? handler, TimeSpan? timeout); }` (default base `https://api.github.com/`); `public sealed record PublicCheckResult(SyncPublic.PublicVerdict Verdict, string? Notice, bool FromCache)`; `public sealed partial class RepoGuard` with `public Task<PublicCheckResult> CheckPublicAsync(CancellationToken cancellationToken = default)` and the constructor `public RepoGuard(Core.Config.GitSyncConfig settings, IMetaStore meta, GitClient? client, HostIdentity host, IPublicRepoProbe probe, Func<DateTimeOffset> clock)` (declared here, shared with Task 8).

- [ ] **Step 1: Write the failing tests.** `PublicRepoProbeTests` (the header, status and redirect cases through a loopback `HttpListener` with the probe's base address pointed at it, guarded by `LoopbackFact`, because redirects are followed by the probe's own `HttpClientHandler`, which a supplied stub handler replaces; only the host-mismatch and exception cases use stub `HttpMessageHandler`s): the request path is `/repos/o/r`, there is no `Authorization` and no `Cookie` header, `Accept` is `application/vnd.github+json`, `User-Agent` is `total-recall`; `200` returns 200 and `404` returns 404; a `301` with a relative `Location: /repositories/1` then `200` returns 200 (listener); a chain of 4 redirects returns 301 (listener; `ofStatus` maps it to `Unknown`); a stub handler returning `200` with `RequestMessage` set to a request for `https://evil.example/x` returns `null` (this fails if the host check is deleted); a listener that never answers returns `null` within the probe's limit (the internal constructor argument `TimeSpan? timeout` is set to 300 ms in the test; the default is 5 seconds); a cancellation from the caller's token propagates as `OperationCanceledException`, and only the probe's own timeout maps to `null`; a stub handler that throws `HttpRequestException` returns `null`. `RepoGuardPublicTests` (stub probe counting calls, a settable clock, a real in-memory `SqliteStore` for `_meta`): a `https://github.com/o/r` remote with status 200 is `Public`, 404 is `Private` and the cache key is written as `v1|<slugKey>|<seconds>`, 403 / `null` is `Unknown` with a notice on the first call and `null` notice on the second call of the same instance, a `git@work-github:o/r.git` remote is `Skipped` with a notice once and no probe call, a remote with `https://user:tok3n12345@github.com/o/r` is probed as `o/r` and no result field contains `tok3n12345`; a cached private answer 23 h 59 min old gives `FromCache = true` and no probe call, 24 h + 1 s asks again, a cache for another repository asks again, a cache stamped 6 minutes ahead asks again, 4 minutes ahead is used; a `Public` answer is never written to the cache; an unknown answer is not cached; two successive calls in one instance with a 200 each call the probe twice (no verdict memo).
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~PublicRepoProbeTests|FullyQualifiedName~RepoGuardPublicTests"`. Expected: build failure.
- [ ] **Step 3: Implement** (decisions 7, 8, 9): the probe uses an `HttpClientHandler` with `UseCookies = false`, `UseDefaultCredentials = false`, `MaxAutomaticRedirections = 3`, `AllowAutoRedirect = true`, the system proxy, and `HttpCompletionOption.ResponseHeadersRead`; after the response it compares `response.RequestMessage.RequestUri.Host` with the base address host and returns `null` on a mismatch. `CheckPublicAsync` follows decision 7's order: no remote means `Skipped`; `parseGitHubSlug` none means `Skipped` with the notice (once per instance); the cache read through `meta.GetMeta(SyncIdentity.MetaKeys.PublicCheck)`; the probe; `ofStatus`; on `Private` write the cache with `meta.SetMeta`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "Add the public repository check"`.

### Task 8: The identity half of `RepoGuard`

**Files:**
- Create: `src/TotalRecall.Infrastructure/GitSync/RepoGuard.cs`
- Test: `tests/TotalRecall.Infrastructure.Tests/GitSync/RepoGuardIdentityTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 5, 6, 7 (the constructor), the 1a-1 `GitClient` (`LsTreeRootAsync`, `CatBlobAsync`, `CloneDirectory`), `GitSyncPaths`.
- Produces: `public enum ManifestReadKind { NoClone, NoManifest, Invalid, Unreadable, Newer, Ok }`; `public sealed record ManifestRead(ManifestReadKind Kind, ParsedManifest? Manifest, int NewerVersion, string? Reason)`; `public sealed record GuardVerdict(SyncIdentity.JoinState Join, ManifestRead Manifest, bool HostUnverified) { public bool CanSync { get; } }`; on `RepoGuard`: `public Task<ManifestRead> ReadManifestAsync(SyncGit.Revision? revision = null, CancellationToken cancellationToken = default)` and `public Task<GuardVerdict> EvaluateIdentityAsync(SyncGit.Revision? revision = null, CancellationToken cancellationToken = default)`.

- [ ] **Step 0: Reuse the existing plumbing helper.** `PlumbingRepo` (`tests/TotalRecall.Infrastructure.Tests/GitSync/GitClientTreeTests.cs`) already builds commits with arbitrary entry modes through `hash-object` and `update-index --cacheinfo` and `commit-tree` under a written identity; change it from private to `internal` if it is not, and use it for every manifest shape (`Add("120000", ...)`, `Add("160000", ...)`, a directory case as `Add("100644", blob, "manifest.json/x")`, which makes `manifest.json` a tree). Tests make the clone with plain git (`GitTestEnvironment.RunGit(... "clone", <bare or plumbing remote>, <GitSyncPaths.CloneDirectory>)` and `fetch`), never through the recorded client, so the recorder holds only the guard's calls.
- [ ] **Step 1: Write the failing tests** (real `git` through `GitTestEnvironment`/`BareRemote`/`[GitFact]`; each test builds its own `new RecordingGitProcess(new GitProcess())` and its own `GitClient` over it, used only by the guard (not the shared `ClientRig`, whose recorder sees other tests' calls); a real in-memory `SqliteStore` for `_meta`):
  - `ReadManifestAsync`: no `.git` under the clone directory (directory absent, and an empty directory) is `NoClone` with no process started; a clone with a valid manifest is `Ok` with the `repo_id`; a clone without `manifest.json` is `NoManifest`; a clone whose `manifest.json` is a symlink (mode 120000), a directory (040000) or a submodule entry (160000) is `Invalid`; a newer-format manifest is `Newer 2`; a half-made clone (`.git` present, no commit) is `Unreadable`; a `manifest.json` of 70000 bytes is `Unreadable` (the 64 KiB read cap fails the blob read), also when it names a newer format at 65537 bytes; one of 4097 bytes at `v1` is `Invalid`.
  - Revision: a clone whose `HEAD` holds manifest `repo_id` A and whose `refs/remotes/origin/main` holds B (make the commit in a second clone, push, `fetch` in the first without resetting) returns A for `Head` and B for `RemoteBranch "main"`.
  - `EvaluateIdentityAsync`, each with `_meta` set by `SetMeta` and the settings built with `GitSyncSettings.normalise`: not configured; never joined; remote changed; data directory moved (stored host differs from the host identity's fingerprint); repository replaced (stored `repo_id` differs from the clone's manifest); all agreeing is `Joined` with `CanSync` true; a host source that fails gives `HostUnverified = true`, `Join = Joined` and `CanSync = false` even though everything else agrees; with a fixed `HostIdentity` (a `Found "m"` source and a fixed path) the stored host equal to `SyncIdentity.hostFingerprint "m" <path>` and nothing else read from the machine, the verdict is `Joined` (the hostname is not an input); joined with no clone is `Joined` with `NoClone` and `CanSync` true; joined with a newer manifest has `CanSync` false and `Manifest.Kind = Newer`; joined with a missing, invalid or unreadable manifest has `CanSync` false.
  - Over every case above, the `RecordingGitProcess` records no command that is `Clone`, `LsRemote`, `Fetch`, `Push`, `AddOrigin` or `SetOriginUrl` (decision 3).
  - A guard built with a `null` client and no remote returns `NotJoined NotConfigured` without touching the client; one with a configured remote and a `null` client reads the manifest as `NoClone`.
- [ ] **Step 2: Run** `... --filter "FullyQualifiedName~RepoGuardIdentityTests"`. Expected: build failure.
- [ ] **Step 3: Implement `RepoGuard.cs`** (decisions 3, 4, 5, 6): `NoClone` is decided from the file system (`.git` absent under `CloneDirectory`) without a process; otherwise `LsTreeRootAsync(revision ?? Head)`, find the entry named exactly `manifest.json`, require mode `100644` or `100755` and kind `blob`, `CatBlobAsync(sha, 65536)`, then `ManifestFile.Parse`. Any failed `GitOutcome` is `Unreadable` with a redacted reason. `EvaluateIdentityAsync` reads the settings' remote, `new GitSyncIdentityStore(meta).Read()`, `host.Fingerprint`, and the manifest's `repo_id` only for `Ok`, calls `SyncIdentity.evaluate`, and sets `HostUnverified = host.Fingerprint is null`. `CanSync` is `Join is Joined && !HostUnverified && Manifest.Kind is NoClone or Ok`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Deliberate break:** make `ReadManifestAsync` ignore the `revision` argument; the revision test fails. Restore it.
- [ ] **Step 6: Commit** `git commit -m "Add the repository identity guard"`.

### Task 9: `OpenGit`, the fallback and the re-embed edit

**Files:**
- Modify: `src/TotalRecall.Server/ServerComposition.cs` (`OpenProduction`, the new `OpenConfigured` and `OpenGit`, the catch filter), `src/TotalRecall.Cli/Commands/ReindexEmbeddingsCommand.cs` (`IsPostgres`)
- Test: `tests/TotalRecall.Server.Tests/ServerCompositionGitTests.cs` (new, with a `[CollectionDefinition("process-environment-console", DisableParallelization = true)]` in the same file), `tests/TotalRecall.Cli.Tests/Commands/ReindexEmbeddingsCommandTests.cs` (add)

**Interfaces:**
- Consumes: `GitExecutable.TryFind(string? pathVariable, bool? windows = null)`, `SyncGit.validateRemote(windows, remote)`, `GitSyncConfig` (`Remote`, `Branch`, `Warnings`), `SyncGit.redact`.
- Produces: `internal static ServerCompositionHandles OpenConfigured(Core.Config.TotalRecallConfig cfg, string? dbPath, string querySource, string? pathVariable)`; `internal static ServerCompositionHandles OpenGit(Core.Config.TotalRecallConfig cfg, string? dbPath, string querySource, string? pathVariable)`.

- [ ] **Step 1: Write the failing tests.** `ServerCompositionGitTests` (configs from `new ConfigLoader().LoadEffectiveConfig(tempConfigPath)`; the database in a temp directory; `TOTAL_RECALL_HOME` set to a temp directory for the test and restored in `finally`, because `OpenSqlite` creates `ConfigLoader.GetDataDir()` even when a database path is given; `Console.Error` replaced by a `StringWriter` and restored; the tests carry `[Collection("process-environment-console")]`): `[storage] mode = "git"` with `[git_sync] remote = "git@github.com:o/r.git"` and a `pathVariable` naming a temp directory that holds an empty file named `OperatingSystem.IsWindows() ? "git.exe" : "git"` opens, `handles.StorageMode == "git"`, the registry holds the same tool names as a `local` open of the same database, and no `git` process was started (the fake file is never executed: it is an empty file, so an attempt would fail the test by exception); with `normalise` warnings (`interval_seconds = 5`) the warning line appears once on stderr; no remote, a refused remote (`ext::sh`, which has no whitespace, so `normalise` keeps it and `validateRemote` refuses it), an unusable branch (`branch = ".bad"`), and a `pathVariable` that finds no `git` each return `StorageMode == "sqlite (git failed)"` and stderr holds `[total-recall] git storage failed, falling back to sqlite:` and the reason; a remote `https://user:tok3n12345@host/r.git` that is refused for another reason never leaks `tok3n12345` to stderr (use `ext::https://user:tok3n12345@h/x` as the refused remote). A mode of `cortex` or `postgres` with its usual failing setup still falls back with its own label (the existing catch behaviour). `ReindexEmbeddingsCommandTests` (the existing `ConsoleCapture` collection; `TOTAL_RECALL_HOME` set to a temp directory holding `config.toml` with `[storage]` `mode = "git"` and `connection_string = "Host=x"`, restored in `finally`): `RunAsync(["--db", <a path that does not exist>])` returns 1 and stderr contains `database not found` and does not contain `postgres backend not yet supported` (before the edit an unknown mode with a connection string counted as Postgres, so this fails without it); a second case with `mode = "postgres"` and the same connection string still returns 1 with the postgres message.
- [ ] **Step 2: Run** `dotnet test tests/TotalRecall.Server.Tests/TotalRecall.Server.Tests.csproj --filter "FullyQualifiedName~ServerCompositionGitTests"` and the Cli filter. Expected: build failure.
- [ ] **Step 3: Implement** (decisions 10, 11, 12; the break check: restore `m == "cortex" || m == "local"` in `IsPostgres` and the reindex test fails): `OpenProduction` loads the configuration and calls `OpenConfigured`; `OpenConfigured` holds the existing `configuredMode` switch, with a `"git" => OpenGit(...)` case and the catch filter `configuredMode is "cortex" or "postgres" or "git"`. `OpenGit` treats `cfg.GitSync` being `None` (no `[git_sync]` table) as no remote, and throws `InvalidOperationException` (message redacted with `SyncGit.redact`) when the remote is `None`, refused by `SyncGit.validateRemote(OperatingSystem.IsWindows(), remote)`, when `Branch` is `None`, or when `GitExecutable.TryFind(pathVariable)` is null; otherwise it writes each of `Warnings` to stderr once and returns `OpenSqlite(cfg, dbPath, "git", querySource)`. `IsPostgres` returns false for `"git"` (`m == "cortex" || m == "local" || m == "git"`).
- [ ] **Step 4: Run.** Expected: PASS; also run the whole Server and Cli test projects.
- [ ] **Step 5: Commit** `git commit -m "Open the git storage mode and fall back when it cannot work"`.

### Task 10: Documentation

**Files:**
- Modify: `src/TotalRecall.Core/AGENTS.md`, `src/TotalRecall.Infrastructure/AGENTS.md`, `src/TotalRecall.Server/Handlers/AGENTS.md`, `AGENTS.md` (root)

- [ ] **Step 1: Update the guides.** Core `AGENTS.md`: rows for the four new files and what each holds. Infrastructure `AGENTS.md`: rows for the new `GitSync/` files, and a paragraph "Git sync guards" (the identity rule and its order, `CanSync`, the host fingerprint and `HostUnverified`, the public check and its cache and notices, that nothing here fetches or writes). `Handlers/AGENTS.md`: the `config_set` refusal. Root `AGENTS.md`: the layout/WHERE-TO-LOOK rows for the guards and `OpenGit`, the sentence in the architecture section that the storage modes now include `git` (opens as the local store, no cycle yet), and the denylist.
- [ ] **Step 2: Check the guide claims** against the code with `grep` for each file name and each constant named in the paragraph.
- [ ] **Step 3: Commit** `git commit -m "Describe the git sync guards in the contributor guides"`.
- [ ] **Step 4: Verify the whole branch** (acceptance criterion 14; this is the profile's verify list): `dotnet test src/TotalRecall.sln --filter "Category!=Integration"` (every project passes; the only skips are the 3 existing Postgres tests plus the platform-conditional ones), `npm run test:shim` (also covers `scripts/release-notes.test.js` against the new CHANGELOG section), the catalog guard (`dotnet build src/TotalRecall.Host -c Release` then `dotnet run --project src/TotalRecall.Host -c Release --no-build -- dump-catalog | diff -u catalog.json -`, no output: no tool was added), and `dotnet publish src/TotalRecall.Host/TotalRecall.Host.csproj -c Release -r linux-x64 -p:PublishAot=true` with no warning other than the baseline `NU1903`. Record the result in the Finish report; no commit.
