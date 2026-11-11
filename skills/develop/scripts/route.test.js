import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden, goldenNames } from './golden.js';
import {
  SUBCOMMANDS,
  countTasks,
  countCycles,
  tasksFor,
  isLive,
  position,
  route,
  hasMarkerLine,
  hasUncountedMarkers,
} from './route.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HANDOFF = path.join(HERE, '..', 'handoff.md');
const ROUTE = path.join(HERE, 'route.js');
const FIXTURES = path.join(HERE, 'fixtures');
const COMMENTS = path.join(FIXTURES, 'route-comments.json');
const NEWCOMER = path.join(FIXTURES, 'route-comments-newcomer.json');
const PERMISSIONS = path.join(FIXTURES, 'route-permissions.json');

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'route-test-'));
}

function run(args) {
  const result = spawnSync(process.execPath, [ROUTE, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

/** Writes `content` (a string, or a value to stringify) to a fresh file and returns its path. */
function put(dir, name, content) {
  const file = path.join(dir, name);
  fs.writeFileSync(file, typeof content === 'string' ? content : JSON.stringify(content));
  return file;
}

function writersOf(list) {
  const set = new Set(list);
  return (login) => set.has(login);
}

/** Raw `gh api graphql --paginate` output for the given flat comments, as one page. */
function rawOf(comments) {
  const nodes = comments.map((c) => ({
    id: c.id,
    createdAt: c.createdAt,
    isMinimized: c.isMinimized ?? false,
    lastEditedAt: null,
    author: { __typename: 'User', login: c.authorLogin },
    editor: null,
    body: c.body,
  }));
  const page = { data: { repository: { issue: { comments: { pageInfo: { hasNextPage: false, endCursor: null }, nodes } } } } };
  return `${JSON.stringify(page)}\n`;
}

function checkpointComment(id, phase, author, createdAt, extra = '') {
  return {
    id,
    createdAt,
    authorLogin: author,
    body: `Summary.\n\n\`\`\`\ntr-develop/v1 phase=${phase} branch=feat/x track=small${extra}\n\`\`\`\n`,
  };
}

// ---- goldens exist ----

test('the Resume and routing goldens are all present in handoff.md', () => {
  const names = goldenNames(HANDOFF);
  for (const n of ['position-cases', 'position-error-cases', 'task-count-cases', 'tasks-for-cases', 'live-cases', 'route-cases']) {
    assert.ok(names.includes(n), n);
  }
});

// ---- countTasks / countCycles ----

for (const c of readGolden(HANDOFF, 'task-count-cases')) {
  test(`${c.fn === 'cycles' ? 'countCycles' : 'countTasks'}: ${c.name}`, () => {
    const fn = c.fn === 'cycles' ? countCycles : countTasks;
    assert.deepEqual(fn(c.text), c.expect);
  });
}

// ---- tasksFor ----

for (const c of readGolden(HANDOFF, 'tasks-for-cases')) {
  test(`tasksFor: ${c.name}`, () => {
    assert.equal(tasksFor(c.planSha, c.tip, c.tasksDone), c.expect);
  });
}

// ---- isLive ----

for (const c of readGolden(HANDOFF, 'live-cases')) {
  test(`isLive: ${c.name}`, () => {
    assert.equal(isLive(c.checkpoints, c.now), c.expect);
  });
}

test('isLive: an unparseable now is a caller error, never a quiet false', () => {
  assert.throws(() => isLive([], 'soon'), /now/);
});

// ---- position ----

for (const c of readGolden(HANDOFF, 'position-cases')) {
  test(`position: ${c.name}`, () => {
    assert.equal(position(c.progress, c.counts), c.expect);
  });
}

for (const c of readGolden(HANDOFF, 'position-error-cases')) {
  test(`position fails closed: ${c.name}`, () => {
    assert.throws(() => position(c.progress, c.counts));
  });
}

// ---- route ----

for (const c of readGolden(HANDOFF, 'route-cases')) {
  test(`route: ${c.name}`, () => {
    const { writers, ...rest } = c.input;
    const got = route({ ...rest, hasWrite: writersOf(writers) });
    assert.deepEqual(got, c.expect);
  });
}

test('route: an issue state other than open or closed is an error', () => {
  assert.throws(() => route({
    comments: [], hasWrite: () => true, pinValid: true, issueState: 'OPEN', baseIsDefault: true, hasUncountedMarkers: false,
  }), /issueState/);
});

test('route: a pin check that is needed but not supplied is an error, never a guess', () => {
  const comments = [
    { ...checkpointComment('1', 'defined', 'alice', '2026-11-10T10:00:00Z'), authorType: 'User', editorLogin: null, editorType: null, lastEditedAt: null, isMinimized: false },
    { ...checkpointComment('2', 'tasks', 'alice', '2026-11-10T11:00:00Z'), authorType: 'User', editorLogin: null, editorType: null, lastEditedAt: null, isMinimized: false },
  ];
  assert.throws(() => route({
    comments, hasWrite: () => true, issueState: 'open', baseIsDefault: true, hasUncountedMarkers: false,
  }), /pinValid/);
});

// ---- marker lines ----

test('hasMarkerLine: a trimmed line starting with tr-develop/ in any position, fenced or not', () => {
  assert.equal(hasMarkerLine('x\n```\ntr-develop/v1 phase=kickoff\n```\n'), true);
  assert.equal(hasMarkerLine('x\r\n   tr-develop/v1 phase=tasks  \r\n'), true);
  assert.equal(hasMarkerLine('quoted: tr-develop/v1 phase=tasks inside a sentence'), false);
  assert.equal(hasMarkerLine('<!-- tr-develop:ac-begin -->'), false);
  assert.equal(hasMarkerLine(''), false);
});

test('hasUncountedMarkers: only comments that do not count, and the body', () => {
  const mk = (id, author, body, extra = {}) => ({
    id, createdAt: '2026-11-10T10:00:00Z', authorLogin: author, authorType: 'User', editorLogin: null, editorType: null, lastEditedAt: null, isMinimized: false, body, ...extra,
  });
  const marker = 'a\n\n```\ntr-develop/v1 phase=kickoff branch=feat/x\n```\n';
  const hw = writersOf(['alice']);
  assert.equal(hasUncountedMarkers([mk('1', 'alice', marker)], hw, null), false);
  assert.equal(hasUncountedMarkers([mk('1', 'mallory', marker)], hw, null), true);
  assert.equal(hasUncountedMarkers([mk('1', 'alice', marker, { isMinimized: true })], hw, null), true);
  assert.equal(hasUncountedMarkers([mk('1', 'mallory', 'no markers here')], hw, null), false);
  assert.equal(hasUncountedMarkers([], hw, 'body\ntr-develop/v1 phase=defined\n'), true);
  assert.equal(hasUncountedMarkers([], hw, 'body\n'), false);
});

// ---- CLI: dispatcher ----

test('SUBCOMMANDS lists the CLI names', () => {
  assert.deepEqual(SUBCOMMANDS, ['count-tasks', 'count-cycles', 'tasks-for', 'live', 'position', 'route']);
});

for (const name of SUBCOMMANDS) {
  test(`dispatcher: ${name} is dispatched (a missing argument is a usage error, not unknown)`, () => {
    const r = run([name]);
    assert.equal(r.status, 1);
    assert.equal(r.parsed.ok, false);
    assert.notEqual(r.parsed.error, 'unknown subcommand');
  });
}

test('dispatcher: an unlisted word is rejected', () => {
  for (const word of ['frobnicate', 'ROUTE', '', 'toString', '__proto__']) {
    const r = run([word]);
    assert.equal(r.status, 1, word);
    assert.deepEqual(r.parsed, { ok: false, error: 'unknown subcommand' }, word);
  }
  const none = run([]);
  assert.equal(none.status, 1);
  assert.deepEqual(none.parsed, { ok: false, error: 'unknown subcommand' });
});

// ---- CLI: count-tasks, count-cycles, tasks-for ----

test('CLI count-tasks and count-cycles read plain-text files', () => {
  const dir = tmpDir();
  const plan = put(dir, 'plan.md', '# P\r\n\r\n### Task 1: A\r\n\r\n```\r\n### Task 2: B\r\n```\r\n');
  const approach = put(dir, 'approach.md', 'Cycle 1: a\n- Cycle 2: b\n');
  assert.deepEqual(run(['count-tasks', plan]).parsed, { count: 1, sequential: true });
  assert.deepEqual(run(['count-cycles', approach]).parsed, { count: 2, sequential: true });
  const bad = put(dir, 'bad.md', '### Task 1: A\n### Task 3: C\n');
  assert.deepEqual(run(['count-tasks', bad]).parsed, { count: 2, sequential: false });
});

test('CLI count-tasks fails on a missing file', () => {
  const r = run(['count-tasks', path.join(tmpDir(), 'nope.md')]);
  assert.equal(r.status, 1);
  assert.equal(r.parsed.ok, false);
});

test('CLI tasks-for: the tip, "-" for no tip, and null', () => {
  const dir = tmpDir();
  const base = '### Task 1: A\n\n### Task 2: B\n';
  const planSha = put(dir, 'plan-sha.md', base);
  const tip = put(dir, 'tip.md', `${base}\n### Task 3: C\n`);
  const none = put(dir, 'none.md', '# nothing\n');
  assert.deepEqual(run(['tasks-for', planSha, tip, '2']).parsed, { tasks: 3 });
  assert.deepEqual(run(['tasks-for', planSha, '-', '2']).parsed, { tasks: 2 });
  assert.deepEqual(run(['tasks-for', none, '-', '0']).parsed, { tasks: null });
  const bad = run(['tasks-for', planSha, '-', 'two']);
  assert.equal(bad.status, 1);
  assert.equal(bad.parsed.ok, false);
});

// ---- CLI: position ----

test('CLI position takes {progress, counts}', () => {
  const dir = tmpDir();
  const f = put(dir, 'p.json', { progress: { phase: 'tasks', fields: { track: 'plan', planSha: 'b'.repeat(40), tasksDone: '1' } }, counts: { tasks: 3, cycles: null } });
  assert.deepEqual(run(['position', f]).parsed, { position: 'task:2' });
  const start = put(dir, 's.json', { progress: null, counts: { tasks: null, cycles: null } });
  assert.deepEqual(run(['position', start]).parsed, { position: 'start' });
  const bad = put(dir, 'b.json', { progress: { phase: 'tasks', fields: {} }, counts: { tasks: 1, cycles: 1 } });
  const r = run(['position', bad]);
  assert.equal(r.status, 1);
  assert.equal(r.parsed.ok, false);
});

test('CLI position accepts the progress object that counted.js select prints (extra keys)', () => {
  const dir = tmpDir();
  const f = put(dir, 'p.json', { progress: { id: 'IC_1', phase: 'kickoff', fields: { track: 'small' } }, counts: { tasks: null, cycles: 2 } });
  assert.deepEqual(run(['position', f]).parsed, { position: 'cycle:1' });
});

// ---- CLI: live ----

test('CLI live: createdAt survives from select to isLive', () => {
  const inside = run(['live', COMMENTS, PERMISSIONS, '2026-11-10T12:00:00Z']);
  assert.deepEqual(inside.parsed, { complete: true, needPermissions: [], live: true });
  const outside = run(['live', COMMENTS, PERMISSIONS, '2026-11-12T12:00:00Z']);
  assert.deepEqual(outside.parsed, { complete: true, needPermissions: [], live: false });
});

test('CLI live: an outsider checkpoint (bob, no write access) never makes it live', () => {
  const dir = tmpDir();
  const raw = put(dir, 'raw.json', rawOf([checkpointComment('IC_1', 'tasks', 'bob', '2026-11-10T11:59:00Z')]));
  const r = run(['live', raw, PERMISSIONS, '2026-11-10T12:00:00Z']);
  assert.deepEqual(r.parsed, { complete: true, needPermissions: [], live: false });
});

test('CLI live: an incomplete selection reports live:null and the logins to look up', () => {
  const r = run(['live', NEWCOMER, PERMISSIONS, '2026-11-10T12:00:00Z']);
  assert.equal(r.status, 0);
  assert.deepEqual(r.parsed, { complete: false, needPermissions: ['carol'], live: null });
});

test('CLI live: a missing permissions file is {} (everyone is unlooked)', () => {
  const r = run(['live', COMMENTS, path.join(tmpDir(), 'none.json'), '2026-11-10T12:00:00Z']);
  assert.equal(r.parsed.complete, false);
  assert.deepEqual(r.parsed.needPermissions, ['alice', 'bob']);
  assert.equal(r.parsed.live, null);
});

test('CLI live: an unparseable now fails', () => {
  const r = run(['live', COMMENTS, PERMISSIONS, 'soon']);
  assert.equal(r.status, 1);
  assert.equal(r.parsed.ok, false);
});

// ---- CLI: route ----

test('CLI route: counted progress and a valid pin deliver; the outsider pr-open is ignored and reported', () => {
  const dir = tmpDir();
  const state = put(dir, 'state.json', { pinValid: true, issueState: 'OPEN', baseIsDefault: true });
  const r = run(['route', COMMENTS, PERMISSIONS, state]);
  assert.equal(r.status, 0);
  assert.deepEqual(r.parsed, { complete: true, needPermissions: [], route: 'deliver', askClosed: false, ignoredMarkers: true });
});

test('CLI route: GraphQL CLOSED asks first', () => {
  const dir = tmpDir();
  const state = put(dir, 'state.json', { pinValid: true, issueState: 'CLOSED', baseIsDefault: true });
  const r = run(['route', COMMENTS, PERMISSIONS, state]);
  assert.equal(r.parsed.route, 'deliver');
  assert.equal(r.parsed.askClosed, true);
});

test('CLI route: a pin that failed validation with progress is repin-or-stop', () => {
  const dir = tmpDir();
  const state = put(dir, 'state.json', { pinValid: false, issueState: 'open', baseIsDefault: true });
  assert.equal(run(['route', COMMENTS, PERMISSIONS, state]).parsed.route, 'repin-or-stop');
});

test('CLI route: while complete is false route is null, even though select has a pin and progress', () => {
  const dir = tmpDir();
  const state = put(dir, 'state.json', { pinValid: true, issueState: 'open', baseIsDefault: true });
  const r = run(['route', NEWCOMER, PERMISSIONS, state]);
  assert.equal(r.status, 0);
  assert.deepEqual(r.parsed, { complete: false, needPermissions: ['carol'], route: null, askClosed: false, ignoredMarkers: false });
  // Once carol is looked up as a collaborator without write access, the same issue routes.
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' }, carol: null });
  const done = run(['route', NEWCOMER, perms, state]);
  assert.equal(done.parsed.complete, true);
  assert.equal(done.parsed.route, 'deliver');
});

test('CLI route: a login missing from the permissions file is incomplete, route null', () => {
  const dir = tmpDir();
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' } });
  const state = put(dir, 'state.json', { pinValid: true, issueState: 'open', baseIsDefault: true });
  const r = run(['route', COMMENTS, perms, state]);
  assert.equal(r.parsed.complete, false);
  assert.deepEqual(r.parsed.needPermissions, ['bob']);
  assert.equal(r.parsed.route, null);
});

test('CLI route: pr-open and closed route before pin validation, so pinValid may be omitted', () => {
  const dir = tmpDir();
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' } });
  const state = put(dir, 'state.json', { issueState: 'closed', baseIsDefault: true });
  const prOpen = put(dir, 'pr.json', rawOf([
    checkpointComment('IC_1', 'defined', 'alice', '2026-11-10T09:00:00Z'),
    checkpointComment('IC_2', 'pr-open', 'alice', '2026-11-10T10:00:00Z'),
  ]));
  assert.deepEqual(run(['route', prOpen, perms, state]).parsed, {
    complete: true, needPermissions: [], route: 'after-the-pr', askClosed: false, ignoredMarkers: false,
  });
  const closed = put(dir, 'closed.json', rawOf([
    checkpointComment('IC_1', 'defined', 'alice', '2026-11-10T09:00:00Z'),
    checkpointComment('IC_2', 'closed', 'alice', '2026-11-10T10:00:00Z'),
  ]));
  assert.equal(run(['route', closed, perms, state]).parsed.route, 'terminal');
  const open = put(dir, 'open.json', { issueState: 'open', baseIsDefault: true });
  assert.equal(run(['route', closed, perms, open]).parsed.route, 'new-issue-or-stop');
  const notDefault = put(dir, 'nd.json', { issueState: 'open', baseIsDefault: false });
  assert.equal(run(['route', closed, perms, notDefault]).parsed.route, 'terminal');
});

test('CLI route: pinValid is required once the pin matters', () => {
  const dir = tmpDir();
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' } });
  const state = put(dir, 'state.json', { issueState: 'open', baseIsDefault: true });
  const raw = put(dir, 'raw.json', rawOf([
    checkpointComment('IC_1', 'defined', 'alice', '2026-11-10T09:00:00Z'),
    checkpointComment('IC_2', 'tasks', 'alice', '2026-11-10T10:00:00Z'),
  ]));
  const r = run(['route', raw, perms, state]);
  assert.equal(r.status, 1);
  assert.equal(r.parsed.ok, false);
});

test('CLI route: a bad issueState or a missing baseIsDefault fails', () => {
  const dir = tmpDir();
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' } });
  const raw = put(dir, 'raw.json', rawOf([checkpointComment('IC_1', 'defined', 'alice', '2026-11-10T09:00:00Z')]));
  const badState = put(dir, 'bad.json', { pinValid: true, issueState: 'merged', baseIsDefault: true });
  assert.equal(run(['route', raw, perms, badState]).status, 1);
  const noBase = put(dir, 'nb.json', { pinValid: true, issueState: 'open' });
  assert.equal(run(['route', raw, perms, noBase]).status, 1);
});

test('CLI route: ignoredMarkers is computed from uncounted comments and from the body file', () => {
  const dir = tmpDir();
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' }, mallory: { role_name: 'read' } });
  const state = put(dir, 'state.json', { pinValid: true, issueState: 'open', baseIsDefault: true });
  const plain = put(dir, 'plain.json', rawOf([{ id: 'IC_1', createdAt: '2026-11-10T09:00:00Z', authorLogin: 'alice', body: 'just a note' }]));
  assert.deepEqual(run(['route', plain, perms, state]).parsed, {
    complete: true, needPermissions: [], route: 'define', askClosed: false, ignoredMarkers: false,
  });
  const body = put(dir, 'body.md', 'Ticket text\r\n\r\ntr-develop/v1 phase=defined track=small\r\n');
  assert.deepEqual(run(['route', plain, perms, state, body]).parsed, {
    complete: true, needPermissions: [], route: 'define', askClosed: false, ignoredMarkers: true,
  });
  const cleanBody = put(dir, 'clean.md', 'Ticket text\n');
  assert.equal(run(['route', plain, perms, state, cleanBody]).parsed.ignoredMarkers, false);
  const outsider = put(dir, 'outsider.json', rawOf([checkpointComment('IC_1', 'kickoff', 'mallory', '2026-11-10T09:00:00Z')]));
  assert.deepEqual(run(['route', outsider, perms, state]).parsed, {
    complete: true, needPermissions: [], route: 'define', askClosed: false, ignoredMarkers: true,
  });
});

test('CLI route: a counted comment with a malformed line is not an uncounted marker', () => {
  const dir = tmpDir();
  const perms = put(dir, 'perms.json', { alice: { role_name: 'write' } });
  const state = put(dir, 'state.json', { pinValid: true, issueState: 'open', baseIsDefault: true });
  const twice = checkpointComment('IC_1', 'kickoff', 'alice', '2026-11-10T09:00:00Z');
  twice.body += '\ntr-develop/v1 phase=kickoff branch=feat/x track=small\n';
  const raw = put(dir, 'raw.json', rawOf([twice]));
  const r = run(['route', raw, perms, state]);
  assert.equal(r.parsed.route, 'define');
  assert.equal(r.parsed.ignoredMarkers, false);
});
