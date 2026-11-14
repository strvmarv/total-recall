# total-recall on Kiro

## Install

No stop condition. The install script configures Kiro through `~/.kiro/settings/mcp.json` and `~/.kiro/steering/total-recall.md` (or under `$KIRO_HOME`), only when `KIRO_HOME` is set or `~/.kiro` exists, and the package is installed under `node_modules` (not from a git checkout). It does **not** copy the hooks; those are per workspace (see User steps).

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

Then search `~/.kiro/settings/mcp.json` for the `total-recall` entry (see Rules in `INSTALL.md`), and check that `~/.kiro/steering/total-recall.md` exists. For anything missing, take the matching step of path 2 (that step only; do not run the skip-form install again).

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then each step below is its own choice for the user; the agent takes the ones the user accepts. The installed package is located with `npm root -g` (the package is `<npm root -g>/@strvmarv/total-recall`; not run).

1. **Ask first**: add the MCP entry to `mcpServers` in `~/.kiro/settings/mcp.json`, creating the file if it is missing. The `cwd` is part of the shape the install script writes:
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall",
      "cwd": "${workspaceFolder}"
    }
  }
}
```
2. **Ask first**: copy `<npm root -g>/@strvmarv/total-recall/.kiro/steering/total-recall.md` whole (with its front matter, which marks it always-included) to `~/.kiro/steering/total-recall.md`, creating the directory if needed.

Add only the items above and keep the rest of the file. On Path 1, and for a fallback step after it, an `npx` entry that the install script just wrote is accepted. Any other `total-recall` entry that is already there, and any existing entry on Path 2 proper (the script did not run), means stop and ask the user.

## User steps

1. Start a new Kiro session.
2. Hooks are per workspace and the install script does not copy them (its header comment says so): to get the floor, the retrieval reminder, the `session_start` call and the capture hook in a workspace, copy the package's `.kiro/hooks/` directory into that workspace's `.kiro/hooks/`. This step is from that comment and was not run.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Then the configured check described under Install (the MCP entry and the steering file).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server.
- The startup instructions, as an `always`-inclusion steering file that tells the model to call `session_start`.
- With the workspace hooks copied: the pinned-directive floor (a `UserPromptSubmit` command hook running `pinned-floor --host kiro`, whose plain-text output Kiro appends to context), a `UserPromptSubmit` agent hook that reminds the model to search memory, a `SessionStart` agent hook that calls `session_start`, and a `Stop` agent hook for capture (all from `.kiro/hooks/` and `AGENTS.md`).

## Known gaps

- Without the workspace hooks there is no floor and no capture hook; only the steering file's instruction remains.
- The token-growth throttle is inactive for Kiro (the payload lacks `transcript_path`); the floor runs on the turn count only.
- The workspace hooks and the plain install's entry run `npx --yes @strvmarv/total-recall`, with no version (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).

## Evidence

Everything here is read from `scripts/install-kiro-hooks.js`, `.kiro/` and `AGENTS.md`; nothing was run on Kiro. The header comment of `scripts/install-kiro-hooks.js` states that hooks are not copied.
