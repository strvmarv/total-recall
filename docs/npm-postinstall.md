# What the npm install script changes

Installing the package with scripts enabled (`npm install -g @strvmarv/total-recall`, and also `pi install npm:` or `pi install git:` for pi, which were observed to run it) runs `scripts/postinstall.js`. It first fetches the engine for the platform (a failure is only a warning; the engine is then downloaded on first launch, see [Provisioning on first launch](first-run.md#provisioning-on-first-launch)). It then looks for each tool below and, for the ones it finds on the machine, **writes files outside the package**, without asking.

Every step is skipped when its tool is not found, and every step is non-fatal: a failure is logged to stderr with the prefix `[total-recall:postinstall]` and the install goes on.

## What it changes

| Tool | Found when | What it writes |
|---|---|---|
| Hermes | `HERMES_HOME` is set, or `~/.hermes/config.yaml` exists (on Windows also `%APPDATA%\hermes\config.yaml`) | Copies the package's `hermes-plugin/` to `<hermes home>/plugins/total-recall/`, overwriting files there. If the `hermes` command works, runs `hermes config set memory.provider total-recall`, which **switches Hermes's memory provider**. |
| Kiro | `KIRO_HOME` is set, or `~/.kiro` exists | Adds a `total-recall` entry to `mcpServers` in `~/.kiro/settings/mcp.json` (with `cwd` set to `${workspaceFolder}`), and copies the steering file to `~/.kiro/steering/total-recall.md`, overwriting it. It does **not** copy the hooks in `.kiro/hooks/`; those are per workspace. |
| Cline | `CLINE_HOME` is set, or `~/.vscode` exists | Adds a `total-recall` entry to `mcpServers` in `<that directory>/cline_mcp_settings.json` (by default `~/.vscode/cline_mcp_settings.json`). The repository's Cline importer reads Cline's settings from another location, so this entry is not evidence that Cline is configured. |
| Windsurf | `WINDSURF_HOME` is set, or `~/.codeium/windsurf` exists | Adds a `total-recall` entry to `mcpServers` in `mcp_config.json`, and writes the steering text (without its front matter) to `rules/total-recall.md` in the same directory, overwriting it. |
| Gemini CLI | `GEMINI_HOME` is set, or `~/.gemini` exists | In `settings.json`: adds a `total-recall` entry to `mcpServers` and a **`BeforeAgent` hook** named `total-recall Pinned Floor` that runs `npx --yes @strvmarv/total-recall pinned-floor --host gemini-cli` (timeout 60000 ms) **on every prompt**. In `GEMINI.md`: inserts or replaces a block between `<!-- total-recall -->` and `<!-- /total-recall -->` that tells the model to call `session_start`. |
| Zed | `ZED_SETTINGS` is set, or the Zed settings directory exists (`~/Library/Application Support/Zed` on macOS, `%APPDATA%\Zed` on Windows, `~/.config/zed` elsewhere) | Adds a `total-recall` entry to `context_servers` in `settings.json`. |
| VS Code | `VSCODE_USER_DIR` is set, or the VS Code `User` directory exists (`~/Library/Application Support/Code/User`, `%APPDATA%\Code\User`, `~/.config/Code/User`) | Adds a `total-recall` entry to `servers` in `mcp.json` there, and, because VS Code was found, also to `mcpServers` in `~/.copilot/mcp-config.json`, which is **Copilot CLI's** MCP configuration. |

How an entry is merged, for every JSON file above:

- If the file already has a `total-recall` entry, it is left alone. An entry that already runs `npx` with `@strvmarv/total-recall` is silently kept; any other entry is kept and a line says so on stderr.
- If the file exists but does not parse as strict JSON (for example a `settings.json` with comments), it is left untouched and a line says so. Gemini CLI's hook and `GEMINI.md` steps follow the same file rule for `settings.json`; the `GEMINI.md` step runs regardless.
- Otherwise the entry is added and the whole file is **rewritten** as JSON with two-space indentation, so formatting that differs from that is lost.
- A directory that is missing is created.

When the package is not installed under a `node_modules` directory (a git checkout, which is what `pi install git:` produces), the install script treats it as a development install and skips the Kiro MCP entry and steering file, the Windsurf rules file and the `GEMINI.md` block. Everything else above still applies.

The paths and what each step writes are read from `scripts/postinstall.js` and `scripts/install-*.js`; this page was not produced by running them on every tool.

## The npx entries

Every MCP entry above is `npx --yes @strvmarv/total-recall`, with **no version**. `npx` runs a copy of the package that is already installed before it fetches one, so an older global install can answer instead of the latest release. Observed: with a global 4.0.4 installed while the latest release was 4.7.0, the unversioned command ran 4.0.4, while `npx --yes @strvmarv/total-recall@4.7.0` ran 4.7.0 and served 50 tools. Keep the global install current, or pin a version in the entry.

To see what is installed without running anything, use `npm ls -g @strvmarv/total-recall`. `npx --yes @strvmarv/total-recall --version` also answers, but it downloads the package when no copy is installed and, by npm's defaults, runs the package's install script then (not observed).

## How to skip it

Install without running the script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

For pi (POSIX shell; not run on Windows):

```sh
npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall
```

No skip form was observed for `pi install git:`.

Skipping the script also skips the engine download at install time; the first launch downloads it instead (see [first run](first-run.md#provisioning-on-first-launch)). Nothing the script would have written is then configured, so each harness page gives the entry to write by hand.
