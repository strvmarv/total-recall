import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { SHIM_TEST_TARGETS, expandTargets, buildArgs, main } from './run-shim-tests.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.join(HERE, '..');

// A throwaway tree: `files` maps a relative path to its text.
const made = [];
after(() => { for (const dir of made) fs.rmSync(dir, { recursive: true, force: true }); });
function tree(files) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'run-shim-tests-'));
  made.push(dir);
  for (const [rel, text] of Object.entries(files)) {
    fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
    fs.writeFileSync(path.join(dir, rel), text);
  }
  return dir;
}
const PASS = "import { test } from 'node:test';\ntest('ok', () => {});\n";
const FAIL = "import { test } from 'node:test';\ntest('bad', () => { throw new Error('bad'); });\n";
const sink = () => { const lines = []; return { lines, write: (s) => { lines.push(String(s)); return true; } }; };

test('a directory expands to its test files only, recursively, without node_modules', () => {
  const root = tree({
    'a/one.test.js': PASS, 'a/sub/two.test.mjs': PASS, 'a/sub/three.test.cjs': PASS,
    'a/fixtures/helper.js': 'export {};\n', 'a/notes.md': 'x', 'a/node_modules/dep/x.test.js': PASS,
  });
  assert.deepEqual(expandTargets(['a/'], { root }), ['a/one.test.js', 'a/sub/three.test.cjs', 'a/sub/two.test.mjs']);
});

test('files and directories mix, come back sorted and without duplicates', () => {
  const root = tree({ 'z.test.js': PASS, 'a/b.test.js': PASS, 'a/c.test.js': PASS });
  assert.deepEqual(expandTargets(['z.test.js', 'a/', 'a/b.test.js'], { root }), ['a/b.test.js', 'a/c.test.js', 'z.test.js']);
});

test('no path handed to Node is absolute, holds a backslash or a glob character', () => {
  const root = tree({ 'a/b.test.js': PASS, 'a/sub dir/c.test.js': PASS, 'd.test.js': PASS });
  for (const p of expandTargets(['a/', 'd.test.js'], { root })) {
    assert.equal(path.isAbsolute(p), false, p);
    assert.equal(p.includes('\\'), false, p);
    assert.equal(p.includes('*'), false, p);
  }
});

test('a missing target and an empty directory throw an error that names them', () => {
  const root = tree({ 'empty/readme.md': 'x', 'a.test.js': PASS });
  assert.throws(() => expandTargets(['nope.test.js'], { root }), /nope\.test\.js/);
  assert.throws(() => expandTargets(['gone/'], { root }), /gone\//);
  assert.throws(() => expandTargets(['empty/'], { root }), /empty\//);
});

test('buildArgs puts --test, then the extra arguments, then the files', () => {
  assert.deepEqual(buildArgs(['--test-name-pattern', 'x'], ['a.test.js', 'b.test.js']),
    ['--test', '--test-name-pattern', 'x', 'a.test.js', 'b.test.js']);
  assert.deepEqual(buildArgs([], ['a.test.js']), ['--test', 'a.test.js']);
});

test('main returns zero for a passing test file and non-zero for a failing one', () => {
  const root = tree({ 'pass.test.js': PASS, 'bad/fail.test.js': FAIL });
  assert.equal(main([], { root, targets: ['pass.test.js'], stdio: 'ignore' }), 0);
  assert.equal(main([], { root, targets: ['pass.test.js', 'bad/'], stdio: 'ignore' }), 1);
});

test('main still runs the files when the caller is itself a node --test child (NODE_TEST_CONTEXT is set)', () => {
  const root = tree({ 'bad/fail.test.js': FAIL });
  const before = process.env.NODE_TEST_CONTEXT;
  process.env.NODE_TEST_CONTEXT = 'child-v8';
  try {
    // A nested run that inherited the variable skips its files and exits 0.
    assert.equal(main([], { root, targets: ['bad/'], stdio: 'ignore' }), 1);
  } finally {
    if (before === undefined) delete process.env.NODE_TEST_CONTEXT; else process.env.NODE_TEST_CONTEXT = before;
  }
});

test('main prints the name of a missing target and returns 1', () => {
  const root = tree({ 'a.test.js': PASS });
  const stderr = sink();
  assert.equal(main([], { root, targets: ['missing.test.js'], stdio: 'ignore', stderr }), 1);
  assert.match(stderr.lines.join(''), /missing\.test\.js/);
});

test('main passes extra arguments to node --test', () => {
  const root = tree({
    'a.test.js': "import { test } from 'node:test';\ntest('wanted', () => {});\ntest('unwanted', () => { throw new Error('ran'); });\n",
  });
  assert.equal(main(['--test-name-pattern', '^wanted$'], { root, targets: ['a.test.js'], stdio: 'ignore' }), 0);
  assert.equal(main([], { root, targets: ['a.test.js'], stdio: 'ignore' }), 1);
});

test('importing the module runs nothing', () => {
  // Importing must not start the suite (it would run this file again, without end).
  const r = spawnSync(process.execPath, ['--input-type=module', '-e', `await import(${JSON.stringify(new URL('./run-shim-tests.js', import.meta.url).href)}); console.log('imported');`],
    { encoding: 'utf8', env: { ...process.env, NODE_TEST_CONTEXT: '' } });
  assert.equal(r.status, 0);
  assert.equal(r.stdout.trim(), 'imported');
});

test('the package script runs this runner, so no Node version and no shell decides what is run', () => {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO, 'package.json'), 'utf8'));
  assert.equal(pkg.scripts['test:shim'], 'node scripts/run-shim-tests.js');
});

test('every real target expands to at least one test file', () => {
  for (const target of SHIM_TEST_TARGETS) {
    assert.ok(expandTargets([target], { root: REPO }).length >= 1, target);
  }
  assert.ok(SHIM_TEST_TARGETS.includes('scripts/run-shim-tests.test.js'));
});
