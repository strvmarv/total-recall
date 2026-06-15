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

test('never-drop: not_ready before engine, proxied after; one open stream', async () => {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const out = collect(stdout);

  // Gate provisioning so we can observe the not_ready window deterministically.
  let release;
  const gate = new Promise((r) => { release = r; });

  const shim = runShim({
    stdin, stdout, catalog, version: '3.2.0',
    serverName: 'total-recall', serverVersion: '3.2.0', protocolVersion: '2024-11-05',
    provision: async () => { await gate; return { ok: true }; },
    engineCommand: process.execPath, engineArgs: [fake],
  });

  // initialize -> answered instantly by the shim
  stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize' }) + '\n');
  await new Promise((r) => setTimeout(r, 20));
  assert.equal(out.find((m) => m.id === 1).result.capabilities.tools.listChanged, true);

  // tools/call BEFORE provisioning completes -> not_ready
  stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'memory_store' } }) + '\n');
  await new Promise((r) => setTimeout(r, 20));
  const early = out.find((m) => m.id === 2);
  assert.equal(JSON.parse(early.result.content[0].text).status, 'not_ready');

  // complete provisioning -> engine starts + handshakes
  // On Windows, spawning a Node child process can take 300-500 ms;
  // use a generous window so the test is reliable without weakening assertions.
  release();
  await new Promise((r) => setTimeout(r, 600));

  // tools/call AFTER ready -> proxied to the (fake) engine
  stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'memory_store' } }) + '\n');
  await new Promise((r) => setTimeout(r, 200));
  const late = out.find((m) => m.id === 3);
  assert.equal(late.result.content[0].text, 'engine-ok');

  // listChanged notification emitted once engine became ready
  assert.ok(out.some((m) => m.method === 'notifications/tools/list_changed'));

  shim.stop();
});
