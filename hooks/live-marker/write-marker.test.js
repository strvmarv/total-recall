// hooks/live-marker/write-marker.test.js — the live-session marker writer (Part 5).
//
// Every spawn passes an explicit, scrubbed env: TOTAL_RECALL_HOME and HOME (and USERPROFILE) are
// temporary directories, so nothing here can touch the real data directory or ~/.claude.

import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  isValidSessionId, buildMarker, writeMarker, prune, resolveDataDir,
} from './write-marker.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const SCRIPT = path.join(here, 'write-marker.js');
const CASES = JSON.parse(fs.readFileSync(path.join(here, 'session-id-cases.json'), 'utf8'));
const posix = process.platform !== 'win32';
const AT = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/;
const SID = '3f2a9c1e-5b7d-4e08-9a41-0c6d2f8b7a15';
const TRANSCRIPT = `/home/u/.claude/projects/-work-proj/${SID}.jsonl`;

let root;
let counter = 0;

before(() => { root = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-live-marker-')); });
after(() => { fs.rmSync(root, { recursive: true, force: true }); });

/** A fresh scrubbed environment: a data directory and a home, both temporary. */
function freshEnv() {
  const dir = path.join(root, `case-${counter++}`);
  const dataDir = path.join(dir, 'data');
  const home = path.join(dir, 'home');
  fs.mkdirSync(dataDir, { recursive: true });
  fs.mkdirSync(home, { recursive: true });
  return {
    dataDir,
    liveDir: path.join(dataDir, 'live'),
    env: { TOTAL_RECALL_HOME: dataDir, HOME: home, USERPROFILE: home, PATH: process.env.PATH },
  };
}

function payload(over = {}) {
  return {
    session_id: SID,
    transcript_path: TRANSCRIPT,
    cwd: '/work/proj',
    hook_event_name: 'SessionStart',
    source: 'startup',
    ...over,
  };
}

/** Runs the writer as a child process. `input` is a string or an object (JSON-encoded). */
function run({ env, input, args = ['4242'] }) {
  const text = typeof input === 'string' ? input : JSON.stringify(input);
  return spawnSync(process.execPath, [SCRIPT, ...args], { env, input: text, encoding: 'utf8', timeout: 10000 });
}

function markerFiles(liveDir) {
  try { return fs.readdirSync(liveDir).sort(); } catch { return []; }
}

function assertSilent(result) {
  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
  assert.equal(result.stderr, '');
}

/** A pid that no longer exists: a child that exited and was reaped. */
async function deadPid() {
  const child = spawn(process.execPath, ['-e', '0'], { stdio: 'ignore' });
  const pid = child.pid;
  await new Promise((resolve) => child.once('exit', resolve));
  return pid;
}

describe('the marker file', () => {
  test('writes the marker with the documented shape', () => {
    const { env, liveDir } = freshEnv();
    const r = run({ env, input: payload() });
    assertSilent(r);
    const file = path.join(liveDir, 'claude-code-4242.json');
    const marker = JSON.parse(fs.readFileSync(file, 'utf8'));
    const { at, ...rest } = marker;
    assert.match(at, AT);
    assert.deepEqual(rest, {
      v: 1, host: 'claude-code', host_pid: 4242, session_id: SID, transcript_path: TRANSCRIPT, cwd: '/work/proj',
    });
    assert.deepEqual(Object.keys(marker), ['v', 'host', 'host_pid', 'session_id', 'transcript_path', 'cwd', 'at']);
    if (posix) {
      assert.equal(fs.statSync(file).mode & 0o777, 0o600);
      assert.equal(fs.statSync(liveDir).mode & 0o777, 0o700);
    }
  });

  test('a changed session_id for the same pid replaces the content and leaves no .tmp', () => {
    const { env, liveDir } = freshEnv();
    assertSilent(run({ env, input: payload() }));
    const second = '9b1d0a52-0000-4000-8000-000000000001';
    assertSilent(run({
      env,
      input: payload({
        session_id: second,
        transcript_path: `/home/u/.claude/projects/-work-proj/${second}.jsonl`,
        hook_event_name: 'UserPromptSubmit',
      }),
    }));
    assert.deepEqual(markerFiles(liveDir), ['claude-code-4242.json']);
    const marker = JSON.parse(fs.readFileSync(path.join(liveDir, 'claude-code-4242.json'), 'utf8'));
    assert.equal(marker.session_id, second);
    assert.ok(marker.transcript_path.endsWith(`${second}.jsonl`));
  });

  test('a cwd with a newline and unicode is stored verbatim', () => {
    const { env, liveDir } = freshEnv();
    const cwd = '/work/pro\nj/caf\u00e9 \u{1F600}';
    assertSilent(run({ env, input: payload({ cwd }) }));
    const marker = JSON.parse(fs.readFileSync(path.join(liveDir, 'claude-code-4242.json'), 'utf8'));
    assert.equal(marker.cwd, cwd);
  });

  test('buildMarker takes a Date or milliseconds for now', () => {
    const a = buildMarker(payload(), 7, new Date(Date.UTC(2026, 10, 24, 10, 0, 0, 5)));
    const b = buildMarker(payload(), 7, Date.UTC(2026, 10, 24, 10, 0, 0, 5));
    assert.equal(a.at, '2026-11-24T10:00:00.005Z');
    assert.deepEqual(a, b);
  });
});

describe('payload rules', () => {
  const refusedPayloads = [
    ['missing transcript_path', (p) => { delete p.transcript_path; }],
    ['relative transcript_path', (p) => { p.transcript_path = 'projects/x/t.jsonl'; }],
    ['transcript_path with NUL', (p) => { p.transcript_path = '/x/t\u0000.jsonl'; }],
    ['transcript_path of 4097 characters', (p) => { p.transcript_path = `/${'a'.repeat(4096)}`; }],
    ['transcript_path that is not a string', (p) => { p.transcript_path = 5; }],
    ['missing cwd', (p) => { delete p.cwd; }],
    ['relative cwd', (p) => { p.cwd = 'work/proj'; }],
    ['cwd with NUL', (p) => { p.cwd = '/work\u0000'; }],
    ['missing session_id', (p) => { delete p.session_id; }],
    ['session_id that is not a string', (p) => { p.session_id = 12; }],
    ...CASES.refused.map((id) => [`refused session_id ${JSON.stringify(id.slice(0, 20))} (${id.length})`, (p) => { p.session_id = id; }]),
  ];

  for (const [name, mutate] of refusedPayloads) {
    test(`refuses: ${name}`, () => {
      const { env, liveDir } = freshEnv();
      const p = payload();
      mutate(p);
      assertSilent(run({ env, input: p }));
      assert.deepEqual(markerFiles(liveDir), []);
    });
  }

  test('a transcript_path of exactly 4096 characters is accepted', () => {
    const { env, liveDir } = freshEnv();
    assertSilent(run({ env, input: payload({ transcript_path: `/${'a'.repeat(4095)}` }) }));
    assert.deepEqual(markerFiles(liveDir), ['claude-code-4242.json']);
  });

  for (const id of CASES.accepted) {
    test(`writes for the accepted session_id ${JSON.stringify(id.slice(0, 20))} (${id.length})`, () => {
      const { env, liveDir } = freshEnv();
      assertSilent(run({ env, input: payload({ session_id: id }) }));
      const marker = JSON.parse(fs.readFileSync(path.join(liveDir, 'claude-code-4242.json'), 'utf8'));
      assert.equal(marker.session_id, id);
    });
  }

  for (const input of [null, [], 'x', 5]) {
    test(`refuses a payload that is ${JSON.stringify(input)}`, () => {
      const { env, liveDir } = freshEnv();
      assertSilent(run({ env, input: JSON.stringify(input) }));
      assert.deepEqual(markerFiles(liveDir), []);
    });
  }
});

describe('shared session-id list', () => {
  for (const id of CASES.accepted) {
    test(`accepts ${JSON.stringify(id.slice(0, 20))} (${id.length})`, () => {
      assert.equal(isValidSessionId(id), true);
    });
  }
  for (const id of CASES.refused) {
    test(`refuses ${JSON.stringify(id.slice(0, 20))} (${id.length})`, () => {
      assert.equal(isValidSessionId(id), false);
    });
  }
  test('refuses a non-string', () => {
    for (const v of [undefined, null, 1, {}, []]) assert.equal(isValidSessionId(v), false);
  });
});

describe('bad input never fails', () => {
  test('garbage stdin', () => {
    const { env, liveDir } = freshEnv();
    assertSilent(run({ env, input: '\u0000{not json' }));
    assertSilent(run({ env, input: '' }));
    assert.deepEqual(markerFiles(liveDir), []);
  });

  test('a 65 KiB stdin', () => {
    const { env, liveDir } = freshEnv();
    const big = JSON.stringify(payload({ prompt: 'x'.repeat(65 * 1024) }));
    assert.ok(big.length > 64 * 1024);
    assertSilent(run({ env, input: big }));
    assert.deepEqual(markerFiles(liveDir), []);
  });

  test('a stdin that is never closed exits 0 within 3 s', async () => {
    const { env, liveDir } = freshEnv();
    const started = Date.now();
    const child = spawn(process.execPath, [SCRIPT, '4242'], { env, stdio: ['pipe', 'pipe', 'pipe'] });
    let out = '';
    child.stdout.on('data', (c) => { out += c; });
    child.stderr.on('data', (c) => { out += c; });
    child.stdin.on('error', () => {});
    child.stdin.write('{"session_id":');
    const code = await new Promise((resolve) => child.once('exit', resolve));
    assert.equal(code, 0);
    assert.ok(Date.now() - started < 3000, 'took too long');
    assert.equal(out, '');
    assert.deepEqual(markerFiles(liveDir), []);
    child.stdin.destroy();
  });

  for (const arg of ['0', '-1', '012', '2147483648', 'abc', '', '12 3', '1e3']) {
    test(`a host pid argument of ${JSON.stringify(arg)} writes nothing`, () => {
      const { env, liveDir } = freshEnv();
      assertSilent(run({ env, input: payload(), args: [arg] }));
      assert.deepEqual(markerFiles(liveDir), []);
    });
  }

  test('no host pid argument writes nothing', () => {
    const { env, liveDir } = freshEnv();
    assertSilent(run({ env, input: payload(), args: [] }));
    assert.deepEqual(markerFiles(liveDir), []);
  });

  test('the largest pid, 2147483647, is accepted', () => {
    const { env, liveDir } = freshEnv();
    assertSilent(run({ env, input: payload(), args: ['2147483647'] }));
    assert.deepEqual(markerFiles(liveDir), ['claude-code-2147483647.json']);
  });

  test('no data directory in the environment writes nothing', () => {
    const { env } = freshEnv();
    const bare = { PATH: env.PATH };
    assertSilent(run({ env: bare, input: payload() }));
  });

  test('a relative TOTAL_RECALL_HOME writes nothing', () => {
    const { env } = freshEnv();
    const cwd = fs.mkdtempSync(path.join(root, 'rel-'));
    const r = spawnSync(process.execPath, [SCRIPT, '4242'], {
      env: { ...env, TOTAL_RECALL_HOME: 'relative-dir' }, cwd, input: JSON.stringify(payload()), encoding: 'utf8',
    });
    assertSilent(r);
    assert.deepEqual(fs.readdirSync(cwd), []);
  });

  test('an unwritable live directory exits 0', { skip: !posix || process.getuid?.() === 0 ? 'needs a non-root POSIX user' : false }, () => {
    const { env, liveDir } = freshEnv();
    fs.mkdirSync(liveDir, { mode: 0o700 });
    fs.chmodSync(liveDir, 0o500);
    try {
      assertSilent(run({ env, input: payload() }));
      assert.deepEqual(markerFiles(liveDir), []);
    } finally {
      fs.chmodSync(liveDir, 0o700);
    }
  });

  test('a live path that is a file exits 0', () => {
    const { env, liveDir } = freshEnv();
    fs.writeFileSync(liveDir, 'not a directory');
    assertSilent(run({ env, input: payload() }));
  });
});

describe('resolveDataDir (the engine rule)', () => {
  const abs = path.resolve('/tmp/x');
  test('a non-empty TOTAL_RECALL_HOME as it is', () => {
    assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: abs, HOME: '/h' }), abs);
  });
  test('else .total-recall under HOME', () => {
    assert.equal(resolveDataDir({ HOME: abs }), path.join(abs, '.total-recall'));
    assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: '', HOME: abs }), path.join(abs, '.total-recall'));
  });
  test('an empty HOME that is set wins over USERPROFILE and is not absolute', () => {
    assert.equal(resolveDataDir({ HOME: '', USERPROFILE: abs }), null);
  });
  test('USERPROFILE only when HOME is unset', () => {
    assert.equal(resolveDataDir({ USERPROFILE: abs }), path.join(abs, '.total-recall'));
  });
  test('a relative result is refused', () => {
    assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: 'rel' }), null);
    assert.equal(resolveDataDir({ HOME: 'rel' }), null);
  });
  test('nothing set is null', () => {
    assert.equal(resolveDataDir({}), null);
  });
});

describe('pruning', () => {
  const day = 24 * 3600 * 1000;

  function touch(dir, name, { ageMs = 0, content = '{}' } = {}) {
    const file = path.join(dir, name);
    fs.writeFileSync(file, content);
    if (ageMs) {
      const t = new Date(Date.now() - ageMs);
      fs.utimesSync(file, t, t);
    }
    return file;
  }

  function liveDirFor() {
    const { liveDir } = freshEnv();
    fs.mkdirSync(liveDir, { recursive: true });
    return liveDir;
  }

  test('prune removes the marker of a dead pid', async () => {
    const liveDir = liveDirFor();
    const pid = await deadPid();
    touch(liveDir, `claude-code-${pid}.json`);
    prune({ liveDir, now: Date.now(), selfName: 'claude-code-4242.json' });
    assert.deepEqual(markerFiles(liveDir), []);
  });

  test('prune keeps a live pid', () => {
    const liveDir = liveDirFor();
    touch(liveDir, `claude-code-${process.pid}.json`);
    prune({ liveDir, now: Date.now(), selfName: 'claude-code-4242.json' });
    assert.deepEqual(markerFiles(liveDir), [`claude-code-${process.pid}.json`]);
  });

  test('prune removes a marker older than 30 days even for a live pid', () => {
    const liveDir = liveDirFor();
    touch(liveDir, `claude-code-${process.pid}.json`, { ageMs: 31 * day });
    prune({ liveDir, now: Date.now(), selfName: 'claude-code-4242.json' });
    assert.deepEqual(markerFiles(liveDir), []);
  });

  test('prune keeps a live marker younger than 30 days', () => {
    const liveDir = liveDirFor();
    touch(liveDir, `claude-code-${process.pid}.json`, { ageMs: 29 * day });
    prune({ liveDir, now: Date.now(), selfName: 'claude-code-4242.json' });
    assert.equal(markerFiles(liveDir).length, 1);
  });

  test('prune never removes enabled, its own file or a foreign name', async () => {
    const liveDir = liveDirFor();
    const dead = await deadPid();
    const old = 90 * day;
    const names = [
      'enabled', 'notes.txt', 'claude-code-abc.json', 'claude-code-123456789012.json',
      'other-4242.json', 'claude-code-4242.json.bak',
    ];
    for (const n of names) touch(liveDir, n, { ageMs: old });
    touch(liveDir, `claude-code-${dead}.json`);
    // its own name, even when the pid is dead
    const self = `claude-code-${dead + 1000000}.json`;
    touch(liveDir, self, { ageMs: old });
    prune({ liveDir, now: Date.now(), selfName: self });
    assert.deepEqual(markerFiles(liveDir), [...names, self].sort());
  });

  test('prune does not touch a directory with a matching name', async () => {
    const liveDir = liveDirFor();
    const pid = await deadPid();
    fs.mkdirSync(path.join(liveDir, `claude-code-${pid}.json`));
    assert.doesNotThrow(() => prune({ liveDir, now: Date.now(), selfName: 'x' }));
    assert.equal(markerFiles(liveDir).length, 1);
  });

  test('prune looks at no more than 200 names', () => {
    const liveDir = liveDirFor();
    // pids above any real pid_max: dead
    for (let i = 0; i < 250; i++) touch(liveDir, `claude-code-${2000000000 + i}.json`);
    prune({ liveDir, now: Date.now(), selfName: 'claude-code-4242.json' });
    assert.equal(markerFiles(liveDir).length, 50);
  });

  test('prune removes a stale tmp over one hour old and keeps a fresh one', () => {
    const liveDir = liveDirFor();
    touch(liveDir, 'claude-code-4242.json.123.abcd.tmp', { ageMs: 2 * 3600 * 1000 });
    touch(liveDir, 'claude-code-4242.json.124.ef01.tmp', { ageMs: 5 * 60 * 1000 });
    touch(liveDir, 'unrelated.tmp', { ageMs: 2 * 3600 * 1000 });
    prune({ liveDir, now: Date.now(), selfName: 'claude-code-1.json' });
    assert.deepEqual(markerFiles(liveDir), ['claude-code-4242.json.124.ef01.tmp', 'unrelated.tmp']);
  });

  test('prune on a missing directory does not throw', () => {
    assert.doesNotThrow(() => prune({ liveDir: path.join(root, 'nope'), now: Date.now(), selfName: 'x' }));
  });

  test('a SessionStart run prunes a dead pid (end to end), writing its own file', async () => {
    const { env, liveDir } = freshEnv();
    fs.mkdirSync(liveDir, { recursive: true });
    const pid = await deadPid();
    touch(liveDir, `claude-code-${pid}.json`);
    touch(liveDir, 'enabled');
    assertSilent(run({ env, input: payload({ hook_event_name: 'SessionStart' }) }));
    assert.deepEqual(markerFiles(liveDir), ['claude-code-4242.json', 'enabled']);
  });

  test('prune runs only on SessionStart', async () => {
    const { env, liveDir } = freshEnv();
    fs.mkdirSync(liveDir, { recursive: true });
    const pid = await deadPid();
    touch(liveDir, `claude-code-${pid}.json`);
    assertSilent(run({ env, input: payload({ hook_event_name: 'UserPromptSubmit' }) }));
    assert.deepEqual(markerFiles(liveDir), [`claude-code-${pid}.json`, 'claude-code-4242.json'].sort());
  });
});
