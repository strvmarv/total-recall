# README Front Door Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `README.md` into a front door of about 150 lines, move its reference sections, corrected, to pages under `docs/`, and extend the documentation check so it stays that way.

**Architecture:** Documentation plus new rules in the existing zero-dependency test `scripts/host-docs.test.js`. The rules are written first as functions of a `tree` that return problems (the pattern already in that file), each with a test on the repository and defect tests on an in-memory tree. The reference pages are created by moving README text whole, then the README is rewritten until every rule passes.

**Tech Stack:** Markdown; Node 20 built-ins (`node:test`, `node:assert/strict`, `node:crypto`, `node:fs`, `node:os`, `node:path`, `node:url`), ESM, no dependencies.

**Spec:** `docs/superpowers/specs/2026-11-14-readme-front-door-design.md`. It is read-only for the whole run. Where this plan and the spec differ, the spec wins. The README line numbers below are the README at `e3359b6` (`git show e3359b6:README.md`).

## Global Constraints

- The spec file is never edited.
- No product code changes: nothing under `src/`, `bin/`, `pi/`, `codex/`, `copilot/`, `cursor/`, `hooks/`, `hermes-plugin/`, `.kiro/`, `scripts/postinstall.js`, `scripts/install-*.js`. `INSTALL.md` and `docs/hosts/*.md` are not edited.
- The banner (the README's first fenced block) is unchanged, byte for byte.
- Moved text is moved whole: no paragraph, table row, command or number is dropped or reworded except the corrections the spec lists (1 to 10). Heading levels are raised so the page title is the `#` heading; relative links and the image path are rewritten for the page's place under `docs/` (a link `docs/hosts/pi.md` becomes `hosts/pi.md`, `docs/database-location.md` becomes `database-location.md`, `CONTRIBUTING.md` becomes `../CONTRIBUTING.md`).
- Every correction is re-verified against the tree before it is written; if a check fails, the old text stays and the PR says so.
- The test uses Node built-ins only and must pass on Node 20 with no `npm ci`. `npm run test:shim` cannot run on Node 24; use the glob form in `.github/develop.md`.
- README `##` headings, exact and in order: `Install`, `Check that it works`, `Use it`, `How it works`, `Documentation`, `Privacy`, `Contributing`, `Credits`, `License`. Under `Install`, `###` headings in order: `Let your assistant install it`, `Claude Code`, `pi`, `Other harnesses`.
- The README holds exactly one npm-running command (the `pi install npm:@strvmarv/total-recall` line) and no `npx`. The "Other harnesses" table describes installs in words ("npm package plus configuration"), never with the literal command.
- The check prompt (from `INSTALL.md` Step 4) and the status-line example (from `skills/commands/SKILL.md`) are copied character for character.
- Commits carry no AI attribution and no co-author trailer.

## Review Focus

1. Lost content: every section of the old README must exist somewhere in the new README or under `docs/` (the final review compares them).
2. A README heading inside the banner or another fenced block must not count as a heading (the existing `headings()` helper strips fences).
3. Rule 13 must not require links to `docs/TODO.md`, `docs/superpowers/**`, `docs/images/**` or non-Markdown files.
4. Rule 15 must not accept `usage_status` as proof that `status` is documented.
5. Widening rule 3 to all `docs/*.md` must not start checking `docs/TODO.md`.

---

### Task 1: The new rules, written first

**Files:**
- Modify: `scripts/host-docs.test.js`

**Interfaces:**
- Produces, in the style of the existing checks (`(tree) => string[]`):
  - `checkReadmeShape(tree)` rules 10 and 11: `README.md` has at most `README_MAX_LINES` (200) lines; the sha256 of its first fenced block equals `BANNER_SHA256` (computed once from `git show e3359b6:README.md` and stored as a constant); its `##` headings equal `README_SECTIONS` in order; the `###` headings between `## Install` and the next `##` equal `INSTALL_PARTS` in order.
  - `checkReadmeContent(tree)` rules 12 and 14: the README contains `RAW + 'INSTALL.md'`; it contains the check prompt (the text of the fenced block in `INSTALL.md`'s `## Step 4` section that starts `Check that total-recall is working`, read from `INSTALL.md`, not repeated as a constant); it contains the status line captured from `skills/commands/SKILL.md` (reuse the capture of `checkStatusLine`); `NPM_RUNNING` matches exactly once, on a line containing `pi install npm:`; `/\bnpx\b/` does not match.
  - `checkDocsReachable(tree)` rule 13: every `docs/*.md` except `docs/TODO.md`, and every `docs/hosts/*.md`, is the target of a link in `README.md` (resolved as `checkLinks` resolves links).
  - `checkToolReference(tree)` rule 15: `catalog.json` parses to a list of tools with names (handle both a top-level array and a `tools` array); every name appears in `docs/tools.md` as a code span (`` `name` ``); `docs/tools.md` contains `<N> in SQLite mode` where N is the number of tools.
  - `checkLinks` (rule 3) takes its file list from `tree.list('docs')`: every `docs/*.md` except `docs/TODO.md`, plus `INSTALL.md`, `README.md`, `docs/hosts/*.md` and `.opencode/INSTALL.md`. The three pages it required by name stay required.
- Consumes: the existing helpers `headings`, `links`, `fencedBlocks`, `repoTree`, `memoryTree`, `validFiles`, `defect`.

- [ ] **Step 1: Compute the banner hash.** Run: `git show e3359b6:README.md | node -e "const c=require('crypto');let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const m=/^(\`{3,})[^\n]*\n[\s\S]*?^\1[ \t]*$/m.exec(s);console.log(c.createHash('sha256').update(m[0]).digest('hex'))})"`. Record the value as `BANNER_SHA256`. The test hashes the block the same way (the `body` that `fencedBlocks` returns for the first block).
- [ ] **Step 2: Write the tests.** For each new check: `rule N holds on the repository`, and defect tests on `validFiles()` extended with a valid README (banner, the nine `##` headings, the four `###` headings, the prompt URL, the check prompt, the status line, one `pi install npm:` line, links to every docs page), a `docs/tools.md` and a `catalog.json`. Defects, one test each: a 201-line README; a banner with one changed character; `Use it` before `Check that it works`; a missing `## Privacy`; `### pi` before `### Claude Code`; a `## Fake` heading inside a fenced block is ignored (passes); the check prompt reworded; the status line missing; a second npm-running line; an `npx` line; a new `docs/extra.md` not linked; a `docs/hosts/x.md` not linked; `docs/TODO.md` and `docs/superpowers/specs/a.md` unlinked are ignored (passes); a tool in `catalog.json` absent from `docs/tools.md`; `usage_status` present but `status` absent fails; the SQLite count off by one; a broken link inside a new `docs/*.md` page is reported by rule 3; a broken link inside `docs/TODO.md` is not.
- [ ] **Step 3: Run and see the new tests fail.**

Run: `node --test scripts/host-docs.test.js`
Expected: the new tests fail with `ReferenceError` (the checks do not exist); the existing 67 pass.

- [ ] **Step 4: Implement the checks and constants.**
- [ ] **Step 5: Run again.**

Run: `node --test scripts/host-docs.test.js`
Expected: every defect test and every pre-existing test passes. These repository tests fail, for these reasons: rules 10 and 11 (the README has 670 lines and other headings); rule 12/14 (no check prompt; several npm-running lines); rule 13 (`docs/npm-postinstall.md` and `docs/first-run.md` and most `docs/hosts/*.md` are not linked from the README); rule 15 (`docs/tools.md` is missing).

- [ ] **Step 6: Commit.**

```bash
git add scripts/host-docs.test.js
git commit -m "test: add README shape, reachability and tool-reference rules to the docs check"
```

### Task 2: Reference pages, part one (how it works, platforms, web UI, commands)

**Files:**
- Create: `docs/how-it-works.md`, `docs/platforms.md`, `docs/web-ui.md`, `docs/commands.md`
- Read: `git show e3359b6:README.md` L143-157, L161-222, L226-237, L241-274, L278-315; `src/TotalRecall.Infrastructure/Importers/*.cs` (header comments); `docs/hosts/*.md` ("What you get", "Known gaps"); `skills/commands/SKILL.md`; `AGENTS.md` ("What happens on `session_start`")

- [ ] **Step 1: `docs/how-it-works.md`.** Title `# How total-recall works`. Move "What Gets Remembered" (L143-157) and "How It Works" (L161-222) whole. Corrections: 7 (the hot tier is "injected at session start", wherever "auto-injected into every prompt" or "per prompt" describes the hot tier); 8, second half (in the "Import sync" step, one clause for each of the six tools the step names, saying what is imported, written as the importer's header comment has it; reword the `Imported` example cell to agree). The per-host floor table stays here.
- [ ] **Step 2: `docs/platforms.md`.** Title `# Supported platforms`. Rebuild the table (correction 8, first half): one row for each of the 14 harness pages in `INSTALL.md` order; columns: Harness (linking `hosts/<id>.md`), MCP tools, Startup instructions, Pinned floor, Commands, Notes. Every cell is taken from that page's "What you get" and "Known gaps"; no "Full" or "Core" grade. Keep the known-gaps summaries of today's rows where the page supports them.
- [ ] **Step 3: `docs/web-ui.md`.** Title `# Web UI`. Move L241-274 whole; the image becomes `images/web-ui-dashboard.png`. No correction (seven sections is right, correction 9).
- [ ] **Step 4: `docs/commands.md`.** Title `# Commands`. Move L278-315 whole. Correction 5: add rows for `setup` (Claude Code only), `recent`, `update`, `ui`; `compact [--fast]`; remove `eval --grow`, and add one line that benchmark candidates are reviewed with the CLI `total-recall eval grow list|resolve` or the `eval_grow` tool (verify `src/TotalRecall.Cli/Commands/Eval/GrowCommand.cs` first).
- [ ] **Step 5: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: rule 3 passes (the four pages' links resolve); rules 10 to 15 still fail on the repository for the reasons of Task 1 (rule 13 now also names these four pages as unlinked).

- [ ] **Step 6: Commit.**

```bash
git add docs/how-it-works.md docs/platforms.md docs/web-ui.md docs/commands.md
git commit -m "docs: move how it works, platforms, web UI and commands out of the README"
```

### Task 3: Reference pages, part two (configuration to building)

**Files:**
- Create: `docs/configuration.md`, `docs/cortex.md`, `docs/skills.md`, `docs/develop.md`, `docs/tools.md`, `docs/architecture.md`, `docs/verifying-a-release.md`, `docs/building.md`
- Read: `git show e3359b6:README.md` L319-401, L403-435, L437-478, L480-520, L524-549, L553-592 and L654-664, L596-607, L611-636; `src/TotalRecall.Infrastructure/Config/defaults.toml`; `src/TotalRecall.Server/ServerComposition.cs` (`BuildRegistry`); `catalog.json`

- [ ] **Step 1: `docs/configuration.md`** (title `# Configuration`): L319-401 whole. Corrections 3 (`default = "user:local"`, with the comment `# Default scope for new entries (e.g. "user:paul", "team:eng")`), 4 (remove the `[usage]` table with `initial_backfill_days`; verify by grepping `src/` for a reader of the key), 7 (the `max_entries` comment says "injected at session start"). The link to relocating the database becomes `database-location.md`.
- [ ] **Step 2: `docs/cortex.md`** (`# Connecting to Cortex`, L403-435) and **`docs/skills.md`** (`# Skills`, L437-478), whole, no correction.
- [ ] **Step 3: `docs/develop.md`** (`# The develop skill`, L480-520), whole, no correction.
- [ ] **Step 4: `docs/tools.md`** (`# MCP tools`, L524-549). Corrections 1 and 2: the opening sentence reads "The MCP server exposes 42 core tools in every backend mode; 50 in SQLite mode and 51 in Cortex mode, which add usage, cache, skill and feedback tools." (count the registrations in `BuildRegistry` and `catalog.json` first); add the row `| Insights | \`insights\` |`. Every name in `catalog.json` must appear as a code span.
- [ ] **Step 5: `docs/architecture.md`** (`# Architecture`): L553-592 whole, then "Core Technologies" (L654-664) as `## Core technologies`. Corrections 1 (the `TotalRecall.Server` line: "42 core tool handlers (50 to 51 with mode-dependent tools)") and 7 (the Hot line).
- [ ] **Step 6: `docs/verifying-a-release.md`** (`# Verifying a release`, L596-607) and **`docs/building.md`** (`# Building from source`: "Prerequisites" L611-617 as `## Prerequisites`, "Installation from Source" L621-636 as `## Build`). Correction 6: drop "~1000 tests" from the comment.
- [ ] **Step 7: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: `rule 15 holds on the repository` and rule 3 pass; rules 10 to 14 still fail on the repository.

- [ ] **Step 8: Commit.**

```bash
git add docs/configuration.md docs/cortex.md docs/skills.md docs/develop.md docs/tools.md docs/architecture.md docs/verifying-a-release.md docs/building.md
git commit -m "docs: move configuration, Cortex, skills, develop, tools, architecture, release verification and building out of the README"
```

### Task 4: The README

**Files:**
- Modify: `README.md` (everything below the banner and the badges is replaced)

- [ ] **Step 1: Write the README** to the structure in the spec's "`README.md`" section and the headings in Global Constraints: banner unchanged; badges, title, one-liner, a pitch of at most four sentences; `## Install` with the requirements line (Node.js 20 or later; Linux x64 or arm64, macOS on Apple Silicon, Windows x64; an Intel Mac is not supported), then the four parts (the pasted prompt; Claude Code shell commands in one block, the slash commands in one line, link `docs/hosts/claude-code.md`; pi with the one `pi install npm:` line, the sentence about the install script with a link to `docs/npm-postinstall.md`, link `docs/hosts/pi.md`; the twelve-row "Other harnesses" table); `## Check that it works`; `## Use it` (six entries); `## How it works` (at most 150 words, link `docs/how-it-works.md`); `## Documentation` (a table linking every `docs/*.md` page except `docs/TODO.md`, and `INSTALL.md`, `PRIVACY.md`, `CONTRIBUTING.md`, `CHANGELOG.md`); `## Privacy`; `## Contributing`; `## Credits`; `## License`.
- [ ] **Step 2: Check "The Problem" and "The Solution" (old L40-63) bullet by bullet.** For each fact not in the new README, find it under `docs/`; add one sentence to the matching page for any that exists nowhere (for example the list of tools memories are shared across, token-expenditure tracking, the cross-device note pointing at `database-location.md`). Record the mapping in the task report.
- [ ] **Step 3: Run.**

Run: `node --test scripts/host-docs.test.js`
Expected: every test passes.

Run: `wc -l README.md` and count the words between `## How it works` and `## Documentation`
Expected: at most 200 lines (target about 150); at most 150 words.

- [ ] **Step 4: Commit.**

```bash
git add README.md docs
git commit -m "docs: make the README a front door"
```

### Task 5: Privacy policy, references that follow, changelog

**Files:**
- Modify: `PRIVACY.md`, `skills/develop/SKILL.md`, `skills/develop/tutorial.md`, `AGENTS.md`, `docs/TODO.md`, `CHANGELOG.md`; `CONTRIBUTING.md` only if a grep finds a link into a README section that moved

- [ ] **Step 1: `PRIVACY.md`** (correction 10, defined by claim): the archive size becomes about 100 MB; every statement that no data is transmitted, that there is no telemetry, usage analytics, cloud sync or remote copy, that text is never sent to an embedding service, that the data lives in a local SQLite database or that embeddings are computed locally gets "by default"; statements scoped to the engine download or the web UI stay; the Summary's "two narrow, opt-in cases" points at the section; one paragraph under "What data leaves your machine" names the opt-in configurations as `docs/configuration.md` and `docs/cortex.md` have them (a Postgres backend; Cortex: memory sync and telemetry to the instance the user configures, team knowledge base queries, skills only when `[skills] auto_import` is on; a remote embedder: OpenAI or Bedrock); the "last updated" date becomes November 2026. Nothing else changes.
- [ ] **Step 2: The skill sentences.** In `skills/develop/SKILL.md` (Preflight, check 8) and `skills/develop/tutorial.md`, "the README lists the minimal allowlist" becomes a reference to `docs/develop.md` in the total-recall repository. Run `node --test skills/develop/scripts/*.test.js` and expect every test to pass (a test that pins the old sentence is updated with it, and the report says so).
- [ ] **Step 3: `AGENTS.md` and `docs/TODO.md`.** Add one "WHERE TO LOOK" row: `| User documentation (README front door, reference pages) and its check | \`README.md\`, \`docs/*.md\`, \`scripts/host-docs.test.js\` |`. Repoint `(README, "Verifying a release")` to `docs/verifying-a-release.md` in both files. `grep -rn 'README' CONTRIBUTING.md AGENTS.md skills pi hooks scripts docs/*.md` and repoint any other reference to a README section that moved.
- [ ] **Step 4: `CHANGELOG.md`.** Under `## Unreleased`, `### Changed`: one entry for the README front door and the new reference pages (naming the corrections that change what a reader is told: tool counts, `insights`, the default scope, the removed key, the commands, the platform table, the privacy policy), and one for the new check rules (#77).
- [ ] **Step 5: Run the whole shim suite.**

Run: the `node --test ...` line of the second `sh verify` block in `.github/develop.md`
Expected: every test passes.

- [ ] **Step 6: Commit.**

```bash
git add PRIVACY.md skills/develop/SKILL.md skills/develop/tutorial.md AGENTS.md docs/TODO.md CHANGELOG.md
git commit -m "docs: correct the privacy policy and repoint references to moved README sections"
```
