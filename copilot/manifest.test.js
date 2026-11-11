// copilot/manifest.test.js — guards the Copilot CLI plugin manifest, its hooks file and the packaging
// that ships them. (The release version sync across every manifest lives in codex/manifest.test.js.)

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const readText = (rel) => fs.readFileSync(path.join(root, rel), 'utf8');
const readJson = (rel) => JSON.parse(readText(rel));

const pkg = readJson('package.json');
const manifest = readJson('.github/plugin/plugin.json');
const hooksFile = readJson('copilot/hooks.json');

test('copilot manifest identifies the plugin and carries the release version', () => {
  assert.equal(manifest.name, 'total-recall');
  assert.equal(typeof manifest.description, 'string');
  assert.equal(manifest.version, pkg.version);
  assert.ok(manifest.keywords.includes('copilot-cli'));
});

for (const key of ['skills', 'hooks']) {
  test(`copilot manifest: ${key} is a ./-relative path inside the plugin root that exists`, () => {
    const value = manifest[key];
    assert.equal(typeof value, 'string');
    assert.ok(value.startsWith('./'), `${key} starts with ./`);
    const resolved = path.resolve(root, value);
    const rel = path.relative(root, resolved);
    assert.ok(rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel), `${key} escapes the plugin root`);
    assert.ok(fs.existsSync(resolved), `${key} target exists`);
  });
}

test('copilot manifest declares no mcpServers: a root .mcp.json always wins, so it would be dead', () => {
  assert.equal('mcpServers' in manifest, false);
});

test('the dead .copilot-plugin/ manifest (Copilot never read it) is gone', () => {
  assert.equal(fs.existsSync(path.join(root, '.copilot-plugin')), false);
});

test('copilot hooks file declares exactly sessionStart and userPromptSubmitted', () => {
  assert.equal(hooksFile.version, 1);
  assert.deepEqual(Object.keys(hooksFile.hooks).sort(), ['sessionStart', 'userPromptSubmitted']);
});

for (const [event, entries] of Object.entries(readJson('copilot/hooks.json').hooks)) {
  test(`copilot hooks: ${event} runs node with the braced \${PLUGIN_ROOT} (PowerShell leaves $VAR empty) and a numeric timeout`, () => {
    assert.equal(entries.length, 1);
    const [entry] = entries;
    assert.equal(entry.type, 'command');
    assert.match(entry.command, /^node "\$\{PLUGIN_ROOT\}\/copilot\/hook\.js" [a-z-]+$/);
    assert.equal(/\$CLAUDE_PLUGIN_ROOT|\$PLUGIN_ROOT/.test(entry.command), false, 'no unbraced variable');
    assert.equal(typeof entry.timeoutSec, 'number');
  });
}

test('package.json ships the manifest and hooks and runs the copilot tests', () => {
  assert.ok(pkg.files.includes('copilot/'));
  assert.ok(pkg.files.includes('.github/plugin/'));
  assert.equal(pkg.files.includes('.copilot-plugin/'), false);
  assert.match(pkg.scripts['test:shim'], /\bcopilot\//);
});

test('.mcp.json (shared with Claude Code, and read by Copilot) keeps its shape', () => {
  const server = readJson('.mcp.json').mcpServers['total-recall'];
  assert.equal(server.command, 'node');
  assert.equal(server.cwd, '${CLAUDE_PLUGIN_ROOT}');
});
