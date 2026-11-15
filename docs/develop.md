# The `develop` skill

The plugin ships a `develop` skill (`/develop`) that takes a change from an idea to an open pull request, with a GitHub issue as the ticket.

- **Define** is interactive: you and the model brainstorm, agree a spec (or a short approach), have it reviewed, and publish a GitHub issue that meets the ticket standard. A `defined` comment pins the issue body the run will build.
- **Deliver** is hands-off: from that issue it writes a plan, builds with review at every step, verifies, and opens a PR that closes the issue. It then stops. It never merges, rebases or force-pushes, and it works in its own git worktree, so your checkout is untouched.

`/develop 123` (or an issue URL) delivers an existing issue, `/develop <description>` starts a Define, and `/develop initialize` writes the repository profile with you. On Claude Code the command may be namespaced by the plugin name (for example `/total-recall:develop`); this is unverified.

A fork is refused (`origin` must not be a fork), and a host with no way to ask you a question cannot run Define.

**Requirements**

- The superpowers plugin, version 7 or later (brainstorming, writing-plans, subagent-driven-development, test-driven-development, verification-before-completion, finishing-a-development-branch).
- `gh` 2.48 or later (Define reads every open pull request with `gh api --paginate --slurp`), logged in to a github.com account with write access to the repository, whose `origin` is on github.com.
- Optional: a repository profile, `.github/develop.md`, naming the `gh` account to use, the ticket-standard switch and the commands to run before the PR opens (the format is in `skills/develop/profile.md`; this repository's own profile is the example).
- Claude Code and pi are supported first. On a host without a way to dispatch the two reviewer agents (`adversarial-reviewer`, `counterexample-reviewer`), or where `adversarial-reviewer` is not available, the reviews run in the same session (`review=self`); on pi, Preflight first offers to copy the agent files into pi's global agent directory, never over a file already there.

**Trust.** A run acts on the pin (the latest `defined` or `reapproved` comment) written by someone with write access to the repository, not on whatever the issue body says now. Any other issue text, comment or tool output is treated as data.

**Account pinning.** Every `gh` and network `git` call is pinned to the chosen account on that one command; the skill never runs `gh auth switch`. Both the token lookup and the tool run with `GH_HOST=github.com`, so an Enterprise `GH_HOST` in the environment cannot redirect them. The pinned forms look like this:

```sh
t=$(GH_HOST=github.com gh auth token --user <login>) && [ -n "$t" ] && GH_HOST=github.com GH_TOKEN="$t" gh <args>
t=$(GH_HOST=github.com gh auth token --user <login>) && [ -n "$t" ] && GH_HOST=github.com GH_TOKEN="$t" GIT_TERMINAL_PROMPT=0 git -c credential.helper= -c 'credential.helper=!gh auth git-credential' <args>
```

The PowerShell forms (the skill picks the form that matches the shell it is running in):

```powershell
$e = 1; $p = $env:GH_TOKEN; $h = $env:GH_HOST; try { $env:GH_HOST = 'github.com'; $env:GH_TOKEN = (gh auth token --user <login>); if (-not $env:GH_TOKEN) { throw 'no token' }; gh '<arg>' '<arg>'; $e = $LASTEXITCODE } finally { $env:GH_TOKEN = $p; $env:GH_HOST = $h }; exit $e
$e = 1; $p = $env:GH_TOKEN; $q = $env:GIT_TERMINAL_PROMPT; $h = $env:GH_HOST; try { $env:GH_HOST = 'github.com'; $env:GH_TOKEN = (gh auth token --user <login>); if (-not $env:GH_TOKEN) { throw 'no token' }; $env:GIT_TERMINAL_PROMPT = '0'; git -c credential.helper= -c 'credential.helper=!gh auth git-credential' '<arg>' '<arg>'; $e = $LASTEXITCODE } finally { $env:GH_TOKEN = $p; $env:GIT_TERMINAL_PROMPT = $q; $env:GH_HOST = $h }; exit $e
```

**Minimal tool allowlist.** Deliver runs many routine commands, and a plain `gh *` or `git *` permission pattern may not match the pinned forms above (they begin with `t=$(...)`), so either use an auto-accept mode or allow these:

- the skill's own scripts: `node <skill dir>/scripts/ghpin.js scratch`, `node <skill dir>/scripts/ghpin.js run *`, and the other CLIs in that directory (`node <skill dir>/scripts/*.js *`), where `<skill dir>` is the directory that holds the skill's `SKILL.md` (`ghpin.js run` spawns `gh` or `git` with no shell and applies the pin itself);
- the pinned `gh` and `git` forms shown above, as written;
- the local `git` calls a run makes: `remote get-url origin`, `status`, `diff`, `add`, `commit`, `worktree` (`add`, `list`, `remove`), `branch -d`, `rev-parse`, `check-ref-format`, `show`, `ls-tree`, `merge-base --is-ancestor` and the reachability probe `rev-parse --verify --quiet` (the last four are run through `ghpin.js run`);
- the network `git` calls, `fetch`, `push` and `ls-remote`, which go through `ghpin.js run` or the pinned `git` form above;
- the `sh verify` commands of the repository profile.

## Larger work: parent tickets and sub-tickets

A change that is not small or not independent does not fit one pull request. When Define finds that, it offers two choices: define only the first piece (one ticket, as before), or define the change as a **parent** ticket. It creates a parent only if you pick that.

A parent holds the goal, the decisions every part must follow and an ordered breakdown into parts, in its own spec. It is never built. It is delivered as a docs-only pull request that puts the spec on the base branch and says `Refs #<n>`, never `Closes`, so merging it closes nothing. Each part becomes a **sub-ticket**: an ordinary `/develop` ticket, linked to the parent as a GitHub sub-issue, that goes through Define and Deliver on its own and is reviewed against the parent spec.

1. Define the parent: pick "Define it as a parent", review the spec and its breakdown, and sign off. The preview lists the parts that will become sub-issues. The pin carries `track=parent`.
2. `/develop <parent>` opens the spec pull request. You merge it.
3. `/develop <parent>` again records `spec-merged` and creates one sub-issue per part, in order. Running it twice creates none twice.
4. `/develop <sub-issue>` defines that part against the merged parent spec (it asks first if a part it depends on is not done), then delivers it as usual. Its pull request says `Closes #<n>` and `Part of #<parent>`.
5. `/develop <parent>` shows status. When every part is closed it asks whether to close the parent, and closes it only on a yes.

**What status shows.** For each part: its number and title, its sub-issue and the state of it, the latest checkpoint of an open sub-ticket, and whether it is ready (every part it depends on is closed as completed). It warns about a sub-issue of the parent that has no valid first line, names another parent, or is not in the spec, and reports whether the spec on the base branch still matches the one that was merged. It never edits or deletes an issue and never changes the parent's body. A part closed as not planned counts as settled but not as done. If the parent spec is edited after it merged, the sub-tickets defined earlier warn at Deliver; the skill has no flow to re-approve a merged parent spec.

**Compatibility.** Parent tickets need this version of the skill. A ticket with no parent that passes the size check behaves as before: the only differences are one extra read of its parent link in Define and the new question for a change that fails it. Pins and checkpoints written earlier stay valid. An older version of the skill rejects a parent pin as a trust failure, which is harmless while the parent has no progress. A parent that already has a `pr-open` is read by an older version as an ordinary ticket: it posts `closed` when the spec pull request merges and ignores `spec-merged`. To recover, minimize that `closed` comment (the skill treats a minimized checkpoint as retired), then run `/develop <parent>` with this version.

**What was verified.** On 2026-11-15, against a throwaway private repository: linking a sub-issue with `gh api -X POST repos/<o>/<r>/issues/<parent>/sub_issues -F sub_issue_id=<id>` works when `<id>` is the new issue's numeric `id` (not its number) and is sent as an integer with `-F`; the string form (`-f`) is refused with a 422. Linking the same issue twice is refused with a 422 and changes nothing. Five issues created and linked one after another hit no rate limit. An issue closed by a merged `Closes #<n>` pull request has `state_reason` `completed`, which is what "done" means. `GET .../issues/<n>/parent` answers 404 for an unlinked issue. **Not verified:** a long breakdown. A breakdown is capped at 50 parts, and creating that many sub-issues in a row was not tried, so a secondary rate limit at that size is unknown; a run that is cut short can be run again, because it creates only the parts that have no sub-issue yet. The limit of 100 sub-issues per parent comes from GitHub's documentation, not from a test.
