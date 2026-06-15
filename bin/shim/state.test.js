import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ShimState } from './state.js';

test('starts in init, not ready', () => {
  const s = new ShimState();
  assert.equal(s.phase, 'init');
  assert.equal(s.ready, false);
});

test('proxying is the only ready phase', () => {
  const s = new ShimState();
  s.set('provisioning', { pct: 40 });
  assert.equal(s.ready, false);
  s.set('proxying');
  assert.equal(s.ready, true);
});

test('notReadyResult is an MCP isError tool result carrying structured JSON', () => {
  const s = new ShimState();
  s.set('provisioning', { pct: 42 });
  const r = s.notReadyResult();
  assert.equal(r.isError, true);
  assert.equal(r.content[0].type, 'text');
  const payload = JSON.parse(r.content[0].text);
  assert.equal(payload.status, 'not_ready');
  assert.equal(payload.phase, 'provisioning');
  assert.equal(payload.pct, 42);
  assert.equal(typeof payload.hint, 'string');
});
