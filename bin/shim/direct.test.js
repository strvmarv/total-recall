import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { isServeInvocation, runDirect } from './direct.js';

// A stderr sink that records what was written.
function sink() {
  const s = { text: '' };
  s.write = (chunk) => { s.text += chunk; return true; };
  return s;
}

// A fake child process: EventEmitter + a kill() spy.
function fakeChild() {
  const c = new EventEmitter();
  c.killed = [];
  c.kill = (sig) => { c.killed.push(sig); return true; };
  return c;
}

test('isServeInvocation: bare + serve are serve; everything else is not', () => {
  assert.equal(isServeInvocation([]), true);
  assert.equal(isServeInvocation(['serve']), true);
  assert.equal(isServeInvocation(['serve', '--foo']), true);
  assert.equal(isServeInvocation(['ui']), false);
  assert.equal(isServeInvocation(['ui', '--port', '5577']), false);
  assert.equal(isServeInvocation(['reindex-embeddings']), false);
  assert.equal(isServeInvocation(['dump-catalog']), false);
});

test('runDirect: provisioning failure -> onExit(1), no spawn', async () => {
  const stderr = sink();
  let spawned = false;
  const codes = [];
  await runDirect({
    args: ['ui'],
    provision: async () => ({ ok: false, error: 'no artifact' }),
    spawn: () => { spawned = true; return fakeChild(); },
    stderr,
    onExit: (code) => codes.push(code),
    registerSignals: false,
  });
  assert.equal(spawned, false);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /could not provision engine: no artifact/);
});

test('runDirect: provision returning null -> onExit(1) with "unknown"', async () => {
  const stderr = sink();
  const codes = [];
  const child = await runDirect({
    args: ['ui'],
    provision: async () => null,
    spawn: () => fakeChild(),
    stderr,
    onExit: (code) => codes.push(code),
    registerSignals: false,
  });
  assert.equal(child, undefined);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /could not provision engine: unknown/);
});

test('runDirect: provisioned -> spawns binary with args + inherited stdio, returns child', async () => {
  const stderr = sink();
  let call = null;
  const child = fakeChild();
  const returned = await runDirect({
    args: ['ui', '--port', '5577'],
    provision: async () => ({ ok: true, binaryPath: '/path/to/total-recall.exe' }),
    spawn: (cmd, args, opts) => { call = { cmd, args, opts }; return child; },
    stderr,
    onExit: () => {},
    registerSignals: false,
  });
  assert.equal(call.cmd, '/path/to/total-recall.exe');
  assert.deepEqual(call.args, ['ui', '--port', '5577']);
  assert.deepEqual(call.opts, { stdio: 'inherit' });
  assert.equal(returned, child);
});

test('runDirect: registerSignals forwards SIGINT/SIGTERM to the child', async () => {
  const child = fakeChild();
  const proc = new EventEmitter();   // stand-in for `process`
  await runDirect({
    args: ['ui'],
    provision: async () => ({ ok: true, binaryPath: 'bin' }),
    spawn: () => child,
    onExit: () => {},
    registerSignals: true,
    proc,
  });
  proc.emit('SIGINT');
  proc.emit('SIGTERM');
  assert.deepEqual(child.killed, ['SIGINT', 'SIGTERM']);
});

test('runDirect: failed spawn fires error THEN exit(null,null) -> onExit called once with 1', async () => {
  const codes = [];
  const child = fakeChild();
  await runDirect({
    args: ['ui'],
    provision: async () => ({ ok: true, binaryPath: 'bin' }),
    spawn: () => child,
    stderr: sink(),
    onExit: (code) => codes.push(code),
    registerSignals: false,
  });
  // Node's documented sequence for an un-spawnable child:
  child.emit('error', new Error('ENOENT'));
  child.emit('exit', null, null);
  assert.deepEqual(codes, [1]); // NOT [1, 0]
});

test('runDirect: mirrors the child exit code', async () => {
  const codes = [];
  const child = fakeChild();
  await runDirect({
    args: ['reindex-embeddings'],
    provision: async () => ({ ok: true, binaryPath: 'bin' }),
    spawn: () => child,
    onExit: (code) => codes.push(code),
    registerSignals: false,
  });
  child.emit('exit', 7, null);
  assert.deepEqual(codes, [7]);
});

test('runDirect: clean exit (code 0) mirrors 0', async () => {
  const codes = [];
  const child = fakeChild();
  await runDirect({
    args: ['ui'],
    provision: async () => ({ ok: true, binaryPath: 'bin' }),
    spawn: () => child,
    onExit: (code) => codes.push(code),
    registerSignals: false,
  });
  child.emit('exit', 0, null);
  assert.deepEqual(codes, [0]);
});

test('runDirect: signal death -> nonzero exit', async () => {
  const calls = [];
  const child = fakeChild();
  await runDirect({
    args: ['ui'],
    provision: async () => ({ ok: true, binaryPath: 'bin' }),
    spawn: () => child,
    onExit: (code, signal) => calls.push([code, signal]),
    registerSignals: false,
  });
  child.emit('exit', null, 'SIGTERM');
  assert.deepEqual(calls, [[1, 'SIGTERM']]);
});

test('runDirect: spawn error -> onExit(1) + diagnostic', async () => {
  const stderr = sink();
  const codes = [];
  const child = fakeChild();
  await runDirect({
    args: ['ui'],
    provision: async () => ({ ok: true, binaryPath: 'bin' }),
    spawn: () => child,
    stderr,
    onExit: (code) => codes.push(code),
    registerSignals: false,
  });
  child.emit('error', new Error('ENOENT'));
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /failed to start engine: ENOENT/);
});
