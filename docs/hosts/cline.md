# total-recall on Cline

## Install

No stop condition. The install script finds Cline by the directories Cline creates and writes the entry to the settings file in each one that exists: `~/.cline/data/settings/cline_mcp_settings.json` (current Cline) and `settings/cline_mcp_settings.json` inside the `saoudrizwan.claude-dev` folder under `globalStorage` in VS Code's `User` directory (older Cline; `~/Library/Application Support/Code/User` on macOS, `%APPDATA%\Code\User` on Windows, `~/.config/Code/User` elsewhere, or `$VSCODE_USER_DIR`). The first is the file the repository's Cline importer reads (`ClineImporter.cs`); the second matches the importer only on Linux, because the importer's older path is a Linux path on every operating system. `CLINE_HOME` is an explicit override: it is the directory that holds `cline_mcp_settings.json` itself, it is written (and created) without any check, and then nothing else is written. When none of these exists the script writes no Cline file. It never uses `~/.vscode`. Whether Cline loads the file the script wrote was not observed.

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

### Both paths

On Path 1 the script has added the entry to Cline's settings file when it found a Cline directory (see above); an earlier install may also have left `~/.vscode/cline_mcp_settings.json`, which Cline does not read and the user can delete. On Path 2, and for a Cline the script did not find, the entry is added by the user through Cline's own command (see User steps). Nothing on either path tells the model to call `session_start`; see User steps. The entry:

```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```

## User steps

1. In VS Code, open the Command Palette and run "Cline: MCP Settings".
2. In the file that opens, add the `total-recall` entry above to `mcpServers`, keeping what is already there (if `mcpServers` already exists, add only the `"total-recall": {...}` member inside it). The agent does not search this file: the user looks for an existing entry, which the install script may have written (an `npx` entry; leave it). If a `total-recall` entry is already in that file, do not add a second one: the user decides whether to replace it. The agent only supplies text and never edits that file.
3. This is a user step; do not write the file yourself. Fetch the file linked here and give the user the text to add to your harness's persistent instructions file. The text is the body, below its front matter, of [`.kiro/steering/total-recall.md`](https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md); its "Sticky directives" paragraph describes Kiro's hook and does not apply here.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. After Path 1 the agent can read the file the script wrote (the path is in the install script's output and in `postinstall.log`, see [What the install script changes](../npm-postinstall.md#what-it-reports)) and report whether it holds a `total-recall` entry; it cannot check that Cline loaded it, and the user's "Cline: MCP Settings" file shows what Cline uses. Where the script found no Cline directory, report the entry as pending with the user. If `total-recall --version` is not found or differs, report and stop; the user can open a new terminal or fix PATH. The startup status line appears only after the user completes User step 3; without it, the check prompt is the test.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server in Cline.
- Updating: run `npm install -g @strvmarv/total-recall` again (add `--ignore-scripts` if you installed that way), then restart Cline.

## Known gaps

- No hooks, no pinned-directive floor and no startup instructions: MCP tools only (from `AGENTS.md`). The instructions are a user step.
- Cline loading the file the script wrote was not observed, and the script writes only where a Cline directory already exists: a Cline that keeps its settings elsewhere (a VS Code fork such as Cursor, VSCodium or Insiders, for one) is not found, and its user adds the entry by hand.
- On Windows npm installs `total-recall` as a `.cmd` shim. Whether Cline starts a bare `total-recall` there was not observed; if the server does not start, the user can give the full path of `total-recall.cmd` (from `where.exe total-recall` in PowerShell, where plain `where` is another command; JSON needs the backslashes doubled; never change PowerShell's execution policy, report instead) as the command.
- The plain install writes its entry with `npx --yes @strvmarv/total-recall@<version>`, pinned to the installed version, and a later install refreshes an entry in exactly that form (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).

## Evidence

The manual steps are from the earlier `INSTALL.md`. The script's locations are from `scripts/install-cline.js` and the importer's paths from `src/TotalRecall.Infrastructure/Importers/ClineImporter.cs`, which the script matches for `~/.cline/data` and, on Linux, for the older layout; neither was run against a real Cline, and Cline itself was not run. The script is tested from a copy of the package against throwaway home directories (`scripts/host-config.test.js`).
