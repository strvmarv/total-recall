import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));

test('pi manifest declares exactly the extension and it exists', () => {
  assert.deepEqual(pkg.pi, { extensions: ['./pi/extension.js'] });
  assert.ok(fs.existsSync(path.join(root, 'pi', 'extension.js')));
});

test('pi manifest declares no skills (a skill would be advertised to subagent children too)', () => {
  assert.equal(pkg.pi.skills, undefined);
});

test('files ships what the extension reads at runtime', () => {
  for (const dir of ['pi/', 'skills/', 'scripts/', 'bin/']) assert.ok(pkg.files.includes(dir), dir);
});

test('keywords include pi-package (eligibility for the pi package gallery)', () => {
  assert.ok(pkg.keywords.includes('pi-package'));
});

test('test:shim runs the pi tests and no dependency was added', () => {
  assert.ok(pkg.scripts['test:shim'].includes(' pi/ '));
  assert.equal(pkg.dependencies, undefined);
});
