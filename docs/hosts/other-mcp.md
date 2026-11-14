# total-recall on any other MCP client

## Install

No stop condition. This page is for an MCP client that has no page of its own. total-recall is a standard stdio MCP server started by the command `total-recall`; this page gives the tools and nothing else.

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

The form that skips the install script. The install script configures nothing for the client being installed here, so nothing is lost by skipping it:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then add this entry to the client's MCP server configuration. Most clients use a top-level `mcpServers` object; if the client's documentation names a different key or file, use that. Add only this entry and keep the rest of the file. If a `total-recall` entry is already there that this install did not just write, stop and ask the user.

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

Nothing on this path tells the model to call `session_start`; see User steps.

## User steps

1. Restart the client so it reads its MCP configuration.
2. This is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Search the client's MCP configuration for the `total-recall` entry (see Rules in `INSTALL.md`).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP tools. Nothing else: no hooks, no startup instructions, no pinned-directive floor.

## Known gaps

- Anything the client does with MCP servers beyond starting them is up to the client; total-recall has no integration with it.

## Evidence

Not run. The entry is the generic `mcpServers` shape the other pages use; no client was named, so none was observed.
