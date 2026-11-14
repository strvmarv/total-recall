import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { checkVersions } from './sync-version.js';

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

const read = (dir, rel) => fs.readFileSync(path.join(dir, rel), 'utf8');
const write = (dir, rel, text) => fs.writeFileSync(path.join(dir, rel), text);

// ---- Cycle 1: --check ----

test('checkVersions: a synced tree passes', () => {
  const dir = makeTree();
  const r = checkVersions({ root: dir });
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
    const r = checkVersions({ root: dir });
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
  const r = checkVersions({ root: dir });
  assert.deepEqual(r.problems.map((p) => p.field).sort(), [
    '.claude-plugin/plugin.json: version',
    'hermes-plugin/plugin.yaml: version',
  ]);
});

test('checkVersions: a missing version field is a problem, not a pass', () => {
  const dir = makeTree();
  write(dir, 'hermes-plugin/plugin.yaml', 'name: total-recall\n');
  const r = checkVersions({ root: dir });
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, 'hermes-plugin/plugin.yaml: version');
  assert.equal(r.problems[0].actual, null);
});

test('checkVersions: a missing file is a problem', () => {
  const dir = makeTree();
  fs.rmSync(path.join(dir, '.cursor-plugin/plugin.json'));
  const r = checkVersions({ root: dir });
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, '.cursor-plugin/plugin.json: version');
});

for (const bad of ['4.7', 'v4.7.0', '4.7.0-', '4.7.0-1', '4.7.0+build', '04.7.0']) {
  test(`checkVersions: package.json version ${JSON.stringify(bad)} is invalid`, () => {
    const dir = makeTree(bad);
    const r = checkVersions({ root: dir });
    assert.equal(r.ok, false);
    assert.equal(r.problems[0].field, 'package.json: version');
    assert.match(r.problems[0].reason, /invalid/);
  });
}

test('checkVersions: package.json without a version is a problem', () => {
  const dir = makeTree();
  write(dir, 'package.json', '{\n  "name": "x"\n}\n');
  const r = checkVersions({ root: dir });
  assert.equal(r.ok, false);
  assert.equal(r.problems[0].field, 'package.json: version');
});

test('checkVersions: accepts a prerelease whose first identifier starts with a letter', () => {
  const dir = makeTree('4.7.0-beta.1');
  assert.deepEqual(checkVersions({ root: dir }), { ok: true, problems: [] });
});
