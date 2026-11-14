# Install total-recall

You are an AI assistant installing total-recall for the user you are working with. Follow this file step by step. A person can follow it too.

## Rules

- Follow only the page for the harness you are running in (Step 1). Do not run another harness's steps.
- A step marked **User** is done by the user. Say exactly what they need to do, then wait or stop. Never claim you did it.
- A step marked **Ask first** changes other tools on the machine, edits the user's configuration beyond adding the `total-recall` entry, or grants a permission. Tell the user what it changes and get a yes before you run it. The user asking you to install total-recall is not that yes. On a no, use the alternative the page gives, or leave the step to the user.
- Do not report success: report what you verified. First make sure the checks your page asks you to run now have passed. A check the page says only the user can run, or that waits for the next session, is pending, not failed: report it as pending. If a step fails, report the failure and stop, still saying what failed and which User steps remain. Never retry it with `sudo`, `--force` or any other escalation.
- When you read or edit a configuration file, touch only the `total-recall` entry and never print the file's other contents: other servers' entries can hold credentials, and a program's error message can print the line it failed on, so use only the commands below, which print fixed text. For a file that is plain JSON (no comments), read it with this, where `<key>` is the object your page names (`mcpServers`, `servers`, `context_servers` or `mcp`):

```
node -e "const fs=require('fs'); const f=process.argv[1]; if (fs.existsSync(f)===false) { console.log('missing') } else { try { const j=JSON.parse(fs.readFileSync(f,'utf8').replace(/^\uFEFF/,'')); if (j===null || typeof j!=='object' || Array.isArray(j)) { console.log('root is not an object') } else { const o=j[process.argv[2]]; if (o===undefined) { console.log('no object; root keys:', Object.keys(j).length) } else if (o===null) { console.log('key is null') } else if (typeof o==='object' && Array.isArray(o)===false) { console.log('total-recall present:', Object.prototype.hasOwnProperty.call(o,'total-recall'), '; entries:', Object.keys(o).length) } else { console.log('key is not an object') } } } catch (e) { console.log('unreadable') } }" <file> <key>
```

  It prints `missing`; `unreadable` (comments or invalid JSON: count with `grep -c '"total-recall"' <file>` and leave any edit to the user); `root is not an object`, `key is null` or `key is not an object` (leave the edit to the user); `no object; root keys: N`; or `total-recall present: true|false; entries: N`. For anything that is not a JSON entry under a key (a hook, a marker, a line in a YAML or TOML file, a name across several files), count matches only: `grep -c <text> <file>` (PowerShell: `(Select-String -Pattern <text> <file>).Count`). Report that a count shows the text is present, not where. Never print matching lines or the lines around a match. These show that the entry is under the named key, not what else is there: report anything you could not see as unverified. A match inside a comment is not an entry.
- Edit by inserting text, never by parsing and rewriting the file, so comments and formatting survive. Before editing, copy the file beside itself as `<file>.total-recall.bak`. To add an entry to a JSON object, insert it right after that object's opening brace, followed by a comma if the object has entries; if the object is missing, insert it right after the root object's opening brace, followed by a comma if the root has other keys (the read above gives both counts); create the file if it is missing. If you cannot place the insertion without printing other servers' entries, leave the edit to the user and give them the text. Afterwards run this check, which prints only whether the file still parses:

```
node -e "const fs=require('fs'); try { JSON.parse(fs.readFileSync(process.argv[1],'utf8').replace(/^\uFEFF/,'')); console.log('parses') } catch (e) { console.log('does not parse') }" <file>
```

  If it prints `does not parse`, restore the copy and tell the user. If it prints `parses`, tell the user the copy exists, that it holds the same content as the file (including other servers' credentials) and that it can be deleted, and to delete it before committing if the file is inside a project. If the file has comments, leave the edit to the user and give them the text.

## Before you start

- Supported platforms: `linux-x64`, `linux-arm64`, `osx-arm64` and `win-x64`. An Intel Mac is not supported: stop and tell the user. `node -p "process.platform + ' ' + process.arch"` prints yours: `darwin arm64` is `osx-arm64`, `win32 x64` is `win-x64`, `linux x64` is `linux-x64` and `linux arm64` is `linux-arm64`.
- Node.js 20 or later (`node --version`).
- Internet access.

## Step 1: find your harness's page

You are expected to know which harness you are running in. If you do not, ask the user. Find it in the table.

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

Fetch your harness's page at its address and run its Install section, in order. Respect each **Ask first** and **User** mark. Do not skip ahead to Verify. Where the page offers more than one path, use the one marked recommended unless the user asked for another, and say which you used; a page's statement that a path is unsupported on your platform overrides this. The page's **Ask first** paragraph already lists what an install may change; fetch the document it links only if you need more detail to answer the user. Before you run a command that edits a configuration file, check that file for an existing `total-recall` entry with the read command in Rules; the page says what to do if there is one.

## Step 3: verify now

Run the checks in the page's Verify section for this session, where it gives any. They usually include a version check, whose printed version you compare with the version the page says to expect, and on some pages a check that your harness was configured. For an entry you added, the parse check in Rules must have passed before you call it verified, and presence plus a parse is still not placement. A bare `total-recall` on the PATH can be an older copy than the one your harness runs, so use the command the page gives. If the page gives no check you can run now, say so: verification is then pending until the next session.

## Step 4: hand over to the user

Relay the page's **User** steps for the path you installed, in order. Tell them:

- to start a new session, because MCP tools appear only in a new session;
- what they should see. If your page says your harness delivers the startup instructions, the assistant gives a short status line; if the page makes adding them a user step, the line appears only after the user has added them, and the check prompt is the test. The line is an example, not exact text: `total-recall loaded — 2 pinned, 3 hot, 12 warm, 5 cold, 2 KB collections. Storage: cortex. Last session: 2 hours ago.`
- the check to run: paste this prompt into the new session, wherever the assistant can call total-recall's tools. Where it cannot, the page gives its own check.

```
Check that total-recall is working: call its status tool and tell me the tier counts and the database path.
```

## Step 5: report

Give this report in the same message as the handover in Step 4, then stop. Never write "installed successfully": what you can honestly say in this session is what you verified, and the user's check in the new session is what completes the install. Tell the user what you verified in this session and nothing more:

- the version, if the version check passed;
- the harness's entry, if a configured check passed, or that it is left to the user;
- or "installed; verification is pending in your next session", if the page gave no immediate check.

Then say what was installed and where (a globally installed npm package is under `npm root -g`; otherwise as the page says), the version, each step left to the user, and any known gap on the page that affects them. For anything outside this harness that an install may have changed, say that it may have changed and name the tools; do not say it changed unless you inspected it. If only part of the page's checks could run, say which passed and which are pending. Tell the user that if the new session shows no total-recall tools, the entry may not resolve `total-recall` on the harness's PATH, and to report that rather than edit further.
