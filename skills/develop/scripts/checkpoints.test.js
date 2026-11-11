import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import {
  PHASES,
  PROGRESS_PHASES,
  SUBCOMMANDS,
  isLogin,
  parseCheckpoint,
  requiredFields,
  validateCheckpoint,
  checkCarry,
  formatCheckpoint,
} from './checkpoints.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HANDOFF = path.join(HERE, '..', 'handoff.md');
const CHECKPOINTS = path.join(HERE, 'checkpoints.js');

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'checkpoints-test-'));
}

function run(args) {
  const result = spawnSync(process.execPath, [CHECKPOINTS, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

/** Writes `value` as JSON into a fresh temp directory and returns { dir, file }. */
function jsonFile(value, dir = tmpDir()) {
  const file = path.join(dir, 'input.json');
  fs.writeFileSync(file, JSON.stringify(value));
  return { dir, file };
}

function ctxFor(c) {
  const reachable = new Set(c.reachable);
  return { repo: c.ctx.repo, base: c.ctx.base, isAncestor: (sha) => reachable.has(sha) };
}

const A = 'a'.repeat(40);
const LINE = `tr-develop/v1 phase=kickoff branch=feat/x head=${A} review=agent gh=strvmarv track=small`;

// ---- constants ----

test('PHASES and PROGRESS_PHASES are the spec lists', () => {
  assert.deepEqual(PHASES, [
    'defined', 'reapproved', 'kickoff', 'plan-approved', 'tasks',
    'final-approved', 'pr-open', 'blocked', 'closed',
  ]);
  assert.deepEqual(PROGRESS_PHASES, [
    'kickoff', 'plan-approved', 'tasks', 'final-approved', 'pr-open', 'closed', 'reapproved',
  ]);
});

// ---- isLogin ----

test('isLogin accepts logins with _ and - and rejects hostile text', () => {
  for (const ok of ['jane-doe_acme', 'a', 'a-b_c', 'strvmarv', 'x'.repeat(39)]) {
    assert.equal(isLogin(ok), true, ok);
  }
  for (const bad of ['x; rm -rf /', '$(id)', '-lead', '', 'jane\n', 'x'.repeat(40), 'a b', undefined, null, 7]) {
    assert.equal(isLogin(bad), false, String(bad));
  }
});

// ---- parse-cases ----

test('parse-cases golden: parseCheckpoint matches every expectation', () => {
  const cases = readGolden(HANDOFF, 'parse-cases');
  assert.ok(cases.length >= 12);
  for (const c of cases) {
    const got = parseCheckpoint(c.body);
    if (c.expect.ok) {
      assert.equal(got.ok, true, c.name);
      assert.equal(got.phase, c.expect.phase, c.name);
      assert.equal(typeof got.line, 'string', c.name);
      assert.ok(got.line.startsWith('tr-develop/v1 phase='), c.name);
    } else {
      assert.deepEqual(got, { ok: false, error: c.expect.error }, c.name);
    }
  }
});

test('parseCheckpoint returns the fields as key/value tokens, splitting at the first =', () => {
  const got = parseCheckpoint('x\n```\ntr-develop/v1 phase=defined track=small rounds.plan=0 definedAt=2026-11-10T12:34:56Z note=a=b\n```\n');
  assert.equal(got.ok, true);
  assert.deepEqual(got.fields, {
    track: 'small',
    'rounds.plan': '0',
    definedAt: '2026-11-10T12:34:56Z',
    note: 'a=b',
  });
});

test('parseCheckpoint strips every carriage return before splitting, so a lone one joins its neighbours', () => {
  const crlf = parseCheckpoint(`Summary\r\n\r\n${LINE}\r\n`);
  assert.equal(crlf.ok, true);
  assert.equal(crlf.line, LINE);
  assert.deepEqual(parseCheckpoint(`Summary\r${LINE}\r`), { ok: false, error: 'no-line' });
});

// ---- requiredFields ----

test('requiredFields follows the per-phase table', () => {
  assert.deepEqual(requiredFields('defined', 'plan'), [
    'track', 'repo', 'base', 'branch', 'bodyHash', 'acHash', 'definedBy', 'definedAt', 'spec', 'approvedSha',
  ]);
  assert.deepEqual(requiredFields('defined', 'small'), [
    'track', 'repo', 'base', 'branch', 'bodyHash', 'acHash', 'definedBy', 'definedAt',
  ]);
  assert.deepEqual(requiredFields('reapproved', 'small'), [
    'track', 'repo', 'base', 'branch', 'bodyHash', 'acHash', 'definedBy', 'definedAt',
    'head', 'review', 'gh', 'deferred',
  ]);
  assert.deepEqual(requiredFields('kickoff', 'plan'), ['track', 'branch', 'head', 'review', 'gh', 'spec', 'approvedSha']);
  assert.deepEqual(requiredFields('kickoff', 'small'), ['track', 'branch', 'head', 'review', 'gh']);
  assert.deepEqual(requiredFields('plan-approved', 'small'), [
    'track', 'branch', 'head', 'review', 'gh', 'spec', 'approvedSha', 'plan', 'planSha', 'rounds.plan',
  ]);
  assert.deepEqual(requiredFields('tasks', 'plan'), [
    'track', 'branch', 'head', 'review', 'gh', 'deferred', 'spec', 'approvedSha', 'plan', 'planSha', 'tasksDone',
  ]);
  assert.deepEqual(requiredFields('tasks', 'small'), ['track', 'branch', 'head', 'review', 'gh', 'deferred', 'cyclesDone']);
  assert.deepEqual(requiredFields('final-approved', 'plan'), [
    'track', 'branch', 'head', 'review', 'gh', 'deferred', 'rounds.final', 'spec', 'approvedSha', 'plan', 'planSha', 'tasksDone',
  ]);
  assert.deepEqual(requiredFields('pr-open', 'plan'), [
    'track', 'branch', 'head', 'review', 'gh', 'pr', 'deferred', 'spec', 'approvedSha', 'plan', 'planSha', 'tasksDone',
  ]);
  assert.deepEqual(requiredFields('blocked', 'plan'), ['branch', 'stage', 'blocker']);
  assert.deepEqual(requiredFields('closed', 'small'), ['branch', 'head', 'gh', 'pr']);
});

// ---- validate-cases ----

test('validate-cases golden: validateCheckpoint matches every expectation', () => {
  const cases = readGolden(HANDOFF, 'validate-cases');
  for (const c of cases) {
    assert.deepEqual(validateCheckpoint(c.cp, ctxFor(c)), c.expect, c.name);
  }
});

test('validate-cases golden covers every phase, both error codes and the brief\'s named cases', () => {
  const cases = readGolden(HANDOFF, 'validate-cases');
  for (const phase of PHASES) {
    assert.ok(cases.some((c) => c.cp.phase === phase && c.expect.ok === true), `a valid ${phase}`);
  }
  assert.ok(cases.some((c) => c.expect.error === 'missing'));
  assert.ok(cases.some((c) => c.expect.error === 'bad-value'));
  const names = cases.map((c) => c.name);
  for (const needle of [
    'branch=- on kickoff', 'blocked with branch=-', 'blocker=7', 'stage=deploy', 'track=other', 'head 39 hex',
    'bodyHash 63 hex', 'plan path outside', 'planSha not reachable', 'kickoff with planSha but no plan',
    'closed head not reachable', 'rounds.plan=0', 'gh=x;y', 'pr in another repository', 'pr lookalike',
    'reapproved carrying rounds.final', 'reapproved carrying pr',
  ]) {
    assert.ok(names.some((n) => n.includes(needle)), `golden case "${needle}"`);
  }
});

test('validateCheckpoint rejects a non-string or empty value and an unknown phase', () => {
  const c = readGolden(HANDOFF, 'validate-cases').find((x) => x.name === 'kickoff plan track');
  const ctx = ctxFor(c);
  assert.deepEqual(validateCheckpoint({ phase: 'kickoff', fields: { ...c.cp.fields, gh: 7 } }, ctx), { ok: false, error: 'bad-value', key: 'gh' });
  assert.deepEqual(validateCheckpoint({ phase: 'nope', fields: c.cp.fields }, ctx), { ok: false, error: 'bad-value', key: 'phase' });
});

test('validateCheckpoint: a missing track is `missing track` before any other missing field', () => {
  const ctx = { repo: 'a/b', base: 'main', isAncestor: () => true };
  assert.deepEqual(validateCheckpoint({ phase: 'tasks', fields: {} }, ctx), { ok: false, error: 'missing', key: 'track' });
});

// ---- carry-cases ----

test('carry-cases golden: checkCarry matches every expectation', () => {
  const cases = readGolden(HANDOFF, 'carry-cases');
  for (const c of cases) {
    assert.deepEqual(checkCarry(c.checkpoint, c.prior), c.expect, c.name);
  }
});

test('carry-cases golden covers the brief\'s named cases', () => {
  const names = readGolden(HANDOFF, 'carry-cases').map((c) => c.name);
  for (const needle of [
    'reapproved omits tasksDone', 'reapproved after final-approved without rounds.final',
    'reapproved replaces rounds.plan', 'kickoff omits planSha', 'kickoff with a null prior',
    'kickoff omits rounds.final', 'kickoff carries rounds.final',
    'blocked-only history', 'no prior key',
  ]) {
    assert.ok(names.some((n) => n.includes(needle)), `golden case "${needle}"`);
  }
});

test('carry-cases golden through the CLI', () => {
  for (const c of readGolden(HANDOFF, 'carry-cases')) {
    const { file } = jsonFile({ checkpoint: c.checkpoint, ...(Object.hasOwn(c, 'prior') ? { prior: c.prior } : {}) });
    const { parsed, status } = run(['carry', file]);
    assert.deepEqual(parsed, c.expect, c.name);
    assert.equal(status, c.expect.ok ? 0 : 1, c.name);
  }
});

test('CLI carry: a call with no `prior` key is bad-input, a null prior is accepted', () => {
  const checkpoint = { phase: 'kickoff', fields: { track: 'small' } };
  const missing = run(['carry', jsonFile({ checkpoint }).file]);
  assert.deepEqual(missing.parsed, { ok: false, error: 'bad-input' });
  assert.equal(missing.status, 1);
  const nul = run(['carry', jsonFile({ checkpoint, prior: null }).file]);
  assert.deepEqual(nul.parsed, { ok: true });
});

test('checkCarry with an undefined prior is bad-input, and other phases are not carry-checked', () => {
  assert.deepEqual(checkCarry({ phase: 'kickoff', fields: {} }, undefined), { ok: false, error: 'bad-input' });
  assert.deepEqual(checkCarry({ phase: 'tasks', fields: {} }, { phase: 'tasks', fields: { planSha: A } }), { ok: true });
});

// ---- formatCheckpoint ----

test('formatCheckpoint writes branch first, table order next, unknown keys last and sorted', () => {
  const line = formatCheckpoint('kickoff', {
    zeta: '1', gh: 'strvmarv', head: A, alpha: '2', review: 'agent', branch: 'feat/x', track: 'small',
  });
  assert.equal(
    line,
    `tr-develop/v1 phase=kickoff branch=feat/x track=small head=${A} review=agent gh=strvmarv alpha=2 zeta=1`,
  );
});

test('formatCheckpoint refuses a bad phase, key or value', () => {
  assert.throws(() => formatCheckpoint('deploy', {}), { code: 'bad-phase' });
  assert.throws(() => formatCheckpoint('kickoff', { 'bad key': 'x' }), { code: 'bad-key' });
  assert.throws(() => formatCheckpoint('kickoff', { phase: 'tasks' }), { code: 'bad-key' });
  assert.throws(() => formatCheckpoint('kickoff', { branch: '' }), { code: 'bad-value', key: 'branch' });
  assert.throws(() => formatCheckpoint('kickoff', { branch: 'a b' }), { code: 'bad-value', key: 'branch' });
  assert.throws(() => formatCheckpoint('kickoff', { branch: 'a\u00a0b' }), { code: 'bad-value', key: 'branch' });
  assert.throws(() => formatCheckpoint('kickoff', { branch: 'a\nb' }), { code: 'bad-value', key: 'branch' });
});

test('round trip: every valid validate golden formats and parses back to the same fields', () => {
  const valid = readGolden(HANDOFF, 'validate-cases').filter((c) => c.expect.ok === true);
  assert.ok(valid.length >= 12);
  for (const c of valid) {
    const line = formatCheckpoint(c.cp.phase, c.cp.fields);
    const got = parseCheckpoint(line);
    assert.equal(got.ok, true, c.name);
    assert.equal(got.phase, c.cp.phase, c.name);
    assert.deepEqual(got.fields, c.cp.fields, c.name);
    assert.equal(got.line, line, c.name);
  }
});

// ---- CLI: validate ----

test('CLI validate: valid and invalid goldens give the same result as the function', () => {
  const cases = readGolden(HANDOFF, 'validate-cases');
  const picks = [
    ...cases.filter((c) => c.expect.ok === true).slice(0, 4),
    ...cases.filter((c) => c.expect.error === 'missing').slice(0, 2),
    ...cases.filter((c) => c.expect.error === 'bad-value').slice(0, 3),
    cases.find((c) => c.name.startsWith('closed head not reachable')),
    cases.find((c) => c.name === 'planSha not reachable'),
  ];
  for (const c of picks) {
    const { file } = jsonFile({ checkpoint: c.cp, repo: c.ctx.repo, base: c.ctx.base, reachable: c.reachable });
    const { parsed, status } = run(['validate', file]);
    assert.deepEqual(parsed, c.expect, c.name);
    assert.equal(status, c.expect.ok ? 0 : 1, c.name);
  }
});

test('CLI validate: malformed input is bad-input', () => {
  for (const input of [
    {},
    { checkpoint: { phase: 'kickoff', fields: {} }, repo: 'a/b', base: 'main' },
    { checkpoint: { phase: 'kickoff', fields: {} }, repo: 'a/b', base: 'main', reachable: 'x' },
    { checkpoint: { phase: 'kickoff' }, repo: 'a/b', base: 'main', reachable: [] },
  ]) {
    const { parsed, status } = run(['validate', jsonFile(input).file]);
    assert.deepEqual(parsed, { ok: false, error: 'bad-input' });
    assert.equal(status, 1);
  }
});

// ---- readback-cases ----

test('readback-cases golden through the CLI', () => {
  const cases = readGolden(HANDOFF, 'readback-cases');
  assert.ok(cases.some((c) => c.expect.ok === true));
  assert.ok(cases.some((c) => c.expect.error === 'multiple-lines'));
  assert.ok(cases.some((c) => c.expect.error === 'mismatch'));
  for (const c of cases) {
    const dir = tmpDir();
    const fetched = path.join(dir, 'fetched.txt');
    const lineFile = path.join(dir, 'line.txt');
    fs.writeFileSync(fetched, c.fetched);
    fs.writeFileSync(lineFile, c.line);
    const { parsed, status } = run(['readback', fetched, lineFile]);
    assert.deepEqual(parsed, c.expect, c.name);
    assert.equal(status, c.expect.ok ? 0 : 1, c.name);
  }
});

test('CLI readback: a BOM or trailing newline in lineFile or the fetched file is harmless', () => {
  const dir = tmpDir();
  const fetched = path.join(dir, 'fetched.txt');
  const lineFile = path.join(dir, 'line.txt');
  fs.writeFileSync(fetched, `\uFEFFSummary\n\n\`\`\`\n${LINE}\n\`\`\`\n`);
  fs.writeFileSync(lineFile, `${LINE}\n`);
  assert.deepEqual(run(['readback', fetched, lineFile]).parsed, { ok: true });
});

test('CLI readback: a missing file is an error object, not a crash', () => {
  const { parsed, status } = run(['readback', path.join(tmpDir(), 'nope.txt'), path.join(tmpDir(), 'nope2.txt')]);
  assert.equal(parsed.ok, false);
  assert.equal(status, 1);
});

// ---- CLI: format ----

function formatInput(dir, summary, overrides = {}) {
  const summaryFile = path.join(dir, 'summary.txt');
  fs.writeFileSync(summaryFile, summary);
  return {
    phase: 'kickoff',
    fields: { track: 'small', branch: 'feat/x', head: A, review: 'agent', gh: 'strvmarv' },
    summaryFile,
    outFile: path.join(dir, 'comment.md'),
    lineFile: path.join(dir, 'line.txt'),
    ...overrides,
  };
}

test('CLI format: outFile parses back to the line in lineFile, UTF-8 without a BOM', () => {
  const dir = tmpDir();
  const input = formatInput(dir, 'Resumed on the branch.\r\nSecond line with caf\u00e9.\n\n');
  const { parsed, status } = run(['format', jsonFile(input, dir).file]);
  assert.equal(status, 0);
  assert.deepEqual(Object.keys(parsed), ['line']);
  const comment = fs.readFileSync(input.outFile);
  const lineBytes = fs.readFileSync(input.lineFile);
  assert.notDeepEqual([...comment.subarray(0, 3)], [0xef, 0xbb, 0xbf]);
  assert.notDeepEqual([...lineBytes.subarray(0, 3)], [0xef, 0xbb, 0xbf]);
  const line = lineBytes.toString('utf8');
  assert.equal(parsed.line, line);
  const got = parseCheckpoint(comment.toString('utf8'));
  assert.equal(got.ok, true);
  assert.equal(got.line, line);
  assert.deepEqual(got.fields, input.fields);
  // The shape: summary, a blank line, then the line inside a fence.
  assert.ok(comment.toString('utf8').startsWith('Resumed on the branch.\r\nSecond line with caf\u00e9.\n\n```\ntr-develop/v1 phase=kickoff '));
  assert.ok(comment.toString('utf8').endsWith('\n```\n'));
  assert.equal(readbackOk(dir, input), true);
});

function readbackOk(dir, input) {
  return run(['readback', input.outFile, input.lineFile]).parsed.ok === true;
}

test('CLI format: a summary holding a triple-backtick run gets a longer fence', () => {
  const dir = tmpDir();
  const input = formatInput(dir, 'Ran:\n\n```sh\nnpm test\n```\n');
  const { parsed } = run(['format', jsonFile(input, dir).file]);
  assert.ok(parsed.line);
  const lines = fs.readFileSync(input.outFile, 'utf8').split('\n');
  const idx = lines.indexOf(parsed.line);
  assert.ok(idx > 0);
  const fence = lines[idx - 1];
  assert.match(fence, /^`+$/);
  assert.ok(fence.length > 3, 'the fence is longer than the longest run in the summary');
  assert.equal(lines[idx + 1], fence);
  assert.equal(parseCheckpoint(fs.readFileSync(input.outFile, 'utf8')).line, parsed.line);
});

test('CLI format: a four-backtick run in the summary gets a five-backtick fence', () => {
  const dir = tmpDir();
  const input = formatInput(dir, 'x ```` y');
  run(['format', jsonFile(input, dir).file]);
  assert.ok(fs.readFileSync(input.outFile, 'utf8').split('\n').includes('`````'));
});

test('CLI format: refuses a summary with a checkpoint line, writing nothing', () => {
  const variants = {
    'own line': `Before\n${LINE}\nAfter\n`,
    indented: `Before\n    ${LINE}\n`,
    'NBSP prefix': `Before\n\u00a0${LINE}\n`,
    'BOM prefix': `\uFEFF${LINE}\n`,
    'CRLF with trailing spaces': `Before\r\n${LINE}   \r\n`,
    'lone CR': `Before\r${LINE}\r`,
    'a different phase': `x\ntr-develop/v1 phase=closed branch=feat/x\n`,
  };
  for (const [label, summary] of Object.entries(variants)) {
    const dir = tmpDir();
    const input = formatInput(dir, summary);
    const { parsed, status } = run(['format', jsonFile(input, dir).file]);
    assert.deepEqual(parsed, { ok: false, error: 'summary-has-checkpoint-line' }, label);
    assert.equal(status, 1, label);
    assert.equal(fs.existsSync(input.outFile), false, label);
    assert.equal(fs.existsSync(input.lineFile), false, label);
  }
});

test('CLI format: a summary that only mentions the grammar is accepted', () => {
  const dir = tmpDir();
  const input = formatInput(dir, 'The line starts with tr-develop/v1 phase=kickoff and then fields.\n> quoted: tr-develop/v1 phase=kickoff\n');
  const { parsed, status } = run(['format', jsonFile(input, dir).file]);
  assert.equal(status, 0);
  assert.ok(parsed.line);
});

test('CLI format: refuses an empty value or one with whitespace, writing nothing', () => {
  for (const value of ['', 'two words', 'tab\there', 'nb\u00a0sp']) {
    const dir = tmpDir();
    const input = formatInput(dir, 'Summary.\n');
    input.fields = { ...input.fields, branch: value };
    const { parsed, status } = run(['format', jsonFile(input, dir).file]);
    assert.deepEqual(parsed, { ok: false, error: 'bad-value', key: 'branch' }, JSON.stringify(value));
    assert.equal(status, 1);
    assert.equal(fs.existsSync(input.outFile), false);
    assert.equal(fs.existsSync(input.lineFile), false);
  }
});

test('CLI format: an unknown phase and malformed input are errors', () => {
  const dir = tmpDir();
  const input = formatInput(dir, 'Summary.\n', { phase: 'deploy' });
  assert.deepEqual(run(['format', jsonFile(input, dir).file]).parsed, { ok: false, error: 'bad-phase' });
  assert.deepEqual(run(['format', jsonFile({ phase: 'kickoff' }).file]).parsed, { ok: false, error: 'bad-input' });
});

// ---- SUBCOMMANDS dispatcher ----

test('SUBCOMMANDS dispatcher: every listed name dispatches, an unlisted word is rejected', () => {
  assert.deepEqual(SUBCOMMANDS, ['validate', 'carry', 'format', 'readback']);
  for (const name of SUBCOMMANDS) {
    const { parsed } = run([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
  }
  const bad = run(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
  assert.deepEqual(run([]).parsed, { ok: false, error: 'unknown subcommand' });
});
