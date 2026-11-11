# total-recall Installation Guide

This file is designed to be read by AI coding assistants. If you're an LLM helping a user install total-recall, follow the instructions for their platform below.

## What total-recall ships as

Since 0.8.0, total-recall is a **prebuilt .NET 8 NativeAOT binary** wrapped by a tiny Node launcher (`bin/start.js`) that detects the host platform and exec's the right per-RID binary. The npm package ships binaries for `linux-x64`, `linux-arm64`, `osx-arm64`, and `win-x64`. No `dotnet` runtime, no `bun`, and no system SQLite needed — the binary bundles its own `libe_sqlite3`, `libonnxruntime`, `vec0` (sqlite-vec extension), and the `bge-small-en-v1.5` ONNX embedding model (~133 MB).

## Prerequisites

- **Node.js >= 20.0.0** — required only for `npm install` and the `bin/start.js` launcher (~60 lines, zero runtime overhead). The actual MCP server is the prebuilt .NET binary.
- **Internet access** — only needed if you install via Claude Code's `/plugin` flow with a `source: github` marketplace entry. In that case `bin/start.js` downloads the matching per-RID archive (~90 MB, includes the bundled embedding model) from GitHub Releases on first launch, verifying it by sha256 against the release `provisioning.manifest.json`. The download runs in-process inside the shim: the MCP connection stays up the entire time, and any tool call during provisioning returns a `{ "status": "not_ready", "phase": "provisioning" }` result — wait a few seconds and retry. Once provisioning finishes the engine starts automatically and memory features become available; no restart required. The npm install path ships all RIDs in the tarball and needs no runtime download.
- **Intel Mac (`darwin-x64`) is not currently shipped.** Apple Silicon (`osx-arm64`) is. All Apple hardware sold since November 2020 is arm64.

## Relocating the database

By default, total-recall stores its SQLite database at `<TOTAL_RECALL_HOME>/total-recall.db` (typically `~/.total-recall/total-recall.db`). Set `TOTAL_RECALL_DB_PATH` to relocate **only** the database file — `config.toml`, the embedding model cache, and export directories stay anchored to `TOTAL_RECALL_HOME`.

**When to use it:**

- **Cloud-synced memories.** Point at a file under Dropbox / iCloud / OneDrive so your memory store survives machine loss and flows to other devices.
- **Shared database across workspaces.** Multiple Claude Code windows running in different project directories can read/write one memory store. The existing `project` field on memories gives you per-workspace views without needing separate DBs.

**How to configure:**

Shell export (simplest, per-terminal):

```bash
export TOTAL_RECALL_DB_PATH=~/Dropbox/total-recall/memories.db
```

Claude Code `.mcp.json` env block (per-host, survives shell sessions):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "node",
      "args": ["${CLAUDE_PLUGIN_ROOT}/bin/start.js"],
      "cwd": "${CLAUDE_PLUGIN_ROOT}",
      "env": {
        "TOTAL_RECALL_DB_PATH": "~/Dropbox/total-recall/memories.db"
      }
    }
  }
}
```

Two workspaces sharing one database:

> Set the same `TOTAL_RECALL_DB_PATH` in both workspace configs. Each Claude Code window opens its own SQLite connection; the OS's file locks serialize writes across processes. Use the `project` field on memories (set automatically from CWD when you store, filterable on `memory_search`) to keep per-workspace views clean.

**Rules:**

- Must be an absolute file path (e.g. `/Users/you/Dropbox/tr.db`), or start with `~/` which expands to your home directory.
- Bare `~` and trailing `/` or `\` are rejected — total-recall needs a full file path, not a directory.
- The parent directory is created automatically on first run if it doesn't exist.
- Invalid values cause the MCP server to **fail at startup** with a clear stderr message. No partial database will be created.
- The env var is read **once at startup**; changing it requires restarting the MCP host (close and reopen Claude Code).

**Cloud sync caveats (important):**

SQLite on sync drives is historically fragile. Dropbox in particular has mishandled `-wal` and `-shm` sidecar files under concurrent writes, leading to corruption. iCloud Drive is less exposed but still not guaranteed. We do **not** force a journal mode — you choose:

- If you see corruption symptoms on a sync drive, try `PRAGMA journal_mode=DELETE` in a one-shot sqlite CLI session to disable WAL. You accept worse concurrency in exchange for fewer sidecar files for the sync daemon to mishandle.
- See [https://www.sqlite.org/howtocorrupt.html](https://www.sqlite.org/howtocorrupt.html) for authoritative guidance on SQLite + network/sync filesystems.
- **Do not share the same DB file between machines while both are running.** Sync drives are eventually consistent; concurrent writes from two hosts will corrupt the DB because file locks are local-filesystem-only.

**Concurrent writers on the same machine (shared workspaces):**

- SQLite uses OS-level file locks. Two processes on the same machine writing to the same file serialize correctly — one waits, the other writes. No data loss.
- Reads are unblocked by other readers.
- For a memory system's write volume (a handful of stores per minute), contention is invisible.

**Manual migration recipe:**

If you already have a database at the default location and want to move it:

```bash
# 1. Stop the MCP server — close any Claude Code window(s) using total-recall
# 2. Move the file (and its sidecars, if WAL is active)
mv ~/.total-recall/total-recall.db    ~/Dropbox/total-recall/memories.db
mv ~/.total-recall/total-recall.db-wal ~/Dropbox/total-recall/memories.db-wal 2>/dev/null || true
mv ~/.total-recall/total-recall.db-shm ~/Dropbox/total-recall/memories.db-shm 2>/dev/null || true
# 3. Set the env var (shell, .mcp.json, or settings.json)
export TOTAL_RECALL_DB_PATH=~/Dropbox/total-recall/memories.db
# 4. Restart Claude Code or your MCP host
```

total-recall does **not** auto-migrate this kind of relocation — silent migration on partial failure invites corruption. (This is distinct from the automatic 0.7.x → 0.8.x TS-to-.NET schema migration, which IS handled by `AutoMigrationGuard` on first launch with a non-destructive backup of the old TS database to `total-recall.db.ts-backup`.)

## Quick Install (Any Platform)

The MCP server is available as an npm package:

```bash
npm install -g @strvmarv/total-recall
```

This installs the current stable version (`0.7.2` TypeScript at the time of this writing; will be `0.8.x` .NET after the cutover). To install the .NET beta:

```bash
npm install -g @strvmarv/total-recall@beta
```

## Claude Code

### Option A: Plugin Install (Recommended)

Run in Claude Code:

```
/plugin install total-recall@strvmarv-total-recall-marketplace
```

If the marketplace isn't registered yet:

```
/plugin marketplace add strvmarv/total-recall-marketplace
/plugin install total-recall@strvmarv-total-recall-marketplace
```

### Option B: MCP Server Only (any tool)

Add to your Claude Code MCP config (`~/.claude.json`):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

This requires `npm install -g @strvmarv/total-recall` (or `@beta`) so the `total-recall` command is on PATH.

## GitHub Copilot CLI

### Option A: Plugin install (recommended)

[Copilot CLI](https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli) installs total-recall as a plugin from the total-recall marketplace. No `mcp-config.json` or hooks editing is needed:

```bash
copilot plugin marketplace add strvmarv/total-recall-marketplace
copilot plugin install total-recall@strvmarv-total-recall-marketplace
```

Then start a new session. The git install carries no engine binary: the MCP server downloads and verifies it on first launch (a few seconds), and tools return a "not ready" result until it is there; the pinned-directive floor stays silent until then. This works once `strvmarv/total-recall-marketplace` points at a release that contains `.github/plugin/` and `copilot/`; until it does, install from a checkout instead (`copilot plugin marketplace add <path-to-a-marketplace-repo>`, or `copilot --plugin-dir <checkout>` for one session).

**Allow the tools.** Copilot asks for approval before an MCP tool call. Interactively you can approve per call; non-interactively (`copilot -p`) a call that is not allowed is refused ("blocked by permissions") and `session_start` never runs. Allow just this server with `--allow-tool=total-recall` (add it to the command line or alias; avoid `--allow-all-tools`).

What you get in Copilot CLI (observed on GitHub Copilot CLI **1.0.98-2**, Windows, with `-p` and interactively; macOS/Linux are unobserved):

- The `total-recall` MCP server from the plugin's `.mcp.json`, the same file Claude Code uses (Copilot always prefers a root `.mcp.json` over a manifest `mcpServers` block).
- The startup instructions and capture / retrieve / feedback rules (`skills/commands/SKILL.md`), delivered by a `sessionStart` hook (`copilot/hook.js`) so the model calls `session_start`. Copilot starts the session lazily, with the first prompt.
- The pinned-directive floor on every prompt (`userPromptSubmitted` hook → `additionalContext`, which Copilot appends to the user message), on the turn-count trigger only (`floor_every_n_turns`, default 6): the hook payload has no transcript path, so the token-growth trigger is not used. Copilot's hooks documentation says command hooks cannot add context on this event; on the tested build they can, so a later build may change this (`docs/TODO.md` names the documented fallback).
- Skills: `/total-recall:commands <subcommand>` works as in Claude Code.

Not observed: a subagent run (the floor skips hook payloads that carry an `agentId`, per Copilot's hooks docs), macOS/Linux, and the interactive approval prompt.

The manifest is `.github/plugin/plugin.json` (Copilot's documented lookup finds it before `.claude-plugin/plugin.json`); its `hooks` path replaces the Claude-format `hooks/hooks.json`, which cannot run under Copilot on Windows (on Windows the unbraced `$CLAUDE_PLUGIN_ROOT` in those hook commands expands to nothing: observed).

Known gaps:

- **Server-side project scope.** Copilot starts the server in the plugin directory, so `session_start` resolves the project from there and injects only global pins and the plugin's own project docs, while the floor (which uses the session directory from the hook payload) also includes pins scoped to your repo. A session-directory workaround was rejected because Copilot's session record keeps the original directory after a resume from another one.
- **Compaction.** `/compact` fires no hook and its summary can drop the startup instructions and the pins; the floor re-asserts the pins on its schedule, but the instructions are not re-injected until the next session.
- No `SessionEnd` compaction nudge, no first-run tutorial, and the `setup` and `update` subcommands of the commands skill have no Copilot meaning (use `copilot plugin update total-recall@strvmarv-total-recall-marketplace`).
- The engine's importers read your real `~/.claude`, `~/.copilot` and similar directories regardless of a redirected profile on Windows, so a throwaway test setup still imports your real memories into its own store.

### Option B: MCP server only

Add to your Copilot CLI MCP config (`~/.copilot/mcp-config.json`):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

This requires `npm install -g @strvmarv/total-recall` so the command is on PATH; you get the tools but no startup instructions and no floor.

## OpenCode

See `.opencode/INSTALL.md` for the full OpenCode-specific install guide. The short version: `npm install -g @strvmarv/total-recall` then add the same `mcpServers` block to your OpenCode config.

## pi

[pi](https://pi.dev) installs total-recall as a pi package. No `mcp.json` or settings editing is needed:

```bash
pi install npm:@strvmarv/total-recall
# or straight from git
pi install git:github.com/strvmarv/total-recall
```

The package is large (about 378 MB unpacked: the engine binaries for every platform plus the ~133 MB embedding model), so the first install takes a while. The `npm:` form works once the release that carries pi support is published (npm 4.2.3 predates it); until then use the `git:` form. `postinstall` fetches the engine for your platform and, as for any install of this package, adds a total-recall MCP entry (without overwriting an existing one) to any other supported tool it detects (VS Code, Copilot CLI, Cline, Windsurf, Gemini CLI, Zed, Kiro, Hermes). If the engine is missing (`--ignore-scripts`, offline install), the MCP server downloads and verifies it on first launch.

What you get in pi, at parity with Claude Code unless noted:

- The `total-recall` MCP server (registered by the extension; a `total-recall` entry you already have in `~/.pi/agent/mcp.json` (or the project's `.pi/mcp.json`) wins over it; the pinned floor then follows that entry's plain `TOTAL_RECALL_*` env values, such as `TOTAL_RECALL_HOME`, so it reads the same store, and it stays silent if the entry is remote, disabled, or sets those values with `${VAR}` or `!command`). Most tools are reached through pi's `tool_search`; `session_start`, `session_end`, `session_refresh`, `memory_*` and `kb_search` are declared directly.
- The same startup instructions and capture / retrieve / feedback rules (`skills/commands/SKILL.md`), injected as a hidden message with your first prompt of a session and again after a compaction.
- The pinned-directive floor on every prompt, on the turn-count trigger only (`floor_every_n_turns`, default 6). The token-growth trigger is not used in pi: pi creates its session file only after the first message, which would trigger an early floor on turn 2. It stays silent until the engine is provisioned.
- `/total-recall <subcommand>` (alias `/total-recall:commands`), with tab completion of the subcommands. It asks the model to run the command (`help`, `status`, `search <query>`, `pin <id>`, `forget <query>`, `kb ...`, `compact`, ...), so it works in an interactive or RPC session; in `pi -p "/total-recall help"` pi exits before the model's answer is printed.

The injected instructions also tell the model to run this package's CLI as `node <package>/bin/start.js <args>` (for `compact --run`, `ui`), because a plain `pi install` does not put `total-recall` on your PATH. If you navigate with `/tree` to a point before the instructions were delivered, they are queued again for your next prompt.

Known gaps: nothing triggers `session_end` in pi (it is bookkeeping only), the `setup` and `update` subcommands have no pi meaning (use `pi update`), and `compact` defaults to a compactor agent that pi does not have, so use `compact --fast` or let the model make the calls from `session_context`. Subagent children (pi-subagents, pi-herdr-agents) and `--no-session` runs get the MCP tools but no injected instructions, floor, or `/total-recall`.

## Codex CLI

[Codex CLI](https://developers.openai.com/codex) installs total-recall as a plugin from the total-recall marketplace. No `config.toml`, `hooks.json` or `mcp.json` editing is needed:

```bash
codex plugin marketplace add strvmarv/total-recall-marketplace
codex plugin add total-recall@strvmarv-total-recall-marketplace
```

(or browse to it with `/plugins` inside Codex), then start a new session. The git install carries no engine binary: the MCP server downloads and verifies it on first launch (a few seconds), and tools report "provisioning" until it is ready. The plugin resolves through the separate marketplace repo, so this path works once that repo's `ref` points at a release containing `.codex-plugin/` and `codex/`.

**Trust the hooks once.** Codex skips plugin hooks until you review them: open `/hooks` in Codex and trust the two total-recall hooks. A plugin update that changes a bundled script asks for a new review. (`codex exec --dangerously-bypass-hook-trust` skips the review for one invocation; it is meant for automation that already vets its hooks, and the review flow itself has not been observed interactively here, so this follows Codex's documentation.)

**Allow the tools.** Codex asks for approval before calling an MCP tool that is not marked read-only, and none of total-recall's are. In an interactive session you should be able to approve per call (the interactive prompt was not observed here; this is Codex's documented behavior). Non-interactive runs (`codex exec`) cannot answer, so the model's `session_start` call is refused with "MCP tool call requires approval, but approval policy is never". To allow every total-recall tool without prompting (this includes the tools that write and delete memories), add to `~/.codex/config.toml`:

```toml
[plugins."total-recall@strvmarv-total-recall-marketplace".mcp_servers.total-recall]
default_tools_approval_mode = "approve"
```

What you get in Codex, at parity with Claude Code unless noted:

- The `total-recall` MCP server, started from the plugin's bundled `codex/mcp.json`. Codex expands no variables in a bundled server's command, so that file runs a small launcher that finds the installed plugin under `~/.codex/plugins/cache/` and starts `bin/start.js` from there; it sets no working directory, so project-scoped pins follow the directory you run Codex in. Codex starts the server with a sanitized environment, so the file forwards the variables the engine needs (`TOTAL_RECALL_*`, `HOME`, `XDG_*`, `CODEX_HOME`, proxy and CA settings); the hook runs with your full environment, and forwarding keeps both on the same store.
- The same startup instructions and capture / retrieve / feedback rules (`skills/commands/SKILL.md`), delivered by a `SessionStart` hook on `startup` and `resume` (both observed) and on `clear` and `compact` (matched because Codex documents them as session-start sources; not observed), plus a short note about Codex. Resuming a session (`codex exec resume`, `codex resume`) delivers them again, as under Claude Code.
- The pinned-directive floor on every prompt (`UserPromptSubmit` hook), on the turn-count trigger only (`floor_every_n_turns`, default 6). The token-growth trigger is not used: a Codex session log is several times larger than the conversation it records (per-turn bookkeeping, reasoning, tool output, the injected text itself), so the byte-growth heuristic would re-inject on most prompts. The floor stays silent until the engine is provisioned. Codex keeps at most about 16,000 tokens of a hook's context before replacing the rest with a preview, so a pinned block beyond roughly 125 default-size pins would lose its tail.
- The commands skill is listed by Codex as `total-recall:total-recall:commands`; invoke it as `$total-recall:total-recall:commands` or from `/skills` (the listing was observed; the invocation follows Codex's documentation).

Known gaps: no `SessionEnd` compaction nudge (Codex gives that hook 3 seconds and no way to show output to you; the engine's `session_start` hints still report uncompacted entries), and the `setup` and `update` subcommands are Claude Code specific. Windows is the verified platform; macOS and Linux follow the same design but have not been run. A custom `CODEX_HOME` works because the launcher forwards it; a relative one does not (it would resolve against the server's directory, not Codex's).

Updating: run `codex plugin add total-recall@strvmarv-total-recall-marketplace` again (Codex replaces the old version directory), start a new session, and re-trust the hooks if `/hooks` asks. Troubleshooting: `codex plugin list` shows the installed version, `codex mcp list` shows the launcher entry, and a stderr line `total-recall plugin not found under ...` means the plugin is not in Codex's cache for the `CODEX_HOME` the server was given.

## Cline (VS Code)

Add to your Cline MCP settings:

1. Open VS Code Command Palette
2. Search "Cline: MCP Settings"
3. Add:
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## Cursor

Observed on Cursor CLI (`cursor-agent`) **2026.11.10-731fb78**, Windows, with `-p` and interactively. macOS/Linux are **unsupported until observed**: the plugin's MCP server runs in a sanitized environment, and a POSIX server without `HOME` or `USERPROFILE` would make the engine create a literal `~/.total-recall` directory in your project. The Cursor desktop IDE agent, subagents and cloud agents were not tested.

### Option A: local plugin (recommended)

Put a copy of this repository (a `git clone`, or the npm package contents) at `~/.cursor/plugins/local/total-recall` and restart Cursor (or run `Developer: Reload Window`). A symlink only loads when its target resolves inside `~/.cursor/plugins/local`. For one run, `cursor-agent --plugin-dir <checkout>` loads it without copying. The checkout needs the engine binary: either install the npm package (it ships `binaries/`) or let the MCP server download and verify it on first launch (the download path was exercised in other hosts but not inside Cursor; tools return "not ready" and the floor stays silent until the engine is there).

The marketplace flow (`cursor-agent plugin marketplace add https://github.com/strvmarv/total-recall-marketplace`, then `/plugins`) indexes the existing marketplace, but which manifest Cursor loads from it (`.cursor-plugin/plugin.json` or the Claude one) was **not verified**, so it is not a supported install path yet.

**Approve the server.** Cursor asks before using a plugin's MCP server (it shows as `plugin-total-recall-total-recall`). Interactively, approve it; with `cursor-agent -p`, pass `--approve-mcps`. The unapproved refusal text was not observed.

What you get in Cursor:

- The `total-recall` MCP server from `cursor/mcp.json` (`node ${CURSOR_PLUGIN_ROOT}/bin/start.js`). It has no `cwd`, so Cursor starts it in your workspace and project-scoped pins work server-side (observed), unlike Claude Code and Copilot CLI.
- The startup instructions (`skills/commands/SKILL.md` plus a short Cursor note), delivered by a `sessionStart` hook (`cursor/hook.js`) as `additional_context`, so the model calls `session_start` itself (observed 3/3 fresh conversations with the whole 18.6 KB intact, using the earlier wording of the appended Cursor note; the final note wording is unit-tested only because the live account hit its usage limit). The hook runs once per conversation, not again on `--continue`.
- The pinned-directive floor, on the **first tool call of each user message** (`postToolUse` hook → `additional_context`), on the turn-count trigger only (`floor_every_n_turns`, default 6, counted in tool-using messages): Cursor's CLI never fires a per-prompt hook (`beforeSubmitPrompt`, `stop` and `afterAgentResponse` did not fire, and `beforeSubmitPrompt` cannot add context anyway). Interactively the cadence is exact (the 7th tool-using message injects by default). In `-p` mode Cursor reuses one generation id for the whole conversation, even across `--continue`, so the floor counts once per `-p` conversation.
- Skills: `commands` and `using-total-recall`.

Known gaps and limits:

- **First `session_start` can time out.** On a fresh store the first call imports your other tools' history and can exceed Cursor's 60 second MCP request limit, which a plugin cannot raise (seen with a very large history: about 87 s). The hook's note tells the model to call it once more; the import is incremental. In `-p` the process exit kills the engine mid-import (both `-p` runs on a cold large store timed out), so on a big history repeat the run or warm the store with one interactive session first.
- **No custom store.** Cursor starts plugin servers with a sanitized environment, so `TOTAL_RECALL_HOME`, `XDG_*` and `HERMES_HOME` cannot reach the server; the hook ignores them too so the floor and the server agree. Do not combine the plugin with a second hand-configured `total-recall` server (two servers, and the hook would read the default store while the second reads yours); if you need a non-default store, use Option B instead of the plugin. On Windows a `HOME` that differs from `USERPROFILE` (MSYS/Git Bash setups) also splits stores between Cursor (`%USERPROFILE%\.total-recall`) and Claude Code or Copilot CLI (`$HOME/.total-recall`).
- **Floor only on turns that use a tool**, and a message whose tools all fail does not count. Subagent tool calls carry the parent's conversation id and may not be distinguishable from the parent's (a subagent run was not observed).
- **Compaction.** `preCompact` fires on a manual `/compact` but cannot carry context, and `sessionStart` does not fire again, so nothing re-injects the instructions after a compaction (whether they survive it was not observed). The floor keeps re-asserting the pins.
- No `SessionEnd` nudge, no first-run tutorial, and the `setup` and `update` subcommands of the commands skill have no Cursor meaning. `hooks/hooks-cursor.json` (Claude-format, runs `bash`) is kept for hand-wired setups and is untested.
- The engine's importers read your real `~/.claude`, `~/.cursor` and similar directories regardless of a redirected profile on Windows.
- Contributors: run `cursor-agent` from PowerShell or cmd on Windows. From Git Bash a Cursor CLI bug blocks every tool call.

### Option B: MCP server only (no plugin)

Add to your Cursor MCP config (`.cursor/mcp.json` or global settings):

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

This requires `npm install -g @strvmarv/total-recall` so the command is on PATH. You get the tools but no startup instructions and no floor. Use it instead of, never together with, the plugin.

## Verification

After installation:

```bash
total-recall --version
# Expected: total-recall 0.8.0 (or 0.7.2 if you're on stable)

total-recall status
# Expected: tier counts, KB info, embedding model "bge-small-en-v1.5", schema version
```

In a Claude Code (or other host) session, the first session output should include:

```
total-recall: initialized · X memories imported · Y docs ingested · system verified
```

You can verify it's working with:
- `/total-recall:commands status` — shows tier sizes and health
- `/total-recall:commands search "test"` — runs a test search

## Launch the web UI

total-recall includes a built-in browser UI served directly from the same binary:

```bash
total-recall ui
```

This starts a local server on port **5577** (default) and opens the browser automatically. The access token is embedded in the served page — no credentials to copy. The UI provides six sections: Dashboard, Memory, Knowledge Base, Usage, Insights, and Config.

Useful flags:

```bash
total-recall ui --port 5600      # use a different port
total-recall ui --no-open        # start without auto-opening the browser
total-recall ui --host 0.0.0.0   # bind all interfaces (use with caution on shared machines)
total-recall ui --smoke          # CI/scripted check: probe /api/health and exit 0 or 1
```

The server binds loopback only by default and uses an ephemeral per-launch token for security. No separate install or Node.js is needed.

## What Happens on First Run

1. Creates `~/.total-recall/` directory if missing
2. Creates SQLite database with schema (`Schema.cs` MigrationRunner applies all migrations 1..5)
3. Loads the bundled `bge-small-en-v1.5` ONNX embedding model from `models/` (no download needed at runtime — it was fetched and sha256-verified at build time and shipped inside the artifact)
4. Scans for existing memories from host tools (Claude Code, Copilot CLI, Cursor, Cline, OpenCode, Hermes), deduplicates via content hash
5. Auto-ingests project docs (README, docs/, etc.) into a `<project>-project-docs` KB collection
6. Runs a quick smoke test (22-query benchmark) to verify retrieval quality

If you're upgrading from 0.7.x (TypeScript) to 0.8.x (.NET), the `AutoMigrationGuard` runs on first launch:

- Detects an existing TS-format database via the absence of the `.NET` schema marker
- Renames the existing `total-recall.db` to `total-recall.db.ts-backup` (the original is **never deleted**)
- Runs the .NET MigrationRunner against a fresh database, then re-imports the TS data with re-embedding (the .NET tokenizer is canonical BERT BasicTokenization, slightly more accurate than the prior hand-rolled WordPiece — see `docs/superpowers/specs/2026-04-07-rewrite-language-evaluation.md`).
- If the migration is interrupted partway through and you end up with both `total-recall.db` and `total-recall.db.ts-backup` on disk, the next launch's guard handles all 5 partial-state cases automatically (since 0.8.0-beta.7).
