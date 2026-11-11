// cursor/manifest.test.js — guards the Cursor plugin manifest, its hooks and MCP files and the packaging
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
const manifest = readJson('.cursor-plugin/plugin.json');
const hooksFile = readJson('cursor/hooks.json');
const mcpFile = readJson('cursor/mcp.json');

test('cursor manifest identifies the plugin and carries the release version', () => {
  assert.equal(manifest.name, 'total-recall');
  assert.equal(typeof manifest.description, 'string');
  assert.equal(manifest.version, pkg.version);
  assert.ok(manifest.keywords.includes('cursor'));
});

for (const key of ['skills', 'hooks', 'mcpServers']) {
  test(`cursor manifest: ${key} is a ./-relative path inside the plugin root that exists`, () => {
    const value = manifest[key];
    assert.equal(typeof value, 'string');
    assert.ok(value.startsWith('./'), `${key} starts with ./`);
    const resolved = path.resolve(root, value);
    const rel = path.relative(root, resolved);
    assert.ok(rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel), `${key} escapes the plugin root`);
    assert.ok(fs.existsSync(resolved), `${key} target exists`);
  });
}

test('cursor hooks file declares exactly sessionStart and postToolUse', () => {
  assert.equal(hooksFile.version, 1);
  assert.deepEqual(Object.keys(hooksFile.hooks).sort(), ['postToolUse', 'sessionStart']);
});

for (const [event, entries] of Object.entries(hooksFile.hooks)) {
  test(`cursor hooks: ${event} runs node with the braced \${CURSOR_PLUGIN_ROOT} (PowerShell leaves $VAR empty) and a numeric timeout`, () => {
    assert.equal(entries.length, 1);
    const [entry] = entries;
    assert.match(entry.command, /^node "\$\{CURSOR_PLUGIN_ROOT\}\/cursor\/hook\.js" [a-z-]+$/);
    assert.equal(/\$CLAUDE_PLUGIN_ROOT|\$CURSOR_PLUGIN_ROOT/.test(entry.command), false, 'no unbraced variable');
    assert.equal(typeof entry.timeout, 'number');
  });
}

test('cursor mcp.json starts bin/start.js through the braced plugin root, with no cwd (the workspace is the default) and no env', () => {
  assert.deepEqual(Object.keys(mcpFile.mcpServers), ['total-recall']);
  const server = mcpFile.mcpServers['total-recall'];
  assert.equal(server.command, 'node');
  assert.deepEqual(server.args, ['${CURSOR_PLUGIN_ROOT}/bin/start.js']);
  assert.equal('cwd' in server, false);
  assert.equal('env' in server, false);
});

test('hooks/hooks-cursor.json (hand-wired setups) uses only the braced plugin root variable', () => {
  const text = readText('hooks/hooks-cursor.json');
  assert.equal(/\$CURSOR_PLUGIN_ROOT/.test(text), false, 'no unbraced $CURSOR_PLUGIN_ROOT');
  assert.ok(text.includes('${CURSOR_PLUGIN_ROOT}'));
});

test('package.json ships the manifest, hooks and MCP file and runs the cursor tests', () => {
  assert.ok(pkg.files.includes('cursor/'));
  assert.ok(pkg.files.includes('.cursor-plugin/'));
  assert.match(pkg.scripts['test:shim'], /\bcursor\//);
});

test('.mcp.json (shared with Claude Code) keeps its shape', () => {
  const server = readJson('.mcp.json').mcpServers['total-recall'];
  assert.equal(server.command, 'node');
  assert.equal(server.cwd, '${CLAUDE_PLUGIN_ROOT}');
});
