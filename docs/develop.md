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
