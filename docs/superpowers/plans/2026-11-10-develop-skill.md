# `develop` skill (GitHub issues) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the `develop` skill in `total-recall`: a Define/Deliver workflow that turns an idea or a GitHub issue into an open PR, with its trust, checkpoint and resume rules enforced by small tested scripts.

**Architecture:** Markdown skill files under `skills/develop/` carry the workflow. Every rule that must be exact (hashing, checkpoint grammar, counting, routing, the position table, argument parsing, account pinning, the profile) is a zero-dependency Node ESM script in `skills/develop/scripts/`, tested by `node --test` against `json golden:<name>` blocks that live in the Markdown file stating the rule. Two reviewer agents ship in `agents/`. On pi the skill is reached by a `/develop` command the existing extension registers.

**Tech Stack:** Node ≥ 20 ESM, `node:test`, `node:assert/strict`, `node:crypto`, `node:fs`. No dependencies, no TypeScript, no build step, no .NET change.

**Spec:** `docs/superpowers/specs/2026-11-10-develop-skill-design.md` (read it first; section numbers below refer to it).

## Global Constraints

- Work only in `D:/source/total-recall/.worktrees/develop-skill` on branch `feat/develop-skill`. Never touch the main checkout.
- Zero runtime dependencies. Scripts are ESM (`package.json` has `"type": "module"`), named `*.js`, tests `*.test.js` beside them, using `node:test` and `node:assert/strict` like `pi/floor.test.js`. A script reads files whose paths are arguments and prints exactly one JSON object; nothing is piped in.
- Identifiers fixed by the spec: skill name `develop`; machine prefix `tr-develop/v1`; phases `defined|reapproved|kickoff|plan-approved|tasks|final-approved|pr-open|blocked|closed`; region markers `<!-- tr-develop:ac-begin -->`/`ac-end` and `approach-begin`/`approach-end`; profile header `tr-develop/profile-v1`; profile path `.github/develop.md`; tracks `plan` and `small`.
- Patterns fixed by the spec: branch `^(feat|fix)/[a-z0-9][a-z0-9-]{0,62}$`; spec path `^docs/superpowers/specs/[A-Za-z0-9._-]+\.md$`; plan path `^docs/superpowers/plans/[A-Za-z0-9._-]+\.md$`; SHAs 40 hex, hashes 64 hex; counters `^\d+$`; checkpoint line `^tr-develop/v1 phase=(defined|reapproved|kickoff|plan-approved|tasks|final-approved|pr-open|blocked|closed)( [a-zA-Z][a-zA-Z0-9.]*=\S+)*$`.
- Numbers fixed by the spec: review loop cap 6 rounds; paired final review at 600 changed lines; approach at most 60 lines and 4,000 characters; branch slug at most 50 characters; concurrency window 24 hours.
- Trust: write access means `role_name` in `admin|maintain|write` or `permission` in `admin|write`. A 404 is "no access".
- Provenance: the skill is a clean redesign. No file may contain the strings `cortex`, `Jira`, `AIOPS`, `rai-ops`, `Radancy`, `{noformat}` or `STRATA` (Task 12 adds a test for this). Write every file fresh; do not copy from `D:/source/rai-ops-cortex-plugin`.
- Commits: `type(scope): summary`, no ticket id, **no co-author trailer and no AI attribution**. Commit after each task. **Never push or open a PR in any task**: that is the owner's call after the plan is done.
- Network `git` and every `gh` call, in any task and in every subagent prompt, are pinned to the chosen account (spec section 6, check 4): `GH_TOKEN=$(gh auth token --user strvmarv) gh ...` and `GH_TOKEN=$(gh auth token --user strvmarv) GIT_TERMINAL_PROMPT=0 git -c credential.helper= -c 'credential.helper=!gh auth git-credential' ...`. Never `gh auth switch`. Never run an authenticating git command to probe credentials (it opens Credential Manager's account picker). Local git (`add`, `commit`, `status`, `diff`) needs no pinning.
- Subagent fanout: at most 3 primary agents at once, each dispatching at most 1 sub-agent. Put this in every dispatch prompt.
- Shell: Git Bash. Give file tools absolute paths.

## Review Focus

Input classes the spec implies but no happy path exercises. Each has a test in the task that owns the code.

1. A body with the criteria markers inside a code fence, duplicated, or in the wrong order: must be `missing`, `duplicate` or `order`, never a silently wrong region (Task 4).
2. A comment edited on the web, so it has CRLF line endings, trailing spaces and a BOM-free second `tr-develop/v1` line quoted inside the summary: one matching line or `multiple-lines`, never a guess (Task 5).
3. Logins with `_` or `-` (enterprise-managed accounts look like `paul-marvin_radancy`), and a hostile "login" such as `x; rm -rf /` or `$(id)`: accepted or rejected by one pattern, and never reaching a shell unquoted (Tasks 6, 9, 10).
4. A plan that mentions `### Task 3:` inside a fenced code block, has zero tasks, or numbers tasks out of order: the count ignores fences, and zero is blocker 6, not "go to the final loop" (Task 7).
5. Argument text that is almost an issue reference: `007`, `123; rm -rf /`, `fix 123`, `owner/repo#9` for another repository, a non-github.com URL, a pull-request URL: each routes to the one place the table says (Task 8).

---

### Task 1: Amend the spec for what the repository already decided

**Files:**
- Modify: `docs/superpowers/specs/2026-11-10-develop-skill-design.md` (sections 4, 18, 20, 21, and the review-status paragraph)

The spec's section 18 says to add `"skills": [...]` to the `pi` block of `package.json`. That contradicts `pi/package.test.js`, which asserts the block is exactly `{ extensions: ['./pi/extension.js'] }` and declares no skills, because a skill would be advertised to subagent children. The extension already registers `/total-recall` as a command that forwards to the model; `/develop` follows that pattern.

**Interfaces:**
- Produces: the amended spec every later task reads.

- [ ] **Step 1: Edit section 18** so the `package.json` bullet reads: `test:shim` gains `skills/develop/`; the `pi` block is unchanged and still declares no skills; `pi/develop.js` registers a `/develop` command from the existing extension, and `pi/extension.js` calls it. Add a bullet for `pi/develop.js` and its test.
- [ ] **Step 2: Edit section 4's layout** so `scripts/` lists: `golden.js`, `regions.js`, `checkpoints.js`, `counted.js`, `route.js`, `args.js`, `ghpin.js`, `profile.js`, each with `*.test.js`, plus `skill.test.js`.
- [ ] **Step 3: Edit section 21 item 3** to: Claude Code namespaces plugin skills, so `/develop` may need to be `/total-recall:develop` (unverified); on pi a bare `/develop` works because the extension registers it. Add to section 20 as call 24: "On pi, `/develop` is an extension command, not a manifest skill, because `pi/package.test.js` forbids manifest skills."
- [ ] **Step 4: Add one sentence to the review-status paragraph** at the top: "This spec was amended once after review, by the plan's Task 1, for the pi packaging constraint; the plan review covers that amendment."
- [ ] **Step 5: Verify** `grep -n "skills\": \[" docs/superpowers/specs/2026-11-10-develop-skill-design.md` prints nothing for the pi block, and the spec still contains exactly one `## 18.` heading.
- [ ] **Step 6: Commit** `docs(develop): amend the spec for pi command packaging`.

### Task 2: Verify the GitHub behaviours the trust model rests on

**Files:**
- Modify: `docs/superpowers/specs/2026-11-10-develop-skill-design.md` (section 21 only)

**Interfaces:**
- Produces: recorded results that Tasks 6, 7 and 9 rely on; a failed check changes those tasks' contracts, so stop and report to the owner instead of continuing.

- [ ] **Step 1: Permission endpoint.** For each logged-in account (`gh auth status`), run `GH_TOKEN=$(gh auth token --user <login>) gh api repos/strvmarv/total-recall/collaborators/strvmarv/permission --jq '{permission,role_name}'`. Record the status each account gets (the work account has no push access and is expected to 403 or 404). Run the same for a nonexistent login (`this-user-does-not-exist-zz`) and for `ghost`, recording 404 versus 200. Record which results this does and does not settle: a write-but-not-admin caller is not available here, so that case stays open.
- [ ] **Step 2: Edit history shapes**, read-only, against a public repository: `GH_TOKEN=$(gh auth token --user strvmarv) gh api graphql -f query='{repository(owner:"cli",name:"cli"){issue(number:1){userContentEdits(first:2){nodes{editedAt diff editor{login}}} timelineItems(first:5,itemTypes:[RENAMED_TITLE_EVENT]){nodes{__typename ... on RenamedTitleEvent{previousTitle currentTitle}}}}}}'`. Record that revisions hold a `diff` body and no title, and that `RenamedTitleEvent.previousTitle` exists.
- [ ] **Step 3: Record** both results as dated bullets under section 21, replacing the "unverified" wording they settle, and keeping the write-but-not-admin case listed as open.
- [ ] **Step 4: Verify** `git diff --stat` shows only the spec changed, then commit `docs(develop): record verified GitHub behaviour`.

### Task 3: Golden-block reader

**Files:**
- Create: `skills/develop/scripts/golden.js`
- Test: `skills/develop/scripts/golden.test.js`

**Interfaces:**
- Produces: `readGolden(markdownPath: string, name: string): unknown` returns the parsed JSON of the single fenced block whose info string is `json golden:<name>`; throws `Error('golden <name> not found')`, `Error('golden <name> duplicated')` or `Error('golden <name> is not valid JSON: ...')`. `goldenNames(markdownPath: string): string[]` lists every golden name in file order. Consumed by every later script test.

- [ ] **Step 1: Write the failing test** using a temp file written with `fs.mkdtempSync`: a Markdown file with two goldens (`a` holding `[1,2]`, `b` holding `{"x":true}`), a plain ```` ```json ```` block with no golden tag, and a ```` ```json golden:bad ```` block holding `{`. Assert `readGolden(p,'a')` deepEquals `[1,2]`; `goldenNames(p)` deepEquals `['a','b','bad']`; `readGolden(p,'zz')` throws `/not found/`; `readGolden(p,'bad')` throws `/not valid JSON/`; a file with `a` twice throws `/duplicated/`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/golden.test.js`. Expected: FAIL, module missing.
- [ ] **Step 3: Implement** both functions: scan lines, open on `^```json golden:([A-Za-z0-9_-]+)\s*$`, close on `^```\s*$`.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add a golden-block reader for script tests`.

### Task 4: Regions and hashing

**Files:**
- Create: `skills/develop/scripts/regions.js`, `skills/develop/handoff.md` (only the section `## Hashing and regions` with its golden blocks for now)
- Test: `skills/develop/scripts/regions.test.js`

**Interfaces:**
- Consumes: `readGolden` (Task 3).
- Produces (spec section 8):
  - `normalize(text: string): string`: CRLF→LF; remove a task-list marker `[ ]`/`[x]`/`[X]` that directly follows a list bullet or number and whitespace at line start, at any depth; strip trailing whitespace on every line and trailing blank lines. Nothing else (no punctuation stripping, no entity decoding, no whitespace collapsing).
  - `bodyHash(title: string, body: string): string`: SHA-256 hex over UTF-8 of `JSON.stringify([normalize(title), normalize(body)])`.
  - `regionHash(text: string): string`: SHA-256 hex over UTF-8 of `normalize(text)`.
  - `extractRegion(body: string, kind: 'ac'|'approach'): { ok: true, text: string } | { ok: false, error: 'missing'|'duplicate'|'order' }`: the lines strictly between the begin and end marker lines; markers inside a fenced code block do not count.
  - `neutralize(text: string): string`: every `<!--` becomes `&lt;!--`; a line-leading `tr-develop` becomes `tr&#8209;develop`.
  - `fenceFor(text: string): string`: a backtick fence one longer than the longest backtick run in `text`, at least three.
  - CLI: `node regions.js body <file>` (file is JSON `{title, body}`) and `node regions.js ac|approach <file>` (file is the body text) print one JSON line `{ok, hash?, error?}`.

- [ ] **Step 1: Write the golden blocks** in `handoff.md` and the tests. Goldens: `normalize-pairs` is a list of `{name, a, b, same}`; the test asserts `regionHash(a) === regionHash(b)` equals `same`. Include, with `same:false`: `src/*.ts` vs `src/.ts`, `max_retries` vs `maxretries`, `> 5` vs `< 5`, `-1` vs `1`, `5 0` vs `50`, `&lt;` vs `<`; with `same:true`: `- [ ] a` vs `- [x] a`, `1. [ ] a` vs `1. [X] a`, `  - [ ] nested` vs `  - [x] nested`, LF vs CRLF text, text with and without trailing spaces and a final blank line. `body-pairs` is a list of `{t1,b1,t2,b2,same}` including `("Fix login","Crash on retry")` vs `("Fix login Crash","on retry")` with `same:false`. `region-cases` is a list of `{body, kind, expect}` covering: well-formed ac region; well-formed approach region; missing begin; missing end; two ac pairs (`duplicate`); end before begin (`order`); an ac pair inside a fenced code block only (`missing`); an ac pair inside a fence plus a real one (ok, real one). `neutralize-cases` is a list of `{in, out}` including a body holding `<!-- tr-develop:ac-begin -->` and a line starting `tr-develop/v1`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/regions.test.js`. Expected: FAIL, module missing.
- [ ] **Step 3: Implement** the functions in `regions.js` with the CLI under an `import.meta.url` main guard. `fenceFor('')` returns three backticks; `fenceFor('a ```` b')` returns five.
- [ ] **Step 4: Add CLI tests** that spawn `node regions.js` on temp files with `child_process.spawnSync` and assert the JSON output for one `ok` case and one error case.
- [ ] **Step 5: Run again.** Expected: PASS.
- [ ] **Step 6: Commit** `feat(develop): add region extraction and hashing`.

### Task 5: Checkpoint grammar

**Files:**
- Create: `skills/develop/scripts/checkpoints.js`
- Modify: `skills/develop/handoff.md` (add `## Checkpoints` with goldens)
- Test: `skills/develop/scripts/checkpoints.test.js`

**Interfaces:**
- Consumes: `readGolden`.
- Produces (spec section 10):
  - `PHASES: string[]` (the nine phases) and `PROGRESS_PHASES: string[]` = `kickoff, plan-approved, tasks, final-approved, pr-open, closed, reapproved`.
  - `parseCheckpoint(commentBody: string): { ok: true, phase: string, fields: Record<string,string>, line: string } | { ok: false, error: 'no-line'|'multiple-lines' }`: strip `\r` first; exactly one trimmed line matching the grammar; that line may sit inside a fence; fields are the `key=value` tokens.
  - `requiredFields(phase: string, track: 'plan'|'small'): string[]` per the table below.
  - `validateCheckpoint(cp: {phase, fields}, ctx: { repo: string, base: string, isAncestor: (sha: string) => boolean }): { ok: true } | { ok: false, error: 'missing'|'bad-value', key: string }`: required fields present; `branch` matches the branch pattern or is `-` on `blocked`; `spec` and `plan` match their path patterns; `head`, `planSha`, `approvedSha` are 40 hex, and `head`, `planSha` (and `approvedSha` on `defined`/`reapproved`) satisfy `ctx.isAncestor`; `bodyHash`, `acHash` are 64 hex; counters (`tasksDone`, `cyclesDone`, `deferred`, `rounds.*`) match `^\d+$`; `repo` equals `ctx.repo` case-insensitively and `base` equals `ctx.base` on `defined`/`reapproved`; `blocker` is `1`..`6`; `review` is `agent|self`.
  - `formatCheckpoint(phase: string, fields: Record<string,string>): string`: the `tr-develop/v1 phase=<phase> key=value ...` line, `branch` first, other keys in the order of the table below, unknown keys last sorted.

Required fields (all tracks unless noted; "plan:" means plan track only):
`defined`: track, repo, base, branch, bodyHash, acHash, definedBy, definedAt; plan: spec, approvedSha. `reapproved`: everything `defined` requires plus head, review, gh. `kickoff`: track, branch, head, review, gh; plan: spec, approvedSha. `plan-approved`: track, branch, head, review, gh, spec, approvedSha, plan, planSha, rounds.plan. `tasks`: track, branch, head, review, gh, deferred; plan: spec, approvedSha, plan, planSha, tasksDone; small: cyclesDone. `final-approved`: track, branch, head, review, gh, deferred, rounds.final; plan: spec, approvedSha, plan, planSha, tasksDone. `pr-open`: track, branch, head, review, gh, pr, deferred; plan: spec, approvedSha, plan, planSha, tasksDone. `blocked`: branch, stage, blocker. `closed`: branch, head, gh, pr.

- [ ] **Step 1: Write the golden blocks and tests.** `parse-cases`: list of `{name, body, expect}`; expect is `{ok:true, phase}` or `{ok:false, error}`. Cover: summary text then a fenced line (ok); CRLF body with trailing spaces after the line (ok); two matching lines (`multiple-lines`); the line quoted inside the summary plus the real one in the fence (`multiple-lines`); no line (`no-line`); an unknown phase `phase=deploy` (`no-line`); a leading-space line (ok, trimmed); a token without `=` (`no-line`). `validate-cases`: list of `{name, cp, ctx, ancestors, expect}` where `ancestors` lists the SHAs `isAncestor` accepts; cover one valid line per phase and each `missing`/`bad-value` code at least once, including `branch=-` valid on `blocked` and invalid on `kickoff`, `blocker=7`, `head` 39 hex, `bodyHash` 63 hex, `plan` path outside `docs/superpowers/plans/`, unreachable `planSha`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/checkpoints.test.js`. Expected: FAIL, module missing.
- [ ] **Step 3: Implement** the four exports. Keep the grammar regex and the per-phase table as data at the top of the file.
- [ ] **Step 4: Add a round-trip test**: for every valid golden, `parseCheckpoint(formatCheckpoint(phase, fields))` returns the same fields.
- [ ] **Step 5: Run again.** Expected: PASS.
- [ ] **Step 6: Commit** `feat(develop): add the checkpoint grammar and validation`.

### Task 6: Which comments count

**Files:**
- Create: `skills/develop/scripts/counted.js`
- Modify: `skills/develop/handoff.md` (add `## Trust` with goldens)
- Test: `skills/develop/scripts/counted.test.js`

**Interfaces:**
- Consumes: `parseCheckpoint`, `PROGRESS_PHASES` (Task 5); `readGolden`.
- Produces (spec section 9). A comment is `{ id: string, authorLogin: string|null, editorLogin: string|null, lastEditedAt: string|null, isMinimized: boolean, body: string }`; arrays are in posted order.
  - `isWriteAccess(perm: { role_name?: string, permission?: string } | null): boolean`.
  - `isLogin(s: string): boolean`: `^[A-Za-z0-9](?:[A-Za-z0-9_-]{0,38})$`.
  - `counts(c: Comment, hasWrite: (login: string) => boolean): boolean`: author present, `isLogin(author)` and `hasWrite(author)`; if `editorLogin` is set it must also pass; `editorLogin === null && lastEditedAt !== null` is false; `isMinimized` is false.
  - `countedCheckpoints(comments: Comment[], hasWrite): { id: string, phase: string, fields: Record<string,string> }[]`: only counted comments with a parseable checkpoint line.
  - `governingPin(cps): checkpoint | null`: the latest with phase `defined` or `reapproved`.
  - `latestProgress(cps): checkpoint | null`: the latest with a phase in `PROGRESS_PHASES`.

- [ ] **Step 1: Write goldens and tests.** `perm-cases`: `{perm, expect}` for `{role_name:'admin'}`, `maintain`, `write` → true; `{permission:'write'}` → true (custom role on write); `{role_name:'triage',permission:'read'}`, `{permission:'none'}`, `null` → false. `login-cases`: `paul-marvin_radancy`, `a`, `a-b_c` accepted; `x; rm -rf /`, `$(id)`, `-lead`, `` (empty), 40-character name rejected. `count-cases`: `{name, comment, writers, expect}` covering: writer author counts; non-writer author does not; writer author but non-writer editor does not; null editor with `lastEditedAt` set does not; minimized does not; null author does not. `pin-cases`: `{name, comments, writers, expectPin, expectProgress}` where the expectations are comment ids or null; cover: an outsider's forged `defined` after a writer's (ignored); a later `reapproved` becomes the pin; `blocked` is never progress; a lone `kickoff` with no pin gives `expectPin:null` and a progress id; a minimized pin retires it.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/counted.test.js`. Expected: FAIL.
- [ ] **Step 3: Implement** the five functions.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add comment counting and pin selection`.

### Task 7: Position table and routing

**Files:**
- Create: `skills/develop/scripts/route.js`
- Modify: `skills/develop/handoff.md` (add `## Resume and routing` with goldens)
- Test: `skills/develop/scripts/route.test.js`

**Interfaces:**
- Consumes: `countedCheckpoints`, `governingPin`, `latestProgress` (Task 6).
- Produces (spec sections 5 and 10):
  - `countTasks(planText: string): number`: lines matching `^### Task (\d+):` outside fenced code blocks.
  - `countCycles(approachText: string): number`: lines matching `^\s*Cycle (\d+):`.
  - `position(progress: { phase: string, fields: Record<string,string> } | null, counts: { tasks: number|null, cycles: number|null }): 'start'|'terminal'|'after-the-pr'|'finish'|'write-plan'|'final-loop'|'block6'|` `` `task:${number}` `` `|` `` `cycle:${number}` ``. Rows: `null`→`start`; `closed`→`terminal`; `pr-open`→`after-the-pr`; `final-approved`→`finish`; otherwise plan track: no `planSha`→`write-plan`; `counts.tasks` null or 0→`block6`; `tasksDone` (0 when absent) below `counts.tasks`→`task:<tasksDone+1>`, else `final-loop`; small track: `counts.cycles` null or 0→`block6`; `cyclesDone` (0 when absent) below it→`cycle:<cyclesDone+1>`, else `final-loop`.
  - `route(input: { comments: Comment[], hasWrite, pinValid: boolean, issueState: 'open'|'closed', baseIsDefault: boolean }): { route: 'define'|'deliver'|'after-the-pr'|'terminal'|'new-issue-or-stop'|'redefine-or-stop'|'repin-or-stop', askClosed: boolean, ignoredMarkers: boolean }`. Rules in order: no counted checkpoint at all → `define` (`ignoredMarkers` true when any uncounted comment or the body has a `tr-develop/` line, which the caller passes as `hasUncountedMarkers` in the input; add that field); latest progress `pr-open` → `after-the-pr`; latest progress `closed` → `new-issue-or-stop` when the issue is open and `baseIsDefault`, else `terminal`; no pin or `pinValid` false → `repin-or-stop` when any progress exists, else `redefine-or-stop`; otherwise `deliver` with `askClosed` true when the issue is closed.

- [ ] **Step 1: Write goldens and tests.** `position-cases`: `{name, progress, counts, expect}`, one case per row above plus: a `kickoff` carrying `planSha` resumes `task:<n>` and never `write-plan`; a `reapproved` after `final-approved` (so no `pr`, `tasksDone` equal to `counts.tasks`) gives `final-loop`; `counts.tasks` 0 gives `block6`; a small-track `kickoff` with no `cyclesDone` gives `cycle:1`. `count-cases`: plan text with `### Task 1:` and `### Task 2:` outside a fence and `### Task 3:` inside a fence → 2; text with none → 0; an approach with `Cycle 1:` and `Cycle 2:` → 2. `route-cases`: `{name, input, expect}` covering every route value, a lone counted `blocked` with no pin (`redefine-or-stop`), a pin plus progress but `pinValid:false` (`repin-or-stop`), a closed issue with a pin and progress `tasks` (`deliver`, `askClosed:true`), a closed issue whose latest progress is `pr-open` (`after-the-pr`), an open issue with latest progress `closed` and `baseIsDefault:false` (`terminal`), and an outsider-only `kickoff` on an otherwise empty issue (`define`, `ignoredMarkers:true`).
- [ ] **Step 2: Run** `node --test skills/develop/scripts/route.test.js`. Expected: FAIL.
- [ ] **Step 3: Implement** the four functions, reusing Task 6's selectors.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add the resume position table and routing`.

### Task 8: Argument parsing

**Files:**
- Create: `skills/develop/scripts/args.js`, `skills/develop/SKILL.md` (frontmatter and a `## Routing` section with the golden only; Task 12 completes it)
- Test: `skills/develop/scripts/args.test.js`

**Interfaces:**
- Consumes: `readGolden`.
- Produces (spec section 5): `parseArg(text: string, originRepo: string): { route: 'issue', number: number } | { route: 'wrong-repo', repo: string, number: number } | { route: 'pull', number: number } | { route: 'tutorial' } | { route: 'initialize' } | { route: 'define', seed: string }`. Whitespace trimmed. `^#?[1-9]\d*$` → `issue`. `<owner>/<repo>#<n>` and `https://github.com/<owner>/<repo>/issues/<n>` (query and fragment ignored) → `issue` when the repository equals `originRepo` case-insensitively, else `wrong-repo`. `https://github.com/<owner>/<repo>/pull/<n>` → `pull`. Empty, `help`, `--help`, `-h`, `?` (any case) → `tutorial`. `initialize` (any case) → `initialize`. Anything else, a non-github.com URL included, → `define` with the trimmed text as `seed`.

- [ ] **Step 1: Write the golden** `routing` in `SKILL.md` as a list of `[input, expected]` pairs with `originRepo` fixed to `strvmarv/total-recall`: `123`, ` 123 `, `#123`, `007`, `0`, `123; rm -rf /`, `fix 123`, `strvmarv/total-recall#9`, `other/repo#9` (`wrong-repo`), `https://github.com/strvmarv/total-recall/issues/7?x=1#c`, `https://github.com/strvmarv/total-recall/pull/7`, `https://example.com/strvmarv/total-recall/issues/7` (`define`), ``, `   `, `help`, `HELP`, `-h`, `?`, `--HELP`, `help me fix the cache` (`define`), `initialize`, ` Initialize `, `initialize the cache` (`define`), `add a retry to the sync command`. Write the test that runs `parseArg` over every pair.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/args.test.js`. Expected: FAIL.
- [ ] **Step 3: Implement** `parseArg`. Create `SKILL.md` with frontmatter `name: develop` and a one-sentence `description` that begins "Use when" and mentions defining a change or delivering one from a GitHub issue, then the `## Routing` heading above the golden.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add argument parsing and the routing golden`.

### Task 9: Account pinning

**Files:**
- Create: `skills/develop/scripts/ghpin.js`
- Modify: `skills/develop/SKILL.md` (nothing yet; Task 12 documents it)
- Test: `skills/develop/scripts/ghpin.test.js`

**Interfaces:**
- Consumes: `isLogin` (Task 6).
- Produces (spec section 6, check 4):
  - `qualifies(perms: { admin?: boolean, maintain?: boolean, push?: boolean } | null): boolean`.
  - `pickAccount(input: { accounts: { login: string, perms: object|null }[], active: string, profileAccount?: string }): { ok: true, login: string, ask: string[] } | { ok: false, error: 'none-qualify'|'profile-account-not-qualifying'|'bad-login', detail: string }`. Rules: a `profileAccount` must be a valid login and qualify, else `ok:false`; with no `profileAccount` the active account if it qualifies, else the only qualifying one, else the first qualifying one with `ask` listing all qualifying logins (empty `ask` otherwise); none qualify → `none-qualify` with the logins found in `detail`.
  - `ghCommand(login: string, args: string): string` returns `t=$(gh auth token --user <login>) && [ -n "$t" ] && GH_TOKEN="$t" gh <args>`.
  - `gitCommand(login: string, args: string): string` returns `t=$(gh auth token --user <login>) && [ -n "$t" ] && GH_TOKEN="$t" GIT_TERMINAL_PROMPT=0 git -c credential.helper= -c 'credential.helper=!gh auth git-credential' <args>`.
  - `psGhCommand(login: string, args: string): string` and `psGitCommand` return the PowerShell forms: `try { $env:GH_TOKEN = (gh auth token --user <login>); if (-not $env:GH_TOKEN) { throw 'no token' }; ...; } finally { Remove-Item Env:GH_TOKEN -ErrorAction SilentlyContinue }`, with the git form carrying `$env:GIT_TERMINAL_PROMPT = '0'` and the two `-c` options.
  - All four command builders throw `Error('bad login')` when `isLogin(login)` is false.

- [ ] **Step 1: Write the failing tests.** `pickAccount`: the active work account without push plus `strvmarv` with push gives `strvmarv`, `ask: []`; two qualifying accounts with no profile account gives the active one if it qualifies, else the first with `ask` listing both; `profileAccount: 'strvmarv'` that does not qualify gives `profile-account-not-qualifying`; no qualifying account gives `none-qualify` and `detail` names both logins; `profileAccount: 'x; rm'` gives `bad-login`. Commands: `ghCommand('strvmarv','api user')` equals the exact string above with `strvmarv` and `api user` substituted; `gitCommand('paul-marvin_radancy','push origin x')` contains `--user paul-marvin_radancy`, `GIT_TERMINAL_PROMPT=0`, `-c credential.helper= -c 'credential.helper=!gh auth git-credential'` and ends with `push origin x`; every builder throws `bad login` for `x; rm -rf /`, `$(id)` and the empty string; the string produced for a valid login never contains an unquoted `;` from the login.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/ghpin.test.js`. Expected: FAIL.
- [ ] **Step 3: Implement** the six exports.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add account selection and pinned gh and git commands`.

### Task 10: The repository profile

**Files:**
- Create: `skills/develop/scripts/profile.js`, `skills/develop/profile.md` (a `## Format` section with goldens; Task 18 completes it)
- Test: `skills/develop/scripts/profile.test.js`

**Interfaces:**
- Consumes: `isLogin`, `readGolden`.
- Produces (spec section 15): `parseProfile(markdown: string): { ok: true, profile: { base: string|null, ticketStandard: 'on'|'off', templates: Record<'feature'|'bug'|'chore', string>, ghAccount: string|null, verify: string[] } } | { ok: false, error: string }`. The profile block is the first fenced ```` ```text ```` block whose first line is `tr-develop/profile-v1`; then `key: value` lines; unknown keys are ignored. `base` matches `^[A-Za-z0-9._/-]+$`; `ticket.standard` is `on` or `off` (default `on`); `ticket.template.<type>` matches `^[A-Za-z0-9._-]+\.(md|ya?ml)$`; `gh.account` passes `isLogin`; each fenced block with info string `sh verify` is one command in `verify`, in order. No block → `{ ok: false, error: 'no-profile' }`; a bad value → `{ ok: false, error: 'bad-value:<key>' }`. A missing file is the caller's concern.

- [ ] **Step 1: Write the golden** `profile-cases` in `profile.md`: `{name, markdown, expect}` covering the full example from spec section 15 (with `gh.account: strvmarv` and two verify blocks), a minimal block with only the header, an unknown key ignored, `ticket.standard: maybe` (`bad-value:ticket.standard`), `gh.account: x; rm` (`bad-value:gh.account`), `ticket.template.bug: ../../etc/passwd` (`bad-value:ticket.template.bug`), and a document with no profile block (`no-profile`). Write the test over them.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/profile.test.js`. Expected: FAIL.
- [ ] **Step 3: Implement** `parseProfile`.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add the repository profile parser`.

### Task 11: Reviewer agents

**Files:**
- Create: `agents/adversarial-reviewer.md`, `agents/counterexample-reviewer.md`
- Test: `skills/develop/scripts/agents.test.js`

**Interfaces:**
- Produces: two agent definitions named exactly `adversarial-reviewer` and `counterexample-reviewer` (spec section 12). Both: frontmatter `name`, a `description` of one or two sentences, `tools: Read, Grep, Glob, Bash`; body written fresh. Both must state: read-only, never edit, no state-changing shell commands; never dispatch; end every reply with the exact block `VERDICT: GO | NO-GO` then `FINDINGS:` then one `- [blocker|major|minor] <file:line | section> — <issue> — <suggested fix>` line per finding or `- none`; blocker and major only for a wrong, unsafe or unbuildable result; round awareness ("round N of ≤ 6" and the previous triage). `adversarial-reviewer` reviews for correctness, simplicity, security and fit with the codebase. `counterexample-reviewer` assumes the change is wrong, reports only defects traceable to a concrete input or state, applies the state-flow and consequence rules of section 12, and does not run builds or tests when paired.

- [ ] **Step 1: Write the failing test** that reads both files and asserts: frontmatter `name` equals the filename stem; `tools` line equals `Read, Grep, Glob, Bash`; the body contains `VERDICT: GO | NO-GO`, `FINDINGS:`, `- [blocker|major|minor]` and the word `read-only`; neither file contains `Edit`, `Write` as a tool name, nor the forbidden provenance strings of Global Constraints; the counterexample file contains `concrete input or state`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/agents.test.js`. Expected: FAIL.
- [ ] **Step 3: Write both agent files.**
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add the reviewer agents`.

### Task 12: Skill structure test and the main skill file

**Files:**
- Create: `skills/develop/scripts/skill.test.js`, `skills/develop/tutorial.md`
- Modify: `skills/develop/SKILL.md`

**Interfaces:**
- Consumes: `goldenNames`, `readGolden`, `parseArg`.
- Produces: `skill.test.js`, a table-driven structure test that later tasks extend with one entry each: `FILES = { 'SKILL.md': { headings: [...], goldens: [...] }, ... }`. For every entry it asserts the file exists, every listed `## ` heading is present, every listed golden name parses, and every relative Markdown link in every skill file resolves to an existing file. It also asserts: `SKILL.md` frontmatter has `name: develop` and a `description` starting `Use when`; no file under `skills/develop/` or `agents/` (the two new agents) contains a forbidden provenance string; every golden in `SKILL.md` named `routing` still passes `parseArg`.

`SKILL.md` must contain these `## ` sections, written from the spec: `Routing` (the Task 8 golden stays), `Issue entry` (section 5; routes come from `route.js`), `Hosts` (section 5's table and the pi mapping table), `Preflight` (section 6, all eight checks, including the pinned command forms from `ghpin.js` and the order of check 4), `Overrides of superpowers defaults` (the O1 to O10 table of section 13), `Worktrees` (section 14, and the worktree rules of section 11), `Progress` (section 14), `Memory` (section 16), `Commits and PRs` (no attribution; the repository's own conventions), `Files` (a link to every other file in the skill). `tutorial.md` is what `/develop` with no argument prints: what the skill does, the two phases, one example of each argument form, and the repository requirements, in under 60 lines.

- [ ] **Step 1: Write `skill.test.js`** with `FILES` holding only `SKILL.md` (headings above, golden `routing`) and `tutorial.md` (heading `# develop`, at most 60 lines).
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL (missing headings).
- [ ] **Step 3: Write the sections** of `SKILL.md` and `tutorial.md`. The Preflight section quotes the two pinned command forms exactly as `ghpin.js` builds them, and states that the pi mapping is `ask_user`, `update_progress`, `Agent`, and `cd <dir> &&` worktree entry.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add the main skill file, the tutorial and the structure test`.

### Task 13: The ticket standard

**Files:**
- Create: `skills/develop/ticket-standard.md`
- Modify: `skills/develop/scripts/skill.test.js` (add an entry)

**Interfaces:**
- Produces: the standard Define writes to and the reviewer grades against (spec section 7). Required `## ` sections: `Is it one ticket`, `Problem before solution`, `Acceptance criteria`, `Scope`, `Ready`, `Shape by type` (the Feature, Bug and Chore section lists exactly as in section 7), `Title, labels and type`, `Repository overrides` (templates under `.github/ISSUE_TEMPLATE/` at the default branch; the profile's `ticket.template.<type>` and `ticket.standard`; the marked criteria region is always present), `Review grades` (missing section, uncheckable criterion, or engineering detail in a plan-track ticket is a major; title or length is a minor), `Markers` (the criteria and approach markers and the 60-line, 4,000-character approach limit).

- [ ] **Step 1: Extend `FILES`** in `skill.test.js` with `ticket-standard.md` and its headings, plus a test that the file mentions each of `INVEST`, `Given/When/Then`, `.github/ISSUE_TEMPLATE`, `<!-- tr-develop:ac-begin -->` and `60 lines`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL.
- [ ] **Step 3: Write the file** from spec section 7 in your own words.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(develop): add the ticket standard`.

### Task 14: The handoff reference

**Files:**
- Modify: `skills/develop/handoff.md`, `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Produces: the reference the other files link to. `handoff.md` already holds the goldens from Tasks 4 to 7. Add the prose so the file has these `## ` sections: `Hashing and regions` (spec section 8, including the neutralization rule and the `bodyHash`/`acHash` definitions), `The issue after Define` (regions, the `defined` comment and its keys table), `Checkpoints` (grammar, field table, read-back), `Trust` (write access, what counts, validation, integrity, the spec guard and its exemptions, terminal-only stops, the one-run permission cache), `Resume and routing` (pin, progress, the position table, work pushed after the last checkpoint, what survives a crash, concurrency, the issue's own state), `Re-approval` (spec section 11's steps 0 to 6, atomic, with the `reapproved` fields), `After the PR` (open, merged and closed-unmerged outcomes).

- [ ] **Step 1: Extend `FILES`** with `handoff.md`, its seven headings and the golden names `normalize-pairs`, `body-pairs`, `region-cases`, `neutralize-cases`, `parse-cases`, `validate-cases`, `perm-cases`, `login-cases`, `count-cases`, `pin-cases`, `position-cases`, `route-cases`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL on the missing headings.
- [ ] **Step 3: Write the prose** from spec sections 8 to 11. State rules once; point at the scripts for anything exact (`node skills/develop/scripts/regions.js ...`).
- [ ] **Step 4: Run** the whole skill test directory: `node --test skills/develop/scripts/`. Expected: PASS.
- [ ] **Step 5: Commit** `docs(develop): write the handoff reference`.

### Task 15: Define

**Files:**
- Create: `skills/develop/define.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Produces (spec section 11, Define): `## ` sections `Fetch`, `Claim`, `Worktree`, `Brainstorm`, `Plan track`, `Small track`, `Sign-off`, `Publish`, `Wrap-up`, each stating its numbered step from the spec: the stop outcomes and their one short note, the track sizing rules, the ticket standard applied to the preview, the sign-off's four choices and the explicit-approval rule, local hashing at publish with the read-back and the two-try cap, the `defined` comment, and the wrap-up for choices 1 and 2. A final `Rules` section: never edit the spec after approval; an outsider's issue is tracked in a new issue by default; a stale `wip/` branch is never reused.

- [ ] **Step 1: Extend `FILES`** with `define.md` and its ten headings, plus a test that the text mentions `bodyHash`, `acHash`, `defined`, `Original report` and `at most twice`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL.
- [ ] **Step 3: Write the file** from the spec.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `docs(develop): write the Define flow`.

### Task 16: Deliver

**Files:**
- Create: `skills/develop/deliver.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Produces (spec section 11, Deliver): `## ` sections `Common steps` (steps 1 to 6 as amended, including the order fetch, read, route, concurrency, worktree look, integrity, workspace, kickoff and its skip rule), `Plan track`, `Small track`, `Finish and close-out`, `Merged close-out`. Each states its steps and which script decides what (`route.js` for the route and position, `regions.js` for hashes, `checkpoints.js` for every posted line). `Finish and close-out` includes the docs-update rule, the profile's verify commands, `Closes #<n>` versus `Refs #<n>`, the existing-PR rule (head repository equals this repository), and the no-attribution rule for commits and PR bodies.

- [ ] **Step 1: Extend `FILES`** with `deliver.md` and its five headings, plus tests that the text mentions `route.js`, `position`, `Closes #`, `Refs #`, `final-approved`, `never merge`, and does not contain `Co-Authored-By`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL.
- [ ] **Step 3: Write the file** from the spec.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `docs(develop): write the Deliver flow`.

### Task 17: Review contract and blockers

**Files:**
- Create: `skills/develop/review-contract.md`, `skills/develop/blockers.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Produces: `review-contract.md` (spec section 12) with `## ` sections `Dispatch frame` (the five parts in order, with the fanout line "at most 3 primary agents, each dispatching at most 1 sub-agent"), `Loop rules`, `The final loop` (600-line threshold, paired review, failure handling), `Self-review mode`, `Agents`. `blockers.md` (section 14) with `Stop for these` (the six blockers, numbered as in the spec), `Decide and continue`, `At a blocker` (the procedure, including the terminal-only kinds and the `blocked` fields `stage` and `blocker`).

- [ ] **Step 1: Extend `FILES`** with both files and headings, plus tests that `review-contract.md` contains `round N of ≤ 6`, `600`, `VERDICT: GO | NO-GO`, and that `blockers.md` contains each of the numbers 1 to 6 as a list marker and the words `terminal`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL.
- [ ] **Step 3: Write both files** from the spec.
- [ ] **Step 4: Run again.** Expected: PASS.
- [ ] **Step 5: Commit** `docs(develop): write the review contract and the blocker list`.

### Task 18: The profile document and initialize

**Files:**
- Modify: `skills/develop/profile.md`, `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Produces: `profile.md` already holds the `profile-cases` golden from Task 10. Add `## ` sections `Format` (keys and their meaning from spec section 15, including `gh.account`), `Loading` (read from the default branch only), `Verify blocks`, `Initialize` (the `/develop initialize` flow: read the repository's docs, workflows and issue templates; propose each value; one reviewer loop over the file; commit on `develop-profile/<date>`; open a PR that is never merged).

- [ ] **Step 1: Extend `FILES`** with `profile.md`, its four headings and golden `profile-cases`, plus a test that the text mentions `.github/develop.md`, `default branch` and `never merged`.
- [ ] **Step 2: Run** `node --test skills/develop/scripts/skill.test.js`. Expected: FAIL.
- [ ] **Step 3: Write the prose.**
- [ ] **Step 4: Run** `node --test skills/develop/scripts/`. Expected: PASS.
- [ ] **Step 5: Commit** `docs(develop): write the profile reference and the initialize flow`.

### Task 19: The `/develop` command on pi

**Files:**
- Create: `pi/develop.js`
- Modify: `pi/extension.js` (call `registerDevelop` next to `registerCommands`), `pi/extension.test.js`, `pi/package.test.js`
- Test: `pi/develop.test.js`

**Interfaces:**
- Consumes: the `hookable(ctx)` function and `rootDir` already in `pi/extension.js`.
- Produces: `developText(args: string, skillPath: string): string` returns a user message that names `skillPath`, tells the model to read it and follow it, and carries the arguments verbatim (a blank argument says there are none, which the skill routes to the tutorial). `registerDevelop(pi, { hookable, skillPath })` registers `develop` with `pi.registerCommand`; the handler shows `ctx.ui.notify('develop is not active in this session', 'warning')` and returns when `!hookable(ctx)`, else `pi.sendUserMessage(developText(args, skillPath), ctx.isIdle() ? undefined : { deliverAs: 'followUp' })`. A throw from `registerCommand` is caught and ignored.

- [ ] **Step 1: Write the failing tests** in `pi/develop.test.js` using the `fakePi`/`fakeCtx` shape from `pi/command.test.js`: `developText('123', '/r/skills/develop/SKILL.md')` contains `/r/skills/develop/SKILL.md` and `123`; blank arguments mention no arguments; `registerDevelop` registers exactly one command named `develop`; its handler sends one user message when `hookable` is true, and only notifies (sends nothing) when false; a `registerCommand` that throws does not propagate; an argument containing a newline and quotes is carried verbatim. In `pi/extension.test.js` add a test that a session registers both `total-recall` and `develop`, and that a child (`PI_SUBAGENT_ID` set) gets the not-active notice from the `develop` handler. In `pi/package.test.js` keep the existing assertions and add: `skills/develop/SKILL.md` exists and the pi block still declares no skills.
- [ ] **Step 2: Run** `node --test pi/`. Expected: FAIL (missing module, missing registration).
- [ ] **Step 3: Implement** `pi/develop.js` and the one-line wiring in `pi/extension.js`, inside the existing try block that already guards `registerCommands`.
- [ ] **Step 4: Run again.** Expected: PASS for the whole `pi/` directory.
- [ ] **Step 5: Commit** `feat(pi): register a /develop command that runs the develop skill`.

### Task 20: Wire into the repository

**Files:**
- Modify: `package.json` (`test:shim`), `AGENTS.md`, `README.md`, `CHANGELOG.md`
- Create: `.github/develop.md`

**Interfaces:**
- Produces: `test:shim` runs `skills/develop/` too; the repository documents and uses the skill.

- [ ] **Step 1: Edit `package.json`** so `test:shim` is `node --test bin/shim/ pi/ codex/ skills/develop/ scripts/fetch-binary.test.js hooks/hooks.config.test.js`. Run `npm run test:shim`. Expected: PASS, and the output counts the `skills/develop` tests. If Node does not discover tests in a nested `scripts/` directory under this form, list `skills/develop/scripts/` instead and say so in the commit body.
- [ ] **Step 2: Create `.github/develop.md`** with the profile block for this repository: header, `gh.account: strvmarv`, `ticket.standard: on`, and `sh verify` blocks `dotnet test src/TotalRecall.sln --filter "Category!=Integration"` and `npm run test:shim`. Add a test to `skills/develop/scripts/profile.test.js` that parses this file with `parseProfile` and expects `ok: true`, `ghAccount: 'strvmarv'`, and two verify commands.
- [ ] **Step 3: Edit `AGENTS.md`**: add "WHERE TO LOOK" rows for the skill (`skills/develop/`), its scripts and tests (`skills/develop/scripts/`), the reviewer agents (`agents/*-reviewer.md`) and the pi command (`pi/develop.js`); add a short `develop skill` section stating the trust line, the pinned `gh`/`git` rule, that specs and plans are committed, and the `.github/develop.md` profile.
- [ ] **Step 4: Edit `README.md`** with a `develop` section (what it does, requirements: superpowers 7+, `gh`, the profile, Claude Code and pi first) near the Skills section, and `CHANGELOG.md` with an `Added` entry under `Unreleased`. Do not bump any version.
- [ ] **Step 5: Run** `npm run test:shim`. Expected: PASS.
- [ ] **Step 6: Commit** `docs(develop): document the skill and wire its tests into the shim job`.

### Task 21: Final verification

**Files:** none.

- [ ] **Step 1: Run** `npm run test:shim`. Expected: all pass, none skipped.
- [ ] **Step 2: Provenance scan.** Run `grep -rniE "cortex|jira|aiops|rai-ops|radancy|noformat|strata" skills/develop agents/adversarial-reviewer.md agents/counterexample-reviewer.md pi/develop.js .github/develop.md`. Expected: no output.
- [ ] **Step 3: Attribution scan.** Run `git log main..HEAD --format=%B | grep -ciE "co-authored|generated with"`. Expected: `0`.
- [ ] **Step 4: Diff scope.** Run `git diff --stat main...HEAD`. Expected: only `skills/develop/**`, `agents/*-reviewer.md`, `pi/develop*.js`, `pi/extension*.js`, `pi/package.test.js`, `package.json`, `.github/develop.md`, `AGENTS.md`, `README.md`, `CHANGELOG.md`, `.gitignore`, and `docs/superpowers/**`.
- [ ] **Step 5: Report** to the owner with the list of what remains manual: the spec's section 19 dogfood runs and the open items of section 21 (write-but-not-admin permission check, a real push with the pinned form), each needing the owner's say-so. Do not push or open a PR.
