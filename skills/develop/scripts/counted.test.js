import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden, goldenNames } from './golden.js';
import { isLogin as checkpointsIsLogin } from './checkpoints.js';
import {
  SUBCOMMANDS,
  isLogin,
  isWriteAccess,
  isWriteAccessResult,
  flattenComments,
  counts,
  countedCheckpoints,
  governingPin,
  latestProgress,
  selectState,
} from './counted.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HANDOFF = path.join(HERE, '..', 'handoff.md');
const COUNTED = path.join(HERE, 'counted.js');
const FIXTURES = path.join(HERE, 'fixtures');
const SKILL_DIR = path.join(HERE, '..');

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'counted-test-'));
}

function run(args) {
  const result = spawnSync(process.execPath, [COUNTED, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

/** A set-backed hasWrite for a list of logins. */
function writersOf(list) {
  const set = new Set(list);
  return (login) => set.has(login);
}

/** Raw `gh api graphql --paginate` output for a list of page objects, one per line. */
function rawOf(pages) {
  return `${pages.map((p) => JSON.stringify(p)).join('\n')}\n`;
}

/** A GraphQL comments page for flat comment objects (the shape the goldens use). */
function pageOf(comments, hasNext = false) {
  return {
    data: {
      repository: {
        issue: {
          comments: {
            pageInfo: { hasNextPage: hasNext, endCursor: null },
            nodes: comments.map((c) => ({
              id: c.id,
              createdAt: c.createdAt,
              isMinimized: c.isMinimized,
              lastEditedAt: c.lastEditedAt,
              author: c.authorLogin === null ? null : { __typename: c.authorType, login: c.authorLogin },
              editor: c.editorLogin === null ? null : { __typename: c.editorType, login: c.editorLogin },
              body: c.body,
            })),
          },
        },
      },
    },
  };
}

// ---- goldens exist ----

test('the Trust goldens are all present in handoff.md', () => {
  const names = goldenNames(HANDOFF);
  for (const n of ['perm-cases', 'login-cases', 'comment-count-cases', 'pin-cases', 'flatten-cases',
    'flatten-fail-cases', 'fetch-commands', 'pages-cases', 'history-cases', 'write-access-cases', 'select-cases']) {
    assert.ok(names.includes(n), n);
  }
});

// ---- isWriteAccess / isLogin ----

for (const c of readGolden(HANDOFF, 'perm-cases')) {
  test(`isWriteAccess: ${c.name}`, () => {
    assert.equal(isWriteAccess(c.perm), c.expect);
  });
}

test('isWriteAccess: non-object input is false', () => {
  assert.equal(isWriteAccess(undefined), false);
  assert.equal(isWriteAccess('write'), false);
  assert.equal(isWriteAccess(['write']), false);
});

test('isLogin is the checkpoints.js definition, re-exported', () => {
  assert.equal(isLogin, checkpointsIsLogin);
});

for (const c of readGolden(HANDOFF, 'login-cases')) {
  test(`isLogin: ${JSON.stringify(c.login)} is ${c.expect ? 'accepted' : 'rejected'}`, () => {
    assert.equal(isLogin(c.login), c.expect);
  });
}

// ---- counts ----

for (const c of readGolden(HANDOFF, 'comment-count-cases')) {
  test(`counts: ${c.name}`, () => {
    assert.equal(counts(c.comment, writersOf(c.writers)), c.expect);
  });
}

// ---- pin selection ----

for (const c of readGolden(HANDOFF, 'pin-cases')) {
  test(`pin selection: ${c.name}`, () => {
    const cps = countedCheckpoints(c.comments, writersOf(c.writers));
    const pin = governingPin(cps);
    const progress = latestProgress(cps);
    assert.equal(pin ? pin.id : null, c.expectPin);
    assert.equal(progress ? progress.id : null, c.expectProgress);
  });
}

test('countedCheckpoints: carries id, createdAt, phase and fields in posted order', () => {
  const [c] = readGolden(HANDOFF, 'pin-cases').filter((x) => x.name.startsWith('a lone kickoff'));
  const cps = countedCheckpoints(c.comments, writersOf(c.writers));
  assert.equal(cps.length, 1);
  assert.deepEqual(Object.keys(cps[0]).sort(), ['createdAt', 'fields', 'id', 'phase']);
  assert.equal(cps[0].phase, 'kickoff');
  assert.deepEqual(cps[0].fields, { branch: 'feat/x', track: 'small' });
});

test('governingPin and latestProgress return null on an empty list', () => {
  assert.equal(governingPin([]), null);
  assert.equal(latestProgress([]), null);
});

// ---- flattenComments ----

for (const c of readGolden(HANDOFF, 'flatten-cases')) {
  test(`flattenComments: ${c.name}`, () => {
    const raw = fs.readFileSync(path.join(FIXTURES, c.fixture), 'utf8');
    assert.deepEqual(flattenComments(raw), c.expect);
  });
}

for (const c of readGolden(HANDOFF, 'flatten-fail-cases')) {
  test(`flattenComments fails closed: ${c.name}`, () => {
    assert.throws(() => flattenComments(c.raw));
  });
}

test('flattenComments: pages concatenate in order and an empty last page is fine', () => {
  const [one] = readGolden(HANDOFF, 'flatten-cases')[0].expect;
  const raw = rawOf([pageOf([one], true), pageOf([], false)]);
  assert.deepEqual(flattenComments(raw), [one]);
});

// ---- comments.graphql ----

test('comments.graphql declares its variables and selects every field flattenComments reads', () => {
  const text = fs.readFileSync(path.join(SKILL_DIR, 'queries', 'comments.graphql'), 'utf8');
  for (const decl of ['$owner: String!', '$name: String!', '$number: Int!', '$endCursor: String']) {
    assert.ok(text.includes(decl), decl);
  }
  for (const v of ['$owner', '$name', '$number', '$endCursor']) assert.ok(text.includes(v), v);
  for (const f of ['id', 'createdAt', 'isMinimized', 'lastEditedAt', 'author', 'editor', '__typename', 'login',
    'body', 'pageInfo', 'hasNextPage', 'endCursor', 'nodes', 'comments']) {
    assert.match(text, new RegExp(`(^|[^A-Za-z_])${f}([^A-Za-z_]|$)`), `selects ${f}`);
  }
  assert.match(text, /comments\(first:\s*100,\s*after:\s*\$endCursor\)/);
});

// ---- fetch-commands ----

const FETCH = readGolden(HANDOFF, 'fetch-commands');

test('fetch-commands: the expected names, each starting with api', () => {
  assert.deepEqual(FETCH.map((f) => f.name), [
    'issue', 'pr-create', 'pr-edit', 'permission', 'comment-body', 'comment-post', 'comment-edit',
    'comments', 'edits', 'renames',
  ]);
  for (const f of FETCH) assert.equal(f.args[0], 'api', f.name);
});

test('fetch-commands: --paginate is present for exactly comments, edits and renames', () => {
  for (const f of FETCH) {
    const expected = ['comments', 'edits', 'renames'].includes(f.name);
    assert.equal(f.args.includes('--paginate'), expected, f.name);
  }
});

test('fetch-commands: every query=@ path ends with an existing file under skills/develop/queries/', () => {
  let seen = 0;
  for (const f of FETCH) {
    for (const a of f.args) {
      if (!a.startsWith('query=@')) continue;
      seen += 1;
      const rel = a.slice('query=@<skill>/'.length);
      assert.ok(a.startsWith('query=@<skill>/queries/'), a);
      assert.ok(rel.endsWith(`${f.name}.graphql`), a);
      assert.ok(fs.existsSync(path.join(SKILL_DIR, rel)), a);
    }
  }
  assert.equal(seen, 3);
});

test('fetch-commands: number is typed (-F), owner and name are strings (-f)', () => {
  for (const name of ['comments', 'edits', 'renames']) {
    const { args } = FETCH.find((f) => f.name === name);
    assert.equal(args[args.indexOf('number=<n>') - 1], '-F', name);
    assert.equal(args[args.indexOf('owner=<owner>') - 1], '-f', name);
    assert.equal(args[args.indexOf('name=<repo>') - 1], '-f', name);
  }
});

test('fetch-commands: the one --jq filter is .body and is free of quotes', () => {
  const filters = [];
  for (const f of FETCH) {
    f.args.forEach((a, i) => { if (a === '--jq') filters.push(f.args[i + 1]); });
  }
  assert.deepEqual(filters, ['.body']);
  assert.doesNotMatch(filters[0], /["'`]/);
});

test('fetch-commands: text goes through files, titles and bodies never as plain -f values', () => {
  for (const name of ['pr-create', 'pr-edit']) {
    const { args } = FETCH.find((f) => f.name === name);
    assert.ok(args.includes('title=@<titleFile>'));
    assert.ok(args.includes('body=@<bodyFile>'));
    assert.equal(args[args.indexOf('title=@<titleFile>') - 1], '-F');
    assert.equal(args[args.indexOf('body=@<bodyFile>') - 1], '-F');
  }
  for (const name of ['comment-post', 'comment-edit']) {
    const { args } = FETCH.find((f) => f.name === name);
    assert.equal(args[args.indexOf('body=@<commentFile>') - 1], '-F');
  }
  assert.deepEqual(FETCH.find((f) => f.name === 'pr-create').args.filter((a) => a.startsWith('head=') || a.startsWith('base=')), ['head=<branch>', 'base=<base>']);
  assert.deepEqual(FETCH.find((f) => f.name === 'permission').args, ['api', 'repos/<owner>/<repo>/collaborators/<login>/permission']);
});

// ---- isWriteAccessResult ----

const WRITE_ACCESS_CASES = readGolden(HANDOFF, 'write-access-cases');

for (const c of WRITE_ACCESS_CASES.filter((x) => x.kind === 'result')) {
  test(`isWriteAccessResult: ${c.name}`, () => {
    const call = () => isWriteAccessResult(c.text, c.httpStatus);
    if (c.expect.error) assert.throws(call, { message: c.expect.error });
    else assert.equal(call(), c.expect.writeAccess);
  });
}

// ---- CLI write-access ----

function writeAccessCli(text, httpStatus) {
  const dir = tmpDir();
  const file = path.join(dir, 'perm.json');
  if (text !== null) fs.writeFileSync(file, text);
  const args = ['write-access', file];
  if (httpStatus !== null) args.push(String(httpStatus));
  return run(args);
}

for (const c of WRITE_ACCESS_CASES.filter((x) => x.kind === 'result')) {
  test(`CLI write-access: ${c.name}`, () => {
    const { parsed, status } = writeAccessCli(c.text, c.httpStatus);
    if (c.expect.error) {
      assert.deepEqual(parsed, { ok: false, error: c.expect.error });
      assert.equal(status, 1);
    } else {
      assert.deepEqual(parsed, { writeAccess: c.expect.writeAccess });
      assert.equal(status, 0);
    }
  });
}

test('CLI write-access: a status that is not three digits is {ok:false}', () => {
  const { parsed, status } = writeAccessCli(null, 'abc');
  assert.equal(parsed.ok, false);
  assert.equal(status, 1);
});

// ---- CLI permissions ----

function runPermissions(entries, existing, dir = tmpDir()) {
  const manifest = entries.map((e, i) => {
    const file = path.join(dir, `perm-${i}.json`);
    if (e.text !== null) fs.writeFileSync(file, e.text);
    return { login: e.login, file, httpStatus: e.httpStatus };
  });
  const manifestFile = path.join(dir, 'manifest.json');
  fs.writeFileSync(manifestFile, JSON.stringify(manifest));
  const outFile = path.join(dir, 'permissions.json');
  if (existing !== null) fs.writeFileSync(outFile, JSON.stringify(existing));
  const result = run(['permissions', manifestFile, outFile]);
  return { ...result, outFile, dir };
}

for (const c of WRITE_ACCESS_CASES.filter((x) => x.kind === 'manifest')) {
  test(`CLI permissions: ${c.name}`, () => {
    const { parsed, status, outFile } = runPermissions(c.entries, c.existing);
    if (c.expectError) {
      assert.equal(parsed.ok, false);
      assert.equal(parsed.error, c.expectError);
      assert.equal(status, 1);
      // nothing is written on a failure: no file, or the existing file untouched
      if (c.existing === null) assert.equal(fs.existsSync(outFile), false);
      else assert.deepEqual(JSON.parse(fs.readFileSync(outFile, 'utf8')), c.existing);
    } else {
      assert.equal(status, 0);
      assert.equal(parsed.ok, true);
      const map = JSON.parse(fs.readFileSync(outFile, 'utf8'));
      assert.deepEqual(map, c.expectMap);
      assert.deepEqual(parsed.logins.sort(), Object.keys(c.expectMap).sort());
    }
  });
}

test('CLI permissions: a second run in a new scratch directory starts from {} even though an old directory has a map', () => {
  const first = runPermissions([{ login: 'a', text: JSON.stringify({ permission: 'write', role_name: 'write' }), httpStatus: null }], null);
  assert.deepEqual(JSON.parse(fs.readFileSync(first.outFile, 'utf8')), { a: { permission: 'write', role_name: 'write' } });
  const second = runPermissions([], null);
  assert.equal(second.status, 0);
  assert.deepEqual(JSON.parse(fs.readFileSync(second.outFile, 'utf8')), {});
});

test('CLI permissions: an existing map that is not an object is {ok:false}', () => {
  const dir = tmpDir();
  const outFile = path.join(dir, 'permissions.json');
  fs.writeFileSync(outFile, '[1]');
  const manifestFile = path.join(dir, 'manifest.json');
  fs.writeFileSync(manifestFile, '[]');
  const { parsed, status } = run(['permissions', manifestFile, outFile]);
  assert.equal(parsed.ok, false);
  assert.equal(status, 1);
});

test('CLI permissions: a manifest that is not an array is {ok:false}', () => {
  const dir = tmpDir();
  const manifestFile = path.join(dir, 'manifest.json');
  fs.writeFileSync(manifestFile, '{}');
  const { parsed, status } = run(['permissions', manifestFile, path.join(dir, 'out.json')]);
  assert.equal(parsed.ok, false);
  assert.equal(status, 1);
});

// ---- selectState and CLI select ----

const SELECT_CASES = readGolden(HANDOFF, 'select-cases');

for (const c of SELECT_CASES) {
  test(`selectState: ${c.name}`, () => {
    const raw = rawOf([pageOf(c.comments)]);
    assert.deepEqual(selectState(raw, c.permissions ?? {}), c.expect);
  });
}

function selectCli(comments, permissions, dir = tmpDir()) {
  const rawFile = path.join(dir, 'comments.raw.json');
  fs.writeFileSync(rawFile, rawOf([pageOf(comments)]));
  const permFile = path.join(dir, 'permissions.json');
  if (permissions !== null) fs.writeFileSync(permFile, JSON.stringify(permissions));
  return { ...run(['select', rawFile, permFile]), dir, permFile };
}

for (const c of SELECT_CASES) {
  test(`CLI select: ${c.name}`, () => {
    const { parsed, status } = selectCli(c.comments, c.permissions);
    assert.equal(status, 0);
    assert.deepEqual(parsed, c.expect);
  });
}

test('CLI select: the definer-then-kickoff sequence converges after one permissions pass', () => {
  const c = SELECT_CASES.find((x) => x.name.startsWith('a definer in the map and a kickoff'));
  const first = selectCli(c.comments, c.permissions);
  assert.equal(first.parsed.complete, false);
  assert.deepEqual(first.parsed.needPermissions, ['strvmarv']);

  // The caller fetches E's permission (here a write answer) and merges it into the same map.
  const dir = first.dir;
  const permFile = path.join(dir, 'perm-E.json');
  fs.writeFileSync(permFile, JSON.stringify({ permission: 'write', role_name: 'write', user: { login: 'strvmarv' } }));
  const manifestFile = path.join(dir, 'manifest.json');
  fs.writeFileSync(manifestFile, JSON.stringify([{ login: 'strvmarv', file: permFile, httpStatus: null }]));
  const merged = run(['permissions', manifestFile, first.permFile]);
  assert.equal(merged.status, 0);
  const map = JSON.parse(fs.readFileSync(first.permFile, 'utf8'));
  assert.deepEqual(Object.keys(map).sort(), ['jane-doe_acme', 'strvmarv']);

  const second = run(['select', path.join(dir, 'comments.raw.json'), first.permFile]);
  assert.equal(second.parsed.complete, true);
  assert.deepEqual(second.parsed.needPermissions, []);
  assert.equal(second.parsed.pin.phase, 'defined');
  assert.equal(second.parsed.progress.phase, 'kickoff');
});

test('CLI select: a 404 for E (no file, status 404) makes the map hold null and the pass complete', () => {
  const c = SELECT_CASES.find((x) => x.name.startsWith('a definer in the map and a kickoff'));
  const first = selectCli(c.comments, c.permissions);
  const dir = first.dir;
  const manifestFile = path.join(dir, 'manifest.json');
  fs.writeFileSync(manifestFile, JSON.stringify([{ login: 'strvmarv', file: path.join(dir, 'absent.json'), httpStatus: 404 }]));
  assert.equal(run(['permissions', manifestFile, first.permFile]).status, 0);
  const second = run(['select', path.join(dir, 'comments.raw.json'), first.permFile]);
  assert.equal(second.parsed.complete, true);
  assert.deepEqual(second.parsed.counted, ['IC_def']);
  assert.equal(second.parsed.progress, null);
});

test('CLI select: an invalid permissions file is {ok:false}', () => {
  const comments = readGolden(HANDOFF, 'select-cases')[0].comments;
  const dir = tmpDir();
  const rawFile = path.join(dir, 'comments.raw.json');
  fs.writeFileSync(rawFile, rawOf([pageOf(comments)]));
  const permFile = path.join(dir, 'permissions.json');
  fs.writeFileSync(permFile, '[1]');
  const { parsed, status } = run(['select', rawFile, permFile]);
  assert.equal(parsed.ok, false);
  assert.equal(status, 1);
  fs.writeFileSync(permFile, JSON.stringify({ a: 'write' }));
  const again = run(['select', rawFile, permFile]);
  assert.equal(again.parsed.ok, false);
});

test('CLI select: a failing comments read is {ok:false} (fails closed)', () => {
  const dir = tmpDir();
  const rawFile = path.join(dir, 'comments.raw.json');
  fs.writeFileSync(rawFile, JSON.stringify({ errors: [{ message: 'rate limited' }] }));
  const { parsed, status } = run(['select', rawFile, path.join(dir, 'permissions.json')]);
  assert.equal(parsed.ok, false);
  assert.equal(status, 1);
});

test('CLI select: reads the two-page fixture', () => {
  const dir = tmpDir();
  const { parsed, status } = run(['select', path.join(FIXTURES, 'comments-two-pages.json'), path.join(dir, 'absent.json')]);
  assert.equal(status, 0);
  // The only checkpoint-bearing comment is minimized but still names a login that is not in the map.
  assert.equal(parsed.complete, false);
  assert.deepEqual(parsed.needPermissions, ['strvmarv']);
  assert.deepEqual(parsed.counted, []);
});

// ---- SUBCOMMANDS dispatcher ----

test('SUBCOMMANDS dispatcher: every listed name dispatches, an unlisted word is rejected', () => {
  assert.deepEqual(SUBCOMMANDS, ['select', 'write-access', 'permissions']);
  for (const name of SUBCOMMANDS) {
    const { parsed } = run([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
  }
  const bad = run(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
});
