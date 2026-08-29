import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { pullDockerImage } from './pull-docker-image.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const version = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'package.json'), 'utf8')).version;
const DEFAULT_TAG = `ghcr.io/strvmarv/total-recall:${version}`;

function spawnResult(status, error = undefined) {
  return { status, error, stdout: '', stderr: '' };
}

test('pullDockerImage: docker absent -> skipped no-docker, no pull', () => {
  const calls = [];
  const r = pullDockerImage({
    env: {},
    spawnSyncFn: (cmd, args) => {
      calls.push([cmd, args]);
      return spawnResult(null, new Error('spawn docker ENOENT'));
    },
    log: () => {},
  });
  assert.deepEqual(r, { skipped: 'no-docker' });
  assert.deepEqual(calls, [['docker', ['image', 'inspect', DEFAULT_TAG]]]);
});

test('pullDockerImage: image present -> skipped present, no pull', () => {
  const calls = [];
  const r = pullDockerImage({
    env: {},
    spawnSyncFn: (cmd, args) => {
      calls.push([cmd, args]);
      return spawnResult(0);
    },
    log: () => {},
  });
  assert.deepEqual(r, { skipped: 'present' });
  assert.equal(calls.length, 1); // only the inspect, no pull
});

test('pullDockerImage: absent -> pulls, ok on success', () => {
  const calls = [];
  const r = pullDockerImage({
    env: {},
    spawnSyncFn: (cmd, args) => {
      calls.push([cmd, args]);
      return calls.length === 1 ? spawnResult(1) : spawnResult(0);
    },
    log: () => {},
  });
  assert.deepEqual(r, { ok: true });
  assert.deepEqual(calls[1], ['docker', ['pull', DEFAULT_TAG]]);
});

test('pullDockerImage: pull failure -> ok:false + warning', () => {
  const logs = [];
  const r = pullDockerImage({
    env: {},
    spawnSyncFn: () => spawnResult(1),
    log: (m) => logs.push(m),
  });
  assert.equal(r.ok, false);
  assert.match(logs.join(''), /docker pull .* failed/);
});

test('pullDockerImage: TOTAL_RECALL_DOCKER_IMAGE override is honored', () => {
  const calls = [];
  pullDockerImage({
    env: { TOTAL_RECALL_DOCKER_IMAGE: 'ghcr.io/strvmarv/total-recall@sha256:abc' },
    spawnSyncFn: (cmd, args) => {
      calls.push(args);
      return spawnResult(0);
    },
    log: () => {},
  });
  assert.deepEqual(calls[0], ['image', 'inspect', 'ghcr.io/strvmarv/total-recall@sha256:abc']);
});
