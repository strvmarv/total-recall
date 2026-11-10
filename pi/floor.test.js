import { test } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import { spawn as realSpawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveEngine, parseFloorOutput, runFloor } from './floor.js';

const fixture = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'floor-child.js');
const ENVELOPE = (ctx) => JSON.stringify({
  hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: ctx },
});

function engineDeps(over = {}) {
  const calls = [];
  const deps = {
    platform: 'win32',
    arch: 'x64',
    detectRid: (p, a) => { calls.push(['detectRid', p, a]); return 'win-x64'; },
    getBinaryPath: (rid) => { calls.push(['getBinaryPath', rid]); return `/root/binaries/${rid}/total-recall.exe`; },
    existsSync: () => true,
    payloadIntact: () => ({ ok: true }),
    ...over,
  };
  return { deps, calls };
}

// ---- resolveEngine ----

test('resolveEngine: returns the binary path when rid, file and payload are all good', () => {
  const { deps, calls } = engineDeps();
  assert.equal(resolveEngine(deps), '/root/binaries/win-x64/total-recall.exe');
  assert.deepEqual(calls[0], ['detectRid', 'win32', 'x64']);
});

test('resolveEngine (RF2): unsupported platform means null and getBinaryPath is never called', () => {
  const { deps, calls } = engineDeps({ detectRid: () => null });
  assert.equal(resolveEngine(deps), null);
  assert.equal(calls.some((c) => c[0] === 'getBinaryPath'), false);
});

test('resolveEngine (RF2): missing binary means null', () => {
  const { deps } = engineDeps({ existsSync: () => false });
  assert.equal(resolveEngine(deps), null);
});

test('resolveEngine (RF2): a failing payload check means null and checks the binary directory', () => {
  let dir;
  const { deps } = engineDeps({ payloadIntact: (d) => { dir = d; return { ok: false, reason: 'truncated' }; } });
  assert.equal(resolveEngine(deps), null);
  assert.equal(path.normalize(dir), path.normalize('/root/binaries/win-x64'));
});

test('resolveEngine: a throwing dependency means null, never an exception', () => {
  const { deps } = engineDeps({ existsSync: () => { throw new Error('boom'); } });
  assert.equal(resolveEngine(deps), null);
});

// ---- parseFloorOutput ----

test('parseFloorOutput: reads additionalContext from the claude-code envelope', () => {
  assert.equal(parseFloorOutput(ENVELOPE('PIN')), 'PIN');
  assert.equal(parseFloorOutput(ENVELOPE('PIN') + '\n'), 'PIN');
});

test('parseFloorOutput (RF1): a stray line before the envelope is tolerated', () => {
  assert.equal(parseFloorOutput('warn line\n' + ENVELOPE('PIN')), 'PIN');
});

test('parseFloorOutput (RF1): empty, no-op, non-object and blank outputs give undefined', () => {
  for (const out of ['', '   \n', '{}', 'null', '[]', 'not json', '"str"', ENVELOPE('   '), ENVELOPE('')]) {
    assert.equal(parseFloorOutput(out), undefined, JSON.stringify(out));
  }
});

test('parseFloorOutput (RF1): a non-string additionalContext gives undefined', () => {
  assert.equal(parseFloorOutput('{"hookSpecificOutput":{"additionalContext":42}}'), undefined);
});

// ---- runFloor ----

const run = (mode, extra = {}) => runFloor({
  binaryPath: process.execPath,
  args: [fixture, mode],
  payload: { session_id: 'sid-1', cwd: '/proj' },
  ...extra,
});

test('runFloor: payload reaches the child on stdin (stdin is ended) and the envelope is parsed', async () => {
  assert.equal(await run('ok'), 'sid-1');
});

test('runFloor (RF1): stray stdout and stderr noise do not break parsing', async () => {
  assert.equal(await run('warn'), 'sid-1');
});

test('runFloor (RF1): a non-zero exit yields undefined even when it printed an envelope', async () => {
  assert.equal(await run('exit1'), undefined);
});

test('runFloor (RF1): garbage output yields undefined', async () => {
  assert.equal(await run('garbage'), undefined);
});

test('runFloor: multi-byte characters split across stdout chunks survive intact', async () => {
  assert.equal(await run('utf8'), 'pin \u2014 \ud83d\ude00 end');
});

test('runFloor: a hung child is killed at the timeout and the result is undefined', async () => {
  let child;
  const spawn = (...a) => { child = realSpawn(...a); return child; };
  const t0 = Date.now();
  const out = await run('hang', { timeoutMs: 200, spawn });
  assert.equal(out, undefined);
  assert.ok(Date.now() - t0 < 2000, 'did not wait for the child');
  if (child.exitCode === null && child.signalCode === null) await once(child, 'close');
  assert.ok(child.exitCode !== null || child.signalCode !== null, 'child is dead');
});

test('runFloor (RF2): a spawn that throws synchronously, and a missing binary, resolve undefined', async () => {
  assert.equal(await run('ok', { spawn: () => { throw new Error('no spawn'); } }), undefined);
  assert.equal(await runFloor({ binaryPath: path.join(path.dirname(fixture), 'does-not-exist.exe'), payload: {} }), undefined);
});

test('runFloor: defaults to the pinned-floor claude-code invocation and a 10 s timeout', async () => {
  let seen;
  const spawn = (cmd, args, opts) => {
    seen = { cmd, args, opts };
    return realSpawn(process.execPath, [fixture, 'ok'], opts);
  };
  await runFloor({ binaryPath: 'X', payload: { session_id: 'sid-1' }, spawn });
  assert.equal(seen.cmd, 'X');
  assert.deepEqual(seen.args, ['pinned-floor', '--host', 'claude-code']);
  assert.deepEqual(seen.opts.stdio, ['pipe', 'pipe', 'ignore']);
  assert.equal(seen.opts.windowsHide, true);
});
