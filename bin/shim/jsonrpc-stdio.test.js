import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';
import { readMessages, writeMessage, makeResult, makeError } from './jsonrpc-stdio.js';

test('readMessages parses one JSON object per line', async () => {
  const stream = new PassThrough();
  const seen = [];
  readMessages(stream, (m) => seen.push(m));
  stream.write('{"jsonrpc":"2.0","id":1,"method":"ping"}\n');
  stream.write('{"jsonrpc":"2.0","id":2,"method":"x"}\n');
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(seen.length, 2);
  assert.equal(seen[0].id, 1);
  assert.equal(seen[1].method, 'x');
});

test('readMessages handles a message split across chunks', async () => {
  const stream = new PassThrough();
  const seen = [];
  readMessages(stream, (m) => seen.push(m));
  stream.write('{"jsonrpc":"2.0","id":');
  stream.write('7,"method":"ping"}\n');
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(seen.length, 1);
  assert.equal(seen[0].id, 7);
});

test('readMessages drops blank and unparseable lines without throwing', async () => {
  const stream = new PassThrough();
  const seen = [];
  readMessages(stream, (m) => seen.push(m));
  stream.write('\n');
  stream.write('not json\n');
  stream.write('{"ok":true}\n');
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(seen.length, 1);
  assert.deepEqual(seen[0], { ok: true });
});

test('writeMessage emits newline-delimited JSON', () => {
  const stream = new PassThrough();
  let out = '';
  stream.on('data', (b) => { out += b.toString(); });
  writeMessage(stream, { jsonrpc: '2.0', id: 1, result: {} });
  assert.equal(out, '{"jsonrpc":"2.0","id":1,"result":{}}\n');
});

test('makeResult / makeError build valid envelopes', () => {
  assert.deepEqual(makeResult(5, { a: 1 }), { jsonrpc: '2.0', id: 5, result: { a: 1 } });
  assert.deepEqual(makeError(5, -32601, 'nope'),
    { jsonrpc: '2.0', id: 5, error: { code: -32601, message: 'nope' } });
});
