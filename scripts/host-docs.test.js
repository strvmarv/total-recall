// scripts/host-docs.test.js — guards the install instructions: INSTALL.md (the agent runbook), the
// per-harness pages in docs/hosts/, and the facts they carry that go stale.
//
// Every check is a function of a `tree` ({read(path), list(dir)}), so the same check runs on the real
// repository and, with one deliberate defect, on an in-memory copy to prove it can fail. No dependencies.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const RAW = 'https://raw.githubusercontent.com/strvmarv/total-recall/main/';
const BLOB = 'https://github.com/strvmarv/total-recall/blob/main/';

export const HARNESSES = [
  { id: 'claude-code', name: 'Claude Code' },
  { id: 'pi', name: 'pi' },
  { id: 'codex-cli', name: 'Codex CLI' },
  { id: 'copilot-cli', name: 'Copilot CLI' },
  { id: 'cursor', name: 'Cursor' },
  { id: 'gemini-cli', name: 'Gemini CLI' },
  { id: 'kiro', name: 'Kiro' },
  { id: 'opencode', name: 'OpenCode' },
  { id: 'cline', name: 'Cline' },
  { id: 'windsurf', name: 'Windsurf' },
  { id: 'zed', name: 'Zed' },
  { id: 'vscode', name: 'VS Code' },
  { id: 'hermes', name: 'Hermes' },
  { id: 'other-mcp', name: 'Any other MCP client' },
];
export const PAGE_HEADINGS = ['Install', 'User steps', 'Verify', 'What you get', 'Known gaps', 'Evidence'];
// The install modules scripts/postinstall.js imports, and the tools each one may change.
export const MODULE_TOOLS = {
  'install-hermes-plugin': ['Hermes'],
  'install-kiro-hooks': ['Kiro'],
  'install-cline': ['Cline'],
  'install-windsurf': ['Windsurf'],
  'install-gemini': ['Gemini CLI'],
  'install-zed': ['Zed'],
  'install-vscode': ['VS Code', 'Copilot CLI'],
};
export const TOOLS = [...new Set(Object.values(MODULE_TOOLS).flat())];
// A command that runs an install: what INSTALL.md must not hold, and what needs the disclosure above it.
const INSTALL_COMMAND = /\b(?:npm|pi|plugin) install\b|\bplugin add\b/;
const NPM_RUNNING = /\b(?:npm|pi) install\b/;
const STALE_VERSION = /(?<![\d.])0\.(?:7\.2|8\.(?:\d+|x))(?!\d)/;

// ---------------------------------------------------------------------------------------------
// Text helpers
// ---------------------------------------------------------------------------------------------

/** CRLF to LF. */
export function normalise(text) {
  return text.replace(/\r\n/g, '\n');
}

const FENCE = /^(`{3,}|~{3,})[^\n]*\n[\s\S]*?(?:^\1[ \t]*$|(?![\s\S]))/gm;
const blank = (s) => s.replace(/[^\n]/g, ' ');

/** The text with fenced blocks blanked (newlines kept), so offsets and line numbers hold. */
function stripFences(md) {
  return md.replace(FENCE, blank);
}

/** The text with fenced blocks and inline code spans blanked. */
export function stripCode(md) {
  return stripFences(md).replace(/`[^`\n]*`/g, blank);
}

export function fencedBlocks(md) {
  return [...md.matchAll(FENCE)].map((m) => ({ index: m.index, body: m[0] }));
}

/** GitHub's heading anchor. */
export function slugify(text) {
  return text
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .toLowerCase()
    .replace(/[^\p{L}\p{N} _-]/gu, '')
    .replace(/ /g, '-');
}

/** ATX headings outside fences, with GitHub's -1, -2 suffix on repeated text. */
export function headings(md) {
  const seen = new Map();
  const out = [];
  for (const line of stripFences(md).split('\n')) {
    const m = /^(#{1,6})[ \t]+(.+?)[ \t]*#*[ \t]*$/.exec(line);
    if (!m) continue;
    const text = m[2].trim();
    const base = slugify(text);
    const n = seen.get(base) ?? 0;
    seen.set(base, n + 1);
    out.push({ level: m[1].length, text, slug: n === 0 ? base : `${base}-${n}` });
  }
  return out;
}

/** Targets of `[text](target)` and `![alt](target)`, outside code. */
export function links(md) {
  return [...stripCode(md).matchAll(/\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)].map((m) => m[1]);
}

/** The repository path after either base URL, wherever the URL appears (prose, code or a fence). */
export function repoPaths(md) {
  const re = /https:\/\/(?:raw\.githubusercontent\.com\/strvmarv\/total-recall\/main|github\.com\/strvmarv\/total-recall\/blob\/main)\/([^\s)\]>"'`]+)/g;
  return [...md.matchAll(re)].map((m) => m[1].replace(/#.*$/, '').replace(/[.,;:]+$/, ''));
}

// ---------------------------------------------------------------------------------------------
// Trees
// ---------------------------------------------------------------------------------------------

/** A tree over a directory. `read` gives LF text or null; `list` the names directly under a directory. */
export function repoTree(root = ROOT) {
  return {
    read(p) {
      try {
        return normalise(fs.readFileSync(path.join(root, p), 'utf8'));
      } catch {
        return null;
      }
    },
    list(dir) {
      try {
        return fs.readdirSync(path.join(root, dir));
      } catch {
        return [];
      }
    },
  };
}

/** A tree over an object of path to text. */
export function memoryTree(files) {
  return {
    read: (p) => (Object.hasOwn(files, p) ? normalise(files[p]) : null),
    list(dir) {
      const prefix = dir.replace(/\/$/, '') + '/';
      return [...new Set(Object.keys(files).filter((k) => k.startsWith(prefix)).map((k) => k.slice(prefix.length).split('/')[0]))];
    },
  };
}

const exists = (tree, p) => tree.read(p) !== null || tree.list(p).length > 0;
const hostPages = (tree) => tree.list('docs/hosts').filter((n) => n.endsWith('.md')).map((n) => `docs/hosts/${n}`);

/** The rows `| <name> | <raw url> |` of INSTALL.md's harness table. */
function tableRows(tree) {
  const text = tree.read('INSTALL.md') ?? '';
  return [...text.matchAll(/^\|\s*(.+?)\s*\|\s*(https?:\/\/\S+?)\s*\|\s*$/gm)].map((m) => ({ name: m[1], url: m[2] }));
}

// ---------------------------------------------------------------------------------------------
// Checks. Each returns a list of problems, empty when the rule holds.
// ---------------------------------------------------------------------------------------------

/** Rule 1: one page per harness with the six headings in order, and a table that names them all. */
export function checkPages(tree) {
  const problems = [];
  for (const h of HARNESSES) {
    const p = `docs/hosts/${h.id}.md`;
    const text = tree.read(p);
    if (text === null) {
      problems.push(`${p} is missing`);
      continue;
    }
    const got = headings(text).filter((x) => x.level === 2).map((x) => x.text);
    if (got.join('|') !== PAGE_HEADINGS.join('|')) {
      problems.push(`${p}: the "##" headings are [${got.join(', ')}], expected [${PAGE_HEADINGS.join(', ')}]`);
    }
  }
  const expected = new Set(HARNESSES.map((h) => `docs/hosts/${h.id}.md`));
  for (const p of hostPages(tree)) if (!expected.has(p)) problems.push(`${p} is not a harness page`);
  const rows = tableRows(tree);
  const want = HARNESSES.map((h) => `${h.name} ${RAW}docs/hosts/${h.id}.md`);
  const got = rows.map((r) => `${r.name} ${r.url}`);
  if (got.join('\n') !== want.join('\n')) {
    problems.push(`INSTALL.md: the harness table rows are [${got.join('; ')}], expected [${want.join('; ')}]`);
  }
  return problems;
}

/** Rule 2: no install command or version in INSTALL.md; no stale release guidance on a page. */
export function checkNoStaleFacts(tree) {
  const problems = [];
  const install = tree.read('INSTALL.md');
  if (install !== null) {
    const cmd = INSTALL_COMMAND.exec(install);
    if (cmd) problems.push(`INSTALL.md holds an install command ("${cmd[0]}")`);
    const ver = /\d+\.\d+\.\d+/.exec(install);
    if (ver) problems.push(`INSTALL.md holds a version number ("${ver[0]}")`);
  }
  for (const p of [...hostPages(tree), '.opencode/INSTALL.md']) {
    const text = tree.read(p);
    if (text === null) continue;
    if (text.includes('@beta')) problems.push(`${p} mentions @beta`);
    const stale = STALE_VERSION.exec(text);
    if (stale) problems.push(`${p} holds the stale version ${stale[0]}`);
  }
  return problems;
}

/** Rule 3: every file the docs link to exists, with its anchor, and so does every repository URL. */
export function checkLinks(tree) {
  const problems = [];
  const fixed = ['INSTALL.md', 'README.md', 'docs/npm-postinstall.md', 'docs/database-location.md', 'docs/first-run.md'];
  for (const p of fixed) if (tree.read(p) === null) problems.push(`${p} is missing`);
  const files = [...new Set([...fixed, ...hostPages(tree), '.opencode/INSTALL.md'])];
  for (const p of files) {
    const text = tree.read(p);
    if (text === null) continue;
    const dir = path.posix.dirname(p);
    for (const target of links(text)) {
      if (/^[a-z][a-z0-9+.-]*:/i.test(target)) continue;
      const [rel, anchor] = target.split('#');
      const resolved = rel === '' ? p : path.posix.normalize(path.posix.join(dir, decodeURI(rel)));
      if (!exists(tree, resolved)) {
        problems.push(`${p}: the link "${target}" points at ${resolved}, which does not exist`);
        continue;
      }
      if (anchor && resolved.endsWith('.md')) {
        const body = tree.read(resolved);
        if (body !== null && !headings(body).some((h) => h.slug === decodeURI(anchor).toLowerCase())) {
          problems.push(`${p}: the link "${target}" has no heading "#${anchor}" in ${resolved}`);
        }
      }
    }
    if (p === 'INSTALL.md' || p.startsWith('docs/hosts/') || p === '.opencode/INSTALL.md') {
      for (const rp of repoPaths(text)) {
        if (!exists(tree, rp)) problems.push(`${p}: the repository URL for ${rp} points at a file that does not exist`);
      }
    }
  }
  return problems;
}

/** Rule 4: the example status line in INSTALL.md is the one in the commands skill. */
export function checkStatusLine(tree) {
  const skill = tree.read('skills/commands/SKILL.md');
  const m = skill === null ? null : /^\s*>\s*(total-recall loaded — .+)$/m.exec(skill);
  if (!m) return ['skills/commands/SKILL.md has no "total-recall loaded — ..." example status line'];
  const install = tree.read('INSTALL.md');
  return install !== null && install.includes(m[1]) ? [] : [`INSTALL.md does not contain the status line "${m[1]}"`];
}

/** Rules 5 and 6: the "Ask first" paragraph above any npm-running install, and the tool list it names. */
export function checkDisclosure(tree) {
  const problems = [];
  const postinstall = tree.read('scripts/postinstall.js');
  const modules = postinstall === null ? [] : [...postinstall.matchAll(/from '\.\/(install-[\w-]+)\.js'/g)].map((m) => m[1]);
  const known = Object.keys(MODULE_TOOLS);
  for (const m of modules) if (!known.includes(m)) problems.push(`scripts/postinstall.js imports ${m}, which MODULE_TOOLS does not list`);
  for (const k of known) if (!modules.includes(k)) problems.push(`MODULE_TOOLS lists ${k}, which scripts/postinstall.js does not import`);

  const ref = tree.read('docs/npm-postinstall.md');
  if (ref === null) problems.push('docs/npm-postinstall.md is missing');
  else for (const t of TOOLS) if (!ref.includes(t)) problems.push(`docs/npm-postinstall.md does not name ${t}`);

  const url = `${RAW}docs/npm-postinstall.md`;
  for (const h of HARNESSES) {
    const p = `docs/hosts/${h.id}.md`;
    const text = tree.read(p);
    if (text === null) continue;
    const first = NPM_RUNNING.exec(text);
    if (!first) continue;
    const above = text.slice(0, first.index);
    const para = above.split(/\n[ \t]*\n/).find((x) => x.trimStart().startsWith('**Ask first.**'));
    if (!para) {
      problems.push(`${p}: no "**Ask first.**" paragraph above the first "${first[0]}"`);
      continue;
    }
    for (const t of TOOLS) if (!para.includes(t)) problems.push(`${p}: the "Ask first" paragraph does not name ${t}`);
    for (const w of ['memory provider', 'hook']) if (!para.includes(w)) problems.push(`${p}: the "Ask first" paragraph does not say "${w}"`);
    if (!para.includes(url)) problems.push(`${p}: the "Ask first" paragraph does not link ${url}`);
  }
  return problems;
}

/** Rule 7: the names the hook scripts cite are rows of the INSTALL.md table. */
export function checkHookNotes(tree) {
  const problems = [];
  const names = new Set(tableRows(tree).map((r) => r.name));
  for (const p of ['codex/hook.js', 'copilot/hook.js', 'cursor/hook.js']) {
    const text = tree.read(p);
    const cited = text === null ? [] : [...text.matchAll(/INSTALL\.md, "([^"]+)"/g)].map((m) => m[1]);
    if (cited.length === 0) problems.push(`${p} cites no INSTALL.md name`);
    for (const c of cited) if (!names.has(c)) problems.push(`${p} cites INSTALL.md, "${c}", which is not a row of the INSTALL.md table`);
  }
  return problems;
}

/** Rule 8: the packaged OpenCode file only points at the page. */
export function checkOpenCodePointer(tree) {
  const text = tree.read('.opencode/INSTALL.md');
  if (text === null) return ['.opencode/INSTALL.md is missing'];
  const problems = [];
  if (fencedBlocks(text).length > 0) problems.push('.opencode/INSTALL.md holds a fenced block; it must only point at docs/hosts/opencode.md');
  if (!repoPaths(text).includes('docs/hosts/opencode.md')) problems.push('.opencode/INSTALL.md does not link docs/hosts/opencode.md');
  return problems;
}

// ---------------------------------------------------------------------------------------------
// Helper tests
// ---------------------------------------------------------------------------------------------

test('stripCode: fenced blocks and inline code are blanked, newlines kept', () => {
  const md = 'a `b` c\n\n```js\nx\n```\n\nz\n';
  const out = stripCode(md);
  assert.equal(out.length, md.length);
  assert.equal(out.split('\n').length, md.split('\n').length);
  assert.ok(!out.includes('b') && !out.includes('x') && !out.includes('js'));
  assert.ok(out.includes('a') && out.includes('z'));
});

test('fencedBlocks: finds each fence with its offset', () => {
  const md = 'x\n\n```sh\nnpm i\n```\n\n~~~\nplain\n~~~\n';
  const blocks = fencedBlocks(md);
  assert.equal(blocks.length, 2);
  assert.equal(blocks[0].index, md.indexOf('```'));
  assert.ok(blocks[1].body.includes('plain'));
});

test('slugify: GitHub anchors', () => {
  assert.equal(slugify('Option B: MCP only'), 'option-b-mcp-only');
  assert.equal(slugify('What `it` does_now'), 'what-it-does_now');
  assert.equal(slugify('[Link](x.md) text'), 'link-text');
});

test('links: a link inside a fence or an inline code span is not a link', () => {
  const md = 'a [ok](a.md)\n\n```\n[no](b.md)\n```\n\n`[no](c.md)` and ![img](d.png)\n';
  assert.deepEqual(links(md), ['a.md', 'd.png']);
});

test('links: a badge that wraps an image gives both targets', () => {
  assert.deepEqual(links('[![badge](img.svg)](https://x.example/y)'), ['img.svg', 'https://x.example/y']);
});

test('headings: repeated text gets -1, -2 suffixes', () => {
  assert.deepEqual(headings('# A\n\n## Option B: MCP only\n\n## Option B: MCP only\n').map((h) => h.slug),
    ['a', 'option-b-mcp-only', 'option-b-mcp-only-1']);
});

test('headings: a shell comment inside a fence is not a heading', () => {
  assert.deepEqual(headings('# T\n\n```sh\n# not a heading\n```\n').map((h) => h.text), ['T']);
});

test('parsing: CRLF input gives the same headings and links as LF', () => {
  const lf = '# T\n\n## Install\n\n[x](y.md#install)\n';
  const crlf = lf.replace(/\n/g, '\r\n');
  assert.equal(normalise(crlf), lf);
  const read = memoryTree({ 'a.md': crlf }).read('a.md');
  assert.deepEqual(headings(read), headings(lf));
  assert.deepEqual(links(read), links(lf));
});

test('repoTree: read returns LF for a file saved with CRLF', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'host-docs-'));
  try {
    fs.writeFileSync(path.join(dir, 'a.md'), '# T\r\n\r\n## Install\r\n');
    assert.equal(repoTree(dir).read('a.md'), '# T\n\n## Install\n');
    assert.equal(repoTree(dir).read('missing.md'), null);
    assert.deepEqual(repoTree(dir).list('.'), ['a.md']);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('repoPaths: trailing punctuation and fragments are dropped', () => {
  assert.deepEqual(
    repoPaths(`(${RAW}docs/hosts/pi.md). See ${RAW}docs/first-run.md#x, and \`${RAW}a.md\` and ${BLOB}b.md`),
    ['docs/hosts/pi.md', 'docs/first-run.md', 'a.md', 'b.md']);
});

test('command patterns: whole words only', () => {
  for (const s of ['pi installs total-recall as a pi package', 'the plugin installed']) {
    assert.ok(!NPM_RUNNING.test(s) && !INSTALL_COMMAND.test(s), s);
  }
  for (const s of ['pi install npm:x', 'npm install -g x']) assert.ok(NPM_RUNNING.test(s) && INSTALL_COMMAND.test(s), s);
  for (const s of ['claude plugin install x', 'codex plugin add x']) assert.ok(!NPM_RUNNING.test(s) && INSTALL_COMMAND.test(s), s);
});

test('stale versions: a harness version is not the forbidden 0.8.x', () => {
  const page = (body) => memoryTree({ 'docs/hosts/pi.md': body });
  const mine = (t) => checkNoStaleFacts(t).filter((p) => p.includes('pi.md'));
  assert.deepEqual(mine(page('Observed on 10.8.2 and Claude Code 2.1.301 and 1.0.98-2.')), []);
  assert.notDeepEqual(mine(page('Expected: total-recall 0.8.0')), []);
  assert.notDeepEqual(mine(page('stable is 0.7.2')), []);
  assert.notDeepEqual(mine(page('0.8.x is the line')), []);
});

// ---------------------------------------------------------------------------------------------
// A valid in-memory tree, for the defect tests
// ---------------------------------------------------------------------------------------------

function validPage(h) {
  const tools = TOOLS.join(', ');
  return [
    `# ${h.name}`,
    '',
    '## Install',
    '',
    `**Ask first.** This install may change ${tools}; it switches Hermes's memory provider and adds a Gemini CLI hook that runs on every prompt. See ${RAW}docs/npm-postinstall.md.`,
    '',
    '```sh',
    'npm install -g @strvmarv/total-recall',
    '```',
    '',
    '## User steps',
    '',
    'None.',
    '',
    '## Verify',
    '',
    'Run it.',
    '',
    '## What you get',
    '',
    'Memory.',
    '',
    '## Known gaps',
    '',
    'None known.',
    '',
    '## Evidence',
    '',
    'Observed on 10.8.2.',
    '',
  ].join('\n');
}

const STATUS = 'total-recall loaded — 2 pinned, 3 hot, 12 warm, 5 cold, 2 KB collections. Storage: cortex. Last session: 2 hours ago.';

function validFiles() {
  const files = {
    'INSTALL.md': [
      '# Install',
      '',
      `Example: ${STATUS}`,
      '',
      '| Harness | Page |',
      '|---|---|',
      ...HARNESSES.map((h) => `| ${h.name} | ${RAW}docs/hosts/${h.id}.md |`),
      '',
    ].join('\n'),
    'README.md': '# R\n\nSee [pi](docs/hosts/pi.md#install) and [first run](docs/first-run.md).\n',
    'docs/npm-postinstall.md': `# Postinstall\n\n${TOOLS.join(', ')}\n`,
    'docs/database-location.md': '# Relocating the database\n',
    'docs/first-run.md': '# First run\n',
    '.opencode/INSTALL.md': `# OpenCode\n\nSee ${BLOB}docs/hosts/opencode.md\n`,
    'skills/commands/SKILL.md': `# Commands\n\n     > ${STATUS}\n`,
    'scripts/postinstall.js': Object.keys(MODULE_TOOLS).map((m) => `import { x } from './${m}.js';`).join('\n') + '\n',
    'codex/hook.js': "const a = '(INSTALL.md, \"Codex CLI\")';\n",
    'copilot/hook.js': "const a = '(INSTALL.md, \"Copilot CLI\")';\n",
    'cursor/hook.js': "const a = '(INSTALL.md, \"Cursor\")';\n",
  };
  for (const h of HARNESSES) files[`docs/hosts/${h.id}.md`] = validPage(h);
  return files;
}

const CHECKS = { checkPages, checkNoStaleFacts, checkLinks, checkStatusLine, checkDisclosure, checkHookNotes, checkOpenCodePointer };

test('the valid in-memory tree passes every check', () => {
  const tree = memoryTree(validFiles());
  for (const [name, check] of Object.entries(CHECKS)) assert.deepEqual(check(tree), [], name);
});

/** A defect test: change the valid tree, expect the check to report a problem that matches `re`. */
function defect(name, check, change, re) {
  test(`defect: ${name}`, () => {
    const files = validFiles();
    change(files);
    const problems = check(memoryTree(files));
    assert.ok(problems.some((p) => re.test(p)), `expected a problem matching ${re}, got ${JSON.stringify(problems)}`);
  });
}

test('a page saved with CRLF passes checkPages and checkDisclosure as the LF page does', () => {
  const files = validFiles();
  for (const h of HARNESSES) files[`docs/hosts/${h.id}.md`] = files[`docs/hosts/${h.id}.md`].replace(/\n/g, '\r\n');
  files['INSTALL.md'] = files['INSTALL.md'].replace(/\n/g, '\r\n');
  const tree = memoryTree(files);
  assert.deepEqual(checkPages(tree), []);
  assert.deepEqual(checkDisclosure(tree), []);
});

// ---------------------------------------------------------------------------------------------
// Rule 1: the pages and the table
// ---------------------------------------------------------------------------------------------

test('rule 1 holds on the repository', () => assert.deepEqual(checkPages(repoTree()), []));
defect('a harness page is missing', checkPages, (f) => { delete f['docs/hosts/zed.md']; }, /zed\.md is missing/);
defect('Verify stands before User steps', checkPages, (f) => {
  f['docs/hosts/pi.md'] = f['docs/hosts/pi.md'].replace('## User steps', '## TMP').replace('## Verify', '## User steps').replace('## TMP', '## Verify');
}, /pi\.md: the "##" headings/);
defect('an extra page', checkPages, (f) => { f['docs/hosts/extra.md'] = '# x\n'; }, /extra\.md is not a harness page/);
defect('the table puts pi first', checkPages, (f) => {
  const [a, b] = [`| Claude Code | ${RAW}docs/hosts/claude-code.md |`, `| pi | ${RAW}docs/hosts/pi.md |`];
  f['INSTALL.md'] = f['INSTALL.md'].replace(a, '@@').replace(b, a).replace('@@', b);
}, /harness table rows/);
defect('the table has thirteen rows', checkPages, (f) => {
  f['INSTALL.md'] = f['INSTALL.md'].replace(`| Hermes | ${RAW}docs/hosts/hermes.md |\n`, '');
}, /harness table rows/);

// ---------------------------------------------------------------------------------------------
// Rule 2: stale facts
// ---------------------------------------------------------------------------------------------

test('rule 2 holds on the repository', () => assert.deepEqual(checkNoStaleFacts(repoTree()), []));
defect('an install command in INSTALL.md', checkNoStaleFacts, (f) => { f['INSTALL.md'] += '\nRun npm install -g x\n'; }, /INSTALL\.md holds an install command/);
defect('a version number in INSTALL.md', checkNoStaleFacts, (f) => { f['INSTALL.md'] += '\ntotal-recall 4.7.0\n'; }, /INSTALL\.md holds a version number/);
defect('@beta on a page', checkNoStaleFacts, (f) => { f['docs/hosts/pi.md'] += '\nUse @beta\n'; }, /pi\.md mentions @beta/);
defect('0.8.0 in the packaged OpenCode file', checkNoStaleFacts, (f) => { f['.opencode/INSTALL.md'] += '\nSince 0.8.0\n'; }, /\.opencode\/INSTALL\.md holds the stale version 0\.8\.0/);

// ---------------------------------------------------------------------------------------------
// Rule 3: links
// ---------------------------------------------------------------------------------------------

test('rule 3 holds on the repository', () => assert.deepEqual(checkLinks(repoTree()), []));
defect('a link to a missing page', checkLinks, (f) => { f['README.md'] += '\n[x](docs/hosts/nope.md)\n'; }, /docs\/hosts\/nope\.md.*does not exist/);
defect('an anchor with no heading', checkLinks, (f) => { f['README.md'] += '\n[x](docs/first-run.md#no-such-heading)\n'; }, /no heading "#no-such-heading"/);
defect('a repository URL to a missing page', checkLinks, (f) => { f['INSTALL.md'] += `\n${RAW}docs/hosts/nope.md\n`; }, /docs\/hosts\/nope\.md.*does not exist/);
defect('a link from a page to a file that moved', checkLinks, (f) => { f['docs/hosts/pi.md'] += '\n[db](../database-location-old.md)\n'; }, /database-location-old\.md/);

test('rule 3: a broken link inside a code span or fence is not reported', () => {
  const files = validFiles();
  files['README.md'] += '\n`[x](docs/nope.md)`\n\n```\n[y](docs/nope2.md)\n```\n';
  assert.deepEqual(checkLinks(memoryTree(files)), []);
});

test('rule 3: a repeated heading gets a -1 anchor that resolves, and a -2 that does not', () => {
  const files = validFiles();
  files['docs/first-run.md'] = '# First run\n\n## Option\n\n## Option\n';
  files['README.md'] += '\n[a](docs/first-run.md#option-1)\n';
  assert.deepEqual(checkLinks(memoryTree(files)), []);
  files['README.md'] += '\n[b](docs/first-run.md#option-2)\n';
  assert.equal(checkLinks(memoryTree(files)).length, 1);
});

// ---------------------------------------------------------------------------------------------
// Rule 4: the status line
// ---------------------------------------------------------------------------------------------

test('rule 4 holds on the repository', () => assert.deepEqual(checkStatusLine(repoTree()), []));
defect('INSTALL.md without the status line', checkStatusLine, (f) => { f['INSTALL.md'] = '# Install\n'; }, /does not contain the status line/);
defect('the skill changes its example', checkStatusLine, (f) => { f['skills/commands/SKILL.md'] = '> total-recall loaded — 9 pinned.\n'; }, /does not contain the status line/);

// ---------------------------------------------------------------------------------------------
// Rules 5 and 6: disclosure
// ---------------------------------------------------------------------------------------------

test('rules 5 and 6 hold on the repository', () => assert.deepEqual(checkDisclosure(repoTree()), []));
defect('the paragraph omits Zed', checkDisclosure, (f) => {
  f['docs/hosts/pi.md'] = f['docs/hosts/pi.md'].replace('Zed, ', '');
}, /pi\.md: the "Ask first" paragraph does not name Zed/);
defect('the paragraph stands below the command', checkDisclosure, (f) => {
  const t = f['docs/hosts/pi.md'];
  const para = t.split('\n').find((l) => l.startsWith('**Ask first.**'));
  f['docs/hosts/pi.md'] = t.replace(para + '\n', '') + '\n' + para + '\n';
}, /pi\.md: no "\*\*Ask first\.\*\*" paragraph above/);
defect('a command only in prose, with no paragraph', checkDisclosure, (f) => {
  f['docs/hosts/zed.md'] = '# Zed\n\n## Install\n\nRun npm install -g x first.\n\n## User steps\n\n## Verify\n\n## What you get\n\n## Known gaps\n\n## Evidence\n';
}, /zed\.md: no "\*\*Ask first\.\*\*" paragraph above the first "npm install"/);
defect('the paragraph does not link the reference', checkDisclosure, (f) => {
  f['docs/hosts/pi.md'] = f['docs/hosts/pi.md'].replace(`See ${RAW}docs/npm-postinstall.md.`, '');
}, /pi\.md: the "Ask first" paragraph does not link/);
defect('postinstall.js imports a module MODULE_TOOLS does not list', checkDisclosure, (f) => {
  f['scripts/postinstall.js'] += "import { y } from './install-newtool.js';\n";
}, /install-newtool/);
defect('npm-postinstall.md does not name a tool', checkDisclosure, (f) => {
  f['docs/npm-postinstall.md'] = f['docs/npm-postinstall.md'].replace('Windsurf', '');
}, /npm-postinstall\.md does not name Windsurf/);

// ---------------------------------------------------------------------------------------------
// Rule 7: the names the hook scripts cite
// ---------------------------------------------------------------------------------------------

test('rule 7 holds on the repository', () => assert.deepEqual(checkHookNotes(repoTree()), []));
defect('a hook note cites a name that is not a row', checkHookNotes, (f) => {
  f['codex/hook.js'] = "const a = '(INSTALL.md, \"Codex\")';\n";
}, /codex\/hook\.js cites INSTALL\.md, "Codex"/);

// ---------------------------------------------------------------------------------------------
// Rule 8: the packaged OpenCode pointer
// ---------------------------------------------------------------------------------------------

test('rule 8 holds on the repository', () => assert.deepEqual(checkOpenCodePointer(repoTree()), []));
defect('the OpenCode file holds a fenced block', checkOpenCodePointer, (f) => {
  f['.opencode/INSTALL.md'] += '\n```sh\ntotal-recall\n```\n';
}, /holds a fenced block/);
defect('the OpenCode file does not link the page', checkOpenCodePointer, (f) => {
  f['.opencode/INSTALL.md'] = '# OpenCode\n\nSee the docs.\n';
}, /does not link docs\/hosts\/opencode\.md/);
