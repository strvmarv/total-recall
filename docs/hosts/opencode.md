# total-recall on OpenCode

## Install

No stop condition. OpenCode registers MCP servers under a top-level `mcp` key, in a different shape from the `mcpServers` block most clients use.

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

The form that skips the install script. The install script configures nothing for OpenCode, so nothing is lost by skipping it here:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then add this entry to OpenCode's config, `~/.config/opencode/opencode.jsonc` (the file used on the machine that produced this page; use the user's own OpenCode config file if it is somewhere else). Add only this entry and keep the rest of the file, including its comments. If a `total-recall` entry is already there that this install did not just write, stop and ask the user.

```json
{
  "mcp": {
    "total-recall": {
      "type": "local",
      "command": ["total-recall"],
      "enabled": true
    }
  }
}
```

Optional: **Ask first**, then copy the package's skills into OpenCode's plugins directory. Run it from the package root (`<npm root -g>/@strvmarv/total-recall`, located with `npm root -g`) or from a checkout. This step is from the earlier OpenCode guide and was not run:

```sh
mkdir -p ~/.opencode/plugins/total-recall
cp -r skills/ ~/.opencode/plugins/total-recall/skills/
```

Nothing on this path tells the model to call `session_start`; see User steps.

## User steps

1. Restart OpenCode so it reads its config.
2. This is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Search OpenCode's config for the `total-recall` entry (see Rules in `INSTALL.md`).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server as a local MCP command.
- With the optional copy, the total-recall skills in OpenCode's plugins directory.

## Known gaps

- This install adds no hook, no pinned-directive floor and no startup instructions: it configures MCP tools only, and the instructions are a user step. Whether OpenCode reads the optional skills copy was not observed.
- The earlier OpenCode guide also described an `npx` entry and a source-checkout entry. They are not carried here: an `npx` entry runs an already-installed copy in preference to fetching the latest release (see [What the install script changes](../npm-postinstall.md#the-npx-entries)), and the checkout entry was not observed in OpenCode's `mcp` form.

## Evidence

Observed on OpenCode 1.16.2: the working config on the machine that produced this page (a machine in use, not a throwaway), `~/.config/opencode/opencode.jsonc`, registers the server under a top-level `mcp` key as `{"type":"local","command":["total-recall"],"enabled":true}`. The install, the skills copy and the restart came from the earlier `.opencode/INSTALL.md` and were not run.
