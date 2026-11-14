# What the install script changes

Installing the package with scripts enabled (`npm install -g @strvmarv/total-recall`, and also `pi install npm:` or `pi install git:` for pi, which were observed to run it) runs `scripts/postinstall.js`. It first makes sure the engine for the platform is present: the npm package already ships it, so this does nothing there, and a failure is only a warning (the engine is then downloaded on first launch, see [Provisioning on first launch](first-run.md#provisioning-on-first-launch)). It then looks for each tool below and, for the ones it finds on the machine, **writes files outside the package**, without asking.

Every step is skipped when its tool is not found. Every step is wrapped so that a failure is logged to stderr with the prefix `[total-recall:postinstall]`, listed in the summary below, and the script goes on. The script always exits 0.

## What it changes

| Tool | Found when | What it writes |
|---|---|---|
| Hermes | `HERMES_HOME` is set, or `~/.hermes/config.yaml` exists (on Windows also `%APPDATA%\hermes\config.yaml`) | Copies the package's `hermes-plugin/` to `<hermes home>/plugins/total-recall/`, overwriting files there. If the `hermes` command works, runs `hermes config set memory.provider total-recall`, which **switches Hermes's memory provider**. |
| Kiro | `KIRO_HOME` is set, or `~/.kiro` exists | Adds a `total-recall` entry to `mcpServers` in `<kiro home>/settings/mcp.json` (with `cwd` set to `${workspaceFolder}`; `<kiro home>` is `KIRO_HOME` or `~/.kiro`), and copies the steering file to `<kiro home>/steering/total-recall.md`, overwriting it. It does **not** copy the hooks in `.kiro/hooks/`; those are per workspace. |
| Cline | `CLINE_HOME` is set, or `~/.vscode` exists | Adds a `total-recall` entry to `mcpServers` in `<that directory>/cline_mcp_settings.json` (by default `~/.vscode/cline_mcp_settings.json`). The repository's Cline importer reads Cline's settings from another location, so this entry is not evidence that Cline is configured. |
| Windsurf | `WINDSURF_HOME` is set, or `~/.codeium/windsurf` exists | Adds a `total-recall` entry to `mcpServers` in `mcp_config.json`, and writes the steering text (without its front matter) to `rules/total-recall.md` in the same directory, overwriting it. |
| Gemini CLI | `GEMINI_HOME` is set, or `~/.gemini` exists | In `settings.json`: adds a `total-recall` entry to `mcpServers` and a **`BeforeAgent` hook** named `total-recall Pinned Floor` that runs `npx --yes @strvmarv/total-recall pinned-floor --host gemini-cli` (timeout 60000 ms) **on every prompt**. In `GEMINI.md`: inserts or replaces a block between `<!-- total-recall -->` and `<!-- /total-recall -->` that tells the model to call `session_start`. |
| Zed | `ZED_SETTINGS` is set (it names the settings **directory**), or the Zed settings directory exists (`~/Library/Application Support/Zed` on macOS, `%APPDATA%\Zed` on Windows, `~/.config/zed` elsewhere) | Adds a `total-recall` entry to `context_servers` in `settings.json` in that directory. |
| VS Code | The VS Code `User` directory exists (`VSCODE_USER_DIR` if set, else `~/Library/Application Support/Code/User`, `%APPDATA%\Code\User` or `~/.config/Code/User`) | Adds a `total-recall` entry to `servers` in `mcp.json` there, and, because VS Code was found, also to `mcpServers` in `~/.copilot/mcp-config.json`, which is **Copilot CLI's** MCP configuration. |

How an entry is merged, for every JSON file above:

- If the file already has a `total-recall` entry, it is left alone. An entry that already runs `npx` with `@strvmarv/total-recall` is silently kept; any other entry is kept and a line says so on stderr.
- If the file exists but does not parse as strict JSON (for example a `settings.json` with comments), it is left untouched and a line says so.
- Otherwise the entry is added and the whole file is **rewritten** as JSON with two-space indentation, so formatting that differs from that is lost.
- A directory that is missing is created.

Gemini CLI is the exception to the first bullet. Its `settings.json` is read and rewritten by its own code: an existing `total-recall` MCP entry is kept, but the `BeforeAgent` hook is still added when it is missing, and a file that parses is always rewritten at two-space indentation. Only the unparsable-file rule applies unchanged (the file is left alone), and the `GEMINI.md` step then still runs.

When the package is not installed under a `node_modules` directory (a git checkout, which is what `pi install git:` produces), the install script treats it as a development install and skips the Kiro MCP entry and steering file, the Windsurf rules file and the `GEMINI.md` block. Everything else above still applies.

The paths and what each step writes are read from `scripts/postinstall.js` and `scripts/install-*.js`; this page was not produced by running them on every tool.

## What it reports

When the script has finished with the other tools it prints one summary on stderr: a line for each file it wrote and for each step that failed, giving the tool, what it did and the full path.

```text
[total-recall:postinstall] Changed other tools (4 changes):
[total-recall:postinstall]   Gemini CLI: rewrote /home/me/.gemini/settings.json
[total-recall:postinstall]   Gemini CLI: added BeforeAgent hook (runs on every prompt) in /home/me/.gemini/settings.json
[total-recall:postinstall]   Hermes: added /home/me/.hermes/plugins/total-recall
[total-recall:postinstall]   Hermes: switched memory.provider to total-recall (hermes config set)
```

The actions are `added` (the file did not exist), `rewrote` (an existing file was changed or rewritten, including one that only gained an entry), `overwrote` (a steering or rules file or a plugin directory that was already there), `switched` (the Hermes memory provider) and `failed` (a step that threw, with the message). Gemini CLI's `settings.json` is rewritten on every run, so it is listed on every run; its MCP entry and its hook get a line of their own only when they are newly added. An entry that is kept, and a file that does not parse, are not listed. When nothing was written and nothing failed, the summary is one line: `No changes were made to other tools.`

npm often hides stderr for a global install, so the same lines are also appended to `postinstall.log` in the data directory: `$TOTAL_RECALL_HOME` when that is set, else `~/.total-recall`. Each entry has a heading with the UTC date and time and the package version. The log is only ever appended to, so a later install that changes nothing leaves the record of an earlier one in place, and a run that wrote nothing and failed nothing adds no entry. If the data directory cannot be found or written, the summary is still printed and the install goes on.

This was checked by running the install script from a copy of the package against throwaway home directories and a stand-in `hermes` command, not against the real tools.

## The npx entries

Every MCP entry above is `npx --yes @strvmarv/total-recall`, with **no version**. `npx` runs a copy of the package that is already installed before it fetches one, so an older global install can answer instead of the latest release. Observed: with a global 4.0.4 installed while the latest release was 4.7.0, the unversioned command ran 4.0.4, while `npx --yes @strvmarv/total-recall@4.7.0` ran 4.7.0 and served 50 tools. Keep the global install current, or pin a version in the entry.

To see what is installed without running anything, use `npm ls -g @strvmarv/total-recall`. `npx --yes @strvmarv/total-recall --version` also answers, but it downloads the package when no copy is installed and, by npm's defaults, runs the package's install script then (not observed).

## How to skip it

To keep the engine step but leave every other tool alone, set `TOTAL_RECALL_SKIP_HOST_CONFIG=1` (exactly `1`) for the install. The script then writes no file of another tool, runs no `hermes` command, adds nothing to `postinstall.log`, and prints one line saying the other tools were skipped:

```sh
TOTAL_RECALL_SKIP_HOST_CONFIG=1 npm install -g @strvmarv/total-recall
```

Or install without running the script at all:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

For pi (POSIX shell; not run on Windows):

```sh
npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall
```

No skip form was observed for `pi install git:`.

The npm package already ships the engine for every platform, so skipping the script loses nothing there. A git-sourced install downloads the engine on first launch instead (see [first run](first-run.md#provisioning-on-first-launch)). Nothing the script would have written is then configured, so each harness page gives the entry to write by hand.
