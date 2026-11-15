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

// --- Harness client name -> session_start `host` argument -------------------------------------

function surfaceFor(clientInfo, rawParams) {
  const s = new ServerSurface({ catalog, serverName: 'total-recall', serverVersion: '3.2.0', protocolVersion: '2024-11-05' });
  const init = { jsonrpc: '2.0', id: 1, method: 'initialize' };
  if (rawParams !== undefined) init.params = rawParams;
  else if (clientInfo !== undefined) init.params = { clientInfo };
  s.route(init, new ShimState());
  return s;
}
const ready = () => { const st = new ShimState(); st.set('proxying'); return st; };
const sessionStart = (args, hasArgs = true) => {
  const m = { jsonrpc: '2.0', id: 9, method: 'tools/call', params: { name: 'session_start' } };
  if (hasArgs) m.params.arguments = args;
  return m;
};

test('session_start gets host from the harness clientInfo.name; the original is untouched', () => {
  const s = surfaceFor({ name: 'claude-code', version: '1' });
  const msg = sessionStart({ task: 'x' });
  const snapshot = structuredClone(msg);
  const out = s.route(msg, ready());
  assert.equal(out.kind, 'proxy');
  assert.notEqual(out.message, msg);
  assert.equal(out.message.id, 9);
  assert.equal(out.message.method, 'tools/call');
  assert.equal(out.message.jsonrpc, '2.0');
  assert.equal(out.message.params.name, 'session_start');
  assert.deepEqual(out.message.params.arguments, { task: 'x', host: 'claude-code' });
  assert.deepEqual(msg, snapshot);
});

test('a caller-supplied host is replaced', () => {
  const s = surfaceFor({ name: 'claude-code' });
  const out = s.route(sessionStart({ host: 'evil', task: 'x' }), ready());
  assert.deepEqual(out.message.params.arguments, { host: 'claude-code', task: 'x' });
});

test('absent and null arguments become { host }', () => {
  const s = surfaceFor({ name: 'codex' });
  assert.deepEqual(s.route(sessionStart(undefined, false), ready()).message.params.arguments, { host: 'codex' });
  assert.deepEqual(s.route(sessionStart(null), ready()).message.params.arguments, { host: 'codex' });
});

test('non-object arguments are forwarded unchanged', () => {
  const s = surfaceFor({ name: 'codex' });
  for (const bad of [[], 'x', 5]) {
    const msg = sessionStart(bad);
    const out = s.route(msg, ready());
    assert.equal(out.kind, 'proxy');
    assert.deepEqual((out.message ?? msg).params.arguments, bad);
  }
});

test('other tools, tools/list and ping get no host', () => {
  const s = surfaceFor({ name: 'claude-code' });
  const call = { jsonrpc: '2.0', id: 3, method: 'tools/call', params: { name: 'memory_search', arguments: { query: 'q' } } };
  const out = s.route(call, ready());
  assert.equal(out.kind, 'proxy');
  assert.deepEqual((out.message ?? call).params.arguments, { query: 'q' });
  const list = s.route({ jsonrpc: '2.0', id: 4, method: 'tools/list' }, ready());
  assert.equal(list.kind, 'proxy');
  assert.equal(list.message, undefined);
  assert.deepEqual(s.route({ jsonrpc: '2.0', id: 5, method: 'ping' }, ready()).message.result, {});
});

test('no usable client name: session_start is forwarded unchanged', () => {
  const cases = [
    surfaceFor(undefined),
    surfaceFor(undefined, null),
    surfaceFor({}),
    surfaceFor({ name: '' }),
    surfaceFor({ name: '   ' }),
    surfaceFor({ name: 42 }),
    surfaceFor({ name: ['a'] }),
    surfaceFor('claude-code'),
  ];
  for (const s of cases) {
    const msg = sessionStart({ task: 'x' });
    const out = s.route(msg, ready());
    assert.equal(out.kind, 'proxy');
    assert.deepEqual((out.message ?? msg).params.arguments, { task: 'x' });
  }
});

test('the name survives a later initialize without one, and a later name replaces it', () => {
  const s = surfaceFor({ name: 'claude-code' });
  s.route({ jsonrpc: '2.0', id: 2, method: 'initialize', params: {} }, new ShimState());
  s.route({ jsonrpc: '2.0', id: 3, method: 'initialize', params: { clientInfo: { name: 7 } } }, new ShimState());
  assert.equal(s.route(sessionStart({}), ready()).message.params.arguments.host, 'claude-code');
  s.route({ jsonrpc: '2.0', id: 4, method: 'initialize', params: { clientInfo: { name: 'cursor' } } }, new ShimState());
  assert.equal(s.route(sessionStart({}), ready()).message.params.arguments.host, 'cursor');
});

test('a 300-character name is cut to 128 and a hermes name is verbatim', () => {
  const long = surfaceFor({ name: 'a'.repeat(300) });
  assert.equal(long.route(sessionStart({}), ready()).message.params.arguments.host, 'a'.repeat(128));
  const h = surfaceFor({ name: 'hermes-mcp-client' });
  assert.equal(h.route(sessionStart({}), ready()).message.params.arguments.host, 'hermes-mcp-client');
});

test('session_start before the engine is ready still answers not_ready', () => {
  const s = surfaceFor({ name: 'claude-code' });
  const st = new ShimState(); st.set('provisioning', { pct: 10 });
  const out = s.route(sessionStart({}), st);
  assert.equal(out.kind, 'respond');
  assert.equal(JSON.parse(out.message.result.content[0].text).phase, 'provisioning');
});
