# Install total-recall

You are an AI assistant installing total-recall for the user you are working with. Follow this file step by step. A person can follow it too.

## Rules

- Follow only the page for the harness you are running in (Step 1). Do not run another harness's steps.
- A step marked **User** is done by the user. Say exactly what they need to do, then wait or stop. Never claim you did it.
- A step marked **Ask first** changes other tools on the machine, edits the user's configuration beyond adding the `total-recall` entry, or grants a permission. Tell the user what it changes and get a yes before you run it. On a no, use the alternative the page gives, or leave the step to the user.
- Do not report success until the Verify section of your page has passed. If a step fails, report the failure and stop. Never retry it with `sudo`, `--force` or any other escalation.
- When you read or edit a configuration file, touch only the `total-recall` entry and never print the file's other contents.

## Before you start

- Supported platforms: `linux-x64`, `linux-arm64`, `osx-arm64` and `win-x64`. An Intel Mac is not supported: stop and tell the user.
- Node.js 20 or later.
- Internet access, where your harness's page says the engine is downloaded on first launch.

## Step 1: find your harness's page

You are expected to know which harness you are running in. If you do not, ask the user. Find it in the table and fetch the page at its address.

| Harness | Page |
|---|---|
| Claude Code | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/claude-code.md |
| pi | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/pi.md |
| Codex CLI | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/codex-cli.md |
| Copilot CLI | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/copilot-cli.md |
| Cursor | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/cursor.md |
| Gemini CLI | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/gemini-cli.md |
| Kiro | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/kiro.md |
| OpenCode | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/opencode.md |
| Cline | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/cline.md |
| Windsurf | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/windsurf.md |
| Zed | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/zed.md |
| VS Code | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/vscode.md |
| Hermes | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/hermes.md |
| Any other MCP client | https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/other-mcp.md |

If your harness is not listed, use the last row.

## Step 2: follow the page's Install section

Fetch the page and run its Install section, in order. Respect each **Ask first** and **User** mark. Do not skip ahead to Verify.

## Step 3: verify now

Run the checks in the page's Verify section for this session, where it gives any. They usually include a version check, and on some pages a check that your harness was configured. A bare `total-recall` on the PATH can be an older copy than the one your harness runs, so use the command the page gives. If the page gives no check you can run now, say so: verification is then pending until the next session.

## Step 4: hand over to the user

Relay the page's **User** steps to the user, in order. Tell them:

- to start a new session, because MCP tools appear only in a new session;
- what they should see. On a harness that delivers the startup instructions the assistant gives a short status line. This is an example, not exact text: `total-recall loaded — 2 pinned, 3 hot, 12 warm, 5 cold, 2 KB collections. Storage: cortex. Last session: 2 hours ago.`
- the check to run: paste this prompt into the new session, wherever the assistant can call total-recall's tools. Where it cannot, the page gives its own check.

```
Check that total-recall is working: call its status tool and tell me the tier counts and the database path.
```

## Step 5: report

Tell the user what you verified in this session and nothing more:

- the engine version, if the version check passed;
- the harness's entry, if a configured check passed, or that it is left to the user;
- or "installed; verification is pending in your next session", if the page gave no immediate check.

Then say what was installed and where, the version, anything changed outside this harness, each step left to the user, and any known gap on the page that affects them.
