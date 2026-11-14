# total-recall on Claude Code

## Install

No stop condition: Claude Code is the primary host.

### Plugin (recommended)

The plugin gives Claude Code the MCP server, the startup instructions, the per-prompt pinned-directive floor and the `/total-recall:commands` skill. Run both commands in a shell:

```sh
claude plugin marketplace add strvmarv/total-recall-marketplace
claude plugin install total-recall@strvmarv-total-recall-marketplace
```

The marketplace entry is the repository URL with no ref, so the install takes the default branch. It carries no engine binary: the first launch downloads and verifies it for the version in that tree's `package.json` (see [first run](../first-run.md#provisioning-on-first-launch)).

The same two steps are slash commands the user can type inside a Claude Code session instead:

```
/plugin marketplace add strvmarv/total-recall-marketplace
/plugin install total-recall@strvmarv-total-recall-marketplace
```

After the install, offer the permission rule. **Ask first**: tell the user that the rule `mcp__plugin_total-recall_total-recall__*` lets Claude Code call every total-recall tool without asking each time, including the tools that write and delete memories. On a yes, make the edit the `setup` section of `skills/commands/SKILL.md` describes: add that one rule to `permissions.allow` in `~/.claude/settings.json`, create the file if it is missing, and change nothing else in it. On a no, tell the user that in a permission mode that never asks (TUI fullscreen, `dontAsk`) the total-recall tools are blocked until they run `/total-recall:commands setup` themselves. The rule names the plugin's tools, so it applies to this path only.

### MCP server only (no plugin)

This gives the tools and nothing else: no hooks, no startup instructions, no floor, no skills.

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

The form that skips the install script changes nothing else. The install script configures nothing for Claude Code, so nothing is lost by skipping it here:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then add this entry to `mcpServers` in `~/.claude.json`. Add only this entry and keep the rest of the file; if a `total-recall` entry is already there that this install did not just write, stop and ask the user.

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

Nothing on this path tells the model to call `session_start`. Add the startup instructions as a user step (see User steps).

## User steps

1. Plugin path: start a new Claude Code session; MCP tools and hooks appear only in a new session.
2. Plugin path, if the allow rule was declined and the session runs in a mode that never asks: run `/total-recall:commands setup`, then restart the session.
3. MCP-only path: restart Claude Code so it reads `~/.claude.json`.
4. MCP-only path: nothing delivers the startup instructions, so add them to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Plugin path, now:

```sh
ls ~/.claude/plugins/cache/strvmarv-total-recall-marketplace/total-recall/
node ~/.claude/plugins/cache/strvmarv-total-recall-marketplace/total-recall/<version>/bin/start.js --version
```

`<version>` is the directory the first command lists. Expected: `total-recall <version>`. The first run downloads and verifies the engine (about 100 MB), so it can take a while; a later run answers at once. If `CLAUDE_CONFIG_DIR` is set, the plugin directory is under it instead of `~/.claude`.

MCP-only path, now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports; read `~/.claude.json` and confirm the `total-recall` entry. A bare `total-recall` on PATH can be an older copy than the one another harness runs.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4, and should see the tier counts and the database path.

## What you get

Plugin path:

- The `total-recall` MCP server, started from the plugin's `.mcp.json` (`node ${CLAUDE_PLUGIN_ROOT}/bin/start.js`).
- The startup instructions and the capture, retrieve and feedback rules, delivered by a `SessionStart` hook (`startup`, `resume`, `clear`, `compact`) from `hooks/hooks.json`.
- The pinned-directive floor on every prompt (`UserPromptSubmit` hook), by the adaptive throttle described in `AGENTS.md`.
- A `SessionEnd` hook for bookkeeping.
- The skills `commands` (`/total-recall:commands <subcommand>`), `using-total-recall` and `develop`.
- Updates: run `/plugin` and update `total-recall` from the `strvmarv-total-recall-marketplace` marketplace, then `/reload-plugins`.

MCP-only path: the MCP tools and nothing else.

## Known gaps

- Nothing here was observed in a Claude Code session started after the install; the checks above are the engine and the plugin directory.
- In a permission mode that never asks, the plugin's tools are blocked until the allow rule is in place.
- The MCP-only path has no instructions, no floor and no skills.

## Evidence

Observed on Claude Code 2.1.301, macOS arm64, Node 24.18, npm 11.16, with `CLAUDE_CONFIG_DIR` redirected to a throwaway directory: `claude plugin marketplace add strvmarv/total-recall-marketplace` and `claude plugin install total-recall@strvmarv-total-recall-marketplace` both exit 0 and install version 4.7.0 under `plugins/cache/strvmarv-total-recall-marketplace/total-recall/4.7.0/`, with no `binaries/` directory. `node <that directory>/bin/start.js --version` then downloaded, verified and extracted the engine and printed `total-recall 4.7.0`; `status` printed the tier, database and embedding tables. The marketplace entry (`"source": {"source": "url", "url": "https://github.com/strvmarv/total-recall.git"}`, no ref) was read from the marketplace repository.

Not observed: the first Claude Code session after the install, the allow-rule edit, the MCP-only path (its entry and its steps come from the earlier `INSTALL.md` and from `skills/commands/SKILL.md`), and the hooks (from `hooks/hooks.json`, not run).
