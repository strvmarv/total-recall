# total-recall on VS Code

## Install

No stop condition. The install script configures VS Code in `mcp.json` inside the VS Code `User` directory (`~/Library/Application Support/Code/User` on macOS, `%APPDATA%\Code\User` on Windows, `~/.config/Code/User` elsewhere, or `$VSCODE_USER_DIR`), only when that directory exists. VS Code's key is `servers`, not `mcpServers`. When it finds VS Code it also writes Copilot CLI's `~/.copilot/mcp-config.json`.

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

Then read VS Code's `mcp.json` and confirm the `total-recall` entry under `servers`. If it is missing, take step 1 of path 2 (that step only; do not run the skip-form install again).

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then each step below is its own choice for the user; the agent takes the ones the user accepts.

1. **Ask first**: add the entry to `servers` in VS Code's `mcp.json`, creating the file if it is missing.
```json
{
  "servers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```
2. **Ask first**, optional: for Copilot CLI, add the entry the install script also writes to `mcpServers` in `~/.copilot/mcp-config.json`.
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

Add only the items above and keep the rest of the file. On Path 1, and for a fallback step after it, an `npx` entry that the install script just wrote is accepted. Any other `total-recall` entry that is already there, and any existing entry on Path 2 proper (the script did not run), means stop and ask the user.

Nothing on either path tells the model to call `session_start`; see User steps.

## User steps

1. Restart VS Code so it reads `mcp.json`.
2. This is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Then the configured check described under Install (the entry under `servers`).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server in VS Code.

## Known gaps

- No hooks, no pinned-directive floor and no startup instructions: MCP tools only (from `AGENTS.md`). The instructions are a user step.
- The plain install writes the entries with `npx --yes @strvmarv/total-recall`, with no version (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).

## Evidence

Read from `scripts/install-vscode.js` and `AGENTS.md`; nothing was run on VS Code.
