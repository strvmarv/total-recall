# total-recall on Copilot CLI

## Install

No stop condition. Observed on Windows; macOS and Linux are unobserved.

### Plugin (recommended)

[Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli) installs total-recall as a plugin from the total-recall marketplace. No `mcp-config.json` or hooks edit is needed:

```sh
copilot plugin marketplace add strvmarv/total-recall-marketplace
copilot plugin install total-recall@strvmarv-total-recall-marketplace
```

The marketplace entry is the repository URL with no ref, so the install takes the default branch, which contains `.github/plugin/` and `copilot/`. The install carries no engine binary: the MCP server downloads and verifies it on first launch for the version in that tree's `package.json` (a few seconds), tools return a "not ready" result until it is there, and the pinned-directive floor stays silent until then.

To install from a checkout instead (from the earlier `INSTALL.md`, not run): `copilot plugin marketplace add <path-to-a-marketplace-repo>`, or `copilot --plugin-dir <checkout>` for one session.

### MCP server only (no plugin)

This gives the tools and nothing else: no startup instructions and no floor.

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md). The script can write Copilot CLI's own MCP config (`~/.copilot/mcp-config.json`) when VS Code's user directory exists.

```sh
npm install -g @strvmarv/total-recall
```

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then read `~/.copilot/mcp-config.json` and take the first case that applies:

- After the plain install, the file has a `total-recall` entry that runs `npx --yes @strvmarv/total-recall`: that is the entry the install script writes, so leave it.
- The file has no `total-recall` entry: add the entry below to `mcpServers`, keeping the rest of the file.
- Any other existing `total-recall` entry, or any existing entry after the skip form: this install did not write it, so stop and ask the user.

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

1. Start a new Copilot CLI session.
2. Allow the tools. Copilot asks for approval before an MCP tool call. Interactively the user can approve per call. Non-interactively (`copilot -p`) a call that is not allowed is refused ("blocked by permissions") and `session_start` never runs. Allow just this server by adding `--allow-tool=total-recall` to the command line or an alias of the user's own choosing (avoid `--allow-all-tools`).
3. MCP-only path: add the startup instructions to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Plugin path, now:

```sh
node ~/.copilot/installed-plugins/strvmarv-total-recall-marketplace/total-recall/bin/start.js --version
```

Expected: `total-recall <version>`; the first run downloads the engine. The directory is where Copilot CLI 1.0.83 holds the installed plugin on the machine that produced this page, and `bin/start.js` is in it.

MCP-only path, now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports, and `~/.copilot/mcp-config.json` should hold the `total-recall` entry.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4 (with `--allow-tool=total-recall` on the command line under `-p`).

## What you get

Plugin path (observed on GitHub Copilot CLI 1.0.98-2, Windows, with `-p` and interactively):

- The `total-recall` MCP server from the plugin's `.mcp.json`, the same file Claude Code uses (Copilot always prefers a root `.mcp.json` over a manifest `mcpServers` block).
- The startup instructions and the capture, retrieve and feedback rules (`skills/commands/SKILL.md`), delivered by a `sessionStart` hook (`copilot/hook.js`) so the model calls `session_start`. Copilot starts the session lazily, with the first prompt.
- The pinned-directive floor on every prompt (`userPromptSubmitted` hook, which adds `additionalContext` that Copilot appends to the user message), on the turn-count trigger only (`floor_every_n_turns`, default 6): the hook payload has no transcript path, so the token-growth trigger is not used. Copilot's hooks documentation says command hooks cannot add context on this event; on the tested build they can, so a later build may change this (`docs/TODO.md` names the documented fallback).
- Skills: `/total-recall:commands <subcommand>` works as in Claude Code.
- Updating: `copilot plugin update total-recall@strvmarv-total-recall-marketplace`.

The manifest is `.github/plugin/plugin.json`; Copilot's documented lookup finds it before `.claude-plugin/plugin.json`, and its `hooks` path replaces the Claude-format `hooks/hooks.json`, which cannot run under Copilot on Windows (the unbraced `$CLAUDE_PLUGIN_ROOT` in those hook commands expands to nothing: observed).

MCP-only path: the tools, no startup instructions, no floor.

## Known gaps

- **Server-side project scope.** Copilot starts the server in the plugin directory, so `session_start` resolves the project from there and injects only global pins and the plugin's own project docs, while the floor (which uses the session directory from the hook payload) also includes pins scoped to your repo. A session-directory workaround was rejected because Copilot's session record keeps the original directory after a resume from another one.
- **Compaction.** `/compact` fires no hook and its summary can drop the startup instructions and the pins; the floor re-asserts the pins on its schedule, but the instructions are not re-injected until the next session.
- No `SessionEnd` compaction nudge, no first-run tutorial, and the `setup` and `update` subcommands of the commands skill have no Copilot meaning.
- The engine's importers read your real `~/.claude`, `~/.copilot` and similar directories regardless of a redirected profile on Windows, so a throwaway test setup still imports your real memories into its own store.
- Not observed: a subagent run (the floor skips hook payloads that carry an `agentId`, per Copilot's hooks documentation), macOS and Linux, and the interactive approval prompt.

## Evidence

Plugin-path behaviour was observed on GitHub Copilot CLI 1.0.98-2, Windows, with `-p` and interactively; it is recorded in `AGENTS.md` ("Copilot CLI host") and the earlier `INSTALL.md`, and was not re-run for this page. The plugin directory above was observed on this machine with Copilot CLI 1.0.83 (a machine in use, not a throwaway). The MCP-only path and the `copilot plugin update` and `--plugin-dir` forms come from the earlier `INSTALL.md` and were not run. That the install script may write `~/.copilot/mcp-config.json` is read from `scripts/install-vscode.js`, not run.
