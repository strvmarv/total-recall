// copilot/hook.test.js — the Copilot CLI hook entry: unit tests against the injectable run(), plus
// process tests for the properties only a real process shows (exit code, stderr, stdin handling,
// and degrading when the floor module cannot be loaded).

import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { run, stripFrontmatter, copilotNote, SESSION_START, USER_PROMPT_SUBMITTED } from './hook.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const hookPath = path.join(here, 'hook.js');
const SKILL = '---\nname: total-recall:commands\ndescription: x\n---\n\nIMMEDIATE ACTION: call session_start — em dash \u2014 here.\n';
const ROOT = path.join(path.sep, 'plugins', 'total-recall');

const parseOut = (out) => JSON.parse(out.trim());

const made = [];
after(() => { for (const d of made) fs.rmSync(d, { recursive: true, force: true }); });
function tmpDir(prefix) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  made.push(dir);
  return dir;
}

function deps(over = {}) {
  const calls = { runFloor: [] };
  return {
    calls,
    d: {
      command: SESSION_START,
      stdinText: '',
      rootDir: ROOT,
      readFile: () => SKILL,
      loadFloor: async () => ({
        resolveEngine: () => '/engine/total-recall',
        runFloor: async (args) => { calls.runFloor.push(args); return 'FLOOR TEXT'; },
      }),
      ...over,
    },
  };
}

const payload = (o = {}) => JSON.stringify({ sessionId: 's-1', timestamp: 1, cwd: '/proj', prompt: 'hi', ...o });

// ---- session-start ----

test('session-start: top-level additionalContext with the frontmatter-free skill text and the Copilot note', async () => {
  const { d } = deps();
  const env = parseOut(await run(d));
  assert.deepEqual(Object.keys(env), ['additionalContext']);
  const text = env.additionalContext;
  assert.ok(text.startsWith('IMMEDIATE ACTION: call session_start'));
  assert.ok(!text.includes('name: total-recall:commands'));
  assert.ok(text.includes('\u2014'), 'non-ASCII survives');
  assert.ok(text.includes(copilotNote(ROOT)));
});

test('copilotNote: names the bundled CLI, the PATH caveat and the approval pointer', () => {
  const note = copilotNote(ROOT);
  assert.ok(note.includes(path.join(ROOT, 'bin', 'start.js')));
  assert.ok(/may not be on PATH/i.test(note));
  assert.ok(/approval/i.test(note));
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

// ---- user-prompt-submitted ----

test('user-prompt-submitted: floor text passes through as top-level additionalContext', async () => {
  const { d } = deps({ command: USER_PROMPT_SUBMITTED, stdinText: payload() });
  assert.deepEqual(parseOut(await run(d)), { additionalContext: 'FLOOR TEXT' });
});

test('user-prompt-submitted: the engine gets exactly {session_id, cwd}; no transcript_path', async () => {
  const { d, calls } = deps({ command: USER_PROMPT_SUBMITTED, stdinText: payload({ transcriptPath: '/x/t.jsonl' }) });
  await run(d);
  assert.equal(calls.runFloor.length, 1);
  assert.equal(calls.runFloor[0].binaryPath, '/engine/total-recall');
  assert.deepEqual(calls.runFloor[0].payload, { session_id: 's-1', cwd: '/proj' });
  assert.deepEqual(calls.runFloor[0].env, {});
});

test('user-prompt-submitted: a missing cwd falls back to the process cwd', async () => {
  const { d, calls } = deps({ command: USER_PROMPT_SUBMITTED, stdinText: JSON.stringify({ sessionId: 's-1' }) });
  await run(d);
  assert.equal(calls.runFloor[0].payload.cwd, process.cwd());
});

test('user-prompt-submitted: no engine means silence and the floor is never run', async () => {
  const { d, calls } = deps({
    command: USER_PROMPT_SUBMITTED, stdinText: payload(),
    loadFloor: async () => ({ resolveEngine: () => null, runFloor: async (a) => { calls.runFloor.push(a); return 'x'; } }),
  });
  assert.equal(await run(d), '');
  assert.equal(calls.runFloor.length, 0);
});

test('user-prompt-submitted: an empty or blank floor result says nothing', async () => {
  for (const result of [undefined, '', '   ']) {
    const { d } = deps({
      command: USER_PROMPT_SUBMITTED, stdinText: payload(),
      loadFloor: async () => ({ resolveEngine: () => '/e', runFloor: async () => result }),
    });
    assert.equal(await run(d), '');
  }
});

test('user-prompt-submitted: unparsable stdin, or a missing/blank/non-string sessionId, says nothing', async () => {
  for (const stdinText of ['', 'not json', '[]', 'null', JSON.stringify({ cwd: '/p' }), JSON.stringify({ sessionId: '   ' }), JSON.stringify({ sessionId: 7 }), JSON.stringify({ session_id: 's-1' })]) {
    const { d, calls } = deps({ command: USER_PROMPT_SUBMITTED, stdinText });
    assert.equal(await run(d), '', JSON.stringify(stdinText));
    assert.equal(calls.runFloor.length, 0);
  }
});

test('user-prompt-submitted: a subagent hook (agentId present) says nothing; a blank agentId does not', async () => {
  const { d, calls } = deps({ command: USER_PROMPT_SUBMITTED, stdinText: payload({ agentId: 'a-1', agentType: 'explore' }) });
  assert.equal(await run(d), '');
  assert.equal(calls.runFloor.length, 0);
  const { d: blank } = deps({ command: USER_PROMPT_SUBMITTED, stdinText: payload({ agentId: '  ' }) });
  assert.notEqual(await run(blank), '');
});

test('user-prompt-submitted: any failure along the way says nothing', async () => {
  const cases = [
    async () => { throw new Error('import failed'); },
    async () => ({ resolveEngine: () => { throw new Error('boom'); }, runFloor: async () => 'x' }),
    async () => ({ resolveEngine: () => '/e', runFloor: async () => { throw new Error('boom'); } }),
  ];
  for (const loadFloor of cases) {
    const { d } = deps({ command: USER_PROMPT_SUBMITTED, stdinText: payload(), loadFloor });
    assert.equal(await run(d), '');
  }
});

test('an unknown command says nothing', async () => {
  const { d } = deps({ command: 'session-end' });
  assert.equal(await run(d), '');
});

// ---- real process behavior ----

function copyTree(files) {
  const tree = tmpDir('tr-copilot-tree-');
  fs.writeFileSync(path.join(tree, 'package.json'), '{"type":"module"}\n');
  for (const rel of files) {
    fs.mkdirSync(path.dirname(path.join(tree, rel)), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, rel), path.join(tree, rel));
  }
  return tree;
}

test('process: session-start prints valid JSON, exits 0, writes nothing to stderr', () => {
  const r = spawnSync(process.execPath, [hookPath, 'session-start'], {
    input: '{}', encoding: 'utf8', env: { ...process.env, TOTAL_RECALL_HOME: tmpDir('tr-copilot-trh-') },
  });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  const env = parseOut(r.stdout);
  assert.ok(env.additionalContext.length > 1000);
  assert.equal('hookSpecificOutput' in env, false);
});

test('process: user-prompt-submitted with no provisioned engine prints nothing and exits 0', () => {
  // A tree with the hook and the floor module but no binaries/, so silence proves "no engine".
  const tree = copyTree(['copilot/hook.js', 'pi/floor.js', 'scripts/fetch-binary.js']);
  const r = spawnSync(process.execPath, [path.join(tree, 'copilot', 'hook.js'), 'user-prompt-submitted'], {
    input: payload(), encoding: 'utf8', env: { ...process.env, TOTAL_RECALL_HOME: tmpDir('tr-copilot-trh-') },
  });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  assert.equal(r.stdout, '');
});

test('process: a tree without pi/ or scripts/ degrades silently (the floor module cannot load) and exits 0', () => {
  const tree = copyTree(['copilot/hook.js', 'skills/commands/SKILL.md']);
  const treeHook = path.join(tree, 'copilot', 'hook.js');
  const floor = spawnSync(process.execPath, [treeHook, 'user-prompt-submitted'], { input: payload(), encoding: 'utf8' });
  assert.equal(floor.status, 0);
  assert.equal(floor.stderr, '');
  assert.equal(floor.stdout, '');
  const start = spawnSync(process.execPath, [treeHook, 'session-start'], { input: '{}', encoding: 'utf8' });
  assert.equal(start.status, 0);
  assert.equal(start.stderr, '');
  assert.ok(parseOut(start.stdout).additionalContext.length > 100);
});

function runWithOpenStdin(command) {
  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(process.execPath, [hookPath, command], {
      stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, TOTAL_RECALL_HOME: tmpDir('tr-copilot-trh-') },
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
  assert.ok(parseOut(r.stdout).additionalContext.length > 100);
});

test('process: user-prompt-submitted gives up on a never-written stdin within the cap, silently, exit 0', async () => {
  const r = await runWithOpenStdin('user-prompt-submitted');
  assert.equal(r.code, 0);
  assert.ok(r.ms < 4000, `took ${r.ms} ms`);
  assert.equal(r.stdout, '');
  assert.equal(r.stderr, '');
});
