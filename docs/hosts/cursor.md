# total-recall on Cursor

## Install

**The plugin path is not supported on macOS and Linux.** Tell the user that the plugin they asked for cannot be installed there, and ask whether to continue with the "MCP server only" option below instead. macOS and Linux are unsupported until observed for the plugin: the plugin's MCP server runs in a sanitized environment, and a POSIX server without `HOME` or `USERPROFILE` would make the engine create a literal `~/.total-recall` directory in the user's project. On macOS or Linux skip "Local plugin" and use "MCP server only", which is marked not run.

Observed on Cursor CLI (`cursor-agent`) 2026.11.10-731fb78, Windows, with `-p` and interactively. The Cursor desktop IDE agent, subagents and cloud agents were not tested.

### Local plugin (Windows only; recommended there)

Put a copy of the repository at `~/.cursor/plugins/local/total-recall` with a `git clone`, and restart Cursor (or run `Developer: Reload Window`):

```sh
git clone https://github.com/strvmarv/total-recall "$HOME/.cursor/plugins/local/total-recall"
```

A symlink only loads when its target resolves inside `~/.cursor/plugins/local`. For one run, `cursor-agent --plugin-dir <checkout>` loads a checkout without copying it. A clone carries no engine binary: the MCP server downloads and verifies it on first launch (that path was exercised in other hosts but not inside Cursor), tools return "not ready" and the floor stays silent until the engine is there.

The marketplace flow (`cursor-agent plugin marketplace add https://github.com/strvmarv/total-recall-marketplace`, then `/plugins`) indexes the existing marketplace, but which manifest Cursor loads from it (`.cursor-plugin/plugin.json` or the Claude one) was not verified, so it is not a supported install path yet.

### MCP server only (no plugin)

Not run for this page: this path comes from the earlier `INSTALL.md` and was not exercised here.

This gives the tools and nothing else: no startup instructions, no floor. Use it instead of the plugin, never together with it (two servers, and the hook would read the default store while the second reads yours).

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

The form that skips the install script. The install script configures nothing for Cursor, so nothing is lost by skipping it here:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Ask the user which Cursor MCP config to use: the project's `.cursor/mcp.json` or their global Cursor MCP config; do not guess, and warn that a project file may be committed. Then add this entry to `mcpServers` in that file, creating the file or the `mcpServers` key if it is missing. Add only this entry and keep the rest of the file; if a `total-recall` entry is already there that this install did not just write, stop and ask the user.

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

1. Restart Cursor (or run `Developer: Reload Window`) so it reads the plugin or the MCP config.
2. Plugin path: approve the server. (Whether a hand-written MCP-only entry needs approval was not observed.) Cursor asks before using a plugin's MCP server (it shows as `plugin-total-recall-total-recall`). Interactively, approve it. With `cursor-agent -p`, the user passes `--approve-mcps` on their own command line. The refusal text for an unapproved server was not observed.
3. MCP-only path: this is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.
4. First session on a store with a large history: the first `session_start` can exceed Cursor's 60 second MCP request limit (see Known gaps); the user may need to ask the model to call it once more.

## Verify

Plugin path, now (Windows):

```sh
node "$HOME/.cursor/plugins/local/total-recall/bin/start.js" --version
```

Expected: `total-recall <version>`; the first run downloads the engine. The directory is the one the install step creates.

MCP-only path, now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports, and the Cursor MCP config should hold the `total-recall` entry. A bare `total-recall` on PATH can be an older copy than the one another harness runs.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

Plugin path:

- The `total-recall` MCP server from `cursor/mcp.json` (`node ${CURSOR_PLUGIN_ROOT}/bin/start.js`). It has no `cwd`, so Cursor starts it in your workspace and project-scoped pins work server-side (observed), unlike Claude Code and Copilot CLI.
- The startup instructions (`skills/commands/SKILL.md` plus a short Cursor note), delivered by a `sessionStart` hook (`cursor/hook.js`) as `additional_context`, so the model calls `session_start` itself (observed 3 of 3 fresh conversations with the whole 18.6 KB intact, using an earlier wording of the Cursor note; the final wording is unit-tested only because the live account hit its usage limit). The hook runs once per conversation, not again on `--continue`.
- The pinned-directive floor, on the first tool call of each user message (`postToolUse` hook, `additional_context`), on the turn-count trigger only (`floor_every_n_turns`, default 6, counted in tool-using messages): Cursor's CLI never fires a per-prompt hook (`beforeSubmitPrompt`, `stop` and `afterAgentResponse` did not fire, and `beforeSubmitPrompt` cannot add context anyway). Interactively the cadence is exact (the 7th tool-using message injects by default). In `-p` mode Cursor reuses one generation id for the whole conversation, even across `--continue`, so the floor counts once per `-p` conversation.
- Skills: `commands` and `using-total-recall`.
- The startup note tells the model to run this package's CLI (`compact --run`, `ui`) as `node <root>/cursor/cli.js <args>`: the wrapper drops the same store variables as the hook, so those commands use the same database as Cursor's server.

MCP-only path: the tools, no startup instructions, no floor.

## Known gaps

- **First `session_start` can time out.** On a fresh store the first call imports other tools' history and can exceed Cursor's 60 second MCP request limit, which a plugin cannot raise (seen with a very large history: about 87 s). The hook's note tells the model to call it once more; the import is incremental. In `-p` the process exit kills the engine mid-import (both `-p` runs on a cold large store timed out), so on a big history repeat the run or warm the store with one interactive session first.
- **No custom store on the plugin path.** Cursor starts plugin servers with a sanitized environment, so `TOTAL_RECALL_HOME`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME` and `HERMES_HOME` cannot reach the server; the hook ignores them too so the floor and the server agree. If a non-default store is needed, use the MCP-only option. On Windows a `HOME` that differs from `USERPROFILE` (MSYS or Git Bash setups) also splits stores between Cursor (`%USERPROFILE%\.total-recall`) and Claude Code or Copilot CLI (`$HOME/.total-recall`).
- **Floor only on turns that use a tool**, and a message whose tools all fail does not count. Subagent tool calls carry the parent's conversation id and may not be distinguishable from the parent's (a subagent run was not observed).
- **Compaction.** `preCompact` fires on a manual `/compact` but cannot carry context, and `sessionStart` does not fire again, so nothing re-injects the instructions after a compaction (whether they survive it was not observed). The floor keeps re-asserting the pins.
- No `SessionEnd` nudge, no first-run tutorial, and the `setup` and `update` subcommands of the commands skill have no Cursor meaning. `hooks/hooks-cursor.json` (Claude-format, runs `bash`) is kept for hand-wired setups and is untested.
- The engine's importers read your real `~/.claude`, `~/.cursor` and similar directories regardless of a redirected profile on Windows.
- MCP-only path: whether the sanitized environment hazard in the stop paragraph applies to a hand-written entry was not observed. If the user finds a literal `~/.total-recall` directory in a project, report it. A bare `total-recall` must resolve on the PATH of the Cursor process; if the server does not start, the user can give the absolute path from `command -v total-recall` (inferred, not observed).
- Contributors: run `cursor-agent` from PowerShell or cmd on Windows. From Git Bash a Cursor CLI bug blocks every tool call.

## Evidence

Everything under the plugin path was observed on Cursor CLI 2026.11.10-731fb78, Windows, and is recorded in `AGENTS.md` ("Cursor CLI host") and the earlier `INSTALL.md`; none of it was re-run for this page. The macOS and Linux stop is from those observations. The MCP-only path, its entry and the `git clone` command come from the earlier `INSTALL.md` (which said "a `git clone`, or the npm package contents") and were not run. The local plugin directory `~/.cursor/plugins/local` did not exist on the machine that produced this page, so no listing of it was made.
