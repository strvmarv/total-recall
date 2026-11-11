# develop

`/develop` takes a change from an idea to an open pull request. A GitHub issue is the ticket.

## What it does

1. **Define** (interactive). You and the assistant brainstorm the change. It is written up as a spec
   (a larger change) or a short approach (a small one), reviewed, and published as an issue that meets
   a written ticket standard. A small comment on the issue pins what you approved.
2. **Deliver** (hands-off). From that issue the assistant plans, builds with review at every step,
   verifies each acceptance criterion and opens a PR that closes the issue. Then it stops. It never
   merges, rebases or force-pushes.

A run that stops (a crash, a blocker, a closed laptop) can be resumed by anyone with write access:
run `/develop <n>` again and it picks up from the issue.

## Ways to run it

| You type | What happens |
|---|---|
| `/develop` | prints this page |
| `/develop add a retry to the sync command` | Define, seeded with your text, ends in a published issue |
| `/develop 123` or `/develop #123` | on issue 123: Define if it was never defined, else Deliver, resume, or report its PR |
| `/develop https://github.com/<owner>/<repo>/issues/123` | the same, for an issue in this repository |
| `/develop initialize` | writes this repository's profile (`.github/develop.md`) and opens a PR for it |

A pull request URL is refused: give the issue.

## What the repository needs

- A git repository whose `origin` is on github.com and is not a fork.
- `gh`, logged in to an account with write access to the repository.
- superpowers 7 or later.
- A subagent tool for reviews (Claude Code, or pi with pi-subagents and `ask_user`) and the
  `adversarial-reviewer` agent, shipped with this plugin; on a host without either, the session
  reviews its own work.
- Optional: `.github/develop.md` on the default branch, which sets the base branch, the `gh` account,
  the ticket template and the verify commands the run must pass.

Deliver runs many routine git, `gh` and test commands. The README lists the minimal allowlist, or use
an auto-accept mode.
