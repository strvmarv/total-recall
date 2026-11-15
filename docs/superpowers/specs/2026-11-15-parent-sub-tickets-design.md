# Parent tickets and sub-tickets for `/develop`

Status: design for review. Scope: the `develop` skill (`skills/develop/`), its scripts and tests, and its user documentation.

## Summary

`/develop` takes one GitHub issue to one pull request. That is the right unit for a change one person can hold in their head, and it is the wrong unit for larger work. Today the skill refuses the larger case: when a change is "not small or not independent" it says so, asks which piece to define first, defines only that one and "never creates a parent or an epic". People then run the larger case by hand. Issue #92 is the example in this repository: a hand-written tracking issue, native GitHub sub-issues, one shared design document that rides along with the first sub-ticket's pull request, and a checklist in the parent body that someone edits.

This design adds a third **track**, `parent`, so that larger work is a first-class shape:

- A **parent ticket** holds the goal, the shared decisions and an ordered breakdown into parts. It has its own spec (the *parent spec*) and is delivered as a small docs-only pull request that puts the spec on the base branch. It is never built, and it is closed only when every part is done.
- A **sub-ticket** is an ordinary `/develop` ticket (plan or small track) that is one part of a parent. Its spec is the implementation design for that part and is reviewed against the parent spec. Everything the skill does today for a ticket (pin, spec guard, plan, tasks, final loop, PR, checkpoints) is unchanged.

The parent spec says *what* and *in what pieces*; each sub-spec says *how*, written just in time so that it reflects what earlier parts actually shipped.

## Goals

1. Larger work can be defined once as a parent and then delivered part by part, with each part running through the existing Define and Deliver flow.
2. The parent spec is on the base branch before any part is defined, so every sub-ticket cites a merged document.
3. The parent never goes stale in the trust model: its body stays pinned and static, and live progress comes from GitHub's native sub-issue progress and from the sub-tickets' own checkpoints.
4. Every ticket without a parent behaves exactly as it does today.

## Non-goals

- Stacked branches or an integration branch. A part whose dependencies have not merged waits (or the person overrides after being asked). The base stays the default branch, so required CI still runs on every pull request.
- Parents of parents. One level only.
- Sub-issues in another repository.
- Amending a parent spec after its pull request has merged. That is an ordinary pull request to the document. Sub-tickets notice the change (Parent drift) but the skill has no flow to re-approve a merged parent.
- Editing or deleting stubs after they are created. Status reports a difference from the spec; the person acts on it.
- Using GitHub's "blocked by" issue relations. Dependencies live in the parent spec, which is committed and reviewed.
- Closing a parent without asking.

## Design

### 1. The `parent` track

`track` takes a third value, `parent`, beside `plan` and `small`. A parent ticket is defined and delivered like a plan-track ticket with these differences:

| | Plan track | Parent track |
|---|---|---|
| Spec | the implementation design | the parent spec (section 2) |
| Define reviewer checks | the spec and the ticket | the same, plus the breakdown (section 4) |
| Publish | issue, pin, branch push | the same, plus one stub per part (section 5) |
| Deliver builds | plan, tasks, final loop, verify | nothing: no plan, no tasks, no final loop, no verify, no docs step |
| Deliver opens | a PR that `Closes #n` | a docs-only PR that `Refs #n` (never `Closes`) |
| Checkpoints | kickoff, plan-approved, tasks, final-approved, pr-open, closed | kickoff, pr-open, spec-merged, closed |
| After the PR merges | `closed` | `spec-merged`, then status mode (section 7) |

The parent's pin carries `track=parent`, `branch`, `spec` and `approvedSha` exactly as a plan-track pin does, so the integrity check, the spec guard and re-approval work unchanged while the spec pull request is open.

The parent's body follows the ticket standard like any ticket: problem, outcome, acceptance criteria (the criteria for the whole goal), scope and context. The marked acceptance-criteria region is present and hashed. The body is never edited after sign-off, so it holds no progress list; the native sub-issue panel and status mode (section 7) show progress.

### 2. The parent spec

The parent spec is a committed file, `docs/superpowers/specs/YYYY-MM-DD-<slug>-design.md`, with these parts, in this order:

1. **Goal**, and what success means.
2. **Non-goals.**
3. **Shared decisions**: constraints and choices every part must follow (interfaces, naming, data shapes at the boundary, rollout rules). No implementation detail that belongs to one part.
4. **Breakdown**: the ordered parts.

The breakdown is machine-read. Each part is a level-3 heading and three labelled lines:

```
### Part 2: Direct delivery in pi and Claude Code
Scope: one short paragraph of what this part delivers and what it leaves to other parts.
Depends on: Part 1
Done when: one or two lines of rough, observable acceptance.
```

Rules, checked by a script (section 9) outside code fences:

- Headings are exactly `### Part <n>: <title>` with `n` = 1, 2, 3 ... in file order, no gap or repeat. The title is at most 80 characters.
- Each part has a `Depends on:` line: `none`, or a comma-separated list of `Part <m>` with every `m` lower than `n`. Order is therefore a valid build order and cycles are impossible.
- A spec with no part headings is not a parent spec.

The reviewer grades the breakdown: each part is independently shippable (INVEST), no part is a horizontal layer that ships nothing alone, the parts together cover every acceptance criterion of the parent, and the dependencies are real and minimal.

### 3. Pin and checkpoints

New and changed fields:

| Field | Where | Meaning |
|---|---|---|
| `track=parent` | pin, kickoff, pr-open, spec-merged, closed, reapproved | the third track |
| `parent` | `defined`, `reapproved` of a sub-ticket | the parent's issue number, `^[1-9]\d{0,8}$`; present with `parentBlob` or not at all |
| `parentBlob` | same | the git blob id (40 hex) of the parent spec at `origin/<base>` when the sub-ticket was signed off |
| `specBlob` | `spec-merged` | the blob id (40 hex) of the spec at `origin/<base>` after the merge |

New phase `spec-merged`, for the parent track only. Required: `branch`, `head`, `gh`, `pr`, `spec`, `specBlob`. It is a progress phase and, like `pr-open`, `blocked` and `closed`, ends a run for the concurrency check. Its `head` follows the `closed` rule (40 hex, not required to be reachable, because the branch may be deleted after the merge).

Required fields per phase for the parent track (the table in `handoff.md` gains a "parent:" prefix like the existing "plan:" and "small:" ones): `kickoff` needs `spec` and `approvedSha`; `pr-open` needs `spec` and `approvedSha` (no `plan`, `planSha`, `tasksDone`) and `deferred=0`; `reapproved` needs what `defined` needs plus `head`, `review`, `gh`, `deferred` (no plan fields); `closed` is unchanged. A parent never posts `plan-approved`, `tasks` or `final-approved`; one that appears on a parent track is a trust failure.

Resume position for the parent track:

| Progress | Position |
|---|---|
| none | `start` |
| `kickoff` or `reapproved` | `finish` (open or update the spec PR) |
| `pr-open` | `after-the-pr` |
| `spec-merged` | `parent-status` |
| `closed` | `terminal` |

The new issue route `parent-status` is chosen when the latest counted progress is `spec-merged`. The existing routing order is otherwise unchanged. A ticket whose pin has no `track=parent` takes every path it takes today.

### 4. Define a parent

Define reaches the size decision in Brainstorm (today it ends in "plan track" or "small track"). A change that fails INVEST (not small, not independent) now offers two choices, and Define asks which:

- **Define it as a parent** (this design), or
- **Define only the first piece** (today's behaviour: a single ticket and no parent).

Define never creates a parent unless the person picks it. `ticket-standard.md` changes from "never create a parent or an epic" to "create a parent only through the parent track; never a hand-made epic whose body tracks work".

The parent run follows the plan track step by step: rename the branch `feat/<slug>`, brainstorm writes the parent spec (section 2), commit, draft the ticket, run the spec loop (six rounds, review contract unchanged) with the extra breakdown questions of section 2, sign-off. At sign-off the preview adds the list of stubs that Publish will create: part number, title, dependencies.

### 5. Publish a parent: the stubs

After the `defined` comment is posted and read back (Publish step 5 and step 6 of `define.md`), Define creates one **stub** per part, in order, by an operation called ensure-stubs:

1. List the parent's current sub-issues (`GET repos/<o>/<r>/issues/<parent>/sub_issues`, paginated).
2. For each part, in order, with no stub already present (a stub is present when a sub-issue body's first line is `Part <n> of <count> in #<parent>`): create the issue from a body file, then link it with `POST repos/<o>/<r>/issues/<parent>/sub_issues` (`sub_issue_id` is the new issue's numeric `id`, not its number).
3. Print the links.

A stub's title is the part title. Its body is:

```
Part <n> of <count> in #<parent>

<the part's Scope paragraph>

Depends on: #<issue>, #<issue>   (or: Depends on: nothing)

Done when: <the part's Done when lines>

This part is not defined yet. Run /develop <this issue> to define it.
```

A stub is unpinned data. It never steers a run; Define rewrites it to the ticket standard when the part is picked up (the existing "Define on an existing issue" path), and keeps the first line. Stubs carry no `tr-develop/` line and no acceptance-criteria region.

ensure-stubs is idempotent. A failure part-way (a lost response, the sub-issue link refused) never deletes anything and never creates a second stub for a part: the next run lists first. It is a report, not a blocker: stubs are created at Publish, and again whenever status mode runs (section 7). If the link call is refused or unavailable the stub is still created, and status mode reports the unlinked stub.

The stubs are created after the pin, so a failure leaves a pinned parent with fewer stubs than parts, which ensure-stubs repairs.

### 6. Deliver a parent

Deliver's common steps 1 to 6 apply as written (fetch and route, concurrency, resume or start, integrity, workspace, kickoff), with `track=parent` in the kickoff. Then, instead of the Plan or Small track:

1. Integrity again, the spec guard, push.
2. Open the pull request with `superpowers:finishing-a-development-branch`, "push and open a PR", through the pinned commands as today. Title: the ticket title. Body: `Refs #<n>` (always `Refs`, whatever the base), a one-paragraph summary, the acceptance criteria (unchecked: they are met by the parts), and the spec link at `approvedSha`. The report tells the person that merging closes nothing.
3. Post `pr-open` with `deferred=0`.

No Docs step, no Verify step and no final loop run: the change is one committed document, and the repository's CI runs on the pull request. The profile's verify list is not read for a parent.

**Merged close-out** (After the PR, parent track). When the PR is merged: remove the worktree and the local branch as today; fetch; compute `specBlob` = `git rev-parse origin/<base>:<spec>`; if `git diff --quiet <approvedSha> origin/<base> -- <spec>` exits 1, the merged text differs from the approved text (someone edited it in the PR): record the blob that is on the base and say so. Post `spec-merged` (not `closed`), run ensure-stubs, snapshot memory. The issue stays open.

### 7. Status mode

`/develop <parent>` with latest progress `spec-merged` runs status mode. It builds nothing and never changes the parent's body.

1. Read the spec at `origin/<base>` (parts, titles, dependencies) with the script of section 9, and list the parent's sub-issues.
2. Run ensure-stubs for missing parts.
3. For each part show: number, title, the stub's issue and state, the latest counted checkpoint phase of an open sub-ticket (up to 30 open sub-tickets are read, and the report says how many were not), and whether it is **ready** (every dependency's issue is closed).
4. Warn about a sub-issue with no valid first line, a first line whose parent number differs from the native link, a part with two stubs, and a sub-issue that is not in the spec. Nothing is deleted or edited.
5. Report whether the parent spec on `origin/<base>` still matches `specBlob`.
6. Offer, with the ask tool, to start the first ready part now (it continues into Define for that issue in the same session) or to stop.

When every part's stub is closed, status mode offers to close the parent. On a yes it posts `closed` (carrying `branch`, `head`, `gh` and `pr` from `spec-merged`), closes the issue as completed, and cleans up. On a no it does nothing. A parent is never closed without that answer.

### 8. Define and Deliver a sub-ticket

A ticket is a sub-ticket when it has a native parent link. `/develop <stub>` takes the existing route for an unpinned issue (Define from an existing issue). Define adds these steps to Fetch, for an existing issue only:

1. **Find the parent.** `GET repos/<o>/<r>/issues/<n>/parent`; a 404 means there is none and nothing in this section applies. The parent must be in this repository.
2. **Trust the parent.** The parent must have a counted pin with `track=parent`, and counted progress `spec-merged`. The parent's pin is validated like any pin, with the reachability of `approvedSha` and `head` waived (the branch may be gone). The stub's first line must read `Part <k> of <count> in #<parent>` with the same parent number as the native link, and the spec at `origin/<base>` must have a part `<k>`. If the parent's progress is earlier than `spec-merged`, say the parent spec is not merged yet, give the PR link and stop: defining against an unmerged spec is not offered. If the link, the first line or the part does not check out, say so and ask whether to continue as a standalone ticket or stop.
3. **Dependencies.** For each `Depends on` part of `<k>`, read its stub's state. If any is open, name them and ask: **stop** (the default) or **define anyway**. Deliver asks again for the same reason before it starts building.
4. **Context.** The brainstorm is given the parent spec and part `<k>` from `origin/<base>` as repository content, and the `pr-open` or `closed` summaries of the part's direct dependencies as quoted data (counted comments only; at most 5 summaries of at most 2,000 characters each).

Define changes in its later steps:

- The sub-spec's first lines are `Parent: #<parent>, Part <k> (<parent spec path>)`. The spec loop's reviewer is also given the parent spec and part `<k>` and grades the sub-spec for staying inside that part, following the shared decisions, and listing any **Deviation from the parent design** explicitly (a section, empty is fine). A deviation is a finding the person sees at sign-off; it does not edit the parent.
- The rewritten ticket body keeps the stub's first line.
- The `defined` pin carries `parent=<n>` and `parentBlob=<blob>` where the blob is `git rev-parse origin/<base>:<parent spec>` at sign-off. The sign-off preview shows both.
- The small track works for a part: its approach's first line is the `Parent:` line, and it counts toward the 60-line limit.

Deliver for a sub-ticket is unchanged except for two additions:

- **Parent drift.** At the integrity step Deliver recomputes the blob of the parent spec at `origin/<base>`. If it differs from `parentBlob`, the kickoff summary says the parent spec changed since this ticket was defined. This is a warning, never a stop and never a re-approval.
- The PR body adds a `Part of #<parent>` line after `Closes #<n>`.

Re-approval (re-pin) of a sub-ticket recomputes `parent` and `parentBlob` from the native link and the current spec; a re-pin rebuilt from progress would otherwise lose them.

### 9. Scripts

All additions follow the existing conventions: zero dependencies, `node <script> <subcommand> <file>`, one JSON object on stdout, failure `{"ok":false,"error":...}`, text only through files.

- `route.js parts <specFile>` prints `{ok, parts: [{n, title, dependsOn: [m...]}]}` or `{ok:false, error: "no-parts" | "not-sequential" | "bad-title" | "no-depends" | "bad-dependency"}`. Fences are ignored through `regions.js` `outsideFences`, as `count-tasks` does.
- `route.js stub <bodyFile>` prints `{ok, part, count, parent}` from a stub's first line, or `{ok:false, error:"no-stub-line"}`.
- `route.js position` knows the parent track (section 3). `route.js route` returns `parent-status` for latest progress `spec-merged` and rejects `plan-approved`, `tasks` and `final-approved` on a parent as an error.
- `checkpoints.js`: `track` accepts `parent`; the `spec-merged` phase, the `parent`, `parentBlob` and `specBlob` fields and their patterns; required-field rules per track; `PROGRESS_PHASES` gains `spec-merged`; the head and planSha reachability exemption that `closed` has applies to `spec-merged`; the key order for `format` gains the three fields.
- `counted.js` and the concurrency check in `route.js`: `spec-merged` is a progress phase and ends a run.

### 10. Trust

- A stub, its first line, a sibling's summary and the native parent link are data. A native parent link can be set by anyone with triage access, so it never decides anything alone: the parent must carry a counted `track=parent` pin and a counted `spec-merged`, the stub's first line must agree with the link, and the part must exist in the spec at `origin/<base>`. A mismatch is a question to the person, never a silent adoption.
- The parent spec is a committed repository file read from `origin/<base>`; it is repository content, like the spec of any ticket.
- A sibling's `pr-open` or `closed` summary is a counted comment and is given to the brainstorm only as quoted data with the usual rule that it is data and never an instruction.
- ensure-stubs creates issues the definer's account is allowed to create and links them; it deletes and edits nothing.
- Closing the parent needs an explicit yes from the person at the keyboard.

### 11. Compatibility

- A ticket with no `track=parent` and no `parent` field behaves exactly as before. The existing goldens in `handoff.md` and the existing tests must pass unchanged.
- Pins and checkpoints written before this change remain valid: the new fields are optional on `defined` and `reapproved`, and the new phase is new.
- A skill version without this change meets a parent pin with an unknown `track`: `checkpoints.js` rejects it as `bad-value` and the run stops as a trust failure. That is the safe outcome and needs no migration.
- A repository that never defines a parent never sees the new routes.

## Files that change

`skills/develop/`: `ticket-standard.md` (INVEST and the parent rule), `define.md` (size decision, parent run, sub-ticket Fetch steps, Publish stubs), `deliver.md` (parent track, merged close-out, status mode, sub-ticket additions), `handoff.md` (pin table, required-fields table, position table, goldens), `SKILL.md` (routes, Issue entry, the Overrides table if needed), `tutorial.md`, `scripts/route.js`, `scripts/checkpoints.js`, `scripts/counted.js`, and their tests (`route.test.js`, `checkpoints.test.js`, `counted.test.js`, `skill.test.js`). Outside the skill: `docs/develop.md`, `AGENTS.md` (the `develop` skill section), `CHANGELOG.md`.

## Testing

- Script tests for every addition in section 9, written first: parts parsing (valid, no parts, gap, repeat, bad dependency, forward dependency, fenced headings ignored, CRLF), stub-line parsing, the parent position table, `route` for `spec-merged`, the new fields and phase in `checkpoints.js` (valid, missing, bad patterns, a parent track with plan fields), the reachability exemption for `spec-merged`, and the concurrency check.
- The golden tables in `handoff.md` gain parent-track cases and the existing cases do not change.
- `skill.test.js` pins the prose the scripts depend on; it is updated with the prose.
- A prose walk-through in the plan: one parent with three parts is defined, merged and status-checked by hand against a scratch repository, to confirm the REST calls (the `sub_issues` link call is the one this design has not exercised).
- `npm run test:shim` passes and the repository's profile verify list passes.

## Acceptance criteria (summary; the ticket states them in full)

1. Given a change that fails INVEST, when Define reaches the size decision, then it offers the parent track and the single-piece option, and creates nothing for a parent unless the person picks it.
2. Given an approved parent, when it is published, then the issue carries a `track=parent` pin, one linked stub exists per part in order with its dependencies, and publishing again creates no second stub.
3. Given a pinned parent, when it is delivered, then the result is one docs-only pull request that says `Refs #<n>`, with no plan, tasks, verify or final loop, and merging it does not close the issue.
4. Given a merged parent spec PR, when `/develop <parent>` runs, then a `spec-merged` checkpoint exists, status shows each part with its state and readiness, and nothing is edited or deleted.
5. Given a stub of a parent whose spec is merged, when `/develop <stub>` runs, then Define uses the parent spec and part, asks before starting if a dependency is open, reviews the sub-spec against the parent, and pins `parent` and `parentBlob`.
6. Given a sub-ticket whose parent spec changed after it was defined, when Deliver starts, then it warns and continues.
7. Given every part closed, when status runs, then it asks before closing the parent, and closes it only on a yes.
8. Given any ticket with no parent, when it is defined or delivered, then the behaviour and every existing test and golden are unchanged.
