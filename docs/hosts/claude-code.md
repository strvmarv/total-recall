# total-recall on Claude Code

## Install

No stop condition: Claude Code is the primary host.

### Plugin (recommended)

The plugin gives Claude Code the MCP server, the startup instructions, the per-prompt pinned-directive floor and the `/total-recall:commands` skill. Tell the user that these two commands add a third-party marketplace and enable an MCP server and hooks, one of which runs on every prompt; the user's request to install the plugin covers them. Run both commands in a shell:

```sh
claude plugin marketplace add strvmarv/total-recall-marketplace
claude plugin install total-recall@strvmarv-total-recall-marketplace
```

The marketplace entry is the repository URL with no ref, so the install takes the default branch. It carries no engine binary: the first launch downloads and verifies it for the version in that tree's `package.json` (see [first run](../first-run.md#provisioning-on-first-launch)).

If the `claude` command is not found or either command exits non-zero, stop. The user can type the same two steps as slash commands inside a Claude Code session (a User step); wait for them to say both have run, then make the permission offer below and continue at Verify:

```
/plugin marketplace add strvmarv/total-recall-marketplace
/plugin install total-recall@strvmarv-total-recall-marketplace
```

The plugin path changes nothing outside Claude Code (the two commands update Claude Code's own plugin registry), so there is nothing else to report there. After the install, offer the permission rule. **Ask first**: tell the user that the rule `mcp__plugin_total-recall_total-recall__*` lets Claude Code call every total-recall tool without asking each time, including the tools that write and delete memories. On a yes, make the edit the `setup` section of `skills/commands/SKILL.md` describes: add that one rule to `permissions.allow` in `~/.claude/settings.json` (under `$CLAUDE_CONFIG_DIR` instead of `~/.claude` if that is set, the same place the plugin directory is under), create the file if it is missing, and change nothing else in it. Check the file without printing it. The path below is `$HOME/.claude/settings.json`; if `CLAUDE_CONFIG_DIR` is set use `$CLAUDE_CONFIG_DIR/settings.json` instead, and in PowerShell use `$env:USERPROFILE\.claude\settings.json` (or `$env:CLAUDE_CONFIG_DIR\settings.json`):

```sh
node -e "const fs=require('fs'); const f=process.argv[1]; if (fs.existsSync(f)===false) { console.log('missing') } else { try { const j=JSON.parse(fs.readFileSync(f,'utf8').replace(/^\uFEFF/,'')); const a=j && j.permissions && j.permissions.allow; console.log(Array.isArray(a) ? (a.includes(process.argv[2]) ? 'present' : 'absent, ' + a.length + ' items') : 'no allow array') } catch (e) { console.log('unreadable') } }" "$HOME/.claude/settings.json" 'mcp__plugin_total-recall_total-recall__*'
```

If it prints `present`, do nothing. If it prints `absent`, insert the string as the first item of the `permissions.allow` array by text insertion (with a comma after it if the count is above 0), following the edit rules in `INSTALL.md` (copy first, parse check after). If it prints `missing`, create the file holding exactly `{"permissions":{"allow":["mcp__plugin_total-recall_total-recall__*"]}}`. If it prints `unreadable` (for example the file has comments) or `no allow array`, do not edit: tell the user to run `/total-recall:commands setup` themselves. Do not run the edit if you cannot place it without printing the other rules.

### MCP server only (no plugin)

This gives the tools and nothing else: no hooks, no startup instructions, no floor, no skills.

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

The form that skips the install script changes nothing else. The install script configures nothing for Claude Code, so nothing is lost by skipping it here:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then give the user this entry to add; this is a User step, not yours to edit. Claude Code rewrites `~/.claude.json` itself and the file also holds other sensitive content. The entry goes in the `mcpServers` object that is a direct child of the file's root (not one under `projects`), which the user creates if it is missing. If `CLAUDE_CONFIG_DIR` is set, where Claude Code keeps this file was not checked: tell the user and ask which file they mean. If the user already has a `total-recall` entry there, they keep it and tell you.

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
2. Plugin path, if the allow rule was declined: tell the user that, in a mode that never asks, the tools stay blocked until they run `/total-recall:commands setup` and restart the session.
3. MCP-only path: add the entry above to `~/.claude.json`, then restart Claude Code so it reads the file (inferred, not observed), and tell the agent when done.
4. MCP-only path: nothing delivers the startup instructions. This is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Plugin path, now:

```sh
ls ~/.claude/plugins/cache/strvmarv-total-recall-marketplace/total-recall/
node ~/.claude/plugins/cache/strvmarv-total-recall-marketplace/total-recall/<version>/bin/start.js --version
```

`<version>` is the directory the first command lists (if more than one is listed, use the newest and tell the user). Expected: `total-recall <version>`, which is also the version `npm view @strvmarv/total-recall version` reports unless the default branch is ahead of the last release. This proves the engine and the plugin files, not that Claude Code has the plugin enabled: report that as pending until the next session. The first run downloads and verifies the engine (about 100 MB), so it can take a while; a later run answers at once. If `CLAUDE_CONFIG_DIR` is set, the plugin directory is under it instead of `~/.claude`.

MCP-only path, now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. If the user comes back in this session after adding the entry, run the read command from Rules in `INSTALL.md` on `~/.claude.json` with the key `mcpServers`; `total-recall present: true` is a check, anything else is pending. If they do not, report the entry as pending. A bare `total-recall` on PATH can be an older copy than the one another harness runs.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4, and should see the tier counts and the database path.

## What you get

Plugin path:

- The `total-recall` MCP server, started from the plugin's `.mcp.json` (`node ${CLAUDE_PLUGIN_ROOT}/bin/start.js`).
- The startup instructions and the capture, retrieve and feedback rules, delivered by a `SessionStart` hook (`startup`, `resume`, `clear`, `compact`) from `hooks/hooks.json`.
- The pinned-directive floor on every prompt (`UserPromptSubmit` hook), by the adaptive throttle described in `AGENTS.md`.
- A `SessionEnd` hook for bookkeeping.
- The skill `commands` (`/total-recall:commands <subcommand>`). The plugin's `skills/` directory also holds `using-total-recall` and `develop` (read from the directory listing, not run).
- Updates: run `/plugin` and update `total-recall` from the `strvmarv-total-recall-marketplace` marketplace, then `/reload-plugins`.

MCP-only path: the MCP tools and nothing else.

## Known gaps

- Nothing here was observed in a Claude Code session started after the install; the checks above are the engine and the plugin directory.
- In a permission mode that never asks, the plugin's tools are blocked until the allow rule is in place.
- The MCP-only path has no instructions, no floor and no skills.

## Evidence

Observed on Claude Code 2.1.301, macOS arm64, Node 24.18, npm 11.16, with `CLAUDE_CONFIG_DIR` redirected to a throwaway directory: `claude plugin marketplace add strvmarv/total-recall-marketplace` and `claude plugin install total-recall@strvmarv-total-recall-marketplace` both exit 0 and install version 4.7.0 under `plugins/cache/strvmarv-total-recall-marketplace/total-recall/4.7.0/`, with no `binaries/` directory. `node <that directory>/bin/start.js --version` then downloaded, verified and extracted the engine and printed `total-recall 4.7.0`; `status` printed the tier, database and embedding tables. The marketplace entry (`"source": {"source": "url", "url": "https://github.com/strvmarv/total-recall.git"}`, no ref) was read from the marketplace repository.

Not observed: the first Claude Code session after the install, the allow-rule edit, the MCP-only path (its entry and its steps come from the earlier `INSTALL.md` and from `skills/commands/SKILL.md`; the restart step is inferred), the hooks (from `hooks/hooks.json`, not run) and the extra skills (from the plugin's `skills/` directory listing, not run).
