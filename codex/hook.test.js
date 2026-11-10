// codex/hook.test.js — the Codex hook entry: unit tests against the injectable run(), plus
// process tests for the properties only a real process shows (exit code, stderr, stdin handling,
// and degrading when the floor module cannot be loaded).

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { run, stripFrontmatter, codexNote, storeEnvStrip, SESSION_START, USER_PROMPT_SUBMIT } from './hook.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const hookPath = path.join(here, 'hook.js');
const SKILL = '---\nname: total-recall:commands\ndescription: x\n---\n\nIMMEDIATE ACTION: call session_start — em dash \u2014 here.\n';
const ROOT = path.join(path.sep, 'plugins', 'total-recall');

const parseOut = (out) => JSON.parse(out.trim());

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
      environ: {},
      platform: 'win32',
      ...over,
    },
  };
}

const payload = (o = {}) => JSON.stringify({ session_id: 's-1', cwd: '/proj', transcript_path: '/x/rollout.jsonl', hook_event_name: 'UserPromptSubmit', ...o });

// ---- session-start ----

test('session-start: emits the SessionStart envelope with the frontmatter-free skill text and the Codex note', async () => {
  const { d } = deps();
  const env = parseOut(await run(d));
  assert.equal(env.hookSpecificOutput.hookEventName, 'SessionStart');
  const text = env.hookSpecificOutput.additionalContext;
  assert.ok(text.startsWith('IMMEDIATE ACTION: call session_start'));
  assert.ok(!text.includes('name: total-recall:commands'));
  assert.ok(text.includes('\u2014'), 'non-ASCII survives');
  assert.ok(text.includes(codexNote(ROOT)));
});

test('codexNote: names the bundled CLI, tool search and the $-invocation with the name Codex actually lists', () => {
  const note = codexNote(ROOT);
  assert.ok(note.includes(path.join(ROOT, 'bin', 'start.js')));
  assert.ok(/tool search/i.test(note));
  assert.ok(note.includes('$total-recall:total-recall:commands'));
  assert.ok(/may not be on PATH/i.test(note));
});

test('session-start: a missing or unreadable skill file says nothing', async () => {
  const { d } = deps({ readFile: () => { throw new Error('ENOENT'); } });
  assert.equal(await run(d), '');
  const { d: blank } = deps({ readFile: () => '---\nname: x\n---\n   \n' });
  assert.equal(await run(blank), '');
});

test('session-start: the real SKILL.md plus the note fits the handler limit (limit x 4 bytes)', async () => {
  const hooks = JSON.parse(fs.readFileSync(path.join(here, 'hooks.json'), 'utf8'));
  const limit = hooks.hooks.SessionStart[0].hooks[0].additionalContextLimit;
  const out = await run({
    command: SESSION_START, stdinText: '', rootDir: repoRoot, environ: {}, platform: process.platform,
    readFile: (p) => fs.readFileSync(p, 'utf8'),
    loadFloor: async () => { throw new Error('unused'); },
  });
  const text = parseOut(out).hookSpecificOutput.additionalContext;
  assert.ok(Buffer.byteLength(text, 'utf8') <= limit * 4, `${Buffer.byteLength(text, 'utf8')} bytes vs ${limit * 4}`);
});

test('stripFrontmatter removes the YAML header only', () => {
  assert.equal(stripFrontmatter('---\na: 1\n---\nbody\n'), 'body\n');
  assert.equal(stripFrontmatter('no header\n'), 'no header\n');
  assert.equal(stripFrontmatter('---\r\na: 1\r\n---\r\nbody'), 'body');
});

// ---- user-prompt-submit ----

test('user-prompt-submit: floor text passes through in the UserPromptSubmit envelope', async () => {
  const { d } = deps({ command: USER_PROMPT_SUBMIT, stdinText: payload() });
  const env = parseOut(await run(d));
  assert.deepEqual(env, { hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: 'FLOOR TEXT' } });
});

test('user-prompt-submit: the engine gets exactly {session_id, cwd}; transcript_path is never forwarded', async () => {
  const { d, calls } = deps({ command: USER_PROMPT_SUBMIT, stdinText: payload() });
  await run(d);
  assert.equal(calls.runFloor.length, 1);
  assert.equal(calls.runFloor[0].binaryPath, '/engine/total-recall');
  assert.deepEqual(calls.runFloor[0].payload, { session_id: 's-1', cwd: '/proj' });
});

test('user-prompt-submit: a missing cwd falls back to the process cwd', async () => {
  const { d, calls } = deps({ command: USER_PROMPT_SUBMIT, stdinText: JSON.stringify({ session_id: 's-1' }) });
  await run(d);
  assert.equal(calls.runFloor[0].payload.cwd, process.cwd());
});

test('user-prompt-submit: no engine means silence and the floor is never run', async () => {
  const { d, calls } = deps({
    command: USER_PROMPT_SUBMIT, stdinText: payload(),
    loadFloor: async () => ({ resolveEngine: () => null, runFloor: async (a) => { calls.runFloor.push(a); return 'x'; } }),
  });
  assert.equal(await run(d), '');
  assert.equal(calls.runFloor.length, 0);
});

test('user-prompt-submit: an empty or blank floor result says nothing', async () => {
  for (const result of [undefined, '', '   ']) {
    const { d } = deps({
      command: USER_PROMPT_SUBMIT, stdinText: payload(),
      loadFloor: async () => ({ resolveEngine: () => '/e', runFloor: async () => result }),
    });
    assert.equal(await run(d), '');
  }
});

test('user-prompt-submit: unparsable or empty stdin, or a missing/blank/non-string session_id, says nothing', async () => {
  for (const stdinText of ['', 'not json', '[]', 'null', JSON.stringify({ cwd: '/p' }), JSON.stringify({ session_id: '   ' }), JSON.stringify({ session_id: 7 })]) {
    const { d, calls } = deps({ command: USER_PROMPT_SUBMIT, stdinText });
    assert.equal(await run(d), '', JSON.stringify(stdinText));
    assert.equal(calls.runFloor.length, 0);
  }
});

test('user-prompt-submit: any failure along the way says nothing', async () => {
  const cases = [
    async () => { throw new Error('import failed'); },
    async () => ({ resolveEngine: () => { throw new Error('boom'); }, runFloor: async () => 'x' }),
    async () => ({ resolveEngine: () => '/e', runFloor: async () => { throw new Error('boom'); } }),
  ];
  for (const loadFloor of cases) {
    const { d } = deps({ command: USER_PROMPT_SUBMIT, stdinText: payload(), loadFloor });
    assert.equal(await run(d), '');
  }
});

test('an unknown command says nothing', async () => {
  const { d } = deps({ command: 'session-end' });
  assert.equal(await run(d), '');
});

// ---- store environment strip (fallback when env_vars forwarding is unavailable) ----

test('storeEnvStrip: store-determining names are blanked case-insensitively; HOME only on win32', () => {
  const environ = { Total_Recall_Home: 'a', TOTAL_RECALL_CORTEX_PAT: 'b', xdg_data_home: 'c', XDG_CONFIG_HOME: 'd', HERMES_HOME: 'e', home: 'f', PATH: 'g', CODEX_HOME: 'h' };
  const win = storeEnvStrip(environ, 'win32');
  assert.deepEqual(Object.keys(win).sort(), ['HERMES_HOME', 'Total_Recall_Home', 'TOTAL_RECALL_CORTEX_PAT', 'XDG_CONFIG_HOME', 'home', 'xdg_data_home'].sort());
  assert.ok(Object.values(win).every((v) => v === undefined));
  const posix = storeEnvStrip({ ...environ, HOME: '/h' }, 'linux');
  assert.equal('HOME' in posix, false);
  assert.equal('home' in posix, false);
  assert.equal('PATH' in win, false);
  assert.equal('CODEX_HOME' in win, false);
});

// ---- real process behavior ----

function runHook(command, { input, cwd, env } = {}) {
  return spawnSync(process.execPath, [hookPath, command], {
    input, cwd, encoding: 'utf8', env: { ...process.env, TOTAL_RECALL_HOME: fs.mkdtempSync(path.join(os.tmpdir(), 'tr-codex-trh-')), ...env },
  });
}

test('process: session-start prints valid JSON, exits 0, writes nothing to stderr', () => {
  const r = runHook('session-start', { input: '{}' });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  const env = parseOut(r.stdout);
  assert.equal(env.hookSpecificOutput.hookEventName, 'SessionStart');
  assert.ok(env.hookSpecificOutput.additionalContext.length > 1000);
});

test('process: user-prompt-submit with no provisioned engine prints nothing and exits 0', () => {
  const r = runHook('user-prompt-submit', { input: payload() });
  assert.equal(r.status, 0);
  assert.equal(r.stderr, '');
  assert.equal(r.stdout, '');
});

function copyTree(files) {
  const tree = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-codex-tree-'));
  fs.writeFileSync(path.join(tree, 'package.json'), '{"type":"module"}\n');
  for (const rel of files) {
    fs.mkdirSync(path.dirname(path.join(tree, rel)), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, rel), path.join(tree, rel));
  }
  return tree;
}

test('process: a tree without pi/ or scripts/ degrades silently (the floor module cannot load) and exits 0', () => {
  const tree = copyTree(['codex/hook.js', 'skills/commands/SKILL.md']);
  const treeHook = path.join(tree, 'codex', 'hook.js');
  const floor = spawnSync(process.execPath, [treeHook, 'user-prompt-submit'], { input: payload(), encoding: 'utf8' });
  assert.equal(floor.status, 0);
  assert.equal(floor.stderr, '');
  assert.equal(floor.stdout, '');
  const start = spawnSync(process.execPath, [treeHook, 'session-start'], { input: '{}', encoding: 'utf8' });
  assert.equal(start.status, 0);
  assert.equal(start.stderr, '');
  assert.equal(parseOut(start.stdout).hookSpecificOutput.hookEventName, 'SessionStart');
});

function runWithOpenStdin(command) {
  return new Promise((resolve) => {
    const started = Date.now();
    const child = spawn(process.execPath, [hookPath, command], {
      stdio: ['pipe', 'pipe', 'pipe'],
      env: { ...process.env, TOTAL_RECALL_HOME: fs.mkdtempSync(path.join(os.tmpdir(), 'tr-codex-trh-')) },
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
  assert.equal(parseOut(r.stdout).hookSpecificOutput.hookEventName, 'SessionStart');
});

test('process: user-prompt-submit gives up on a never-written stdin within the cap, silently, exit 0', async () => {
  const r = await runWithOpenStdin('user-prompt-submit');
  assert.equal(r.code, 0);
  assert.ok(r.ms < 4000, `took ${r.ms} ms`);
  assert.equal(r.stdout, '');
  assert.equal(r.stderr, '');
});
