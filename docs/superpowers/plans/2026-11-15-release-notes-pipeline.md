# Release notes pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn each release's annotated tag message into the GitHub release notes field, a `release-notes.json` shipped in the npm package and in all four platform archives, and a release gate that fails a stable tag with no usable summary (issue #91, ticket T1 of the what's-new notice).

**Architecture:** One zero-dependency ESM script, `scripts/release-notes.js`, parses a tag message into a notes object, computes the previous release from the tag list, sanitises the summary, and writes the files; the workflow calls it twice (a gate in `check-version`, then the writer in `publish` before the npm size guard and `npm publish`). Everything pure is unit-tested; the git reads are tested against a temporary repository; the workflow wiring is tested as text like the existing `release-workflow.test.js` checks.

**Tech Stack:** Node >= 20 ESM, `node:test`, git CLI, GitHub Actions YAML (no YAML parser available).

**Spec:** `docs/superpowers/specs/2026-11-15-whats-new-notice-design.md` (section 1 and the sanitiser rule in section 2). Read-only for this run.

## Global Constraints

- Zero dependencies; ESM; runs on Node 20 and Node 24 (`npm run test:shim` is the runner, new test files go in `SHIM_TEST_TARGETS` in `scripts/run-shim-tests.js`).
- `version` and `previous` in `release-notes.json` are bare versions (`4.7.0`, no `v`); `previous` is `null` for the first release.
- `previous`: stable release = highest lower stable version; prerelease = highest lower version of any kind; tags filtered to `vMAJOR.MINOR.PATCH[-prerelease]` (the repo also has `0.5.1`, ignored), ordered by SemVer 2.0.0 precedence.
- Summary: first paragraph after the title line, then the sanitiser, **in this order**: (1) strip ANSI escape sequences (ESC `[` ... final byte); (2) strip control characters except tab, newline and carriage return; (3) flatten `[text](url)` to `text` and drop the angle brackets around a bare `<url>`; (4) collapse every whitespace run (newlines included) to one space; (5) trim; (6) if longer than 200 characters cut at the last space at or before 200 with no ellipsis, one word longer than 200 is cut at 200. Inline code and emphasis characters are kept.
- Section headings: the walk covers only the lines after the summary paragraph. A heading is any non-blank, non-bullet line, optionally prefixed with `#`s; it opens an Added/Changed/Fixed section when it starts with that word (`Added (from 4.7.0-beta.1)` counts), any other heading closes the current section and is ignored. A line starting with whitespace continues the previous bullet (joined with one space), and `-`, `*` and `+` all mark a bullet. Bullets with no preceding section heading (v4.3.0 to v4.5.0) are deliberately not sectioned. A first paragraph whose first line is a section heading or a bullet is **not** a summary: the message has no summary (error for a stable tag, warning for a prerelease). A bare `vX.Y.Z` or `total-recall vX.Y.Z` title both parse; a message whose title line is empty is an error for a stable tag.
- Stable tag with no summary is an error; prerelease with no summary writes `summary: ""` and a warning. The tag must be an annotated tag object (`git cat-file -t refs/tags/<tag>` prints `tag`), else fail closed.
- `release-notes.json` keys: `version`, `previous`, `summary`, `sections` (`added`, `changed`, `fixed`: arrays of bullet strings), `url` (`https://github.com/strvmarv/total-recall/releases/tag/v<version>`), `releasesUrl` (`https://github.com/strvmarv/total-recall/releases`).
- Release body = the tag message without its title line and the blank line after it.
- No AI attribution in commits.

## Review Focus

- A tag message with CRLF line endings or trailing whitespace: parses like LF text.
- A tag message that is only a title (v4.2.1): summary empty, no throw; stable gate fails.
- A summary that is a single 400-character word, or all control characters (sanitises to empty): does not throw.
- Tag list containing `v4.10.0`, `v4.9.0`, `0.5.1`, `vNEXT`, and duplicates: `previous` of 4.10.0 is 4.9.0.
- A prerelease tag released after its stable sibling exists (re-cut): previous still computed from lower versions only.

---

### Task 1: Summary sanitiser and the shared fixture

**Files:**
- Create: `scripts/release-notes.js`
- Create: `scripts/fixtures/summary-sanitiser.json`
- Create: `scripts/release-notes.test.js`
- Modify: `scripts/run-shim-tests.js` (add `'scripts/release-notes.test.js'` to `SHIM_TEST_TARGETS`)

**Interfaces:**
- Produces: `export function sanitizeSummary(text: string): string` in `scripts/release-notes.js`; the fixture is an array of `{ "name": string, "input": string, "expected": string }` that the engine's tests will read later.

- [ ] **Step 1: Write the failing test** `release-notes.test.js` › `sanitizeSummary matches every case in scripts/fixtures/summary-sanitiser.json`, reading the fixture and asserting `sanitizeSummary(c.input) === c.expected`. Fixture cases (write them first): `"Fixed [the cache](https://x.y) bug\n"` → `"Fixed the cache bug"`; `"\u001b[31mred\u001b[0m"` → `"red"`; `"a\n\nb\tc"` → `"a b c"`; a 250-character sentence of 10-letter words → cut at the last space at or before 200, no ellipsis; one 400-character word → its first 200 characters; `"\u0007\u0000"` → `""`; `"see <https://x.y> now"` → `"see https://x.y now"`; `"a\u00a0b\u2028c\u000bd"` → `"a b c d"` (non-breaking space, line separator and vertical tab are whitespace, matching .NET's `char.IsWhiteSpace`); `"[two\nlines](https://x.y)"` → `"two lines"`; `"keep \`code\` and *emphasis*"` unchanged.
- [ ] **Step 2: Run** `node --test scripts/release-notes.test.js` — Expected: FAIL (module missing).
- [ ] **Step 3: Implement** `sanitizeSummary` in `scripts/release-notes.js` as the six ordered passes of Global Constraints (the order matters: pass 2 keeps tab, LF and CR so pass 4 can turn them into spaces, and ANSI removal (pass 1) must come before pass 2 because pass 2 strips the ESC character).
- [ ] **Step 4: Add the test file to `SHIM_TEST_TARGETS`**, add to the test file `test('SHIM_TEST_TARGETS runs scripts/release-notes.test.js')` asserting the exported list includes it, then run `node --test scripts/release-notes.test.js` and `node scripts/run-shim-tests.js` — Expected: PASS.
- [ ] **Step 5: Commit** `git add scripts/release-notes.js scripts/release-notes.test.js scripts/fixtures/summary-sanitiser.json scripts/run-shim-tests.js && git commit -m "Add the release notes summary sanitiser and its shared fixture"`

### Task 2: Tag message parser with the real messages as fixtures

**Files:**
- Modify: `scripts/release-notes.js`
- Create: `scripts/fixtures/tag-messages/v4.2.1.txt`, `v4.3.0.txt`, `v4.4.0.txt`, `v4.5.0.txt`, `v4.6.0.txt`, `v4.7.0-beta.1.txt`, `v4.7.0-beta.2.txt`, `v4.7.0.txt` (each saved byte-exact from `git tag -l --format='%(contents)' <tag>`)
- Modify: `.gitattributes` (add `scripts/fixtures/tag-messages/*.txt text eol=lf` so a Windows checkout keeps the bytes; the fixtures under `scripts/` also ship in the npm package, which is deliberate and small)
- Test: `scripts/release-notes.test.js`

**Interfaces:**
- Consumes: `sanitizeSummary` (Task 1).
- Produces: `export function parseTagMessage(message: string): { title: string, summary: string, sections: { added: string[], changed: string[], fixed: string[] }, body: string }` where `body` is the message without the title line and the blank line after it (used for the release body); never throws; `summary` is `""` when there is no paragraph after the title.

- [ ] **Step 1: Write the failing tests**, one `test()` per fixture file asserting, from the real messages: v4.2.1 → `summary === ""`, empty sections; v4.4.0 → a non-empty summary of 7 words; v4.6.0 and v4.7.0 → summary starts with the real first words, `sections.added.length > 0`; v4.7.0-beta.2 → the `Added (from 4.7.0-beta.1)` heading fills `sections.added`; v4.3.0, v4.4.0 and v4.5.0 (bare title, headingless bullets) → summary present, all three sections empty, and no trailing "Observed on ..." or "Not yet observed ..." line appears in any section of any fixture; a message `"v1.0.0\n\nFixed the release workflow.\n\nAdded\n- one\n"` → `summary "Fixed the release workflow."`, `sections.added == ["one"]` (the summary's first word is not a heading); a `### Added` markdown heading opens the added section; plus `parseTagMessage("v1.0.0\r\n\r\nHello  there.\r\n")` → summary `"Hello there."`, and a title-only message → `summary === ""`.
- [ ] **Step 2: Run** `node --test scripts/release-notes.test.js` — Expected: FAIL (`parseTagMessage` not defined).
- [ ] **Step 3: Implement** `parseTagMessage`: normalise CRLF, first line is the title, first non-blank paragraph after it is the summary (through `sanitizeSummary`), then walk only the lines after that paragraph by the heading rule of Global Constraints; `- ` bullets under an open section are collected (trimmed, marker removed).
- [ ] **Step 4: Run** the test file — Expected: PASS.
- [ ] **Step 5: Commit** `git add scripts && git commit -m "Parse annotated tag messages into a summary and sections"`

### Task 3: Version tags, SemVer ordering and `previous`

**Files:**
- Modify: `scripts/release-notes.js`
- Test: `scripts/release-notes.test.js`

**Interfaces:**
- Produces: `export function parseReleaseTag(tag: string): string | null` (bare version for `vMAJOR.MINOR.PATCH[-prerelease]`, else `null`); `export function compareVersions(a: string, b: string): number` (SemVer 2.0.0 precedence, numeric identifiers compared numerically, a release above its prereleases); `export function previousVersion(version: string, tags: string[]): string | null`.

- [ ] **Step 1: Write the failing tests**: `parseReleaseTag` → `"4.7.0-beta.1"` for `v4.7.0-beta.1`, `null` for `0.5.1`, `vNEXT`, `v1.2`, `v1.2.3+build`; `compareVersions("4.10.0","4.9.0") > 0`, `("4.7.0-beta.10","4.7.0-beta.2") > 0`, `("4.7.0-beta.2","4.7.0-beta.1") > 0`, `("4.7.0","4.7.0-beta.2") > 0`; `previousVersion` with tags `["v4.6.0","v4.7.0-beta.1","v4.7.0-beta.2","v4.7.0","0.5.1"]`: `("4.7.0")` → `"4.6.0"`, `("4.7.0-beta.2")` → `"4.7.0-beta.1"`, `("4.6.0")` → `null` for the first stable when no lower stable exists (add a `v4.5.0` case separately to show lower stable wins), `("4.10.0", [..."v4.9.0","v4.10.0"])` → `"4.9.0"`, a re-cut case (`previousVersion("4.7.0-beta.2", ["v4.6.0","v4.7.0-beta.1","v4.7.0-beta.2","v4.7.0"])` → `"4.7.0-beta.1"` with the higher stable tag present), duplicates and the odd tag ignored.
- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Implement** the three functions; `previousVersion` filters through `parseReleaseTag`, drops versions not lower than `version`, drops prereleases when `version` is stable, returns the maximum by `compareVersions`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -am "Compute the previous release with SemVer ordering"`

### Task 4: Notes assembly, annotated-tag guard and the CLI

**Files:**
- Modify: `scripts/release-notes.js`
- Test: `scripts/release-notes.test.js`

**Interfaces:**
- Consumes: `parseTagMessage`, `previousVersion`, `parseReleaseTag`.
- Produces:
  - `export function buildReleaseNotes({ tag: string, message: string, tags: string[] }): { notes: ReleaseNotes, body: string, warnings: string[] }` — throws `Error("<tag> is not a release tag")` when `parseReleaseTag(tag)` is null (for example `vNEXT`); throws `Error("no title in the tag message of <tag>")` for a stable tag with an empty title line and `Error("no summary in the tag message of <tag>")` for a stable tag with no summary; for a prerelease it returns `summary: ""` and one warning in the same cases.
  - `export function readAnnotatedTag(tag: string, { cwd }: { cwd?: string } = {}): { message: string, tags: string[] }` — first `parseReleaseTag(tag)` (throw before any git call when null), then runs git with `execFileSync`/`spawnSync` (no shell, `<tag>` passed after `--` or as an exact `refs/tags/<tag>` ref so it is never a glob), throws `Error("<tag> is not an annotated tag")` unless `git cat-file -t refs/tags/<tag>` prints `tag`, reads `git tag -l --format=%(contents) <tag>` and `git tag -l`.
  - CLI: `node scripts/release-notes.js <tag> [--check] [--notes-out <file>...] [--body-out <file>] [--cwd <dir>]`. `--check` validates and prints warnings as `::warning::` lines or errors as `::error::` lines and exits 1 on error; `--notes-out` may repeat and writes the JSON (2-space indent, trailing newline) to each file; `--body-out` writes the body. Exit 2 on a usage error.

- [ ] **Step 1: Write the failing tests**: (a) `buildReleaseNotes` for the real v4.7.0 message and the tag list of Task 3 yields `Object.keys(notes)` equal to the six keys of Global Constraints, `version "4.7.0"`, `previous "4.6.0"`, `url` and `releasesUrl` exactly as in Global Constraints, and `body` equal to the fixture message with its title line and the blank line after it removed (trailing newline normalised); (b) stable tag with the v4.2.1 message throws and names the tag; (c) prerelease with no summary returns `summary ""` and one warning; (d) in a temporary git repository (`fs.mkdtempSync`, `git init -b main`, one commit, `git tag v1.0.0` lightweight, `git tag -a v1.0.1 -m ...` annotated; every git call in the tests runs with `GIT_AUTHOR_NAME/EMAIL`, `GIT_COMMITTER_NAME/EMAIL` set, `GIT_CONFIG_GLOBAL` and `GIT_CONFIG_SYSTEM` pointing at an empty file, and `-c commit.gpgsign=false -c tag.gpgsign=false`, so a developer's signing config cannot change the tag's contents) `readAnnotatedTag("v1.0.0", { cwd: repo })` throws an error matching `/not an annotated tag/` (and a tag that does not exist throws a different message, so a missing tag cannot satisfy the test) and `readAnnotatedTag("v1.0.1", { cwd: repo })` returns the message; (e) `buildReleaseNotes` with tag `vNEXT` throws, and with a stable message whose title line is empty throws the no-title error; (f) a summary-less stable message whose first paragraph is `Added` followed by a bullet throws the no-summary error; (g) the CLI in that repository with `--notes-out a.json --notes-out b.json --body-out body.md` writes identical JSON to both and the body; with `--check` on a summary-less stable tag exits 1 and prints a line starting `::error::`; a summary-less prerelease exits 0 with `::warning::`.
- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Implement** the three exports and the CLI entry, guarded by the same `realpathSync`-based main-module check that `scripts/run-shim-tests.js` (`isMainModule`) uses, not a bare `import.meta.url` comparison.
- [ ] **Step 4: Run** the test file and `npm run test:shim` — Expected: PASS.
- [ ] **Step 5: Commit** `git commit -am "Build release notes from an annotated tag and expose them as a CLI"`

### Task 5: Workflow wiring and its tests

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `scripts/release-workflow.test.js`

**Interfaces:**
- Consumes: the CLI of Task 4.
- Produces: in `check-version`: checkout with `fetch-depth: 0`, a step `git fetch --force --tags origin`, a step running `node scripts/release-notes.js "$REF_NAME" --check` with the tag in `env`; in `publish`: checkout with `fetch-depth: 0`, the same fetch step, and a step "Write release notes" placed after the artifact downloads and **before** "Guard npm package size (pre-publish)" running `node scripts/release-notes.js "$REF_NAME" --notes-out binaries/linux-x64/release-notes.json --notes-out binaries/linux-arm64/release-notes.json --notes-out binaries/osx-arm64/release-notes.json --notes-out binaries/win-x64/release-notes.json --body-out "$RUNNER_TEMP/release-body.md"`; the "Attach archives to GitHub release" step gains `body_path: ${{ runner.temp }}/release-body.md`.

- [ ] **Step 1: Write the failing tests** in `scripts/release-workflow.test.js`, in its existing style (a check function of the workflow text plus a mutation test). First generalise the existing `publishSteps` helper to take a job name so `check-version` steps can be parsed too. Give the new steps fixed names: "Fetch all tags" (both jobs), "Check the release notes" (`check-version`), "Write release notes" (`publish`). Checks: in both jobs the fetch step comes after checkout and before the script step and the checkout has `fetch-depth: 0`; the `check-version` gate step runs `release-notes.js` with `--check`; the "Write release notes" step names all four `binaries/<rid>/release-notes.json` and comes before "Guard npm package size (pre-publish)" and "npm publish"; the attach step has `body_path` pointing at the file `--body-out` writes (the check maps `${{ runner.temp }}` to `$RUNNER_TEMP` before comparing, and says so); no new `run:` block contains `${{` (the tag reaches the shell through `env` only). Mutations: the write step moved after `npm publish` is caught, a missing rid is caught, `body_path` removed is caught, the gate step removed from `check-version` is caught, the fetch step removed or moved after the script is caught, and `${{ github.ref_name }}` inlined into a `run:` is caught.
- [ ] **Step 2: Run** `node --test scripts/release-workflow.test.js` — Expected: FAIL (new tests).
- [ ] **Step 3: Edit `release.yml`** as in Interfaces; the tag name reaches the shell only through `env: REF_NAME: ${{ github.ref_name }}` (the workflow's rule: no interpolated expressions in `run:`).
- [ ] **Step 4: Run** `npm run test:shim` — Expected: PASS, including every existing release-workflow test (attestation order, `attestations: write` scope, prerelease draft/publish).
- [ ] **Step 5: Commit** `git commit -am "Gate the release on the tag message and ship the notes with every package"`

### Task 6: Documentation and changelog

**Files:**
- Modify: `AGENTS.md` (Release flow: step 4 now says the tag message is checked and what shape it must have; add a line that the release body and `release-notes.json` come from it)
- Modify: `CHANGELOG.md` (`## Unreleased` → `### Added`: an entry in the file's style for the notes pipeline, naming #91)
- Modify: `CONTRIBUTING.md` only if it describes tagging (check with grep; skip otherwise)

- [ ] **Step 1: Write the failing check**: a test in `scripts/release-notes.test.js` › `AGENTS.md documents the tag message shape the release checks` asserting `AGENTS.md` contains `release-notes.json` and the words `annotated`, `summary paragraph`.
- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Edit** the docs (tag message shape: `total-recall vX.Y.Z` title, blank line, one-paragraph summary, optional Added/Changed/Fixed lists; a stable release without the summary paragraph fails `check-version`).
- [ ] **Step 4: Run** `npm run test:shim` and `node scripts/sync-version.js --check` — Expected: PASS (docs checks such as `host-docs.test.js` still pass).
- [ ] **Step 5: Commit** `git commit -am "Document the checked tag message and the release notes files"`
