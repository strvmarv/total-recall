# Blockers

This is the single stop list. Stop for the blockers below and for nothing else; everywhere else attempt one low-risk remediation and continue.

## Stop for these

1. **Review cap reached** without sign-off.
2. **The approved artifact would have to change:** an impossible requirement, a contradiction with the codebase, or a scope change.
3. **Missing access:** credentials, a required tool, push or PR permission, or the tracker, after one safe remediation attempt.
4. **A destructive, security-sensitive or outward-facing action**, or any side effect outside the worktree, beyond: pushing the feature branch; opening or updating its PR; the issue writes this skill defines; and deleting the local branch of a merged PR with `git branch -d`. Merging, force-pushing, deleting data, changing secrets or permissions, releasing and messaging people all stop here.
5. **Out-of-scope verification failure,** or a base-merge conflict that cannot be resolved within the spec.
6. **Pin, trust or integrity failure:** anything [Trust](handoff.md#trust), [Checkpoints](handoff.md#checkpoints) or [Resume and routing](handoff.md#resume-and-routing) says fails closed. A failure of trust, a failed permission lookup (blocker 3) and a changed-artifact stop with no one to approve it are terminal-only: report them in the terminal and post nothing.

## Decide and continue

These are not blockers. Decide, record the decision in the next checkpoint, and continue:

- details the spec leaves open;
- triage of review findings;
- plan deviations that stay within the spec;
- re-running a flaky test;
- merging a moved base without conflicts.

## At a blocker

Do these in order:

1. Commit and push the work in progress, after the spec guard. If the push is what failed, skip it and say so.
2. Post a `blocked` checkpoint ([Checkpoints](handoff.md#checkpoints)). Its summary names the blocker, what was tried and the options. It carries `stage` and `blocker`; it takes `branch=-` when no branch exists yet and omits `head` when none is pushed. Posting a `blocked`, like a `defined`, `reapproved` or `closed`, skips the integrity check that other checkpoints run.
3. Take a memory snapshot ([Memory](SKILL.md#memory)).
4. Mark the active todo `BLOCKED:` ([Progress](SKILL.md#progress)).
5. Stop.

Post nothing, and make the terminal report the record, when either holds:

- no counted pin or `kickoff` exists on the issue yet;
- the blocker is of the terminal-only kinds in blocker 6: a trust failure, a failed permission lookup, or a changed-artifact stop with no one to approve it.

A blocker raised inside [Re-approval](handoff.md#re-approval) posts `blocked` and pushes none of Re-approval's commits.

Later sessions read the blocker back from `counted.js select`'s `latest`, the latest counted checkpoint of any phase, `blocked` included: show its `stage` and `blocker` to the person. A `blocked` is never progress, so it does not move the position. A resume the person starts on purpose after a `blocked` with `blocker=1` gives that loop a fresh 6 rounds; say so ([Resume and routing](handoff.md#resume-and-routing)). For worktrees and the todo list see [Worktrees](SKILL.md#worktrees) and [Progress](SKILL.md#progress).
