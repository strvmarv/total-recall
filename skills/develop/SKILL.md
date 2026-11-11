---
name: develop
description: Use when defining a change or delivering one from a GitHub issue, via /develop with an issue number, an issue URL, or a description of the change.
---

## Routing

```json golden:routing
[
  ["123", {"route": "issue", "number": 123}],
  [" 123 ", {"route": "issue", "number": 123}],
  ["#123", {"route": "issue", "number": 123}],
  ["007", {"route": "define", "seed": "007"}],
  ["0", {"route": "define", "seed": "0"}],
  ["123; rm -rf /", {"route": "define", "seed": "123; rm -rf /"}],
  ["fix 123", {"route": "define", "seed": "fix 123"}],
  ["9007199254740993", {"route": "define", "seed": "9007199254740993"}],
  ["strvmarv/total-recall#9", {"route": "issue", "number": 9}],
  ["other/repo#9", {"route": "wrong-repo", "repo": "other/repo", "number": 9}],
  ["https://github.com/strvmarv/total-recall/issues/7?x=1#c", {"route": "issue", "number": 7}],
  ["https://github.com/strvmarv/total-recall/pull/7", {"route": "pull", "number": 7}],
  ["https://example.com/strvmarv/total-recall/issues/7", {"route": "define", "seed": "https://example.com/strvmarv/total-recall/issues/7"}],
  ["", {"route": "tutorial"}],
  ["   ", {"route": "tutorial"}],
  ["help", {"route": "tutorial"}],
  ["HELP", {"route": "tutorial"}],
  ["-h", {"route": "tutorial"}],
  ["?", {"route": "tutorial"}],
  ["--HELP", {"route": "tutorial"}],
  ["help me fix the cache", {"route": "define", "seed": "help me fix the cache"}],
  ["initialize", {"route": "initialize"}],
  [" Initialize ", {"route": "initialize"}],
  ["initialize the cache", {"route": "define", "seed": "initialize the cache"}],
  ["add a retry to the sync command", {"route": "define", "seed": "add a retry to the sync command"}]
]
```

## Preflight

```json golden:pinned-commands
[
  {
    "shell": "bash", "tool": "gh", "login": "strvmarv", "args": ["api", "user"],
    "expect": "t=$(gh auth token --user strvmarv) && [ -n \"$t\" ] && GH_TOKEN=\"$t\" gh api user"
  },
  {
    "shell": "bash", "tool": "git", "login": "strvmarv", "args": ["api", "user"],
    "expect": "t=$(gh auth token --user strvmarv) && [ -n \"$t\" ] && GH_TOKEN=\"$t\" GIT_TERMINAL_PROMPT=0 git -c credential.helper= -c 'credential.helper=!gh auth git-credential' api user"
  },
  {
    "shell": "powershell", "tool": "gh", "login": "strvmarv", "args": ["api", "user"],
    "expect": "$e = 1; $p = $env:GH_TOKEN; try { $env:GH_TOKEN = (gh auth token --user strvmarv); if (-not $env:GH_TOKEN) { throw 'no token' }; gh 'api' 'user'; $e = $LASTEXITCODE } finally { $env:GH_TOKEN = $p }; exit $e"
  },
  {
    "shell": "powershell", "tool": "git", "login": "strvmarv", "args": ["api", "user"],
    "expect": "$e = 1; $p = $env:GH_TOKEN; $q = $env:GIT_TERMINAL_PROMPT; try { $env:GH_TOKEN = (gh auth token --user strvmarv); if (-not $env:GH_TOKEN) { throw 'no token' }; $env:GIT_TERMINAL_PROMPT = '0'; git -c credential.helper= -c 'credential.helper=!gh auth git-credential' 'api' 'user'; $e = $LASTEXITCODE } finally { $env:GH_TOKEN = $p; $env:GIT_TERMINAL_PROMPT = $q }; exit $e"
  }
]
```
