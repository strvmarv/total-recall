# The repository profile

`.github/develop.md` is the one file a repository uses to tell `/develop` how to run there. It is
optional: without it every default applies. This file is the format reference and the `/develop
initialize` flow; `scripts/profile.js` parses the format, and the cases at the end of Format are the
golden it is tested against.

## Format

The profile is the first fenced `text` block of `.github/develop.md` whose first line is the header
`tr-develop/profile-v1`, followed by `key: value` lines. Each command to run at Finish sits in its own
`sh verify` block. The `## Verify` heading below is a convention only: the parser takes any `sh verify` block in
the document:

````
```text
tr-develop/profile-v1
base: main
ticket.standard: on
ticket.template.feature: feature_request.md
ticket.template.bug: bug_report.yml
gh.account: strvmarv
```

## Verify

```sh verify
dotnet test src/TotalRecall.sln --filter "Category!=Integration"
```

```sh verify
npm run test:shim
```
````

- `base`: the branch runs start from and target. Default: the default branch of `origin`. It must match
  `[A-Za-z0-9][A-Za-z0-9._/-]*`. A `base` that is not the default branch is allowed; the PR then says `Refs #<n>` ([Commits and
  PRs](SKILL.md#commits-and-prs)), because GitHub closes an issue only for a PR merged into the default
  branch.
- `gh.account`: the logged-in `gh` account to use for this repository ([Preflight](SKILL.md#preflight),
  check 4). It must have write access, else the run stops, naming it. Optional.
- `ticket.standard`: `on` (default) or `off`. `off` skips the shape and quality rules of the ticket standard
  ([ticket-standard.md](ticket-standard.md#repository-overrides)).
- `ticket.template.<type>`: a file under `.github/ISSUE_TEMPLATE/` for `feature`, `bug` or `chore`. It
  sets the sections of that type's ticket. The value is a bare file name (no path) that starts with a
  letter or digit, continues with letters, digits, `.`, `_` or `-`, and ends `.md`, `.yml` or `.yaml`.
- `sh verify` blocks: the commands Finish runs, in order, each of which must exit 0
  ([Verify blocks](#verify-blocks)).

Unknown keys are ignored, so a later stage can add its own switches. A repeated known key, a value that
fails its pattern (a `gh.account` that is not a login, a `base` that starts with a dash, a template
name that climbs out of its folder, a `ticket.standard` other than `on` or `off`) and an empty `sh
verify` block make the profile invalid. A header with no keys is a valid profile, and so is a file with
CRLF line endings. The parser's output, and so the cases below, are the contract:

```json golden:profile-cases
[
  {
    "name": "full example with gh.account and two verify blocks",
    "markdown": "```text\ntr-develop/profile-v1\nbase: main\nticket.standard: on\nticket.template.feature: feature_request.md\nticket.template.bug: bug_report.yml\ngh.account: strvmarv\n```\n\n## Verify\n\n```sh verify\ndotnet test src/TotalRecall.sln --filter \"Category!=Integration\"\n```\n\n```sh verify\nnpm run test:shim\n```\n",
    "expect": {
      "ok": true,
      "profile": {
        "base": "main",
        "ticketStandard": "on",
        "templates": { "feature": "feature_request.md", "bug": "bug_report.yml" },
        "ghAccount": "strvmarv",
        "verify": ["dotnet test src/TotalRecall.sln --filter \"Category!=Integration\"", "npm run test:shim"]
      }
    }
  },
  {
    "name": "the same full example with CRLF line endings",
    "markdown": "```text\r\ntr-develop/profile-v1\r\nbase: main\r\nticket.standard: on\r\nticket.template.feature: feature_request.md\r\nticket.template.bug: bug_report.yml\r\ngh.account: strvmarv\r\n```\r\n\r\n## Verify\r\n\r\n```sh verify\r\ndotnet test src/TotalRecall.sln --filter \"Category!=Integration\"\r\n```\r\n\r\n```sh verify\r\nnpm run test:shim\r\n```\r\n",
    "expect": {
      "ok": true,
      "profile": {
        "base": "main",
        "ticketStandard": "on",
        "templates": { "feature": "feature_request.md", "bug": "bug_report.yml" },
        "ghAccount": "strvmarv",
        "verify": ["dotnet test src/TotalRecall.sln --filter \"Category!=Integration\"", "npm run test:shim"]
      }
    }
  },
  {
    "name": "minimal block: the header only, every default applies",
    "markdown": "```text\ntr-develop/profile-v1\n```\n",
    "expect": {
      "ok": true,
      "profile": { "base": null, "ticketStandard": "on", "templates": {}, "ghAccount": null, "verify": [] }
    }
  },
  {
    "name": "an unknown key is ignored",
    "markdown": "```text\ntr-develop/profile-v1\nfuture.switch: anything at all\nbase: release/1.x\n```\n",
    "expect": {
      "ok": true,
      "profile": { "base": "release/1.x", "ticketStandard": "on", "templates": {}, "ghAccount": null, "verify": [] }
    }
  },
  {
    "name": "ticket.standard must be on or off",
    "markdown": "```text\ntr-develop/profile-v1\nticket.standard: maybe\n```\n",
    "expect": { "ok": false, "error": "bad-value:ticket.standard" }
  },
  {
    "name": "gh.account must be a login, not a shell fragment",
    "markdown": "```text\ntr-develop/profile-v1\ngh.account: x; rm\n```\n",
    "expect": { "ok": false, "error": "bad-value:gh.account" }
  },
  {
    "name": "base must not start with a dash",
    "markdown": "```text\ntr-develop/profile-v1\nbase: -x\n```\n",
    "expect": { "ok": false, "error": "bad-value:base" }
  },
  {
    "name": "a template name must not climb out of the template folder",
    "markdown": "```text\ntr-develop/profile-v1\nticket.template.bug: ../../etc/passwd\n```\n",
    "expect": { "ok": false, "error": "bad-value:ticket.template.bug" }
  },
  {
    "name": "a repeated known key is rejected, not overridden",
    "markdown": "```text\ntr-develop/profile-v1\nbase: main\nbase: dev\n```\n",
    "expect": { "ok": false, "error": "bad-value:base" }
  },
  {
    "name": "an empty sh verify block is rejected",
    "markdown": "```text\ntr-develop/profile-v1\n```\n\n```sh verify\n\n```\n",
    "expect": { "ok": false, "error": "bad-value:verify" }
  },
  {
    "name": "a document with no profile block",
    "markdown": "# Repository\n\nNo profile here.\n\n```text\nnot a profile\n```\n",
    "expect": { "ok": false, "error": "no-profile" }
  }
]
```

## Loading

Read the profile from `origin`'s default branch only, never from the feature branch or the worktree,
so a feature branch cannot change the commands its own run trusts. Preflight reads it once and Define
and Deliver reuse that read, so both see the same file and agree on `base`. Every command below goes
through `ghpin.js run` (tool `git`, the picked login), which is how its output reaches a file in the
scratch directory. They are local commands, so the account pin does not change their output.

1. **Find it.** Run `["ls-tree","--name-only","origin/<default>","--",".github/develop.md"]` with `out` set to a
   file in the scratch directory. The exit status decides, and no error text is parsed:
   - exit 0 and an empty file: the profile is absent, every default applies and nothing is reported;
   - exit 0 and a non-empty file: the profile is present, go to step 2;
   - any non-zero exit (an unfetched `origin/<default>` ref, any tool trouble): blocker 3
     ([blockers.md](blockers.md#stop-for-these)) in **both** Define and Deliver, after one safe
     remediation attempt (fetch `origin` once, then repeat). What is posted follows
     [At a blocker](blockers.md#at-a-blocker): in Define there is no pin yet, so nothing is posted and the
     report is terminal. A failed read is never read as "absent": `git show` exits 128 both for a missing
     path and for a missing ref, and `ls-tree` exits 128 for a missing ref but 0 with an empty listing for
     a missing path, so only the empty-listing test above says "absent".
2. **Read it.** Run `["show","origin/<default>:.github/develop.md"]` with `out` set to a profile file in the
   scratch directory (a non-zero exit is blocker 3, as above).
3. **Parse it.** Run `node <skill dir>/scripts/profile.js parse <profileFile>`. The output is one JSON
   object: `{ok:true, profile}` with `base`, `ticketStandard`, `templates`, `ghAccount` and `verify`,
   or `{ok:false, error}`:
   - `{ok:false,error:"no-profile"}` (exit 1): the file has no profile block. Every default applies and
     nothing is reported; this is not a failure.
   - `{ok:false,error:"bad-value:<key>"}`: an **invalid profile**. It is a warning in Define (say so once and use the defaults) and blocker 6 in
     Deliver: Deliver never guesses at commands it was told to run.
   - Anything else is tool trouble, not an invalid profile: a profile file that is missing or cannot be
     read (`ENOENT`, `EACCES`), `usage: ...` or `unknown subcommand`. It is blocker 3 in Define and in
     Deliver.
4. `<base>` is the profile's `base`, else the default branch of `origin`.

## Verify blocks

Finish runs the `verify` list in order, from the worktree ([Finish and close-out](deliver.md#finish-and-close-out)).

- Each `sh verify` block is one command: its whole text is passed as the single argument to `sh -e -c`,
  so it stops at the first failing line, and the block must exit 0. Write a multi-line block when
  later lines depend on earlier ones; write separate blocks for independent checks.
- A command that fails outside this change is blocker 5.
- Write each command the way the repository's CI runs it. A quoted-glob `node --test` command (for
  example `node --test "pi/*.test.js"`) needs Node 21 or newer; CI's Node 20 and the package script use
  the directory form (`node --test pi/`). Use the form that works on the Node version the repository's
  CI runs.
- A command must not write outside the worktree (the initialize review asks this of every command).
- `base` is checked with `git check-ref-format --branch <base>` before it is used in `origin/<base>` or
  `origin/<base>...HEAD` ([Common steps](deliver.md#common-steps), step 1); a name that fails the check
  is blocker 6.

## Initialize

`/develop initialize` writes the profile with the person. No issue is involved, so there is no `defined`
comment and no checkpoint. Like every run it works in a worktree ([Worktrees](SKILL.md#worktrees)), cut
from `origin`'s default branch, with every GitHub call pinned to the chosen account ([Preflight](SKILL.md#preflight)).

1. **Read.** Read the repository's own docs (`README.md`, `AGENTS.md`, `CONTRIBUTING.md` and the
   like), its CI workflows (`.github/workflows/`) and its issue templates (`.github/ISSUE_TEMPLATE/`).
   If `.github/develop.md` already exists on the default branch, read it too: the run proposes changes
   to it, not a replacement.
2. **Propose.** Propose each value, with the file it came from, and ask the person about each one in
   turn: `base` (the default branch unless the repository releases from another), `gh.account` (only if
   the repository needs a particular account), `ticket.standard`, a `ticket.template.<type>` for each
   type that has a template, and one `sh verify` block per command the repository's CI runs on a
   change. Offer nothing the repository's own files do not support, and leave a key out when the person
   has no answer.
3. **Write** `.github/develop.md` in the format above and run `profile.js parse` on it: an invalid
   result is fixed before anything else.
4. **Review.** One reviewer loop over the file ([review-contract.md](review-contract.md)), with these
   questions in the scope: are the commands what the repository's own CI runs, and does any command
   write outside the worktree. Triage and re-run until a `GO`, within the 6-round cap. If the loop reaches the cap without sign-off,
   the run stops with a terminal report: there is no issue to post a `blocked` checkpoint to.
5. **Commit and PR.** Commit the file on the branch `develop-profile/<date>` (`<date>` is the day as
   `YYYY-MM-DD`; if that branch exists locally or on origin, suffix it and say so) with a message that
   follows the repository's own conventions. The commit message, the PR title and the PR body carry no
   AI attribution unless the repository's own `AGENTS.md` or `CONTRIBUTING.md` asks for one. Push it and open a PR against the default branch, with the title and body written
   to files and sent from them. The PR is never
   merged: `/develop` stops after opening it, says where it is, and leaves the merge to the person.
   Until it is merged the profile does not exist for any run, since it is read from the default
   branch only.
