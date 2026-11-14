# INSTALL.md as an agent runbook, with one page per harness

Date: 2026-11-13. Base: `main` at v4.7.0. Docs change with one small test file; no product code changes.

## Intent

The README's primary install path is a sentence the user pastes into their AI coding assistant:

> Install the total-recall memory plugin: fetch and follow the instructions at
> https://raw.githubusercontent.com/strvmarv/total-recall/main/INSTALL.md

So `INSTALL.md` is executed by an agent on a user's machine. Today it is a 366-line human reference
that an agent cannot follow safely. This change turns it into a short runbook that takes an agent in
any supported harness from that sentence to an install the user can see working, and moves the
per-harness reference into pages that both agents and people read.

The URL stays the same, so prompts already pasted in the wild keep working.

## What is wrong today

Line numbers are `INSTALL.md` at `main` (6b0378a).

1. **Wrong release guidance** (L99-103). It says stable is `0.7.2` TypeScript and to install `@beta`
   for the .NET build. npm `latest` is 4.7.0 and `beta` is 4.7.0-beta.2, so an agent that follows it
   installs an older prerelease.
2. **Undisclosed changes to other tools** (L97; disclosed only at L202, in the pi section). The first
   runnable command is `npm install -g`, labelled "Any Platform". Its `postinstall`
   (`scripts/postinstall.js`) changes other tools when their config directory exists: see
   [What postinstall changes](#what-postinstall-changes).
3. **No procedure for an agent.** Nothing says how to pick a section, which steps only the user can
   do, or that a new session is needed. The Claude Code steps (L110-121) are slash commands, which a
   model cannot type, and the install line comes before the marketplace line a new user needs first.
4. **Verification cannot pass** (L309-329). `total-recall` is not on PATH after a plugin install; the
   expected version is 0.8.0; the expected first-session line
   (`total-recall: initialized · X memories imported · ...`) appears nowhere else in the repository;
   the slash-command checks exist only in some hosts.
5. **Missing harnesses.** No section for Gemini CLI, Kiro, Windsurf, Zed, VS Code or Hermes, all of
   which `postinstall` configures and all but Kiro the README lists (the README does not mention
   Kiro). The Cline section (L246-261) omits the npm
   step and the file path. The OpenCode pointer (L190) is a relative path to `.opencode/INSTALL.md`,
   which shows an `mcpServers` block; a working OpenCode 1.16.2 config uses a top-level `mcp` key.
6. **Preamble.** 76 lines about relocating the database (L15-90) come before the first install step.
7. **Stale conditionals** (L150, L202, L224): "works once a release contains ...". pi support shipped
   in 4.3.0. The marketplace entry now points at the repository URL with no ref.
8. **No warning on MCP-only installs** for Claude Code (L123-137), Cline and OpenCode that nothing
   will tell the model to call `session_start`. The Copilot and Cursor sections do carry it.
9. **Smaller drift:** "six sections" in the web UI (seven), "migrations 1..5" (17 exist), a "~60 line"
   launcher (it is now the MCP shim), "~90 MB" archives (97-101 MB).

## Goals

- An agent that fetches `INSTALL.md` knows, before any command, how to find the steps for its harness,
  and reaches them in one more fetch.
- The agent runs only what it can run, hands the user the steps only a person can do, and says what an
  install will change on the machine before it changes it.
- The user can confirm the install in the next session on every install path, and the agent can
  confirm it before the session ends wherever the install location is known, without `total-recall`
  on PATH.
- Every harness the README lists as supported has one page: how to install, what the user must do, how
  to verify, what you get, known gaps, and what the statements rest on.
- Facts that go stale are checked by a test in the existing shim test job, so the next drift fails CI.
- Nothing useful in today's `INSTALL.md` is lost; it moves.

## Non-goals

- Changing what `postinstall` does, or the `npx` entries it writes. This change documents them.
- Restructuring `README.md` (the next ticket). Here the README changes only where its links would
  break and where four lines state install facts this change makes false.
- A pinned or tagged install URL, a new install command, or marketplace changes.
- New uninstall or update guides. Existing update and troubleshooting text moves with its harness.
- Running installs for harnesses that are not available. Statements about them carry their source.

## Anti-goals

- A page that claims more than was observed. A confident instruction that nobody ran is the failure
  this change exists to remove.
- The same per-harness steps in two places.
- An agent led to change tools the user did not ask about without the user being told first.

## Approaches

**A. One file, all harness steps inline.** One fetch, no second hop. But the file is long again
(13 harnesses), an agent reads other harnesses' commands before its own (the reviewer's dry run showed
an agent likely to run the first command it meets), and people-facing detail has to live either here
(long) or in a second copy (duplication).

**B. A short runbook plus one page per harness (chosen).** `INSTALL.md` holds the procedure and a
table of harnesses with absolute raw URLs; the agent fetches exactly one page. Each fetch is short and
holds no other harness's commands. The pages are the single source for both agents and people, and the
README's host table (next ticket) links to the same pages. Cost: a second fetch, on the same host as
the first.

**C. A separate agent-only file, `INSTALL.md` left for people.** Strands every prompt already pasted,
which point at `INSTALL.md`, and keeps two documents to drift.

**D. An install command that detects the harness.** The right long-term answer, but it is a code
project of its own and does not fix the document people are told to use today.

B was chosen by the author of this spec on the owner's instruction to proceed with the direction
agreed in conversation (rewrite `INSTALL.md` in place, per-harness detail under `docs/hosts/`).

## Decided by the owner

- The work is two tickets: this one first, the README restructure next.
- Harnesses whose only install mechanism is `npm install -g` recommend the plain install, after the
  user has been told what it changes and has said yes. The quiet path is given second.
- On Claude Code the installing agent offers to add the allow rule for total-recall's tools, and adds
  it only on a yes.

## Design

### `INSTALL.md` (the runbook)

Written to the agent in the second person, plain imperative steps, no background. Sections, in order:

1. **Who this is for.** One or two lines: an AI assistant installing total-recall for the user it is
   working with; a person can follow it too.
2. **Rules.** Short and absolute:
   - follow only the page for the harness you are running in; do not run another harness's steps;
   - a step marked **User** is done by the user: say exactly what they need to do, then wait or stop;
     never claim you did it;
   - a step marked **Ask first** changes other tools on the machine or grants a permission: tell the
     user what it changes and get a yes before running it; on a no, use the alternative the page
     gives or leave the step to the user;
   - do not report success until Verify has passed; if a step fails, report the failure and stop:
     never retry it with `sudo`, `--force` or any other escalation;
   - when you read or edit a config file, touch only the `total-recall` entry and never print the
     file's other contents.
3. **Before you start.** Stop conditions and requirements: supported platforms (`linux-x64`,
   `linux-arm64`, `osx-arm64`, `win-x64`; an Intel Mac is not supported: stop and tell the user),
   Node.js 20 or later, internet access for the engine download where a page says so.
4. **Step 1: identify your harness.** A table, Claude Code first, pi second, then the rest: the
   harness and the absolute raw URL of its page
   (`https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/hosts/<id>.md`). The agent
   is expected to know which harness it is running in; the step says to ask the user if it does not,
   and gives no recognition hints. If the harness is not listed, use the generic MCP page.
5. **Step 2: fetch that page and follow its Install section.**
6. **Step 3: verify now.** Run the checks the page's Verify section gives for this session (the
   version check, compared with the version just installed, and on some pages a check that the
   harness was configured), where it gives any. State that a bare `total-recall` on PATH can be an older copy
   than the one the harness runs.
7. **Step 4: hand over.** Relay the page's User steps to the user, in order. Tell them to start a new
   session (MCP tools appear only then), what they should see, and give them the check the page
   names: the prompt to paste, or the page's own check where the model cannot call total-recall's
   tools.
8. **Step 5: report.** State one of two outcomes: "verified now" (Step 3 passed) or "installed;
   verification is pending in your next session" (the page gave no immediate check). Then what was
   installed and where, the version, anything changed outside this harness, any step left to the
   user, and any known gap the page lists that affects them.

`INSTALL.md` contains no install command and no total-recall version number.

### Harness pages (`docs/hosts/<id>.md`)

Ids: `claude-code`, `pi`, `codex-cli`, `copilot-cli`, `cursor`, `gemini-cli`, `kiro`, `opencode`,
`cline`, `windsurf`, `zed`, `vscode`, `hermes`, `other-mcp`.

Every page has these headings, in this order (a heading with nothing to say holds "None known."):

- `## Install`: opens with any stop condition its source states for this harness (for example
  Cursor: "macOS/Linux are unsupported until observed"), so the agent meets it before a command.
  Then the commands the agent runs, in order, in fenced blocks. Where more than one path exists
  (plugin, MCP only), the recommended one first, each stating what it gives up. A step that needs
  the user's yes is marked **Ask first**.
- `## User steps`: what only the user can do (approve a server, trust hooks, restart, type a slash
  command), in order, each a single instruction the agent can relay.
- `## Verify`: the checks the agent can run now (the version check, and where the rules below
  require it a check that the harness was configured), or a plain statement that there is none for
  this path, and what the user should see in the next session.
- `## What you get`: MCP tools, startup instructions, the pinned-directive floor, commands, as they
  apply to this harness.
- `## Known gaps`
- `## Evidence`: what was observed, on which harness version and operating system, and what is taken
  from the harness's documentation or from a script in this repository without being run.

Content sources, per page. The builder writes from these and adds nothing that is not in them:

| Page | Source |
|---|---|
| `claude-code` | `INSTALL.md` L106-137 and L12 (provisioning on first launch); `skills/commands/SKILL.md` (`setup`, `update`); observations below |
| `pi` | `INSTALL.md` L192-213; `AGENTS.md` (pi paragraph of "Pinned-directive floor"); observations below |
| `codex-cli` | `INSTALL.md` L215-244; `AGENTS.md` "Codex CLI host"; `codex/bootstrap.cjs` header comment (the plugin cache layout) |
| `copilot-cli` | `INSTALL.md` L139-186; `AGENTS.md` "Copilot CLI host" |
| `cursor` | `INSTALL.md` L263-307; `AGENTS.md` "Cursor CLI host" |
| `gemini-cli` | `scripts/install-gemini.js`; `AGENTS.md` (Gemini CLI in "Per-host status") |
| `kiro` | `scripts/install-kiro-hooks.js`; `.kiro/`; `AGENTS.md` (Kiro in "Per-host status") |
| `windsurf` | `scripts/install-windsurf.js` |
| `zed` | `scripts/install-zed.js`; observation below |
| `vscode` | `scripts/install-vscode.js` |
| `cline` | `INSTALL.md` L246-261; `scripts/install-cline.js`; `ClineImporter.cs` (path note below) |
| `hermes` | `scripts/install-hermes-plugin.js`; `hermes-plugin/` |
| `opencode` | `.opencode/INSTALL.md`; `INSTALL.md` L188-190; observation below |
| `other-mcp` | `INSTALL.md` L92-103 (corrected) and the MCP-only options |

Rules for the pages:

- The stale conditionals (finding 7) are removed. Where a page needs to say how the marketplace
  resolves, it says what is true now: the marketplace entry is the repository URL with no ref, so a
  marketplace install takes the default branch, and the engine is downloaded on first launch for the
  version in that tree's `package.json`.
- **Any install that runs npm** changes other tools: `npm install`, and every form of `pi install`
  (pi runs `npm install` for a `git:` package too; both forms observed below). Before such a command
  the page names, in one sentence, the tools that may be changed (Hermes, Kiro, Cline, Windsurf,
  Gemini CLI, Zed, VS Code, and Copilot CLI's MCP config), links
  [What postinstall changes](#what-postinstall-changes) by absolute raw URL for the detail, and marks
  the command **Ask first**. The agent has what it needs to ask without another fetch.
- **The form that skips it.** For npm: `npm install -g --ignore-scripts @strvmarv/total-recall`. For
  pi: `npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall` (observed below); no
  skip form was observed for `pi install git:`, and the page says so. The npm package ships the
  engines for all four platforms, so skipping the script loses nothing else on an npm install. Only a
  git-sourced install has no engine until first launch, when the launcher downloads and verifies it.
- **Which form comes first.** On every page with an npm path the plain form comes first, marked
  **Ask first**, and the skip form second. The owner decided this for the harnesses that only npm
  configures; applying it to `pi`, `opencode`, `other-mcp` and the MCP-only options is this spec's
  call, for one rule everywhere.
- **Harnesses that only npm configures** (`gemini-cli`, `kiro`, `windsurf`, `zed`, `vscode`, `hermes`,
  `cline`). Recommended path: `npm install -g @strvmarv/total-recall`, **Ask first**. Second path, for
  a user who wants nothing else touched: `npm install -g --ignore-scripts @strvmarv/total-recall`,
  then a hand-written entry for this harness only. The entry is the file, key and shape that
  harness's `scripts/install-*.js` writes, with `"command": "total-recall"` and no `args` in place of
  the `npx` form, because the global command was just installed. Where the script does more than
  write an entry, the second path lists each action as its own step so the user can take or leave
  it. The agent performs the ones the user takes; they are edits to the user's own config, so each
  is **Ask first**. A rules or steering file is written the way the script writes it, from the
  installed package (`npm root -g` locates it, marked not run): Kiro's steering file copied whole;
  Windsurf's rules file as the body without the front matter; for Gemini CLI the body inserted as a
  marked block in `GEMINI.md`, never replacing the file. The Gemini hook is the command the script
  writes, with `total-recall` in place of `npx --yes @strvmarv/total-recall`. The VS Code page offers
  the `~/.copilot/mcp-config.json` entry the script also writes as one of these steps. Hermes has no
  MCP entry to write: its script copies the
  `hermes-plugin/` directory into Hermes's plugins and switches the memory provider, so its second
  path is those two actions done by hand. `other-mcp` uses the same entry form.
- **Kiro hooks.** `postinstall` writes the MCP entry and the steering file and does not copy the
  hooks (`scripts/install-kiro-hooks.js`, header comment). The page says so, and gives copying the
  package's `.kiro/hooks/` into a workspace's `.kiro/hooks/` as a User step, attributed to that
  comment and marked not run.
- **Paths where nothing delivers the `session_start` instruction.** The condition, judged per path
  from its sources: the path ends with no hook, extension, provider plugin, rules file or steering
  file that tells the model to call `session_start` (pi's extension and Hermes's provider deliver it
  whether or not `postinstall` ran; the plain install writes a rules or steering file for Windsurf,
  Gemini CLI and Kiro). Such a path says that nothing will tell the model to call `session_start`,
  and says what to add. On the skip path of Windsurf, Gemini CLI and Kiro the step that writes the
  rules or steering file (above) is the answer, and the only mechanism the page gives. On every
  other such path adding it is a **User step**: the text is the body of
  `.kiro/steering/total-recall.md` below its front matter, which is what `postinstall` installs for
  Windsurf and Gemini CLI; the page links it by absolute raw URL, does not restate it, notes that
  its "Sticky directives" paragraph describes Kiro's hook, and calls the destination "your harness's
  persistent instructions file" because no source names one for these paths.
- **Claude Code** leads with the shell commands (marketplace add, then install), then gives the slash
  commands as the same two steps for a user to type. After the install the agent offers the allow
  rule, **Ask first**, on the plugin path only (the rule names the plugin's tools): it says that the
  rule lets Claude Code call every total-recall tool without asking each time, including the tools
  that write and delete memories, and on a yes makes the edit the `setup` section of `skills/commands/SKILL.md`
  describes (add the one rule to `permissions.allow` in `~/.claude/settings.json`, create the file if
  it is missing, change nothing else). On a no, it tells the user that in a mode that never asks the
  tools are blocked until they run `/total-recall:commands setup`.
- **Verify now needs a known location.** A page gives `node <directory>/bin/start.js --version` only
  with a directory that is in the sources or in the observations below: Claude Code's plugin cache,
  pi's npm and git package directories, Copilot CLI's `installed-plugins` directory, Codex's plugin
  cache (`AGENTS.md`), Cursor's local plugin directory. The global npm path uses
  `total-recall --version`. A path with neither says there is no immediate check and that
  verification happens in the next session. The builder may add a location from a harness's own
  documentation, marked "from its documentation, not run".
- **Verify that the harness was configured, on the harnesses npm configures.** The version check
  proves the engine, not the entry: `postinstall` skips silently when the directory is absent, when
  the config file has comments it cannot parse, or when a different `total-recall` entry exists.
  After the plain install those pages have the agent read the file the script writes and confirm
  the `total-recall` entry (the `npx` entry the script wrote is accepted), and fall back to the
  hand-written entry if it is absent. The Copilot CLI page's MCP-only option does the same, because
  the VS Code module writes `~/.copilot/mcp-config.json` when VS Code's user directory exists. Two
  pages differ. Cline: the entry in `~/.vscode/cline_mcp_settings.json` is not evidence that Cline is
  configured (see the Cline rule below), so the page says so and the manual steps are the path.
  Hermes: see the Hermes rule below.
- **Hand-written entries, everywhere.** Add only the named entry, keep the rest of the file, and if
  a `total-recall` entry that this install did not just write already exists, stop and ask the user.
- **Approval flags.** Copilot CLI's `--allow-tool=total-recall` and Cursor's `--approve-mcps` are
  User steps; the agent never writes them into a config, alias or shell profile.
- **Where the skip form costs nothing.** On `pi`, `opencode`, `other-mcp` and the MCP-only options
  (Copilot CLI's excepted, above) `postinstall` configures nothing for the harness being installed,
  and the page says so beside the skip form. The pi skip form is marked "POSIX shell; not run on Windows".
- **Codex CLI.** Editing `~/.codex/config.toml` to set `default_tools_approval_mode = "approve"` is
  **Ask first**, with the same wording about write and delete tools. `--dangerously-bypass-hook-trust`
  is mentioned as information only; the page never tells the agent to use it. Trusting the hooks in
  `/hooks` is a User step.
- The Cline page states where `postinstall` writes (`~/.vscode/cline_mcp_settings.json`) and that the
  repository's own importer reads Cline's settings from a different location, so the written entry may
  not be the file Cline reads. It gives the manual steps from today's section, with the npm step.
- **OpenCode.** The page gives the config in the `mcp` form observed below and marks the `skills/`
  copy step as needing a checkout. `.opencode/INSTALL.md` ships in the npm package (`package.json`
  `files`). It becomes a pointer: one line saying what it is and the page's absolute URL, with no
  command and no config block, so no step is repeated and no install command sits outside the
  disclosure rule.
- **Hermes.** Its model is given one tool, `memory`, with the same name and schema as Hermes's
  built-in memory tool (`hermes-plugin/memory_tool_schema.py`), not total-recall's MCP tools. So the
  check prompt cannot work there, and a memory read-back passes whichever provider is active. The
  script can also copy the plugin and leave the provider unset (when the `hermes` CLI is not on PATH
  or the command fails). The page's checks, both marked not run: now, the agent confirms that
  Hermes's plugins directory holds `total-recall` and that `memory.provider` is `total-recall` in
  Hermes's config, and falls back to the by-hand actions if either is missing; in the next session,
  the user asks Hermes to remember a phrase of their choosing, then runs
  `total-recall memory recent` and sees it.

### What postinstall changes

A page of its own, `docs/npm-postinstall.md` (outside `docs/hosts/`, so the harness-page rules of
the test do not apply to it), linked from every page that runs npm.
One row per tool that `scripts/postinstall.js` configures, from the `scripts/install-*.js` it imports:
the condition that triggers it, the file written, what is written, whether an existing entry is kept.
As read on 2026-11-13:

| Tool | Triggered when | What changes |
|---|---|---|
| Hermes | a Hermes `config.yaml` is found | copies `hermes-plugin/` into Hermes's plugins and runs `hermes config set memory.provider total-recall` |
| Kiro | `~/.kiro` exists | MCP entry in `settings/mcp.json`; a steering file |
| Cline | `~/.vscode` exists | MCP entry in `~/.vscode/cline_mcp_settings.json` |
| Windsurf | `~/.codeium/windsurf` exists | MCP entry in `mcp_config.json`; a rules file |
| Gemini CLI | `~/.gemini` exists | MCP entry and a `BeforeAgent` hook in `settings.json`; a block in `GEMINI.md` |
| Zed | Zed's config directory exists | entry in `settings.json` |
| VS Code and Copilot CLI | the VS Code user directory exists | entry in VS Code's `mcp.json`; an entry in Copilot CLI's `~/.copilot/mcp-config.json` |

The builder confirms each row against the script and records any override environment variable the
script honours. The page also states:

- the entries start the server as `npx --yes @strvmarv/total-recall` with no version;
- that this works (observed below), and that `npx` runs a copy that is already installed before it
  fetches one (observed below: a global 4.0.4 ran while `latest` was 4.7.0), with the check
  `npx --yes @strvmarv/total-recall --version`;
- how to skip all of it: the skip forms above;
- which rows are skipped when the package is installed from a git checkout (the scripts skip some
  steps there; the builder reads which from the scripts).

### Where the rest of today's `INSTALL.md` goes

| Today | Goes to |
|---|---|
| "What total-recall ships as", "Prerequisites" (L5-13) | "Before you start" in `INSTALL.md`, cut to the stop conditions; the launcher description is corrected or dropped; the paragraph on provisioning at first launch (L12: the `not_ready` result, no restart needed) goes to `docs/first-run.md` and is linked from the git-sourced pages |
| "Relocating the database" (L15-90) | `docs/database-location.md`, moved whole |
| "Quick Install" (L92-103) | `docs/hosts/other-mcp.md`, with no version numbers and no `@beta` advice |
| Per-harness sections | their pages |
| "Verification" (L309-329) | Steps 3 and 4 of `INSTALL.md` and each page's Verify |
| "Launch the web UI" (L331-350) | removed: the README's "Web UI" section already holds every line of it |
| "What Happens on First Run" and the 0.7 to 0.8 upgrade note (L352-366) | `docs/first-run.md`, with the migration count removed, not replaced by another number |

### Verify, concretely

- **Now, by the agent.** The page gives the command for its install path:
  `node <plugin root>/bin/start.js --version` for a plugin or package directory, `total-recall
  --version` only for the global npm path. Expected: `total-recall <version>`, equal to the `version`
  in that directory's `package.json` (or `npm view @strvmarv/total-recall version` for the global
  path). On a git-sourced plugin install the first run downloads and verifies the engine, so this also
  proves provisioning.
- **Next session, by the user.** On a harness that delivers the startup instructions, the assistant
  should give a short total-recall status line; `INSTALL.md` quotes the example from
  `skills/commands/SKILL.md`, "Session Start" (`total-recall loaded ...`) as an example, not as
  exact text. The firm check, wherever the model can call total-recall's tools, is the check prompt,
  which asks the assistant to call the `status` tool and report the tier counts and the database
  path. Where it cannot (Hermes), the page gives its own check.
- `INSTALL.md` Step 4 holds the check prompt once; pages refer to it.

### References that must follow the move

- `README.md` links to `INSTALL.md#pi`, `#cursor`, `#codex-cli`, `#github-copilot-cli` (two each) and
  `#relocating-the-database` (one): repoint to the new pages.
- Four README lines state install facts this change makes false or dangling, and are corrected
  here. L137: the sentence that the `mcpServers` block "works with" Copilot CLI, OpenCode, Cline,
  Cursor and Hermes drops OpenCode and Hermes and links their pages, because OpenCode uses an `mcp`
  key and Hermes has no MCP entry. L91: drop the comment "once the release carrying pi support is published". L117: drop the
  sentence "This works once the marketplace points at a release ... (see the INSTALL.md caveat)".
  L139: replace the note that `npx` "does not work due to an npm bug" (the linked issue is unrelated,
  and `npx` was observed working) with: "prefer the global install. `npx -y @strvmarv/total-recall`
  runs a copy that is already installed (for example an older global install) instead of fetching
  the latest release." No other README change.
- `codex/hook.js`, `copilot/hook.js`, `cursor/hook.js` each inject a note citing
  `INSTALL.md, "Codex CLI"`, `INSTALL.md, "Copilot CLI"` and `INSTALL.md, "Cursor"`. The scripts are
  not edited: a change to a bundled Codex script makes Codex ask every user for a new hook trust
  review and skip the hooks until then (`INSTALL.md` L226), which a documentation pointer does not
  justify. Instead the harness table in `INSTALL.md` names those three harnesses exactly "Codex CLI",
  "Copilot CLI" and "Cursor", so each citation still leads to the right row and its page.
- `AGENTS.md` "WHERE TO LOOK": add a row for the harness pages. No other `AGENTS.md` change.

### The check that keeps it true

One zero-dependency Node test file, run by `npm run test:shim` (so also listed in the verify block of
`.github/develop.md`, which `skills/develop/scripts/profile.test.js` requires to match `test:shim`).
The profile is read from `main`, so during this run the builder also runs the new file directly. It
fails when:

1. a harness id above has no page, a page lacks a required heading or has them out of order, or the
   `INSTALL.md` table and the set of pages differ;
2. `INSTALL.md` contains a fenced install command (`npm install`, `pi install`, `plugin install`,
   `plugin add`) or a total-recall version number, or any harness page or `.opencode/INSTALL.md`
   mentions the versions `0.7.2` or `0.8.x` (matched as whole version tokens, so a harness version
   such as `10.8.2` in an Evidence section does not trip it) or advises `@beta`. `docs/first-run.md`
   is exempt: it holds the 0.7 to 0.8 upgrade note;
3. a relative link or anchor in `INSTALL.md`, `docs/hosts/*.md`, `docs/npm-postinstall.md`,
   `docs/database-location.md`, `docs/first-run.md` or `README.md` does not resolve to a file and
   heading in the tree, or a raw URL to this repository's `main` in `INSTALL.md`, `docs/hosts/*.md` or
   `.opencode/INSTALL.md` does not map to a file in the tree;
4. the example status line quoted in `INSTALL.md` is not in `skills/commands/SKILL.md`;
5. a page runs `npm install` or any `pi install` without, above the command, the sentence naming the
   tools and the link to `npm-postinstall.md`;
6. the tools named in `npm-postinstall.md`, or in that sentence on any page, differ from the
   `install-*.js` modules that `scripts/postinstall.js` imports (Copilot CLI counts as named by the
   VS Code module);
7. the name a hook note cites in `codex/hook.js`, `copilot/hook.js` or `cursor/hook.js`
   (`INSTALL.md, "<name>"`) is not a harness name in the `INSTALL.md` table;
8. `.opencode/INSTALL.md` contains a fenced block, or does not link `docs/hosts/opencode.md` by
   absolute URL.

A limit of rule 6, stated so nobody relies on more: it catches a new `install-*.js` module, not a
tool added inside an existing module (as Copilot CLI's config is inside the VS Code module).

Each rule is developed test-first: the test fails against today's tree for the reason named, then the
document change makes it pass.

### Review before the PR

Besides the usual review, the final review includes a dry run: a reviewer agent is given only the text
of `INSTALL.md` and the one page its harness would fetch, for Claude Code, pi, Gemini CLI, Cursor on
macOS and Cline on Windows, and reports any step where it would have to guess, would run something the page did not ask
for, or could not tell whether a step was the user's. No install is run. A step that fails the dry run
is a finding like any other.

## Observations this design rests on

Made on 2026-11-13 on macOS (arm64), Node 24.18, npm 11.16. Each was run in a throwaway directory with
the config and data directories redirected, unless it says otherwise.

- **Claude Code 2.1.301.** `claude plugin marketplace add strvmarv/total-recall-marketplace`, then
  `claude plugin install total-recall@strvmarv-total-recall-marketplace` (with `CLAUDE_CONFIG_DIR`
  redirected): both exit 0, version 4.7.0 installed under
  `plugins/cache/strvmarv-total-recall-marketplace/total-recall/4.7.0/`, with no `binaries/`
  directory. `node <that directory>/bin/start.js --version` then downloaded, verified and extracted the
  engine and printed `total-recall 4.7.0`; `status` printed the tier, database and embedding tables.
  Not observed: the first Claude Code session after it.
- **pi 1.5.2.** The session that produced this spec ran total-recall 4.7.0 through the pi package:
  the instructions were injected, `session_start` returned normally and the smoke test passed. The
  package directory was `~/.pi/agent/npm/node_modules/@strvmarv/total-recall`. On the same machine
  `total-recall --version` printed 4.0.4, an older global npm install: not the copy pi runs.
- **`npx`.** `npx --yes @strvmarv/total-recall@4.7.0` with install scripts disabled answered
  `initialize`, listed 50 tools and started the engine. `npx --yes @strvmarv/total-recall` (no
  version) ran the global 4.0.4. Not observed: Windows, and `npx` with install scripts enabled.
- **Copilot CLI 1.0.83.** On this machine (not a throwaway) the installed plugin is at
  `~/.copilot/installed-plugins/strvmarv-total-recall-marketplace/total-recall/`.
- **`pi install`, three forms** (pi 1.5.2, `HOME` redirected to a throwaway directory holding an
  empty `.gemini/`):
  - `pi install npm:@strvmarv/total-recall`: installed to
    `~/.pi/agent/npm/node_modules/@strvmarv/total-recall`; `postinstall` ran (it wrote
    `.gemini/settings.json` and `.gemini/GEMINI.md`).
  - `npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall`: same directory;
    `postinstall` did not run (no Gemini file written); `binaries/` held all four platforms and
    `node <directory>/bin/start.js --version` printed `total-recall 4.7.0`.
  - `pi install git:github.com/strvmarv/total-recall`: installed to
    `~/.pi/agent/git/github.com/strvmarv/total-recall`; `postinstall` ran (it wrote the MCP entry and
    the hook to `.gemini/settings.json`, and no `GEMINI.md`); `binaries/` held this platform only;
    `--version` printed `total-recall 4.7.0`.
- **Zed.** `~/Library/Application Support/Zed/settings.json` on this machine (not a throwaway) holds a
  `total-recall` entry `{"command":"npx","args":["--yes","@strvmarv/total-recall"]}`, as
  `scripts/install-zed.js` writes it.
- **OpenCode 1.16.2.** This machine's working config, `~/.config/opencode/opencode.jsonc`, registers
  the server under a top-level `mcp` key as
  `{"type":"local","command":["total-recall"],"enabled":true}`.
- **npm.** `dist-tags`: `latest` 4.7.0, `beta` 4.7.0-beta.2. The package's `pi` block is published.
- **Marketplace.** `strvmarv/total-recall-marketplace` `marketplace.json` has
  `"source": {"source": "url", "url": "https://github.com/strvmarv/total-recall.git"}`, no ref.
- **Release 4.7.0.** The four archives are 97 to 101 MB.

Statements about Codex CLI, Copilot CLI and Cursor come from today's `INSTALL.md` and `AGENTS.md`,
which record observations on Windows; the pages keep those attributions.

## Constraints

- Standing rules of `AGENTS.md`: no AI attribution in commits; nothing destructive.
- Deliver runs no install and changes nothing outside its worktree. The observations above are the
  evidence; a page says "not run" where that is the case.
- The shim test job runs on Node 20 with no `npm ci`: the test uses Node built-ins only.
- `docs/` and `INSTALL.md` are not in the npm package (`package.json` `files`); do not add them.

## Left to the builder

- The exact wording of the runbook and pages, within the structure above.
- The test file's name and location, and how it parses Markdown (headings, fences, links).
- Whether "Before you start" keeps a one-line description of what gets installed.
- The order of harnesses after Claude Code and pi.

## Out of scope, to be filed separately

Found during the audit; not part of this change:

- `postinstall` changes other tools silently, including Hermes's memory provider; opt-in or a visible
  summary would be safer.
- The written MCP entries use unversioned `npx`, which can run an old engine against a newer database.
- `scripts/install-cline.js` writes to a path the repository's own importer does not read.
- Plugin installs track `main` while the engine download is keyed to the `package.json` version, so an
  install made between a version bump and the release publishing may fail to provision (inferred).
- The pi section's statement that subagent children get no injected instructions did not hold for a
  reviewer subagent in this session.
- `AGENTS.md` drift (web UI "six sections", handler counts), the commands skill's missing `eval
  --grow`, and the README restructure (the next ticket).
