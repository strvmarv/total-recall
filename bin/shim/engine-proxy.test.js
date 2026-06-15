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

test('crash after handshake fires onExit and synthesizes not_ready for pending ids', async () => {
  const forwarded = [];
  let exited = false;
  const proxy = new EngineProxy({ command: process.execPath, args: [fake, '--crash-after-initialize'], stderr: process.stderr });
  proxy.onForward = (m) => forwarded.push(m);
  proxy.onExit = () => { exited = true; };
  // handshake succeeds, then the engine exits(1) immediately.
  await proxy.startAndHandshake().catch(() => {});
  await new Promise((r) => setTimeout(r, 50));
  assert.equal(exited, true);
});

test('hang on initialize rejects the handshake within the timeout', async () => {
  const proxy = new EngineProxy({ command: process.execPath, args: [fake, '--hang-initialize'], stderr: process.stderr, handshakeTimeoutMs: 200 });
  proxy.onForward = () => {};
  await assert.rejects(() => proxy.startAndHandshake());
  proxy.stop();
});
