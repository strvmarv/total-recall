# total-recall on Zed

## Install

No stop condition. The install script configures Zed in `settings.json` inside Zed's settings directory (`~/Library/Application Support/Zed` on macOS, `%APPDATA%\Zed` on Windows, `~/.config/zed` elsewhere, or the directory in `$ZED_SETTINGS`), only when `ZED_SETTINGS` is set or the default directory exists. Zed's key is `context_servers`, not `mcpServers`.

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

Then search Zed's `settings.json` for the `total-recall` entry (see Rules in `INSTALL.md`). If it is missing (the install script leaves a file it cannot parse as strict JSON, for example one with comments, untouched), take the step of path 2 (that step only; do not run the skip-form install again).

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then, **Ask first**: add the entry to `context_servers` in Zed's `settings.json`, keeping every comment and setting that is already there.
```json
{
  "context_servers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

Add only this entry and keep the rest of the file. On Path 1, and for a fallback step after it, an `npx` entry that the install script just wrote is accepted. Any other `total-recall` entry that is already there, and any existing entry on Path 2 proper (the script did not run), means stop and ask the user.

Nothing on either path tells the model to call `session_start`; see User steps.

## User steps

1. Restart Zed so it reads `settings.json`.
2. This is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Then the configured check described under Install (the entry under `context_servers`).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server, as a context server.

## Known gaps

- No hooks, no pinned-directive floor and no startup instructions: MCP tools only (from `AGENTS.md`). The instructions are a user step.
- The plain install writes the entry with `npx --yes @strvmarv/total-recall`, with no version (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).

## Evidence

Observed on macOS: `~/Library/Application Support/Zed/settings.json` on the machine that produced this page (a machine in use, not a throwaway) holds a `total-recall` entry `{"command":"npx","args":["--yes","@strvmarv/total-recall"]}` under `context_servers`, which is what `scripts/install-zed.js` writes. The path list, the `context_servers` key and the handling of a file with comments are read from that script and not run; the hand-written entry was not run.
