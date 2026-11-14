# total-recall on Hermes

## Install

No stop condition. Hermes has no MCP entry to write: total-recall is a Hermes memory provider. The install script copies the `hermes-plugin/` directory into Hermes's plugins directory and switches the memory provider. It runs when `$HERMES_HOME` is set, or when a Hermes home with a `config.yaml` exists (`~/.hermes`, on Windows also `%APPDATA%\hermes`).

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

```sh
npm install -g @strvmarv/total-recall
```

If the `hermes` command is not on PATH, or its `config set` fails, the script copies the plugin and leaves the provider unset, so check as described under Verify.

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then each step below is its own choice for the user; the agent takes the ones the user accepts. The installed package is located with `npm root -g` (the package is `<npm root -g>/@strvmarv/total-recall`; not run).

1. **Ask first**: copy `<npm root -g>/@strvmarv/total-recall/hermes-plugin/` to `<hermes home>/plugins/total-recall/`, creating the directory if needed.
2. **Ask first**: switch Hermes's memory provider to total-recall. After this Hermes's memory tool writes to total-recall instead of Hermes's own `MEMORY.md` and `USER.md` files.
```sh
hermes config set memory.provider total-recall
```

## User steps

1. Start a new Hermes session.
2. In that session, ask Hermes to remember a phrase of your choosing.
3. Then run `total-recall memory recent` and confirm the phrase is listed. Hermes's model sees one tool, `memory`, so a memory read-back inside Hermes passes whichever provider is active; this command is the check.

## Verify

Now (both marked not run): confirm that `<hermes home>/plugins/total-recall` exists and that `memory.provider` is `total-recall` in Hermes's `config.yaml`; for either that is missing, take the matching step of path 2. `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs.

The check prompt in `INSTALL.md`, Step 4, does not apply here: Hermes's model is given a single `memory` tool, with the same name and schema as Hermes's built-in memory tool, and not total-recall's MCP tools. The check is the one in User steps.

## What you get

A Hermes memory provider, from `hermes-plugin/provider.py` and `hermes-plugin/memory_tool_schema.py` (read, not run):

- Hermes's `memory` tool keeps its name and schema, and its add, replace, remove and read actions go to total-recall's MCP server instead of the local `MEMORY.md` and `USER.md` files.
- The provider starts the total-recall MCP server itself and calls `session_start` when a Hermes session begins, and `session_end` when it ends.
- It adds the hot-tier context (`session_context`) to the system prompt, searches memory (`memory_search`, top 5) for each turn's query, and stores each turn as a `surfaced` entry tagged `hermes-turn`.

## Known gaps

- The model is given the one `memory` tool, not total-recall's MCP tools. No per-turn pinned-directive floor was found in `hermes-plugin/`.
- The install script can copy the plugin and leave the provider unset (no `hermes` command on PATH, or the command fails).

## Evidence

Read from `scripts/install-hermes-plugin.js` and `hermes-plugin/`; nothing was run on Hermes, including the checks above.
