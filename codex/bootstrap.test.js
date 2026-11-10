// codex/bootstrap.test.js — black-box tests for the Codex MCP launch bootstrap and its generator.
//
// The bootstrap is a CommonJS script body run as `node -e <source>` (codex/mcp.json embeds it
// verbatim). Each test builds a fake Codex cache under a temp dir, runs the real source in a
// child process with a Codex-like minimal environment, and reads which fixture start.js the
// bootstrap handed over to (the fixture prints its argv and cwd).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { buildMcp, ENV_VARS } from './build-mcp.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const sourcePath = path.join(here, 'bootstrap.cjs');
const mcpPath = path.join(here, 'mcp.json');
const lf = (text) => text.replace(/\r\n/g, '\n');
const source = lf(fs.readFileSync(sourcePath, 'utf8'));

/** A fixture start.js valid as both CJS and ESM: no require, no __filename. */
const FIXTURE = 'console.log(JSON.stringify({ argv: process.argv, cwd: process.cwd() }));\n';

function tmp(prefix) {
  return fs.mkdtempSync(path.join(os.tmpdir(), `tr-codex-${prefix}-`));
}

/** Builds <home>/plugins/cache/<mk>/total-recall/<ver>/bin/start.js; returns its path. */
function addTree(home, mk, ver, { mtime } = {}) {
  const start = path.join(home, 'plugins', 'cache', mk, 'total-recall', ver, 'bin', 'start.js');
  fs.mkdirSync(path.dirname(start), { recursive: true });
  fs.writeFileSync(start, FIXTURE);
  if (mtime) fs.utimesSync(start, mtime, mtime);
  return start;
}

/** Runs the bootstrap the way Codex does: node -e <source>, sanitized env, no cwd of the plugin. */
function runBootstrap({ codexHome, userHome, cwd, extraEnv = {} }) {
  const env = { PATH: process.env.PATH ?? '', ...extraEnv };
  if (process.env.SystemRoot) env.SystemRoot = process.env.SystemRoot;
  if (codexHome) env.CODEX_HOME = codexHome;
  if (userHome) { env.HOME = userHome; env.USERPROFILE = userHome; }
  const spawnCwd = cwd ?? tmp('cwd');
  const r = spawnSync(process.execPath, ['-e', source], { env, cwd: spawnCwd, encoding: 'utf8' });
  let out;
  try { out = JSON.parse(r.stdout); } catch { out = undefined; }
  return { status: r.status, stderr: r.stderr, out, spawnCwd };
}

const real = (p) => fs.realpathSync.native(p);

// ---- tree selection ----

test('newest version wins, compared numerically (4.10.0 > 4.3.0 > 4.2.3)', () => {
  const home = tmp('home');
  addTree(home, 'mk', '4.2.3');
  addTree(home, 'mk', '4.3.0');
  const newest = addTree(home, 'mk', '4.10.0');
  const r = runBootstrap({ codexHome: home });
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.out.argv[1], newest);
});

test('a release beats its prerelease; prerelease identifiers compare per semver', () => {
  const home = tmp('home');
  addTree(home, 'mk', '4.2.3-beta.1');
  addTree(home, 'mk', '4.2.3-beta.2');
  addTree(home, 'mk', '4.2.3-beta.10');
  const beta10 = path.join(home, 'plugins', 'cache', 'mk', 'total-recall', '4.2.3-beta.10', 'bin', 'start.js');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], beta10);
  const release = addTree(home, 'mk', '4.2.3');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], release);
});

test('a non-semver directory such as "local" loses to any valid version, and a lone one is used', () => {
  const home = tmp('home');
  const local = addTree(home, 'mk', 'local');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], local);
  const published = addTree(home, 'mk', '4.2.3');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], published);
});

test('two marketplaces: higher version wins; equal versions fall to the newer start.js mtime', () => {
  const home = tmp('home');
  addTree(home, 'mkA', '4.2.3');
  const higher = addTree(home, 'mkB', '4.3.0');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], higher);

  const home2 = tmp('home');
  addTree(home2, 'mkA', '4.2.3', { mtime: new Date('2026-01-01T00:00:00Z') });
  const newer = addTree(home2, 'mkB', '4.2.3', { mtime: new Date('2026-06-01T00:00:00Z') });
  assert.equal(runBootstrap({ codexHome: home2 }).out.argv[1], newer);
});

test('enabled filter: only marketplaces enabled in config.toml are candidates', () => {
  const home = tmp('home');
  const a = addTree(home, 'mkA', '4.2.3');
  addTree(home, 'mkB', '9.9.9');
  fs.writeFileSync(path.join(home, 'config.toml'), [
    '[plugins."total-recall@mkA"]', 'enabled = true', '',
    '[plugins."other@mkB"]', 'enabled = true', '',
  ].join('\n'));
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], a);
});

test('enabled filter: CRLF and indentation are tolerated; false does not enable', () => {
  const home = tmp('home');
  const a = addTree(home, 'mkA', '4.2.3');
  addTree(home, 'mkB', '9.9.9');
  fs.writeFileSync(path.join(home, 'config.toml'), [
    '  [plugins."total-recall@mkB"]', '  enabled = false', '',
    '  [plugins."total-recall@mkA"]', '  enabled = true', '',
  ].join('\r\n'));
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], a);
});

test('enabled filter: when it would leave nothing, all candidates are used (never exit 1 for that)', () => {
  const home = tmp('home');
  addTree(home, 'mkA', '4.2.3');
  const b = addTree(home, 'mkB', '4.3.0');
  fs.writeFileSync(path.join(home, 'config.toml'), '[plugins."total-recall@mkZ"]\nenabled = true\n');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], b);
  fs.writeFileSync(path.join(home, 'config.toml'), '[plugins."total-recall@mkA"]\nenabled = false\n');
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], b);
});

test('an unreadable or absent config.toml is ignored', () => {
  const home = tmp('home');
  const a = addTree(home, 'mk', '4.2.3');
  fs.mkdirSync(path.join(home, 'config.toml')); // a directory where the file should be
  assert.equal(runBootstrap({ codexHome: home }).out.argv[1], a);
});

// ---- where it looks ----

test('CODEX_HOME is honored over HOME/USERPROFILE', () => {
  const codexHome = tmp('chome');
  const userHome = tmp('uhome');
  const mine = addTree(codexHome, 'mk', '4.2.3');
  addTree(path.join(userHome, '.codex'), 'mk', '9.9.9');
  assert.equal(runBootstrap({ codexHome, userHome }).out.argv[1], mine);
});

test('without CODEX_HOME it scans <home>/.codex', () => {
  const userHome = tmp('uhome');
  const fallback = addTree(path.join(userHome, '.codex'), 'mk', '4.2.3');
  assert.equal(runBootstrap({ userHome }).out.argv[1], fallback);
});

test('nothing found: exit 1 and a stderr message naming the cache', () => {
  const home = tmp('home');
  const r = runBootstrap({ codexHome: home });
  assert.equal(r.status, 1);
  assert.ok(r.stderr.includes('total-recall plugin not found under'));
  assert.ok(r.stderr.includes(path.join(home, 'plugins', 'cache')));
});

// ---- hand-over ----

test('start.js sees argv exactly [node, start.js] and the spawn cwd (never the plugin dir)', () => {
  const home = tmp('home');
  const start = addTree(home, 'mk', '4.2.3');
  const r = runBootstrap({ codexHome: home });
  assert.deepEqual(r.out.argv, [process.execPath, start]);
  assert.equal(real(r.out.cwd), real(r.spawnCwd));
});

test('evaluation is CommonJS even from a "type":"module" cwd', () => {
  const home = tmp('home');
  const start = addTree(home, 'mk', '4.2.3');
  const cwd = tmp('esmcwd');
  fs.writeFileSync(path.join(cwd, 'package.json'), '{"type":"module"}\n');
  const r = runBootstrap({ codexHome: home, cwd });
  assert.equal(r.status, 0, r.stderr);
  assert.equal(r.out.argv[1], start);
});

test('a start.js that fails to load: exit 1 with a message, not a silent hang', () => {
  const home = tmp('home');
  const start = addTree(home, 'mk', '4.2.3');
  fs.writeFileSync(start, 'throw new Error("boom");\n');
  const r = runBootstrap({ codexHome: home });
  assert.equal(r.status, 1);
  assert.ok(r.stderr.includes('boom'));
});

// ---- generator ----

test('build-mcp: the committed mcp.json is exactly what the generator produces from bootstrap.cjs', () => {
  assert.equal(lf(fs.readFileSync(mcpPath, 'utf8')), buildMcp(source));
});

test('build-mcp: any change to the source changes the output; args[1] is the source verbatim', () => {
  assert.notEqual(buildMcp(source + ' '), buildMcp(source));
  const cfg = JSON.parse(fs.readFileSync(mcpPath, 'utf8'));
  const server = cfg.mcpServers['total-recall'];
  assert.deepEqual(Object.keys(cfg.mcpServers), ['total-recall']);
  assert.equal(server.command, 'node');
  assert.equal(server.args[0], '-e');
  assert.equal(server.args[1], source);
  assert.equal(server.args.length, 2);
  assert.equal(server.cwd, undefined, 'no cwd: the engine must inherit the session directory');
  assert.deepEqual(server.env_vars, ENV_VARS);
});

test('build-mcp: CRLF in the source is normalized before embedding', () => {
  assert.equal(buildMcp(source.replace(/\n/g, '\r\n')), buildMcp(source));
});

test('build-mcp --check: exits 0 on the repo and 1 on a stale mcp.json', () => {
  const run = (cwdTree) => spawnSync(process.execPath, [path.join(cwdTree, 'codex', 'build-mcp.js'), '--check'],
    { encoding: 'utf8' });
  assert.equal(run(path.resolve(here, '..')).status, 0);

  const tree = tmp('tree');
  fs.mkdirSync(path.join(tree, 'codex'));
  fs.writeFileSync(path.join(tree, 'package.json'), '{"type":"module"}\n');
  for (const f of ['build-mcp.js', 'bootstrap.cjs', 'mcp.json']) {
    fs.copyFileSync(path.join(here, f), path.join(tree, 'codex', f));
  }
  assert.equal(run(tree).status, 0);
  fs.appendFileSync(path.join(tree, 'codex', 'mcp.json'), ' ');
  const stale = run(tree);
  assert.equal(stale.status, 1);
  assert.ok(stale.stderr.includes('stale'));
});
