# total-recall on Cline

## Install

No stop condition. **The entry the install script writes may not be the file Cline reads.** The script writes `cline_mcp_settings.json` in `~/.vscode/` (or `$CLINE_HOME`) whenever that directory exists. The repository's own Cline importer reads Cline's settings from `~/.cline/data/settings/cline_mcp_settings.json` and, for older installs, from VS Code's `globalStorage/saoudrizwan.claude-dev/settings/` (`ClineImporter.cs`). So the written entry is not evidence that Cline is configured, and on both paths below the entry is added through Cline's own command.

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

On both paths the entry is added the same way, by the user (see User steps). The entry:

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## User steps

1. In VS Code, open the Command Palette and run "Cline: MCP Settings".
2. In the file that opens, add the `total-recall` entry above to `mcpServers`, keeping what is already there.
3. Add the startup instructions to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. There is no check the agent can run that the entry is in the file Cline reads; the user's "Cline: MCP Settings" file shows it.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server in Cline.

## Known gaps

- No hooks, no pinned-directive floor and no startup instructions: MCP tools only (from `AGENTS.md`). The instructions are a user step.
- The path mismatch above is a known problem in the install script.
- The plain install writes its entry with `npx --yes @strvmarv/total-recall`, with no version (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).

## Evidence

The manual steps are from the earlier `INSTALL.md`. The script's path is from `scripts/install-cline.js` and the importer's paths from `src/TotalRecall.Infrastructure/Importers/ClineImporter.cs`; neither was run, and Cline itself was not run.
