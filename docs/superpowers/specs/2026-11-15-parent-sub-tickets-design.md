# Parent tickets and sub-tickets for `/develop`

Status: design for review. Scope: the `develop` skill (`skills/develop/`), its scripts and tests, and its user documentation.

## Summary

`/develop` takes one GitHub issue to one pull request. That is the right unit for a change one person can hold in their head, and it is the wrong unit for larger work. Today the skill refuses the larger case: when a change is "not small or not independent" it says so, asks which piece to define first, defines only that one and "never creates a parent or an epic". People then run the larger case by hand. Issue #92 is the example in this repository: a hand-written tracking issue, native GitHub sub-issues, one shared design document that rides along with the first sub-ticket's pull request, and a checklist in the parent body that someone edits.

This design adds a third **track**, `parent`, so that larger work is a first-class shape:

- A **parent ticket** holds the goal, the shared decisions and an ordered breakdown into parts. It has its own spec (the *parent spec*) and is delivered as a small docs-only pull request that puts the spec on the base branch. It is never built, and it is closed only when every part is done.
- A **sub-ticket** is an ordinary `/develop` ticket (plan or small track) that is one part of a parent. Its spec is the implementation design for that part and is reviewed against the parent spec. Everything the skill does today for a ticket (pin, spec guard, plan, tasks, final loop, PR, checkpoints) is unchanged.

The parent spec says *what* and *in what pieces*; each sub-spec says *how*, written just in time so that it reflects what earlier parts actually shipped.

Terms. The **spec-bearing tracks** are `plan` and `parent`: the two whose pin carries a `spec` and an `approvedSha`, so the integrity check, the spec guard and re-approval apply to both. Wherever the skill's prose says "plan track" for one of those mechanisms, the implementation names the spec-bearing tracks instead (the plan lists each sentence).

## Goals

1. Larger work can be defined once as a parent and then delivered part by part, with each part running through the existing Define and Deliver flow.
2. The parent spec is on the base branch before any part is defined, so every sub-ticket cites a merged document.
3. The parent never goes stale in the trust model: its body stays pinned and static, and live progress comes from the sub-tickets' own checkpoints and states.
4. A ticket that passes INVEST and has no parent is defined and delivered as it is today.

## Non-goals

- Stacked branches or an integration branch. A part whose dependencies are not done waits (or the person overrides after being asked). The base stays the default branch, so required CI still runs on every pull request.
- Parents of parents. One level only.
- Sub-issues in another repository.
- Amending a parent spec after its pull request has merged. That is an ordinary pull request to the document. Sub-tickets notice the change (Parent drift) but the skill has no flow to re-approve a merged parent. A merged spec that cannot be parsed is reported, not repaired (section 6).
- Editing or deleting sub-issues after they are created. Status reports a difference from the spec; the person acts on it.
- Adopting an existing issue as a part (for example #92's sub-issues, which have no first line). Only issues the skill created are recognised.
- Using GitHub's "blocked by" issue relations. Dependencies live in the parent spec, which is committed and reviewed.
- Closing a parent without asking.

## Why one ticket

`ticket-standard.md` says a change that is not small is several tickets. This change could be split into the parent track (sections 1 to 7) and sub-ticket awareness (section 8). A split is feasible (today's Define already handles an existing issue, so the parent half would ship alone). It is kept as one ticket because the halves are best reviewed together against one design, they share the same unverified API risk (the sub-issue link call), and a split would need the same spec cited by two tickets, which is the problem this design exists to remove. The plan orders the tasks so the parent half lands first; the person may ask for the split at sign-off.

## Design

### 1. The `parent` track

`track` takes a third value, `parent`, beside `plan` and `small`. A parent ticket is defined and delivered like a plan-track ticket with these differences:

| | Plan track | Parent track |
|---|---|---|
| Spec | the implementation design | the parent spec (section 2) |
| Define reviewer checks | the spec and the ticket | the same, plus the breakdown (section 2) |
| Publish | issue, pin, branch push | the same; no sub-issues yet (section 5) |
| Deliver builds | plan, tasks, final loop, verify | nothing: no plan, no tasks, no final loop, no verify, no docs step |
| Deliver opens | a PR that `Closes #n` | a docs-only PR that `Refs #n` (never `Closes`) |
| Checkpoints | kickoff, plan-approved, tasks, final-approved, pr-open, closed | kickoff, pr-open, spec-merged, closed |
| After the PR merges | `closed` | `spec-merged`, sub-issues created, then status mode (section 7) |

The parent's pin carries `track=parent`, `branch`, `spec` and `approvedSha` exactly as a plan-track pin does, so the integrity check, the spec guard and re-approval work unchanged while the spec pull request is open.

The parent's body follows the ticket standard like any ticket: problem, outcome, acceptance criteria (the criteria for the whole goal), scope and context. The marked acceptance-criteria region is present and hashed. The body is never edited after sign-off, so it holds no progress list; status mode (section 7) shows progress.

A parent is refused on an issue that already has a parent (one level only).

### 2. The parent spec

The parent spec is a committed file, `docs/superpowers/specs/YYYY-MM-DD-<slug>-design.md`, with these parts, in this order:

1. **Goal**, and what success means.
2. **Non-goals.**
3. **Shared decisions**: constraints and choices every part must follow (interfaces, naming, data shapes at the boundary, rollout rules). No implementation detail that belongs to one part.
4. **Breakdown**: the ordered parts.

The breakdown is machine-read. Each part is a level-3 heading and three labelled lines:

```
### Part 2: Direct delivery in pi and Claude Code
Scope: what this part delivers and what it leaves to other parts.
Depends on: Part 1
Done when: one or two lines of rough, observable acceptance.
```

Grammar, read outside code fences, CRLF read as LF:

- Heading: exactly `### Part <n>: <title>`, `n` = 1, 2, 3 ... in file order with no gap, repeat or leading zero, 1 to 50 parts. The title is 1 to 80 characters with no leading or trailing space.
- Each part has the labels `Scope:`, `Depends on:` and `Done when:`, each exactly once, in that order, at the start of a line. A label's text runs to the next label or heading, is trimmed, and is non-empty and at most 1,500 characters. A text may span lines.
- `Depends on:` is `none`, or `Part <m>` repeated, separated by `, `, in ascending order with no repeat, every `m` lower than `n`. Order is therefore a valid build order and cycles are impossible. The comparison is case-sensitive.
- A spec with no part heading is not a parent spec.

The reviewer grades the breakdown: each part is independently shippable (INVEST), no part is a horizontal layer that ships nothing alone, the parts together cover every acceptance criterion of the parent, and the dependencies are real and minimal.

### 3. Pin, checkpoints and routing

New and changed fields:

| Field | Where | Meaning |
|---|---|---|
| `track=parent` | pin, kickoff, pr-open, spec-merged, closed, reapproved | the third track |
| `parent` | `defined`, `reapproved` of a sub-ticket | the parent's issue number, `^[1-9]\d{0,8}$`; present with `parentBlob` or not at all |
| `parentBlob` | same | the git blob id (40 hex) of the parent spec at `origin/<base>` when the sub-ticket was signed off |
| `specBlob` | `spec-merged` | the blob id (40 hex) of the spec at `origin/<base>` after the merge |

New phase `spec-merged`, for the parent track only. Required: `track` (always `parent`), `branch`, `head`, `gh`, `pr`, `spec`, `specBlob`. It is a progress phase; it joins `PHASES`, the checkpoint-line pattern (`LINE_RE`, which hard-codes the phase list) and `PROGRESS_PHASES`. Like `closed` it is exempt from the reachability of `head`, because the branch may be deleted after the merge, and from the integrity check before posting (the exemption list in `deliver.md` and `handoff.md` gains it). For the concurrency check it ends a run and is not a heartbeat: it is added to both lists in `route.js` (`NOT_HEARTBEAT` and `ENDS_A_RUN`), so a fresh `spec-merged` never makes the next `/develop` stop to ask whether a run is live.

Required fields for the parent track (the table in `handoff.md` gains a "parent:" prefix like the existing "plan:" and "small:" ones): `kickoff` needs `spec` and `approvedSha`; `pr-open` needs `spec` and `approvedSha` (no `plan`, `planSha`, `tasksDone`) and `deferred=0`; `reapproved` needs what `defined` needs plus `head`, `review`, `gh`, `deferred` (no plan fields); `closed` is unchanged. A parent never posts `plan-approved`, `tasks` or `final-approved`; one that appears on a parent track is an error in `route.js`, not a guess (`position()` today returns `finish` for `final-approved` before it looks at the track, so that early return is reordered to check the track first).

Resume position for the parent track:

| Progress | Position |
|---|---|
| none | `start` |
| `kickoff` or `reapproved` | `finish` (open or update the spec PR) |
| `pr-open` | `after-the-pr` |
| `spec-merged` | `parent-status` |
| `closed` | `terminal` |

Routing order. The new issue route `parent-status` is chosen when the latest counted progress is `spec-merged`, and like `pr-open` and `closed` it is chosen **before pin validation**, because after the merge the branch is usually deleted and the pin's `approvedSha` is no longer reachable from `origin/<branch>`. Status mode checks only the patterns of the pin and checkpoint fields it uses (`spec` matches the spec pattern, `specBlob` is 40 hex, `definedAt` is an ISO-8601 UTC time, and the validator gains that pattern for `definedAt`, which today falls through unchecked). For a closed parent issue the route returns `askClosed` true (`issueState` closed), as the `deliver` route does. The rest of the routing order is unchanged, and a ticket whose pin has no `track=parent` takes every path it takes today.

### 4. Define a parent

Define reaches the size decision in Brainstorm (today it ends in "plan track" or "small track"). A change that fails INVEST (not small, not independent) now offers two choices, and Define asks which:

- **Define it as a parent** (this design), or
- **Define only the first piece** (today's behaviour: a single ticket and no parent).

Define never creates a parent unless the person picks it. `ticket-standard.md` changes from "never create a parent or an epic" to "create a parent only through the parent track; never a hand-made epic whose body tracks work".

The parent run follows the plan track step by step: rename the branch `feat/<slug>`, brainstorm writes the parent spec (section 2), commit, draft the ticket, run the spec loop (six rounds, review contract unchanged) with the extra breakdown questions of section 2, sign-off. At sign-off the preview adds the list of parts that will become sub-issues after the spec is merged: part number, title, dependencies. The pin, the branch push and the `defined` comment are published as for the plan track; no sub-issue exists yet.

### 5. The sub-issues (stubs)

Sub-issues are created when the spec is merged, never at Publish: while the spec pull request is open the spec can still change in review or through re-approval, and a sub-issue created earlier would be stale or orphaned. They are created by an operation called ensure-stubs, run by Merged close-out (section 6) and again by status mode (section 7).

A **stub** is the issue for one part. Its identity is the pair (parent number, part number), read from its body's first line, which is exactly `Part <n> of <count> in #<parent>` (the count is informational and is not part of the identity). Ensure-stubs, for a merged spec that parses (section 6):

1. List the parent's sub-issues (`GET repos/<o>/<r>/issues/<parent>/sub_issues`, paginated). Each issue whose first line parses as a stub line for this parent is a stub for that part.
2. Scan the repository's issues updated since the parent's `definedAt` (`GET repos/<o>/<r>/issues?state=all&since=<definedAt>`, at most 5 pages of 100, bodies included; the report says when the scan was cut short) for unlinked issues whose first line parses as a stub line for this parent. An issue is **eligible** only when it is not a pull request (the issues endpoint returns pull requests too) and its author has write access (`counted.js write-access`, the lookup the skill already uses; a `user.type` other than `User` is never eligible). Of the eligible claimants of a part the lowest issue number is **adopted**: linked to the parent, never duplicated. An ineligible claimant is only reported (step 5), never linked.
3. For each part, in order, with no stub after steps 1 and 2: create the issue from a body file with no assignee (it is an unclaimed task), and link it (`POST repos/<o>/<r>/issues/<parent>/sub_issues`, `-F sub_issue_id=<id>` so that the number is typed as an integer, where `id` is the new issue's numeric `id`, not its number). A refused link is retried once; a second refusal is reported with the issue number, and the issue stays as an unlinked stub that step 2 adopts on the next run. A ghost stub whose link is refused for good is closed by hand by the person, and the part then gets a new stub on the next run.
4. When two linked issues claim the same part, the lowest issue number is the stub and the others are reported, never edited or closed.
5. Print the links and anything reported (ineligible claimants, duplicates, a truncated scan).

There is no lock: two runs on different machines can each create a stub for a part. The result is a reported duplicate, which step 4 resolves by number and the person closes by hand. That is accepted.

A stub's title is the part title. Its body is written by a script from the spec (section 9), never retyped:

```
Part <n> of <count> in #<parent>

<the part's Scope text>

Depends on: #<issue>, #<issue>   (or: Depends on: nothing)

Done when: <the part's Done when text>

This part is not defined yet. Run /develop <this issue> to define it.
```

A dependency's issue number exists when it is needed, because parts are created in order and a part depends only on lower parts. A stub is unpinned data. It never steers a run; Define rewrites it to the ticket standard when the part is picked up (the existing "Define on an existing issue" path) and keeps the first line. Stubs carry no `tr-develop/` line and no acceptance-criteria region. Ensure-stubs deletes and edits nothing, and it is idempotent: the lists come first, every time.

### 6. Deliver a parent

Deliver's common steps 1 to 6 apply as written (fetch and route, concurrency, resume or start, integrity, workspace, kickoff), with `track=parent` in the kickoff. Then, instead of the Plan or Small track:

1. Integrity again, the spec guard, push. The branch must change only the spec file: `git diff --name-only origin/<base>...HEAD` lists exactly `<spec>`, otherwise it is blocker 2 (the approved artifact would have to change).
2. Open the pull request with `superpowers:finishing-a-development-branch`, "push and open a PR", through the pinned commands as today. Title: the ticket title. Body: `Refs #<n>` (always `Refs`, whatever the base), a one-paragraph summary, the acceptance criteria (unchecked: they are met by the parts), and the spec link at `approvedSha`. The report tells the person that merging closes nothing.
3. Post `pr-open` with `deferred=0`.

No Docs step, no Verify step and no final loop run: the change is one committed document, and the repository's CI runs on the pull request. The profile's verify list is not read for a parent.

**Merged close-out** (After the PR, parent track). When the PR is merged:

1. Fetch. Pattern-check `spec` and `approvedSha` before they reach a command. Remove the worktree and the local branch as today.
2. Run `route.js parts` on the spec at `origin/<base>`. If the file is missing or the breakdown does not parse, this is blocker 2 (the approved artifact would have to change), at stage `finish`: a pin exists, so `blockers.md` applies and a `blocked` checkpoint is posted, and `spec-merged` is not. The recovery is an ordinary pull request that fixes the document, then `/develop <parent>` again, which repeats this step (a `blocked` is never progress, so the route is still `after-the-pr`).
3. Compute `specBlob` = `git rev-parse origin/<base>:<spec>`. Then test whether the merged text equals the approved text: `git diff --quiet <approvedSha> origin/<base> -- <spec>`; exit 1 means someone edited the spec in the PR, which is reported and recorded (the blob on the base governs); if `<approvedSha>` is not in the local store (a fresh machine, a squash merge), fetch `refs/pull/<pr>/head` and retry once, and when it is still absent say the comparison was skipped.
4. Post `spec-merged` (no integrity check runs for it), run ensure-stubs, snapshot memory (`SKILL.md`'s list gains `spec-merged`). The issue stays open.

### 7. Status mode

`/develop <parent>` with latest progress `spec-merged` runs status mode. It runs Deliver's step 1 (fetch, read, route) only: there is no run to be live, so the concurrency check is skipped. It builds nothing and never changes the parent's body. A closed parent issue is handled by the route's `askClosed`.

1. Read the spec at `origin/<base>` with `route.js parts`. If it does not parse, this is blocker 2 at stage `finish`, as in section 6 step 2.
2. Run ensure-stubs.
3. For each part show: number, title, the stub's issue and state, the latest counted checkpoint phase of an open sub-ticket (up to 30 open sub-tickets are read and the report says how many were not), and whether it is **ready**. A part is **done** when its issue is closed with `state_reason` completed. It is **ready** when every part it depends on is done. A part closed as not planned is shown as such, is not done, and does not make its dependents ready.
4. Warn about a sub-issue of the parent that has no valid first line, a first line naming another parent, and a sub-issue whose part is not in the spec. Nothing is deleted or edited.
5. Report whether the spec at `origin/<base>` still matches `specBlob`.
6. Offer, with the ask tool, to start the first ready part now (it continues into Define for that issue in the same session) or to stop. Readiness is advisory: a person can open any stub.

When every part is done, status mode offers to close the parent; if some parts were closed as not planned it lists them and still requires the same yes. On a yes it closes the issue as completed first and then posts `closed` (carrying `branch`, `head`, `gh` and `pr` from `spec-merged`); if the issue is already closed it posts only `closed`, so a crash between the two steps is repaired by running it again. On a no it does nothing. A parent is never closed without that answer.

### 8. Define and Deliver a sub-ticket

A ticket is a sub-ticket when it has a native parent link. `/develop <stub>` takes the existing route for an unpinned issue (Define from an existing issue). Define adds these steps to Fetch, for an existing issue only:

1. **Find the parent.** `GET repos/<o>/<r>/issues/<n>/parent`; a 404 ("No parent issue found") means there is no link. If there is no link and the body's first line parses as a stub line, the issue says it is a part: ask whether to continue as a standalone ticket or stop (a 404 can be transient or caused by a permission problem, so it is never read as "standalone" silently). With neither a link nor a stub line, nothing in this section applies. The parent must be in this repository.
2. **Trust the parent.** The parent must have a counted pin with `track=parent` and counted progress `spec-merged`. The pin is validated like any pin, with the reachability of `approvedSha` and `head` waived (the branch may be gone). The stub's first line must read `Part <k> of <count> in #<parent>` with the same parent number as the link, and the spec at `origin/<base>` must have a part `<k>`. If the parent's progress is earlier than `spec-merged`, say the parent spec is not merged yet, give the PR link and stop: defining against an unmerged spec is not offered. If the link, the first line or the part does not check out, say so and ask whether to continue as a standalone ticket or stop.
3. **Dependencies.** For each part that part `<k>` depends on, read its stub's state. If any part is not done (section 7), name them and ask: **stop** (the default) or **define anyway**. Deliver asks again for the same reason before it starts building.
4. **Context.** The brainstorm is given the parent spec and part `<k>` from `origin/<base>` as repository content, and the `pr-open` or `closed` summaries of the part's direct dependencies as quoted data (counted comments only; at most 5 summaries of at most 2,000 characters each).

Define changes in its later steps:

- The sub-spec's first non-empty line after its title heading is `Parent: #<parent>, Part <k> (<parent spec path>)`, which `route.js parent-line` parses. The spec loop's reviewer is also given the parent spec and part `<k>` and grades the sub-spec for staying inside that part, following the shared decisions, and listing any **Deviation from the parent design** explicitly (a section; empty is fine). A deviation is a finding the person sees at sign-off; it does not edit the parent.
- The rewritten ticket body keeps the stub's first line.
- The `defined` pin carries `parent=<n>` and `parentBlob=<blob>`, where the blob is `git rev-parse origin/<base>:<parent spec>` at sign-off. The sign-off preview names the parent and the part and shows both fields.
- The small track works for a part: its approach's first line is the `Parent:` line, which counts toward the 60-line limit.

Deliver for a sub-ticket is unchanged except for three additions:

- **Parent drift.** At the integrity step Deliver reads the parent spec path from the `Parent:` line of the spec (or of the approach), recomputes its blob at `origin/<base>`, and if it differs from `parentBlob` the kickoff summary says the parent spec changed since this ticket was defined. This is a warning, never a stop and never a re-approval.
- The PR body adds a `Part of #<parent>` line after `Closes #<n>`.
- Re-approval (re-pin) of a sub-ticket recomputes `parent` and `parentBlob` from the link and the current spec and shows the old and the new blob to the person, so a re-pin cannot hide drift; a re-pin rebuilt from progress would otherwise lose the fields.

### 9. Scripts

All additions follow the existing conventions: zero dependencies, `node <script> <subcommand> <file>`, one JSON object on stdout, failure `{"ok":false,"error":...}`, text only through files.

- `route.js parts <specFile>` prints `{ok, parts: [{n, title, scope, doneWhen, dependsOn: [m...]}]}` under the grammar of section 2, or `{ok:false, error: "no-parts" | "not-sequential" | "too-many" | "bad-title" | "bad-label" | "empty-text" | "bad-dependency"}`. Fences are ignored through `regions.js` `outsideFences`, as `count-tasks` does.
- `route.js stub <bodyFile>` reads the first line (BOM and `\r` removed, trimmed) against `^Part ([1-9]\d?) of ([1-9]\d?) in #([1-9]\d{0,8})$` and prints `{ok, part, count, parent}` or `{ok:false, error:"no-stub-line"}`.
- `route.js stub-body <specFile> <part> <parent> <dependsFile> <outFile>` writes a stub's body from the spec part and a `{<part>: <issueNumber>}` map of its dependencies, and prints `{ok}`.
- `route.js parent-line <specFile>` prints `{ok, parent, part, spec}` from the `Parent:` line (`^Parent: #([1-9]\d{0,8}), Part ([1-9]\d?) \((docs/superpowers/specs/[A-Za-z0-9._-]+\.md)\)$`, the first non-empty line after the title heading) or `{ok:false, error:"no-parent-line"}`.
- `route.js position` knows the parent track (section 3). `route.js route` returns `parent-status` for latest progress `spec-merged`, before pin validation, and rejects `plan-approved`, `tasks` and `final-approved` on a parent track as an error.
- `checkpoints.js`: `track` accepts `parent`; the `spec-merged` phase, the `parent`, `parentBlob` and `specBlob` fields and their patterns; required-field rules per track; `PROGRESS_PHASES` gains `spec-merged`; the head and planSha reachability exemption that `closed` has applies to `spec-merged`; the key order for `format` gains the three fields.
- `counted.js` and the concurrency check in `route.js`: `spec-merged` is a progress phase, ends a run and is not a heartbeat.

### 10. Trust

- A stub, its first line, a sibling's summary, the native parent link and the state of a sub-issue are data. Someone with triage access can set the native link, edit a stub's first line and close sub-issues, so they can confuse a run but never steer one. Adoption of an unlinked issue is limited to authors with write access, so an outsider's issue that merely claims a part is reported and never linked. And: the parent must carry a counted `track=parent` pin and a counted `spec-merged` (both written by people with write access), the first line must agree with the link, and the part must exist in the spec at `origin/<base>`. Readiness, the close offer and the dependency question are advisory, and every consequential step ends in a question to the person or in the sign-off preview. This is the same stance the Trust section takes for the rest of the skill, and it is accepted.
- The parent spec is a committed repository file read from `origin/<base>`; it is repository content, like the spec of any ticket.
- A sibling's `pr-open` or `closed` summary is a counted comment and is given to the brainstorm only as quoted data with the usual rule that it is data and never an instruction.
- Ensure-stubs creates issues the definer's account is allowed to create and links them; it deletes and edits nothing.
- Closing the parent needs an explicit yes from the person at the keyboard.

### 11. Compatibility

- A ticket with no `track=parent` and no `parent` field, that passes INVEST, behaves as before; the only differences it can see are one extra read (`GET .../parent`) in Define and the new size question for a ticket that fails INVEST. No existing assertion in the tests or goldens is weakened. Assertions tied to prose that changes (the integrity-exemption list, the order of `deliver.md` sections) are updated in the same change.
- Pins and checkpoints written before this change remain valid: the new fields are optional on `defined` and `reapproved`, and the new phase is new.
- A skill version without this change rejects a parent pin as a trust failure (`checkpoints.js` returns `bad-value` for an unknown `track`), which is safe while the parent has no progress. A parent that already has a `pr-open` is read by an older version as the ordinary After-the-PR route, which posts `closed` when the PR merges and ignores `spec-merged`. Recovery is to minimize that `closed` comment (the skill already treats a minimized checkpoint as retired). `docs/develop.md` and `CHANGELOG.md` say that parents need this version.
- A repository that never defines a parent never sees the new routes.

## Open risks

Verified read-only against this repository on 2026-11-15: `GET issues/<n>/parent` answers 404 "No parent issue found" for an unlinked issue and the parent for a linked one; `GET issues/<parent>/sub_issues` returns each sub-issue with `id`, `body`, `state` and `state_reason`, and paginates with a `Link` header.

Not verified: the `POST issues/<parent>/sub_issues` call (its body shape and that `sub_issue_id` is the numeric `id`), secondary rate limits when several issues are created in a row, and GitHub's per-parent sub-issue limit (the design caps a breakdown at 50 parts). The first task of the plan is a probe of these in a throwaway repository before any code depends on them.

Cost. Ensure-stubs reads the sub-issue list (one call per 100) and at most 5 pages of issues, and status mode reads the comments of up to 30 open sub-tickets (one call each, plus permission lookups). Both are bounded and report when they stop early.

## Files that change

`skills/develop/`: `blockers.md` (blocker 2 names an unusable merged parent spec), `ticket-standard.md` (INVEST and the parent rule, Review grades and Markers for the spec-bearing tracks), `define.md` (size decision, parent run, sub-ticket Fetch steps), `deliver.md` (parent track, merged close-out, status mode, sub-ticket additions, the exemption list), `handoff.md` (pin table, required-fields table, position table, goldens), `SKILL.md` (routes, Issue entry, the Memory snapshot list), `tutorial.md`, `scripts/route.js`, `scripts/checkpoints.js`, `scripts/counted.js`, and their tests (`route.test.js`, `checkpoints.test.js`, `counted.test.js`, `skill.test.js`). Outside the skill: `docs/develop.md`, `AGENTS.md` (the `develop` skill section), `CHANGELOG.md`.

## Testing

- Script tests for every addition in section 9, written first: `parts` (valid, no parts, gap, repeat, leading zero, 51 parts, bad title, missing or repeated label, empty text, forward and repeated dependency, fenced headings ignored, CRLF), `stub` (valid, CRLF, BOM, bounds), `stub-body`, `parent-line`, the parent position table, `route` for `spec-merged` before pin validation, the new fields and phase in `checkpoints.js` (valid, missing, bad patterns, a parent track with plan fields), the reachability and heartbeat rules for `spec-merged` (a golden for a fresh `spec-merged` alone is not live), and the concurrency check.
- The golden tables in `handoff.md` gain parent-track cases and the existing cases do not change.
- `skill.test.js` pins the prose the scripts depend on; it is updated with the prose.
- A prose walk-through in the plan: one parent with three parts is defined, merged and status-checked by hand in a throwaway repository, which also covers the open risks above.
- `npm run test:shim` passes and the repository's profile verify list passes.

## Acceptance criteria (summary; the ticket states them in full)

1. Given a change that fails INVEST, when Define reaches the size decision, then it offers the parent track and the single-piece option, and creates nothing for a parent unless the person picks it.
2. Given an approved parent, when it is published, then the issue carries a `track=parent` pin and the sign-off showed the parts that will become sub-issues.
3. Given a pinned parent, when it is delivered, then the result is one docs-only pull request that says `Refs #<n>`, with no plan, tasks, verify or final loop, and merging it does not close the issue.
4. Given a merged parent spec PR, when `/develop <parent>` runs, then a `spec-merged` checkpoint exists, one linked sub-issue exists per part in order (none twice if run again), and status shows each part with its state and readiness without editing or deleting anything.
5. Given a sub-issue of a parent whose spec is merged, when `/develop <stub>` runs, then Define uses the parent spec and part, asks before starting if a dependency is not done, reviews the sub-spec against the parent, and pins `parent` and `parentBlob`.
6. Given a sub-ticket whose parent spec changed after it was defined, when Deliver starts, then it warns and continues.
7. Given every part done, when status runs, then it asks before closing the parent, and closes it only on a yes.
8. Given a ticket that passes INVEST and has no parent, when it is defined or delivered, then its behaviour is as before and no existing assertion is weakened.
