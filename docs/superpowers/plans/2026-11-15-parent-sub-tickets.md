# Parent tickets and sub-tickets for `/develop` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `parent` track and sub-ticket awareness to the `/develop` skill, as the spec describes, without changing the behaviour of any ticket that has no parent.

**Architecture:** The skill is Markdown prose plus zero-dependency Node scripts under `skills/develop/scripts/`. The grammar and routing live in `checkpoints.js` and `route.js`; their test fixtures (goldens) are fenced JSON blocks in `handoff.md`, which `route.test.js`, `checkpoints.test.js`, `counted.test.js` and `skill.test.js` read. New behaviour is added script-first (tests first), then the prose that tells the model to use it, then the user-facing documents. The parent half (Tasks 2 to 6) lands before the sub-ticket half (Tasks 7 and 8), so the person can ask at sign-off for the work to be split there.

**Tech Stack:** Node 20 ES modules, `node:test`, `node:assert/strict`. No new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-15-parent-sub-tickets-design.md` (read-only: the spec guard runs before every push). The plan cites it as "spec §N". Executors read both.

## Global Constraints

- All scripts keep the existing conventions: zero dependencies, ESM, `node <script> <subcommand> <file>`, one JSON object on stdout, failure `{"ok":false,"error":...}` with exit 1, text only through files, `SUBCOMMANDS` exported by every CLI.
- No existing assertion in any test or golden is weakened or deleted. A test tied to prose or arrays that this plan changes (the integrity-exemption regexes and the `PHASES` array in tests) is edited in place in the same task as the change.
- `track` values: `plan`, `small`, `parent`. Phases: the nine existing ones plus `spec-merged` (parent track only).
- New fields and patterns: `parent` `^[1-9]\d{0,8}$`; `parentBlob` and `specBlob` 40 lowercase hex; `definedAt` exactly `^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$`.
- Parent spec breakdown grammar (spec §2): headings `### Part <n>: <title>`, n from 1 with no gap, repeat or leading zero, 1 to 50 parts, title 1 to 80 characters with no leading or trailing space; labels `Scope:`, `Depends on:`, `Done when:` exactly once each, in that order; each label's text non-empty and at most 1,500 characters; `Depends on:` is `none` or `Part <m>` entries joined by `, `, ascending, no repeat, each `m` lower than `n`.
- Stub first line: `^Part ([1-9]\d?) of ([1-9]\d?) in #([1-9]\d{0,8})$` after removing a BOM and `\r` and trimming.
- Plan decisions the spec leaves open (record them in the PR body): `parseParts` reports a text over 1,500 characters as `text-too-long`; `stub-body` also prints the part `title`, and reports `no-such-part` and `missing-dependency`; `route` also throws for a `spec-merged` on a non-parent track (as `position` does).
- Commits: plain one-line sentences like the repository's history, no trailers of any kind, no AI attribution. Never merge, rebase or force-push.
- Run every `gh` call through `node skills/develop/scripts/ghpin.js run <jsonFile>` with login `strvmarv`. Do not run `gh auth switch`.
- Verification command for the skill after every task: `node --test skills/develop/scripts/*.test.js` (all pass; `skill.test.js` has `COMPLETE = true`, so every new subcommand must be named in a skill Markdown file as `<script>.js <subcommand>`). The repository's profile verify list (including `npm run test:shim`) runs at Finish; Task 9 runs the Node part of it.

## Review Focus

Inputs the spec implies that no single task's tests would otherwise exercise; each line is pinned by a test in the task named.

1. A parent spec whose breakdown headings appear inside a fenced code block (the spec's own example does): they are not parts. Task 3.
2. A parent spec saved with CRLF line endings: `parts`, `stub` and `parent-line` read it like LF. Tasks 3 and 7.
3. A stub body that GitHub's web editor saved with a BOM or CRLF, or whose first line has trailing spaces: still a stub line. Task 3.
4. Part text containing a line that starts with `tr-develop/` or a `<!-- tr-develop:` marker: `stub-body` refuses it. Task 3.
5. A part's Scope text with a continuation line that starts with `#92` (not a heading): the text is not truncated. Task 3.
6. A parent whose latest progress is `spec-merged` while the pin is missing, a `plan` pin, or has a bad `spec`, `specBlob` or `definedAt`: route `terminal-pin`, never `repin-or-stop`. Task 4.
7. A fresh `spec-merged` alone does not make the next `/develop` think a run is live. Task 4.
8. Every pre-existing pin and checkpoint (no `track=parent`, no `parent`) validates and routes exactly as before: the existing golden cases still pass unchanged. Tasks 2, 4 and 7.

## "Plan track" sentence map

The spec says the implementation names the spec-bearing tracks (plan and parent) wherever the prose says "plan track" for the spec, the spec guard or integrity. Line numbers are in the files as they are at the start of the plan; find them by the quoted words. Tasks 6 and 8 apply this map.

| File and place | Change |
|---|---|
| `define.md` intro "agree a spec (plan track)" | add "or the parent track" |
| `define.md` "a project with a written design: the plan track" list | add the parent bullet (Task 6) |
| `define.md` Draft the ticket "Plan track: no engineering detail" | "Plan and parent tracks" |
| `define.md` Sign-off "Plan track: `approvedSha` is the worktree's `HEAD`"; the `defined` fields "on the plan track `spec` and `approvedSha`"; Publish step 2 "Branch (plan track)"; step 5 summary link and the `reachable` build "(plan track: probe ...)"; Wrap-up "On the plan track the pushed branch stays" | "plan and parent tracks" |
| `define.md` the "move it to the plan track" sentences about an over-long approach | unchanged (plan only) |
| `deliver.md` integrity "plan track: `git diff --quiet <approvedSha> ...`"; workspace "Start, plan track"; kickoff spec guard "(plan track: ...)"; Rules "the spec guard before every push, plan track" | "spec-bearing tracks" |
| `deliver.md` the line that sends a start to "step 1 of the Plan track or the Small track" | add "or the Parent track" |
| `deliver.md` `route.js tasks-for` and `count-tasks` bullets, the implementer-prompt "spec-is-read-only rule (plan track)", Finish step 3 "spec guard (plan track)", Finish step 4 spec link | unchanged (plan only: a parent has no tasks or implementer, and its own section states its guard and link) |
| `ticket-standard.md` "Leave the mechanism to the spec (plan track)", the Review-grades intro "plan track's spec loop", the grades row "in a plan-track ticket" | "plan or parent track" |
| `handoff.md` "The `defined` comment" summary "On the plan track the summary links the spec"; Trust "Integrity" bullet "Plan track: `git diff --quiet ...`"; Trust "The spec guard (plan track)"; Re-approval step 1 "The spec (plan track)", step 3 "plan track: the spec's content ...", step 6 "`approvedSha` (plan track ...)" | "spec-bearing tracks" |
| `handoff.md` Re-approval "Plan track with a `planSha`: re-run the plan loop", "Plan track with no `planSha`", "A change to context text only, on the plan track" | keep, and add one sentence: a parent has no `planSha`, so its re-approval runs no loop and its position is `finish` |
| `handoff.md` "Required fields" "plan:" key, Position table rows, `tasksDone` row | keep; add the "parent:" prefix and the parent rows (Tasks 2 and 6) |
| `handoff.md` Concurrency paragraph (heartbeat list, `ends a run` list), the `progress` enumeration, the `pinValid` sentence "required except when the latest progress is `pr-open` or `closed`", the After-the-PR "Merged" bullet ("post the `closed` checkpoint") | add `spec-merged` (the `pinValid` sentence also names `spec-merged`; the Merged bullet says the parent track posts `spec-merged` and runs ensure-stubs instead) (Task 6) |
| `review-contract.md` "On the plan track code fixes go to an implementer" | unchanged |

---

### Task 1: Probe the sub-issue calls in a throwaway repository

**Files:**
- Create: `docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a findings file that Tasks 5, 6 and 9 cite. It records, each with the observed HTTP status and the relevant response fields: (a) the working form of the link call (`POST repos/<o>/<r>/issues/<parent>/sub_issues` with `-F sub_issue_id=<numeric id>`) and what the string form `-f` does; (b) what linking the same issue twice returns; (c) whether creating and linking five issues in a row hits a rate limit; (d) the `state_reason` of an issue closed by merging a PR whose body says `Closes #<n>` into the default branch; (e) GitHub's documented per-parent sub-issue limit if it can be read (it is not probed).

This task is the one outward-facing action in the plan. The person approved it on 2026-11-15 in the session that defined this ticket: one private repository `strvmarv/develop-probe`, archived at the end of Task 10 (the token has no delete scope, so the person deletes it later), and nothing else on GitHub touched. Tasks 2 to 4 do not depend on Task 1.

- [ ] **Step 0: Confirm the approval**

If this session already holds the person's yes to exactly this action, go on. Otherwise ask with the ask tool (name the repository, the calls, and that it is archived and not deleted) and stop Task 1 on anything but a yes: then skip Steps 1 to 6, make Tasks 5, 6, 9 and 10 describe the link call as unverified (and Task 10 reduces to its local checks), and say so in the PR body.

- [ ] **Step 1: Create the repository**

`POST user/repos` with `-f name=develop-probe -F private=true -F auto_init=true`, through `ghpin.js run` with an `out` file. Expected: HTTP 201, `full_name` `strvmarv/develop-probe`, `default_branch` `main`.

- [ ] **Step 2: Create one parent issue and three child issues; link two children**

Create the four issues with `-F title=@<file>`; read each response's `number` and numeric `id`. Link child 1 with `-F sub_issue_id=<id>` and child 2 with `-f sub_issue_id=<id>` (the string form). Record both statuses and response bodies. Expected: the integer form returns 201 with the parent issue's JSON; record whatever the string form does.

- [ ] **Step 3: Link twice, then five in a row**

Link child 1 again (record the status and message). Create five more issues and link each immediately with no pause; record every status and any `Retry-After` or rate-limit message.

- [ ] **Step 4: Probe `state_reason` after a `Closes` merge**

Create a branch (`POST repos/strvmarv/develop-probe/git/refs` with `ref=refs/heads/probe` and the `sha` of `main`), add one file on it (`PUT repos/strvmarv/develop-probe/contents/probe.txt` with `-f message=...`, `-f branch=probe`, `-f content=<base64 of one line>`), open a PR whose body is `Closes #<child 3>`, merge it (`PUT repos/strvmarv/develop-probe/pulls/<n>/merge`), then read the child. Record `state` and `state_reason`. Expected: `closed` and `completed`.

- [ ] **Step 5: Write the findings file**

Write `docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md` with a heading per finding (a) to (e), the observed status and the exact working command form for each. Put a "Differs from the spec" section first (an empty one reads "None"). If a result contradicts a spec assumption (for example `state_reason` is not `completed`, which the definition of "done" relies on), stop the plan, report it to the person and wait: the spec is read-only and the person decides.

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md
git commit -m "Record what the sub-issue calls do in a throwaway repository"
```

---

### Task 2: Checkpoint grammar for the parent track and `spec-merged`

**Files:**
- Modify: `skills/develop/scripts/checkpoints.js`
- Modify: `skills/develop/scripts/checkpoints.test.js`
- Modify: `skills/develop/scripts/counted.test.js`
- Modify: `skills/develop/handoff.md` (the `parse-cases` and `validate-cases` goldens and the Checkpoints tables)

**Interfaces:**
- Consumes: the existing `PHASES`, `PROGRESS_PHASES`, `LINE_RE`, `TRACKS`, `REQUIRED`, `KEY_ORDER`, `valueOk`, `validateCheckpoint`, `requiredFields` in `checkpoints.js`.
- Produces (later tasks rely on these names): exported `DEFINED_AT_RE`, `SPEC_RE`, `SHA_RE`; `PHASES` is `['defined','reapproved','kickoff','plan-approved','tasks','final-approved','pr-open','spec-merged','blocked','closed']` and `PROGRESS_PHASES` is `['kickoff','plan-approved','tasks','final-approved','pr-open','spec-merged','closed','reapproved']`; `requiredFields(phase, track)` accepts track `'parent'`.

Implements spec §3 (phase, `specBlob`, required fields) and §9 (`checkpoints.js`). The `parent` and `parentBlob` fields are Task 7.

- [ ] **Step 1: Write the failing tests and golden cases**

Edit in place the existing `PHASES and PROGRESS_PHASES are the spec lists` test in `checkpoints.test.js` to the two arrays above. Add `validate-cases` golden cases (existing shape: `name`, `cp`, `ctx`, `reachable`, `expect`): `spec-merged parent track` valid, with `head` not in `reachable` (the exemption), `pr` in this repository, `spec`, `specBlob`; `spec-merged missing specBlob` (`missing`/`specBlob`); `spec-merged specBlob 39 hex` (`bad-value`/`specBlob`); `spec-merged track plan` (`bad-value`/`track`); `defined parent track` valid (with `spec`, `approvedSha`, reachable); `kickoff parent track` valid; `pr-open parent track` valid with `deferred` `0` and no `plan`, `planSha` or `tasksDone`; `pr-open parent track deferred 2` (`bad-value`/`deferred`); `reapproved parent track` valid with no plan fields; `tasks parent track`, `plan-approved parent track`, `final-approved parent track` each `{"ok":false,"error":"bad-value","key":"track"}`; `definedAt with an offset` (`2026-11-10T12:34:56+01:00`, `bad-value`/`definedAt`). Add one `parse-cases` entry that parses a `spec-merged` line. In `checkpoints.test.js` add: `requiredFields('spec-merged','parent')` equals `['track','branch','head','gh','pr','spec','specBlob']`; `requiredFields('pr-open','parent')` contains `spec` and `approvedSha` and none of `plan`, `planSha`, `tasksDone`. In `counted.test.js` add a case in the style of the existing `latestProgress`/`select-cases` tests showing that a counted `spec-merged` after a `pr-open` is the `progress` returned by `selectState`.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/checkpoints.test.js skills/develop/scripts/counted.test.js`
Expected: FAIL (unknown phase `spec-merged`, unknown track `parent`).

- [ ] **Step 3: Implement in `checkpoints.js`**

- Add `'spec-merged'` to `PHASES` (before `blocked`), to `PROGRESS_PHASES` (before `closed`) and to the phase alternation in `LINE_RE`; add `'parent'` to `TRACKS`.
- Add `DEFINED_AT_RE` and export it with `SPEC_RE` and `SHA_RE`.
- `REQUIRED` entries gain a `parent` array: `defined`, `reapproved`, `kickoff`, `pr-open` need `['spec','approvedSha']`; add `'spec-merged': { all: ['track','branch','head','gh','pr','spec','specBlob'], plan: [], small: [], parent: [] }`. `requiredFields` returns `entry.all` plus `entry.parent` for track `'parent'`; `pr-open`'s plan list never applies to `parent`.
- `valueOk` gains cases `specBlob` (`SHA_RE`) and `definedAt` (`DEFINED_AT_RE`); `head` is also exempt from reachability when `phase === 'spec-merged'`.
- `validateCheckpoint`, after the required-field loop: track `parent` is `bad-value`/`track` on `plan-approved`, `tasks` and `final-approved`; `spec-merged` with any track other than `parent` is `bad-value`/`track`; on track `parent`, `pr-open` with a `deferred` other than `'0'` is `bad-value`/`deferred`.
- `KEY_ORDER` gains `'specBlob'` right after `'approvedSha'`.

- [ ] **Step 4: Run the tests**

Run: `node --test skills/develop/scripts/checkpoints.test.js skills/develop/scripts/counted.test.js`
Expected: PASS, including every pre-existing golden case unchanged.

- [ ] **Step 5: Update the Checkpoints prose tables in `handoff.md`**

In "Checkpoints": the line grammar alternation, the "Patterns" paragraph (`specBlob`, `definedAt`, `track` is `plan`, `small` or `parent`), the "Required fields" table (a `spec-merged` row and a "parent:" prefix like "plan:" and "small:" for `kickoff`, `pr-open`, `reapproved`), the "What the fields mean" table (`specBlob`), the Reachability paragraph (`spec-merged`'s `head` follows the `closed` exemption). Leave the integrity-exemption sentences (Task 6).

- [ ] **Step 6: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/scripts/checkpoints.js skills/develop/scripts/checkpoints.test.js skills/develop/scripts/counted.test.js skills/develop/handoff.md
git commit -m "Teach the checkpoint grammar the parent track and the spec-merged phase"
```

---

### Task 3: Breakdown and stub parsing in `route.js`

**Files:**
- Modify: `skills/develop/scripts/route.js`
- Modify: `skills/develop/scripts/route.test.js`
- Modify: `skills/develop/handoff.md` (one paragraph in "Resume and routing")

**Interfaces:**
- Consumes: `outsideFences` from `./regions.js` (yields `{n, line}` for each line outside code fences; CRLF read as LF).
- Produces, exported from `route.js` and appended to `SUBCOMMANDS` after the existing names, in this order: `'parts'`, `'stub'`, `'stub-body'`.
  - `parseParts(specText: string): {ok: true, parts: Array<{n: number, title: string, scope: string, doneWhen: string, dependsOn: number[]}>} | {ok: false, error: 'no-parts'|'not-sequential'|'too-many'|'bad-title'|'bad-label'|'empty-text'|'text-too-long'|'bad-dependency'}`
  - `parseStubLine(bodyText: string): {ok: true, part: number, count: number, parent: number} | {ok: false, error: 'no-stub-line'}`
  - `stubBody(specText: string, part: number, parent: number, depends: Record<string, number>): {ok: true, title: string, body: string} | {ok: false, error: string}` where the error is a `parseParts` error, `'no-such-part'`, `'missing-dependency'` or `'marker-in-text'`.
  - CLI forms: `parts <specFile>`, `stub <bodyFile>`, `stub-body <specFile> <part> <parent> <dependsFile> <outFile>` (writes `body` to `outFile`, prints `{ok, title}`).

Implements spec §2, §5 and §9. The sub-ticket `parent-line` function is Task 7.

- [ ] **Step 1: Write the failing tests in `route.test.js`**

Use a small helper `part(n, {title, scope, depends, done})` that builds one breakdown block, and a valid three-part spec (`dependsOn` `[]`, `[1]`, `[1,2]`).
- `parseParts`: the valid spec returns the three parts; no heading is `no-parts`; headings 1 and 3 is `not-sequential`; `### Part 02:` is `bad-title`; 51 parts is `too-many`; a title of 81 characters and a title with a trailing space are `bad-title`; a part missing `Done when:`, with `Scope:` twice, with labels out of order, or with non-blank text before the first label is `bad-label`; an empty `Scope:` text is `empty-text`; 1,501 characters is `text-too-long`; `Depends on: Part 2` in part 2, `Part 1, Part 1`, `Part 2, Part 1`, `part 1` and `Part 1,Part 2` are each `bad-dependency`; `Depends on: none` gives `[]`.
- Review Focus 1, 2, 5: a spec with one real part and a second part wholly inside a fenced block parses to one part; a CRLF copy of the valid spec parses to an equal result; a label's text spanning two lines is joined with `\n` and trimmed; a Scope continuation line `#92 shipped the link call` is kept in the text and does not end the block, while `## Next section` does.
- `parseStubLine`: `Part 2 of 3 in #92` parses; with a leading BOM, with `\r\n`, and with trailing spaces still parses; `Part 0 of 3 in #92`, `Part 2 of 3 of #92`, `part 2 of 3 in #92`, `Part 2 of 3 in #092` and an empty body are `no-stub-line`; a valid line on line two is `no-stub-line`.
- `stubBody` for part 2 with `{ "1": 91 }` returns `title` equal to part 2's title and a body whose lines are exactly: `Part 2 of 3 in #92`, blank, the scope text, blank, `Depends on: #91`, blank, `Done when: ` plus the done text, blank, `This part is not defined yet. Run /develop <this issue> to define it.`; a part with `Depends on: none` has `Depends on: nothing`; two dependencies render `Depends on: #91, #92`; a missing map entry is `missing-dependency`; an unknown part is `no-such-part`; Review Focus 4: a scope with a line whose trimmed text starts with `tr-develop/`, or containing `<!-- tr-develop:`, is `marker-in-text`.
- CLI: `parts` and `stub` print the JSON above for a file; `stub-body` writes the body and prints `{ok:true,title}`; `SUBCOMMANDS` equals the old list plus the three names.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/route.test.js`
Expected: FAIL (`parseParts` is not exported).

- [ ] **Step 3: Implement the three functions and CLI wiring in `route.js`**

`parseParts` scans `outsideFences(specText)`. A candidate is a line starting `### Part `; it must match `^### Part ([1-9]\d*): (\S(?:.*\S)?)$` with a title of at most 80 characters, else `bad-title`. Candidate numbers must be exactly 1, 2, 3 in order else `not-sequential` (checked before `too-many`); over 50 is `too-many`. A part's block runs to the next line that matches `^#{1,6}\s` (an ATX heading) or to the end. In a block a line starting `Scope:`, `Depends on:` or `Done when:` starts that label; a label's text is the rest of its line plus following lines up to the next label, trimmed; non-blank text before the first label, a repeated or missing label, or labels out of order is `bad-label`; empty text is `empty-text`; over 1,500 characters is `text-too-long`. `Depends on:` text must be `none` or match `^Part [1-9]\d*(, Part [1-9]\d*)*$` with strictly ascending numbers each below `n`, else `bad-dependency`. `stubBody` returns `marker-in-text` when any line of the scope or done text, trimmed, starts with `tr-develop/`, or when either text contains `<!-- tr-develop:`; otherwise it builds the body from the exact layout in Step 1, joined with `\n` and ending in one `\n`. `parseStubLine` takes the first line (split on `\n`), strips a leading `\uFEFF` and `\r`, trims, and matches the Global Constraints regex.

- [ ] **Step 4: Run the tests**

Run: `node --test skills/develop/scripts/route.test.js`
Expected: PASS.

- [ ] **Step 5: Name the subcommands in `handoff.md`**

In "Resume and routing", after "Counting", add a paragraph "**Parent specs and stubs.**" naming `route.js parts <specFile>`, `route.js stub <bodyFile>` and `route.js stub-body <specFile> <part> <parent> <dependsFile> <outFile>`, one sentence each (what it prints and its error names), pointing to the design spec section 2 for the grammar.

- [ ] **Step 6: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/scripts/route.js skills/develop/scripts/route.test.js skills/develop/handoff.md
git commit -m "Parse parent spec breakdowns and stub lines in route.js"
```

---

### Task 4: Position, concurrency and routing for the parent track

**Files:**
- Modify: `skills/develop/scripts/route.js`
- Modify: `skills/develop/scripts/route.test.js`
- Modify: `skills/develop/handoff.md` (goldens `position-cases`, `position-error-cases`, `live-cases`, `route-cases`)

**Interfaces:**
- Consumes: `SPEC_RE`, `SHA_RE`, `DEFINED_AT_RE` from `./checkpoints.js` (Task 2); `governingPin`, `latestProgress`, `countedCheckpoints` from `./counted.js`.
- Produces: `position(progress, counts)` returns the new value `'parent-status'` and handles the parent track; `route(input)` returns the new `route` values `'parent-status'` and `'terminal-pin'`; `isLive` does not treat `spec-merged` as a heartbeat. No signature changes.

Implements spec §3 (resume position, routing order, concurrency) and §9.

- [ ] **Step 1: Write the failing golden cases and tests**

`position-cases` (shape `name`, `progress`, `counts`, `expect`): parent `kickoff` is `finish`; parent `reapproved` is `finish`; parent `pr-open` is `after-the-pr`; parent `spec-merged` is `parent-status`. `position-error-cases`: parent `tasks`, parent `plan-approved`, parent `final-approved` (the early `final-approved` return must check the track first), and `spec-merged` with track `plan`. `live-cases` (read the existing cases for the shape): a lone fresh `spec-merged` is `false`; a parent `kickoff` two hours old followed by a later `spec-merged` is `false`. `route-cases` (shape `input`, `expect`): a parent `defined`, `kickoff`, `pr-open`, `spec-merged` history on an open issue is `{"route":"parent-status","askClosed":false,"ignoredMarkers":false}`; the same with `issueState` `closed` has `askClosed` true; the same with `pinValid` `false` is still `parent-status`; the same with the pin line `track=plan` is `terminal-pin`; a `spec-merged` whose `specBlob` is 39 hex is `terminal-pin`; one whose pin `definedAt` has an offset is `terminal-pin`; a history with `spec-merged` and no `defined` is `terminal-pin`. Plain tests: a parent-track `tasks` as latest progress makes `route` throw `/parent/`; a `spec-merged` with `track=plan` as latest progress makes `route` throw. Every pre-existing golden case is untouched.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/route.test.js`
Expected: FAIL.

- [ ] **Step 3: Implement in `route.js`**

- Add `'spec-merged'` to both `NOT_HEARTBEAT` and `ENDS_A_RUN`.
- `position`: keep the `closed` and `pr-open` early returns (they never read the track). Replace the `final-approved` early return by: throw `new Error('final-approved is not valid on the parent track')` when `fields.track === 'parent'`, otherwise return `'finish'`. After `fields` is computed: on track `parent`, return `'finish'` for `kickoff` and `reapproved`, `'parent-status'` for `spec-merged`, and throw for every other phase; on any other track, `spec-merged` throws.
- `route`: right after `progress` is computed, throw when `progress.fields.track === 'parent'` and the phase is `plan-approved`, `tasks` or `final-approved`, and when `progress.phase === 'spec-merged'` and `progress.fields.track !== 'parent'`. After the `closed` handling and before the `typeof pinValid` check: when `progress.phase === 'spec-merged'`, `const pin = governingPin(cps)`; the route is `'parent-status'` only when `pin !== null`, `pin.fields.track === 'parent'`, `SPEC_RE.test(progress.fields.spec)`, `SHA_RE.test(progress.fields.specBlob)` and `DEFINED_AT_RE.test(pin.fields.definedAt)`, with `askClosed` true when `issueState === 'closed'`; otherwise `'terminal-pin'` with `askClosed` false. `pinValid` is not read for `spec-merged`.

- [ ] **Step 4: Run the tests**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add skills/develop/scripts/route.js skills/develop/scripts/route.test.js skills/develop/handoff.md
git commit -m "Route the parent track: position, concurrency and the parent-status route"
```

---

### Task 5: The new GitHub calls in the Trust fetch-command table

**Files:**
- Modify: `skills/develop/handoff.md` (the `fetch-commands` golden and the Trust "Fetch commands" prose)
- Modify: `skills/develop/scripts/counted.test.js`

**Interfaces:**
- Consumes: Task 1's findings for the working link-call form.
- Produces: `fetch-commands` entries (appended after `renames`, in this order): `parent-read` `["api","repos/<owner>/<repo>/issues/<n>/parent"]`; `sub-issues` `["api","repos/<owner>/<repo>/issues/<parent>/sub_issues?per_page=100&page=<N>"]`; `issue-scan` `["api","repos/<owner>/<repo>/issues?state=open&since=<definedAt>&per_page=100&page=<N>"]`; `stub-create` `["api","repos/<owner>/<repo>/issues","-F","title=@<titleFile>","-F","body=@<bodyFile>"]`; `sub-issue-link` `["api","repos/<owner>/<repo>/issues/<parent>/sub_issues","-F","sub_issue_id=<id>"]`; `issue-close` `["api","-X","PATCH","repos/<owner>/<repo>/issues/<n>","-F","state=closed","-F","state_reason=completed"]`.

Implements spec §Testing (the fetch-command golden) and §5, §7.

- [ ] **Step 1: Write the failing tests in `counted.test.js`**

Edit in place the `fetch-commands: the expected names` test to the old ten names followed by the six new ones; keep the "--paginate for exactly comments, edits and renames" test (the new calls use explicit pages and no `--paginate`); add tests: `sub-issue-link` types `sub_issue_id` with `-F` (the argument before `sub_issue_id=<id>` is `-F`); `sub-issues` and `issue-scan` carry `per_page=100` and `page=<N>`; `issue-scan` has `state=open`; `stub-create` sends title and body through `-F ...=@file`; no new call has a `--jq` filter (the one-filter test still passes).

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/counted.test.js`
Expected: FAIL (names differ).

- [ ] **Step 3: Add the six entries to the golden and the Trust prose**

Append the entries to `golden:fetch-commands` in `handoff.md` in the order above. In the Trust "Fetch commands" prose add one sentence per new call naming where it is used (Define Fetch, Ensure-stubs, Status mode) and that the page number is substituted by the caller for `<N>`.

- [ ] **Step 4: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/handoff.md skills/develop/scripts/counted.test.js
git commit -m "Add the sub-issue and issue-scan calls to the Trust fetch-command table"
```

---

### Task 6: Prose for the parent track

**Files:**
- Modify: `skills/develop/ticket-standard.md`, `define.md`, `deliver.md`, `blockers.md`, `handoff.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Consumes: the script and error names of Tasks 2 to 5; Task 1's findings (the working link-call form).
- Produces: the model's instructions for the parent half, and updated exemption assertions.

Implements spec §1, §2, §4, §5, §6, §7, §10, §11 and the "Plan track" sentence map (parent half).

- [ ] **Step 1: Write the failing assertions in `skill.test.js` and update the two exemption tests**

Edit in place the two existing exemption assertions (the Checkpoints regex ``/Check integrity \(see Trust: Integrity\), except for a `blocked`, `defined`, `reapproved` or `closed`/`` and the Trust regex ``/except a `blocked`, `defined`, `reapproved`\s+or `closed`/``) to the five-name list ``blocked`, `defined`, `reapproved`, `closed` or `spec-merged``. Add assertions: `ticket-standard.md` no longer contains `Never create a parent or an epic` and contains `parent track`; `define.md` contains `track=parent`, `Define it as a parent` and `Parent track`; `blockers.md` contains `parent spec`; `deliver.md` has the headings `Parent track` (between `Small track` and `Finish and close-out`), `Ensure-stubs` and `Status mode` (after `After the PR`), and the existing five-section order test still passes; `deliver.md` contains `spec-merged`, `Refs #`, `terminal-pin`, `parent-status`, `settled`, `state=open`, `sub_issues`, `route.js parts`, `route.js stub-body`, `route.js stub`; `handoff.md` contains `spec-merged` in its position table and its progress enumeration.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/skill.test.js`
Expected: FAIL on the exemption regexes and the new assertions.

- [ ] **Step 3: Edit `ticket-standard.md` and `blockers.md`**

`ticket-standard.md`: replace "Never create a parent or an epic" (in "Is it one ticket") by the spec §4 rule (a parent only through the parent track, never a hand-made epic whose body tracks work); apply the sentence map to its three "plan track" places. `blockers.md`: blocker 2 gains "or a merged parent spec that cannot be read".

- [ ] **Step 4: Edit `define.md` (parent half)**

Apply the sentence map to `define.md`. In "Brainstorm" add the size decision (spec §4: a change that fails INVEST offers "Define it as a parent" or "Define only the first piece", nothing created for a parent unless chosen) and the parent bullet in the track list. Add a "Parent track" section after "Plan track" (spec §1, §2, §4): the run is the plan track with the §2 grammar, the extra reviewer breakdown questions, the sign-off preview adding the parts list (number, title, dependencies), the pin `track=parent`, no sub-issues at Publish, a parent refused on an issue that already has a parent. In "Draft the ticket", the "One ticket?" bullet now says the parent track exists instead of "create no parent".

- [ ] **Step 5: Edit `deliver.md` (parent half)**

Apply the sentence map to `deliver.md`. Conventions and Common steps: the exemption list gains `spec-merged`; the route step names `parent-status` and `terminal-pin` and says both are chosen before pin validation (`terminal-pin` is a terminal report with no re-pin). New section "Parent track" after "Small track" (spec §6 steps 1 to 3). "Finish and close-out" says in one sentence that the parent track uses its own section. "Merged close-out" gains the parent-track subsection (spec §6 steps 1 to 4, including blocker 2 at stage `finish`, `specBlob`, the `refs/pull/<pr>/head` fallback, the diff-exit rule). New section "Ensure-stubs" (spec §5): list with explicit `per_page=100&page=N`, the `state=open` scan since `definedAt` (pages 1 to 5), eligibility (not a PR, `counted.js write-access`, `user.type` `User`), lowest eligible number wins, adoption by linking, creation with no assignee through `route.js stub-body` and `stub-create`, the link in the form Task 1 found (`-F sub_issue_id=<id>`), a failed create stops the run, no lock and duplicates reported, ineligible claimants reported. New section "Status mode" after "After the PR" (spec §7 steps 1 to 6, done versus settled, blob comparison before ensure-stubs, the close offer and its ordering with `issue-close`, the per-part question on a non-default base).

- [ ] **Step 6: Edit `handoff.md` (parent half)**

Apply the sentence map to `handoff.md`. The two integrity-exemption sentences (Checkpoints "Posting" step, Trust "Integrity") gain `spec-merged`; the Concurrency paragraph and the `progress` enumeration gain `spec-merged`; the `pinValid` sentence names `spec-merged` beside `pr-open` and `closed`; the Position table gains the parent rows (spec §3); the Route rules gain `parent-status` and `terminal-pin` before the pin check; the After-the-PR "Merged" bullet says the parent track posts `spec-merged` and runs ensure-stubs; Re-approval gains the one sentence that a parent has no `planSha`, runs no loop and has position `finish`.

- [ ] **Step 7: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/ticket-standard.md skills/develop/define.md skills/develop/deliver.md skills/develop/blockers.md skills/develop/handoff.md skills/develop/scripts/skill.test.js
git commit -m "Describe defining, delivering and checking a parent in the develop skill"
```

---

### Task 7: The `parent` and `parentBlob` pin fields and `parent-line`

**Files:**
- Modify: `skills/develop/scripts/checkpoints.js`, `checkpoints.test.js`
- Modify: `skills/develop/scripts/route.js`, `route.test.js`
- Modify: `skills/develop/handoff.md` (a `validate-cases` entries, the pin table, one paragraph naming `parent-line`)

**Interfaces:**
- Consumes: Tasks 2 and 3.
- Produces: `defined` and `reapproved` accept optional `parent` and `parentBlob` (a pair); `KEY_ORDER` writes them after `approvedSha` and before `specBlob`; exported `parseParentLine(fileText: string): {ok: true, parent: number, part: number, spec: string} | {ok: false, error: 'no-parent-line'}` and the CLI `parent-line <file>`, appended to `SUBCOMMANDS`.

Implements spec §3 (fields) and §8, §9 (`parent-line`).

- [ ] **Step 1: Write the failing tests and golden cases**

`validate-cases`: `defined plan track with parent and parentBlob` valid; `defined with parent but no parentBlob` (`missing`/`parentBlob`); `parent 0` (`bad-value`/`parent`); `parentBlob 41 hex` (`bad-value`/`parentBlob`); `kickoff carrying parent` (`bad-value`/`parent`: only `defined` and `reapproved` carry it); `defined parent track carrying parent` (`bad-value`/`parent`: one level only); `reapproved plan track with parent and parentBlob` valid. `checkpoints.test.js`: a `defined` with `parent` formats `... spec=... approvedSha=... parent=12 parentBlob=...`. `route.test.js` for `parseParentLine`: `Parent: #92, Part 2 (docs/superpowers/specs/2026-11-15-x-design.md)` as the first line, after a leading `# Title` heading and blank lines, and as a CRLF file all parse to `{ok:true,parent:92,part:2,spec:...}`; a spec path outside `docs/superpowers/specs/`, a missing part, and a `Parent:` line that is neither the first non-empty line nor the first after a leading `# ` heading are `no-parent-line`; the CLI prints the JSON; `SUBCOMMANDS` gains `'parent-line'` last.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/checkpoints.test.js skills/develop/scripts/route.test.js`
Expected: FAIL.

- [ ] **Step 3: Implement**

`checkpoints.js`: `valueOk` gains `parent` (`PR_NUMBER_RE`) and `parentBlob` (`SHA_RE`); `validateCheckpoint` requires `parent` and `parentBlob` together (`missing` for the absent one), allows them only on `defined` and `reapproved`, and rejects them on track `parent`; `KEY_ORDER` gains `'parent'`, `'parentBlob'` between `'approvedSha'` and `'specBlob'`. `route.js`: `parseParentLine` reads the first non-empty trimmed line, or the first after a leading `# ` heading, and matches `^Parent: #([1-9]\d{0,8}), Part ([1-9]\d?) \((docs/superpowers/specs/[A-Za-z0-9._-]+\.md)\)$`; wire `parent-line <file>`.

- [ ] **Step 4: Update `handoff.md`**

Add the `parent` and `parentBlob` rows to the "The `defined` comment" pin table (optional, sub-ticket only) and to the Checkpoints field tables, and add `route.js parent-line <file>` to the "Parent specs and stubs" paragraph.

- [ ] **Step 5: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/scripts/checkpoints.js skills/develop/scripts/checkpoints.test.js skills/develop/scripts/route.js skills/develop/scripts/route.test.js skills/develop/handoff.md
git commit -m "Record the parent and its spec version in a sub-ticket's pin"
```

---

### Task 8: Prose for sub-tickets

**Files:**
- Modify: `skills/develop/define.md`, `deliver.md`, `handoff.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Consumes: Tasks 5 and 7.
- Produces: the model's instructions for the sub-ticket half.

Implements spec §8 and §10 (sub-ticket parts) and the sentence map (Re-approval rows).

- [ ] **Step 1: Write the failing assertions in `skill.test.js`**

`define.md` contains the exact string `GET repos/<owner>/<repo>/issues/<n>/parent`, `parentBlob`, `route.js parent-line`, `Deviation from the parent design`, `Part <k> of <count> in #<parent>` and `define anyway`; `deliver.md` contains `Part of #` and `parent spec changed`; `handoff.md` Re-approval mentions `parentBlob`.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/skill.test.js`
Expected: FAIL on the new assertions only.

- [ ] **Step 3: Edit `define.md` (sub-ticket half)**

In "Fetch", for an existing issue only, add the four steps of spec §8: (1) find the parent with the `parent-read` call (404 means no link; any status other than 200 or 404 is blocker 3; a 404 with a stub first line from `route.js stub` asks standalone-or-stop), (2) trust the parent (counted `track=parent` pin and counted `spec-merged`, pin patterns only, first line agrees with the link, part exists via `route.js parts`; earlier than `spec-merged` stops with the PR link; `closed` or any mismatch asks standalone-or-stop), (3) dependencies (done versus not done through the Ensure-stubs selection in `deliver.md`, ask stop or "define anyway"), (4) context (parent spec and part from `origin/<base>`; at most 5 sibling summaries of at most 2,000 characters, counted comments, quoted data). In the later steps add the bullets of spec §8: the `Parent: #<parent>, Part <k> (<path>)` first line and `route.js parent-line`, the reviewer's "Deviation from the parent design" grading, keeping the stub's first line, `parent` and `parentBlob` (the blob from `git rev-parse origin/<base>:<parent spec>`) in the pin and the sign-off preview, and the small-track approach's first line counting toward 60 lines.

- [ ] **Step 4: Edit `deliver.md` and `handoff.md` (sub-ticket half)**

`deliver.md`: at the integrity step, the parent-drift warning (read the path with `route.js parent-line` from the spec or approach, recompute the blob at `origin/<base>`, warn on a difference or on an unreadable parent, never stop or re-approve); in "Finish and close-out" the PR body adds `Part of #<parent>` after `Closes #<n>`; Re-approval says a sub-ticket recomputes `parent` and `parentBlob` and shows the old and new blob, keeping the old fields with a warning if the recompute fails. `handoff.md`: Re-approval steps 1, 3 and 6 carry the same sentence about `parent` and `parentBlob`.

- [ ] **Step 5: Run the whole skill suite, then commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add skills/develop/define.md skills/develop/deliver.md skills/develop/handoff.md skills/develop/scripts/skill.test.js
git commit -m "Describe defining and delivering a sub-ticket in the develop skill"
```

---

### Task 9: SKILL.md, the tutorial and the user-facing documents

**Files:**
- Modify: `skills/develop/SKILL.md`, `tutorial.md`
- Modify: `docs/develop.md`, `AGENTS.md` (the "develop skill" section), `CHANGELOG.md`
- Modify: `skills/develop/scripts/skill.test.js`

**Interfaces:**
- Consumes: Tasks 6 and 8, and Task 1's findings.
- Produces: the routes list, the Memory snapshot list and the documentation with the compatibility note of spec §11.

Implements spec §3 (routes), §11, Open risks and the Files list.

- [ ] **Step 1: Write the failing assertions in `skill.test.js`**

`SKILL.md` names `parent-status` and `terminal-pin` in "Issue entry" and `spec-merged` in "Memory"; `tutorial.md` mentions a parent and stays within its `maxLines` bound (read the `FILES` table in `skill.test.js`); `docs/develop.md` contains `parent` and the sentence that parents need this version of the skill.

- [ ] **Step 2: Run to verify they fail**

Run: `node --test skills/develop/scripts/skill.test.js`
Expected: FAIL on the new assertions.

- [ ] **Step 3: Edit `SKILL.md` and `tutorial.md`**

`SKILL.md`: the "Issue entry" routes list gains `parent-status` (latest counted progress `spec-merged`, chosen before pin validation: go to "Status mode" in `deliver.md`) and `terminal-pin` (terminal report: restore or un-minimize the pin; no re-pin offered); "Memory" says a snapshot also follows `spec-merged`. `tutorial.md`: one short paragraph on larger work (a parent, its spec PR, its sub-tickets).

- [ ] **Step 4: Edit `docs/develop.md`, `AGENTS.md`, `CHANGELOG.md`**

`docs/develop.md`: a section "Larger work: parent tickets and sub-tickets" (what a parent is, the flow in five lines, what status shows, the compatibility note of spec §11 including the minimize-the-wrong-`closed` recovery, the calls Task 1 verified and the one thing not verified). `AGENTS.md` develop section: the track, the phase, the routes and where the code is. `CHANGELOG.md`: an `### Added` entry under the topmost unreleased heading (read the file's convention first).

- [ ] **Step 5: Run the Node part of the profile verify list, then commit**

Run: `node --test skills/develop/scripts/*.test.js` then `npm run test:shim`
Expected: PASS for both. (The .NET suite and the catalog guard run at Finish.)

```bash
git add skills/develop/SKILL.md skills/develop/tutorial.md docs/develop.md AGENTS.md CHANGELOG.md skills/develop/scripts/skill.test.js
git commit -m "Document parent tickets and sub-tickets for /develop"
```

---

### Task 10: Walk-through in the throwaway repository

**Files:**
- Modify: `docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md` (append a "Walk-through" section)

**Interfaces:**
- Consumes: Tasks 1, 3 and 6 (the `parts`, `stub-body` and `stub` scripts and the Ensure-stubs prose), and the probe repository from Task 1 (not yet archived).
- Produces: evidence that the documented ensure-stubs procedure works end to end against GitHub, and the archived probe repository.

Implements spec §Testing (the prose walk-through) and closes the open risks. If Task 1 was declined, this task only runs the local steps 1 and 2 and says the GitHub steps were not run.

- [ ] **Step 1: Run the scripts on a three-part spec**

Write a three-part parent spec file in the scratch directory (parts with `Depends on: none`, `Part 1`, `Part 1, Part 2`). Run `route.js parts` and check three parts. For each part in order run `route.js stub-body` with the dependency map built from the issue numbers created so far.

- [ ] **Step 2: Follow the Ensure-stubs section of `deliver.md` by hand in the probe repository**

Create a fresh parent issue, then perform Ensure-stubs exactly as `deliver.md` writes it (list, scan, create from the stub bodies with no assignee, link with `-F sub_issue_id=<id>`), through `ghpin.js run`. Check with `route.js stub` that each created body parses, and that the sub-issue list shows three linked issues in order.

- [ ] **Step 3: Run it again and file an impostor**

Run the procedure a second time: expect no new issue. Then file an issue with a stub line for part 2 from the same account (it has write access, so it is eligible): expect the lowest number to stay the stub and the new one to be reported. Record each outcome.

- [ ] **Step 4: Archive the probe repository and append the findings**

`PATCH repos/strvmarv/develop-probe` with `-F archived=true`. Append a "Walk-through" section to the findings file with what each step showed and any deviation from `deliver.md`; if a step failed to work as written, fix `deliver.md` (and its `skill.test.js` assertion if needed) in this task and say so.

- [ ] **Step 5: Run the whole skill suite and commit**

Run: `node --test skills/develop/scripts/*.test.js`
Expected: PASS.

```bash
git add docs/superpowers/plans/2026-11-15-parent-sub-tickets-probe.md skills/develop
git commit -m "Record the ensure-stubs walk-through in the probe repository"
```
