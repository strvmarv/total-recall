import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PassThrough } from 'node:stream';
import { runShim } from './orchestrator.js';

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
