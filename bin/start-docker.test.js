import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { EventEmitter } from 'node:events';
import { resolveDockerImage } from '../scripts/docker-image.js';
import {
  resolveDataDir, parsePort, buildDockerArgs, runDocker, DEFAULT_UI_PORT,
} from './start-docker.js';

function sink() {
  const s = { text: '' };
  s.write = (chunk) => { s.text += chunk; return true; };
  return s;
}

function fakeChild() {
  const c = new EventEmitter();
  c.killed = [];
  c.kill = (sig) => { c.killed.push(sig); return true; };
  c.stderr = new EventEmitter();
  return c;
}

function fakeProc(uid = 501, gid = 20) {
  const p = new EventEmitter();
  p.getuid = () => uid;
  p.getgid = () => gid;
  return p;
}

// --- resolveDockerImage ---
test('resolveDockerImage: override wins', () => {
  assert.equal(
    resolveDockerImage({ TOTAL_RECALL_DOCKER_IMAGE: 'img@sha256:abc' }, () => ({ version: '9.9.9' })),
    'img@sha256:abc');
});

test('resolveDockerImage: default is ghcr.io/...:<version>', () => {
  assert.equal(
    resolveDockerImage({}, () => ({ version: '4.2.0' })),
    'ghcr.io/strvmarv/total-recall:4.2.0');
});

// --- resolveDataDir ---
test('resolveDataDir: TOTAL_RECALL_HOME wins and is made absolute', () => {
  assert.equal(resolveDataDir({ TOTAL_RECALL_HOME: '/custom/data' }), '/custom/data');
  const rel = resolveDataDir({ TOTAL_RECALL_HOME: 'relative/data' });
  assert.ok(path.isAbsolute(rel));
  assert.ok(rel.endsWith(path.join('relative', 'data')));
});

test('resolveDataDir: defaults to ~/.total-recall', () => {
  assert.equal(resolveDataDir({ HOME: '/home/alice' }), '/home/alice/.total-recall');
});

// --- parsePort ---
test('parsePort: default, --port, -p, and invalid', () => {
  assert.equal(parsePort([]), DEFAULT_UI_PORT);
  assert.equal(parsePort(['ui', '--port', '5600']), 5600);
  assert.equal(parsePort(['ui', '-p', '5600']), 5600);
  assert.equal(parsePort(['ui', '--port', 'not-a-number']), null);
  assert.equal(parsePort(['ui', '--port', '99999']), null);
  assert.equal(parsePort(['ui', '--port', '0']), null);
  assert.equal(parsePort(['ui', '--port']), null); // missing value
});

// --- buildDockerArgs ---
test('buildDockerArgs: serve (bare and explicit)', () => {
  const base = { dataDir: '/home/alice/.total-recall', image: 'img:test', uid: 501, gid: 20 };
  const expected = { ok: true, args: ['run', '--rm', '-i', '--user', '501:20', '-v', '/home/alice/.total-recall:/data', 'img:test', 'serve'] };
  assert.deepEqual(buildDockerArgs({ ...base, args: [] }), expected);
  assert.deepEqual(buildDockerArgs({ ...base, args: ['serve'] }), expected);
});

test('buildDockerArgs: ui injects --host 0.0.0.0 --no-open and publishes the port', () => {
  const base = { dataDir: '/d', image: 'img', uid: 501, gid: 20 };
  assert.deepEqual(buildDockerArgs({ ...base, args: ['ui'] }), {
    ok: true,
    args: ['run', '--rm', '--user', '501:20', '-v', '/d:/data', '-p', '5577:5577',
      'img', 'ui', '--host', '0.0.0.0', '--no-open', '--port', '5577'],
  });
  assert.deepEqual(buildDockerArgs({ ...base, args: ['ui', '--port', '5600'] }), {
    ok: true,
    args: ['run', '--rm', '--user', '501:20', '-v', '/d:/data', '-p', '5600:5600',
      'img', 'ui', '--host', '0.0.0.0', '--no-open', '--port', '5600'],
  });
});

test('buildDockerArgs: ui forwards --token and strips user --host/--port', () => {
  const r = buildDockerArgs({ dataDir: '/d', image: 'img', uid: 501, gid: 20, args: ['ui', '--token', 'abc', '--host', '1.2.3.4'] });
  assert.equal(r.ok, true);
  assert.deepEqual(r.args, [
    'run', '--rm', '--user', '501:20', '-v', '/d:/data', '-p', '5577:5577',
    'img', 'ui', '--token', 'abc', '--host', '0.0.0.0', '--no-open', '--port', '5577',
  ]);
});

test('buildDockerArgs: ui --port 0 is rejected', () => {
  const r = buildDockerArgs({ dataDir: '/d', image: 'img', uid: 501, gid: 20, args: ['ui', '--port', '0'] });
  assert.equal(r.ok, false);
  assert.match(r.error, /--port 0/);
});

test('buildDockerArgs: ui -p short flag is translated to --port', () => {
  const r = buildDockerArgs({ dataDir: '/d', image: 'img', uid: 501, gid: 20, args: ['ui', '-p', '5600'] });
  assert.equal(r.ok, true);
  assert.deepEqual(r.args, [
    'run', '--rm', '--user', '501:20', '-v', '/d:/data', '-p', '5600:5600',
    'img', 'ui', '--host', '0.0.0.0', '--no-open', '--port', '5600',
  ]);
});

test('buildDockerArgs: one-shot subcommand passes through, no -i', () => {
  assert.deepEqual(buildDockerArgs({ dataDir: '/d', image: 'img', uid: 501, gid: 20, args: ['status'] }), {
    ok: true,
    args: ['run', '--rm', '--user', '501:20', '-v', '/d:/data', 'img', 'status'],
  });
});

test('buildDockerArgs: no --user when uid/gid are null (win32)', () => {
  assert.deepEqual(buildDockerArgs({ dataDir: '/d', image: 'img', uid: null, gid: null, args: ['status'] }), {
    ok: true,
    args: ['run', '--rm', '-v', '/d:/data', 'img', 'status'],
  });
});

// --- runDocker ---
test('runDocker: TOTAL_RECALL_DB_PATH -> onExit(1), no spawn', async () => {
  const stderr = sink();
  const codes = [];
  let spawned = false;
  await runDocker({
    args: ['serve'],
    env: { TOTAL_RECALL_DB_PATH: '~/db' },
    spawn: () => { spawned = true; return fakeChild(); },
    spawnSyncFn: () => ({ status: 0 }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(spawned, false);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /TOTAL_RECALL_DB_PATH is not supported/);
});

test('runDocker: docker not installed -> "install Docker Desktop", no spawn', async () => {
  const stderr = sink();
  const codes = [];
  let spawned = false;
  await runDocker({
    args: ['serve'],
    env: { HOME: '/home/alice' },
    spawn: () => { spawned = true; return fakeChild(); },
    spawnSyncFn: () => ({ status: null, error: new Error('spawn docker ENOENT') }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(spawned, false);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /install Docker Desktop/);
});

test('runDocker: image absent -> "docker pull" message, no spawn', async () => {
  const stderr = sink();
  const codes = [];
  let spawned = false;
  await runDocker({
    args: ['serve'],
    env: { HOME: '/home/alice', TOTAL_RECALL_DOCKER_IMAGE: 'img:test' },
    spawn: () => { spawned = true; return fakeChild(); },
    spawnSyncFn: () => ({ status: 1, stderr: '' }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(spawned, false);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /docker pull img:test/);
});

test('runDocker: daemon down -> "daemon is not running" message, no spawn', async () => {
  const stderr = sink();
  const codes = [];
  let spawned = false;
  await runDocker({
    args: ['serve'],
    env: { HOME: '/home/alice' },
    spawn: () => { spawned = true; return fakeChild(); },
    spawnSyncFn: () => ({ status: 1, stderr: 'Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?' }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(spawned, false);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /daemon is not running/);
});

test('runDocker: one-shot subcommand skips the pre-flight inspect', async () => {
  const stderr = sink();
  const codes = [];
  let call = null;
  let inspectCalled = false;
  const child = fakeChild();
  await runDocker({
    args: ['status'],
    env: { HOME: '/home/alice', TOTAL_RECALL_DOCKER_IMAGE: 'img:test' },
    spawn: (cmd, args, opts) => { call = { cmd, args, opts }; return child; },
    spawnSyncFn: () => { inspectCalled = true; return { status: 1 }; },
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(inspectCalled, false); // no pre-flight for one-shot
  assert.equal(call.cmd, 'docker');
  assert.deepEqual(call.args, ['run', '--rm', '--user', '501:20', '-v', '/home/alice/.total-recall:/data', 'img:test', 'status']);
  child.emit('exit', 0, null);
  assert.deepEqual(codes, [0]);
});

test('runDocker: ui --port 0 -> onExit(1) via the !built.ok branch', async () => {
  const stderr = sink();
  const codes = [];
  let spawned = false;
  await runDocker({
    args: ['ui', '--port', '0'],
    env: { HOME: '/home/alice' },
    spawn: () => { spawned = true; return fakeChild(); },
    spawnSyncFn: () => ({ status: 0 }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(spawned, false);
  assert.deepEqual(codes, [1]);
  assert.match(stderr.text, /--port 0/);
});

test('runDocker: spawns docker with correct argv and mirrors exit', async () => {
  const stderr = sink();
  const codes = [];
  let call = null;
  const child = fakeChild();
  await runDocker({
    args: ['serve'],
    env: { HOME: '/home/alice', TOTAL_RECALL_DOCKER_IMAGE: 'img:test' },
    spawn: (cmd, args, opts) => { call = { cmd, args, opts }; return child; },
    spawnSyncFn: () => ({ status: 0 }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  assert.equal(call.cmd, 'docker');
  assert.deepEqual(call.args, [
    'run', '--rm', '-i', '--user', '501:20', '-v', '/home/alice/.total-recall:/data', 'img:test', 'serve',
  ]);
  assert.deepEqual(call.opts, { stdio: ['inherit', 'inherit', 'pipe'] });
  child.emit('exit', 0, null);
  assert.deepEqual(codes, [0]);
});

test('runDocker: forwards SIGINT/SIGTERM to the docker child', async () => {
  const child = fakeChild();
  const proc = fakeProc();
  await runDocker({
    args: ['serve'],
    env: { HOME: '/home/alice' },
    spawn: () => child,
    spawnSyncFn: () => ({ status: 0 }),
    onExit: () => {},
    proc,
    mkdir: () => {},
  });
  proc.emit('SIGINT');
  proc.emit('SIGTERM');
  assert.deepEqual(child.killed, ['SIGINT', 'SIGTERM']);
});

test('runDocker: mount failure -> file-sharing hint', async () => {
  const stderr = sink();
  const codes = [];
  const child = fakeChild();
  await runDocker({
    args: ['serve'],
    env: { HOME: '/home/alice' },
    spawn: () => child,
    spawnSyncFn: () => ({ status: 0 }),
    stderr,
    onExit: (code) => codes.push(code),
    proc: fakeProc(),
    mkdir: () => {},
  });
  child.stderr.emit('data', Buffer.from('docker: Error response from daemon: Mounts denied: /data is not shared'));
  child.emit('exit', 125, null);
  assert.deepEqual(codes, [125]);
  assert.match(stderr.text, /File Sharing/);
});
