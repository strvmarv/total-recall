# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **Gap: 0.6.8 (GA), 0.7.0, 0.7.1, 0.7.2.** These TypeScript releases shipped
> without changelog entries between 0.6.8-beta.5 and the 0.8.0 cutover. Backfill
> from `git log v0.6.8-beta.5..v0.7.2` is tracked in `docs/TODO.md`.

## 0.8.0-beta.7 - 2026-04-09

### Fixed

- **Migration guard cliff: `total-recall.db` and `total-recall.db.ts-backup` both present.** Beta tester upgrading through 0.8.0-beta.3..6 hit a hard dead-end on beta.6 startup: `migration failed: could not rename old database: The file '~/.total-recall/total-recall.db.ts-backup' already exists.` Reproduction: an earlier beta run (likely beta.3 or beta.4 before the AOT crash classes were fixed) made it past the rename phase, leaving a 5.4 MB `total-recall.db.ts-backup` with the user's real TS-era data. A subsequent step then created a fresh ~12 KB `total-recall.db` (either a partial init from a rolled-back transaction, or an empty SQLite shell from a failed `MigrationRunner.RunMigrations`). On the next startup the guard saw both files and the bare `File.Move(dbPath, backupPath)` threw `IOException` because the target already existed. The user was stuck — recovery required hand-editing files in `~/.total-recall/`.

  Root cause: the previous `CheckAndMigrateAsync` state machine modeled only two pre-migration states (fresh-TS-needs-migration vs. already-migrated). The real state space is **five**: `NotPresent`, `EmptyFile`, `TsFormat`, `PartialNetEmpty`, `PartialNetPopulated`, plus the steady-state `NetMigrated`. The guard had no concept of "the previous attempt got partway and left both files behind" and treated any unrecognized state as a fatal collision.

  Fix: refactor `AutoMigrationGuard.CheckAndMigrateAsync` into an explicit state machine driven by a new read-only `InspectDbFormat()` helper. The full transition table lives in the class xmldoc; key invariants:

  - **Never delete anything.** Suspect files are renamed to `<dbPath>.failed-migration-<utc>` so unique data is recoverable by hand even after the guard auto-resumes.
  - **The `.ts-backup` is treated as authoritative.** The guard never creates a backup except by renaming `dbPath`, so its existence is *proof* that `dbPath` was once renamed there. If a fresh `dbPath` shows up alongside an existing backup, the backup wins.
  - **Inspection is read-only.** The previous `TryReadMarker` silently `CREATE TABLE IF NOT EXISTS _meta`-d on every peek, mutating TS-era DBs even on no-op runs. The new `InspectDbFormat` opens with `Mode=ReadOnly` so the file is never touched until the state machine decides to act.

  6 new test cases extend the existing 6 to cover all 5 transitions (`Resume_TsAtDb_BackupExists_*`, `Resume_PartialNetEmpty_*`, `Resume_PartialNetPopulated_*`, `Resume_NoDb_BackupOnly_*`, `EmptyShellAtDb_BackupExists_*`). Each test asserts the right `GuardResult`, the right migrator invocation, the marker presence, and that any sidelined file is still on disk via a new `FailedMigrationSidelineCount()` helper. Total project test count: 944 (up from 938).

- **`total-recall-win-x64.zip` was actually a POSIX tar archive.** Verified after the v0.8.0-beta.6 release pipeline went green: `file release-assets/total-recall-win-x64.zip` reported `POSIX tar archive (GNU)`. Root cause: `release.yml`'s win-x64 staging step used `tar -C binaries/win-x64 -a -cf release-assets/total-recall-win-x64.zip .` and asserted in a comment that `bsdtar -a auto-detects the format from the extension, producing a standard .zip`. That's true on macOS where `tar` is bsdtar/libarchive — but the publish job runs on `ubuntu-latest` where `tar` is **GNU tar**, whose `-a`/`--auto-compress` selects a *compression program* from the suffix (gzip/bzip2/xz), not an *archive format*. For `.zip` it falls through to plain uncompressed tar. Result: a tar file with a misleading `.zip` extension. Windows users using Explorer's built-in zip handling, 7-Zip without auto-detection, etc. would fail with "not a zip file"; only Windows tar.exe (bsdtar) handled the misnamed file successfully via libarchive's format auto-detect.

  Fix: switch the win-x64 leg to `.tar.gz` like every other RID. `release.yml`'s `Stage per-RID release assets` step now produces `total-recall-win-x64.tar.gz` via `tar -C binaries/win-x64 -czf release-assets/total-recall-win-x64.tar.gz .`, and the `Attach archives to GitHub release` files list updated accordingly. `scripts/fetch-binary.js`'s `getArchiveName()` now always returns `.tar.gz` regardless of RID, and `extractArchive()` collapses to a single `tar -xzf` invocation — no more isZip branch, no more `Expand-Archive` PowerShell fallback. Windows 10+ ships `tar.exe` (bsdtar/libarchive) since build 17063 / 1803 (April 2018), which handles `.tar.gz` natively. Single archive format, single extraction code path, simpler than what we had.

### Credits

- Migration guard fix delivered by the Mac dogfood agent (commit `7ce54db`). The fix was triggered by the user's actual recovery experience — they had to manually `mv ~/.total-recall/total-recall.db ~/.total-recall/total-recall.db.failed-migration-cruft && mv ~/.total-recall/total-recall.db.ts-backup ~/.total-recall/total-recall.db` to unblock beta.6. This commit makes that recovery automatic.
- `total-recall-win-x64.zip` archive bug discovered by the build agent during post-tag verification of v0.8.0-beta.6 (downloading and inspecting the live release asset via `file`). Mac agent's commit `b5564a3` introduced the bug; tested locally on macOS where bsdtar is the default `tar` and didn't surface the GNU tar / bsdtar divergence. Fixed in this commit by unifying on `.tar.gz` for all RIDs.
- Build agent verified on linux-x64 before bumping: `dotnet build` 0/0, `dotnet test` 944 passing (up from 938 with the new guard tests), happy-path AOT publish produces `runtimes/vec0.so`, tar round-trip on the new win-x64 path produces real `gzip compressed data` (verified via `file`) that extracts cleanly with `runtimes/vec0.so` in place.

## 0.8.0-beta.6 - 2026-04-09

### Fixed

- **`sqlite-vec` native library missing from non-linux-x64 publish trees.** Beta tester on macOS reported `sqlite-vec native library not found at .../runtimes/vec0.dylib` on first DB open, even though the v0.8.0-beta.5 archive download and extraction (Fix A from beta.5) worked correctly. Root cause: `package-lock.json` was originally regenerated on a linux-x64 host (in the `0.8.0-beta.1` Task 3 strip commit `4394138`) using a plain `npm install` that only resolved `sqlite-vec`'s `optionalDependencies` for the host platform. The other four RID variants (`sqlite-vec-linux-arm64`, `sqlite-vec-darwin-x64`, `sqlite-vec-darwin-arm64`, `sqlite-vec-windows-x64`) appeared as bare names in the parent's `optionalDependencies` block but had no resolution / integrity entries of their own. Result: `npm ci` on any non-linux-x64 CI matrix leg never installed the matching variant, the Infrastructure csproj's `<Content Include="..." Condition="Exists(...)">` copy step silently no-op'd, and the publish tree shipped without `runtimes/vec0.<ext>`. This is the textbook **npm optional-dependency platform-locked lockfile** footgun and had been latent on every CI matrix leg other than linux-x64 since beta.1; it only became visible at runtime now that beta.5's archive download/extract path was working correctly enough to expose what was missing inside the archive.

  Fix: regenerated `package-lock.json` with `npm install --package-lock-only --force --os=darwin --cpu=arm64`. The `--force` flag tells npm 11 to fully resolve every optional dep variant in one pass. The new lockfile contains per-RID entries for all 5 variants with correct os/cpu metadata, integrity hashes, and resolved URLs. `npm ci` on each platform still installs only the matching variant (verified locally on linux-x64: installs exactly `sqlite-vec` + `sqlite-vec-linux-x64`). Side effect: regeneration also pruned 299 orphan TypeScript-era lockfile entries (`@babel/*`, `@types/*`, `esbuild`, `vitest`, `@modelcontextprotocol/sdk`, etc.) that survived the original 0.8.0-beta.1 strip. Lockfile shrunk from 3995 lines to 107.

- **Silent failures when sqlite-vec is missing from a publish tree.** New `<Target Name="VerifyVecExtensionPublished" AfterTargets="Publish">` in `src/TotalRecall.Host/TotalRecall.Host.csproj` checks for `runtimes/vec0.{so,dylib,dll}` after publish completes and emits an MSBuild `<Error>` if none are found. The error message names the active RuntimeIdentifier and includes explicit fix instructions (`run npm ci, verify package-lock.json contains a per-RID entry for your target`). Verified locally on linux-x64: happy-path publish with `vec0.so` present exits 0; negative-path publish with `node_modules/sqlite-vec-linux-x64` hidden exits 1 with the new diagnostic. If beta.5's CI run had this target in place, the build agent would have seen a clear failure at the publish step instead of shipping a tarball that crashed on first DB open.

### Known limitations

- Fix A regenerated the lockfile from the Mac agent's machine, which had npm 11.11.0 installed. Older npm versions may not honor `--force` the same way, and contributors regenerating the lockfile in the future need to either use npm 11+ or pass `--os` / `--cpu` flags to manually populate each RID entry. A `docs/TODO.md` follow-up should add a `scripts/regenerate-lockfile.sh` wrapper that captures the right invocation, plus a CI check that verifies the lockfile still has all 5 RID entries.
- Fix B (an explicit `npm install --no-save sqlite-vec-<rid>` step in each release.yml matrix leg as a belt-and-suspenders safety net) was intentionally skipped — Fix A removes the root cause and Fix C catches any regression strictly more generally. If Fix A ever regresses (someone commits a single-platform lockfile), Fix C will fail the publish step with a clear error and the bisect points straight at the regressing commit.

### Credits

- Diagnosis and fixes delivered by the Mac dogfood agent (commit `0386443`). Build agent verified on linux-x64: `dotnet build` 0/0, `dotnet test` 938 passing, `dotnet publish -r linux-x64` happy-path produces `runtimes/vec0.so`, negative-path test (with `sqlite-vec-linux-x64` hidden from node_modules) correctly fails with exit code 1 and the new diagnostic message naming the missing RID. Lockfile structure verified: 5 sqlite-vec entries with correct os/cpu metadata, `npm ci` installs only the host-matching variant.

## 0.8.0-beta.5 - 2026-04-09

### Fixed

- **AOT publish tree shipped as compressed archive instead of bare executable.** v0.8.0-beta.4 attached only the executable to the GitHub Release per RID, but the .NET AOT binary P/Invokes into sibling native libraries (`libonnxruntime.{dylib,so,dll}` via `Microsoft.ML.OnnxRuntime` and `vec0.*` via sqlite-vec) that live next to it in the publish tree. Every fresh `claude /plugin update` install on a `source: github` marketplace entry crashed at first DB open with `TypeInitializationException` -> `DllNotFoundException: libonnxruntime.dylib`. The npm tarball install path was unaffected because the tarball already shipped the full tree. `release.yml` now stages each per-RID publish tree into `total-recall-<rid>.tar.gz` (Unix) or `total-recall-<rid>.zip` (Windows) and attaches the archives as Release assets. `scripts/fetch-binary.js` downloads the matching archive into `os.tmpdir()`, extracts it via system `tar` (or `tar.exe` / `Expand-Archive` on Windows), verifies the expected executable, and restores the +x bit.
- **Opaque exception messages at boundary catches.** Beta tester sessions were blocked for ~30 minutes by `migration guard threw: A type initializer threw an exception` because Program.cs's migration-guard catch wrote only `ex.Message` — the real `DllNotFoundException` naming the missing library was buried in `ex.InnerException`. New `src/TotalRecall.Infrastructure/Diagnostics/ExceptionLogger.cs` provides `LogChain(prefix, ex)` that writes the outer exception type+message, walks the entire `InnerException` chain with indented `-> <Type>: <Message>` lines, then the outer stack trace. AOT-safe (uses the first-class `InnerException` property, not reflection). Retrofitted 10 boundary catches across `Program.cs` (migration guard + composition) and the CLI commands (`StatusCommand`, `Memory/{History,Inspect,Export,Lineage}Command`, `Kb/{List,Refresh,Remove}Command`).
- **`total-recall --version` reported `0.1.0` regardless of build version.** `CliApp.cs` had `private const string AppVersion = "0.1.0"` baked in at compile time. New `ResolveAppVersion()` walks `Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()` at runtime, strips any SourceLink `+<sha>` suffix, falls back to `Assembly.Version.ToString(3)`, then `"unknown"`. AOT-safe — assembly attributes are metadata and survive trimming. `release.yml`'s `Publish (Unix/Windows)` steps now pass `-p:Version=${REF_NAME#v}` and `-p:InformationalVersion=${REF_NAME#v}` to `dotnet publish` so CI builds carry the tag string. `REF_NAME` is passed via `env:` and dereferenced as `"$REF_NAME"` per GitHub Actions script-injection guidance.
- **Stale `git-hooks/pre-commit` ran `npm run build` on src/ commits.** Leftover from the TypeScript era when `tsup` built `dist/index.js` from `src/`. After the 0.8.0 strip, `npm run build` no longer exists in `package.json`, so any commit touching `src/TotalRecall.*.cs` would fail the hook. Replaced with a no-op + comment explaining why (and how to run `dotnet build` manually for a local fast-feedback loop).

### Known limitations

- `src/TotalRecall.Server/McpServer.cs` still has `private const string ServerVersion = "0.1.0"`, reported in the MCP `initialize` response (`serverInfo.version`). Moving it to the same dynamic lookup as `CliApp.AppVersion` requires updating `tests/TotalRecall.Server.Tests/McpServerTests.cs:76` which asserts the exact string. Deferred to a follow-up PR.
- Five additional catch sites in `Memory/{Promote,Demote,Import}Command`, `MigrateCommand`, `ImportHostCommand` were not retrofitted with `ExceptionLogger.LogChain` because no concrete failure was observed there. Same pattern applies; can be propagated later if a real failure surfaces.

### Credits

- Diagnosis and fixes A/B/C delivered by the Mac dogfood agent. Build agent verified `dotnet build` (0/0), `dotnet test` (938 passing), `dotnet publish -r linux-x64 --aot` with the new `-p:Version` flag (binary reports correct version, sibling natives load), and the tar round-trip shape before tagging.

## 0.8.0-beta.4 - 2026-04-09

### Fixed

- **Install-path gap for git-clone installs.** When a host tool fetches total-recall via `git clone` rather than `npm install` (e.g. Claude Code `/plugin update` against a marketplace entry whose `source` is `github`), the installed tree has no `binaries/` because prebuilt binaries are never committed to git. `bin/start.js` then failed with "Prebuilt binary not found for &lt;rid&gt;". Diagnosis confirmed against authoritative Claude Code docs.
- **Per-RID GitHub Release asset naming.** `softprops/action-gh-release` attached files by basename, so four binaries all named `total-recall` collided down to ~2 assets on the GitHub Release (observed on v0.8.0-beta.3 — only 2 of 4 platforms attached). `release.yml` now stages copies into `release-assets/` with per-RID names (`total-recall-linux-x64`, `total-recall-linux-arm64`, `total-recall-osx-arm64`, `total-recall-win-x64.exe`) before attaching. `fail_on_unmatched_files: true` turns a missing asset into a hard error.
- **Prerelease badge on GitHub Releases.** Previously every release was published with `prerelease: false`, so beta tags appeared as stable releases. `release.yml` now sets `prerelease: ${{ contains(github.ref_name, '-') }}` so any tag with a hyphen (e.g. `v0.8.0-beta.4`) is marked prerelease automatically.
- **Stale `.opencode/INSTALL.md`.** The OpenCode install doc told users to invoke `node /path/to/total-recall/dist/index.js`, which stopped working the moment `dist/` was deleted in the 0.8.0 TypeScript strip. Rewritten to document three supported install options (global npm, `npx`, source checkout), all routing through `bin/start.js`.
- **Multi-host plugin manifest version drift.** Four files carry a `version` field — `package.json`, `.claude-plugin/plugin.json`, `.copilot-plugin/plugin.json`, `.cursor-plugin/plugin.json` — and they were all out of sync: Copilot's was stuck at `0.1.0` since day one, Claude's and Cursor's were stuck at `0.7.2` through the entire TS strip series. All four synced to `0.8.0-beta.4`. `AGENTS.md` § "Version sync — four files, one version" now documents this as a standing rule.

### Added

- **`scripts/fetch-binary.js`** — shared zero-dep Node downloader. Detects host RID, reads version from `package.json`, fetches the matching per-RID GitHub Release asset, and writes to `binaries/&lt;rid&gt;/`. Used by both `scripts/postinstall.js` (fast path at npm install time) and `bin/start.js` (safety net at first launch for git-clone installs and `--ignore-scripts` users).
- **`scripts/postinstall.js`** — resurrected with entirely new content. The old 0.6.8-era `postinstall.js` downloaded a ~150MB bun runtime (deleted as dead code in 0.8.0-beta.3). The new one is ~20 lines and calls `ensureBinary()` once at install time. Failures are intentionally non-fatal (exit 0) so `--ignore-scripts` / offline / corporate-firewall installs still succeed; `bin/start.js` retries on first launch.
- **Per-RID GitHub Release assets** so `scripts/fetch-binary.js` has a stable download URL pattern: `https://github.com/strvmarv/total-recall/releases/download/v&lt;version&gt;/total-recall-&lt;rid&gt;[.exe]`.

### Changed

- **`bin/start.js` refactored to use `ensureBinary()` as the single entry.** Previously duplicated RID detection and the "binary not found" error path. Now imports from `scripts/fetch-binary.js` — single source of truth for RID detection, error messages, and the download fallback. Fast path (binary present) has no added overhead.
- **`AGENTS.md` "Release flow" section rewritten** to enumerate all four version files as a standing rule and to remove references to the deleted `dist/`, `publish.yml`, and the bun launcher. The rest of `AGENTS.md` remains stale post-cutover; comprehensive rewrite tracked in `docs/TODO.md`.
- **`docs/TODO.md`** backfilled with 13 post-cutover follow-ups: checksum verification, signed releases, shared binary cache location, Intel Mac support, `--provenance` on npm publish, `release.yml` dry-run step, multi-platform `dotnet-ci.yml`, Node 24 migration, plugin version single-source-of-truth automation, stale `src-ts/` comment scrub, top-level doc scrub, `SqliteConnection.cs` bun:sqlite comment scrub, and a comprehensive `AGENTS.md` rewrite.

## 0.8.0-beta.3 - 2026-04-09

### Fixed

- **`scripts/verify-binaries.js` stuck on the old 5-RID layout.** The `prepublishOnly` safety-check script was still checking for `binaries/darwin-x64/` and `binaries/darwin-arm64/` directories that no longer existed (dropped and renamed in beta.2). It correctly refused to publish — working as designed — but the refusal blocked the npm publish step of the v0.8.0-beta.2 release. Updated to the new 4-RID list.
- **`bin/start.js` `detectRid()` returned stale `darwin-x64` / `darwin-arm64` values.** Even if the beta.2 publish had succeeded, macOS users would have hit "Prebuilt binary not found for darwin-arm64" because the tarball stages `osx-arm64`. Fixed `detectRid()` to return `osx-arm64` for macOS arm64 and dropped the `darwin-x64` branch entirely. Error message now distinguishes Intel Mac as a "not shipped in this release" case.
- **`tests-ts/` directory was never deleted in the original strip pass.** The plan's Task 0 verification only checked for `src-ts/`, `bin-ts/`, and the top-level TS tooling configs. `tests-ts/` held `dist-smoke.test.ts`, `helpers/{db,bun-sqlite-shim,embedding}.ts`, a `fixtures/` dir, and a `manual/` test doc — all pure dead weight post-cutover. Now deleted.
- **`scripts/postinstall.js` was downloading a ~150MB bun runtime** on every user `npm install` and doing nothing else useful. The .NET AOT binary doesn't execute through bun; `bin/start.js` uses `child_process.spawn` to run the native binary directly. Deleted entirely. (The file name was resurrected in beta.4 with entirely new content — a binary downloader.)
- **`.npmignore` was stale and redundant.** Listed `src-ts/`, `tests-ts/`, `tsconfig.json`, `tsup.config.ts`, `vitest.config.ts` — all already deleted. Also superseded by the explicit `files` allow-list in `package.json`. Removed.

## 0.8.0-beta.2 - 2026-04-09

### Fixed

- **Invalid .NET RIDs in `release.yml` matrix.** The matrix used `darwin-arm64` and `darwin-x64`, which are not canonical .NET runtime identifiers — .NET uses `osx-arm64` / `osx-x64` for macOS. The bug was dormant since Plan 6 because `release.yml` had never actually run before v0.8.0-beta.1; the first firing on macos-14 errored out with `NETSDK1083: The specified RuntimeIdentifier 'darwin-arm64' is not recognized`. Renamed matrix-wide: cascades through matrix entries, artifact names, staging paths, publish-job download paths, verification script, chmod script, and the GitHub Release files list.
- **Deprecated `macos-13` runner image.** GitHub retired `macos-13` runner images; the darwin-x64 matrix leg failed instantly with "macos-13-us-default is not supported". Dropped `osx-x64` from the matrix entirely. All modern Apple hardware is Apple Silicon since Nov 2020 and Intel Mac support is deferred (see `docs/TODO.md`).
- **`linux-arm64` cross-compile apt sources hardcoded `jammy` (Ubuntu 22.04).** `ubuntu-latest` runners are now `noble` (24.04), so the `/etc/apt/sources.list.d/arm64.list` patch hit 404 on every arm64 package index fetch. Rewrote the entire leg to use GitHub's native `ubuntu-24.04-arm` runner instead — eliminates the cross-compile toolchain dance (apt sources patching, `gcc-aarch64-linux-gnu`, `-p:CppCompilerAndLinker=clang -p:SysRoot=... -p:LinkerFlavor=lld -p:ObjCopyName=...`). Single unified `Publish (Unix)` step now handles all three Unix RIDs.

### Added

- **`/global.json`** pinning .NET 10 SDK with `rollForward: latestFeature`. Prevents runner pre-install drift — macos-14 already has .NET 10.0.201 preinstalled, and pinning explicitly means every matrix leg uses the same SDK regardless of what each runner image happens to ship. .NET 10 SDK builds `net8.0` targets cleanly.
- **`release.yml` npm publish step now handles prerelease dist-tags.** Logic ported from the deleted `publish.yml`: if `package.json` version contains a dash (e.g. `0.8.0-beta.N`), publish under the matching dist-tag (`beta`, `rc`, `alpha`) instead of clobbering `latest`. Only stable versions publish to `latest`. Implementation uses bash parameter expansion (no `sed`), reads `VERSION` from the committed `package.json` — no untrusted `github.event.*` input.

### Changed

- **`dotnet-ci.yml` Setup .NET step** now uses `global-json-file: global.json` instead of `dotnet-version: 8.0.x` for consistency with `release.yml`.
- **Version bump to `0.8.0-beta.2`.** The `v0.8.0-beta.1` tag stays on origin as a historical marker of the first failed matrix attempt.

### Known limitations

- `release.yml` only grants `contents: write` at the workflow level, not `id-token: write`, so `--provenance` cannot be passed to `npm publish` without a follow-up commit. Tracked in `docs/TODO.md`.

## 0.8.0-beta.1 - 2026-04-08

### Removed

- **All TypeScript source, tests, and build tooling.** The .NET rewrite on the `rewrite/dotnet` branch had been running in parallel to the TypeScript tree for weeks, with `src-ts/` and `bin-ts/` holding the parked TS implementation alongside the active `src/TotalRecall.{Cli,Core,Host,Infrastructure,Server}/` .NET tree. This release strips all remaining TS:
  - `src-ts/` — the entire parked TS source tree (114 files)
  - `bin-ts/` — the previous-generation launcher scripts (`start.cjs`, `total-recall.cmd`, `total-recall.sh`)
  - `dist/` — committed `tsup` output (`index.js`, `defaults.toml`, `eval/ci-smoke.js`, `eval/defaults.toml`)
  - `tsconfig.json`, `tsup.config.ts`, `vitest.config.ts`, `vitest.dist.config.ts` — top-level TS tooling
  - `.github/workflows/ci.yml` — pure TS CI (vitest on node, bun matrix, tsup build, dist-committed check)
  - `.github/workflows/publish.yml` — legacy TS npm publish workflow that triggered on the same `v*` tag as `release.yml` (latent double-publish bug)
  - All TS dependencies and devDependencies from `package.json` except `sqlite-vec`, which is kept in `devDependencies` as a per-platform native-binary source via its npm optionalDependencies (`sqlite-vec-linux-x64`, etc.). `package-lock.json` regenerated from the stripped manifest.

### Changed

- **Distribution model.** `package.json` reduced to a minimal `bin`/`files`/`scripts` shell pointing at `bin/start.js` as the Node launcher. Prebuilt .NET AOT binaries are staged into `binaries/<rid>/` by the release matrix and shipped inside the npm tarball via the `files` allow-list. End users install with `npm install @strvmarv/total-recall` (or `@beta` for prerelease); the Node launcher dispatches to the per-platform binary via `child_process.spawn`.
- **Version bump to `0.8.0-beta.1`.** Main branch stays at `0.7.2` (TypeScript) — beta tags ship via `release.yml` matrix to the `beta` dist-tag without touching `latest`.

### Known issues (all fixed in later betas)

- The `release.yml` matrix failed on three independent latent bugs that had never been exercised before this first tag push: invalid `darwin-*` RIDs (fixed in beta.2), stale `jammy` apt sources for linux-arm64 cross-compile (fixed in beta.2), and deprecated `macos-13` runner (fixed in beta.2).
- `scripts/verify-binaries.js` and `bin/start.js` had the old 5-RID list baked in (fixed in beta.3).
- `tests-ts/` was missed in the strip pass (fixed in beta.3).
- `scripts/postinstall.js` was downloading a bun runtime that nothing used (fixed in beta.3).
- The install-path gap for git-clone-sourced plugin installs was not recognized until beta.3 dogfood (fixed in beta.4 via the `scripts/fetch-binary.js` download bootstrap).
- The `darwin-x64` RID was dropped in beta.2, so Intel Mac is not shipped in the 0.8.x line.

## 0.6.8-beta.5 - 2026-04-06

### Fixed
- **Cursor importer on Windows**: `CursorImporter.importKnowledge` silently skipped every workspace-discovered project on Windows. Root cause: `src/importers/cursor.ts` parsed the `folder` field from Cursor's `workspace.json` with `new URL(folder).pathname`, which returns `/C:/Users/...` on Windows — not a valid filesystem path, so the subsequent `existsSync(join(projectPath, ".cursorrules"))` always failed. Replaced with `fileURLToPath` from `node:url` (via a `safeFileURLToPath` helper that swallows malformed URLs), which produces the correct `C:\Users\...` on Windows and `/Users/...` on POSIX. Affects all Windows users who had Cursor rules to import since 0.6.0-ish.
- **`detectProject` on Windows**: `src/utils/project-detect.ts` used `process.env.HOME` to recognize the home directory and hardcoded `"/"` as the filesystem root. Both are POSIX-only — `HOME` is undefined on Windows (Node uses `USERPROFILE`), and the root is `C:\` (or whatever drive). Switched to `os.homedir()` for the home check and `path.parse(cwd).root` for the root check. Previously, `detectProject(someWindowsHomeDir)` would return the home folder's basename instead of null.
- **CI was failing on `test-bun (windows-latest)`** since 0.6.8-beta.1 because of the two bugs above plus one test bug (`cursor.test.ts` constructed `folder: \`file://\${projectDir}\`` which is malformed on Windows — two slashes, backslashes in the path; fixed to use `pathToFileURL(projectDir).href`). The NPM publish workflow is separate from the CI workflow, so beta.1 through beta.4 all published despite red CI. This beta is the first one where the full matrix (ubuntu/macos/windows × bun) is green.

## 0.6.8-beta.4 - 2026-04-06

### Fixed
- MCP server failed to start on Claude Code (and any host launching via `.mcp.json`) after the 0.6.8 migration to `bun:sqlite`. `bin/start.cjs` still re-exec'd `dist/index.js` under `node`, which cannot resolve the `bun:` URL scheme and crashed immediately with `ERR_UNSUPPORTED_ESM_URL_SCHEME`. The launcher now locates the bundled bun binary at `~/.total-recall/bun/<version>/bun` (installed by `scripts/postinstall.js`), falls back to system `bun` on PATH, and fails fast with a clear remediation message if neither is present.
- `bin/start.cjs` no longer uses the stale `node_modules/better-sqlite3/lib` canary — that dependency was removed from production installs in 0.6.8, so every launch was triggering a spurious `npm install`. The bootstrap now trusts that `npm install` ran postinstall correctly and only checks for `dist/index.js` and a bun runtime.
- `serverInfo.version` in the MCP `initialize` response is no longer hardcoded to `0.5.9` — it's read from `package.json` at startup via the existing `pkgPath` helper, so the value stays in sync with the actual release.
- `bin/total-recall.sh` and `bin/total-recall.cmd` no longer fall back to `node` when bun is missing. That branch was dead code after the `bun:sqlite` migration — `dist/index.js` cannot be parsed by node's ESM loader (`ERR_UNSUPPORTED_ESM_URL_SCHEME`), so the "warning: falling back to node" path only produced a louder crash. Both launchers now fail fast with a clear remediation message pointing at `npm install`.

## 0.6.8 - 2026-04-07

### Changed

- Replaced `better-sqlite3` native addon with `bun:sqlite` (built into Bun runtime). Eliminates native addon ABI mismatches when host tools (OpenCode, Claude Code) bundle their own Node.js version.
- Plugin now downloads a pinned Bun binary (v1.2.10) to `~/.total-recall/bun/` on `npm install`. Requires internet access on first install (~60MB). Subsequent installs use the cached binary.
- Launcher (`bin/total-recall.sh`, `bin/total-recall.cmd`) updated to prefer bundled Bun, fall back to system Bun, then system Node with a warning.
- CI matrix expanded to test on ubuntu, macos, and windows with Bun.
- `better-sqlite3` moved to devDependency only (used by vitest shim for Node-based testing).

## 0.6.7 - 2026-04-06

### Fixed
- `session_start` no longer fails with `ENOENT ... models/registry.json` on marketplace installs. Root cause: `src/embedding/registry.ts` computed the registry path with `../../models/registry.json` relative to `import.meta.url`, which was correct for the source tree but wrong after `tsup` bundles the server to a single `dist/index.js` — the resolver walked one level too many and escaped the version-scoped plugin directory. Replaced with a `pkgPath()` helper that walks up to `package.json` and works identically in source, bundled, and sub-bundled layouts.

### Added
- New `tests/dist-smoke.test.ts` regression test: spawns the built `dist/index.js` as a real MCP subprocess and calls `session_start` against it, so bundler-layout regressions of this kind fail loudly before publish. Wired into `prepublishOnly` via a new `test:dist` npm script. Unit tests previously exercised only the source tree, which is why 0.6.6 shipped broken.

## 0.6.6 - 2026-04-06

### Fixed
- `/total-recall update` now detects the plugin install mode. For marketplace tarball installs (the common case), it routes users through Claude Code's `/plugin` updater instead of attempting a `git pull` against a `.git`-less cache directory. Git-checkout installs still use `git pull origin main`.

## 0.6.5 - 2026-04-06

### Security
- Bump `vite` dev dependency from 8.0.3 to 8.0.5 to address three advisories: `server.fs.deny` bypass via queries (GHSA-v2wj-q39q-566r, high), arbitrary file read via dev-server WebSocket (GHSA-p9ff-h696-f583, high), and path traversal in optimized deps `.map` handling (GHSA-4w7w-66w2-5vf9, moderate). Vite is dev-only and not shipped to end users; advisories were not reachable from the published package.

### Fixed
- Sync `.claude-plugin/plugin.json` version (was stuck at 0.6.3 — missed during the 0.6.4 release).

## 0.6.4 - 2026-04-06

### Fixed
- **Model bundle resilience**: `session_start` no longer crashes with "Protobuf parsing failed" when the bundled `model.onnx` is a Git LFS pointer file. The embedding model loader now performs structural validation (file size match) and SHA-256 checksum verification, falling back to download from HuggingFace when the bundled file is corrupt or missing.

### Added
- `ModelBootstrap` state machine (`src/embedding/bootstrap.ts`) coordinates model validation, downloads, and cross-process locking. Uses `proper-lockfile` to prevent concurrent processes from racing on the same download.
- `ModelNotReadyError` (`src/embedding/errors.ts`) carries structured `reason` ("missing" | "downloading" | "failed" | "corrupted") and a user-facing `hint` with manual install commands.
- Atomic, retrying `downloadModel` with progress callbacks, exponential backoff, and SHA-256 verification on completion.
- `.verified` sidecar file that caches the model's checksum so subsequent loads skip the expensive hash computation.
- Model registry (`models/registry.json`) is now the single source of truth for model metadata (sha256, size, file URLs, revision).
- The MCP tool dispatcher (`src/tools/registry.ts`) translates `ModelNotReadyError` into a structured `model_not_ready` response so the using-total-recall skill can drive recovery.
- The `using-total-recall` skill now contains a recovery table for the four `model_not_ready` reasons.
- Manual smoke-test checklist at `tests/manual/model-bootstrap.md`.

## 0.6.3 - 2026-04-06

### Fixed
- Sync `.claude-plugin/plugin.json` version with `package.json` so the Claude Code marketplace reports the correct installed version. Previously v0.6.2 left `plugin.json` at `0.6.1`, causing `/plugin` to report stale version info.

## 0.6.2 - 2026-04-06

### Added
- `bin/start.cjs`: CJS bootstrap wrapper that detects missing `node_modules` and runs `npm install` before launching the MCP server, resolving version-gated startup failures after fresh plugin installs or `git pull`.
- Updated `.mcp.json` to launch via `bin/start.cjs` instead of `dist/index.js` directly.

### Changed
- Replaced `@iarna/toml` with `smol-toml` (pure ESM, no CJS stream dependency) to fix bundling failures under `noExternal` tsup config.
- Added `platform: node` to tsup config to fix CJS interop in the ESM bundle.

## 0.6.1 - 2026-04-06

### Added
- `help` subcommand added to the `total-recall` skill.

### Fixed
- Bundle `@iarna/toml`, `sqlite-vec`, and the MCP SDK into `dist/` to fix startup failures on marketplace installs where `node_modules` is absent.
- Cross-platform compatibility pass (Windows `cmd.exe` path fixes, macOS node-direct MCP launcher).

## [0.5.9](https://github.com/strvmarv/total-recall/compare/v0.5.8...v0.5.9) (2026-04-05)

### Bug Fixes

* coerce JSON-stringified arrays in MCP tool parameters — fixes `tags must be an array` error when MCP clients serialize array params as strings instead of native arrays
* apply coercion to all array-typed tool parameters: `tags`, `tiers`, `contentTypes`, `content_types`, `accept`, `reject`

## [0.5.8](https://github.com/strvmarv/total-recall/compare/v0.5.7...v0.5.8) (2026-04-05)

### Bug Fixes

* use absolute path via CLAUDE_PLUGIN_ROOT in .mcp.json to fix MCP server launch failure when cwd is not the plugin directory

## [0.5.7](https://github.com/strvmarv/total-recall/compare/v0.5.6...v0.5.7) (2026-04-05)

### Features

* add CI smoke benchmark script ([595e8f7](https://github.com/strvmarv/total-recall/commit/595e8f7))

### Bug Fixes

* resolve CodeQL security alerts ([7b2905e](https://github.com/strvmarv/total-recall/commit/7b2905e))
* handle empty changelog in publish pipeline ([dc80828](https://github.com/strvmarv/total-recall/commit/dc80828))

### Chores

* bump esbuild in the npm_and_yarn group ([eccaec4](https://github.com/strvmarv/total-recall/commit/eccaec4))
* add CI smoke benchmark step to CI pipeline ([58cee2d](https://github.com/strvmarv/total-recall/commit/58cee2d))
* bump testTimeout to 20s for vitest 4 compatibility ([d8af19e](https://github.com/strvmarv/total-recall/commit/d8af19e))

## [0.5.6](https://github.com/strvmarv/total-recall/compare/v0.5.5...v0.5.6) (2026-04-05)

### Chores

* add conventional-changelog-cli dev dependency ([ed38ce8](https://github.com/strvmarv/total-recall/commit/ed38ce8))
* backfill CHANGELOG.md from existing git history ([293d9bb](https://github.com/strvmarv/total-recall/commit/293d9bb))
* add GitHub Release creation and CHANGELOG.md update to publish pipeline ([b104186](https://github.com/strvmarv/total-recall/commit/b104186))
* audit fixes for README, AGENTS.md, and skill visibility ([f55d74e](https://github.com/strvmarv/total-recall/commit/f55d74e))

## [0.5.5](https://github.com/strvmarv/total-recall/compare/v0.5.4...v0.5.5) (2026-04-05)

### Bug Fixes

* recalibrate negative assertion benchmarks for contrastive corpus entries ([ab0ff71](https://github.com/strvmarv/total-recall/commit/ab0ff717a63e3a36a0b121689ecde3cffd84486e))

## [0.5.4](https://github.com/strvmarv/total-recall/compare/v0.5.2...v0.5.4) (2026-04-05)

### Features

* add FTS5 schema migration with sync triggers and backfill ([2478457](https://github.com/strvmarv/total-recall/commit/2478457ecf72e5ade6a3cfc6a4e985d687b8ddc0))
* add FTS5 score fusion to searchMemory for hybrid search ([11905fc](https://github.com/strvmarv/total-recall/commit/11905fc8af58c1a61a8e453e005aee8bd74d23d5))
* add FTS5 search with BM25 scoring and query sanitization ([cdf8e43](https://github.com/strvmarv/total-recall/commit/cdf8e43f8e60a929cb163e0a34a7820255910e5f))
* add search.fts_weight config with 0.3 default ([aa997ff](https://github.com/strvmarv/total-recall/commit/aa997ff7520d635fa1f588e65b3f113d477ccacc))
* add WordPiece tokenizer with BertNormalizer and BertPreTokenizer ([172aa7b](https://github.com/strvmarv/total-recall/commit/172aa7b9ab4a22776704a8347282006920085d82))
* wire WordPiece tokenizer into Embedder, replacing whitespace-only tokenize() ([adfeed7](https://github.com/strvmarv/total-recall/commit/adfeed76bf54110139bfed7ca60857ef1ac25d27))

### Bug Fixes

* tokenizer prototype pollution causing BigInt crash on session_start ([6e0f5e8](https://github.com/strvmarv/total-recall/commit/6e0f5e8d91a7fd6c7deae31d86967247f88320eb))

## [0.5.2](https://github.com/strvmarv/total-recall/compare/0.5.1...v0.5.2) (2026-04-05)

### Features

* add benchmark candidate capture and resolution ([1d212e1](https://github.com/strvmarv/total-recall/commit/1d212e12951657d2fe283ffa343418f368694f2b))
* add eval_grow tool for evolving benchmarks ([b0b9705](https://github.com/strvmarv/total-recall/commit/b0b970560f21dc4ed6982dd1a715605f976d495a))
* add migration 2 — _meta and benchmark_candidates tables ([c6a8882](https://github.com/strvmarv/total-recall/commit/c6a8882d0ccacd2e40189cac817e6fae536b9b97))
* add regression detection with configurable thresholds ([08848db](https://github.com/strvmarv/total-recall/commit/08848dbd44e17359252871224340013458576455))
* add smoke test runner with version-gated execution ([80aa734](https://github.com/strvmarv/total-recall/commit/80aa734dd81e5d4f68907c31139eb6527e5d3552))
* capture miss candidates during eval_report ([d3292b0](https://github.com/strvmarv/total-recall/commit/d3292b09080527d8689b7e0bcfaa481f100f23a1))
* replace single-chunk ingest validation with 3-probe approach ([6a88e36](https://github.com/strvmarv/total-recall/commit/6a88e3660fb377ada8b2b25c16062eb3d4e59616))
* wire regression detection into session_start ([7f5499a](https://github.com/strvmarv/total-recall/commit/7f5499a3c6fbfff51a726b3ef87f8cb549fb14fc))
* wire smoke test into session_start ([b1251ab](https://github.com/strvmarv/total-recall/commit/b1251ab5fd57c23580b828695080ce299e9769b8))

### Bug Fixes

* rebuild dist for v0.5.1 ([c5a4f00](https://github.com/strvmarv/total-recall/commit/c5a4f007e14c79bd790b9a292b67be4bf482d138))

## [0.5.1](https://github.com/strvmarv/total-recall/compare/v0.5.0...0.5.1) (2026-04-05)

### Features

* add Cursor, Cline, OpenCode, and Hermes host importers ([c4d2686](https://github.com/strvmarv/total-recall/commit/c4d2686b38d67902d22eb986ed04af2893aec7e6))

## [0.5.0](https://github.com/strvmarv/total-recall/compare/v0.4.0...v0.5.0) (2026-04-05)

### Features

* add generateHints and getLastSessionAge helpers ([0377646](https://github.com/strvmarv/total-recall/commit/0377646d1b64f9071927c32ad3a309f3ae5646a5))
* add listEntriesByMetadata query helper for metadata-based filtering ([9ca8f59](https://github.com/strvmarv/total-recall/commit/9ca8f5975d0769c55287698ed7846687a2360ba7))
* wire tierSummary, hints, and lastSessionAge into session_start response ([b92ba93](https://github.com/strvmarv/total-recall/commit/b92ba93cb3c6699940329fec88d89018d754d4d4))

### Bug Fixes

* handle singular and zero cases in getLastSessionAge ([60accf3](https://github.com/strvmarv/total-recall/commit/60accf31a502860f6c94ab0963e3c853c99ae7a3))
* pre-release fixes for v0.5.0 ([9b58cad](https://github.com/strvmarv/total-recall/commit/9b58cad979084befcab6720a73ba555fe07933c1))
* validate metadata keys and reject empty filter in listEntriesByMetadata ([e85ca9f](https://github.com/strvmarv/total-recall/commit/e85ca9f0d4c10195aeaaa2965c8e7e8745824a8f))

## [0.4.0](https://github.com/strvmarv/total-recall/compare/v0.3.3...v0.4.0) (2026-04-05)

### Features

* add computeComparisonMetrics for A/B config comparison ([b7dcf5f](https://github.com/strvmarv/total-recall/commit/b7dcf5fce76dca75d9b262e0647d46250aa89178))
* add configSnapshotId to ToolContext ([72f2fc7](https://github.com/strvmarv/total-recall/commit/72f2fc7c0f6f4aef31d76fe6831066080ab86d15))
* add createConfigSnapshot with deduplication ([14fa5e0](https://github.com/strvmarv/total-recall/commit/14fa5e079bc08bbcc20f89ab6ae20ffacccaf2cb))
* add eval_compare and eval_snapshot MCP tools ([eb9b3df](https://github.com/strvmarv/total-recall/commit/eb9b3df58986abfd4a0c438ddc0a5a8c807c8915))
* add eval_compare and eval_snapshot MCP tools, fix e2e test for expanded corpus ([7723f88](https://github.com/strvmarv/total-recall/commit/7723f888238cd8b396a1585df0475f5a4801f6a2))
* add expected_absent negative assertion to benchmarks ([3adabd8](https://github.com/strvmarv/total-recall/commit/3adabd82bde339adb881e8db433424d52c306200))
* create config snapshot on session_start ([ec1644d](https://github.com/strvmarv/total-recall/commit/ec1644da18f456a748a6bc1fd6256115970147ea))
* expand benchmark corpus to ~100 entries and ~139 queries ([06b205f](https://github.com/strvmarv/total-recall/commit/06b205f3e44773d01ad4bb730f2a9b0c605f6f73))
* log retrieval events with config snapshot ID on memory_search ([07ee872](https://github.com/strvmarv/total-recall/commit/07ee8723a13be75c63ab29f07fa4ba193d4b543e))
* snapshot config before config_set writes ([8197ec2](https://github.com/strvmarv/total-recall/commit/8197ec2d0344353cacbf8570ac2b7f08083aa79b))
* thread config snapshot ID into compaction calls ([1ef6f10](https://github.com/strvmarv/total-recall/commit/1ef6f10ee91657cac44e20cd5c6777a0d1aef24e))

### Bug Fixes

* correct QuerySource type and non-null assertions in comparison test ([b631b25](https://github.com/strvmarv/total-recall/commit/b631b255d0487793aaf0eeaf5dee169de9a1c0f4))

## [0.3.3](https://github.com/strvmarv/total-recall/compare/v0.3.2...v0.3.3) (2026-04-05)

## [0.3.2](https://github.com/strvmarv/total-recall/compare/v0.3.1...v0.3.2) (2026-04-05)

### Bug Fixes

* resolve eval corpus path correctly when running from dist/ bundle ([62dedb0](https://github.com/strvmarv/total-recall/commit/62dedb03580b1db818a150ac57f9b93442ef98da))

## [0.3.1](https://github.com/strvmarv/total-recall/compare/v0.3.0...v0.3.1) (2026-04-05)

## [0.3.0](https://github.com/strvmarv/total-recall/compare/v0.2.6...v0.3.0) (2026-04-05)

### Bug Fixes

* revert skill name to total-recall so /total-recall works as shorthand ([d1299a3](https://github.com/strvmarv/total-recall/commit/d1299a3654c180d3e0849ef3ebc6ed7ebd64dfe6))

## [0.2.6](https://github.com/strvmarv/total-recall/compare/v0.2.5...v0.2.6) (2026-04-05)

## [0.2.5](https://github.com/strvmarv/total-recall/compare/v0.2.4...v0.2.5) (2026-04-05)

### Features

* add /total-recall update subcommand ([67ea185](https://github.com/strvmarv/total-recall/commit/67ea185576270e62c23a6c079785f9ed0a007490))

## [0.2.4](https://github.com/strvmarv/total-recall/compare/v0.2.3...v0.2.4) (2026-04-05)

## [0.2.3](https://github.com/strvmarv/total-recall/compare/v0.2.2...v0.2.3) (2026-04-05)

## [0.2.2](https://github.com/strvmarv/total-recall/compare/v0.2.1...v0.2.2) (2026-04-05)

## [0.2.1](https://github.com/strvmarv/total-recall/compare/v0.2.0...v0.2.1) (2026-04-05)

### Features

* bundle ONNX model via Git LFS for offline plugin installs ([ffb5566](https://github.com/strvmarv/total-recall/commit/ffb55660beb6575a4f0d78dd75887ee81d40f8da))

### Bug Fixes

* launcher falls back to npx when dist/ not present (git clone installs) ([edfad6e](https://github.com/strvmarv/total-recall/commit/edfad6e2c64bd4fe55e9750605069b8b1a2dfb2a))
* track dist/ in git so plugin installs work without npx ([ba47f4e](https://github.com/strvmarv/total-recall/commit/ba47f4e4abe6a845507e9be2f895da8d0505be2d))
* use npx in .mcp.json for plugin MCP server discovery ([78dc47f](https://github.com/strvmarv/total-recall/commit/78dc47f30962a97fbc0f5c038e324d05ca7c778a))

## [0.2.0](https://github.com/strvmarv/total-recall/compare/v0.1.5...v0.2.0) (2026-04-05)

### Features

* bundle ONNX model for offline use, check bundled path before downloading ([ab42055](https://github.com/strvmarv/total-recall/commit/ab42055d92c4b4ac90d8b1c0dcb766af37b7da39))

### Bug Fixes

* add node-finding launcher script for MCP server, works with nvm/fnm/volta/brew ([9115a9b](https://github.com/strvmarv/total-recall/commit/9115a9b2f03b2caa1e2b2823dd1b3e111f812cfa))
* correct HuggingFace model download paths and auto-copy defaults.toml to dist ([69a4a3c](https://github.com/strvmarv/total-recall/commit/69a4a3c4054473bc670067b9ddb60a284d2aa377))
* correct plugin.json schema and add .mcp.json for MCP server discovery ([02a5765](https://github.com/strvmarv/total-recall/commit/02a5765af3dbea6a00bbd377967d6c9e3c920b04))
* restore portable .mcp.json with bash launcher ([db204ec](https://github.com/strvmarv/total-recall/commit/db204ec21c3ff0e972b9bc0130ae93ad3b016d8f))

## [0.1.5](https://github.com/strvmarv/total-recall/compare/v0.1.4...v0.1.5) (2026-04-05)

## [0.1.4](https://github.com/strvmarv/total-recall/compare/v0.1.3...v0.1.4) (2026-04-05)

## [0.1.3](https://github.com/strvmarv/total-recall/compare/v0.1.2...v0.1.3) (2026-04-05)

## [0.1.2](https://github.com/strvmarv/total-recall/compare/v0.1.1...v0.1.2) (2026-04-05)

## [0.1.1](https://github.com/strvmarv/total-recall/compare/dc4460315bc36d553d5bee8dd5230396005b5370...v0.1.1) (2026-04-05)

### Features

* add Claude Code importer with frontmatter parsing and dedup ([8f1263e](https://github.com/strvmarv/total-recall/commit/8f1263ef974930dd67fd26067f9198d4f0cf78da))
* add code-aware chunking parser with multi-language support ([f6a0d58](https://github.com/strvmarv/total-recall/commit/f6a0d5830a9bcdb0a081529e14331a27211121cb))
* add compact, inspect, history, lineage, export, and import MCP tools ([059ddab](https://github.com/strvmarv/total-recall/commit/059ddabbf1bbbde4c4515c0b1bd351e30da76d10))
* add compactor subagent for intelligent hot-to-warm compaction ([3776657](https://github.com/strvmarv/total-recall/commit/37766573acdcb64dae292ccbd59a911af27040b4))
* add Copilot CLI importer for plan.md files ([297ac96](https://github.com/strvmarv/total-recall/commit/297ac9688f566cec679f9aadeaafd436b9a37da7))
* add core memory skill for always-on behavior ([84c1098](https://github.com/strvmarv/total-recall/commit/84c1098e8dec0b4a5c7b1796638eb505e3a2493b))
* add decay score calculation with time, frequency, and type weighting ([0d9f58a](https://github.com/strvmarv/total-recall/commit/0d9f58a082085c2cab180391373990f84f8d6282))
* add entry CRUD operations with project scoping and access tracking ([7af9916](https://github.com/strvmarv/total-recall/commit/7af9916d871462172c2cf95fb4461298aaf50cf3))
* add file and directory ingestion with validation ([e36daa3](https://github.com/strvmarv/total-recall/commit/e36daa30a4955e2b77f508dfc1b5721658c0b46d))
* add hierarchical knowledge base index (collection/document/chunk) ([75db7ee](https://github.com/strvmarv/total-recall/commit/75db7ee8f6034a76bfe90620512fdd67a661a085))
* add hot->warm compaction with decay scoring and event logging ([b1c0326](https://github.com/strvmarv/total-recall/commit/b1c0326d4ab10e89e65802ea22d5689e66e6b603))
* add lazy-loading ONNX embedding engine with model download ([614f7b5](https://github.com/strvmarv/total-recall/commit/614f7b58a98a3dd1c7ef18d892a9f48fc33c3e1e))
* add markdown-aware semantic chunking parser ([24fe9ba](https://github.com/strvmarv/total-recall/commit/24fe9baa7bba81d9fd95f3b364b3f62588fe1529))
* add MCP server with memory and system tool handlers ([71e9320](https://github.com/strvmarv/total-recall/commit/71e932091a6b55a30e14d7f2ddfe8b40fb5bc412))
* add memory store, search, get, update, delete, promote/demote ([e6ac427](https://github.com/strvmarv/total-recall/commit/e6ac427a5723e7e5ded13b868f957543f049542d))
* add npm publishing support and self-install instructions ([7a6b68a](https://github.com/strvmarv/total-recall/commit/7a6b68ab8fe17fe7b020767fc2e056a0e8bd804f))
* add platform manifests for Claude Code, Copilot CLI, Cursor, and OpenCode ([7f41e19](https://github.com/strvmarv/total-recall/commit/7f41e19673c9402a650fe1c7b23a3a52e3b164c5))
* add retrieval event logger with outcome tracking ([a75d199](https://github.com/strvmarv/total-recall/commit/a75d19978989fb444fba1049aba0cdf6921e0726))
* add retrieval metrics computation (precision, hit rate, MRR) ([af11999](https://github.com/strvmarv/total-recall/commit/af11999b599e83aa36a2c1937ba8ca239242492a))
* add search, ingest, status, and forget skills ([3396c67](https://github.com/strvmarv/total-recall/commit/3396c67d7be81c495979582441770a2b8c2e233b))
* add SessionStart/End hooks for Claude Code and Cursor ([6257a58](https://github.com/strvmarv/total-recall/commit/6257a58df512ec6a4aa3d6e16a26013cb7733fd8))
* add shared type definitions for entries, tiers, config ([dc44603](https://github.com/strvmarv/total-recall/commit/dc4460315bc36d553d5bee8dd5230396005b5370))
* add SQLite schema with 6 content tables, vector tables, and system tables ([17da662](https://github.com/strvmarv/total-recall/commit/17da6620b45769b1afd491c10d41a63f4133c34f))
* add synthetic benchmark runner with seed corpus and 20 query pairs ([0b4a4db](https://github.com/strvmarv/total-recall/commit/0b4a4db194cf180a8abcc2192f814437c7547c17))
* add TOML config loading with user override support ([756abd7](https://github.com/strvmarv/total-recall/commit/756abd753e37881b4d0aaa6b901d11765b0451ee))
* add TUI dashboard formatters for status and eval ([83604bd](https://github.com/strvmarv/total-recall/commit/83604bdf29dbac56c079f516963e5e2ce3f534f7))
* add unified chunker with format detection and dispatch ([71f3172](https://github.com/strvmarv/total-recall/commit/71f31725cd92fd0750b29e23d8745155fbcd4abd))
* add vector similarity search with sqlite-vec, multi-tier support ([5924f11](https://github.com/strvmarv/total-recall/commit/5924f111d5c70f9ea46a7fd7cfc8d78bd691c337))
* add warm->cold decay sweep and cold->warm promotion ([4fbf610](https://github.com/strvmarv/total-recall/commit/4fbf610f496c5d9d81988def399b0911be1e9c92))
* register KB, eval, import, and session MCP tools ([837efa5](https://github.com/strvmarv/total-recall/commit/837efa59c69d5e9979f2f371a8cc51cb5f1293f1))

### Bug Fixes

* add integrity verification for downloaded model files ([52f97fe](https://github.com/strvmarv/total-recall/commit/52f97fed7a64f0cbbcf14ec148689c9107a6efe9))
* add MCP input validation and path traversal protection ([1773708](https://github.com/strvmarv/total-recall/commit/1773708dfb7e125c3c67261be4e5a815906ae6ce))
* add missing warm_sweep_interval_days to phase2 e2e compaction config ([e6ff48f](https://github.com/strvmarv/total-recall/commit/e6ff48f10ea43d49b46dacde0398bedede1fdafb))
* delete embedding when deleting memory to prevent orphaned vectors ([57b4bd4](https://github.com/strvmarv/total-recall/commit/57b4bd45c03e479f2c24d14e5da6be1d5ebb03a2))
* harden config traversal, benchmark dedup, error reporting in ingestion ([d731f86](https://github.com/strvmarv/total-recall/commit/d731f8684190abfcc0bf8b5b52821a5f451e6d53))
* make embed function optional in updateMemory when content unchanged ([a31cf7c](https://github.com/strvmarv/total-recall/commit/a31cf7c3b905022966e707aa44a3bec7563edb43))
* prevent ReDoS by replacing regex glob conversion with safe string matching ([11e3e40](https://github.com/strvmarv/total-recall/commit/11e3e403e763d505e4b6501a7f888408adefcf56))
* prevent SQL injection by whitelisting orderBy columns ([6be555f](https://github.com/strvmarv/total-recall/commit/6be555fc8eaf02e15b594fbbc6c02fd4128231ac))
* replace hash-based sync embedding with real async ONNX embeddings ([e66c957](https://github.com/strvmarv/total-recall/commit/e66c9571685ebf81a7e52cfb75c36c19fb810681))
* resolve noUncheckedIndexedAccess errors in e2e tests ([2f018fe](https://github.com/strvmarv/total-recall/commit/2f018fe867a30f2405585c2bd6488545379d0cc6))
