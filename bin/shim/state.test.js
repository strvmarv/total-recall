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

// --- Not-ready payload while provisioning is retried (#87) ------------------------------------

const payloadOf = (s) => JSON.parse(s.notReadyResult().content[0].text);
const DEFAULT_HINT = 'total-recall is still starting up; retry in a moment.';

test('waiting for a retry: retryInSeconds counts down from the moment of the call and nextRetryAt is not exposed', () => {
  let t = 1_000_000;
  const s = new ShimState({ now: () => t });
  s.set('provisioning', { attempt: 1, error: 'HTTP 404 for manifest', nextRetryAt: t + 60_000 });
  assert.equal(payloadOf(s).retryInSeconds, 60);
  t += 40_000;
  const p = payloadOf(s);
  assert.equal(p.retryInSeconds, 20);
  assert.equal(p.attempt, 1);
  assert.equal(p.error, 'HTTP 404 for manifest');
  assert.equal('nextRetryAt' in p, false);
  t += 19_500;
  assert.equal(payloadOf(s).retryInSeconds, 1);
  t += 5_000;
  assert.equal(payloadOf(s).retryInSeconds, 0);
});

test('waiting for a retry: the hint says the shim retries by itself, and how soon', () => {
  const t = 5_000;
  const s = new ShimState({ now: () => t });
  s.set('provisioning', { attempt: 2, error: 'boom', nextRetryAt: t + 20_000 });
  const hint = payloadOf(s).hint;
  assert.match(hint, /try again by itself/);
  assert.match(hint, /20 s/);
  assert.notEqual(hint, DEFAULT_HINT);
});

test('waiting with no time left says a moment, not 0 s', () => {
  const t = 5_000;
  const s = new ShimState({ now: () => t });
  s.set('provisioning', { attempt: 2, error: 'boom', nextRetryAt: t });
  assert.match(payloadOf(s).hint, /in a moment/);
  assert.doesNotMatch(payloadOf(s).hint, /0 s/);
});

test('a retry that is running says so, without a time', () => {
  const s = new ShimState();
  s.set('provisioning', { attempt: 2, error: 'boom' });
  const p = payloadOf(s);
  assert.equal('retryInSeconds' in p, false);
  assert.match(p.hint, /trying again now/);
});

test('the first try, with no error, keeps the original hint', () => {
  const s = new ShimState();
  s.set('provisioning', { bytes: 10, total: 100 });
  assert.equal(payloadOf(s).hint, DEFAULT_HINT);
});

test('provisioning-failed says the shim will not try again and the MCP host must be restarted', () => {
  const s = new ShimState();
  s.set('provisioning-failed', { error: 'checksum mismatch', retryable: false });
  const p = payloadOf(s);
  assert.match(p.hint, /will not try again/);
  assert.match(p.hint, /[Rr]estart the MCP host/);
  assert.equal(p.retryable, false);
});

test('other phases keep the original hint', () => {
  const s = new ShimState();
  s.set('engine-restarting', { attempt: 2 });
  assert.equal(payloadOf(s).hint, DEFAULT_HINT);
});

test('a phase carried in the detail (a progress tick) never replaces the state phase', () => {
  const s = new ShimState();
  s.set('provisioning', { attempt: 2, error: 'boom', bytes: 5, total: 50, phase: 'downloading' });
  const p = payloadOf(s);
  assert.equal(p.phase, 'provisioning');
  assert.equal(p.bytes, 5);
});
