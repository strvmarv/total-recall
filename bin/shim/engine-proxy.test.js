import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { EngineProxy } from './engine-proxy.js';

const fake = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fixtures', 'fake-engine.js');

test('handshake completes and tools/call is forwarded and returned', async () => {
  const forwarded = [];
  const proxy = new EngineProxy({ command: process.execPath, args: [fake], stderr: process.stderr });
  proxy.onForward = (m) => forwarded.push(m);
  await proxy.startAndHandshake();
  proxy.send({ jsonrpc: '2.0', id: 100, method: 'tools/call', params: { name: 'x' } });
  await new Promise((r) => setTimeout(r, 50));
  const resp = forwarded.find((m) => m.id === 100);
  assert.ok(resp, 'engine response forwarded to harness');
  assert.equal(resp.result.content[0].text, 'engine-ok');
  proxy.stop();
});

test('pendingIds tracks requests, clears on response, ignores notifications and handshake', async () => {
  const proxy = new EngineProxy({ command: process.execPath, args: [fake], stderr: process.stderr });
  proxy.onForward = () => {};
  await proxy.startAndHandshake();

  // The reserved handshake id must never leak into the pending set.
  assert.ok(!proxy.pendingIds().includes('__shim_init__'));

  // Notification (no id) -> not tracked.
  proxy.send({ jsonrpc: '2.0', method: 'notifications/foo' });
  assert.deepEqual(proxy.pendingIds(), []);

  // Request (with id) -> tracked until its response arrives.
  proxy.send({ jsonrpc: '2.0', id: 42, method: 'tools/call', params: { name: 'x' } });
  assert.ok(proxy.pendingIds().includes(42));

  // After the engine answers, the id is cleared.
  await new Promise((r) => setTimeout(r, 50));
  assert.deepEqual(proxy.pendingIds(), []);

  proxy.stop();
});

test('stderrTail captures engine diagnostics and passes them through', async () => {
  let passthrough = '';
  const sink = { write: (s) => { passthrough += s; } };
  const proxy = new EngineProxy({ command: process.execPath, args: [fake, '--log-stderr'], stderr: sink });
  proxy.onForward = () => {};
  await proxy.startAndHandshake();
  await new Promise((r) => setTimeout(r, 50));
  assert.match(proxy.stderrTail(), /engine-diag-line/);
  assert.match(passthrough, /engine-diag-line/);
  proxy.stop();
});

test('crash after handshake surfaces as onExit OR a rejected handshake', async () => {
  const forwarded = [];
  let exited = false;
  let rejected = false;
  const proxy = new EngineProxy({ command: process.execPath, args: [fake, '--crash-after-initialize'], stderr: process.stderr });
  proxy.onForward = (m) => forwarded.push(m);
  proxy.onExit = () => { exited = true; };
  // The fake answers initialize then exits(1) immediately. Two valid races:
  //   - stdout wins  -> handshake resolves, then exit -> onExit fires
  //   - exit wins    -> handshake rejects before resolve
  // Both prove the crash surfaced; the orchestrator's .catch routes a rejected
  // handshake through the same exit handling, so either outcome is correct.
  await proxy.startAndHandshake().catch(() => { rejected = true; });
  // Poll briefly for the post-resolve exit case rather than a fixed sleep.
  const start = Date.now();
  while (!exited && !rejected && Date.now() - start < 2000) {
    await new Promise((r) => setTimeout(r, 10));
  }
  assert.ok(exited || rejected, 'engine crash after init must surface as onExit or a rejected handshake');
  proxy.stop();
});

test('startAndHandshake twice throws rather than leaking a child', async () => {
  const proxy = new EngineProxy({ command: process.execPath, args: [fake], stderr: process.stderr });
  proxy.onForward = () => {};
  await proxy.startAndHandshake();
  assert.throws(() => proxy.startAndHandshake(), /called twice/);
  proxy.stop();
});

test('hang on initialize rejects the handshake within the timeout', async () => {
  const proxy = new EngineProxy({ command: process.execPath, args: [fake, '--hang-initialize'], stderr: process.stderr, handshakeTimeoutMs: 200 });
  proxy.onForward = () => {};
  await assert.rejects(() => proxy.startAndHandshake());
  proxy.stop();
});
