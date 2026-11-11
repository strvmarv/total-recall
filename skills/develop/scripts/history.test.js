import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import { readPages } from './pages.js';
import { bodyHash } from './regions.js';
import { findRevision, SUBCOMMANDS } from './history.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HANDOFF = path.join(HERE, '..', 'handoff.md');
const HISTORY = path.join(HERE, 'history.js');
const FIXTURES = path.join(HERE, 'fixtures');
const QUERIES = path.join(HERE, '..', 'queries');

const CASES = readGolden(HANDOFF, 'history-cases');

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'history-test-'));
}

function run(args) {
  const result = spawnSync(process.execPath, [HISTORY, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

test('history-cases golden covers the required shapes', () => {
  assert.ok(CASES.length >= 20);
  assert.ok(CASES.some((c) => c.expect.error));
  assert.ok(CASES.some((c) => c.expect.found === false));
  assert.ok(CASES.some((c) => c.expect.source === 'current'));
  assert.ok(CASES.some((c) => c.expect.source === 'revision'));
});

for (const c of CASES) {
  test(`findRevision: ${c.name}`, () => {
    const pinnedBodyHash = Object.hasOwn(c, 'pinnedBodyHash') ? c.pinnedBodyHash : bodyHash(c.pinned.title, c.pinned.body);
    const call = () => findRevision(c.editsPages, c.renamePages, c.current, pinnedBodyHash);
    if (c.expect.error) {
      assert.throws(call);
    } else {
      assert.deepEqual(call(), c.expect);
    }
  });
}

test('findRevision: the result never depends on rename order within a page (sorted first)', () => {
  const ren = (createdAt, previousTitle, currentTitle) => ({ createdAt, previousTitle, currentTitle });
  const page = (nodes) => ({ data: { repository: { issue: { timelineItems: { pageInfo: { hasNextPage: false, endCursor: null }, nodes } } } } });
  const edits = [{ data: { repository: { issue: { userContentEdits: { pageInfo: { hasNextPage: false, endCursor: null }, nodes: [
    { editedAt: '2026-03-09T00:00:00Z', deletedAt: null, diff: 'new', editor: { login: 'a' } },
    { editedAt: '2026-03-01T00:00:00Z', deletedAt: null, diff: 'old', editor: { login: 'a' } },
  ] } } } } }];
  const a = [ren('2026-03-03T00:00:00Z', 'A', 'B'), ren('2026-03-06T00:00:00Z', 'B', 'C')];
  const h = bodyHash('A', 'old');
  const forward = findRevision(edits, [page(a)], { title: 'C', body: 'new' }, h);
  const reversed = findRevision(edits, [page([...a].reverse())], { title: 'C', body: 'new' }, h);
  assert.deepEqual(forward, reversed);
  assert.equal(forward.found, true);
});

test('findRevision: a rename node without text fields (an unmatched fragment) is skipped', () => {
  const page = { data: { repository: { issue: { timelineItems: { pageInfo: { hasNextPage: false, endCursor: null }, nodes: [{}] } } } } };
  const edits = [{ data: { repository: { issue: { userContentEdits: null } } } }];
  const r = findRevision(edits, [page], { title: 'T', body: 'b' }, bodyHash('T', 'b'));
  assert.equal(r.found, true);
});

test('findRevision: pages must be arrays', () => {
  assert.throws(() => findRevision(null, [], { title: 'T', body: 'b' }, bodyHash('T', 'b')));
  assert.throws(() => findRevision([], null, { title: 'T', body: 'b' }, bodyHash('T', 'b')));
});

// ---- the two query files ----

function readQuery(name) {
  return fs.readFileSync(path.join(QUERIES, `${name}.graphql`), 'utf8');
}

for (const [name, fields] of [
  ['edits', ['editedAt', 'diff', 'deletedAt', 'userContentEdits']],
  ['renames', ['createdAt', 'previousTitle', 'currentTitle', 'timelineItems', 'RenamedTitleEvent']],
]) {
  test(`${name}.graphql declares its variables and selects every field its reader uses`, () => {
    const text = readQuery(name);
    for (const decl of ['$owner: String!', '$name: String!', '$number: Int!', '$endCursor: String']) {
      assert.ok(text.includes(decl), `${name}.graphql declares ${decl}`);
    }
    for (const v of ['$owner', '$name', '$number', '$endCursor']) {
      assert.ok(text.includes(v), `${name}.graphql uses ${v}`);
    }
    for (const f of [...fields, 'pageInfo', 'hasNextPage', 'endCursor', 'nodes']) {
      assert.match(text, new RegExp(`\\b${f}\\b`), `${name}.graphql selects ${f}`);
    }
    assert.match(text, /after:\s*\$endCursor/);
  });
}

test('renames.graphql restricts the timeline to RENAMED_TITLE_EVENT', () => {
  assert.match(readQuery('renames'), /itemTypes:\s*\[RENAMED_TITLE_EVENT\]/);
});

test('each query holds exactly one paginated connection (one pageInfo)', () => {
  for (const name of ['comments', 'edits', 'renames']) {
    const n = (readQuery(name).match(/pageInfo/g) || []).length;
    assert.equal(n, 1, `${name}.graphql has one pageInfo`);
  }
});

// ---- SUBCOMMANDS dispatcher ----

test('SUBCOMMANDS dispatcher: every listed name dispatches, an unlisted word is rejected', () => {
  assert.deepEqual(SUBCOMMANDS, ['find']);
  for (const name of SUBCOMMANDS) {
    const { parsed } = run([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
  }
  const bad = run(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
});

// ---- CLI find ----

const PINNED_TITLE = 'Fix login \u2014 caf\u00e9';
const PINNED_BODY = 'Cr\u00e8me br\u00fbl\u00e9e \u65e5\u672c\u8a9e \u2713\r\nsecond line with "quotes" and } braces\\';

function findArgs(dir, issue, hash) {
  const issueFile = path.join(dir, 'issue.json');
  fs.writeFileSync(issueFile, JSON.stringify(issue));
  const outDir = path.join(dir, 'out');
  return { args: ['find', path.join(FIXTURES, 'edits-pages.json'), path.join(FIXTURES, 'renames-pages.json'), issueFile, hash, outDir], outDir };
}

test('fixtures: edits-pages.json and renames-pages.json are two-page and one-page raw outputs', () => {
  const edits = readPages(fs.readFileSync(path.join(FIXTURES, 'edits-pages.json'), 'utf8'));
  const renames = readPages(fs.readFileSync(path.join(FIXTURES, 'renames-pages.json'), 'utf8'));
  assert.equal(edits.length, 2);
  assert.equal(renames.length, 1);
});

test('CLI find: writes old.title and old.body byte-exact for non-ASCII text, no BOM', () => {
  const dir = tmpDir();
  const { args, outDir } = findArgs(dir, { title: 'Fix login (renamed)', body: 'Current body' }, bodyHash(PINNED_TITLE, PINNED_BODY));
  const { parsed, status } = run(args);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { found: true, source: 'revision', editedAt: '2026-03-02T10:00:00Z' });
  const title = fs.readFileSync(path.join(outDir, 'old.title'));
  const body = fs.readFileSync(path.join(outDir, 'old.body'));
  assert.deepEqual(title, Buffer.from(PINNED_TITLE, 'utf8'));
  assert.deepEqual(body, Buffer.from(PINNED_BODY, 'utf8'));
  assert.notEqual(title[0], 0xef);
  assert.notEqual(body[0], 0xef);
});

test('CLI find: the current text matching prints source current with editedAt null', () => {
  const dir = tmpDir();
  const { args, outDir } = findArgs(dir, { title: 'Fix login (renamed)', body: 'Current body' }, bodyHash('Fix login (renamed)', 'Current body'));
  const { parsed } = run(args);
  assert.deepEqual(parsed, { found: true, source: 'current', editedAt: null });
  assert.equal(fs.readFileSync(path.join(outDir, 'old.body'), 'utf8'), 'Current body');
});

test('CLI find: a null issue body is the empty string', () => {
  const dir = tmpDir();
  const { args, outDir } = findArgs(dir, { title: 'Fix login (renamed)', body: null }, bodyHash('Fix login (renamed)', ''));
  const { parsed } = run(args);
  assert.deepEqual(parsed, { found: true, source: 'current', editedAt: null });
  assert.equal(fs.readFileSync(path.join(outDir, 'old.body'), 'utf8'), '');
});

test('CLI find: no match prints {found:false} and writes nothing', () => {
  const dir = tmpDir();
  const { args, outDir } = findArgs(dir, { title: 'Fix login (renamed)', body: 'Current body' }, bodyHash('Never', 'existed'));
  const { parsed, status } = run(args);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { found: false });
  assert.equal(fs.existsSync(path.join(outDir, 'old.body')), false);
});

test('CLI find: a pinned hash that is not 64 hex is {found:false}', () => {
  const dir = tmpDir();
  const { args } = findArgs(dir, { title: 'T', body: 'b' }, 'nothex');
  const { parsed, status } = run(args);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { found: false });
});

test('CLI find: a failed parse prints {ok:false,error} and exits 1', () => {
  const dir = tmpDir();
  const bad = path.join(dir, 'bad.json');
  fs.writeFileSync(bad, '{"data":{"repository":{"issue":null}}}');
  const issueFile = path.join(dir, 'issue.json');
  fs.writeFileSync(issueFile, JSON.stringify({ title: 'T', body: 'b' }));
  const { parsed, status } = run(['find', bad, path.join(FIXTURES, 'renames-pages.json'), issueFile, bodyHash('T', 'b'), path.join(dir, 'out')]);
  assert.equal(status, 1);
  assert.equal(parsed.ok, false);
  assert.equal(typeof parsed.error, 'string');
});

test('CLI find: an issue file that is an error response is {ok:false}', () => {
  const dir = tmpDir();
  const issueFile = path.join(dir, 'issue.json');
  fs.writeFileSync(issueFile, JSON.stringify({ message: 'Not Found' }));
  const { parsed, status } = run(['find', path.join(FIXTURES, 'edits-pages.json'), path.join(FIXTURES, 'renames-pages.json'), issueFile, bodyHash('T', 'b'), path.join(dir, 'out')]);
  assert.equal(status, 1);
  assert.equal(parsed.ok, false);
});

test('CLI find: a missing argument is {ok:false}', () => {
  const { parsed, status } = run(['find', 'a', 'b']);
  assert.equal(status, 1);
  assert.equal(parsed.ok, false);
});
