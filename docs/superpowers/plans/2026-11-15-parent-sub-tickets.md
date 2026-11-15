# Parent tickets and sub-tickets for `/develop` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `parent` track and sub-ticket awareness to the `/develop` skill, as the spec describes, without changing the behaviour of any ticket that has no parent.

**Architecture:** The skill is Markdown prose plus zero-dependency Node scripts under `skills/develop/scripts/`. The grammar and routing live in `checkpoints.js` and `route.js`, and their test fixtures (goldens) live in fenced JSON blocks in `handoff.md`, which `route.test.js`, `checkpoints.test.js` and `skill.test.js` read. New behaviour is added script-first (tests first), then the prose that tells the model to use it, then the user-facing docs.

**Tech Stack:** Node 20 ES modules, `node:test`, `node:assert/strict`. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-15-parent-sub-tickets-design.md` (read-only: the spec guard runs before every push). The plan cites it as "spec §N". Executors read both.

## Global Constraints

- All scripts keep the existing conventions: zero dependencies, ESM, `node <script> <subcommand> <file>`, one JSON object on stdout, failure `{"ok":false,"error":...}` with exit 1, text only through files, `SUBCOMMANDS` exported by every CLI.
- No existing assertion in any test or golden is weakened or deleted. A test tied to prose that this plan changes (the integrity-exemption list in `skill.test.js`) is updated in the same task as the prose.
- `track` values: `plan`, `small`, `parent`. Phases: the nine existing ones plus `spec-merged` (parent track only).
- New fields and patterns: `parent` `^[1-9]\d{0,8}$`; `parentBlob` and `specBlob` 40 lowercase hex; `definedAt` is exactly `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$`.
- Parent spec breakdown grammar (spec §2): headings `### Part <n>: <title>`, n from 1 with no gap, repeat or leading zero, 1 to 50 parts, title 1 to 80 characters with no leading or trailing space; labels `Scope:`, `Depends on:`, `Done when:` exactly once each, in that order; each label's text non-empty and at most 1,500 characters; `Depends on:` is `none` or `Part <m>` entries joined by `, `, ascending, no repeat, each `m` lower than `n`.
- Stub first line: `^Part ([1-9]\d?) of ([1-9]\d?) in #([1-9]\d{0,8})$` after removing a BOM and `\r` and trimming.
- Commits: plain one-line sentences like the repository's history, no trailers of any kind, no AI attribution. Never merge, rebase or force-push.
- Run every `gh` call through `node skills/develop/scripts/ghpin.js run <jsonFile>` with login `strvmarv`. Do not run `gh auth switch`.
- Verification command for the skill after every task: `node --test skills/develop/scripts/*.test.js` (all tests pass; `skill.test.js` has `COMPLETE = true`, so every new subcommand must be named in a skill Markdown file as `<script>.js <subcommand>`).

## Review Focus

Inputs the spec implies that no single task's tests would otherwise exercise; each line is pinned by a test in the task named.

1. A parent spec whose breakdown headings appear inside a fenced code block (the spec's own example does): they must not count as parts. Task 3.
2. A parent spec saved with CRLF line endings: `parts`, `stub` and `parent-line` read it like LF. Task 3.
3. A stub body that GitHub's web editor saved with a BOM or CRLF, or whose first line has trailing spaces: still a stub line. Task 3.
4. Part text containing a line that starts with `tr-develop/` or an `<!-- tr-develop:` marker: `stub-body` refuses it. Task 3.
5. A parent whose latest progress is `spec-merged` while the pin is missing, a `plan` pin, or has a bad `spec`, `specBlob` or `definedAt`: route `terminal-pin`, never `repin-or-stop`. Task 4.
6. A fresh `spec-merged` alone does not make the next `/develop` think a run is live. Task 4.
7. Every pre-existing pin and checkpoint (no `track=parent`, no `parent`) validates and routes exactly as before: the existing golden cases still pass unchanged. Tasks 2 and 4.

---

### Task 1: Probe the sub-issue link call in a throwaway repository

**Files:**
- Create: `docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a findings file that later prose tasks cite. It records, each with the observed HTTP status and the relevant response fields: (a) the working form of the link call (`POST repos/<o>/<r>/issues/<parent>/sub_issues` with `-F sub_issue_id=<numeric id>`), and what `-f` (string) does instead; (b) what linking the same issue twice returns; (c) whether creating and linking five issues in a row hits a rate limit; (d) the `state_reason` of an issue closed by merging a PR whose body says `Closes #<n>` into the default branch; (e) the sub-issue count limit is not probed (the design caps a breakdown at 50 parts; record GitHub's documented limit if it can be read).

This task is the one outward-facing action in the plan and the person approved it explicitly: create one private repository `strvmarv/develop-probe`, use it, archive it (the token has no delete scope, so the person deletes it later), and touch nothing else on GitHub.

- [ ] **Step 1: Create the repository**

`gh api -X POST user/repos -f name=develop-probe -F private=true -F auto_init=true`, through `ghpin.js run` with an `out` file. Expected: HTTP 201, `full_name` `strvmarv/develop-probe`, `default_branch` `main`.

- [ ] **Step 2: Create one parent issue and three child issues; link two children**

Create the four issues with `-F title=@<file>`; read each response's `number` and numeric `id`. Link child 1 with `-F sub_issue_id=<id>` and child 2 with `-f sub_issue_id=<id>` (the string form). Record both statuses and response bodies. Expected: the integer form returns 201 with the parent issue's JSON; record whatever the string form does.

- [ ] **Step 3: Link twice, then five in a row**

Link child 1 again (record the status and message). Create five more issues and link each immediately, with no pause; record every status and any `Retry-After` or rate-limit message.

- [ ] **Step 4: Probe `state_reason` after a `Closes` merge**

Push a branch with one new file to the probe repository (Contents API `PUT repos/strvmarv/develop-probe/contents/probe.txt` on a new branch is enough), open a PR whose body is `Closes #<child 3>`, merge it with `PUT repos/strvmarv/develop-probe/pulls/<n>/merge`, then read the child. Record `state` and `state_reason`. Expected: `closed` and `completed`.

- [ ] **Step 5: Archive the repository and write the findings file**

`PATCH repos/strvmarv/develop-probe` with `-F archived=true`. Write `docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md` with a heading per finding (a) to (e), the observed status and the exact working command form for each. If any call behaved differently from the spec's assumption, say so in a "Differs from the spec" section at the top (an empty section reads "None").

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md
git commit -m "Record what the sub-issue link call does in a throwaway repository"
```

---

### Task 2: Checkpoint grammar for the parent track and `spec-merged`

**Files:**
- Modify: `skills/develop/scripts/checkpoints.js`
- Modify: `skills/develop/scripts/checkpoints.test.js`
- Modify: `skills/develop/handoff.md` (the `parse-cases`, `validate-cases` and `carry-cases` goldens, and the Checkpoints tables)

**Interfaces:**
- Consumes: the existing `PHASES`, `PROGRESS_PHASES`, `LINE_RE`, `TRACKS`, `REQUIRED`, `KEY_ORDER`, `valueOk`, `validateCheckpoint`, `requiredFields` in `checkpoints.js`.
- Produces (later tasks rely on these exact names): exported `DEFINED_AT_RE`, `SPEC_RE`, `SHA_RE` (the regexes already in the file, exported, plus the new `definedAt` one); `PHASES` and `PROGRESS_PHASES` each contain `'spec-merged'`; `requiredFields(phase, track)` accepts track `'parent'`.

Implements spec §3 (fields, phase, required fields) and §9 (`checkpoints.js` bullet).

- [ ] **Step 1: Write the failing tests in `checkpoints.test.js` and the golden cases in `handoff.md`**

Add to the `validate-cases` golden, in the existing case shape (`name`, `cp`, `ctx`, `reachable`, `expect`):
- `spec-merged parent track` valid, with `track=parent`, `branch`, `head` (a sha NOT listed in `reachable`, proving the exemption), `gh`, `pr` (this repository), `spec`, `specBlob`. Expect `{"ok":true}`.
- `spec-merged missing specBlob` expects `{"ok":false,"error":"missing","key":"specBlob"}`; `spec-merged specBlob 39 hex` expects `bad-value` on `specBlob`; `spec-merged track plan` expects `bad-value` on `track`.
- `defined parent track` valid (needs `spec`, `approvedSha`, reachable); `defined parent track carrying parent` expects `bad-value` on `parent` (one level only).
- `kickoff parent track` valid; `pr-open parent track` valid with `deferred` `0` and no `plan`, `planSha` or `tasksDone`; `pr-open parent track deferred 2` expects `bad-value` on `deferred`; `reapproved parent track` valid (no plan fields).
- `tasks parent track`, `plan-approved parent track` and `final-approved parent track` each expect `{"ok":false,"error":"bad-value","key":"track"}`.
- `defined plan track with parent and parentBlob` valid; `defined with parent but no parentBlob` expects `missing` `parentBlob`; `parent 0` expects `bad-value` `parent`; `parentBlob 41 hex` expects `bad-value` `parentBlob`; `kickoff carrying parent` expects `bad-value` `parent` (only `defined` and `reapproved` carry it).
- `definedAt with an offset` (`2026-11-10T12:34:56+01:00`) expects `bad-value` `definedAt`.
Add to `parse-cases` one `spec-merged` line that parses, and to `carry-cases` one `kickoff` after a `spec-merged` prior with the plan fields absent (expect `{"ok":true}`).

In `checkpoints.test.js` add: `PHASES` and `PROGRESS_PHASES` include `spec-merged`; `requiredFields('spec-merged','parent')` equals `['track','branch','head','gh','pr','spec','specBlob']`; `requiredFields('pr-open','parent')` has `spec` and `approvedSha` and none of `plan`, `planSha`, `tasksDone`; `formatCheckpoint` writes `parent`, `parentBlob` and `specBlob` after `approvedSha` and before `head`, so a `defined` with `parent` formats as `... spec=... approvedSha=... parent=12 parentBlob=...`.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/checkpoints.test.js`
Expected: FAIL (unknown phase `spec-merged`, unknown track `parent`).

- [ ] **Step 3: Implement in `checkpoints.js`**

- Add `'spec-merged'` to `PHASES`, `PROGRESS_PHASES` and the phase alternation in `LINE_RE`; add `'parent'` to `TRACKS`.
- Add `DEFINED_AT_RE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/` and export it with `SPEC_RE` and `SHA_RE`.
- `REQUIRED` entries gain a `parent` array: `defined` and `reapproved` and `kickoff` and `pr-open` need `['spec','approvedSha']`; add `'spec-merged': { all: ['track','branch','head','gh','pr','spec','specBlob'], plan: [], small: [], parent: [] }`. `requiredFields` returns `entry.all` plus `entry.parent` for track `'parent'`. `pr-open`'s `plan` list (`plan`, `planSha`, `tasksDone`) must not apply to `parent`.
- `valueOk` gains cases: `parent` (`PR_NUMBER_RE`), `parentBlob` and `specBlob` (`SHA_RE`), `definedAt` (`DEFINED_AT_RE`), and `head` is also exempt from reachability when `phase === 'spec-merged'`.
- `validateCheckpoint`, after the existing required-field loop, adds: track `parent` is `bad-value` `track` on phases `plan-approved`, `tasks` and `final-approved`; `spec-merged` requires track `parent` (a `plan` or `small` track is `bad-value` `track`); on track `parent` a present `deferred` on `pr-open` must equal `'0'` (`bad-value` `deferred`); `parent` and `parentBlob` are a pair (`missing` for the absent one) and are `bad-value` on any phase other than `defined` and `reapproved`, and on track `parent`.
- `KEY_ORDER` gains `'parent'`, `'parentBlob'`, `'specBlob'` right after `'approvedSha'`.
- Add to `CARRY_KEYS` nothing (parent fields are not carried).

- [ ] **Step 4: Run the tests**

Run: `node --test skills/develop/scripts/checkpoints.test.js skills/develop/scripts/counted.test.js`
Expected: PASS, including every pre-existing golden case unchanged.

- [ ] **Step 5: Update the Checkpoints prose tables in `handoff.md`**

In "Checkpoints": the line grammar alternation, the "Patterns" paragraph (`parent`, `parentBlob`, `specBlob`, `definedAt`, `track` is `plan`, `small` or `parent`), the "Required fields" table (a `spec-merged` row; a "parent:" prefix like "plan:" and "small:" for the parent track on `kickoff`, `pr-open` and `reapproved`), the "What the fields mean" table (the three new fields), and the Reachability paragraph (`spec-merged`'s `head` follows the `closed` exemption). Do not touch the integrity-exemption sentences here (Task 6).

- [ ] **Step 6: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/scripts/checkpoints.js skills/develop/scripts/checkpoints.test.js skills/develop/handoff.md
git commit -m "Teach the checkpoint grammar the parent track and the spec-merged phase"
```

---

### Task 3: Breakdown, stub and parent-line parsing in `route.js`

**Files:**
- Modify: `skills/develop/scripts/route.js`
- Modify: `skills/develop/scripts/route.test.js`
- Modify: `skills/develop/handoff.md` (one paragraph naming the four subcommands, in "Resume and routing")

**Interfaces:**
- Consumes: `outsideFences` from `./regions.js` (yields `{line}` for each line outside code fences, CRLF read as LF).
- Produces, exported from `route.js` and added to `SUBCOMMANDS` in this order after the existing ones: `'parts'`, `'stub'`, `'stub-body'`, `'parent-line'`.
  - `parseParts(specText: string): {ok: true, parts: Array<{n: number, title: string, scope: string, doneWhen: string, dependsOn: number[]}>} | {ok: false, error: 'no-parts'|'not-sequential'|'too-many'|'bad-title'|'bad-label'|'empty-text'|'text-too-long'|'bad-dependency'}`
  - `parseStubLine(bodyText: string): {ok: true, part: number, count: number, parent: number} | {ok: false, error: 'no-stub-line'}`
  - `stubBody(specText: string, part: number, parent: number, depends: Record<string, number>): {ok: true, title: string, body: string} | {ok: false, error: string}` where the error is a `parseParts` error, `'no-such-part'`, `'missing-dependency'` or `'marker-in-text'`.
  - `parseParentLine(fileText: string): {ok: true, parent: number, part: number, spec: string} | {ok: false, error: 'no-parent-line'}`
  - CLI forms: `parts <specFile>`, `stub <bodyFile>`, `stub-body <specFile> <part> <parent> <dependsFile> <outFile>` (writes `body` to `outFile`, prints `{ok, title}`), `parent-line <file>`.

Implements spec §2 (grammar), §5 (stub body and first line), §8 and §9. `text-too-long` and the `title` in the `stub-body` output are decisions the spec leaves open (a distinct error for the 1,500-character cap; `ensure-stubs` needs the title and must not retype it).

- [ ] **Step 1: Write the failing tests in `route.test.js`**

Group by function, each with a minimal in-memory spec text built by a small helper `part(n, {title, scope, depends, done})`:
- `parseParts`: a valid three-part spec returns the parts with `dependsOn` `[]`, `[1]`, `[1,2]`; no heading is `no-parts`; headings 1 and 3 is `not-sequential`; `### Part 02:` is `bad-title`; 51 parts is `too-many`; a title of 81 characters and a title with a trailing space are `bad-title`; a part missing `Done when:`, with `Scope:` twice, or with the labels out of order is `bad-label`; an empty `Scope:` text is `empty-text`; 1,501 characters is `text-too-long`; `Depends on: Part 2` in part 2 (not lower), `Part 1, Part 1`, `Part 2, Part 1`, `part 1` and `Part 1,Part 2` are each `bad-dependency`; `Depends on: none` gives `[]`.
- Review Focus 1 and 2: the same valid spec with the first part's whole block inside a fenced block parses to the remaining parts only (so this case makes a spec of one real part plus one fenced part: result has one part); a CRLF copy of the valid spec parses to an equal result; a label's text may span two lines and is joined with `\n` and trimmed.
- `parseStubLine`: `Part 2 of 3 in #92` parses; with a leading BOM, with `\r\n`, and with trailing spaces still parses; `Part 0 of 3 in #92`, `Part 2 of 3 of #92`, `part 2 of 3 in #92`, `Part 2 of 3 in #092` and an empty body are `no-stub-line`; only the first line counts (a valid stub line on line two is `no-stub-line`).
- `stubBody`: for the valid spec, part 2 with `{ "1": 91 }` returns `title` equal to part 2's title and a body whose lines are exactly: `Part 2 of 3 in #92`, blank, the scope text, blank, `Depends on: #91`, blank, `Done when: ` plus the done text, blank, `This part is not defined yet. Run /develop <this issue> to define it.`; a part with `Depends on: none` has the line `Depends on: nothing`; two dependencies render `Depends on: #91, #92`; a dependency absent from the map is `missing-dependency`; a part number not in the spec is `no-such-part`; Review Focus 4: a scope containing a line that starts with `tr-develop/` (also after leading spaces) or the text `<!-- tr-develop:ac-begin -->` is `marker-in-text`.
- `parseParentLine`: the line `Parent: #92, Part 2 (docs/superpowers/specs/2026-11-15-x-design.md)` as the first line, after a leading `# Title` heading and blank lines, and as a CRLF file all parse to `{ok:true, parent:92, part:2, spec:...}`; a spec path outside `docs/superpowers/specs/`, a missing part, and a `Parent:` line that is not the first non-empty line (or the first after a leading `# ` heading) are `no-parent-line`.
- CLI: `parts`, `stub`, `parent-line` print the JSON above for a file; `stub-body` writes the body file and prints `{ok:true,title}`; the `SUBCOMMANDS` array equals the old list plus the four new names.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/route.test.js`
Expected: FAIL (`parseParts` is not exported).

- [ ] **Step 3: Implement the four functions and CLI wiring in `route.js`**

`parseParts`: scan `outsideFences(specText)`. A candidate is a line starting `### Part `; it must match `^### Part ([1-9]\d*): (\S(?:.*\S)?)$` else `bad-title` (a title over 80 characters is `bad-title`). Candidate numbers must be exactly 1, 2, 3 in order else `not-sequential` (checked before `too-many`); more than 50 is `too-many`. A part's block is the lines up to the next line that starts with `#`. Inside a block, a line starting `Scope:`, `Depends on:` or `Done when:` starts that label; the text of a label is the rest of that line plus following lines up to the next label, trimmed; non-blank text before the first label, a repeated or missing label, or labels out of order is `bad-label`. Empty text is `empty-text`; over 1,500 characters is `text-too-long`. `Depends on:` text must be `none` or match `^Part [1-9]\d*(, Part [1-9]\d*)*$` with strictly ascending numbers each below `n`, else `bad-dependency`. `stubBody` builds the body from the parsed part using the exact line layout in Step 1, joins with `\n` and ends with one `\n`. `parseStubLine` takes the first line of the text (split on `\n`), strips a leading `\uFEFF` and `\r`, trims, and matches the Global Constraints regex. `parseParentLine`: first non-empty trimmed line; if it starts with `# ` take the next non-empty line; match `^Parent: #([1-9]\d{0,8}), Part ([1-9]\d?) \((docs/superpowers/specs/[A-Za-z0-9._-]+\.md)\)$`.

- [ ] **Step 4: Run the tests**

Run: `node --test skills/develop/scripts/route.test.js`
Expected: PASS.

- [ ] **Step 5: Name the subcommands in `handoff.md`**

In "Resume and routing", after "Counting", add a short paragraph "**Parent specs and stubs.**" that names `route.js parts <specFile>`, `route.js stub <bodyFile>`, `route.js stub-body <specFile> <part> <parent> <dependsFile> <outFile>` and `route.js parent-line <file>` with one sentence each (what each prints, the error names), pointing to the design spec section 2 for the grammar.

- [ ] **Step 6: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS (the coverage test finds every new subcommand named).

```bash
git add skills/develop/scripts/route.js skills/develop/scripts/route.test.js skills/develop/handoff.md
git commit -m "Parse parent spec breakdowns, stub lines and parent lines in route.js"
```

---

### Task 4: Position, concurrency and routing for the parent track

**Files:**
- Modify: `skills/develop/scripts/route.js`
- Modify: `skills/develop/scripts/route.test.js`
- Modify: `skills/develop/handoff.md` (goldens `position-cases`, `position-error-cases`, `live-cases`, `route-cases`)

**Interfaces:**
- Consumes: `SPEC_RE`, `SHA_RE`, `DEFINED_AT_RE` from `./checkpoints.js` (Task 2); `governingPin`, `latestProgress`, `countedCheckpoints` from `./counted.js`.
- Produces: `position(progress, counts)` returns the new value `'parent-status'` and handles the parent track; `route(input)` returns the new `route` values `'parent-status'` and `'terminal-pin'`; `isLive` ignores `spec-merged` as a heartbeat. No signature changes.

Implements spec §3 (resume position, routing order, concurrency) and §9.

- [ ] **Step 1: Write the failing golden cases and tests**

Add to `position-cases` (shape `name`, `progress`, `counts`, `expect`): parent `kickoff` is `finish`; parent `reapproved` is `finish`; parent `pr-open` is `after-the-pr`; parent `spec-merged` is `parent-status`; `closed` with no track is still `terminal` (already present: keep). Add to `position-error-cases`: parent `tasks`, parent `plan-approved`, parent `final-approved` (the early `final-approved` return must check the track first), and `spec-merged` with track `plan`.
Add to `live-cases` (read the existing cases for the shape): a lone fresh `spec-merged` is `false`; a `kickoff` (parent track) 2 hours old followed by a later `spec-merged` is `false`.
Add to `route-cases` (shape `input` with `comments`, `writers`, `pinValid`, `issueState`, `baseIsDefault`, `hasUncountedMarkers`; `expect`): a parent `defined`, `kickoff`, `pr-open`, `spec-merged` history on an open issue is `{"route":"parent-status","askClosed":false,"ignoredMarkers":false}`; the same with `issueState` `closed` has `askClosed` true; the same history with `pinValid` `false` is still `parent-status` (the route is chosen before pin validation); the same history but the pin line says `track=plan` is `terminal-pin`; a `spec-merged` whose `specBlob` is 39 hex is `terminal-pin`; one whose pin `definedAt` has an offset is `terminal-pin`; a history with a `spec-merged` and no `defined` at all is `terminal-pin`; a `defined` (plan track) followed by a `spec-merged` with `track=plan` makes `route` throw (add this one as a plain test, not a golden). Add plain tests: a parent-track `tasks` checkpoint as latest progress makes `route` throw `/parent/`; every pre-existing golden case is untouched.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/route.test.js`
Expected: FAIL.

- [ ] **Step 3: Implement in `route.js`**

- Add `'spec-merged'` to both `NOT_HEARTBEAT` and `ENDS_A_RUN`.
- `position`: keep the `closed` and `pr-open` early returns (they never read the track). Replace the `final-approved` early return by a check: when `fields.track === 'parent'` throw `new Error('final-approved is not valid on the parent track')`, otherwise return `'finish'`. After computing `fields`: when `fields.track === 'parent'`, return `'finish'` for `kickoff` and `reapproved`, `'parent-status'` for `spec-merged`, and throw for every other phase (`plan-approved`, `tasks`). When the track is not `parent` and the phase is `spec-merged`, throw.
- `route`: right after `progress` is computed, throw `new Error('plan-approved, tasks and final-approved are not valid on the parent track')` when `progress.fields.track === 'parent'` and its phase is one of those three. Before the `pr-open` return, add nothing; after the `closed` handling and before the `typeof pinValid` check, add: when `progress.phase === 'spec-merged'`, `const pin = governingPin(cps)`; the route is `'parent-status'` only when `pin !== null`, `pin.fields.track === 'parent'`, `SPEC_RE.test(progress.fields.spec)`, `SHA_RE.test(progress.fields.specBlob)` and `DEFINED_AT_RE.test(pin.fields.definedAt)`, with `askClosed` true when `issueState === 'closed'`; otherwise `'terminal-pin'` with `askClosed` false. `pinValid` is not read for `spec-merged`, and the `pinValid` type check is skipped for it.

- [ ] **Step 4: Run the tests**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add skills/develop/scripts/route.js skills/develop/scripts/route.test.js skills/develop/handoff.md
git commit -m "Route the parent track: position, concurrency and the parent-status route"
```

---

### Task 5: Prose for Define: size decision, the parent run and sub-tickets

**Files:**
- Modify: `skills/develop/ticket-standard.md`
- Modify: `skills/develop/define.md`
- Modify: `skills/develop/blockers.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Consumes: the script names and error names from Tasks 2 to 4.
- Produces: skill prose the model follows; `skill.test.js` assertions that pin the key rulings.

Implements spec §1, §2, §4, §8 (Define half), §10 and the Files list for these three documents.

- [ ] **Step 1: Write the failing assertions in `skill.test.js`**

Add tests in the style of the existing `define.md: names the hashes...` test: `ticket-standard.md` no longer contains the sentence `Never create a parent or an epic` and contains `parent track`; `define.md` contains `track=parent`, `Define it as a parent`, `route.js parts`, `route.js parent-line`, `GET repos/<owner>/<repo>/issues/<n>/parent` or the phrase `issues/<n>/parent`, `parentBlob`, and `Deviation from the parent design`; `blockers.md` mentions `parent spec` under blocker 2.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/skill.test.js`
Expected: FAIL on the new assertions only.

- [ ] **Step 3: Edit `ticket-standard.md` and `blockers.md`**

`ticket-standard.md`: "Is it one ticket" keeps INVEST; replace "Never create a parent or an epic" by the spec §4 rule (a parent only through the parent track, never a hand-made epic whose body tracks work); the Review grades table and the Markers section name the spec-bearing tracks (`plan` and `parent`) wherever they say plan track (a parent ticket also carries no engineering detail). `blockers.md`: blocker 2 gains "or a merged parent spec that cannot be read" (spec §6 step 2, §7 step 1).

- [ ] **Step 4: Edit `define.md`**

Add, each as its own short subsection with the spec section it implements: (a) in "Brainstorm", the size decision with the two choices (spec §4), and that nothing is created for a parent unless chosen; (b) a "Parent track" section after "Plan track" (spec §1, §4): the run is the plan track with the parent spec grammar of §2, the extra reviewer breakdown questions, the sign-off preview adding the parts list (number, title, dependencies), the pin `track=parent`, no sub-issues at Publish, and a parent is refused on an issue that already has a parent; (c) "Fetch", for an existing issue, the sub-ticket steps 1 to 4 of spec §8 (parent read with its status handling, trust the parent, dependencies and the ask, context), naming `route.js stub` and `route.js parts`; (d) the later-steps bullets of spec §8 (the `Parent:` line, `route.js parent-line`, the reviewer's Deviation section, keeping the stub's first line, `parent` and `parentBlob` in the pin and the sign-off preview, the small-track approach's first line). The claimant and eligibility selection lives in `deliver.md` (Task 6); `define.md` links to it.

- [ ] **Step 5: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/ticket-standard.md skills/develop/define.md skills/develop/blockers.md skills/develop/scripts/skill.test.js
git commit -m "Describe defining a parent and a sub-ticket in the develop skill"
```

---

### Task 6: Prose for Deliver: the parent track, merged close-out, status mode and sub-tickets

**Files:**
- Modify: `skills/develop/deliver.md`
- Modify: `skills/develop/handoff.md` (Trust and Checkpoints integrity-exemption sentences; Resume and routing position and route tables; "The issue after Define" pin table)
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Consumes: Tasks 2 to 4 (names and error values), Task 1's findings file (the working link-call form and the `state_reason` result).
- Produces: the Deliver prose and the updated exemption sentences.

Implements spec §3 (tables), §5 (ensure-stubs), §6, §7, §8 (Deliver half), §10 and §11.

- [ ] **Step 1: Write the failing assertions in `skill.test.js` and update the two exemption tests**

Update the two existing assertions about the exemption list (the Checkpoints regex `/Check integrity \(see Trust: Integrity\), except for a `blocked`, `defined`, `reapproved` or `closed`/` and the Trust regex `/except a `blocked`, `defined`, `reapproved`\s+or `closed`/`) to the new list `` `blocked`, `defined`, `reapproved`, `closed` or `spec-merged` `` (same shape, five names). Add: `deliver.md` contains the headings `Parent track` (between `Small track` and `Finish and close-out`) and `Status mode` (after `After the PR`), and the existing five-section order test still passes; `deliver.md` contains `spec-merged`, `ensure-stubs`, `Refs #`, `terminal-pin`, `parent-status`, `settled`, `Part of #`, `parentBlob`, `state=open`, `sub_issues`; `handoff.md` contains `spec-merged` in both the position table and the pin/progress tables.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/skill.test.js`
Expected: FAIL on the exemption regexes and the new assertions.

- [ ] **Step 3: Edit `deliver.md`**

- Conventions and Common steps: the exemption list gains `spec-merged` (the sentence that lists `blocked`, `defined`, `reapproved` and `closed`); "plan track" mentions that are about the spec, the spec guard or integrity say "spec-bearing tracks (plan and parent)" (spec §Terms, §1); the route step names `parent-status` and `terminal-pin` and says they are chosen before pin validation.
- New section "Parent track" after "Small track" (spec §6 steps 1 to 3): the spec-only diff check (`git diff --name-only origin/<base>...HEAD` lists exactly `<spec>`, else blocker 2), the PR (`Refs #<n>` always; a `Tracks #<original>` line becomes `Refs #<original>`; unchecked criteria; spec link; report that merging closes nothing), `pr-open` with `deferred=0`, and that no Docs step, Verify step, plan, tasks or final loop run and the profile's verify list is not read.
- "Finish and close-out" says in one sentence that the parent track uses its own section instead.
- "Merged close-out": a parent-track subsection with the four steps of spec §6 (fetch and pattern checks, `route.js parts` on the merged spec with blocker 2 at stage `finish`, `specBlob` and the `refs/pull/<pr>/head` fallback and the diff-exit rule, post `spec-merged` then ensure-stubs then memory).
- New section "Ensure-stubs" (spec §5) holding the single claimant selection used by Deliver, status mode and Define: list sub-issues with explicit `per_page=100&page=N`, the open-issue scan since `definedAt` with `state=open`, pages 1 to 5, eligibility (not a PR, `counted.js write-access`, `user.type` `User`), lowest number wins, adoption by linking, the create with no assignee and the link call in the form Task 1 found (`-F sub_issue_id=<id>`), failed create stops the run, no lock and duplicates reported, bodies from `route.js stub-body`.
- New section "Status mode" after "After the PR" (spec §7): steps 1 to 6, done versus settled, the blob comparison before ensure-stubs, the close offer and its ordering (close the issue, then post `closed`; a hand-closed parent posts only `closed`), the non-default-base per-part question, and the pin-pattern check with `terminal-pin` meaning a terminal report with no re-pin.
- Sub-ticket additions (spec §8, Deliver half): the parent-drift warning at the integrity step (read the `Parent:` line with `route.js parent-line`, recompute the blob, warn on a difference or an unreadable parent, never stop), the `Part of #<parent>` PR line, and the re-pin sentence.

- [ ] **Step 4: Edit `handoff.md`**

Update the two integrity-exemption sentences (Checkpoints "Posting" step and Trust "Integrity") to the five-name list; add the parent rows to the "Position" table and the `parent-status` and `terminal-pin` rules to the "Route" list in "Resume and routing"; add `parent` and `parentBlob` rows to the pin table in "The issue after Define" (sub-ticket only, optional).

- [ ] **Step 5: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/deliver.md skills/develop/handoff.md skills/develop/scripts/skill.test.js
git commit -m "Describe delivering a parent, ensure-stubs and status mode in the develop skill"
```

---

### Task 7: SKILL.md, tutorial and the user-facing documents

**Files:**
- Modify: `skills/develop/SKILL.md`
- Modify: `skills/develop/tutorial.md`
- Modify: `docs/develop.md`
- Modify: `AGENTS.md` (the "develop skill" section)
- Modify: `CHANGELOG.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Consumes: Tasks 5 and 6, and Task 1's findings.
- Produces: the routes list, the Memory snapshot list and the documentation, with the compatibility note of spec §11.

Implements spec §3 (routes), §11, Open risks and the Files list.

- [ ] **Step 1: Write the failing assertions in `skill.test.js`**

`SKILL.md` names `parent-status` and `terminal-pin` in "Issue entry" and `spec-merged` in "Memory"; `tutorial.md` mentions a parent; `docs/develop.md` mentions `parent` and says that parents need this version of the skill. Keep `tutorial.md` within its existing `maxLines` bound (read the `FILES` table in `skill.test.js`).

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/skill.test.js`
Expected: FAIL on the new assertions.

- [ ] **Step 3: Edit `SKILL.md` and `tutorial.md`**

`SKILL.md`: "Issue entry" routes list gains `parent-status` (latest counted progress `spec-merged`, chosen before pin validation; go to "Status mode" in `deliver.md`) and `terminal-pin` (terminal report, restore or un-minimize the pin, no re-pin offered); "Memory" says a snapshot also follows `spec-merged`. `tutorial.md`: one short paragraph on larger work (a parent, its spec PR, its sub-tickets).

- [ ] **Step 4: Edit `docs/develop.md`, `AGENTS.md`, `CHANGELOG.md`**

`docs/develop.md`: a section "Larger work: parent tickets and sub-tickets" (what a parent is, the flow in five lines, what status shows, the compatibility note from spec §11 including the minimize-the-wrong-`closed` recovery, the GitHub calls Task 1 verified and the one thing it did not). `AGENTS.md` develop section: the track, the phase, the routes and where the code is. `CHANGELOG.md`: an `### Added` entry under the unreleased heading (read the file for its convention).

- [ ] **Step 5: Run everything, then commit**

Run: `node --test skills/develop/scripts/*.test.js` then `npm run test:shim`
Expected: PASS for both.

```bash
git add skills/develop/SKILL.md skills/develop/tutorial.md docs/develop.md AGENTS.md CHANGELOG.md skills/develop/scripts/skill.test.js
git commit -m "Document parent tickets and sub-tickets for /develop"
```
