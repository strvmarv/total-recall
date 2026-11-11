// cursor/hook.test.js — the Cursor CLI hook entry: unit tests against the injectable run() with an
// in-memory fs, real-fs tests for the marker claim, and process tests for the properties only a real
// process shows (exit code, stderr, stdin handling, degrading when the floor module cannot be loaded).

import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  run, stripFrontmatter, cursorNote, storeEnvStrip, sanitizeId, markerDir, claimGeneration,
  SESSION_START, POST_TOOL_USE, FLOOR_TIMEOUT_MS, MARKER_MAX_AGE_MS,
} from './hook.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const hookPath = path.join(here, 'hook.js');
const SKILL = '---\nname: total-recall:commands\ndescription: x\n---\n\nIMMEDIATE ACTION: call session_start — em dash \u2014 here.\n';
const ROOT = path.join(path.sep, 'plugins', 'total-recall');
const UUID_A = '8d7e1ad9-07dd-4f85-ab97-c1f621184119';
const UUID_B = '10d2b287-a491-4a3c-b31d-2113e904fc83';
const UUID_C = '1a740942-5c06-4f5c-8d8f-0b1b0f0a9e11';
const NOW = 1_800_000_000_000;

const parseOut = (out) => JSON.parse(out.trim());

const made = [];
after(() => { for (const d of made) fs.rmSync(d, { recursive: true, force: true }); });
function tmpDir(prefix) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  made.push(dir);
  return dir;
}

/** In-memory implementation of exactly the eight fs methods the hook uses. */
function memFs({ exists = () => false, lstat } = {}) {
  const files = new Map(); // path -> mtimeMs
  const calls = { mkdir: [], open: [] };
  const api = {
    files,
    calls,
    mkdirSync: (p, o) => { calls.mkdir.push([p, o]); },
    lstatSync: (p) => (lstat ? lstat(p) : { isSymbolicLink: () => false, isDirectory: () => true, uid: 1000, mode: 0o40700 }),
    readdirSync: (dir) => [...files.keys()].filter((p) => path.dirname(p) === dir).map((p) => path.basename(p)),
    statSync: (p) => ({ mtimeMs: files.get(p) }),
    unlinkSync: (p) => { files.delete(p); },
    openSync: (p) => {
      calls.open.push(p);
      if (files.has(p)) throw Object.assign(new Error('EEXIST'), { code: 'EEXIST' });
      files.set(p, NOW);
      return 7;
    },
    closeSync: () => undefined,
    existsSync: exists,
  };
  return api;
}

function deps(over = {}) {
  const calls = { runFloor: [], resolveEngine: 0 };
  const fsApi = over.fsApi ?? memFs();
  return {
    calls,
    fsApi,
    d: {
      command: SESSION_START,
      stdinText: '',
      rootDir: ROOT,
      readFile: () => SKILL,
      loadFloor: async () => ({
        resolveEngine: () => { calls.resolveEngine += 1; return '/engine/total-recall'; },
        runFloor: async (args) => { calls.runFloor.push(args); return 'FLOOR TEXT'; },
      }),
      environ: {},
      platform: 'linux',
      fsApi,
      tmpdir: path.join(path.sep, 'tmp'),
      username: 'paul',
      getuid: () => 1000,
      now: NOW,
      ...over,
    },
  };
}

const payload = (o = {}) => JSON.stringify({
  conversation_id: UUID_A, generation_id: UUID_B, tool_name: 'Shell', tool_input: { command: 'echo hi' },
  workspace_roots: ['/proj'], hook_event_name: 'postToolUse', transcript_path: null, ...o,
});
const post = (over = {}) => deps({ command: POST_TOOL_USE, stdinText: payload(), ...over });

// ---- session-start ----

test('session-start: top-level additional_context with the frontmatter-free skill text and the Cursor note', async () => {
  const { d } = deps();
  const env = parseOut(await run(d));
  assert.deepEqual(Object.keys(env), ['additional_context']);
  const text = env.additional_context;
  assert.ok(text.startsWith('IMMEDIATE ACTION: call session_start'));
  assert.ok(!text.includes('name: total-recall:commands'));
  assert.ok(text.includes('\u2014'), 'non-ASCII survives');
  assert.ok(text.includes(cursorNote(ROOT)));
});

test('cursorNote: names the bundled CLI, the PATH caveat and the approval pointer', () => {
  const note = cursorNote(ROOT);
  assert.ok(note.includes(path.join(ROOT, 'bin', 'start.js')));
  assert.ok(/may not be on PATH/i.test(note));
  assert.ok(/approv/i.test(note));
});

test('session-start: a missing or unreadable skill file says nothing', async () => {
  const { d } = deps({ readFile: () => { throw new Error('ENOENT'); } });
  assert.equal(await run(d), '');
  const { d: blank } = deps({ readFile: () => '---\nname: x\n---\n   \n' });
  assert.equal(await run(blank), '');
});

test('session-start never touches stdinText', async () => {
  const { d } = deps();
  Object.defineProperty(d, 'stdinText', { get() { throw new Error('session-start read stdin'); } });
  assert.notEqual(await run(d), '');
});

test('stripFrontmatter removes the YAML header only', () => {
  assert.equal(stripFrontmatter('---\na: 1\n---\nbody\n'), 'body\n');
  assert.equal(stripFrontmatter('no header\n'), 'no header\n');
  assert.equal(stripFrontmatter('---\r\na: 1\r\n---\r\nbody'), 'body');
});

test('an unknown command says nothing', async () => {
  const { d } = deps({ command: 'session-end' });
  assert.equal(await run(d), '');
});

// ---- post-tool-use ----

test('post-tool-use: floor text passes through as top-level additional_context', async () => {
  const { d } = post();
  assert.deepEqual(parseOut(await run(d)), { additional_context: 'FLOOR TEXT' });
});

test('post-tool-use: the engine gets exactly {session_id: conversation_id, cwd}, the floor timeout, and never a transcript path', async () => {
  const { d, calls } = post({ fsApi: memFs({ exists: (p) => p === '/proj' }) });
  await run(d);
  assert.equal(calls.runFloor.length, 1);
  assert.equal(calls.runFloor[0].binaryPath, '/engine/total-recall');
  assert.deepEqual(calls.runFloor[0].payload, { session_id: UUID_A, cwd: '/proj' });
  assert.equal(calls.runFloor[0].timeoutMs, FLOOR_TIMEOUT_MS);
  assert.equal(FLOOR_TIMEOUT_MS, 8000);
});

test('post-tool-use: store-determining environment variables are always blanked for the floor child', async () => {
  const environ = { TOTAL_RECALL_HOME: '/x', Total_Recall_Cortex_Pat: 'p', XDG_DATA_HOME: '/d', XDG_CONFIG_HOME: '/c', HERMES_HOME: '/h', HOME: 'C:/h', PATH: '/bin' };
  const { d, calls } = post({ environ, platform: 'win32' });
  await run(d);
  const env = calls.runFloor[0].env;
  for (const key of ['TOTAL_RECALL_HOME', 'Total_Recall_Cortex_Pat', 'XDG_DATA_HOME', 'XDG_CONFIG_HOME', 'HERMES_HOME', 'HOME']) {
    assert.ok(key in env && env[key] === undefined, `${key} is blanked`);
  }
  assert.equal('PATH' in env, false);
});

test('storeEnvStrip: names blanked case-insensitively; HOME only on win32', () => {
  const environ = { Total_Recall_Home: 'a', xdg_data_home: 'c', HERMES_HOME: 'e', home: 'f', PATH: 'g', CURSOR_PROJECT_DIR: 'h' };
  const win = storeEnvStrip(environ, 'win32');
  assert.deepEqual(Object.keys(win).sort(), ['HERMES_HOME', 'Total_Recall_Home', 'home', 'xdg_data_home'].sort());
  const posix = storeEnvStrip({ ...environ, HOME: '/h' }, 'linux');
  assert.equal('HOME' in posix, false);
  assert.equal('home' in posix, false);
  assert.equal('PATH' in win, false);
  assert.equal('CURSOR_PROJECT_DIR' in win, false);
});

test('post-tool-use: a leading BOM on stdin is stripped', async () => {
  const { d } = post({ stdinText: `\uFEFF${payload()}` });
  assert.deepEqual(parseOut(await run(d)), { additional_context: 'FLOOR TEXT' });
});

test('post-tool-use: cwd is an existing workspace_roots[0], else CURSOR_PROJECT_DIR, else the process cwd', async () => {
  const a = post({ fsApi: memFs({ exists: () => true }), environ: { CURSOR_PROJECT_DIR: '/env-dir' } });
  await run(a.d);
  assert.equal(a.calls.runFloor[0].payload.cwd, '/proj');
  const b = post({ fsApi: memFs({ exists: () => false }), environ: { CURSOR_PROJECT_DIR: '/env-dir' } });
  await run(b.d);
  assert.equal(b.calls.runFloor[0].payload.cwd, '/env-dir');
  const c = post({ fsApi: memFs({ exists: () => false }), environ: {} });
  await run(c.d);
  assert.equal(c.calls.runFloor[0].payload.cwd, process.cwd());
  const e = post({ stdinText: payload({ workspace_roots: [] }), environ: { CURSOR_PROJECT_DIR: '/env-dir' } });
  await run(e.d);
  assert.equal(e.calls.runFloor[0].payload.cwd, '/env-dir');
});

test('post-tool-use: the first tool call of a generation counts; later ones in the same generation say nothing', async () => {
  const fsApi = memFs();
  const first = post({ fsApi });
  assert.notEqual(await run(first.d), '');
  const second = post({ fsApi });
  assert.equal(await run(second.d), '');
  assert.equal(second.calls.runFloor.length, 0);
  const next = post({ fsApi, stdinText: payload({ generation_id: UUID_C }) });
  assert.notEqual(await run(next.d), '');
  assert.equal(next.calls.runFloor.length, 1);
});

test('post-tool-use: overlapping tool calls in one generation run the engine exactly once', async () => {
  const fsApi = memFs();
  let runs = 0;
  const loadFloor = async () => ({ resolveEngine: () => '/e', runFloor: async () => { runs += 1; return 'T'; } });
  const results = await Promise.all([1, 2, 3, 4].map(() => run(post({ fsApi, loadFloor }).d)));
  assert.equal(runs, 1);
  assert.equal(results.filter((r) => r !== '').length, 1);
});

test('post-tool-use: missing, blank or non-string ids, or unusable stdin, say nothing', async () => {
  const bad = [
    '', 'not json', '[]', 'null',
    payload({ conversation_id: undefined }), payload({ generation_id: undefined }),
    payload({ conversation_id: '  ' }), payload({ generation_id: 7 }),
  ];
  for (const stdinText of bad) {
    const { d, calls } = post({ stdinText });
    assert.equal(await run(d), '', JSON.stringify(stdinText));
    assert.equal(calls.runFloor.length, 0);
    assert.equal(calls.resolveEngine, 0);
  }
});

test('post-tool-use: no provisioned engine says nothing and consumes no marker', async () => {
  const fsApi = memFs();
  const { d, calls } = post({
    fsApi,
    loadFloor: async () => ({ resolveEngine: () => null, runFloor: async (a) => { calls.runFloor.push(a); return 'x'; } }),
  });
  assert.equal(await run(d), '');
  assert.equal(calls.runFloor.length, 0);
  assert.equal(fsApi.files.size, 0);
  assert.equal(fsApi.calls.open.length, 0);
});

test('post-tool-use: an untrusted marker directory says nothing', async () => {
  const fsApi = memFs({ lstat: () => ({ isSymbolicLink: () => true, isDirectory: () => false, uid: 1000, mode: 0o40700 }) });
  const { d, calls } = post({ fsApi });
  assert.equal(await run(d), '');
  assert.equal(calls.runFloor.length, 0);
});

test('post-tool-use: a marker I/O failure says nothing', async () => {
  const fsApi = memFs();
  fsApi.openSync = () => { throw Object.assign(new Error('EACCES'), { code: 'EACCES' }); };
  const { d, calls } = post({ fsApi });
  assert.equal(await run(d), '');
  assert.equal(calls.runFloor.length, 0);
});

test('post-tool-use: an empty or blank floor result says nothing', async () => {
  for (const result of [undefined, '', '   ']) {
    const { d } = post({ loadFloor: async () => ({ resolveEngine: () => '/e', runFloor: async () => result }) });
    assert.equal(await run(d), '');
  }
});

test('post-tool-use: any failure along the way says nothing', async () => {
  const cases = [
    async () => { throw new Error('import failed'); },
    async () => ({ resolveEngine: () => { throw new Error('boom'); }, runFloor: async () => 'x' }),
    async () => ({ resolveEngine: () => '/e', runFloor: async () => { throw new Error('boom'); } }),
  ];
  for (const loadFloor of cases) {
    const { d } = post({ loadFloor });
    assert.equal(await run(d), '');
  }
});

// ---- marker directory and claim ----

test('sanitizeId keeps [A-Za-z0-9_-] and caps at 64 characters', () => {
  assert.equal(sanitizeId('a/b\\c:d e'), 'a_b_c_d_e');
  assert.equal(sanitizeId(UUID_A), UUID_A);
  assert.equal(sanitizeId('x'.repeat(100)).length, 64);
});

test('claimGeneration: real UUID pairs get distinct names per generation; first claim wins', () => {
  const dir = tmpDir('tr-cursor-claim-');
  const claim = (conversationId, generationId, now = Date.now()) => claimGeneration({ dir, conversationId, generationId, now, fsApi: fs });
  assert.equal(claim(UUID_A, UUID_B), true);
  assert.equal(claim(UUID_A, UUID_B), false);
  assert.equal(claim(UUID_A, UUID_C), true);
  assert.equal(claim(UUID_B, UUID_B), true);
  const names = fs.readdirSync(dir);
  assert.equal(names.length, 3);
  assert.ok(names.every((n) => n.length <= 64 + 2 + 64));
  assert.ok(names.includes(`${UUID_A}__${UUID_B}`));
});

test('claimGeneration: a successful claim prunes markers older than three days and keeps fresh ones', () => {
  const dir = tmpDir('tr-cursor-prune-');
  const old = path.join(dir, 'old__conv');
  const fresh = path.join(dir, 'fresh__conv');
  fs.writeFileSync(old, '');
  fs.writeFileSync(fresh, '');
  const t = Date.now();
  fs.utimesSync(old, new Date(t - MARKER_MAX_AGE_MS - 60_000), new Date(t - MARKER_MAX_AGE_MS - 60_000));
  fs.utimesSync(fresh, new Date(t - 60_000), new Date(t - 60_000));
  assert.equal(claimGeneration({ dir, conversationId: UUID_A, generationId: UUID_B, now: t, fsApi: fs }), true);
  assert.equal(fs.existsSync(old), false);
  assert.equal(fs.existsSync(fresh), true);
  // the losing path does not scan or prune
  const stale = path.join(dir, 'stale__conv');
  fs.writeFileSync(stale, '');
  fs.utimesSync(stale, new Date(t - MARKER_MAX_AGE_MS - 60_000), new Date(t - MARKER_MAX_AGE_MS - 60_000));
  assert.equal(claimGeneration({ dir, conversationId: UUID_A, generationId: UUID_B, now: t, fsApi: fs }), false);
  assert.equal(fs.existsSync(stale), true);
});

test('claimGeneration: a non-EEXIST error is false, not a throw', () => {
  const api = memFs();
  api.openSync = () => { throw Object.assign(new Error('ENOSPC'), { code: 'ENOSPC' }); };
  assert.equal(claimGeneration({ dir: '/d', conversationId: 'a', generationId: 'b', now: NOW, fsApi: api }), false);
});

test('markerDir: created 0700 under the temp dir, named for the sanitized user', () => {
  const api = memFs();
  const dir = markerDir({ tmpdir: '/tmp', username: 'pa ul/x', platform: 'linux', fsApi: api, getuid: () => 1000 });
  assert.equal(dir, path.join('/tmp', 'total-recall-cursor-pa_ul_x'));
  assert.deepEqual(api.calls.mkdir[0], [dir, { recursive: true, mode: 0o700 }]);
});

test('markerDir (POSIX checks on every OS via a fake lstat): symlink, wrong owner and loose mode are rejected', () => {
  const base = { tmpdir: '/tmp', username: 'paul', platform: 'linux', getuid: () => 1000 };
  const ok = { isSymbolicLink: () => false, isDirectory: () => true, uid: 1000, mode: 0o40700 };
  assert.ok(markerDir({ ...base, fsApi: memFs({ lstat: () => ok }) }));
  assert.equal(markerDir({ ...base, fsApi: memFs({ lstat: () => ({ ...ok, isSymbolicLink: () => true }) }) }), null);
  assert.equal(markerDir({ ...base, fsApi: memFs({ lstat: () => ({ ...ok, isDirectory: () => false }) }) }), null);
  assert.equal(markerDir({ ...base, fsApi: memFs({ lstat: () => ({ ...ok, uid: 0 }) }) }), null);
  assert.equal(markerDir({ ...base, fsApi: memFs({ lstat: () => ({ ...ok, mode: 0o40755 }) }) }), null);
  assert.equal(markerDir({ ...base, fsApi: memFs({ lstat: () => ({ ...ok, mode: 0o40770 }) }) }), null);
  const throwing = memFs();
  throwing.mkdirSync = () => { throw new Error('EACCES'); };
  assert.equal(markerDir({ ...base, fsApi: throwing }), null);
});

test('markerDir on win32 skips the ownership checks', () => {
  const api = memFs({ lstat: () => { throw new Error('lstat must not be called'); } });
  assert.ok(markerDir({ tmpdir: 'C:\\t', username: 'paul', platform: 'win32', fsApi: api }));
});

test('markerDir (real fs, POSIX only): creates a 0700 directory and accepts it', { skip: process.platform === 'win32' }, () => {
  const tmp = tmpDir('tr-cursor-md-');
  const dir = markerDir({ tmpdir: tmp, username: 'paul', platform: process.platform, fsApi: fs, getuid: process.getuid.bind(process) });
  assert.ok(dir);
  assert.equal(fs.statSync(dir).mode & 0o777, 0o700);
});

// ---- real process behavior ----

function copyTree(files) {
  const tree = tmpDir('tr-cursor-tree-');
  fs.writeFileSync(path.join(tree, 'package.json'), '{"type":"module"}\n');
  for (const rel of files) {
    fs.mkdirSync(path.dirname(path.join(tree, rel)), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, rel), path.join(tree, rel));
  }
  return tree;
}
const tmpEnv = (dir) => ({ TEMP: dir, TMP: dir, TMPDIR: dir });
const stdinPayload = payload();

test('process: session-start prints valid JSON, exits 0, writes nothing to stderr', () => {
  const r = spawnSync(process.execPath, [hookPath, 'session-start'], { input: '{}', encoding: 'utf8', env: { ...process.env, ...tmpEnv(tmpDir('tr-cursor-t-')) } });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  const env = parseOut(r.stdout);
  assert.ok(env.additional_context.length > 1000);
  assert.equal('hookSpecificOutput' in env, false);
});

test('process: post-tool-use with no provisioned engine prints nothing, exits 0 and leaves no marker', () => {
  // A tree with the hook and the floor module but no binaries/, so silence proves "no engine".
  const tree = copyTree(['cursor/hook.js', 'pi/floor.js', 'scripts/fetch-binary.js']);
  const t = tmpDir('tr-cursor-t-');
  const r = spawnSync(process.execPath, [path.join(tree, 'cursor', 'hook.js'), 'post-tool-use'], {
    input: stdinPayload, encoding: 'utf8', env: { ...process.env, ...tmpEnv(t) },
  });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  assert.equal(r.stdout, '');
  assert.deepEqual(fs.readdirSync(t), []);
});

test('process: a BOM-prefixed payload is accepted without a crash', () => {
  const tree = copyTree(['cursor/hook.js', 'pi/floor.js', 'scripts/fetch-binary.js']);
  const r = spawnSync(process.execPath, [path.join(tree, 'cursor', 'hook.js'), 'post-tool-use'], {
    input: `\uFEFF${stdinPayload}`, encoding: 'utf8', env: { ...process.env, ...tmpEnv(tmpDir('tr-cursor-t-')) },
  });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  assert.equal(r.stdout, '');
});

test('process: a tree without pi/ or scripts/ degrades silently (the floor module cannot load) and exits 0', () => {
  const tree = copyTree(['cursor/hook.js', 'skills/commands/SKILL.md']);
  const treeHook = path.join(tree, 'cursor', 'hook.js');
  const floor = spawnSync(process.execPath, [treeHook, 'post-tool-use'], { input: stdinPayload, encoding: 'utf8' });
  assert.equal(floor.status, 0);
  assert.equal(floor.stderr, '');
  assert.equal(floor.stdout, '');
  const start = spawnSync(process.execPath, [treeHook, 'session-start'], { input: '{}', encoding: 'utf8' });
  assert.equal(start.status, 0);
  assert.equal(start.stderr, '');
  assert.ok(parseOut(start.stdout).additional_context.length > 100);
});

function runWithOpenStdin(command) {
  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(process.execPath, [hookPath, command], {
      stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, ...tmpEnv(tmpDir('tr-cursor-t-')) },
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (c) => { stdout += c; });
    child.stderr.on('data', (c) => { stderr += c; });
    child.on('close', (code) => resolve({ code, stdout, stderr, ms: Date.now() - started }));
    // stdin stays open and is never written, like a host that forgot to close the pipe.
    setTimeout(() => { try { child.kill(); } catch { /* gone */ } }, 8000).unref();
  });
}

test('process: session-start does not wait on an open, never-written stdin', async () => {
  const r = await runWithOpenStdin('session-start');
  assert.equal(r.code, 0);
  assert.ok(r.ms < 1500, `took ${r.ms} ms`);
  assert.ok(parseOut(r.stdout).additional_context.length > 100);
});

test('process: post-tool-use gives up on a never-written stdin within the cap, silently, exit 0', async () => {
  const r = await runWithOpenStdin('post-tool-use');
  assert.equal(r.code, 0);
  assert.ok(r.ms < 4000, `took ${r.ms} ms`);
  assert.equal(r.stdout, '');
  assert.equal(r.stderr, '');
});
