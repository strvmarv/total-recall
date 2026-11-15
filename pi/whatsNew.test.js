import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { spawn as realSpawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { writeDirectStamp, runWhatsNew, isDisplayableNotice } from './whatsNew.js';

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'tr-whatsnew-'));
}
function withTmp(fn) {
  const dir = tmpDir();
  try {
    return fn(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}
const NOW = () => new Date('2026-11-15T20:24:56.123Z');

// ---- writeDirectStamp ----

test('writeDirectStamp: writes {at} with the injected clock and creates whats-new', () => withTmp((dir) => {
  assert.equal(writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW }), true);
  const file = path.join(dir, 'whats-new', 'direct-pi.json');
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { at: '2026-11-15T20:24:56.123Z' });
  assert.deepEqual(fs.readdirSync(path.join(dir, 'whats-new')), ['direct-pi.json']);
}));

test('writeDirectStamp: uses the real clock by default', () => withTmp((dir) => {
  const before = Date.now();
  assert.equal(writeDirectStamp({ dataDir: dir, host: 'claude-code' }), true);
  const at = Date.parse(JSON.parse(fs.readFileSync(path.join(dir, 'whats-new', 'direct-claude-code.json'), 'utf8')).at);
  assert.ok(at >= before && at <= Date.now());
}));

test('writeDirectStamp: a non-absolute or empty dataDir returns false and creates nothing', () => withTmp((dir) => {
  const cwd = process.cwd();
  process.chdir(dir);
  try {
    for (const bad of [null, undefined, '', 'rel/dir', '.', 5]) {
      assert.equal(writeDirectStamp({ dataDir: bad, host: 'pi', now: NOW }), false);
    }
    assert.deepEqual(fs.readdirSync(dir), []);
  } finally {
    process.chdir(cwd);
  }
}));

test('writeDirectStamp: bad host ids return false and write nothing', () => withTmp((dir) => {
  for (const bad of ['../x', 'A', '', 'a'.repeat(40), 'pi/x', '1pi', undefined, 5]) {
    assert.equal(writeDirectStamp({ dataDir: dir, host: bad, now: NOW }), false);
  }
  assert.deepEqual(fs.readdirSync(dir), []);
}));

test('writeDirectStamp: replaces an existing stamp', () => withTmp((dir) => {
  writeDirectStamp({ dataDir: dir, host: 'pi', now: () => new Date('2026-01-01T00:00:00.000Z') });
  writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW });
  const file = path.join(dir, 'whats-new', 'direct-pi.json');
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { at: '2026-11-15T20:24:56.123Z' });
}));

test('writeDirectStamp: a data dir that is a file returns false without throwing', () => withTmp((dir) => {
  const asFile = path.join(dir, 'file');
  fs.writeFileSync(asFile, 'x');
  assert.equal(writeDirectStamp({ dataDir: asFile, host: 'pi', now: NOW }), false);
  assert.deepEqual(fs.readdirSync(dir), ['file']);
}));

test('writeDirectStamp: a throwing renameSync returns false and leaves no .tmp file', () => withTmp((dir) => {
  const fsApi = { ...fs, renameSync: () => { throw new Error('rename failed'); } };
  assert.equal(writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW, fsApi }), false);
  assert.deepEqual(fs.readdirSync(path.join(dir, 'whats-new')), []);
}));

test('writeDirectStamp: a throwing writeFileSync returns false and leaves no .tmp file', () => withTmp((dir) => {
  const fsApi = {
    ...fs,
    writeFileSync: (p, data, ...rest) => {
      fs.writeFileSync(p, data.slice(0, 3), ...rest); // a partial temp file
      throw new Error('disk full');
    },
  };
  assert.equal(writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW, fsApi }), false);
  assert.deepEqual(fs.readdirSync(path.join(dir, 'whats-new')), []);
}));

test('writeDirectStamp: a failing write never touches an existing stamp', () => withTmp((dir) => {
  writeDirectStamp({ dataDir: dir, host: 'pi', now: () => new Date('2026-01-01T00:00:00.000Z') });
  const fsApi = { ...fs, renameSync: () => { throw new Error('nope'); } };
  assert.equal(writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW, fsApi }), false);
  const file = path.join(dir, 'whats-new', 'direct-pi.json');
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { at: '2026-01-01T00:00:00.000Z' });
}));

test('writeDirectStamp: interleaved writers never leave a partial file', () => withTmp((dir) => {
  const file = path.join(dir, 'whats-new', 'direct-pi.json');
  for (let i = 0; i < 40; i += 1) {
    const host = 'pi';
    const at = new Date(Date.UTC(2026, 0, 1 + (i % 2), 0, 0, i));
    assert.equal(writeDirectStamp({ dataDir: dir, host, now: () => at }), true);
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), { at: at.toISOString() });
  }
  assert.deepEqual(fs.readdirSync(path.join(dir, 'whats-new')), ['direct-pi.json']);
}));

test('writeDirectStamp: temp files are unique per call (stamp renamed from a sibling .tmp)', () => withTmp((dir) => {
  const temps = [];
  const fsApi = { ...fs, renameSync: (from, to) => { temps.push(from); return fs.renameSync(from, to); } };
  writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW, fsApi });
  writeDirectStamp({ dataDir: dir, host: 'pi', now: NOW, fsApi });
  assert.equal(temps.length, 2);
  assert.notEqual(temps[0], temps[1]);
  for (const t of temps) {
    assert.equal(path.dirname(t), path.join(dir, 'whats-new'));
    assert.match(path.basename(t), /^direct-pi\.json\..+\.tmp$/);
  }
}));

// ---- runWhatsNew ----

function fakeChild({ onSpawn } = {}) {
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.killed = false;
  child.kill = () => { child.killed = true; return true; };
  if (onSpawn) setImmediate(() => onSpawn(child));
  return child;
}
function fakeSpawn(onSpawn) {
  const calls = [];
  const spawn = (cmd, args, opts) => {
    const child = fakeChild({ onSpawn });
    calls.push({ cmd, args, opts, child });
    return child;
  };
  return { spawn, calls };
}
const exits = (out, code = 0) => (child) => {
  if (out !== undefined) child.stdout.write(out);
  child.stdout.end();
  child.emit('close', code);
};

test('runWhatsNew: passes exactly the documented args and options', async () => {
  const { spawn, calls } = fakeSpawn(exits('line\n'));
  await runWhatsNew({ binaryPath: '/bin/tr', host: 'pi', format: 'text', spawn });
  assert.equal(calls[0].cmd, '/bin/tr');
  assert.deepEqual(calls[0].args, ['whats-new', '--host', 'pi', '--format', 'text']);
  assert.deepEqual(calls[0].opts.stdio, ['ignore', 'pipe', 'ignore']);
  assert.equal(calls[0].opts.windowsHide, true);
});

test('runWhatsNew: noMark adds --no-mark at the end', async () => {
  const { spawn, calls } = fakeSpawn(exits('line\n'));
  await runWhatsNew({ binaryPath: '/bin/tr', host: 'claude-code', format: 'claude-code', noMark: true, spawn });
  assert.deepEqual(calls[0].args, ['whats-new', '--host', 'claude-code', '--format', 'claude-code', '--no-mark']);
});

test('runWhatsNew: merges env over process.env and removes undefined values', async () => {
  process.env.TR_WHATSNEW_TEST_KEEP = 'kept';
  process.env.TR_WHATSNEW_TEST_DROP = 'dropped';
  process.env.TR_WHATSNEW_TEST_OVER = 'old';
  try {
    const { spawn, calls } = fakeSpawn(exits('x'));
    await runWhatsNew({
      binaryPath: 'b', host: 'pi', format: 'text', spawn,
      env: { TR_WHATSNEW_TEST_DROP: undefined, TR_WHATSNEW_TEST_OVER: 'new', TR_WHATSNEW_TEST_ADD: 'added' },
    });
    const env = calls[0].opts.env;
    assert.equal(env.TR_WHATSNEW_TEST_KEEP, 'kept');
    assert.equal(env.TR_WHATSNEW_TEST_OVER, 'new');
    assert.equal(env.TR_WHATSNEW_TEST_ADD, 'added');
    assert.equal('TR_WHATSNEW_TEST_DROP' in env, false);
    assert.equal(process.env.TR_WHATSNEW_TEST_DROP, 'dropped');
  } finally {
    delete process.env.TR_WHATSNEW_TEST_KEEP;
    delete process.env.TR_WHATSNEW_TEST_DROP;
    delete process.env.TR_WHATSNEW_TEST_OVER;
  }
});

test('runWhatsNew: resolves trimmed stdout on exit 0', async () => {
  const { spawn } = fakeSpawn(exits('  total-recall 4.8.0: hi\n'));
  assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), 'total-recall 4.8.0: hi');
});

test('runWhatsNew: resolves undefined on a non-zero exit even with output', async () => {
  const { spawn } = fakeSpawn(exits('something', 1));
  assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), undefined);
});

test('runWhatsNew: resolves undefined on empty or whitespace-only output', async () => {
  for (const out of [undefined, '', ' \n\n']) {
    const { spawn } = fakeSpawn(exits(out));
    assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), undefined);
  }
});

test('runWhatsNew: resolves undefined on a spawn error event', async () => {
  const { spawn } = fakeSpawn((child) => child.emit('error', new Error('ENOENT')));
  assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), undefined);
});

test('runWhatsNew: resolves undefined when spawn throws synchronously', async () => {
  const spawn = () => { throw new Error('EACCES'); };
  assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), undefined);
});

test('runWhatsNew: kills a hung child at the timeout, destroys stdout and resolves undefined', async () => {
  const { spawn, calls } = fakeSpawn(() => { /* never exits */ });
  const started = Date.now();
  const result = await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', timeoutMs: 20, spawn });
  assert.equal(result, undefined);
  assert.ok(Date.now() - started < 1000);
  assert.equal(calls[0].child.killed, true);
  assert.equal(calls[0].child.stdout.destroyed, true);
});

test('runWhatsNew: a kill that throws still resolves undefined', async () => {
  const { spawn, calls } = fakeSpawn(() => { /* never exits */ });
  const p = runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', timeoutMs: 20, spawn });
  await new Promise((r) => setImmediate(r));
  calls[0].child.kill = () => { throw new Error('gone'); };
  assert.equal(await p, undefined);
});

test('runWhatsNew: stdout over 8 KiB is truncated without crashing', async () => {
  const big = 'a'.repeat(20_000);
  const { spawn } = fakeSpawn(exits(big));
  const result = await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn });
  assert.equal(result.length, 8192);
  assert.match(result, /^a+$/);
});

test('runWhatsNew: a multi-byte character split across chunks is decoded intact', async () => {
  const bytes = Buffer.from('a \u2014 b\n', 'utf8'); // em dash is 3 bytes
  const { spawn } = fakeSpawn((child) => {
    child.stdout.write(bytes.subarray(0, 3)); // 'a ' + first byte of the dash
    setImmediate(() => {
      child.stdout.write(bytes.subarray(3));
      child.stdout.end();
      child.emit('close', 0);
    });
  });
  assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), 'a \u2014 b');
});

test('runWhatsNew: resolves once when both error and close fire', async () => {
  const { spawn } = fakeSpawn((child) => {
    child.stdout.write('late');
    child.emit('error', new Error('boom'));
    child.emit('close', 0);
  });
  assert.equal(await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', spawn }), undefined);
});

test('runWhatsNew: close after a timeout does not change the answer or throw', async () => {
  const { spawn, calls } = fakeSpawn(() => { /* never exits */ });
  const result = await runWhatsNew({ binaryPath: 'b', host: 'pi', format: 'text', timeoutMs: 10, spawn });
  assert.equal(result, undefined);
  calls[0].child.emit('close', 0);
  calls[0].child.emit('error', new Error('late'));
});

test('runWhatsNew: runs a real child process end to end', async () => {
  const script = "process.stdout.write('hello \\u2014 world\\n')";
  const result = await runWhatsNew({
    binaryPath: process.execPath, host: 'pi', format: 'text', timeoutMs: 5000,
    spawn: (cmd, _args, opts) => realSpawn(cmd, ['-e', script], opts),
  });
  assert.equal(result, 'hello \u2014 world');
});

test('runWhatsNew: a host that is not a plain host id, or an unknown format, never spawns', async () => {
  for (const [host, format] of [['--no-mark', 'text'], ['../x', 'text'], ['', 'text'], ['pi', 'yaml'], ['pi', undefined], [undefined, 'text']]) {
    const { spawn, calls } = fakeSpawn();
    const out = await runWhatsNew({ binaryPath: '/engine', host, format, spawn });
    assert.equal(out, undefined, `${host} ${format}`);
    assert.equal(calls.length, 0, `${host} ${format}`);
  }
});

test('runWhatsNew: the timeout kills the child with SIGKILL', async () => {
  const { spawn, calls } = fakeSpawn();
  let signal;
  const p = runWhatsNew({ binaryPath: '/engine', host: 'pi', format: 'text', timeoutMs: 10, spawn });
  calls[0].child.kill = (sig) => { signal = sig; return true; };
  assert.equal(await p, undefined);
  assert.equal(signal, 'SIGKILL');
});

test('isDisplayableNotice: accepts an ordinary line and the 1000-character limit', () => {
  assert.equal(isDisplayableNotice('total-recall 4.8.0: x. All releases: https://example.test'), true);
  assert.equal(isDisplayableNotice('x'.repeat(1000)), true);
  assert.equal(isDisplayableNotice('total-recall 4.8.0 \u2014 caf\u00e9'), true);
});

for (const [label, value] of [
  ['1001 characters', 'x'.repeat(1001)],
  ['an empty string', ''],
  ['undefined', undefined],
  ['null', null],
  ['a number', 5],
  ['an object', { a: 1 }],
  ['LF', 'a\nb'],
  ['CR', 'a\rb'],
  ['U+2028', 'a\u2028b'],
  ['U+2029', 'a\u2029b'],
  ['a C0 control (BEL)', 'a\u0007b'],
  ['a C0 control (ESC)', 'a\u001b[31mb'],
  ['a tab', 'a\tb'],
  ['DEL', 'a\u007fb'],
  ['a C1 control', 'a\u0085b'],
]) {
  test(`isDisplayableNotice: rejects ${label}`, () => assert.equal(isDisplayableNotice(value), false));
}
