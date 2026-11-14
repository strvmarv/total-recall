# README as a front door, reference in `docs/`

Date: 2026-11-14. Follows `2026-11-13-install-agent-runbook-design.md` (#74, merged as #75), which
moved the install detail to `docs/hosts/`. This is the second of the two tickets agreed then.

## Problem

`README.md` is 670 lines and about 6,700 words. A reader who wants to install total-recall, check
that it works and start using it has to pass, or scroll through, a tier-model essay, a 28-row command
table, a full `config.toml`, the Cortex and skills configuration, the `develop` skill's pinned command
forms, an MCP tool table, the architecture and the build instructions. The audit of 2026-11-13 found:

- the first screen after the banner is "The Problem" and "The Solution"; the install prompt is line 71;
- install is split into seven `###` sections of unequal weight; nine supported harnesses have no
  section, and five of them (Gemini CLI, Kiro, Windsurf, Zed, VS Code) have no route to their
  instructions at all;
- nothing tells a new user what they should see in their first session, or how to check it works;
- "using it" is spread over four sections; the `develop` skill sits under "Configuration";
- several statements are wrong (below), because reference detail in a README is not checked by anything.

## Goals

1. The README is a front door: what it is, how to install, how to check it works, how to use it well,
   where the rest is. About 150 lines, the banner included.
2. Install comes first and in the owner's order: the self-install prompt, then Claude Code, then pi,
   then every other harness.
3. Every piece of reference content now in the README still exists, in a file under `docs/` that the
   README links.
4. The known errors are corrected on the way.
5. A check keeps the README short and the documentation reachable.

## Non-goals

- No change to product code, hook scripts, `INSTALL.md` or the pages under `docs/hosts/` (except a
  link into a README section that moves, if the builder's grep finds one; none is known).
- No rewrite of the reference content: it moves, with the listed corrections, and is not re-audited
  line by line.
- No new documentation site, generator or dependency.
- The follow-ups in #76 and the deferred wording findings of #75.

## Decided by the owner

- The banner (the "REKALL INC." block) stays at the top, unchanged.
- Priorities, in order: install; getting started and checking it works; using it well, including the
  `develop` skill. Reference content moves to `docs/`.
- The README is a short front door, about 150 lines.
- The Claude Code install leads with the shell commands (`claude plugin marketplace add`, `claude
  plugin install`); the slash commands are the alternative.
- CI guards the result: a small check in the existing documentation test.

## Design

### `README.md`

Sections, in this order. Headings are exact; the test checks their order.

1. **Banner.** The existing fenced block, byte for byte.
2. **Title block.** Badges, `# total-recall`, the one-line description, and a pitch of at most four
   sentences: the assistant forgets between sessions; total-recall keeps corrections, preferences,
   decisions and project knowledge in a local store shared by every supported tool; everything is
   local by default (SQLite, a bundled embedding model, no API keys), with Postgres or Cortex as
   options for teams.
3. **`## Install`**
   - one line of requirements directly under the heading (Node.js 20 or later; Linux x64 or arm64,
     macOS on Apple Silicon, Windows x64; an Intel Mac is not supported), from `INSTALL.md`;
   - `### Let your assistant install it`: the pasted prompt, unchanged
     (`Install the total-recall memory plugin: fetch and follow the instructions at
     https://raw.githubusercontent.com/strvmarv/total-recall/main/INSTALL.md`), one sentence saying
     what happens (the assistant reads the runbook, asks before changing anything outside its own
     harness, and tells the user what to check).
   - `### Claude Code`: the two shell commands in one block; one line giving the same two as slash
     commands; a link to `docs/hosts/claude-code.md`.
   - `### pi`: `pi install npm:@strvmarv/total-recall`; one sentence that this runs the package's
     install script, which can configure other tools it finds, with a link to
     `docs/npm-postinstall.md`; a link to `docs/hosts/pi.md` for the form that skips it and the git form.
   - `### Other harnesses`: a table with one row for each of the other twelve pages (Codex CLI,
     Copilot CLI, Cursor, Gemini CLI, Kiro, OpenCode, Cline, Windsurf, Zed, VS Code, Hermes, any other
     MCP client), in the order of `INSTALL.md`: the harness, how it installs in a few words (plugin;
     local plugin, Windows only; npm package plus configuration; memory provider; written without
     the literal install command, see rule 14), and the link
     to its page. No install command for these appears in the README: the page has it, with the
     question the user must be asked first.
4. **`## Check that it works`**: start a new session; on a harness that delivers the startup
   instructions the assistant opens with a short status line (the example from
   `skills/commands/SKILL.md`, the same one `INSTALL.md` quotes); then the check prompt of
   `INSTALL.md` Step 4, word for word; a link to `docs/first-run.md` for what the first run does and
   to the harness's page if nothing appears or where that page gives its own check (Hermes).
5. **`## Use it`**: short entries, each one to three lines with a link:
   - what is remembered without being asked (corrections, preferences, decisions), and that saying
     "remember that..." works too;
   - pins: `pin` for a rule that must always be followed;
   - the knowledge base: `ingest <path>`;
   - commands: `/total-recall:commands help`, link `docs/commands.md`;
   - the web UI: `total-recall ui` where the global npm package is installed; on a plugin install,
     where `total-recall` is not on PATH, ask the assistant for the command for your install; link
     `docs/web-ui.md` (the screenshot moves there);
   - the `develop` skill: one paragraph (Define turns an idea into a reviewed issue, Deliver turns the
     issue into a pull request and never merges), link `docs/develop.md`.
6. **`## How it works`**: at most 150 words: warm is where new memories land and are searched per
   query, hot is the small set injected at session start, sticky pins are always injected first,
   cold is the knowledge base; hybrid keyword and vector search; a local embedding model. Link
   `docs/how-it-works.md`.
7. **`## Documentation`**: a table linking every `docs/*.md` page except `docs/TODO.md` (the new
   pages below and the existing `docs/database-location.md`, `docs/first-run.md` and
   `docs/npm-postinstall.md`), and `INSTALL.md`, `PRIVACY.md`, `CONTRIBUTING.md`, `CHANGELOG.md`.
   The harness pages are linked from `## Install`, not repeated here.
8. **`## Privacy`** (two or three lines: everything is local by default; data leaves the machine
   only when Postgres, Cortex or a remote embedder is configured, and for the engine download on a
   plugin install's first launch; a link to `PRIVACY.md`, which holds the detail. Nothing else is
   claimed here), **`## Contributing`**, **`## Credits`** (the
   superpowers paragraph, shortened; the technology list moves to `docs/architecture.md`),
   **`## License`**.

The README holds no `npx` entry, no configuration block (the generic `mcpServers` block moves out:
`docs/hosts/other-mcp.md` has it), no tool table and no command table.

### New pages under `docs/`

Each takes the named README section whole, with its heading levels raised so the page title is the
`#` heading, with relative links and the image path rewritten for the page's place under `docs/`
(the widened link rule catches a miss), and with the corrections listed under "Corrections". Line numbers are the README at
`e3359b6`.

| Page | From README |
|---|---|
| `docs/how-it-works.md` | "What Gets Remembered" L143-157; "How It Works" L161-222 (tier model, hybrid search, embeddings, session start, pinned-directive floor with its per-host table) |
| `docs/platforms.md` | "Supported Platforms" L226-237 |
| `docs/web-ui.md` | "Web UI" L241-274, with the screenshot (the image path becomes `images/web-ui-dashboard.png`) |
| `docs/commands.md` | "Commands" L278-315 |
| `docs/configuration.md` | "Configuration" L319-401 (the `config.toml` reference, relocating the database, switching to Postgres) |
| `docs/cortex.md` | "Connecting to Cortex" L403-435 |
| `docs/skills.md` | "Skills" L437-478 |
| `docs/develop.md` | "The `develop` skill" L480-520 |
| `docs/tools.md` | "Developer Reference" L524-549 |
| `docs/architecture.md` | "Architecture" L553-592; "Core Technologies" L654-664 |
| `docs/verifying-a-release.md` | "Verifying a release" L596-607 |
| `docs/building.md` | "Prerequisites" L611-617; "Installation from Source" L621-636 |

"Quick Start" (L67-139) is replaced by `## Install`: its Codex CLI, Copilot CLI, Cursor and npm
blocks and the `npx` note already live in `docs/hosts/` and `docs/npm-postinstall.md` (#75).
"The Problem" and "The Solution" (L40-63) are not moved: the pitch and "Use it" replace them. Any
fact in them that the pitch drops and that exists nowhere else (the 4000-token budget, the list of
tools memories are imported from) is in `docs/how-it-works.md` already; the builder checks each bullet
and adds a sentence to the matching page for one that would otherwise be lost.

### Corrections

Made while moving. Each was verified against the code on 2026-11-13 unless marked; the builder
re-verifies each against the tree before writing it, and leaves the old text with a note in the PR if
a check fails.

1. Tool counts: 42 core, 50 in SQLite mode, 51 in Cortex mode (README says 41, 49, 50; the
   architecture block says "41 core ... (48-49 ...)"). `catalog.json` lists the 50 SQLite-mode tools.
2. The tool table lacks `insights`; add it.
3. `[scope] default` is `"user:local"`, not `"user"`.
4. `[usage] initial_backfill_days` is not read by the code; remove the key.
5. Commands: add `setup`, `recent`, `update`, `ui`; `compact` takes `[--fast]`; remove
   `eval --grow`: the commands skill (`skills/commands/SKILL.md`, `eval`) defines no `--grow`. The
   `eval_grow` MCP tool stays in the tool table, and the page may point to the CLI form
   `total-recall eval grow list|resolve`, which exists.
6. "~1000 tests" in the build instructions: drop the number.
7. "auto-injected into every prompt" (hot tier, in "The Solution", the tier model and the
   architecture block): the hot tier is assembled and injected at `session_start` (`AGENTS.md`,
   "What happens on `session_start`", step 6), and the pinned block is re-asserted by the floor. Say
   "injected at session start" where the text moves. The same wording is in the `config.toml`
   comment `max_entries = 50 # Max entries auto-injected per prompt` (L337): change it too.
8. `docs/platforms.md`: the table disagrees with the harness pages in several rows (OpenCode "Full |
   Plugin wrapper" against a page that says MCP tools only; Hermes "Importer ... no session hooks"
   against a memory provider that calls `session_start` and `session_end`; Cline "Full" against "MCP
   tools only"). Rebuild the table with one row per harness page, in the order of `INSTALL.md`: the
   harness (linking its page), what the install delivers (MCP tools; startup instructions; the pinned
   floor; commands), each cell taken from that page's "What you get" and "Known gaps". The "Full" and
   "Core" grades are dropped, because no page defines them. No new claim is made: a cell says only
   what the page says. Today's rows also say what is imported from each tool (Copilot CLI, OpenCode,
   Cline, Hermes); no harness page says that, so those notes do not go into the table. They go into
   the "Import sync" step of `docs/how-it-works.md`, one clause per tool, each checked first against
   its importer under `src/TotalRecall.Infrastructure/Importers/` (for example the Copilot CLI
   importer reads session `plan.md` files, not "memory files") and written as the importer has it.
10. `PRIVACY.md` (a root file the README now links) has three statements that the README and
    `docs/first-run.md` contradict: the engine archive is "~22 MB" (about 100 MB); "no cloud sync"
    and "your text is never sent to an embedding API" (true by default; not when Cortex, Postgres or
    a remote embedder is configured, which the README documents). Correct those three statements,
    each with the qualifier "by default" and one sentence naming the opt-in configurations. No other
    change to `PRIVACY.md`.
9. The web UI has seven sections (the README is right; `Eval` exists under
   `src/TotalRecall.Web/ClientApp/src/pages/`). No change; listed so the reviewer does not "fix" it
   from `AGENTS.md`, which says six.

### References that must follow

- `skills/develop/SKILL.md` (Preflight, check 8) and `skills/develop/tutorial.md` say "the README
  lists the minimal allowlist". After the move it is in `docs/develop.md`. Change both to name
  `docs/develop.md` in the total-recall repository. These are skill prose files, not hook scripts, so
  no host asks for a new trust review; `skills/develop/scripts/*.test.js` must still pass.
- `AGENTS.md` "WHERE TO LOOK": one row for the user documentation (`README.md` front door,
  `docs/*.md` reference pages, the check); and its one reference to a README section that moves
  (`(README, "Verifying a release")` in the release flow) is repointed to
  `docs/verifying-a-release.md`. No other `AGENTS.md` change.
- `CONTRIBUTING.md`: if it links a README section that moves, repoint it (the builder greps).
- `docs/hosts/*.md`, `INSTALL.md` and `docs/npm-postinstall.md` are not edited, except a link that
  pointed into a README section that moves (the builder greps; none is known).
- `CHANGELOG.md`: an entry under `## Unreleased`.

### The check

Added to `scripts/host-docs.test.js`, test-first, in the style of the existing rules (a function of
the tree that returns problems, a test on the repository, and a defect test per sub-rule):

10. `README.md` has at most 200 lines (the target is about 150; the budget leaves room), and its
    first fenced block equals the banner: the test holds the sha256 of that block as it is at
    `e3359b6` and compares.
11. The README's `##` headings are exactly `Install`, `Check that it works`, `Use it`,
    `How it works`, `Documentation`, `Privacy`, `Contributing`, `Credits`, `License`, in that order;
    under `Install` the `###` headings are `Let your assistant install it`, `Claude Code`, `pi`,
    `Other harnesses`, in that order.
12. The README contains the install prompt's URL
    (`https://raw.githubusercontent.com/strvmarv/total-recall/main/INSTALL.md`), the check prompt
    that `INSTALL.md` Step 4 holds, word for word, and the example status line of
    `skills/commands/SKILL.md` (existing rule 4, extended to `README.md`).
13. Every `docs/*.md` file except `docs/TODO.md`, and every `docs/hosts/*.md` file, is linked from
    `README.md` (so a page cannot be added and left unreachable).
14. The README holds no command that runs an npm install other than the one `pi install` line (the
    existing `NPM_RUNNING` pattern; exactly one match, and it is the `pi install npm:` line), and no
    `npx`.
15. Every tool name in `catalog.json` appears in `docs/tools.md` as a code span (`` `name` ``, so
    `status` is not satisfied by `usage_status`), and `docs/tools.md` holds the sentence form
    "N in SQLite mode" where N equals the number of tools in `catalog.json`. Only that count is
    checked: the core and Cortex counts are not derivable from `catalog.json`.

The existing link rule (rule 3) is widened from its fixed list to every `docs/*.md` except
`docs/TODO.md`, so the new pages' links and anchors are checked too.

### Review before the PR

Besides the usual review: a reviewer compares the README at `e3359b6` with the new README plus
everything under `docs/` (the new pages and `docs/hosts/`), section by section, and reports any paragraph, table row, command or number that exists in
neither (lost content), and any number or name that differs without being in "Corrections".

## Constraints

- Standing rules of `AGENTS.md`: no AI attribution in commits; nothing destructive.
- The shim test job runs on Node 20 with no `npm ci`: the test uses Node built-ins only.
- `docs/` is not in the npm package (`package.json` `files`); `README.md` is. Links in the README are
  relative, which npm and GitHub both resolve against the repository.
- The banner is unchanged.

## Left to the builder

- The exact wording of the README within the structure above.
- The order of rows in the Documentation table.
- Whether `docs/platforms.md` keeps one table or two.

## Out of scope, to be filed separately

Tracked in #76: the install script's silent changes, unversioned `npx` entries, the Cline path, the
release-window risk, `npm run test:shim` on Node 24, `AGENTS.md` drift, the commands skill's `eval`
section, pi subagent behaviour.
