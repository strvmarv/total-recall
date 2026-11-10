// codex/manifest.test.js — guards the Codex plugin manifest, the packaging that ships it,
// and the release version sync across every manifest in the repo.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const readText = (rel) => fs.readFileSync(path.join(root, rel), 'utf8');
const readJson = (rel) => JSON.parse(readText(rel));

const pkg = readJson('package.json');
const codexManifest = readJson('.codex-plugin/plugin.json');

// ---- release version sync (AGENTS.md "Version sync": every manifest carries package.json's version) ----

test('version sync: package-lock.json top-level version', () => {
  assert.equal(readJson('package-lock.json').version, pkg.version);
});

test('version sync: package-lock.json packages[""].version', () => {
  assert.equal(readJson('package-lock.json').packages[''].version, pkg.version);
});

for (const rel of [
  '.claude-plugin/plugin.json',
  '.copilot-plugin/plugin.json',
  '.cursor-plugin/plugin.json',
  '.codex-plugin/plugin.json',
]) {
  test(`version sync: ${rel}`, () => {
    assert.equal(readJson(rel).version, pkg.version);
  });
}

test('version sync: hermes-plugin/plugin.yaml', () => {
  const m = /^version:\s*(\S+)\s*$/m.exec(readText('hermes-plugin/plugin.yaml'));
  assert.ok(m, 'plugin.yaml has a top-level version line');
  assert.equal(m[1], pkg.version);
});

// ---- the Codex manifest ----

test('codex manifest identifies the plugin', () => {
  assert.equal(codexManifest.name, 'total-recall');
  assert.equal(typeof codexManifest.description, 'string');
});

for (const key of ['skills', 'hooks', 'mcpServers']) {
  test(`codex manifest: ${key} is a ./-relative path that stays inside the plugin root`, () => {
    const value = codexManifest[key];
    assert.equal(typeof value, 'string');
    assert.ok(value.startsWith('./'), `${key} starts with ./`);
    const resolved = path.resolve(root, value);
    const rel = path.relative(root, resolved);
    assert.ok(rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel), `${key} escapes the plugin root`);
  });
}

// ---- packaging ----

test('package.json ships the Codex files and runs their tests', () => {
  assert.ok(pkg.files.includes('codex/'));
  assert.ok(pkg.files.includes('.codex-plugin/'));
  assert.ok(pkg.keywords.includes('codex'));
  assert.ok(pkg.scripts['test:shim'].includes(' codex/ '));
  assert.ok(pkg.scripts['test:shim'].includes(' pi/ '));
  assert.equal(pkg.dependencies, undefined);
});

test('.gitattributes pins the generated MCP file and its source to LF', () => {
  const lines = readText('.gitattributes').split(/\r?\n/).map((l) => l.trim());
  assert.ok(lines.includes('codex/mcp.json text eol=lf'));
  assert.ok(lines.includes('codex/bootstrap.cjs text eol=lf'));
});
