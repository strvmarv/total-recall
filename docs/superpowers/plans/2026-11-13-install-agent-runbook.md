# INSTALL.md Agent Runbook Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `INSTALL.md` into a short runbook an AI agent can follow, with one page per harness under `docs/hosts/` and a test that fails when the instructions drift.

**Architecture:** Documentation only, plus one zero-dependency Node test. The test is written first as pure check functions over file text (the pattern of `scripts/release-workflow.test.js`): each check returns a list of problems, runs on the real tree, and is also run on a defective copy to prove it can fail. The documents are then written until every check returns nothing.

**Tech Stack:** Markdown; Node 20 built-ins (`node:test`, `node:assert/strict`, `node:fs`, `node:path`, `node:url`), ESM, no dependencies.

**Spec:** `docs/superpowers/specs/2026-11-13-install-agent-runbook-design.md`. It is read-only for the whole run. Every page statement comes from the sources that spec names for the page; where this plan and the spec differ, the spec wins.

## Global Constraints

- The spec file is never edited.
- No product code changes: nothing under `src/`, `bin/`, `pi/`, `codex/`, `copilot/`, `cursor/`, `hooks/`, `skills/`, `hermes-plugin/`, `.kiro/`, and no change to `scripts/postinstall.js` or `scripts/install-*.js`.
- No install is run and nothing outside the worktree is changed. A statement nobody ran is marked "not run" with its source.
- The test uses Node built-ins only and must pass on Node 20 with no `npm ci`.
- `docs/` and `INSTALL.md` are not added to `package.json` `files`.
- Commits carry no AI attribution and no co-author trailer.
- Repository base URLs, exact: raw `https://raw.githubusercontent.com/strvmarv/total-recall/main/` and blob `https://github.com/strvmarv/total-recall/blob/main/`.
- Harness ids and display names, in this order: `claude-code` "Claude Code", `pi` "pi", `codex-cli` "Codex CLI", `copilot-cli` "Copilot CLI", `cursor` "Cursor", `gemini-cli` "Gemini CLI", `kiro` "Kiro", `opencode` "OpenCode", `cline` "Cline", `windsurf` "Windsurf", `zed` "Zed", `vscode` "VS Code", `hermes` "Hermes", `other-mcp` "Any other MCP client". "Codex CLI", "Copilot CLI" and "Cursor" must be spelled exactly so: three hook scripts cite them.
- Page headings, exactly these six `##` headings in this order and no other `##` heading: `Install`, `User steps`, `Verify`, `What you get`, `Known gaps`, `Evidence`. A section with nothing to say holds `None known.`
- The disclosure paragraph: on any page where the text `npm install` or `pi install` appears anywhere (in a fenced block or in prose), a paragraph that begins `**Ask first.**` stands above its first appearance. The paragraph itself does not contain either string; it says "this install". It names every tool the install script may change (Hermes, Kiro, Cline, Windsurf, Gemini CLI, Zed, VS Code, Copilot CLI), says that it switches Hermes's memory provider and adds a Gemini CLI hook that runs on every prompt, and links `https://raw.githubusercontent.com/strvmarv/total-recall/main/docs/npm-postinstall.md`.
- Skip forms, exact: `npm install -g --ignore-scripts @strvmarv/total-recall` and `npm_config_ignore_scripts=true pi install npm:@strvmarv/total-recall` (the pi form is marked "POSIX shell; not run on Windows"). No skip form is given for `pi install git:`; the page says none was observed.
- The check prompt, exact, held once in `INSTALL.md` Step 4: `Check that total-recall is working: call its status tool and tell me the tier counts and the database path.` A page's Verify section refers to "the check prompt in `INSTALL.md`, Step 4" and does not restate it. The Hermes page gives its own check instead.
- `INSTALL.md` prose must not contain the literal strings `npm install`, `pi install`, `plugin install` or `plugin add` (rule 2 scans the whole file); it says "an install that runs npm" where it needs to.
- After the last task, the final review includes the dry run the spec describes under "Review before the PR" (Claude Code, pi, Gemini CLI, Cursor on macOS, Cline on Windows). It is part of the final review loop, not a task.
- The example status line in `INSTALL.md` is the one in `skills/commands/SKILL.md` ("Session Start"), copied character for character and introduced as an example.

## Review Focus

Failure modes the spec implies that the rule checks would not catch unless a test pins them. Each is pinned in Task 1.

1. A link or URL inside a fenced block or an inline code span is not a link: it must not be checked, and a broken one there must not fail the build.
2. Two headings with the same text get GitHub's `-1`, `-2` anchor suffixes; an anchor to the second must resolve and an anchor to a third that does not exist must not.
3. A file saved with CRLF line endings must parse the same as one with LF.
4. A URL followed by `)`, `.`, `,` or a backtick in prose must be read without that character.
5. A harness version in an Evidence section (for example `10.8.2` or `2.1.301`) must not be read as the forbidden `0.8.x`.

---

### Task 1: The drift check, written first

**Files:**
- Create: `scripts/host-docs.test.js`

**Interfaces:**
- Produces (all exported from the test file's module scope as plain functions; later tasks only run the file):
  - `repoTree()` and `memoryTree(files: Record<string,string>)` build a `tree`; both normalise CRLF to LF in `read`, so every helper and check sees LF only.
  - `stripCode(md: string): string` replaces fenced blocks and inline code spans with spaces of equal length.
  - `fencedBlocks(md: string): {index: number, body: string}[]` with `index` the offset of the opening fence.
  - `slugify(text: string): string` GitHub's heading anchor: lowercase, drop every character that is not a letter, digit, space, hyphen or underscore, spaces to hyphens.
  - `headings(md: string): {level: number, text: string, slug: string}[]` ATX headings outside fences, with `-1`, `-2` suffixes on repeats.
  - `links(md: string): string[]` targets of `[text](target)` and `![alt](target)` outside code.
  - `repoPaths(md: string): string[]` the path after either repository base URL, wherever it appears (prose, inline code or a fence: a URL is checked everywhere); trailing `)`, `.`, `,`, backtick and `#fragment` removed.
  - Constants `HARNESSES` (`{id, name}[]`, the order of Global Constraints), `PAGE_HEADINGS`, `TOOLS` (the eight tool names), `MODULE_TOOLS` (`install-hermes-plugin` → Hermes; `install-kiro-hooks` → Kiro; `install-cline` → Cline; `install-windsurf` → Windsurf; `install-gemini` → Gemini CLI; `install-zed` → Zed; `install-vscode` → VS Code and Copilot CLI).
  - Checks, each `(tree) => string[]` where `tree` is `{read(path): string|null, list(dir): string[]}` and the result is a list of problems, empty when the rule holds:
    - `checkPages(tree)` rule 1: every harness has `docs/hosts/<id>.md` with exactly `PAGE_HEADINGS` as its `##` headings in order; `docs/hosts/` holds no other `.md`; the `INSTALL.md` table rows (`| <name> | <raw url> |`) are exactly `HARNESSES` in order.
    - `checkNoStaleFacts(tree)` rule 2: `INSTALL.md` contains none of `npm install`, `pi install`, `plugin install`, `plugin add`, and no `\d+\.\d+\.\d+` token; no harness page and not `.opencode/INSTALL.md` contains `@beta`, or `0.7.2` or `0.8.<digits or x>` as a whole version token (not preceded by a digit or a dot). `docs/first-run.md` is not scanned.
    - `checkLinks(tree)` rule 3: for `INSTALL.md`, `README.md`, `docs/hosts/*.md`, `docs/npm-postinstall.md`, `docs/database-location.md`, `docs/first-run.md`: each must exist; every relative link resolves to a file in the tree, and its `#anchor`, when the target is Markdown, to a heading slug there. For `INSTALL.md`, `docs/hosts/*.md` and `.opencode/INSTALL.md`: every `repoPaths` entry is a file in the tree.
    - `checkStatusLine(tree)` rule 4: the first line of `skills/commands/SKILL.md` matching `^\s*>\s*(total-recall loaded — .+)$` is found, and `INSTALL.md` contains that captured text.
    - `checkDisclosure(tree)` rules 5 and 6: for each harness page whose text contains `npm install` or `pi install` anywhere (fenced or inline), the page text above the first occurrence has a paragraph starting `**Ask first.**` that contains every name in `TOOLS`, the words `memory provider` and `hook`, and the raw URL of `docs/npm-postinstall.md`; the modules imported by `scripts/postinstall.js` as `./install-*.js` are exactly the keys of `MODULE_TOOLS`; `docs/npm-postinstall.md` contains every name in `TOOLS`.
    - `checkHookNotes(tree)` rule 7: every `INSTALL.md, "<name>"` citation in `codex/hook.js`, `copilot/hook.js`, `cursor/hook.js` (at least one per file) names a harness in the `INSTALL.md` table.
    - `checkOpenCodePointer(tree)` rule 8: `.opencode/INSTALL.md` has no fenced block and its `repoPaths` include `docs/hosts/opencode.md`.

- [ ] **Step 1: Write the helper tests.** One `test()` per helper, on inline strings, including the five Review Focus cases:

```js
test('links: a link inside a fence or an inline code span is not a link', () => {
  const md = 'a [ok](a.md)\n\n```\n[no](b.md)\n```\n\n`[no](c.md)` and ![img](d.png)\n';
  assert.deepEqual(links(md), ['a.md', 'd.png']);
});
test('headings: repeated text gets -1, -2 suffixes', () => {
  assert.deepEqual(headings('# A\n\n## Option B: MCP only\n\n## Option B: MCP only\n').map((h) => h.slug),
    ['a', 'option-b-mcp-only', 'option-b-mcp-only-1']);
});
test('parsing: CRLF input gives the same headings and links as LF', () => {
  const lf = '# T\n\n## Install\n\n[x](y.md#install)\n';
  assert.deepEqual(headings(lf.replace(/\n/g, '\r\n')), headings(lf));
  assert.deepEqual(links(lf.replace(/\n/g, '\r\n')), links(lf));
});
test('repoPaths: trailing punctuation and fragments are dropped', () => {
  const base = 'https://raw.githubusercontent.com/strvmarv/total-recall/main/';
  assert.deepEqual(repoPaths(`(${base}docs/hosts/pi.md). See ${base}docs/first-run.md#x, and \`${base}a.md\``),
    ['docs/hosts/pi.md', 'docs/first-run.md', 'a.md']);
});
test('stale versions: a harness version is not the forbidden 0.8.x', () => {
  const page = (body) => memoryTree({ 'docs/hosts/pi.md': body });
  assert.deepEqual(checkNoStaleFacts(page('Observed on 10.8.2 and Claude Code 2.1.301.')).filter((p) => p.includes('pi.md')), []);
  assert.notDeepEqual(checkNoStaleFacts(page('Expected: total-recall 0.8.0')).filter((p) => p.includes('pi.md')), []);
});
```

  `memoryTree` builds a `tree` from an object; `repoTree()` builds one that reads the repository (root = the parent of `scripts/`). Add one more helper test: a `memoryTree` whose page text uses CRLF passes `checkPages` and `checkDisclosure` exactly as the LF version does.

- [ ] **Step 2: Write the rule tests.** For each check, two tests: `rule N holds on the repository` (`assert.deepEqual(check(repoTree()), [])`) and `rule N fails on a defect`, which builds a `memoryTree` from a minimal valid set and one defect, and asserts a non-empty result naming the defect. Defects, one test each:
  - rule 1: a missing harness page; a page with `Verify` before `User steps`; an extra `docs/hosts/extra.md`; a table with pi first and Claude Code second; a table with thirteen rows (one harness missing).
  - rule 2: `npm install -g x` inside `INSTALL.md`; `total-recall 4.7.0` inside `INSTALL.md`; `@beta` on a harness page; `0.8.0` inside `.opencode/INSTALL.md`.
  - rule 3: a link to `docs/hosts/nope.md`; an anchor `#no-such-heading`; a raw URL in `INSTALL.md` to `docs/hosts/nope.md`.
  - rule 4: an `INSTALL.md` without the status line.
  - rules 5 and 6: a page whose `**Ask first.**` paragraph omits `Zed`; a page whose paragraph stands below the command; a page with `npm install -g` only in prose and no paragraph; a `postinstall.js` importing `./install-newtool.js`.
  - rule 7: a hook note citing `INSTALL.md, "Codex"`.
  - rule 8: an `.opencode/INSTALL.md` containing a fenced block; one with no link to the OpenCode page.

- [ ] **Step 3: Run and see the helper and defect tests fail for the right reason.**

Run: `node --test scripts/host-docs.test.js`
Expected: every test fails with `ReferenceError` or `is not defined` (nothing is implemented).

- [ ] **Step 4: Implement the helpers, constants and checks** in `scripts/host-docs.test.js`, above the tests.

- [ ] **Step 5: Run again.**

Run: `node --test scripts/host-docs.test.js`
Expected: all helper tests and all `fails on a defect` tests pass. Every `holds on the repository` test fails, for these reasons: rule 1 "docs/hosts/claude-code.md is missing" (and the other pages); rule 2 `INSTALL.md` contains `npm install` and version numbers; rule 3 `docs/npm-postinstall.md` is missing; rule 4 the status line is not in `INSTALL.md`; rules 5 and 6 `docs/npm-postinstall.md` is missing; rule 7 `Codex CLI` is not in the `INSTALL.md` table; rule 8 `.opencode/INSTALL.md` has a fenced block. Record the output in the task report.

- [ ] **Step 6: Commit.**

```bash
git add scripts/host-docs.test.js
git commit -m "test: add the install documentation drift check"
```

### Task 2: The documents that leave INSTALL.md

**Files:**
- Create: `docs/npm-postinstall.md`, `docs/database-location.md`, `docs/first-run.md`
- Read: `INSTALL.md` (at `main`), `scripts/postinstall.js`, `scripts/install-*.js`, `scripts/lib/merge-json-config.js`, `scripts/lib/is-repo-self.js`

**Interfaces:**
- Produces: `docs/npm-postinstall.md` (linked by every npm page by raw URL; headings `## What it changes`, `## The npx entries`, `## How to skip it`); `docs/database-location.md` with the heading `# Relocating the database`; `docs/first-run.md` with `## Provisioning on first launch`, `## What happens on first run`, `## Upgrading from 0.7.x`.

- [ ] **Step 1: Write `docs/npm-postinstall.md`.** One table row per tool, confirmed line by line against its script: the trigger, the file written, what is written, whether an existing entry is kept (from `merge-json-config.js`), and any environment variable that overrides the location. State which rows are skipped when the package sits in a git checkout (from `is-repo-self.js` and its callers). State the entry form (`npx --yes @strvmarv/total-recall`, no version); that it was observed working; that `npx` runs an installed copy before fetching one (observed: a global 4.0.4 ran while `latest` was 4.7.0); the side-effect-free check `npm ls -g @strvmarv/total-recall`; that `npx --yes @strvmarv/total-recall --version` also answers but downloads the package and runs the script when no copy is installed (npm's default, not observed). Give both skip forms.
- [ ] **Step 2: Write `docs/database-location.md`.** Move `INSTALL.md` L15-90 whole, heading levels raised one step so the title is the `#` heading.
- [ ] **Step 3: Write `docs/first-run.md`.** Move the provisioning paragraph of `INSTALL.md` L12, and L352-366. In the provisioning paragraph, correct the two facts the spec lists as drift: the archive size ("about 100 MB"; 97 to 101 MB observed for 4.7.0) and the trigger (a git-sourced plugin install, which is what the marketplace entry gives; not "a `source: github` marketplace entry"). Remove "migrations 1..5" without putting another number in its place. Keep the 0.7 to 0.8 upgrade note as written.
- [ ] **Step 4: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: `rule 3 holds on the repository` and `rules 5 and 6 hold on the repository` now pass (no harness page exists yet, and the README still resolves into today's `INSTALL.md`); rules 1, 2, 4, 7 and 8 still fail for the reasons of Task 1.

- [ ] **Step 5: Commit.**

```bash
git add docs/npm-postinstall.md docs/database-location.md docs/first-run.md
git commit -m "docs: move the database, first-run and postinstall material out of INSTALL.md"
```

### Task 3: Pages for the plugin harnesses

**Files:**
- Create: `docs/hosts/claude-code.md`, `docs/hosts/pi.md`, `docs/hosts/codex-cli.md`, `docs/hosts/copilot-cli.md`, `docs/hosts/cursor.md`
- Read: the sources the spec's "Content sources" table names for each, and the spec's "Rules for the pages" and "Observations"

**Interfaces:**
- Consumes: the page headings, the disclosure paragraph and the skip forms of Global Constraints; `docs/npm-postinstall.md`, `docs/first-run.md#provisioning-on-first-launch`.
- Produces: five pages. `docs/hosts/copilot-cli.md` and `docs/hosts/cursor.md` are link targets for `README.md` in Task 6.

- [ ] **Step 1: `claude-code.md`.** Install: the two shell commands (`claude plugin marketplace add strvmarv/total-recall-marketplace`, then `claude plugin install total-recall@strvmarv-total-recall-marketplace`), then the same two as slash commands for a user to type; then the allow-rule offer, **Ask first**, plugin path only, worded as the spec's Claude Code rule requires; then the MCP-only option with the disclosure paragraph, the plain form first and the skip form second, and the statement that nothing delivers the `session_start` instruction on that path. Verify: `node ~/.claude/plugins/cache/strvmarv-total-recall-marketplace/total-recall/<version>/bin/start.js --version`, with the first-run download explained. Evidence: the Claude Code 2.1.301 observation, and "the first session after the install was not observed".
- [ ] **Step 2: `pi.md`.** Install: disclosure paragraph, then `pi install npm:@strvmarv/total-recall` (**Ask first**), the skip form, and `pi install git:github.com/strvmarv/total-recall` (also **Ask first**, with "no skip form was observed"). Say beside the skip form that the install script configures nothing for pi itself. Verify: `node ~/.pi/agent/npm/node_modules/@strvmarv/total-recall/bin/start.js --version` (npm) and `node ~/.pi/agent/git/github.com/strvmarv/total-recall/bin/start.js --version` (git). User steps: start a new pi session (`/reload` is given as an alternative, marked "from pi's documentation, not run"). Known gaps and What you get from `INSTALL.md` L192-213, without the stale "once the release ... is published" sentence. Evidence: the three `pi install` observations.
- [ ] **Step 3: `codex-cli.md`, `copilot-cli.md`, `cursor.md`.** Carry `INSTALL.md` L215-244, L139-186 and L263-307 into the six sections, keeping each "observed on ... Windows" attribution in Evidence. Remove the stale conditionals (L150, L224) and say instead what the marketplace entry is. Apply the spec's rules: Codex's `config.toml` edit is **Ask first**, the hook-trust bypass flag is information only, `/hooks` trust is a User step; Copilot's `--allow-tool=total-recall` and Cursor's `--approve-mcps` are User steps; Cursor's Install opens with the macOS/Linux stop for the plugin path and offers only the MCP-only option there, marked not run; the Copilot MCP-only option reads `~/.copilot/mcp-config.json` after the plain install and accepts an `npx` entry. The MCP-only option on the Copilot and Cursor pages (the Codex section has none) mentions the global npm install, so each gets the disclosure paragraph above it, the plain form then the skip form, the hand-written-entry rule (add only the entry; stop and ask if one exists that this install did not write), the statement that nothing delivers the `session_start` instruction, and that instruction as a User step worded as on the Zed page (Task 4). The Cursor page says beside the skip form that the install script configures nothing for Cursor; the Copilot page does not (the script can write its MCP config). Verify directories: Codex `<CODEX_HOME or ~/.codex>/plugins/cache/<marketplace>/total-recall/<version>/` (from `codex/bootstrap.cjs`), Copilot `~/.copilot/installed-plugins/strvmarv-total-recall-marketplace/total-recall/` (observed), Cursor `~/.cursor/plugins/local/total-recall/`.
- [ ] **Step 4: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: no problem mentions any of these five pages except "INSTALL.md table" problems (the table arrives in Task 6).

- [ ] **Step 5: Commit.**

```bash
git add docs/hosts/claude-code.md docs/hosts/pi.md docs/hosts/codex-cli.md docs/hosts/copilot-cli.md docs/hosts/cursor.md
git commit -m "docs: add install pages for Claude Code, pi, Codex CLI, Copilot CLI and Cursor"
```

### Task 4: Pages for the harnesses npm configures

**Files:**
- Create: `docs/hosts/gemini-cli.md`, `docs/hosts/kiro.md`, `docs/hosts/windsurf.md`, `docs/hosts/zed.md`, `docs/hosts/vscode.md`, `docs/hosts/cline.md`, `docs/hosts/hermes.md`
- Read: the matching `scripts/install-*.js`, `.kiro/`, `hermes-plugin/`, `INSTALL.md` L246-261, `src/TotalRecall.Infrastructure/Importers/ClineImporter.cs`, and the spec's rules for these pages

**Interfaces:**
- Consumes: Global Constraints; `docs/npm-postinstall.md`.
- Produces: seven pages with one shape. Install: the disclosure paragraph; path 1 `npm install -g @strvmarv/total-recall` (**Ask first**), then "read `<the file the script writes>` and confirm the `total-recall` entry; if it is absent use path 2"; path 2 the npm skip form, then the hand-written entry (the script's file, key and shape, with `"command": "total-recall"` and no `args`), then each extra action the script performs as its own optional **Ask first** step. Verify, on each of the seven: `total-recall --version` (expected to equal `npm view @strvmarv/total-recall version`); the configured check (read the file the script writes and confirm the `total-recall` entry, with path 2 as the fallback; Cline and Hermes differ, below); and a reference to the check prompt.

- [ ] **Step 1: `gemini-cli.md`, `kiro.md`, `windsurf.md`.** Path 2 extras, written the way each script writes them: Gemini CLI, the marked block inserted into `GEMINI.md` (never replacing the file) and the `BeforeAgent` hook with `total-recall pinned-floor --host gemini-cli`; Windsurf, the rules file as the steering body without front matter; Kiro, the steering file copied whole. The installed package is located with `npm root -g`, marked not run. Kiro: the hooks are not installed by the script; copying the package's `.kiro/hooks/` into a workspace's `.kiro/hooks/` is a User step, attributed to the script's header comment, not run. These three pages do not say "nothing delivers the instruction" for path 1; on path 2 the rules or steering step is the answer.
- [ ] **Step 2: `zed.md`, `vscode.md`.** No rules file exists for these: both paths say that nothing delivers the `session_start` instruction, and that adding it is a User step, linking `https://raw.githubusercontent.com/strvmarv/total-recall/main/.kiro/steering/total-recall.md` (the body below its front matter; its "Sticky directives" paragraph describes Kiro's hook) for "your harness's persistent instructions file". The VS Code page offers the `~/.copilot/mcp-config.json` entry as an optional path 2 step. Zed Evidence: the entry observed in `~/Library/Application Support/Zed/settings.json`.
- [ ] **Step 3: `cline.md`.** State where the script writes and that the repository's importer reads Cline's settings elsewhere, so the written entry is not evidence that Cline is configured. On both paths the entry is added through Cline's own "MCP Settings" command as a User step (the steps of `INSTALL.md` L246-261, with the npm step first). No hooks: the `session_start` instruction is a User step as on the Zed page.
- [ ] **Step 4: `hermes.md`.** No MCP entry exists. Path 1 is the plain install; path 2 is the skip form, then the two actions by hand (copy `hermes-plugin/` into Hermes's plugins directory as `total-recall`; `hermes config set memory.provider total-recall`), each **Ask first**. Verify now: the plugins directory holds `total-recall` and `memory.provider` is `total-recall` in Hermes's config, falling back to the by-hand actions; next session: the user has Hermes remember a phrase of their choosing, then runs `total-recall memory recent` and sees it. Both marked not run. Say why the check prompt does not apply (the model is given one `memory` tool).
- [ ] **Step 5: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: no problem mentions any of these seven pages except "INSTALL.md table" problems.

- [ ] **Step 6: Commit.**

```bash
git add docs/hosts/gemini-cli.md docs/hosts/kiro.md docs/hosts/windsurf.md docs/hosts/zed.md docs/hosts/vscode.md docs/hosts/cline.md docs/hosts/hermes.md
git commit -m "docs: add install pages for the harnesses the npm install configures"
```

### Task 5: OpenCode, the generic page and the packaged pointer

**Files:**
- Create: `docs/hosts/opencode.md`, `docs/hosts/other-mcp.md`
- Modify: `.opencode/INSTALL.md` (whole file replaced)

**Interfaces:**
- Consumes: Global Constraints; `docs/npm-postinstall.md`.
- Produces: `docs/hosts/opencode.md` (link target for `README.md` in Task 6, with `docs/hosts/hermes.md`).

- [ ] **Step 1: `opencode.md`.** Install: disclosure paragraph, plain then skip form (the script configures nothing for OpenCode; say so), then the config in OpenCode's form:

```json
{
  "mcp": {
    "total-recall": { "type": "local", "command": ["total-recall"], "enabled": true }
  }
}
```

  in `~/.config/opencode/opencode.jsonc`, added as a hand-written entry (add only this entry; stop and ask if one exists). The `skills/` copy is a step that copies from the installed package or a checkout, not run. Nothing delivers the `session_start` instruction: User step, as on the Zed page. Evidence: the OpenCode 1.16.2 config observed.
- [ ] **Step 2: `other-mcp.md`.** From `INSTALL.md` L92-103 without version numbers or `@beta`: disclosure, plain then skip form, the generic `mcpServers` entry with `"command": "total-recall"`, the `session_start` User step.
- [ ] **Step 3: Replace `.opencode/INSTALL.md`** with a title, one sentence saying the OpenCode instructions live on the page, and the page's blob URL `https://github.com/strvmarv/total-recall/blob/main/docs/hosts/opencode.md`. No fenced block, no command.
- [ ] **Step 4: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: `rule 8 holds on the repository` passes; no problem mentions `opencode.md` or `other-mcp.md` except "INSTALL.md table" problems.

- [ ] **Step 5: Commit.**

```bash
git add docs/hosts/opencode.md docs/hosts/other-mcp.md .opencode/INSTALL.md
git commit -m "docs: add the OpenCode and generic MCP install pages and point the packaged file at them"
```

### Task 6: The runbook, and the README lines that depend on it

**Files:**
- Modify: `INSTALL.md` (whole file replaced), `README.md` (nine links and lines 91, 117, 137, 139 as numbered at `main`)

**Interfaces:**
- Consumes: all fourteen pages, `docs/database-location.md`, the check prompt and status line of Global Constraints.

- [ ] **Step 1: Replace `INSTALL.md`** with the eight parts the spec lists under "`INSTALL.md` (the runbook)": who this is for; the Rules (five rules: your harness's page only; **User** steps are relayed and never claimed; **Ask first** as the spec defines it; no success before Verify and no escalation of a failed command; touch only the `total-recall` entry of a config file and never print the rest); Before you start; Step 1 with the table `| Harness | Page |` (fourteen rows in the order of Global Constraints, each page as its raw URL), "ask the user if you do not know which harness you are running in", and the generic page for anything unlisted; Step 2; Step 3; Step 4 with the status line as an example and the check prompt; Step 5 with the report wording the spec gives. No install command, no version number.
- [ ] **Step 2: Edit `README.md`.** Repoint `INSTALL.md#pi` → `docs/hosts/pi.md`, `#cursor` → `docs/hosts/cursor.md`, `#codex-cli` → `docs/hosts/codex-cli.md`, `#github-copilot-cli` → `docs/hosts/copilot-cli.md` (two each, link text becomes the page name), `#relocating-the-database` → `docs/database-location.md`. L91: remove the comment `# once the release carrying pi support is published`. L117: remove the sentence that begins "This works once the marketplace points at a release". L137: the sentence lists Copilot CLI, Cline and Cursor as examples of clients that read an `mcpServers` block, and says OpenCode and Hermes are configured differently, linking `docs/hosts/opencode.md` and `docs/hosts/hermes.md`. L139: replace the note with "> **Note:** prefer the global install. `npx -y @strvmarv/total-recall` runs a copy that is already installed (for example an older global install) instead of fetching the latest release." Nothing else in the README changes.
- [ ] **Step 3: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: every test passes.

Run: `grep -n -e 'does not work due to' -e 'once the release carrying' -e 'This works once the marketplace' -e 'OpenCode\*\*, \*\*Cline' README.md`
Expected: no output.

Run: `git diff --stat origin/main -- README.md codex/ copilot/ cursor/`
Expected: `README.md` only, with about a dozen changed lines.

- [ ] **Step 4: Commit.**

```bash
git add INSTALL.md README.md
git commit -m "docs: rewrite INSTALL.md as an agent runbook and repoint the README"
```

### Task 7: Run the check in CI and record the change

**Files:**
- Modify: `package.json` (`scripts.test:shim`), `.github/develop.md` (second verify block), `CONTRIBUTING.md` (the sentence after `npm run test:shim` that lists the test files), `AGENTS.md` ("WHERE TO LOOK" table), `CHANGELOG.md`

- [ ] **Step 1: See the wiring test fail.** Add `scripts/host-docs.test.js` to `test:shim` in `package.json`, after `scripts/release-workflow.test.js`.

Run: `node --test skills/develop/scripts/profile.test.js`
Expected: the test named `.github/develop.md: the repository profile parses ...` fails, because the profile's verify command no longer equals `test:shim`.

- [ ] **Step 2: Add the same path at the same position** in the second `sh verify` block of `.github/develop.md`.

Run: `node --test skills/develop/scripts/profile.test.js`
Expected: every test passes.

- [ ] **Step 3: Update the prose.** `CONTRIBUTING.md`: add `scripts/host-docs.test.js` to the listed files. `AGENTS.md` "WHERE TO LOOK": one row, `| Install instructions (agent runbook, one page per harness) and their drift check | \`INSTALL.md\`, \`docs/hosts/\`, \`docs/npm-postinstall.md\`, \`scripts/host-docs.test.js\` |`. `CHANGELOG.md`: a `## Unreleased` section above `## 4.7.0` with `### Changed` and one entry describing the runbook, the harness pages, the corrected release guidance and the drift check (#74).
- [ ] **Step 4: Run the whole shim suite.**

Run: `npm run test:shim`
Expected: every test passes, including `scripts/host-docs.test.js`.

- [ ] **Step 5: Commit.**

```bash
git add package.json .github/develop.md CONTRIBUTING.md AGENTS.md CHANGELOG.md
git commit -m "test: run the install documentation check in test:shim"
```
