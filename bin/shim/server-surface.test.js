import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ServerSurface } from './server-surface.js';
import { ShimState } from './state.js';

const catalog = { tools: [{ name: 'memory_store', description: 'x', inputSchema: { type: 'object' } }] };
const surface = new ServerSurface({ catalog, serverName: 'total-recall', serverVersion: '3.2.0', protocolVersion: '2024-11-05' });

test('initialize is answered by the shim, advertising tools.listChanged', () => {
  const s = new ShimState();
  const out = surface.route({ jsonrpc: '2.0', id: 1, method: 'initialize' }, s);
  assert.equal(out.kind, 'respond');
  assert.equal(out.message.result.protocolVersion, '2024-11-05');
  assert.equal(out.message.result.serverInfo.name, 'total-recall');
  assert.equal(out.message.result.capabilities.tools.listChanged, true);
});

test('ping answered empty', () => {
  const out = surface.route({ jsonrpc: '2.0', id: 2, method: 'ping' }, new ShimState());
  assert.deepEqual(out.message.result, {});
});

test('tools/list before ready returns the static catalog', () => {
  const out = surface.route({ jsonrpc: '2.0', id: 3, method: 'tools/list' }, new ShimState());
  assert.equal(out.kind, 'respond');
  assert.deepEqual(out.message.result, catalog);
});

test('tools/list once proxying is proxied', () => {
  const s = new ShimState(); s.set('proxying');
  const out = surface.route({ jsonrpc: '2.0', id: 4, method: 'tools/list' }, s);
  assert.equal(out.kind, 'proxy');
});

test('tools/call before ready returns not_ready result', () => {
  const s = new ShimState(); s.set('provisioning', { pct: 10 });
  const out = surface.route({ jsonrpc: '2.0', id: 5, method: 'tools/call', params: { name: 'memory_store' } }, s);
  assert.equal(out.kind, 'respond');
  assert.equal(out.message.result.isError, true);
  assert.equal(JSON.parse(out.message.result.content[0].text).phase, 'provisioning');
});

test('tools/call once proxying is proxied', () => {
  const s = new ShimState(); s.set('proxying');
  const out = surface.route({ jsonrpc: '2.0', id: 6, method: 'tools/call', params: { name: 'memory_store' } }, s);
  assert.equal(out.kind, 'proxy');
});

test('shutdown is answered and signals shutdown', () => {
  const out = surface.route({ jsonrpc: '2.0', id: 7, method: 'shutdown' }, new ShimState());
  assert.equal(out.kind, 'shutdown');
  assert.deepEqual(out.message.result, {});
});

test('notifications/initialized from harness is dropped (engine gets its own)', () => {
  const out = surface.route({ jsonrpc: '2.0', method: 'notifications/initialized' }, new ShimState());
  assert.equal(out.kind, 'drop');
});

test('unknown method before ready is method-not-found', () => {
  const out = surface.route({ jsonrpc: '2.0', id: 8, method: 'resources/list' }, new ShimState());
  assert.equal(out.kind, 'respond');
  assert.equal(out.message.error.code, -32601);
});

// Regression guards: falsy-but-valid request ids must NOT be misclassified as
// notifications. A future rewrite to `!msg.id` would silently break these.
test('request with id:0 is treated as a request, not a notification', () => {
  const out = surface.route({ jsonrpc: '2.0', id: 0, method: 'ping' }, new ShimState());
  assert.equal(out.kind, 'respond');
  assert.deepEqual(out.message.result, {});
  assert.equal(out.message.id, 0);
});

test("request with id:'' is treated as a request, not a notification", () => {
  const out = surface.route({ jsonrpc: '2.0', id: '', method: 'ping' }, new ShimState());
  assert.equal(out.kind, 'respond');
  assert.deepEqual(out.message.result, {});
  assert.equal(out.message.id, '');
});

// An unknown notification pre-ready is dropped (nothing to forward yet); e.g.
// notifications/cancelled before the engine is live has nothing to cancel.
test('unknown notification before ready is dropped', () => {
  const out = surface.route({ jsonrpc: '2.0', method: 'notifications/cancelled' }, new ShimState());
  assert.equal(out.kind, 'drop');
});

// Once proxying, a non-initialized notification forwards to the live engine.
test('unknown notification once proxying is proxied', () => {
  const s = new ShimState(); s.set('proxying');
  const out = surface.route({ jsonrpc: '2.0', method: 'notifications/cancelled' }, s);
  assert.equal(out.kind, 'proxy');
});
