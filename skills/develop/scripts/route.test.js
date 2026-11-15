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
  parseParts,
  parseStubLine,
  stubBody,
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

function lineComment(id, minute, line) {
  return {
    id,
    createdAt: `2026-11-10T10:${minute}:00Z`,
    authorLogin: 'alice',
    authorType: 'User',
    editorLogin: null,
    editorType: null,
    lastEditedAt: null,
    isMinimized: false,
    body: `Summary.\n\n\`\`\`\n${line}\n\`\`\`\n`,
  };
}

function routeOf(comments) {
  return route({
    comments, hasWrite: () => true, pinValid: true, issueState: 'open', baseIsDefault: true, hasUncountedMarkers: false,
  });
}

test('route: a parent-track tasks as the latest progress is an error, never a route', () => {
  assert.throws(() => routeOf([
    lineComment('1', '01', 'tr-develop/v1 phase=defined track=parent'),
    lineComment('2', '02', 'tr-develop/v1 phase=tasks track=parent'),
  ]), /parent/);
});

test('route: a spec-merged with track=plan as the latest progress is an error', () => {
  assert.throws(() => routeOf([
    lineComment('1', '01', 'tr-develop/v1 phase=defined track=plan'),
    lineComment('2', '02', 'tr-develop/v1 phase=spec-merged track=plan'),
  ]), /spec-merged/);
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
  assert.deepEqual(SUBCOMMANDS, [
    'count-tasks', 'count-cycles', 'tasks-for', 'live', 'position', 'route', 'parts', 'stub', 'stub-body',
  ]);
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

// ---- parent spec breakdown and stubs ----

/** One breakdown block. `depends` and the label texts are written as given. */
function part(n, { title = `Part ${n} title`, scope = `Scope ${n}.`, depends = 'none', done = `Done ${n}.` } = {}) {
  return `### Part ${n}: ${title}\nScope: ${scope}\nDepends on: ${depends}\nDone when: ${done}\n`;
}

const VALID_SPEC = [
  '# Design\n\n## Goal\n\nSomething.\n\n## Breakdown\n',
  part(1, { title: 'Shared plumbing', scope: 'Build the plumbing.', done: 'The plumbing runs.' }),
  part(2, { title: 'Direct delivery in pi and Claude Code', scope: 'Deliver directly.', depends: 'Part 1', done: 'A run delivers.' }),
  part(3, { title: 'Status mode', scope: 'Show the status.', depends: 'Part 1, Part 2', done: 'Status prints.' }),
].join('\n');

const VALID_PARTS = [
  { n: 1, title: 'Shared plumbing', scope: 'Build the plumbing.', doneWhen: 'The plumbing runs.', dependsOn: [] },
  { n: 2, title: 'Direct delivery in pi and Claude Code', scope: 'Deliver directly.', doneWhen: 'A run delivers.', dependsOn: [1] },
  { n: 3, title: 'Status mode', scope: 'Show the status.', doneWhen: 'Status prints.', dependsOn: [1, 2] },
];

function partsError(text, error, message) {
  assert.deepEqual(parseParts(text), { ok: false, error }, message);
}

test('parseParts: a valid three-part spec', () => {
  assert.deepEqual(parseParts(VALID_SPEC), { ok: true, parts: VALID_PARTS });
});

test('parseParts: no part heading is no-parts', () => {
  partsError('# Design\n\nNo breakdown here.\n', 'no-parts');
  partsError('', 'no-parts');
});

test('parseParts: the numbers are exactly 1, 2, 3 as written', () => {
  partsError(`${part(1)}\n${part(3)}`, 'not-sequential');
  partsError(`${part(2)}\n${part(3)}`, 'not-sequential');
  partsError(`${part(1)}\n${part(1)}`, 'not-sequential');
  partsError(`${part(2)}\n${part(1)}`, 'not-sequential');
});

test('parseParts: more than 50 parts is too-many, and not-sequential wins over it', () => {
  const many = (count) => Array.from({ length: count }, (_, i) => part(i + 1)).join('\n');
  assert.equal(parseParts(many(50)).ok, true);
  partsError(many(51), 'too-many');
  partsError(`${many(51)}\n${part(53)}`, 'not-sequential');
});

test('parseParts: a bad heading is bad-title', () => {
  partsError(part(1).replace('### Part 1:', '### Part 02:'), 'bad-title');
  partsError(part(1).replace('### Part 1:', '### Part 0:'), 'bad-title');
  partsError(part(1).replace('### Part 1:', '### Part one:'), 'bad-title');
  partsError(part(1).replace('### Part 1: ', '### Part 1:'), 'bad-title');
  partsError(part(1).replace('### Part 1: ', '### Part 1:  '), 'bad-title');
  partsError('### Part 1:\nScope: a\nDepends on: none\nDone when: b\n', 'bad-title');
  partsError(part(1, { title: `${'a'.repeat(80)} ` }), 'bad-title');
  partsError(part(1, { title: 'a'.repeat(81) }), 'bad-title');
  assert.equal(parseParts(part(1, { title: 'a'.repeat(80) })).ok, true);
});

test('parseParts: the labels are each present once, in order, with nothing before the first', () => {
  const head = '### Part 1: T\n';
  partsError(`${head}Scope: a\nDepends on: none\n`, 'bad-label', 'no Done when');
  partsError(`${head}Depends on: none\nDone when: b\n`, 'bad-label', 'no Scope');
  partsError(`${head}Scope: a\nDone when: b\n`, 'bad-label', 'no Depends on');
  partsError(`${head}Scope: a\nScope: c\nDepends on: none\nDone when: b\n`, 'bad-label', 'Scope twice');
  partsError(`${head}Scope: a\nDepends on: none\nDone when: b\nDone when: c\n`, 'bad-label', 'Done when twice');
  partsError(`${head}Depends on: none\nScope: a\nDone when: b\n`, 'bad-label', 'Scope after Depends on');
  partsError(`${head}Scope: a\nDone when: b\nDepends on: none\n`, 'bad-label', 'Done when before Depends on');
  partsError(`${head}Some text first.\nScope: a\nDepends on: none\nDone when: b\n`, 'bad-label', 'text before the first label');
  partsError(`${head}`, 'bad-label', 'a heading and nothing else');
  assert.equal(parseParts(`${head}\n\nScope: a\nDepends on: none\nDone when: b\n`).ok, true, 'blank lines before the first label are fine');
});

test('parseParts: a label with no text is empty-text', () => {
  partsError(part(1, { scope: '' }), 'empty-text');
  partsError(part(1, { scope: '   ' }), 'empty-text');
  partsError(part(1, { depends: '' }), 'empty-text');
  partsError(part(1, { done: '' }), 'empty-text');
  partsError('### Part 1: T\nScope:\n\nDepends on: none\nDone when: b\n', 'empty-text');
});

test('parseParts: a label text is at most 1,500 characters', () => {
  assert.equal(parseParts(part(1, { scope: 'a'.repeat(1500) })).ok, true);
  partsError(part(1, { scope: 'a'.repeat(1501) }), 'text-too-long');
  partsError(part(1, { done: 'a'.repeat(1501) }), 'text-too-long');
  partsError(part(1, { scope: `${'a'.repeat(1000)}\n${'b'.repeat(501)}` }), 'text-too-long');
});

test('parseParts: Depends on is none or ascending Part entries, each below this part', () => {
  const second = (depends) => `${part(1)}\n${part(2, { depends })}`;
  const third = (depends) => `${part(1)}\n${part(2)}\n${part(3, { depends })}`;
  assert.deepEqual(parseParts(part(1, { depends: 'none' })).parts[0].dependsOn, []);
  assert.deepEqual(parseParts(second('Part 1')).parts[1].dependsOn, [1]);
  assert.deepEqual(parseParts(third('Part 1, Part 2')).parts[2].dependsOn, [1, 2]);
  partsError(second('Part 2'), 'bad-dependency');
  partsError(second('Part 3'), 'bad-dependency');
  partsError(part(1, { depends: 'Part 1' }), 'bad-dependency');
  partsError(third('Part 1, Part 1'), 'bad-dependency');
  partsError(third('Part 2, Part 1'), 'bad-dependency');
  partsError(second('part 1'), 'bad-dependency');
  partsError(second('Part 1,Part 2'), 'bad-dependency');
  partsError(third('Part 1,Part 2'), 'bad-dependency');
  partsError(second('Part 01'), 'bad-dependency');
  partsError(second('Part 0'), 'bad-dependency');
  partsError(second('None'), 'bad-dependency');
  partsError(second('Part 1, '), 'bad-dependency');
  partsError(second('Part 1\nPart 2'), 'bad-dependency');
});

test('parseParts: a part heading inside a fenced block is not a part', () => {
  const fenced = [
    part(1),
    '## Example',
    '',
    '```markdown',
    part(2, { title: 'Only an example' }),
    '```',
    '',
  ].join('\n');
  const result = parseParts(fenced);
  assert.equal(result.ok, true);
  assert.deepEqual(result.parts.map((p) => p.n), [1]);
  const tilde = `${part(1)}\n~~~~\n${part(2)}\n\`\`\`\n${part(3)}\n~~~~\n`;
  assert.deepEqual(parseParts(tilde).parts.map((p) => p.n), [1]);
  partsError(`\`\`\`\n${part(1)}\n\`\`\`\n`, 'no-parts');
});

test('parseParts: CRLF text reads like LF', () => {
  assert.deepEqual(parseParts(VALID_SPEC.replace(/\n/g, '\r\n')), parseParts(VALID_SPEC));
  assert.deepEqual(parseParts(VALID_SPEC.replace(/\n/g, '\r\n')), { ok: true, parts: VALID_PARTS });
});

test('parseParts: a label text spanning lines is joined with LF and trimmed', () => {
  const spec = '### Part 1: T\nScope:   first line\nsecond line  \n\nthird line\nDepends on: none\nDone when:\n  one\n  two\n\n';
  const result = parseParts(spec);
  assert.equal(result.ok, true);
  assert.equal(result.parts[0].scope, 'first line\nsecond line  \n\nthird line');
  assert.equal(result.parts[0].doneWhen, 'one\n  two');
});

test('parseParts: a continuation line that starts with #92 is text, a heading ends the block', () => {
  const spec = [
    '### Part 1: T',
    'Scope: first',
    '#92 shipped the link call',
    'Depends on: none',
    'Done when: ok',
    '',
    '## Next section',
    'Scope: stray text after the heading',
    '',
  ].join('\n');
  const result = parseParts(spec);
  assert.equal(result.ok, true);
  assert.equal(result.parts.length, 1);
  assert.equal(result.parts[0].scope, 'first\n#92 shipped the link call');
  assert.equal(result.parts[0].doneWhen, 'ok');
  // The heading ends the last part's block even before its labels are complete.
  partsError('### Part 1: T\nScope: a\nDepends on: none\n## Next section\nDone when: b\n', 'bad-label');
  // Any ATX heading level ends a block, including a deeper one.
  partsError('### Part 1: T\nScope: a\n#### Sub\nDepends on: none\nDone when: b\n', 'bad-label');
});

test('parseParts: a label word is a label only at the start of a line', () => {
  const result = parseParts('### Part 1: T\nScope: a\n  Depends on: not a label\nDepends on: none\nDone when: see Scope: above\n');
  assert.equal(result.ok, true);
  assert.equal(result.parts[0].scope, 'a\n  Depends on: not a label');
  assert.equal(result.parts[0].doneWhen, 'see Scope: above');
});

test('parseStubLine: the first line, with the tolerated variants', () => {
  assert.deepEqual(parseStubLine('Part 2 of 3 in #92'), { ok: true, part: 2, count: 3, parent: 92 });
  assert.deepEqual(parseStubLine('Part 2 of 3 in #92\n\nrest of the body\n'), { ok: true, part: 2, count: 3, parent: 92 });
  assert.deepEqual(parseStubLine('\uFEFFPart 2 of 3 in #92\n'), { ok: true, part: 2, count: 3, parent: 92 });
  assert.deepEqual(parseStubLine('Part 2 of 3 in #92\r\n\r\nrest\r\n'), { ok: true, part: 2, count: 3, parent: 92 });
  assert.deepEqual(parseStubLine('Part 2 of 3 in #92   \n'), { ok: true, part: 2, count: 3, parent: 92 });
  assert.deepEqual(parseStubLine('\uFEFFPart 12 of 50 in #123456789\r\n'), { ok: true, part: 12, count: 50, parent: 123456789 });
});

test('parseStubLine: anything else is no-stub-line', () => {
  const bad = [
    'Part 0 of 3 in #92',
    'Part 2 of 3 of #92',
    'part 2 of 3 in #92',
    'Part 2 of 3 in #092',
    'Part 2 of 3 in #0',
    'Part 02 of 3 in #92',
    'Part 2 of 100 in #92',
    'Part 2 of 3 in #1234567890',
    'Part 2 of 3 in 92',
    ' x Part 2 of 3 in #92',
    'Part 2 of 3 in #92 and more',
    '',
    '\n',
    'Intro line\nPart 2 of 3 in #92\n',
    '\nPart 2 of 3 in #92\n',
  ];
  for (const text of bad) assert.deepEqual(parseStubLine(text), { ok: false, error: 'no-stub-line' }, JSON.stringify(text));
  assert.deepEqual(parseStubLine(undefined), { ok: false, error: 'no-stub-line' });
});

const STUB_LAST = 'This part is not defined yet. Run /develop <this issue> to define it.';

test('stubBody: part 2 with its dependency', () => {
  const r = stubBody(VALID_SPEC, 2, 92, { 1: 91 });
  assert.deepEqual(r, {
    ok: true,
    title: 'Direct delivery in pi and Claude Code',
    body: [
      'Part 2 of 3 in #92', '', 'Deliver directly.', '', 'Depends on: #91', '', 'Done when: A run delivers.', '', STUB_LAST,
    ].join('\n') + '\n',
  });
  assert.deepEqual(stubBody(VALID_SPEC, '2', '92', { 1: 91 }), r, 'strings and numbers alike');
  assert.deepEqual(stubBody(VALID_SPEC, 2, 92, { '1': 91, '9': 5 }), r, 'an extra map entry is ignored');
});

test('stubBody: no dependency, two dependencies, multi-line texts', () => {
  const first = stubBody(VALID_SPEC, 1, 92, {});
  assert.equal(first.body.split('\n')[4], 'Depends on: nothing');
  assert.equal(first.body.split('\n')[0], 'Part 1 of 3 in #92');
  assert.equal(first.title, 'Shared plumbing');
  const third = stubBody(VALID_SPEC, 3, 92, { 1: 91, 2: 93 });
  assert.equal(third.body.split('\n')[4], 'Depends on: #91, #93');
  const multi = stubBody(`${part(1, { scope: 'one\ntwo', done: 'x\ny' })}`, 1, 7, {});
  assert.equal(multi.body, ['Part 1 of 1 in #7', '', 'one', 'two', '', 'Depends on: nothing', '', 'Done when: x', 'y', '', STUB_LAST].join('\n') + '\n');
});

test('stubBody: a missing dependency entry is missing-dependency; an unknown part is no-such-part', () => {
  assert.deepEqual(stubBody(VALID_SPEC, 2, 92, {}), { ok: false, error: 'missing-dependency' });
  assert.deepEqual(stubBody(VALID_SPEC, 3, 92, { 1: 91 }), { ok: false, error: 'missing-dependency' });
  assert.deepEqual(stubBody(VALID_SPEC, 4, 92, {}), { ok: false, error: 'no-such-part' });
});

test('stubBody: a spec that does not parse reports the parseParts error', () => {
  assert.deepEqual(stubBody('# nothing', 1, 92, {}), { ok: false, error: 'no-parts' });
  assert.deepEqual(stubBody(`${part(1)}\n${part(3)}`, 1, 92, {}), { ok: false, error: 'not-sequential' });
});

test('stubBody: part text with a tr-develop/ line or a marker comment is marker-in-text', () => {
  const refused = (opts) => stubBody(`${part(1)}\n${part(2, { depends: 'Part 1', ...opts })}`, 2, 92, { 1: 91 });
  const err = { ok: false, error: 'marker-in-text' };
  assert.deepEqual(refused({ scope: 'before\ntr-develop/v1 phase=kickoff branch=x' }), err);
  assert.deepEqual(refused({ scope: 'before\n   tr-develop/v1 phase=kickoff' }), err);
  assert.deepEqual(refused({ done: 'ok\n\ttr-develop/v1 phase=closed' }), err);
  assert.deepEqual(refused({ scope: 'text <!-- tr-develop:ac-begin --> text' }), err);
  assert.deepEqual(refused({ done: '<!-- tr-develop:ac-end -->' }), err);
  assert.equal(refused({ scope: 'see tr-develop/v1 in the docs' }).ok, true, 'only a line that starts with it');
  assert.equal(refused({ scope: 'an ordinary <!-- comment -->' }).ok, true);
  // Another part's text is not copied, so it does not matter.
  const other = `${part(1, { scope: 'tr-develop/v1 x' })}\n${part(2, { depends: 'Part 1' })}`;
  assert.equal(stubBody(other, 2, 92, { 1: 91 }).ok, true);
});

test('stubBody: a bad part, parent or map is bad-input', () => {
  const bad = { ok: false, error: 'bad-input' };
  for (const value of ['0', 'abc', '012', '', '-1', '1234567890', 0, -1, 2.5, null, undefined, {}, [2]]) {
    assert.deepEqual(stubBody(VALID_SPEC, value, 92, { 1: 91 }), bad, `part ${JSON.stringify(value)}`);
    assert.deepEqual(stubBody(VALID_SPEC, 2, value, { 1: 91 }), bad, `parent ${JSON.stringify(value)}`);
  }
  for (const value of ['91', 0, -3, 1.5, null, true, [91], {}, Number.NaN, Infinity]) {
    assert.deepEqual(stubBody(VALID_SPEC, 2, 92, { 1: value }), bad, `map value ${JSON.stringify(value)}`);
  }
  // Every value is checked, needed or not.
  assert.deepEqual(stubBody(VALID_SPEC, 2, 92, { 1: 91, 7: 'x' }), bad);
  for (const map of [null, undefined, [], 'x', 5]) {
    assert.deepEqual(stubBody(VALID_SPEC, 2, 92, map), bad, `map ${JSON.stringify(map)}`);
  }
  // Bad input is refused before the spec is read.
  assert.deepEqual(stubBody('# nothing', 'abc', 92, {}), bad);
});

test('CLI parts and stub print the function results', () => {
  const dir = tmpDir();
  const spec = put(dir, 'spec.md', VALID_SPEC);
  const parts = run(['parts', spec]);
  assert.equal(parts.status, 0);
  assert.deepEqual(parts.parsed, { ok: true, parts: VALID_PARTS });
  const bad = run(['parts', put(dir, 'bad.md', '# nothing\n')]);
  assert.equal(bad.status, 1);
  assert.deepEqual(bad.parsed, { ok: false, error: 'no-parts' });
  const crlf = run(['parts', put(dir, 'crlf.md', VALID_SPEC.replace(/\n/g, '\r\n'))]);
  assert.deepEqual(crlf.parsed, parts.parsed);

  const stub = run(['stub', put(dir, 'stub.md', '\uFEFFPart 2 of 3 in #92\r\n\r\nbody\r\n')]);
  assert.equal(stub.status, 0);
  assert.deepEqual(stub.parsed, { ok: true, part: 2, count: 3, parent: 92 });
  const none = run(['stub', put(dir, 'none.md', 'Hello\nPart 2 of 3 in #92\n')]);
  assert.equal(none.status, 1);
  assert.deepEqual(none.parsed, { ok: false, error: 'no-stub-line' });
  assert.equal(run(['parts', path.join(dir, 'missing.md')]).parsed.ok, false);
  assert.equal(run(['parts']).parsed.ok, false);
  assert.equal(run(['stub']).parsed.ok, false);
});

test('CLI stub-body writes the body and prints {ok, title}', () => {
  const dir = tmpDir();
  const spec = put(dir, 'spec.md', VALID_SPEC);
  const depends = put(dir, 'depends.json', { 1: 91 });
  const out = path.join(dir, 'stub-body.md');
  const r = run(['stub-body', spec, '2', '92', depends, out]);
  assert.equal(r.status, 0);
  assert.deepEqual(r.parsed, { ok: true, title: 'Direct delivery in pi and Claude Code' });
  assert.equal(fs.readFileSync(out, 'utf8'), stubBody(VALID_SPEC, 2, 92, { 1: 91 }).body);
  assert.deepEqual(parseStubLine(fs.readFileSync(out, 'utf8')), { ok: true, part: 2, count: 3, parent: 92 });
});

test('CLI stub-body: a refusal writes nothing and exits 1', () => {
  const dir = tmpDir();
  const spec = put(dir, 'spec.md', VALID_SPEC);
  const out = path.join(dir, 'never.md');
  const cases = [
    [['stub-body', spec, '2', '92', put(dir, 'empty.json', {}), out], 'missing-dependency'],
    [['stub-body', spec, '9', '92', put(dir, 'd1.json', { 1: 91 }), out], 'no-such-part'],
    [['stub-body', spec, 'abc', '92', put(dir, 'd2.json', { 1: 91 }), out], 'bad-input'],
    [['stub-body', spec, '2', '92', put(dir, 'd3.json', { 1: '91' }), out], 'bad-input'],
    [['stub-body', spec, '2', '92', put(dir, 'd4.json', [1]), out], 'bad-input'],
    [['stub-body', put(dir, 'nospec.md', '# x\n'), '1', '92', put(dir, 'd5.json', {}), out], 'no-parts'],
  ];
  for (const [args, error] of cases) {
    const r = run(args);
    assert.equal(r.status, 1, error);
    assert.deepEqual(r.parsed, { ok: false, error }, error);
  }
  assert.equal(fs.existsSync(out), false);
  const broken = run(['stub-body', spec, '2', '92', put(dir, 'broken.json', '{not json'), out]);
  assert.equal(broken.status, 1);
  assert.equal(broken.parsed.ok, false);
  assert.equal(fs.existsSync(out), false);
  assert.equal(run(['stub-body', spec, '2', '92']).parsed.ok, false);
});
