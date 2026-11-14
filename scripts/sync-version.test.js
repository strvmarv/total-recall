import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { checkVersions, syncVersions, main } from './sync-version.js';

// A throwaway tree that holds the seven version files (eight version fields) at one version.
// The formatting mimics the real files: 2-space JSON, a lockfile whose root entry sits under
// packages[""], and a YAML manifest with a top-level `version:` line.
function writeTree(dir, version) {
  const w = (rel, text) => {
    fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
    fs.writeFileSync(path.join(dir, rel), text);
  };
  w('package.json', `{\n  "name": "@x/pkg",\n  "version": "${version}",\n  "type": "module"\n}\n`);
  w('package-lock.json',
    `{\n  "name": "@x/pkg",\n  "version": "${version}",\n  "lockfileVersion": 3,\n  "packages": {\n    "": {\n      "name": "@x/pkg",\n      "version": "${version}",\n      "license": "MIT"\n    },\n    "node_modules/dep": {\n      "version": "${version}"\n    }\n  }\n}\n`);
  for (const rel of ['.claude-plugin/plugin.json', '.github/plugin/plugin.json', '.cursor-plugin/plugin.json', '.codex-plugin/plugin.json']) {
    w(rel, `{\n  "name": "total-recall",\n  "version": "${version}",\n  "description": "d"\n}\n`);
  }
  w('hermes-plugin/plugin.yaml', `name: total-recall\nversion: ${version}\ndescription: d\n`);
}

function makeTree(version = '4.6.0') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-syncver-'));
  writeTree(dir, version);
  return dir;
}

// The files the guard accepts as version files; the default listing (`git ls-files`) has its own tests below.
const LISTED = [
  '.claude-plugin/plugin.json',
  '.github/plugin/plugin.json',
  '.cursor-plugin/plugin.json',
  '.codex-plugin/plugin.json',
  'hermes-plugin/plugin.yaml',
];
const check = (dir, extra = {}) => checkVersions({ root: dir, listManifests: () => LISTED, ...extra });

const read = (dir, rel) => fs.readFileSync(path.join(dir, rel), 'utf8');
const write = (dir, rel, text) => fs.writeFileSync(path.join(dir, rel), text);

// ---- Cycle 1: --check ----

test('checkVersions: a synced tree passes', () => {
  const dir = makeTree();
  const r = check(dir);
  assert.deepEqual(r, { ok: true, problems: [] });
});

const DRIFTS = [
  ['package-lock.json', 'package-lock.json: version', (t) => t.replace('"version": "4.6.0"', '"version": "4.5.0"')],
  ['package-lock.json', 'package-lock.json: packages[""].version', (t) => t.replace(/(    "": \{[^}]*?"version": ")4\.6\.0/, '$14.5.0')],
  ['.claude-plugin/plugin.json', '.claude-plugin/plugin.json: version', (t) => t.replace('4.6.0', '4.5.0')],
  ['.github/plugin/plugin.json', '.github/plugin/plugin.json: version', (t) => t.replace('4.6.0', '4.5.0')],
  ['.cursor-plugin/plugin.json', '.cursor-plugin/plugin.json: version', (t) => t.replace('4.6.0', '4.5.0')],
  ['.codex-plugin/plugin.json', '.codex-plugin/plugin.json: version', (t) => t.replace('4.6.0', '4.5.0')],
  ['hermes-plugin/plugin.yaml', 'hermes-plugin/plugin.yaml: version', (t) => t.replace('4.6.0', '4.5.0')],
];

for (const [file, field, mutate] of DRIFTS) {
  test(`checkVersions: names ${field} when it drifts`, () => {
    const dir = makeTree();
    write(dir, file, mutate(read(dir, file)));
    const r = check(dir);
    assert.equal(r.ok, false);
    assert.equal(r.problems.length, 1, JSON.stringify(r.problems));
    assert.equal(r.problems[0].field, field);
    assert.equal(r.problems[0].expected, '4.6.0');
    assert.equal(r.problems[0].actual, '4.5.0');
  });
}

test('checkVersions: names every drifting field, not only the first', () => {
  const dir = makeTree();
  for (const file of ['.claude-plugin/plugin.json', 'hermes-plugin/plugin.yaml']) {
    write(dir, file, read(dir, file).replace('4.6.0', '4.5.0'));
  }
  const r = check(dir);
  assert.deepEqual(r.problems.map((p) => p.field).sort(), [
    '.claude-plugin/plugin.json: version',
    'hermes-plugin/plugin.yaml: version',
  ]);
});

test('checkVersions: a missing version field is a problem, not a pass', () => {
  const dir = makeTree();
  write(dir, 'hermes-plugin/plugin.yaml', 'name: total-recall\n');
  const r = check(dir);
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, 'hermes-plugin/plugin.yaml: version');
  assert.equal(r.problems[0].actual, null);
});

test('checkVersions: a missing file is a problem', () => {
  const dir = makeTree();
  fs.rmSync(path.join(dir, '.cursor-plugin/plugin.json'));
  const r = check(dir);
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, '.cursor-plugin/plugin.json: version');
});

for (const bad of ['4.7', 'v4.7.0', '4.7.0-', '4.7.0-1', '4.7.0+build', '04.7.0']) {
  test(`checkVersions: package.json version ${JSON.stringify(bad)} is invalid`, () => {
    const dir = makeTree(bad);
    const r = check(dir);
    assert.equal(r.ok, false);
    assert.equal(r.problems[0].field, 'package.json: version');
    assert.match(r.problems[0].reason, /invalid/);
  });
}

test('checkVersions: package.json without a version is a problem', () => {
  const dir = makeTree();
  write(dir, 'package.json', '{\n  "name": "x"\n}\n');
  const r = check(dir);
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, 'package.json: version');
});

test('checkVersions: accepts a prerelease whose first identifier starts with a letter', () => {
  const dir = makeTree('4.7.0-beta.1');
  assert.deepEqual(check(dir), { ok: true, problems: [] });
});

// ---- Cycle 2: write mode ----

const ALL_FILES = [
  'package.json',
  'package-lock.json',
  '.claude-plugin/plugin.json',
  '.github/plugin/plugin.json',
  '.cursor-plugin/plugin.json',
  '.codex-plugin/plugin.json',
  'hermes-plugin/plugin.yaml',
];
const snapshot = (dir) => Object.fromEntries(ALL_FILES.map((f) => [f, read(dir, f)]));

test('syncVersions: sets all eight fields, package.json included', () => {
  const dir = makeTree('4.6.0');
  const r = syncVersions({ root: dir, version: '4.7.0' });
  assert.equal(r.ok, true);
  assert.deepEqual([...r.changed].sort(), [...ALL_FILES].sort());
  assert.deepEqual(check(dir), { ok: true, problems: [] });
  assert.equal(JSON.parse(read(dir, 'package.json')).version, '4.7.0');
  const lock = JSON.parse(read(dir, 'package-lock.json'));
  assert.equal(lock.version, '4.7.0');
  assert.equal(lock.packages[''].version, '4.7.0');
});

test('syncVersions: changes nothing but the version value, and leaves a colliding lockfile entry alone', () => {
  const dir = makeTree('4.6.0');
  const before = snapshot(dir);
  syncVersions({ root: dir, version: '4.7.0-beta.1' });
  const after = snapshot(dir);
  for (const f of ALL_FILES) {
    if (f === 'package-lock.json') continue;
    assert.equal(after[f], before[f].replace('4.6.0', '4.7.0-beta.1'), f);
  }
  // the lockfile: two fields change, the dependency entry that shares the old version string does not
  const lines = after['package-lock.json'].split('\n');
  const was = before['package-lock.json'].split('\n');
  const diff = lines.map((l, i) => (l === was[i] ? null : i)).filter((i) => i !== null);
  assert.equal(diff.length, 2);
  assert.match(after['package-lock.json'], /"node_modules\/dep": \{\n      "version": "4\.6\.0"/);
});

test('syncVersions: running it again with the same version changes no file', () => {
  const dir = makeTree('4.6.0');
  syncVersions({ root: dir, version: '4.7.0' });
  const before = snapshot(dir);
  const r = syncVersions({ root: dir, version: '4.7.0' });
  assert.equal(r.ok, true);
  assert.deepEqual(r.changed, []);
  assert.deepEqual(snapshot(dir), before);
});

for (const bad of ['4.7', 'v4.7.0', '4.7.0-', '4.7.0-1', '4.7.0-beta-x', '4.7.0+build', '', undefined]) {
  test(`syncVersions: rejects ${JSON.stringify(bad)} and writes nothing`, () => {
    const dir = makeTree('4.6.0');
    const before = snapshot(dir);
    const r = syncVersions({ root: dir, version: bad });
    assert.equal(r.ok, false);
    assert.match(r.error, /invalid version/);
    assert.deepEqual(snapshot(dir), before);
  });
}

test('syncVersions: writes nothing when one file cannot be patched', () => {
  const dir = makeTree('4.6.0');
  write(dir, 'hermes-plugin/plugin.yaml', 'name: total-recall\n');
  const before = snapshot(dir);
  const r = syncVersions({ root: dir, version: '4.7.0' });
  assert.equal(r.ok, false);
  assert.match(r.error, /hermes-plugin\/plugin\.yaml/);
  assert.deepEqual(snapshot(dir), before);
});

test('syncVersions: writes nothing when a file is missing', () => {
  const dir = makeTree('4.6.0');
  fs.rmSync(path.join(dir, '.codex-plugin/plugin.json'));
  const before = Object.fromEntries(ALL_FILES.filter((f) => f !== '.codex-plugin/plugin.json').map((f) => [f, read(dir, f)]));
  const r = syncVersions({ root: dir, version: '4.7.0' });
  assert.equal(r.ok, false);
  assert.match(r.error, /\.codex-plugin\/plugin\.json/);
  for (const [f, text] of Object.entries(before)) assert.equal(read(dir, f), text);
});

test('syncVersions: preserves CRLF line endings and a YAML trailing comment', () => {
  const dir = makeTree('4.6.0');
  write(dir, 'hermes-plugin/plugin.yaml', 'name: total-recall\r\nversion: "4.6.0" # keep\r\ndescription: d\r\n');
  syncVersions({ root: dir, version: '4.7.0' });
  assert.equal(read(dir, 'hermes-plugin/plugin.yaml'), 'name: total-recall\r\nversion: "4.7.0" # keep\r\ndescription: d\r\n');
});

// ---- Cycle 3: unlisted manifests and --expect ----

const KNOWN = LISTED;

test('checkVersions: a tracked plugin manifest outside the list is a problem and is named', () => {
  const dir = makeTree();
  const r = checkVersions({ root: dir, listManifests: () => [...KNOWN, 'opencode-plugin/plugin.json'] });
  assert.equal(r.ok, false);
  assert.equal(r.problems.length, 1);
  assert.equal(r.problems[0].field, 'opencode-plugin/plugin.json');
  assert.match(r.problems[0].reason, /not in the version file list/);
});

test('checkVersions: a plugin.yaml outside the list is a problem too', () => {
  const dir = makeTree();
  const r = checkVersions({ root: dir, listManifests: () => [...KNOWN, 'x/plugin.yaml'] });
  assert.equal(r.problems[0].field, 'x/plugin.yaml');
});

test('checkVersions: only the listed manifests are fine', () => {
  const dir = makeTree();
  assert.deepEqual(checkVersions({ root: dir, listManifests: () => KNOWN }), { ok: true, problems: [] });
});

test('checkVersions: other tracked files are ignored by the guard', () => {
  const dir = makeTree();
  const r = checkVersions({ root: dir, listManifests: () => [...KNOWN, 'src/Web/ClientApp/package.json', 'docs/plugin.md'] });
  assert.equal(r.ok, true);
});

test('checkVersions: the default listing is `git ls-files` of the root', () => {
  const dir = makeTree();
  const git = (...args) => spawnSync('git', ['-C', dir, ...args], { encoding: 'utf8' });
  git('init', '-q');
  fs.mkdirSync(path.join(dir, 'extra'));
  write(dir, 'extra/plugin.json', '{"version":"4.6.0"}\n');
  git('add', '-A');
  const r = checkVersions({ root: dir });
  assert.deepEqual(r.problems.map((p) => p.field), ['extra/plugin.json']);
});

test('checkVersions: an untracked stray manifest is not reported (only tracked files count)', () => {
  const dir = makeTree();
  const git = (...args) => spawnSync('git', ['-C', dir, ...args], { encoding: 'utf8' });
  git('init', '-q');
  git('add', '-A');
  fs.mkdirSync(path.join(dir, 'extra'));
  write(dir, 'extra/plugin.json', '{"version":"4.6.0"}\n');
  assert.equal(checkVersions({ root: dir }).ok, true);
});

test('checkVersions: fails closed when the tracked files cannot be listed', () => {
  const dir = makeTree(); // not a git repository
  const r = checkVersions({ root: dir });
  assert.equal(r.ok, false);
  assert.match(r.problems[0].reason, /cannot list tracked files/);
});

test('checkVersions: expect equal to package.json passes', () => {
  const dir = makeTree('4.7.0-beta.1');
  assert.equal(checkVersions({ root: dir, listManifests: () => KNOWN, expect: '4.7.0-beta.1' }).ok, true);
});

test('checkVersions: expect that differs from package.json is a problem naming both', () => {
  const dir = makeTree('4.6.0');
  const r = checkVersions({ root: dir, listManifests: () => KNOWN, expect: '4.7.0' });
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, 'package.json: version');
  assert.equal(r.problems[0].expected, '4.7.0');
  assert.equal(r.problems[0].actual, '4.6.0');
  assert.match(r.problems[0].reason, /does not match the expected/);
});

test('checkVersions: an expect that is not a valid version is a problem', () => {
  const dir = makeTree('4.6.0');
  const r = checkVersions({ root: dir, listManifests: () => KNOWN, expect: 'v4.6.0' });
  assert.equal(r.ok, false);
  assert.match(r.problems[0].reason, /invalid/);
});

// ---- the CLI ----

const run = (dir, ...args) => main([...args, '--root', dir, '--no-git']);

test('main: <version> writes and reports the changed files', () => {
  const dir = makeTree('4.6.0');
  const r = run(dir, '4.7.0');
  assert.equal(r.code, 0);
  assert.match(r.out, /4\.7\.0/);
  assert.equal(JSON.parse(read(dir, 'package.json')).version, '4.7.0');
});

test('main: <version> that is already current reports it and exits 0', () => {
  const dir = makeTree('4.6.0');
  const r = run(dir, '4.6.0');
  assert.equal(r.code, 0);
  assert.match(r.out, /already/i);
});

test('main: an invalid version exits 1 and says why', () => {
  const dir = makeTree('4.6.0');
  const r = run(dir, '4.7');
  assert.equal(r.code, 1);
  assert.match(r.err, /invalid version/);
});

test('main: --check passes on a synced tree and exits 0', () => {
  const dir = makeTree('4.6.0');
  const r = run(dir, '--check');
  assert.equal(r.code, 0);
  assert.match(r.out, /4\.6\.0/);
});

test('main: --check exits 1 and prints each drifting field', () => {
  const dir = makeTree('4.6.0');
  write(dir, '.cursor-plugin/plugin.json', read(dir, '.cursor-plugin/plugin.json').replace('4.6.0', '4.5.0'));
  const r = run(dir, '--check');
  assert.equal(r.code, 1);
  assert.match(r.err, /\.cursor-plugin\/plugin\.json: version/);
  assert.match(r.err, /4\.5\.0/);
});

test('main: --check --expect <v> compares package.json with the tag version', () => {
  const dir = makeTree('4.6.0');
  assert.equal(run(dir, '--check', '--expect', '4.6.0').code, 0);
  const bad = run(dir, '--check', '--expect', '4.7.0');
  assert.equal(bad.code, 1);
  assert.match(bad.err, /4\.7\.0/);
});

test('main: usage errors exit 2', () => {
  const dir = makeTree('4.6.0');
  assert.equal(run(dir).code, 2);
  assert.equal(run(dir, '--check', '4.7.0').code, 2);
  assert.equal(run(dir, '--expect', '4.7.0').code, 2);
  assert.equal(run(dir, '4.7.0', '4.8.0').code, 2);
  assert.equal(run(dir, '--bogus').code, 2);
});

test('script: runs as a CLI and exits with the result code', () => {
  const dir = makeTree('4.6.0');
  const script = fileURLToPath(new URL('./sync-version.js', import.meta.url));
  const ok = spawnSync(process.execPath, [script, '--check', '--root', dir, '--no-git'], { encoding: 'utf8' });
  assert.equal(ok.status, 0, ok.stderr);
  const bad = spawnSync(process.execPath, [script, '--check', '--expect', '9.9.9', '--root', dir, '--no-git'], { encoding: 'utf8' });
  assert.equal(bad.status, 1);
});
