// cursor/cli.test.js — the Cursor CLI wrapper: it must run bin/start.js with the store-determining variables removed.

import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { strippedEnv } from './cli.js';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const made = [];
after(() => { for (const d of made) fs.rmSync(d, { recursive: true, force: true }); });

test('strippedEnv removes the store-determining variables and keeps the rest', () => {
  const environ = { TOTAL_RECALL_HOME: '/a', Total_Recall_Db_Path: '/b', XDG_DATA_HOME: '/c', XDG_CONFIG_HOME: '/d', HERMES_HOME: '/e', HOME: 'C:/h', USERPROFILE: 'C:/u', PATH: '/bin', CURSOR_PROJECT_DIR: '/p' };
  const win = strippedEnv(environ, 'win32');
  assert.deepEqual(Object.keys(win).sort(), ['CURSOR_PROJECT_DIR', 'PATH', 'USERPROFILE']);
  const posix = strippedEnv(environ, 'linux');
  assert.deepEqual(Object.keys(posix).sort(), ['CURSOR_PROJECT_DIR', 'HOME', 'PATH', 'USERPROFILE']);
  assert.equal('TOTAL_RECALL_HOME' in environ, true, 'the input is not mutated');
});

test('process: the wrapper runs bin/start.js with the arguments, inherited stdio, the exit code, and a stripped environment', () => {
  // A tree with the real wrapper and hook but a stub bin/start.js that reports what it was given.
  const tree = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-cursor-cli-'));
  made.push(tree);
  fs.writeFileSync(path.join(tree, 'package.json'), '{"type":"module"}\n');
  for (const rel of ['cursor/cli.js', 'cursor/hook.js']) {
    fs.mkdirSync(path.dirname(path.join(tree, rel)), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, rel), path.join(tree, rel));
  }
  fs.mkdirSync(path.join(tree, 'bin'));
  fs.writeFileSync(path.join(tree, 'bin', 'start.js'), [
    "const keys = Object.keys(process.env).filter((k) => /^(TOTAL_RECALL_|XDG_|HERMES_HOME$)/i.test(k) || (process.platform === 'win32' && /^HOME$/i.test(k)));",
    "process.stdout.write(JSON.stringify({ args: process.argv.slice(2), leaked: keys, keep: process.env.KEEP_ME }));",
    "process.exitCode = 7;",
  ].join('\n'));
  const r = spawnSync(process.execPath, [path.join(tree, 'cursor', 'cli.js'), 'compact', '--run'], {
    encoding: 'utf8',
    env: { ...process.env, TOTAL_RECALL_HOME: '/x', TOTAL_RECALL_DB_PATH: '/y', XDG_DATA_HOME: '/z', HERMES_HOME: '/h', HOME: os.homedir(), KEEP_ME: 'yes' },
  });
  assert.equal(r.status, 7);
  const out = JSON.parse(r.stdout);
  assert.deepEqual(out.args, ['compact', '--run']);
  assert.deepEqual(out.leaked, []);
  assert.equal(out.keep, 'yes');
});
