# total-recall on Windsurf

## Install

No stop condition. The install script configures Windsurf in `~/.codeium/windsurf/` (or `$WINDSURF_HOME`), only when `WINDSURF_HOME` is set or that directory exists; the rules file is skipped when the package is a git checkout.

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

Then read `~/.codeium/windsurf/mcp_config.json` and confirm the `total-recall` entry under `mcpServers`, and check that `~/.codeium/windsurf/rules/total-recall.md` exists. For anything missing, take the matching step of path 2 (that step only; do not run the skip-form install again).

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then each step below is its own choice for the user; the agent takes the ones the user accepts. The installed package is located with `npm root -g` (the package is `<npm root -g>/@strvmarv/total-recall`; not run).

1. **Ask first**: add the MCP entry to `mcpServers` in `~/.codeium/windsurf/mcp_config.json`, creating the file if it is missing.
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```
2. **Ask first**: write the startup instructions as a rules file. Take `<npm root -g>/@strvmarv/total-recall/.kiro/steering/total-recall.md`, drop its YAML front matter (the first `---` block), and save the rest as `~/.codeium/windsurf/rules/total-recall.md`, creating the directory if needed.

Add only the items above and keep the rest of the file. On Path 1, and for a fallback step after it, an `npx` entry that the install script just wrote is accepted. Any other `total-recall` entry that is already there, and any existing entry on Path 2 proper (the script did not run), means stop and ask the user.

## User steps

1. Start a new Windsurf session.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Then the configured check described under Install (the MCP entry and the rules file).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server.
- The startup instructions, as a rules file that tells the model to call `session_start`.

## Known gaps

- No hooks and no pinned-directive floor: Windsurf is MCP tools plus the rules file (from `AGENTS.md`).
- The plain install writes the entry with `npx --yes @strvmarv/total-recall`, with no version (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).

## Evidence

Read from `scripts/install-windsurf.js` and `AGENTS.md`; nothing was run on Windsurf.
