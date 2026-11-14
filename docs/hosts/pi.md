# total-recall on pi

## Install

No stop condition. pi installs total-recall as a pi package; no `mcp.json` or settings edit is needed, because the package's extension registers the MCP server itself.

**Ask first.** The npm and git forms below run the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running either; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

From npm (recommended):

```sh
pi install npm:@strvmarv/total-recall
```

This needs the `pi` command, and the `git:` form needs `git`. The package is large (about 378 MB unpacked as of 4.2.3: the engine binaries for every platform plus the embedding model), so the first install can take minutes. The engine ships in the npm package, so nothing is downloaded at launch.

The form that skips the install script (POSIX shell; not run on Windows). The install script configures nothing for pi itself, so nothing is lost by skipping it:

```sh
npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall
```

Straight from git:

```sh
pi install git:github.com/strvmarv/total-recall
```

pi runs the package install for a `git:` package too, so the same install script runs and the paragraph above applies. No skip form was observed for the `git:` form. A git install holds the engine for this platform only.

## User steps

1. Start a new pi session. Running `/reload` in the open session is an alternative (from pi's documentation, not run).

## Verify

Now, for the form that was used:

```sh
node ~/.pi/agent/npm/node_modules/@strvmarv/total-recall/bin/start.js --version
node ~/.pi/agent/git/github.com/strvmarv/total-recall/bin/start.js --version
```

The first line is for the `npm:` form and the second for the `git:` form. Expected: `total-recall <version>`, the version `npm view @strvmarv/total-recall version` reports (or the default branch's, for `git:`). For the `git:` form the expected version is the `version` in the repository's `package.json` on its default branch. If the printed version differs from what is expected, report it and stop. A bare `total-recall` on PATH can be an older copy than the one pi runs (observed: 4.0.4 on PATH while pi ran 4.7.0), and a plain pi install does not put `total-recall` on PATH at all.

Then check that pi will use this package's server: search the user's `mcp.json` (`~/.pi/agent/mcp.json`, or under `$PI_CODING_AGENT_DIR` if that is set) and the `.pi/mcp.json` of the directory pi runs in for `total[-_.]recall` (see Rules in `INSTALL.md`). A missing file means no entry. Any hit is a possible override of the extension's server: do not read further, tell the user, and report the server check as pending. The version check proves the package, not that pi loaded it.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4. In pi the `status` tool it asks for is one of those reached through `tool_search`; that is expected.

## What you get

At parity with Claude Code unless noted:

- The `total-recall` MCP server, registered by the extension. A `total-recall` entry you already have in `~/.pi/agent/mcp.json` (or the project's `.pi/mcp.json`) wins over it; the pinned floor then follows that entry's plain `TOTAL_RECALL_*` env values, such as `TOTAL_RECALL_HOME`, so it reads the same store, and it stays silent if the entry is remote, disabled, or sets those values with `${VAR}` or `!command`. Most tools are reached through pi's `tool_search`; `session_start`, `session_end`, `session_refresh`, `memory_*` and `kb_search` are declared directly.
- The startup instructions and the capture, retrieve and feedback rules (`skills/commands/SKILL.md`), injected as a hidden message with the first prompt of a session and again after a compaction.
- The pinned-directive floor on every prompt, on the turn-count trigger only (`floor_every_n_turns`, default 6). The token-growth trigger is not used in pi: pi creates its session file only after the first message, which would trigger an early floor on turn 2. It stays silent until the engine is present, which an npm install already has.
- `/total-recall <subcommand>` (alias `/total-recall:commands`), with tab completion. It asks the model to run the command, so it works in an interactive or RPC session; in `pi -p "/total-recall help"` pi exits before the model's answer is printed.

The injected instructions tell the model to run this package's CLI as `node <package>/bin/start.js <args>` (for `compact --run`, `ui`), because a plain pi install does not put `total-recall` on PATH. If you navigate with `/tree` to a point before the instructions were delivered, they are queued again for the next prompt.

## Known gaps

- Nothing triggers `session_end` in pi; it is bookkeeping only.
- The `setup` and `update` subcommands have no pi meaning (use `pi update`).
- `compact` defaults to a compactor agent that pi does not have; use `compact --fast`, or let the model make the calls from `session_context`.
- Subagent children (pi-subagents, pi-herdr-agents) and `--no-session` runs are designed to get the MCP tools but no injected instructions, floor or `/total-recall` (from `AGENTS.md`). That was not verified, and one subagent was seen to behave differently, so do not rely on it either way.

## Evidence

Observed on pi 1.5.2, macOS arm64, `HOME` redirected to a throwaway directory holding an empty `.gemini/`:

- `pi install npm:@strvmarv/total-recall` installed to `~/.pi/agent/npm/node_modules/@strvmarv/total-recall`; the install script ran (it wrote `.gemini/settings.json` and `.gemini/GEMINI.md`).
- `npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall` installed to the same directory; the install script did not run (no Gemini file written); `binaries/` held all four platforms and `node <directory>/bin/start.js --version` printed `total-recall 4.7.0`.
- `pi install git:github.com/strvmarv/total-recall` installed to `~/.pi/agent/git/github.com/strvmarv/total-recall`; the install script ran (it wrote the MCP entry and the hook to `.gemini/settings.json`, and no `GEMINI.md`); `binaries/` held this platform only; `--version` printed `total-recall 4.7.0`.

The session that produced this page ran total-recall 4.7.0 through the pi package: the instructions were injected, `session_start` returned normally and the smoke test passed. The extension behaviour above is from `AGENTS.md` and the earlier `INSTALL.md`; `AGENTS.md` records the floor-call timing as measured on Windows, and the package size is from a Windows measurement of 4.2.3.
