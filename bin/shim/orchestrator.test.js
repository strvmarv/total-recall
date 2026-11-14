import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PassThrough } from 'node:stream';
import { runShim, PROVISION_RETRY_DELAYS_MS } from './orchestrator.js';

const fake = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'fake-engine.js');
const catalog = { tools: [{ name: 'memory_store', description: 'x', inputSchema: { type: 'object' } }] };

function collect(stream) {
  const msgs = [];
  let buf = '';
  stream.on('data', (b) => {
    buf += b.toString();
    let i;
    while ((i = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
      if (line) msgs.push(JSON.parse(line));
    }
  });
  return msgs;
}

// Poll until a condition holds rather than sleeping a fixed window. Engine
// readiness depends on child-process spawn + handshake latency (300-500 ms on
// Windows, variable under CI load); polling keeps the test deterministic and
// fast without weakening any assertion.
async function waitFor(predicate, timeoutMs = 5000, stepMs = 10) {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > timeoutMs) throw new Error(`waitFor: condition not met within ${timeoutMs}ms`);
    await new Promise((r) => setTimeout(r, stepMs));
  }
}

test('never-drop: not_ready before engine, proxied after; one open stream', async () => {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const out = collect(stdout);

  // Gate provisioning so we can observe the not_ready window deterministically.
  let release;
  const gate = new Promise((r) => { release = r; });

  const shim = runShim({
    stdin, stdout, catalog,
    serverName: 'total-recall', serverVersion: '3.2.0', protocolVersion: '2024-11-05',
    provision: async () => { await gate; return { ok: true }; },
    engineCommand: process.execPath, engineArgs: [fake],
  });

  // initialize -> answered instantly by the shim (before any engine work)
  stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize' }) + '\n');
  await waitFor(() => out.some((m) => m.id === 1));
  assert.equal(out.find((m) => m.id === 1).result.capabilities.tools.listChanged, true);

  // tools/call BEFORE provisioning completes -> not_ready (gate still closed)
  stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'memory_store' } }) + '\n');
  await waitFor(() => out.some((m) => m.id === 2));
  const early = out.find((m) => m.id === 2);
  assert.equal(JSON.parse(early.result.content[0].text).status, 'not_ready');

  // complete provisioning -> engine starts + handshakes; wait for the readiness
  // signal (listChanged) rather than a fixed sleep so spawn latency can't flake.
  release();
  await waitFor(() => out.some((m) => m.method === 'notifications/tools/list_changed'));

  // tools/call AFTER ready -> proxied to the (fake) engine
  stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'memory_store' } }) + '\n');
  await waitFor(() => out.some((m) => m.id === 3));
  const late = out.find((m) => m.id === 3);
  assert.equal(late.result.content[0].text, 'engine-ok');

  shim.stop();
});

// --- Provisioning retry (#87) -----------------------------------------------------------------

const sink = () => ({ write() {} });
const RETRYABLE = (error = 'boom') => ({ ok: false, error, retryable: true });

// Builds a shim whose `provision` is scripted: each call takes the next step (a result, a function
// returning a result or a promise, or an Error to throw). Returns the shim, the harness-side
// messages and the call counter.
function scripted(steps, { delays, ...extra } = {}) {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const out = collect(stdout);
  const calls = { n: 0, onProgress: [] };
  const shim = runShim({
    stdin, stdout, catalog, stderr: sink(),
    serverName: 'total-recall', serverVersion: '3.2.0', protocolVersion: '2024-11-05',
    provision: async (onProgress) => {
      const step = steps[Math.min(calls.n, steps.length - 1)];
      calls.n += 1;
      calls.onProgress.push(onProgress);
      if (step instanceof Error) throw step;
      return typeof step === 'function' ? step() : step;
    },
    engineCommand: process.execPath, engineArgs: [fake],
    provisionRetryDelaysMs: delays,
    ...extra,
  });
  let id = 100;
  const call = async () => {
    const mine = ++id;
    stdin.write(JSON.stringify({ jsonrpc: '2.0', id: mine, method: 'tools/call', params: { name: 'memory_store' } }) + '\n');
    await waitFor(() => out.some((m) => m.id === mine));
    return out.find((m) => m.id === mine).result;
  };
  return { shim, out, calls, call };
}

const settle = (ms = 120) => new Promise((r) => setTimeout(r, ms));

test('retry: a retryable failure is retried by itself and the engine starts once a try succeeds', async () => {
  const { shim, out, calls, call } = scripted([RETRYABLE(), RETRYABLE(), { ok: true }], { delays: [5, 5, 5] });
  await waitFor(() => out.some((m) => m.method === 'notifications/tools/list_changed'));
  assert.equal(calls.n, 3);
  assert.equal((await call()).content[0].text, 'engine-ok');
  shim.stop();
});

test('retry: a call while waiting for the next try gets phase provisioning with the attempt and the last error', async () => {
  const { shim, calls, call } = scripted([RETRYABLE('HTTP 404 for manifest')], { delays: [60_000] });
  await waitFor(() => calls.n === 1 && shim._state.detail.nextRetryAt !== undefined);
  const payload = JSON.parse((await call()).content[0].text);
  assert.equal(payload.status, 'not_ready');
  assert.equal(payload.phase, 'provisioning');
  assert.equal(payload.attempt, 1);
  assert.equal(payload.error, 'HTTP 404 for manifest');
  assert.ok(payload.retryInSeconds > 0 && payload.retryInSeconds <= 60, `retryInSeconds ${payload.retryInSeconds}`);
  assert.equal('nextRetryAt' in payload, false);
  assert.match(payload.hint, /try again by itself/);
  shim.stop();
});

test('retry: a running retry has no nextRetryAt before any progress tick; a tick keeps attempt and error', async () => {
  let release;
  const gate = new Promise((r) => { release = r; });
  const { shim, calls } = scripted([RETRYABLE('first failure'), () => gate.then(() => ({ ok: true }))], { delays: [5] });
  await waitFor(() => calls.n === 2);
  assert.equal(shim._state.phase, 'provisioning');
  assert.equal(shim._state.detail.attempt, 2);
  assert.equal(shim._state.detail.error, 'first failure');
  assert.equal(shim._state.detail.nextRetryAt, undefined);
  calls.onProgress[1]({ bytes: 10, total: 100, phase: 'downloading' });
  assert.equal(shim._state.detail.attempt, 2);
  assert.equal(shim._state.detail.error, 'first failure');
  assert.equal(shim._state.detail.bytes, 10);
  release();
  shim.stop();
});

test('retry: a call during a running retry keeps phase provisioning when a real progress tick arrives', async () => {
  let release;
  const gate = new Promise((r) => { release = r; });
  const { shim, calls, call } = scripted([RETRYABLE('first failure'), () => gate.then(() => ({ ok: true }))], { delays: [5] });
  await waitFor(() => calls.n === 2);
  calls.onProgress[1]({ bytes: 10, total: 100, phase: 'downloading' });
  const payload = JSON.parse((await call()).content[0].text);
  assert.equal(payload.phase, 'provisioning');
  assert.equal(payload.attempt, 2);
  assert.equal(payload.error, 'first failure');
  release();
  shim.stop();
});

test('retry: a non-retryable failure is not retried', async () => {
  const { shim, calls, call } = scripted([{ ok: false, error: 'checksum mismatch', retryable: false }], { delays: [5, 5] });
  await waitFor(() => shim._state.phase === 'provisioning-failed');
  await settle();
  assert.equal(calls.n, 1);
  assert.equal(shim._state.detail.error, 'checksum mismatch');
  assert.equal(JSON.parse((await call()).content[0].text).phase, 'provisioning-failed');
  shim.stop();
});

test('retry: a null result is the non-retryable unknown failure', async () => {
  const { shim, calls } = scripted([null], { delays: [5] });
  await waitFor(() => shim._state.phase === 'provisioning-failed');
  await settle();
  assert.equal(calls.n, 1);
  assert.equal(shim._state.detail.error, 'unknown');
  assert.equal(shim._state.detail.retryable, false);
  shim.stop();
});

test('retry: a throw from provision is a retryable failure', async () => {
  const { shim, out, calls } = scripted([new Error('socket hang up'), { ok: true }], { delays: [5] });
  await waitFor(() => out.some((m) => m.method === 'notifications/tools/list_changed'));
  assert.equal(calls.n, 2);
  shim.stop();
});

test('retry: when every delay is used the shim gives up with the last error and tries no more', async () => {
  const { shim, calls, call } = scripted([RETRYABLE('first'), RETRYABLE('second'), RETRYABLE('last')], { delays: [5, 5] });
  await waitFor(() => shim._state.phase === 'provisioning-failed');
  await settle();
  assert.equal(calls.n, 3);
  assert.equal(shim._state.detail.error, 'last');
  assert.equal(shim._state.detail.gaveUp, true);
  assert.equal(shim._state.detail.retryable, false);
  const payload = JSON.parse((await call()).content[0].text);
  assert.equal(payload.phase, 'provisioning-failed');
  assert.equal(payload.gaveUp, true);
  assert.match(payload.hint, /will not try again/);
  shim.stop();
});

test('retry: stop() while waiting leaves no pending try', async () => {
  const { shim, calls } = scripted([RETRYABLE()], { delays: [30] });
  await waitFor(() => calls.n === 1 && shim._state.detail.nextRetryAt !== undefined);
  shim.stop();
  await settle(150);
  assert.equal(calls.n, 1);
});

test('retry: stop() while a try is running starts no engine and schedules nothing, even if the try then succeeds', async () => {
  let release;
  const gate = new Promise((r) => { release = r; });
  const { shim, out, calls } = scripted([() => gate.then(() => ({ ok: true }))], { delays: [5] });
  await waitFor(() => calls.n === 1);
  shim.stop();
  release();
  await settle(150);
  assert.notEqual(shim._state.phase, 'starting-engine');
  assert.notEqual(shim._state.phase, 'proxying');
  assert.equal(out.some((m) => m.method === 'notifications/tools/list_changed'), false);
});

test('retry: stop() while a try is running and then fails retryably schedules no further try', async () => {
  let release;
  const gate = new Promise((r) => { release = r; });
  const { shim, calls } = scripted([() => gate.then(() => RETRYABLE())], { delays: [5, 5] });
  await waitFor(() => calls.n === 1);
  shim.stop();
  release();
  await settle(150);
  assert.equal(calls.n, 1);
});

test('retry: an unexpected throw outside provision is a final failure, not a retry', async () => {
  const bad = { get ok() { throw new Error('unexpected'); } };
  const { shim, calls } = scripted([bad], { delays: [5, 5] });
  await waitFor(() => shim._state.phase === 'provisioning-failed');
  await settle();
  assert.equal(calls.n, 1);
  assert.equal(shim._state.detail.error, 'unexpected');
  assert.equal(shim._state.detail.retryable, false);
  shim.stop();
});

test('retry: the default schedule starts 5, 10, 20, 40, 60 seconds and adds up to 1215 seconds', () => {
  assert.deepEqual(PROVISION_RETRY_DELAYS_MS.slice(0, 5), [5000, 10_000, 20_000, 40_000, 60_000]);
  assert.equal(PROVISION_RETRY_DELAYS_MS.length, 23);
  assert.equal(PROVISION_RETRY_DELAYS_MS.reduce((a, b) => a + b, 0), 1215_000);
});
