// scripts/run-shim-tests.js — `npm run test:shim`: the Node tests that need no .NET build.
// Zero dependencies, ESM.
//
//   node scripts/run-shim-tests.js [node --test options]
//
// Why a script and not a list of directories in package.json: `node --test <directory>` works on
// Node 20 and not on Node 24 (which treats every argument as a file or a glob), a quoted glob
// needs Node 21 or newer, and an unquoted glob is expanded by `sh` and by nothing on Windows.
// So this script expands the list itself and hands Node explicit relative file paths with forward
// slashes, which every Node >= 20 accepts on every operating system.
//
// A directory is searched for `*.test.js`, `*.test.mjs` and `*.test.cjs` files. That rule
// deliberately matches the `*.test.js` globs this repository's profile used to list, and is
// narrower than Node 20's own directory rule; `bin/shim/fixtures/` holds no such file.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');

// Directories end in `/`. Add a new test file here when it sits outside these directories.
export const SHIM_TEST_TARGETS = [
  'bin/shim/',
  'pi/',
  'codex/',
  'skills/develop/',
  'copilot/',
  'cursor/',
  'scripts/fetch-binary.test.js',
  'scripts/sync-version.test.js',
  'scripts/release-workflow.test.js',
  'scripts/host-docs.test.js',
  'scripts/host-config.test.js',
  'scripts/run-shim-tests.test.js',
  'hooks/hooks.config.test.js',
];

const TEST_FILE = /\.test\.(?:js|mjs|cjs)$/;

function walk(dir, rel, out) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules') continue;
    const childRel = `${rel}/${entry.name}`;
    if (entry.isDirectory()) walk(path.join(dir, entry.name), childRel, out);
    else if (TEST_FILE.test(entry.name)) out.add(childRel);
  }
}

// Root-relative paths with `/`, sorted, no duplicates. A target that is missing, and a directory
// without a test file, throw: running fewer tests than listed must never pass quietly (and
// `node --test` with no file would fall back to discovering tests in the whole working directory).
export function expandTargets(targets, { root = REPO_ROOT } = {}) {
  const out = new Set();
  for (const target of targets) {
    const clean = target.replace(/\/+$/, '');
    const abs = path.join(root, clean);
    let stat;
    try { stat = fs.statSync(abs); } catch { throw new Error(`test target not found: ${target}`); }
    if (stat.isDirectory()) {
      const found = new Set();
      walk(abs, clean, found);
      if (found.size === 0) throw new Error(`test target has no test files: ${target}`);
      for (const f of found) out.add(f);
    } else {
      out.add(clean);
    }
  }
  return [...out].sort();
}

export function buildArgs(extra, files) {
  return ['--test', ...extra, ...files];
}

// Returns the exit status. `stdio` and `stderr` are injectable for tests.
export function main(argv, { root = REPO_ROOT, targets = SHIM_TEST_TARGETS, stdio = 'inherit', stderr = process.stderr } = {}) {
  let files;
  try {
    files = expandTargets(targets, { root });
  } catch (e) {
    stderr.write(`${e.message}\n`);
    return 1;
  }
  // A run started from inside a `node --test` file inherits NODE_TEST_CONTEXT, and a nested run
  // with it set skips its files and exits 0.
  const env = { ...process.env };
  delete env.NODE_TEST_CONTEXT;
  const result = spawnSync(process.execPath, buildArgs(argv, files), { cwd: root, stdio, env });
  if (result.error) {
    stderr.write(`could not run node --test: ${result.error.message}\n`);
    return 1;
  }
  return result.status ?? 1;
}

// Run as a command, not when imported. Both paths are resolved, so a symlinked checkout or script
// still runs (a gate that exits 0 without running would fail open).
function isMainModule() {
  const self = fs.realpathSync(fileURLToPath(import.meta.url));
  // `node scripts/run-shim-tests` (no extension) leaves argv[1] without `.js`.
  for (const candidate of [process.argv[1], `${process.argv[1]}.js`]) {
    try {
      if (candidate && fs.realpathSync(candidate) === self) return true;
    } catch { /* not a file; try the next */ }
  }
  return false;
}

if (isMainModule()) process.exitCode = main(process.argv.slice(2));
