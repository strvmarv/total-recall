# total-recall on Codex CLI

## Install

Windows is the platform the Codex support was verified on. macOS and Linux follow the same design but have not been run.

[Codex CLI](https://developers.openai.com/codex) installs total-recall as a plugin from the total-recall marketplace. No `config.toml`, `hooks.json` or `mcp.json` edit is needed to install it. Run both commands in a shell (or browse to the plugin with `/plugins` inside Codex):

```sh
codex plugin marketplace add strvmarv/total-recall-marketplace
codex plugin add total-recall@strvmarv-total-recall-marketplace
```

The marketplace entry is the repository URL with no ref, so the install takes the default branch, which contains `.codex-plugin/` and `codex/`. The install carries no engine binary: the MCP server downloads and verifies it on first launch for the version in that tree's `package.json` (a few seconds), and tools report "provisioning" until it is ready.

Optional: let every total-recall tool run without a per-call approval. Codex asks for approval before calling an MCP tool that is not marked read-only, and none of total-recall's are. **Ask first**: tell the user that this setting lets Codex call every total-recall tool without asking each time, including the tools that write and delete memories. On a yes, add this to `~/.codex/config.toml`, keeping everything else in the file. If that table is already in the file, leave it; if it is there with a different `default_tools_approval_mode`, stop and ask the user.

```toml
[plugins."total-recall@strvmarv-total-recall-marketplace".mcp_servers.total-recall]
default_tools_approval_mode = "approve"
```

For information only, and not for the agent to use: `codex exec --dangerously-bypass-hook-trust` skips the hook-trust review for one invocation. It is meant for automation that already vets its hooks.

## User steps

1. Start a new Codex session.
2. Trust the hooks once: open `/hooks` in Codex and trust the two total-recall hooks. Codex skips plugin hooks until they are reviewed. A plugin update that changes a bundled script asks for a new review.
3. If the approval setting above was declined: approve each total-recall tool call when Codex asks. Non-interactive runs (`codex exec`) cannot answer, so the model's `session_start` call is refused there with "MCP tool call requires approval, but approval policy is never".

## Verify

Codex keeps the plugin in its cache. Now:

```sh
ls "${CODEX_HOME:-$HOME/.codex}/plugins/cache/"*/total-recall/
node "${CODEX_HOME:-$HOME/.codex}/plugins/cache/<marketplace>/total-recall/<version>/bin/start.js" --version
```

This is for a POSIX shell. In PowerShell, use `$env:CODEX_HOME` when it is set, else `$HOME\.codex`, for the same two commands (`Get-ChildItem` for the first).

`<marketplace>` and `<version>` are the directories the first command lists (the layout is `<home>/plugins/cache/<marketplace>/total-recall/<version>/`, from `codex/bootstrap.cjs`). Expected: `total-recall <version>`; the first run downloads the engine. `codex plugin list` shows the installed version and `codex mcp list` shows the launcher entry (from the earlier `INSTALL.md`, not run here).

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4. If the hooks are not trusted yet, the startup instructions are not delivered and the model may not call `session_start` by itself.

## What you get

At parity with Claude Code unless noted:

- The `total-recall` MCP server, started from the plugin's bundled `codex/mcp.json`. Codex expands no variables in a bundled server's command, so that file runs a small launcher that finds the installed plugin under `<home>/plugins/cache/` and starts `bin/start.js` from there. It sets no working directory, so project-scoped pins follow the directory you run Codex in. Codex starts the server with a sanitized environment, so the file forwards the variables the engine needs (`TOTAL_RECALL_*`, `HOME`, `XDG_*`, `CODEX_HOME`, proxy and CA settings); the hook runs with your full environment, and forwarding keeps both on the same store.
- The startup instructions and the capture, retrieve and feedback rules (`skills/commands/SKILL.md`), delivered by a `SessionStart` hook on `startup` and `resume` (both observed) and on `clear` and `compact` (matched because Codex documents them as session-start sources; not observed), plus a short note about Codex. Resuming a session delivers them again, as under Claude Code.
- The pinned-directive floor on every prompt (`UserPromptSubmit` hook), on the turn-count trigger only (`floor_every_n_turns`, default 6). The token-growth trigger is not used: a Codex session log is several times larger than the conversation it records, so the byte-growth heuristic would re-inject on most prompts. The floor stays silent until the engine is provisioned. Codex keeps at most about 16,000 tokens of a hook's context before replacing the rest with a preview, so a pinned block beyond roughly 125 default-size pins would lose its tail.
- The commands skill, listed by Codex as `total-recall:total-recall:commands`; invoke it as `$total-recall:total-recall:commands` or from `/skills` (the listing was observed; the invocation follows Codex's documentation).
- Updating: run `codex plugin add total-recall@strvmarv-total-recall-marketplace` again (Codex replaces the old version directory), start a new session, and re-trust the hooks if `/hooks` asks.

## Known gaps

- No `SessionEnd` compaction nudge: Codex gives that hook 3 seconds and no way to show output to the user. The engine's `session_start` hints still report uncompacted entries.
- The `setup` and `update` subcommands of the commands skill are Claude Code specific.
- A custom `CODEX_HOME` works because the launcher forwards it; a relative one does not (it would resolve against the server's directory, not Codex's).
- A stderr line `total-recall plugin not found under ...` means the plugin is not in Codex's cache for the `CODEX_HOME` the server was given.
- The interactive hook-trust review and the interactive approval prompt were not observed; they follow Codex's documentation.

## Evidence

Observed on Codex CLI 0.162.1, Windows, with `codex exec` (the interactive flows were not observed): the plugin installs from the marketplace; the bundled MCP server runs through the launcher in a sanitized environment (19 variables on Windows); the `SessionStart` hook delivered the instructions on `startup` and `resume` (about 19 KB, unspilled with `additionalContextLimit` set); the floor fired on exactly the 7th prompt of a session with the default setting, regardless of tool activity, and the session id was stable across `codex exec resume`; a non-interactive `session_start` call was refused for approval as quoted above. These come from `AGENTS.md` ("Codex CLI host") and the earlier `INSTALL.md`, which record them; none was run for this page.

The cache layout comes from the header comment of `codex/bootstrap.cjs`. The 16,000-token hook limit and the `/hooks` trust review are from Codex's documentation.
