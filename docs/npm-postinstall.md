# What the install script changes

Installing the package with scripts enabled (`npm install -g @strvmarv/total-recall`, and also `pi install npm:` or `pi install git:` for pi, which were observed to run it) runs `scripts/postinstall.js`. It first makes sure the engine for the platform is present: the npm package already ships it, so this does nothing there, and a failure is only a warning (the engine is then downloaded on first launch, see [Provisioning on first launch](first-run.md#provisioning-on-first-launch)). It then looks for each tool below and, for the ones it finds on the machine, **writes files outside the package**, without asking.

Every step is skipped when its tool is not found. Every step is wrapped so that a failure is logged to stderr with the prefix `[total-recall:postinstall]`, listed in the summary below, and the script goes on. The script always exits 0.

## What it changes

| Tool | Found when | What it writes |
|---|---|---|
| Hermes | `HERMES_HOME` is set, or `~/.hermes/config.yaml` exists (on Windows also `%APPDATA%\hermes\config.yaml`) | Copies the package's `hermes-plugin/` to `<hermes home>/plugins/total-recall/`, overwriting files there. If the `hermes` command works, runs `hermes config set memory.provider total-recall`, which **switches Hermes's memory provider**. |
| Kiro | `KIRO_HOME` is set, or `~/.kiro` exists | Adds a `total-recall` entry to `mcpServers` in `<kiro home>/settings/mcp.json` (with `cwd` set to `${workspaceFolder}`; `<kiro home>` is `KIRO_HOME` or `~/.kiro`), and copies the steering file to `<kiro home>/steering/total-recall.md`, overwriting it. It does **not** copy the hooks in `.kiro/hooks/`; those are per workspace. |
| Cline | `CLINE_HOME` is set, or `~/.cline/data` exists, or the `saoudrizwan.claude-dev` folder exists under `globalStorage` in VS Code's `User` directory (see the VS Code row for where that is) | Adds a `total-recall` entry to `mcpServers` in `settings/cline_mcp_settings.json` inside each of the two Cline directories that exists (`~/.cline/data` and the `saoudrizwan.claude-dev` folder), the first of which is the file the repository's Cline importer reads (the importer's path for the second is a Linux path on every operating system, so it matches only on Linux). `CLINE_HOME` is an explicit override: it is the directory that holds `cline_mcp_settings.json` itself, it is written (and created) without any check, and nothing else is written then. The script never uses `~/.vscode`; a `~/.vscode/cline_mcp_settings.json` written by an earlier install is left in place and can be deleted. Cline loading the written file was not observed. |
| Windsurf | `WINDSURF_HOME` is set, or `~/.codeium/windsurf` exists | Adds a `total-recall` entry to `mcpServers` in `mcp_config.json`, and writes the steering text (without its front matter) to `rules/total-recall.md` in the same directory, overwriting it. |
| Gemini CLI | `GEMINI_HOME` is set, or `~/.gemini` exists | In `settings.json`: adds a `total-recall` entry to `mcpServers` and a **`BeforeAgent` hook** named `total-recall Pinned Floor` that runs `npx --yes @strvmarv/total-recall@<version> pinned-floor --host gemini-cli` (timeout 60000 ms) **on every prompt**. In `GEMINI.md`: inserts or replaces a block between `<!-- total-recall -->` and `<!-- /total-recall -->` that tells the model to call `session_start`. |
| Zed | `ZED_SETTINGS` is set (it names the settings **directory**), or the Zed settings directory exists (`~/Library/Application Support/Zed` on macOS, `%APPDATA%\Zed` on Windows, `~/.config/zed` elsewhere) | Adds a `total-recall` entry to `context_servers` in `settings.json` in that directory. |
| VS Code | The VS Code `User` directory exists (`VSCODE_USER_DIR` if set, else `~/Library/Application Support/Code/User`, `%APPDATA%\Code\User` or `~/.config/Code/User`) | Adds a `total-recall` entry to `servers` in `mcp.json` there, and, because VS Code was found, also to `mcpServers` in `~/.copilot/mcp-config.json`, which is **Copilot CLI's** MCP configuration. |

How an entry is merged, for every JSON file above:

- If the file already has a `total-recall` entry: an entry in exactly the form the script writes (`npx` with the arguments `--yes` and `@strvmarv/total-recall`, with or without `@<version>`) is moved to the version being installed (see [The npx entries](#the-npx-entries)); an `npx` entry that holds `@strvmarv/total-recall` among other arguments is silently kept; any other entry is kept and a line says so on stderr.
- If the file exists but does not parse as strict JSON (for example a `settings.json` with comments), it is left untouched and a line says so.
- Otherwise the entry is added, and when an entry is added or refreshed the script rewrites the whole file as JSON with two-space indentation, so formatting that differs from that is lost.
- A directory that is missing is created.

Gemini CLI is the exception to the first bullet. Its `settings.json` is read and rewritten by its own code: an existing `total-recall` MCP entry follows the same rules as above and the `BeforeAgent` hook is added when it is missing or, when it is in exactly the script's form, moved to the version being installed, and a file that parses is always rewritten at two-space indentation. Only the unparsable-file rule applies unchanged (the file is left alone), and the `GEMINI.md` step then still runs.

When the package is not installed under a `node_modules` directory (a git checkout, which is what `pi install git:` produces), the install script treats it as a development install and skips the Kiro MCP entry and steering file, the Windsurf rules file and the `GEMINI.md` block. Everything else above still applies, except that a checkout writes the entries and the hook without a version and never refreshes an existing one, because its version may not be published.

The paths and what each step writes are read from `scripts/postinstall.js` and `scripts/install-*.js`; this page was not produced by running them on every tool.

## What it reports

When the script has finished with the other tools it prints one summary on stderr: a line for each file it wrote and for each step that failed (a heading counts the entries), giving the tool, what it did and the full path.

```text
[total-recall:postinstall] Other tools (4 entries):
[total-recall:postinstall]   Gemini CLI: rewrote /home/me/.gemini/settings.json
[total-recall:postinstall]   Gemini CLI: added BeforeAgent hook (runs on every prompt) in /home/me/.gemini/settings.json
[total-recall:postinstall]   Hermes: added /home/me/.hermes/plugins/total-recall
[total-recall:postinstall]   Hermes: switched memory.provider to total-recall (hermes config set)
```

The actions are `added` (the file did not exist), `rewrote` (an existing file was changed or rewritten, including one that only gained an entry), `overwrote` (a steering or rules file or a plugin directory that was already there), `switched` (the Hermes memory provider) and `failed` (a step that threw, with the message). Gemini CLI's `settings.json` is rewritten on every run, so it is listed on every run; its MCP entry and its hook get a line of their own only when they are newly added or refreshed to the installed version. An entry that is kept, and a file that does not parse, are not listed. When nothing was written and nothing failed, the summary is one line: `No changes were made to other tools.`

npm often hides stderr for a global install, so the same lines are also appended to `postinstall.log` in the data directory: `$TOTAL_RECALL_HOME` when that is set, else `~/.total-recall`. Each entry has a heading with the UTC date and time and the package version. The log is only ever appended to, so a later install that changes nothing leaves the record of an earlier one in place, and a run that wrote nothing and failed nothing adds no entry. If the data directory cannot be found or written, the summary is still printed and the install goes on.

This was checked by running the install script from a copy of the package against throwaway home directories and a stand-in `hermes` command, not against the real tools.

## The npx entries

Every MCP entry above, and the Gemini CLI hook, runs `npx --yes @strvmarv/total-recall@<version>`, **pinned to the version of the package that was installed**. `npx` runs a copy of the package that is already installed before it fetches one, so without a version an older global install could answer instead of the release you just installed. Observed: with a global 4.0.4 installed while the latest release was 4.7.0, the unversioned command ran 4.0.4, while `npx --yes @strvmarv/total-recall@4.7.0` ran 4.7.0 and served 50 tools. Entries written by an earlier release have no version.

On each install the script refreshes the entries it wrote itself. An entry counts as the script's own when it is in exactly the form the script writes: the command `npx` with the arguments `--yes` and `@strvmarv/total-recall`, bare or with a valid version (other keys, such as Kiro's `cwd`, do not matter). Such an entry is moved to the version being installed and shows in the summary as a rewrite of its file (Gemini CLI also gets `rewrote MCP entry in <path>` or `rewrote BeforeAgent hook in <path>`); one that is already at that version is not touched. The Gemini CLI hook is refreshed the same way when its command is the script's own. Any other entry is left alone: another command or package, an `@latest` or a range, extra arguments next to a version, a hook with a different command. An `npx` entry that holds the bare name among other arguments (`-y` instead of `--yes`, say) is kept as it always was and is not refreshed. A refresh rewrites the whole file at two-space indentation, and a `settings.json` or other file with comments does not parse as strict JSON, so it is left as it is and not refreshed.

To hold a version by hand, install with `TOTAL_RECALL_SKIP_HOST_CONFIG=1` (see below), which leaves every other tool's file alone, or write an entry in a different form. A package that is not under `node_modules` (a git checkout) writes no version and refreshes nothing, and so does a package whose version cannot be read or is not a valid version; a tarball or `file:` install of a version that is not published would still be pinned to it.

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
