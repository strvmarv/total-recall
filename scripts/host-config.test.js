import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  recordChange, takeChanges, formatSummary, formatLogEntry, appendLog,
} from './lib/change-log.js';

const P = '[total-recall:postinstall]';

function tmp(prefix = 'tr-hostcfg-') {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

// --- change-log: the recorder, the summary text and the append-only log ---

test('recordChange / takeChanges: returns what was recorded, in order, and clears it', () => {
  takeChanges();
  recordChange('Zed', 'added', '/z/settings.json');
  recordChange('Hermes', 'switched', 'memory.provider');
  assert.deepEqual(takeChanges(), [
    { tool: 'Zed', action: 'added', detail: '/z/settings.json' },
    { tool: 'Hermes', action: 'switched', detail: 'memory.provider' },
  ]);
  assert.deepEqual(takeChanges(), []);
});

test('formatSummary: one line per change with tool, action and path', () => {
  const text = formatSummary([
    { tool: 'Zed', action: 'added', detail: '/z/settings.json' },
    { tool: 'Gemini CLI', action: 'rewrote', detail: '/g/settings.json' },
  ]);
  assert.equal(text, [
    `${P} Changed other tools (2 changes):`,
    `${P}   Zed: added /z/settings.json`,
    `${P}   Gemini CLI: rewrote /g/settings.json`,
  ].join('\n'));
});

test('formatSummary: a failed step is a line of its own with its message', () => {
  const text = formatSummary([{ tool: 'Hermes', action: 'failed', detail: 'EACCES: denied' }]);
  assert.match(text, /\n\[total-recall:postinstall\]   Hermes: failed: EACCES: denied$/);
});

test('formatSummary: nothing recorded says so in one line', () => {
  assert.equal(formatSummary([]), `${P} No changes were made to other tools.`);
});

test('formatLogEntry: heading with date, time and version, then the summary lines', () => {
  const entry = formatLogEntry('LINES', { version: '4.7.1', now: new Date('2026-11-14T10:20:30Z') });
  assert.equal(entry, '## 2026-11-14T10:20:30Z total-recall 4.7.1\nLINES\n\n');
});

test('appendLog: creates postinstall.log, and a second call appends without touching the first entry', () => {
  const dir = path.join(tmp(), 'data'); // does not exist yet
  assert.equal(appendLog(dir, 'first\n'), true);
  assert.equal(appendLog(dir, 'second\n'), true);
  assert.equal(fs.readFileSync(path.join(dir, 'postinstall.log'), 'utf8'), 'first\nsecond\n');
});

test('appendLog: an unknown or unwritable directory returns false and never throws', () => {
  assert.equal(appendLog(null, 'x'), false);
  const file = path.join(tmp(), 'a-file');
  fs.writeFileSync(file, '');
  assert.equal(appendLog(path.join(file, 'sub'), 'x'), false); // parent is a regular file
});

// --- runHostConfig: runs the installers, prints the summary, keeps the log ---

import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { runHostConfig } from './lib/host-config.js';

const REPO = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');

function fakeStderr() {
  const chunks = [];
  return { write: (s) => { chunks.push(s); return true; }, text: () => chunks.join('') };
}

async function installerThatRecords() {
  recordChange('Zed', 'added', '/z/settings.json');
}

test('runHostConfig: prints the summary and appends the same lines to the log under TOTAL_RECALL_HOME', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: err, version: '9.9.9' });
  const summary = formatSummary([{ tool: 'Zed', action: 'added', detail: '/z/settings.json' }]);
  assert.equal(err.text(), summary + '\n');
  const log = fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8');
  assert.match(log, /^## \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ total-recall 9\.9\.9\n/);
  assert.ok(log.includes(summary));
});

test('runHostConfig: a second run appends and leaves the first entry as it was', async () => {
  takeChanges();
  const home = tmp();
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: fakeStderr(), version: '1' });
  const first = fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8');
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: fakeStderr(), version: '2' });
  const both = fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8');
  assert.ok(both.startsWith(first));
  assert.ok(both.length > first.length);
});

test('runHostConfig: nothing written and nothing failed says so in one line and adds no log entry', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  await runHostConfig([async () => {}], { env: { TOTAL_RECALL_HOME: home }, stderr: err, version: '1' });
  assert.equal(err.text(), `${P} No changes were made to other tools.\n`);
  assert.equal(fs.existsSync(path.join(home, 'postinstall.log')), false);
});

test('runHostConfig: TOTAL_RECALL_SKIP_HOST_CONFIG=1 runs no installer, says why, and writes no log', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  let called = false;
  await runHostConfig([async () => { called = true; recordChange('Zed', 'added', '/x'); }],
    { env: { TOTAL_RECALL_HOME: home, TOTAL_RECALL_SKIP_HOST_CONFIG: '1' }, stderr: err, version: '1' });
  assert.equal(called, false);
  assert.match(err.text(), /TOTAL_RECALL_SKIP_HOST_CONFIG=1/);
  assert.match(err.text(), /skipped/i);
  assert.equal(fs.existsSync(path.join(home, 'postinstall.log')), false);
});

test('runHostConfig: the skip switch counts only the exact value 1', async () => {
  for (const value of ['0', 'true', '', 'yes']) {
    takeChanges();
    let called = false;
    await runHostConfig([async () => { called = true; }],
      { env: { TOTAL_RECALL_HOME: tmp(), TOTAL_RECALL_SKIP_HOST_CONFIG: value }, stderr: fakeStderr(), version: '1' });
    assert.equal(called, true, `value ${JSON.stringify(value)}`);
  }
});

test('runHostConfig: an unwritable data directory still prints the summary and does not throw', async () => {
  takeChanges();
  const file = path.join(tmp(), 'a-file');
  fs.writeFileSync(file, '');
  const err = fakeStderr();
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: path.join(file, 'sub') }, stderr: err, version: '1' });
  assert.match(err.text(), /Zed: added \/z\/settings\.json/);
});

test('runHostConfig: an unknown data directory (no home variables) still prints the summary', async () => {
  takeChanges();
  const err = fakeStderr();
  await runHostConfig([installerThatRecords], { env: {}, stderr: err, version: '1' });
  assert.match(err.text(), /Zed: added/);
});

test('runHostConfig: an installer that throws becomes a failed line and the others still run', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  async function installExploding() { throw new Error('boom'); }
  await runHostConfig([installExploding, installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: err, version: '1' });
  assert.match(err.text(), /installExploding: failed: boom/);
  assert.match(err.text(), /Zed: added/);
  assert.match(fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8'), /failed: boom/);
});

// --- postinstall.js as npm runs it: one child process per case, from a temp copy of the package ---
// The copy sits under node_modules/ so isRepoSelf() is false (the Kiro steering, the Windsurf rules and
// GEMINI.md are written only then). fetch-binary.js is replaced by a stub that keeps resolveDataDir and
// writes a marker from ensureBinary, so nothing is downloaded. A fake `hermes` on PATH records its calls.

const STUB_FETCH = `export * from './fetch-binary.real.js';
import fs from 'node:fs';
export async function ensureBinary() {
  fs.writeFileSync(process.env.TR_ENGINE_MARKER, 'ran');
  return { ok: true };
}
`;

function makePackage() {
  const root = tmp('tr-pkg-');
  const pkg = path.join(root, 'node_modules', '@strvmarv', 'total-recall');
  fs.mkdirSync(pkg, { recursive: true });
  fs.cpSync(path.join(REPO, 'scripts'), path.join(pkg, 'scripts'), {
    recursive: true,
    filter: (src) => !src.endsWith('.test.js'),
  });
  fs.renameSync(path.join(pkg, 'scripts', 'fetch-binary.js'), path.join(pkg, 'scripts', 'fetch-binary.real.js'));
  fs.writeFileSync(path.join(pkg, 'scripts', 'fetch-binary.js'), STUB_FETCH);
  fs.cpSync(path.join(REPO, 'hermes-plugin'), path.join(pkg, 'hermes-plugin'), { recursive: true });
  fs.cpSync(path.join(REPO, '.kiro', 'steering'), path.join(pkg, '.kiro', 'steering'), { recursive: true });
  fs.copyFileSync(path.join(REPO, 'package.json'), path.join(pkg, 'package.json'));
  return pkg;
}

/** Runs the package's postinstall.js in a fresh sandbox. `env` adds to a minimal environment. */
function runPostinstall(extraEnv = {}, { prepare } = {}) {
  const pkg = makePackage();
  const home = tmp('tr-home-');
  const bin = tmp('tr-bin-');
  const hermesLog = path.join(bin, 'hermes-calls.txt');
  fs.writeFileSync(path.join(bin, 'hermes'), `#!/bin/sh\necho "$@" >> "${hermesLog}"\necho /fake/hermes/config.yaml\n`, { mode: 0o755 });
  const marker = path.join(home, 'engine-marker');
  if (prepare) prepare({ home });
  const r = spawnSync(process.execPath, [path.join(pkg, 'scripts', 'postinstall.js')], {
    encoding: 'utf8',
    env: {
      PATH: `${bin}:/usr/bin:/bin`,
      HOME: home,
      USERPROFILE: home,
      TR_ENGINE_MARKER: marker,
      ...extraEnv,
    },
  });
  return {
    ...r, pkg, home, marker, hermesCalls: () => (fs.existsSync(hermesLog) ? fs.readFileSync(hermesLog, 'utf8') : ''),
    log: () => { const f = path.join(extraEnv.TOTAL_RECALL_HOME ?? path.join(home, '.total-recall'), 'postinstall.log'); return fs.existsSync(f) ? fs.readFileSync(f, 'utf8') : null; },
  };
}

test('postinstall.js: no other tool detected prints the one-line answer, runs the engine step, exits 0, writes no log', () => {
  const r = runPostinstall();
  assert.equal(r.status, 0, r.stderr);
  assert.ok(r.stderr.trimEnd().endsWith(`${P} No changes were made to other tools.`), r.stderr);
  assert.equal(fs.readFileSync(r.marker, 'utf8'), 'ran');
  assert.equal(r.log(), null);
});

test('postinstall.js: TOTAL_RECALL_SKIP_HOST_CONFIG=1 still runs the engine step, exits 0 and leaves every other tool alone', () => {
  const r = runPostinstall({ TOTAL_RECALL_SKIP_HOST_CONFIG: '1' }, {
    prepare: ({ home }) => { fs.mkdirSync(path.join(home, '.gemini'), { recursive: true }); },
  });
  assert.equal(r.status, 0, r.stderr);
  assert.equal(fs.readFileSync(r.marker, 'utf8'), 'ran');
  assert.match(r.stderr, /TOTAL_RECALL_SKIP_HOST_CONFIG=1/);
  assert.deepEqual(fs.readdirSync(path.join(r.home, '.gemini')), []);
  assert.equal(r.hermesCalls(), '');
  assert.equal(r.log(), null);
});
