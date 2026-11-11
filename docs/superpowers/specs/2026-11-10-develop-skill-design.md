# `/develop` on GitHub issues: Stage 1 design

Date: 2026-11-10. Status: draft for review.

## 1. Intent

`total-recall` ships a `develop` skill that takes a change from an idea to an open pull request,
with a GitHub issue as the ticket. It has two halves:

- **Define** is interactive. The person and the assistant brainstorm, agree a spec (or, for a small
  change, a short approach), have it reviewed, and publish an issue that meets a written ticket
  standard and is pinned by a small machine-readable `defined` comment. Together the issue and that
  comment are the **handoff**.
- **Deliver** is hands-off. Starting from that issue, the assistant plans, implements with review at
  every step, opens a PR that closes the issue, and stops.

The idea comes from the `develop` skill in Radancy's internal cortex-plugin, which does this on
Jira. That tool is internal to one business. This is a clean redesign for GitHub, written fresh:
it keeps the ideas that proved themselves there (reviewer loops, a fixed blocker list, checkpoint
comments that make a run resumable) and rewrites the tracker layer, the text and the validators. No
file is copied from the original.

Why now: `total-recall` already carries the memory a long hands-off run needs, its users work in
GitHub repositories, and its own development has no ticket-to-PR workflow.

## 2. Goals, non-goals, anti-goals

**Goals (Stage 1)**

1. `/develop <text>` defines a change and publishes a GitHub issue that meets the ticket standard.
2. `/develop <n>` on an issue with no counted `defined` comment runs Define from that issue; on one
   with it, it runs Deliver, or resumes it, or reports the PR.
3. Deliver ends with an open PR whose body says `Closes #<n>`, after per-task review and a final
   review loop.
4. A run that stops (crash, blocker, closed laptop) can be resumed by anyone with write access,
   from the issue alone.
5. It works in any GitHub repository, not only this one. A repository can override the ticket shape
   and state its verify commands.
6. It is safe on a public repository: nothing written by a person without write access can steer a
   run.

**Non-goals (Stage 1)**

- PR follow-up (review threads, CI re-runs), auto-merge, release or deploy watching, sub-issues and
  multi-repository work. These are Stage 2 and get their own design (section 17).
- Forks. A repository whose `origin` is a fork is refused.
- Trackers other than GitHub issues.
- Token-usage accounting.

**Anti-goals**

- It never merges, rebases or force-pushes.
- It never re-implements superpowers. It sequences superpowers skills and states each default it
  overrides (section 13).
- It never puts engineering detail in the issue on the plan track. The issue is a ticket; the spec
  and plan are files in the repository.

## 3. Constraints

- **Requires superpowers 7 or later** (`brainstorming`, `writing-plans`,
  `subagent-driven-development`, `test-driven-development`, `verification-before-completion`,
  `finishing-a-development-branch`). Tested against 7.0.0.
- **Requires `gh`**, logged in to an account with write access to the repository.
- **Hosts:** Claude Code and pi are supported. Other hosts get a warning and continue (section 5).
- **Packaging:** Markdown skill files under `skills/develop/`, reviewer agents under `agents/`, and
  validators as zero-dependency Node ESM scripts in the style of `pi/*.js`, tested with
  `node --test` in the existing `test:shim` job. No TypeScript, no build step, no new .NET code.
- **This repository's rules still hold:** no AI co-author trailers, nothing destructive, docs
  updated in the same PR.

## 4. Layout

```
skills/develop/
  SKILL.md              routing, hosts, preflight, overrides, worktrees, progress, memory
  tutorial.md           what `/develop` with no argument prints
  ticket-standard.md    what a good ticket is, per type; repository overrides
  define.md             the interactive half
  deliver.md            the hands-off half: both tracks, finish, close-out
  handoff.md            issue anatomy, hashes, checkpoints, trust, integrity, resume, re-approval
  review-contract.md    dispatch frame, loop rules, final loop, self-review mode
  blockers.md           the stop list and the procedure at a stop
  profile.md            `.github/develop.md` and `/develop initialize`
  scripts/
    regions.js          extract a marked region from an issue body; normalize; SHA-256
    checkpoints.js      parse and validate checkpoint lines (including `defined`); decide define or
                        deliver; pick the resume point
    profile.js          parse and validate a profile
    *.test.js           node --test, driven by the golden blocks in the Markdown files
agents/
  adversarial-reviewer.md
  counterexample-reviewer.md
```

Every rule that must be exact (a grammar, a hash, a routing decision) has a `json golden:<name>`
block in the Markdown file that states it, and the script tests read those blocks. A rule and its
implementation therefore cannot drift apart without a test failing.

Scripts read their input from files written with the host's file-write tool and print one JSON
object. They are never fed through a pipe (Windows PowerShell re-encodes piped non-ASCII text).
The skill runs them as `node <skill directory>/scripts/<name>.js <file>`.

## 5. Routing and hosts

`/develop [arg]`

| `arg` (surrounding whitespace ignored) | Route |
|---|---|
| `^#?[1-9]\d*$` | issue `<n>` in the current repository |
| `<owner>/<repo>#<n>`, or a URL whose path is `/<owner>/<repo>/issues/<n>` | that issue; `<owner>/<repo>` must equal `origin`'s, else stop and say which repository to launch in |
| a URL whose path is `/<owner>/<repo>/pull/<n>` | stop: "that is a pull request; give the issue" |
| empty, `help`, `--help`, `-h`, `?` (any case) | the tutorial |
| `initialize` (any case) | write the repository profile (section 15) |
| anything else | Define, seeded with the text |

GitHub numbers issues and pull requests in one sequence. Read the number with the REST issues
endpoint (GraphQL `issue(number:)` answers NOT_FOUND for a pull request, which would look like a
missing issue). A response with a `pull_request` key: say so and stop. A 301 (the issue moved to
another repository): say where, and stop. The returned repository must equal `origin`'s.

**Issue entry.** An issue argument names an issue; the issue decides the route. Read the issue
(title, body, author, labels, assignees, state) and its comments once, before any write. A closed
issue is noted: Deliver asks before going on with one (it never reopens it), and a closed issue
with a counted `pr-open` goes to "After the PR" without asking:

- a **counted** comment (section 9) carries a `tr-develop/v1 phase=defined` or `phase=kickoff` line →
  **Deliver**, which validates it and fails closed (section 9);
- otherwise → **Define**, started from this issue. A `tr-develop/` line in the body or in an
  uncounted comment is ignored and mentioned once; it never routes, and Define rewrites the body.

When the latest counted `defined` comment (the one that governs) fails validation, or no counted
`defined` survives beside a counted `kickoff`, the person gets a choice. With no counted progress
checkpoint on the issue: **re-define this issue** (Define, which posts a new `defined` comment) or
**stop**. With one: **re-pin** (a re-approval, section 11, that rebuilds the pin from the latest
counted `kickoff`'s `track`, `spec` and `branch` and the person's fresh approval) or **stop**. A
re-define is never offered once progress exists. The pin's `branch` is a suggestion: once a
checkpoint exists, the checkpoint's `branch` governs.

A Define that starts on an issue which an open pull request in this repository already closes
(`Closes #<n>`) asks first, so a lost pin cannot quietly start a second run beside an open PR.

Tell the person the outcome, with the issue's title and link, before anything is written, so a
mistyped number is visible.

**Hosts**

| Host | Behaviour |
|---|---|
| Claude Code | supported |
| pi, with a dispatch tool (`Agent` from pi-subagents) and `ask_user` | supported |
| any other host that can dispatch subagents | one warning at preflight ("untested on this host"), then a normal run |
| any host that cannot dispatch subagents | one warning at preflight, then **self-review mode** (section 12) |

The warning is said once, in chat. It is never written to the PR. `SKILL.md` carries a table that
maps each Claude Code construct it names to its pi form (the ask tool, the todo tool, worktree
entry by `cd <dir> &&`, absolute paths for file tools).

## 6. Preflight

Run before any write. On a failure, say what is missing and how to get it, then stop. A preflight
stop leaves nothing behind: no checkpoint, no worktree, no memory snapshot.

1. **Superpowers.** The six skills are available and the installed version is 7 or later. The
   version is read from the plugin manifest of the install the host loaded (on Claude Code the
   plugin cache's `.claude-plugin/plugin.json`; on pi the manifest above the skills folder it loaded
   superpowers from). A higher major than tested: warn and continue. Missing or lower: stop with
   install instructions. Version unreadable: warn and continue in Define, stop in Deliver. "After the PR" (section 11)
   needs no superpowers and skips this check and check 5.
2. **Dispatch and ask tools.** Decide the review mode: `agent` when a dispatch tool is present,
   else `self` (with the warning of section 5). A host with no way to ask the person a question
   stops: Define cannot run without one.
3. **Repository.** A git repository with an `origin` remote that `gh` can resolve. The profile is
   read at `origin`'s default branch; `<base>` is its `base`, else that default branch. `origin` is
   a fork → stop.
4. **`gh` account.** Pick the logged-in account (`gh auth status`) whose permission on the
   repository is `admin`, `maintain` or `write`, preferring the active one, and name it to the
   person. Every `gh` call of the run, and every subagent prompt that touches GitHub, then sets the
   account on that command only: `GH_TOKEN=$(gh auth token --user <login>) gh ...`. The run never
   runs `gh auth switch`, which would change the account for every parallel session on the
   machine. No account with write access → stop, naming the accounts found. In PowerShell the same
   is `$env:GH_TOKEN = (gh auth token --user <login>); gh ...`, scoped to the command.
5. **Reviewers** (review mode `agent` only). An agent named `adversarial-reviewer`, bare or with
   the `total-recall:` prefix, is available. Absent → stop with install instructions.
   `counterexample-reviewer` absent is not a stop (section 12). An agent of the right name from
   another source satisfies the check: the dependency is the name and the required ending, not the
   publisher.
6. **Worktree location.** `.claude/worktrees/` is git-ignored; if not, add it to the local
   `info/exclude` (never committed).
7. **Notice, not a check.** If the session lists another skill named `develop`, say so once, with
   both paths, and continue.
8. **Advisory.** Deliver needs routine tool calls (git, `gh`, the verify commands) to run without
   permission prompts. Say once that the README lists the minimal allowlist or an auto-accept mode.
   It cannot be detected, so it never blocks.

The working tree need not be clean: a run never touches the person's checkout (section 14).

## 7. The ticket standard

`ticket-standard.md` is the standard Define writes to and the reviewer checks against. It is drawn
from established practice, not from one team's habits.

**Is it one ticket?** INVEST: independent, negotiable, valuable, estimable, small, testable. A
change that fails "small" or "independent" is several tickets. Stage 1 says so and asks the person
which one to define first; it does not create a parent.

**Problem before solution.** The ticket says who is affected, what they cannot do today and why it
matters. It names the outcome wanted and leaves the mechanism to the spec.

**Acceptance criteria that can be checked.** Each criterion is observable and has a pass or fail
answer. Given/When/Then where behaviour is involved, a plain checklist otherwise. No criterion like
"works well" or "is fast" without a number. Each criterion is one list item.

**Explicit scope.** What is in, what is out, and what is deliberately not being solved.

**Ready.** Someone who was not in the conversation could start on it without asking a question.

**Shape by type**

| Type | Sections, in order |
|---|---|
| Feature | Problem · Outcome · Acceptance criteria · Scope (in, out) · Context |
| Bug | What happens · Steps to reproduce · Expected · Actual · Environment and version · Impact · Acceptance criteria · Evidence |
| Chore | What · Why now · Acceptance criteria · Context |

**Title.** A plain statement of the outcome, in the repository's existing convention when it has
one (read from recent issues), at most 80 characters.

**Labels and type.** Use labels the repository already has (`bug`, `enhancement` and the like).
Never create a label. Set GitHub's issue type when the repository offers types; otherwise skip it.

**Repository overrides.** Read `.github/ISSUE_TEMPLATE/` at `origin/<base>`:

- A template exists for the type (a Markdown template or a form) → its sections set the shape. The
  standard's quality rules above still apply to the content.
- No template → the table above sets the shape.
- The profile may name a template per type, or set `ticket.standard: off` to skip the shape and
  quality rules (section 15).
- Whatever the shape, the body always has a marked acceptance-criteria region (section 8). If the
  repository's template has no such section, one is appended.

**Review.** In the plan track's spec loop and the small track's approach loop, the reviewer is also
given the ticket preview and the standard, and grades it: a criterion that cannot be checked, a
missing required section, or engineering detail in a plan-track ticket is a major; a title or
length issue is a minor.

## 8. The issue after Define

A person reading the issue sees an ordinary ticket. The issue body holds no machine block. What a run
trusts is a **`defined` comment** (section 10) that a person with write access posted at sign-off;
the body is checked against hashes recorded in that comment.

**The acceptance-criteria region** (both tracks) sits between two HTML comments, which GitHub does
not render:

```
<!-- tr-develop:ac-begin -->
- [ ] Given a failed sync, when the user retries, then ...
- [ ] ...
<!-- tr-develop:ac-end -->
```

**The approach region** (small track only) sits in a collapsed block at the bottom of the ticket:

```
<details><summary>Approach (small change, agreed at sign-off)</summary>

<!-- tr-develop:approach-begin -->
What changes, where, and the red-green cycles, in a few lines.
<!-- tr-develop:approach-end -->

</details>
```

The approach is at most 60 lines and 4,000 characters. One that does not fit belongs on the plan
track, and Define moves it there. Each region appears exactly once, begin before end; anything else
fails closed.

**The `defined` comment** carries the pins:

| Key | Track | Value |
|---|---|---|
| `track` | both | `plan` or `small` |
| `repo` | both | `<owner>/<name>` of `origin` |
| `base` | both | the base branch |
| `branch` | both | the suggested `feat/<slug>` or `fix/<slug>`, slug at most 50 characters so a suffix fits the branch regex (Deliver may add `-2`, section 11) |
| `bodyHash` | both | 64-hex hash of the issue title and the whole body |
| `acHash` | both | 64-hex hash of the acceptance-criteria region |
| `spec` | plan | `docs/superpowers/specs/<file>.md` |
| `approvedSha` | plan | 40-hex commit of the approved spec |
| `definedBy` | both | the definer's GitHub login |
| `definedAt` | both | ISO-8601 UTC |

`bodyHash` covers everything a person can edit in the ticket, so the approach, the context and the
title are all pinned. `acHash` is kept beside it only so a stop can say "the acceptance criteria
changed" rather than "something changed". The plan track's human-readable part links the spec at its
approved commit: `https://<host>/<repo>/blob/<approvedSha>/<spec>`.

**Hashing.** `regions.js` normalizes text and prints a SHA-256. The normalization changes only what
GitHub's editor or a checkbox tick can change, and (bar one case) nothing that could carry meaning:

1. Convert line endings to LF.
2. Remove a task-list marker (`[ ]`, `[x]`, `[X]`) when it directly follows a list bullet or
   number and a space at the start of a line, at any nesting depth.
3. Remove trailing whitespace from every line, and trailing blank lines from the end. The one
   casualty is a Markdown hard break made of two trailing spaces; the ticket standard says to use
   `<br>` for one.

It strips no punctuation, decodes no entity and collapses no whitespace inside a line, so `src/*.ts`
and `src/.ts`, `max_retries` and `maxretries`, `> 5` and `< 5`, `-1` and `1`, and `5 0` and
`50` all hash differently, and so do `&lt;` and `<`. Ticking a box, saving with CRLF and trailing
spaces do not change a hash.

`bodyHash` is the SHA-256, over UTF-8, of the JSON array `[<normalized title>, <normalized body>]`, so a word
moved between the title and the body changes it. `acHash` is the SHA-256 of the normalized lines
strictly between the criteria markers. The hash is always computed by the script, never by hand, and
its golden cases include each pair above.

**What Define does to an existing issue.** The body is rewritten to the standard and shown in full
at sign-off. When the rewrite changes the author's text, the original is kept verbatim in a
collapsed `<details><summary>Original report</summary>` block above the approach block, inside a
code fence longer than any run of backticks in the text, so it cannot close the block or notify
anyone. Its `<!--` is neutralized to `&lt;!--` and a line-leading `tr-develop` to
`tr&#8209;develop`, so it can never create or break a region. The original is outsider text:
agents are never given it, or are given it as quoted data. Sign-off checks that the preview parses
with one criteria region (and, on the small track, one approach region). GitHub's edit history keeps
the original as well.

**An issue opened by someone without write access.** Its author can edit the body at any time, which
would stop every run. Define asks once: **track it in a new issue** (the default; the new issue links
the original and the PR closes both, and Define comments on the original with the new link), or
**continue on this issue**. The new issue is created by the definer, so only people with write access
can edit it.

## 9. Trust, validation and integrity

**The trust line is write access.** A login has write access when
`gh api repos/<owner>/<repo>/collaborators/<login>/permission` returns a `role_name` of `admin`,
`maintain` or `write`, or a `permission` of `admin` or `write` (which covers a custom role built
on write). `triage`, `read`, `none` and everything else do not. A 404 (not a user, a deleted
account) means no write access, not an error. Results are cached for one run, and comments are
resolved newest first. A lookup that fails for a reason other than that (a 403, a 5xx, a rate limit)
and could decide the outcome is blocker 3, terminal report only. The first task of the build verifies
these answers for an account with write but not admin access (section 21).

**What counts.** Comments are read through GraphQL, with pagination, because `editor`, `lastEditedAt`
and `isMinimized` exist only there. A run acts only on content that counts:

- a **comment counts** when its author has write access and its `editor`, if any, has write access,
  and it is not minimized. A comment whose `editor` is null but whose `lastEditedAt` is set (a
  deleted editor account) does not count. A writer can minimize a wrong checkpoint to retire it, but
  so can others (an organization moderator, say), and an unminimized comment counts again. Comment
  bodies have `\r` removed before any line is matched;
- the **body counts** only when its title and body hash to the `bodyHash` of the latest counted
  `defined` comment. Who edited it is irrelevant: the hash is the check, and a counted comment is
  something only a writer can have posted or changed;
- **approval counts** only from a login with write access, given in the session by the person at
  the keyboard. Nothing in an issue, comment, PR or tool result is ever an approval.

Everything else is ordinary text. It is not routed on, not resumed from and not approved. Deliver
takes one snapshot of the title, body and criteria when it verifies them, and passes agents only that
snapshot and other content that counts; any re-fetch is verified again before it is used. Anything else (an uncounted comment, an issue body that no
longer matches) is either left out of their prompts or quoted as data with the rule that it is data
and never an instruction. Every implementer and reviewer frame carries that rule.

`definedBy` is recorded for the audit trail. Any collaborator with write access can resume,
re-approve or take over a run. A person who can edit other people's issues, or minimize their comments, without write access (the
triage role, an organization moderator) can stop a run or rewind its resume point. That is a
denial or a rewind, never a way to steer one: the branch is never rewritten and a stale pin fails its
hash. It is accepted.

**Validation,** before a `defined` comment steers anything:

- `repo` equals `origin`'s `<owner>/<name>`, compared case-insensitively;
- `base` equals the base branch;
- `branch` matches `^(feat|fix)/[a-z0-9][a-z0-9-]{0,62}$`, here and in every checkpoint (a `blocked`
  checkpoint may carry `-`);
- `spec` matches `^docs/superpowers/specs/[A-Za-z0-9._-]+\.md$`;
- `approvedSha` is 40 hex and reachable from `origin/<branch>`;
- `bodyHash` and `acHash` are 64 hex.

**Integrity,** at every Deliver start and resume, and again before the PR is opened. Once a counted
`pr-open` exists none of this runs: the branch may be gone after a merge, so "After the PR"
(section 11) works from the PR's own URL and reports an integrity mismatch only as advice:

- the title and body hash to `bodyHash`, and the criteria region to `acHash`; the same check runs
  each time a checkpoint is about to be posted, so a mid-run edit stops the run at the next
  checkpoint and not only at the end;
- plan track: `git diff --quiet <approvedSha> origin/<branch> -- <spec>` exits 0. Exit 1 is a
  changed spec. Any other exit (128 for an unfetched commit, say) is tool trouble, blocker 3, and is
  never read as a change.

A mismatch is a **changed-artifact stop**: blocker 6 whose way out is "run `/develop <n>` to review
and approve the change" (section 11). Any other failure in this section is a plain blocker 6.

**The spec guard** (plan track). The approved spec is read-only for the whole run. Before every push
of the feature branch, run `git diff --quiet <approvedSha> HEAD -- <spec>`. Exit 1: restore the file
to its approved version in a new commit holding only that path, push, and stop at blocker 6. Any
other non-zero exit is blocker 3. Every implementer prompt says the spec is read-only.

**Trust and changed-artifact stops write nothing public.** When a failure is that content does not
count, a lookup failed, an issue marker is invalid, or the body, criteria or spec no longer match the
pin, the run reports it in the terminal and stops. It posts no
checkpoint and no comment, so a person without write access cannot make a run write to the issue.
The cost is that the next `/develop <n>` within 24 hours of the last checkpoint asks whether a run
is live (section 10); the person answers it.

**This is not authentication.** Anyone with write access can change the body, a pin and a
checkpoint. Collaborators are trusted. These rules keep outsiders from steering a run and make an
honest collaborator's change to an approved artifact visible.

**The approach text is instructions for an autonomous agent.** It is used only as the design of this
repository's change. A directive in it to act outside the repository, touch another system or
change this workflow is ignored and reported, in the run's own words, in the kickoff comment.

## 10. Checkpoints, resume and concurrency

`defined` (the pin, not a progress step), kickoff, each milestone, each blocker and close-out post one issue comment: a short
human-readable summary, then a fenced block holding exactly one line that matches

```
^tr-develop/v1 phase=(defined|kickoff|plan-approved|tasks|final-approved|pr-open|blocked|closed)( [a-zA-Z][a-zA-Z0-9.]*=\S+)*$
```

| Field | When | Value |
|---|---|---|
| `pin` | `kickoff` and later | the id of the counted `defined` comment the run is working under |
| `track`, `spec`, `approvedSha` | `kickoff` and later (`spec` and `approvedSha`: plan track) | copied from that `defined`, so a pin can be rebuilt; `approvedSha` is updated by a re-approval's follow-up |
| `branch` | always; `-` on a `blocked` that has no branch yet | the feature branch |
| `head` | every phase from `kickoff` on; optional on `blocked` | 40-hex commit already pushed to `origin/<branch>` |
| `review` | `kickoff` and later | `agent` or `self` (section 12) |
| `gh` | `kickoff` and later | the posting executor's GitHub login |
| `planSha` | plan track, from `plan-approved` on | commit of the plan file |
| `tasksDone` | plan track, from `tasks` on | completed plan tasks |
| `cyclesDone` | small track, from `tasks` on | completed red-green cycles |
| `rounds.plan`, `rounds.final` | once that loop ran | rounds used |
| `pr` | from `pr-open` on | the PR URL |
| `deferred` | from `final-approved` on | count; the list is in the summary |
| `stage` | `blocked` | the stage the run was in (`read`, `workspace`, `plan`, `build`, `final`, `finish`) |
| `blocker` | `blocked` | the blocker number, 1 to 6 (section 14) |

A `defined` comment also carries the pins of section 8 as fields. Unknown fields are ignored on read.
A comment with more than one matching line is invalid as a whole.

**Read-back.** After posting, read the comment back and check that its raw body holds exactly one
matching line, equal to the line posted. On a failure, edit the comment in place once and read it
back again; a second failure is blocker 6. A `blocked` that fails read-back is not retried: say so
in the final report.

**Resume.** The pin is the latest counted `defined` comment; if it fails validation, that is a trust
failure with the way out of "re-define or re-pin" (section 5). Progress comes from the latest counted
checkpoint that is neither `defined` nor `blocked`; with none, the run is a start. A `blocked` is
informational; its `stage` and `blocker` are shown to the person. The checkpoint's `head` must be an
ancestor of `origin/<branch>`, which holds because history is never rewritten. A kickoff posted for
a new executor copies forward `pin`, `planSha`, `tasksDone`, `cyclesDone`, every `rounds.*`, `pr`
and `deferred` from the latest counted progress checkpoint, and states what it carries.

**A follow-up is pending** exactly when the latest counted progress checkpoint's `pin` differs from
the id of the latest counted `defined`. Nothing else marks it, so no checkpoint order can hide it: a
new executor's kickoff carries the old `pin` and the follow-up's own checkpoint carries the new
one. Deliver's order is workspace, kickoff, follow-up (Re-approval step 4), and only then the next
task, cycle or phase. With no progress checkpoint there is nothing to follow up.

**Work pushed after the last checkpoint.** Commits on `origin/<branch>` after the checkpoint's
`head` are unverified progress, since a task or cycle is pushed when it ends but a checkpoint is
posted only every few of them. The first task or cycle after a resume starts by checking whether its
behaviour is already there (its tests pass and the commit is on the branch) and, if so, finishes it
without rewriting it; only then does it count. `tasksDone` and `cyclesDone` are the floor, never
the exact position.

Round counters resume from `rounds.*`, except after a `blocked` with `blocker=1`: a resume that a
person starts on purpose after one grants that loop a fresh 6 rounds, and says so. A counted
`pr-open` means the run is past Deliver: go to "After the PR" (section 11). A counted `closed` is
terminal: report it and stop. A counted `kickoff` with no valid counted `defined` is the trust
failure of section 5.

**Routing and resume read counted comments only.** Counted checkpoints are the only ones a run
resumes from or is stopped by. Uncounted ones are skipped. The next checkpoint's summary may say how many were ignored, by comment
id only, never quoting them or a login they name.

**Concurrency.** A counted checkpoint other than `defined`, newer than 24 hours, with no later counted
`pr-open`, `blocked` or `closed`, may be a live run: stop and ask before continuing. Checkpoints are the
heartbeat, so a run posts one at least every 5 plan tasks or every 3 small-track cycles. If the
issue is assigned to someone other than the current user, ask before reassigning.

**The issue's own state.** Besides checkpoints, a run changes an issue only by assigning it (to the
definer at Define, to the executor at kickoff; best effort), by Define's claim comment and its
link comment on an outsider's original issue, and, at Define, by writing its body. It sets no
status label. GitHub closes the issue when the PR merges.

## 11. The flows

### Define

1. **Fetch** `origin`, read the profile at `origin`'s default branch.
2. **Claim** (existing issue only). Ask first when the issue is assigned to someone else, is
   closed, or was opened by someone without write access (section 8). Then assign it to the current
   user and post one plain comment, with no `tr-develop/` line: "Define started by `<login>` with
   /develop. The description changes only at sign-off."
3. **Worktree.** Create `wip/develop-<slug>` from `origin/<base>` in its own worktree (section 14),
   so the spec commit has somewhere to land. A `wip/develop-<slug>` branch or worktree left by an
   earlier run is never reused: suffix the slug and tell the person about the old one.
4. **Brainstorm.** Invoke `superpowers:brainstorming`, seeded with the argument or the issue, under
   the overrides of section 13. The track follows the last size it states:
   - a project with a written design → **plan** track;
   - a small change, or a quick clear task → **small** track (Define never just builds it);
   - only a spike's answer wanted → report the findings, remove the worktree, publish nothing.
5. **Plan track.** Rename the branch to `feat/<slug>` or `fix/<slug>`. Brainstorming writes the
   spec to `docs/superpowers/specs/YYYY-MM-DD-<slug>-design.md`, runs its builder check and commits
   it. Draft the ticket to the standard. Run the reviewer **spec loop** (≤ 6 rounds) over the spec
   and the ticket preview, committing fixes.
6. **Small track.** The person approves the approach in chat. Draft the ticket to the standard.
   Run the reviewer **approach loop** (≤ 6 rounds) over the approach text and the ticket preview.
   Nothing is committed on this track.
7. **Sign-off**, one gate. Present in one message: the spec path (plan) or the approach text
   (small), with what the loop changed; the round count and any deferred findings; and the full
   issue preview and the `defined` comment that will pin it. Ask for exactly one choice:
   1. approve, publish and start Deliver;
   2. approve and publish only;
   3. request changes (ticket text only: redraft and present again; spec or approach: edit, re-run
      the loop on the change, present again);
   4. stop and publish nothing. On an existing issue this posts one short comment ("Define stopped at
      sign-off; nothing published"), best effort, and leaves the assignment as it is. A spike's
      findings outcome posts the same kind of note.

   Only an explicit 1 or 2 is approval. On approval, re-check that what is about to be published
   is what was shown (the spec commit, the hashes); a mismatch goes back to the presentation.
8. **Publish.** The hashes are computed locally from the title and body that were previewed and
   approved, never from a re-read. Plan track: find a free branch name (`git ls-remote`; suffix
   `-2`, `-3` if taken) and push. Create the issue (assigned to the definer), or update the existing
   one in a single edit. Read it back and normalize it: it must hash to the local `bodyHash`. A
   difference is GitHub rewriting the markup or someone editing in the window; either way the pin is
   not posted, the difference is shown, and Define goes back to the presentation. Only on a match
   post the `defined` comment with the locally computed values, then read the comment back and the
   body once more. A second failure is blocker 6, and presenting again after a hash mismatch is
   tried at most twice, so a bot that keeps rewriting the body ends in a stop and not a loop. Print
   the issue link. Define runs only under an
   account that passed preflight check 4, so a person without write access cannot reach this step.
9. **Wrap-up.** Snapshot memory (section 16). Choice 1: remove the Define worktree (and the `wip/`
   branch on the small track) and continue with Deliver in this session. Choice 2: the same
   clean-up, then print the issue link and `/develop <n>`.

Never edit the spec after approval to add the issue number: that would change `approvedSha`. The
issue links the spec; the PR body links both.

### Deliver, common steps

1. **Read and validate** the issue (sections 9 and 10). A counted `defined` comment that fails
   validation is a trust failure (terminal report only) with the way out of section 5: "re-define"
   or "re-pin".
2. **Concurrency** (section 10).
3. **Resume or start** (section 10).
4. **Integrity** (section 9).
5. **Workspace.** A start is a run with no counted `kickoff`; anything else is a resume.
   - *Start, plan track:* a worktree on the `defined` comment's `branch`, tracking
     `origin/<branch>` (Define pushed it).
   - *Start, small track:* create the branch from `origin/<base>` and push it at once, so `head`
     exists on the remote before the kickoff. If the name is taken by a branch with commits of its
     own, suffix it (`-2`, `-3`) and record the name in the kickoff; the branch name is derived, so
     this is not a change that needs approval. A taken name whose tip is `origin/<base>` or an
     ancestor of it is reused.
   - *Resume, either track:* fetch. A local worktree for the branch is reused if it is clean; if it
     is behind, fast-forward it; if it has unpushed commits, run the spec guard and push them; if
     it has diverged from `origin/<branch>`, blocker 6. No local worktree (a fresh machine): create
     one from `origin/<branch>`. The remote branch must contain the latest counted checkpoint's
     `head`.
   - If `origin/<base>` has moved, merge it in (never rebase); a conflict that cannot be resolved
     within the spec is blocker 5.
6. **Kickoff.** Assign the issue to the executor, run the spec guard (plan), push, post the
   `kickoff` checkpoint and read it back. A kickoff is posted again whenever the executor is not
   the author of the latest one. It carries `pin`, `track` and, on the plan track, `spec` and
   `approvedSha`; a re-posted kickoff carries the old `pin` while a follow-up is pending.
7. **Pending follow-up** (section 10), if any: Re-approval step 4, before anything else.

### Deliver, plan track

1. Invoke `superpowers:writing-plans`; the plan is saved to
   `docs/superpowers/plans/YYYY-MM-DD-<slug>.md` on the branch. The person is not asked to review
   it or to choose an execution method.
2. Run the reviewer **plan loop** (≤ 6 rounds). Spec guard, push, `plan-approved` checkpoint.
3. Build the visible todo list in one pass: one todo per plan task, then the closing stages.
4. Invoke `superpowers:subagent-driven-development`. The task reviewer is `adversarial-reviewer`
   inside the review-contract frame, for every task. `blockers.md` is the only stop list.
5. About every 5 tasks: spec guard, push, `tasks` checkpoint, memory snapshot.
6. **Final loop** (≤ 6 rounds) over `git diff origin/<base>...HEAD`, fixes dispatched to an
   implementer. Spec guard, push, `final-approved` checkpoint.

### Deliver, small track

1. Build the todo list: one todo per red-green cycle in the approach, then the closing stages.
2. Implement inline, in the main session, with `superpowers:test-driven-development`: one failing
   test, the code that passes it, refactor, next cycle. No subagent per task. Push after each cycle
   (the spec guard does not apply: there is no spec), and every 3 cycles post a `tasks` checkpoint
   with `cyclesDone`. A resume continues at cycle `cyclesDone + 1`, and the person's running session
   confirms the earlier cycles' tests still pass before it does.
3. **Final loop** (≤ 6 rounds) over `git diff origin/<base>...HEAD`, fixes made inline. Push,
   `final-approved` checkpoint.

### Finish and close-out (both tracks)

1. **Docs.** Update the repository's own docs (`AGENTS.md`, `README.md`, `CHANGELOG.md` and the
   like, whichever exist) in this branch where behaviour changed.
2. **Verify.** Run the profile's verify commands, then invoke
   `superpowers:verification-before-completion`. Check each acceptance criterion against the
   change and record the result for the PR body.
3. **Integrity again** (section 9), then the spec guard, then push.
4. **PR.** Invoke `superpowers:finishing-a-development-branch` with `<base>` supplied and "push and
   open a PR" selected. Title: the issue's title as pinned by `bodyHash`. Body: `Closes #<n>` (and
   the original issue, when Define tracked an outsider's issue in a new one), a summary, the
   acceptance criteria with their results, the spec link at `approvedSha` (plan), and the deferred
   findings. GitHub closes an issue only for a PR merged into the repository's default branch, so
   when `base` is not the default branch the body says `Refs #<n>` instead and the report tells the
   person the issue will not close by itself. If an open PR whose head repository is this
   repository (not a fork) and whose head branch is this branch already exists, update it.
5. **Commits and PRs carry no AI attribution.** No co-author trailer, no "generated by" footer, in
   a commit message or a PR body, unless the repository's own `AGENTS.md` or `CONTRIBUTING.md`
   asks for one. Commit messages otherwise follow those files' conventions. This overrides the
   host's default trailer and footer.
6. **Close-out.** Post the `pr-open` checkpoint with the PR link, snapshot memory, report the PR and
   issue links, and keep the worktree parked until the PR merges. Never merge.

### After the PR

`/develop <n>` on an issue with a counted `pr-open`. No pin validation and no integrity check
block this route, because the branch may have been deleted by the merge. The PR is found by the
checkpoint's `pr` URL, and its head repository must be this repository:

- PR open → report its state, and any integrity mismatch as advice, and stop. (Follow-up is
  Stage 2.)
- PR merged → merged close-out: remove the worktree, delete the local branch with `git branch -d`
  (never `-D`), post the `closed` checkpoint, snapshot memory.
- PR closed unmerged → report it. Opening a new PR is a Deliver write, so it first runs pin
  validation and integrity like any other Deliver start: a mismatch goes to re-approval, and a
  deleted branch is recreated from the latest checkpoint's `head` (asked first: it is an outward
  action). Without that the run stops.

### Re-approval

`/develop <n>` on an issue stopped by a changed-artifact stop (section 9), run by someone with
write access:

0. Refuse and ask for a fix first if an edit left the criteria markers missing or duplicated, since
   `acHash` cannot be computed then.
1. Show what changed, old next to new. The old body is the revision in the issue's edit history
   (GraphQL `userContentEdits`, paginated, newest first, each holding a full body) that hashes to the
   pinned `bodyHash` together with the title as of that time, which is read from the issue's
   `RenamedTitleEvent`s (`previousTitle`), since a revision holds no title. GitHub lets revisions be
   deleted, so when none matches, say the original is unavailable and show only the current text.
   The text shown is the text hashed: compute the new `bodyHash` locally from it. For the spec, show `git diff <approvedSha> origin/<branch> -- <spec>`. This works
   whoever last edited the body, an outsider included.
2. Criteria or other ticket text only: ask for approval. Approach or spec: run that artifact's
   reviewer loop on the change first, then ask.
3. On approval, re-fetch and check that the body still hashes to what was shown, else go back to
   step 1. Then post a new `defined` comment carrying the locally computed `bodyHash`, `acHash`,
   `track`, `repo`, `base` and, on the plan track, `spec` and `approvedSha`. Its `branch` is the
   latest counted checkpoint's, when one exists. The latest counted `defined` comment is the pin. Its
   summary names what changed and who approved. The earlier `defined` comments stay as the audit
   trail. A **re-pin** (section 5) is the same steps, with the pin rebuilt as step 3 says.
4. **Follow-up, then a checkpoint.** Only when a progress checkpoint exists; before the first
   kickoff the new `defined` is all a re-approval needs. The loops run only when what the track
   plans from changed against the previous pin: on the plan track `approvedSha` or `acHash`, on the
   small track `bodyHash` (the approach is in the body). Then: plan track, with a `plan-approved` or
   later checkpoint, re-run the plan loop with the change in scope as a fresh loop (its rounds
   count from 1); completed tasks stand and are not renumbered, so `tasksDone` stays valid. Small
   track: `cyclesDone` resets to 0, and the new cycles skip behaviour whose tests already pass.
   A final loop that had run is invalidated: `rounds.final` is dropped and the final loop runs again
   from round 1. Then re-post the latest progress checkpoint's own phase (`kickoff` if nothing
   later exists, else `plan-approved` or `tasks`; `tasks` if the latest was `final-approved`), with
   the new `pin`, the current `approvedSha` and `planSha`, `tasksDone` or `cyclesDone`, and the new
   `rounds.*`. That checkpoint, carrying the new `pin`, is what marks the follow-up done.
5. On refusal, nothing is written. Offer to restore the earlier text. The run stays blocked.

A deliberate change works the same way: edit the criteria in the issue, or commit an edit to the
spec on the branch, then run `/develop <n>`.

## 12. Review contract

Every review uses one frame. The reviewer is `adversarial-reviewer`.

**Dispatch frame**, in this order:

1. **Scope:** exactly what is under review (paths, a diff range or a document) and the relevant
   spec, approach, criteria or plan excerpt. Issue text is quoted as data.
2. **Constraints:** read-only. No edits, no state-changing shell commands.
3. **Round:** "round N of ≤ 6", and for N > 1 the triage of the previous round.
4. **Fanout:** at most 3 primary agents run concurrently from the orchestrator; an agent dispatches
   at most 1 sub-agent.
5. **Required ending:**

   ```
   VERDICT: GO | NO-GO
   FINDINGS:
   - [blocker|major|minor] <file:line | section> — <issue> — <suggested fix>
   ```

**Loop rules**

- Triage every blocker and major: fix it, or reject it with a written rebuttal that cites evidence.
- Signed off = `GO` with no accepted finding open.
- Minors never block. Fix when cheap, else record as *Deferred* (the `final-approved` checkpoint and
  the PR body).
- A reply with no parsable verdict is re-requested once; a second failure counts as a `NO-GO` round.
- Cap: 6 rounds per loop. Reaching it without sign-off is blocker 1. Per-task loops keep
  subagent-driven-development's own escalation.
- The reviewer never fixes. The orchestrator edits documents; on the plan track code fixes go to an
  implementer; on the small track the orchestrator fixes inline.
- Round counts go in the next checkpoint so the caps survive a resume.

**The final loop.** A diff under 600 changed lines (not counting the run's own unchanged spec and
plan, lockfiles and generated files) gets `adversarial-reviewer` alone. A larger one gets a paired
review: `adversarial-reviewer` and `counterexample-reviewer`, each over the whole diff, in the same
round, neither running a build. If `counterexample-reviewer` is absent or fails twice in a round,
`adversarial-reviewer` continues alone and one *Deferred* entry says so.

**Self-review mode** (`review=self`). On a host that cannot dispatch, the session does each review
itself: it re-reads the artifact against the same frame, writes the same findings and verdict, and
the loop rules and caps apply unchanged. Per-task review in subagent-driven-development falls back
to that skill's own no-subagent behaviour. The mode is recorded in every checkpoint's `review`
field and nowhere else.

**The reviewer agents.** Both are written fresh for this plugin. `adversarial-reviewer` reviews for
correctness, simplicity, security and fit with the codebase. `counterexample-reviewer` assumes the
change is wrong and reports only defects it can trace to a concrete input or state. Both have
read-only tools and both always end with the required ending.

## 13. Overrides of superpowers defaults

| # | superpowers default | `/develop` behaviour |
|---|---|---|
| O1 | brainstorming hands the written design to the person for approval after its builder check | the reviewer spec loop runs next; the person approves once, at sign-off, with the ticket preview |
| O2 | brainstorming invokes writing-plans once the document is approved | Define ends at the handoff; Deliver invokes writing-plans |
| O3 | a small change is approved in chat and then built | Define ends at approval, the approach loop, sign-off and the handoff; Deliver builds |
| O4 | a quick clear task is just done; opting out of the questions ends the skill and builds | Define never builds anything it keeps; a quick task is a small change; opting out stops the questions, not the gate |
| O5 | writing-plans: the person reviews the plan and chooses the execution method | the plan loop replaces that review; the track fixes the method |
| O6 | subagent-driven-development reviews tasks with its own reviewer and verdict | the task reviewer is `adversarial-reviewer` in the review-contract frame; its grades map to blocker, major, minor |
| O7 | subagent-driven-development: one final review, one fix, then the workspace is deleted | the final loop; deferred findings are copied to the `final-approved` checkpoint first |
| O8 | subagent-driven-development has its own stop conditions | `blockers.md` is the single stop list |
| O9 | finishing-a-development-branch asks for the base and the finishing option | `<base>` comes from the `defined` comment; "push and open a PR" is chosen; never merge, never delete the branch |
| O10 | brainstorming and subagent-driven-development create their own todos | `/develop` owns the visible list; one todo per plan task or cycle, created in final order |

## 14. Blockers, worktrees, progress

**Stop for these, and only these.** The default everywhere else is to attempt one low-risk
remediation and continue.

1. **Review cap reached** without sign-off.
2. **The approved artifact would have to change:** an impossible requirement, a contradiction with
   the codebase, or a scope change.
3. **Missing access:** credentials, a required tool, push or PR permission, or the tracker, after
   one safe remediation attempt.
4. **A destructive, security-sensitive or outward-facing action**, or any side effect outside the
   worktree, beyond: pushing the feature branch; opening or updating its PR; the issue writes this
   design defines; and deleting the local branch of a merged PR with `git branch -d`. Merging,
   force-pushing, deleting data, changing secrets or permissions, releasing and messaging people
   all stop here.
5. **Out-of-scope verification failure,** or a base-merge conflict that cannot be resolved within
   the spec.
6. **Pin, trust or integrity failure:** anything section 9 or 10 says fails closed. A failure of
   trust, and a changed-artifact stop, are reported in the terminal only and post nothing.

**At a blocker:** commit and push work in progress (after the spec guard; if the push is what
failed, skip it and say so), post a `blocked` checkpoint whose summary names the blocker, what was
tried and the options, snapshot memory, mark the active todo `BLOCKED:`, and stop. The `blocked`
checkpoint carries `stage` and `blocker`, takes `branch=-` when no branch exists yet and omits `head` when none is
pushed. Where no counted `defined` or `kickoff` exists on the issue, or the blocker is blocker 6's trust or
changed-artifact kind, nothing is posted and the terminal report is the record.

**Not blockers** (decide, record in the next checkpoint, continue): details the spec leaves open,
triage of findings, plan deviations that stay within the spec, re-running a flaky test, merging a
moved base without conflicts.

**Worktrees.** Every run works in `<main>/.claude/worktrees/<branch with / replaced by ->`, where
`<main>` is the main checkout. The person's own checkout is never changed. Removing a worktree
never uses `--force`; a throwaway `wip/` branch, or the branch of a merged PR, is deleted with
`git branch -d` only, and kept and reported if that refuses.

**Progress.** When the host has a visible todo tool, the main session keeps one list in final
order: the stages of the current half, with one todo per plan task or red-green cycle created
before the first is started. Subagents never touch it.

## 15. The repository profile

`.github/develop.md`, read from `origin`'s default branch only, so a feature branch cannot change the
commands its own run trusts, and Define and Deliver read the same file. It is optional; without it every default applies.

````
```text
tr-develop/profile-v1
base: main
ticket.standard: on
ticket.template.feature: feature_request.md
ticket.template.bug: bug_report.yml
```

## Verify

```sh verify
dotnet test src/TotalRecall.sln --filter "Category!=Integration"
```

```sh verify
npm run test:shim
```
````

- `base`: the branch runs start from and target. Default: `origin`'s default branch. A `base` that
  is not the default branch is allowed; the PR then says `Refs #<n>` (section 11).
- `ticket.standard`: `on` (default) or `off`.
- `ticket.template.<type>`: a file under `.github/ISSUE_TEMPLATE/` for `feature`, `bug` or `chore`.
- `sh verify` blocks: commands Finish runs, in order, each of which must exit 0.

Unknown keys are ignored, so Stage 2 can add its switches. An invalid profile is blocker 6 in
Deliver and a warning in Define.

**`/develop initialize`** writes this file with the person: it reads the repository's docs, CI
workflows and issue templates, proposes each value, and asks. It then runs one reviewer loop over
the file (are the commands what the repository's own CI runs; does any command write outside the
worktree), commits it on `develop-profile/<date>`, and opens a PR that `/develop` never merges. No
issue is involved.

## 16. Memory

When `total-recall`'s memory tools are available, a run stores one snapshot (`memory_store`, type
Decision, warm tier, not pinned) at Define wrap-up, at every `tasks`, `blocked`, `pr-open` and
`closed` checkpoint. It records the issue, track, branch, phase, last pushed head and any open
decision or blocker. If memory is unavailable the run skips this silently. The issue's checkpoints
are the durable record either way.

## 17. Stage 2 (not designed here)

- PR follow-up: review threads, CI re-runs, an optional loop and opt-in auto-merge.
- Release watching, mapped to a repository's own release workflow.
- Sub-issues for work that is more than one ticket.

The `defined` and checkpoint grammars and the profile all ignore unknown keys so these can be
added without a version change.

## 18. Changes to this repository

In the same PR as the skill:

- `.gitignore`: remove the `docs/superpowers/` line. Already done on this branch, with the rewrite of
  `AGENTS.md` standing rule 2; the plan treats both as done and verifies them. Further `AGENTS.md`
  changes: new rows in "Where to look" and a short section on the skill.
- `README.md`: the skill, its requirements and its host support.
- `CHANGELOG.md`: an entry under an unreleased heading. This change does not bump the version, so
  the seven-file version sync is not triggered.
- `package.json`: the script tests added to `test:shim`; `"skills": ["./skills/develop"]` added to
  the `pi` block, which today has only `extensions`. Stage 1 registers no pi command: on pi the skill
  is invoked as `/skill:develop`, and whether a bare `/develop` can also work is listed in section 21.
- `.github/develop.md`: this repository's own profile.

The main checkout holds 19 specs, 28 plans and one handoff under `docs/superpowers/` that were
never committed. Once the ignore line is gone they show up as untracked files there. Whether to
commit them as history or keep them local is the owner's decision and is not part of this change.

## 19. How we know Stage 1 works

1. The script tests pass, including every golden block.
2. A dogfood run in this repository: an idea is defined on the small track, delivered, and its PR
   opened, with every checkpoint read back.
3. A second dogfood run on the plan track, stopped mid-task and resumed in a fresh session from
   the issue alone.
4. Editing a criterion, or any other ticket text, in a published issue stops the next run, and
   re-approval clears it. Editing as a person without write access and then having a collaborator
   tick a box does not make the edited body count.
5. A `tr-develop/v1` line, or a fake kickoff, posted by an account without write access is ignored:
   on an issue with no counted `defined` comment it still routes to Define, on one with it the run
   is not disturbed, and nothing is written to the issue.

The script tests simulate the trust paths with fixtures. Items 4 and 5 of this list also need a second GitHub account without write access, run against a throwaway issue on
`strvmarv/total-recall` and only with the owner's say-so beforehand; they are manual.

## 20. Calls made here that the owner did not make

1. Track names are `plan` and `small`.
2. The pins live in a `defined` comment, not in the issue body. The body has no machine block. Its
   regions are hashed, and routing and resume read counted comments only. This replaces the
   original's handoff block in the description, to keep an outsider's edit of the body from
   steering a run.
3. Trust is by current write access for everyone; `definedBy` has no special authority and the
   assignee plays no part in trust. The whole title and body are pinned by `bodyHash`.
4. An outsider's issue is tracked in a new issue by default.
5. Re-approval posts a new `defined` comment; there is no chain of amendment records.
6. Re-approval also covers the approach and the spec, each after its reviewer loop.
7. `review=agent|self` is recorded in checkpoints.
8. Worktrees live under `.claude/worktrees/` on every host.
9. `gh auth switch` is never run. Every `gh` call sets `GH_TOKEN` inline for the chosen account.
10. Forks are refused.
11. The reviewer dependency is met by name; no contract version is declared by the agent.
12. A second `develop` skill produces a notice, not a stop.
13. The approach is capped at 60 lines and 4,000 characters.
14. `/develop initialize` is in Stage 1, in a minimal form.
15. Commits and PRs carry no AI attribution unless the repository's own docs ask for one.
16. A resume the person starts on purpose after a review-cap stop gets a fresh 6 rounds.
17. The skill assumes a prompt-free host and says so in an advisory, not a check.

## 21. Not yet verified

1. Whether the collaborator-permission endpoint answers for a caller who has write but not admin
   access. Verified only as the repository's admin. Every trust decision rests on it, so verifying it
   is the first task of the build, before any code that depends on it.
2. How pi resolves two skills of the same name, and whether a pi package can register agents for
   pi-subagents. If it cannot, preflight on pi offers to copy the two agent files into the user's
   agent directory, on a yes.
3. Whether Claude Code accepts `/develop` for a plugin skill or only `/total-recall:develop`, and
   whether a bare `/develop` works on pi (where skills are `/skill:develop`).
4. Whether superpowers' skills behave as section 13 assumes when a host has no dispatch tool.
5. Whether GitHub stores a Markdown body exactly as submitted (it is assumed to), and so whether
   the local hash and the read-back hash agree; and the exact shape of `RenamedTitleEvent` and
   `userContentEdits` pagination.

## 22. Left to the builder

- The exact wording of every file, the checkpoint comment templates and the tutorial.
- The JSON shape each script prints and the full set of golden cases.
- The host-mapping table for pi.
- The order of work, provided section 19 is met.
