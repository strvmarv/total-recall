# total-recall on Gemini CLI

## Install

No stop condition. Gemini CLI is configured through `~/.gemini/settings.json` (or `$GEMINI_HOME/settings.json`) and `GEMINI.md` in the same directory; the install script does that only when `GEMINI_HOME` is set or `~/.gemini` exists.

### Path 1: the plain install (recommended)

**Ask first.** This install runs the package's install script, which may change other tools on the machine: Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code and Copilot CLI's MCP config. Beyond adding an MCP entry it can switch Hermes's memory provider and add a Gemini CLI hook that runs on every prompt. Tell the user this and get a yes before running it; the user asking you to install total-recall is not that yes. On a no, use the form that skips the install script, given below. The detail is in [What the install script changes](https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md).

Before running it, search `settings.json` for `total-recall` (see Install in the runbook for how to read a file). If an entry is already there, tell the user that the install keeps it but still adds the hook, and ask again.

```sh
npm install -g @strvmarv/total-recall
```

It writes the MCP entry and the `BeforeAgent` hook to `settings.json` and a marked block to `GEMINI.md`, and changes other tools too, as the paragraph above says. Then read `settings.json` and confirm the `total-recall` entry under `mcpServers` and the hook named `total-recall Pinned Floor` under `hooks.BeforeAgent.hooks`, and read `GEMINI.md` and confirm the block between `<!-- total-recall -->` and `<!-- /total-recall -->`. For anything missing, take the matching step of path 2 (that step only; do not run the skip-form install again). The install script leaves a `settings.json` that it cannot parse as strict JSON, for example one with comments, untouched, and logs that on stderr.

### Path 2: nothing else touched

The form that skips the install script:

```sh
npm install -g --ignore-scripts @strvmarv/total-recall
```

Then each step below is its own choice for the user; the agent takes the ones the user accepts. The installed package is located with `npm root -g` (the package is `<npm root -g>/@strvmarv/total-recall`; not run).

For steps 1 and 2 the file is `$GEMINI_HOME/settings.json` if `GEMINI_HOME` is set, else `~/.gemini/settings.json`. Edit it by inserting text so that its comments and other content survive; never parse and rewrite it. If you cannot do that safely, leave the step to the user and give them the snippet.

1. **Ask first**: add the MCP entry to `mcpServers`, creating the file if it is missing.
```json
{
  "mcpServers": {
    "total-recall": {
      "command": "total-recall"
    }
  }
}
```
2. **Ask first**: add the floor hook, which runs on every prompt. The shape is an object `BeforeAgent` holding a `hooks` array (as the install script writes it); add this, merging into an existing `hooks` or `BeforeAgent` rather than replacing it, and adding the item to an existing `hooks` array. The command is the one the install script writes, with `total-recall` in place of `npx --yes @strvmarv/total-recall`:
```json
{
  "hooks": {
    "BeforeAgent": {
      "hooks": [
        {
          "type": "command",
          "command": "total-recall pinned-floor --host gemini-cli",
          "timeout": 60000,
          "name": "total-recall Pinned Floor"
        }
      ]
    }
  }
}
```
3. **Ask first**: add the startup instructions. Take the body of `<npm root -g>/@strvmarv/total-recall/.kiro/steering/total-recall.md` below its front matter (the first `---` block at the top of the file), and put it in `GEMINI.md` in the same directory as `settings.json`, between a line `<!-- total-recall -->` and a line `<!-- /total-recall -->`. Create the file if it is missing; append the block if the markers are not there; if they are, replace only what is between them. Never replace the rest of the file.

Add only the items above and keep the rest of the file. On Path 1, and for a fallback step after it, an `npx` entry that the install script just wrote is accepted. Any other `total-recall` entry that is already there, and any existing entry on Path 2 proper (the script did not run), means stop and ask the user.

## User steps

1. Start a new Gemini CLI session so it reads `settings.json` and `GEMINI.md`.

## Verify

Now: `total-recall --version` should print the version that `npm view @strvmarv/total-recall version` reports. A bare `total-recall` on PATH can be an older copy than the one another harness runs. Then the configured check described under Install (the entry, the hook and the `GEMINI.md` block). If the user declines a fallback step, report that check as not passed and the entry, hook or block as left to them with the snippet. This proves the global install and the files, not that Gemini CLI runs the same copy (its entry uses `npx`). Where `total-recall` is not on your PATH after the install, run `bin/start.js` under `npm root -g` instead.

In the next session, the user pastes the check prompt from `INSTALL.md`, Step 4.

## What you get

- The `total-recall` MCP server.
- The startup instructions, as a persistent block in `GEMINI.md` that tells the model to call `session_start`.
- The pinned-directive floor on every prompt: the `BeforeAgent` hook runs `pinned-floor --host gemini-cli` and returns `hookSpecificOutput.additionalContext`. The token-growth throttle is active because the payload includes `transcript_path` (from `AGENTS.md`).

## Known gaps

- The plain install writes the entries with `npx --yes @strvmarv/total-recall`, with no version, so an older installed copy can answer instead of the latest release (see [What the install script changes](../npm-postinstall.md#the-npx-entries)).
- A `total-recall` MCP entry that is already in `settings.json` is kept, but the hook is still added when it is missing and a file that parses is always rewritten at two-space indentation.

## Evidence

Observed on pi 1.5.2, macOS arm64, with `HOME` redirected to a throwaway directory holding an empty `.gemini/`: the install script wrote the MCP entry and the `BeforeAgent` hook to `.gemini/settings.json` (a `pi install npm:` also wrote `GEMINI.md`; a `pi install git:` did not, because the script skips it for a git checkout). The block text, the markers, the hook's fields and the file-handling rules are read from `scripts/install-gemini.js` and not run on Gemini CLI itself; the floor description is from `AGENTS.md`. The hand-written path-2 steps, and locating the package with `npm root -g`, were not run.
