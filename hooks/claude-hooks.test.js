// hooks/claude-hooks.test.js — the Claude Code hooks that run the engine (#98).
//
// The prompt hook (hooks/user-prompt-submit/run.sh) must never download or provision the engine: it
// runs it when it is already provisioned and otherwise says `{}`. (There was a session-end hook too
// until #97 removed it, because Claude Code never displays what a SessionEnd hook prints.) These tests run the real scripts over a temporary copy
// of the plugin tree (each script finds its plugin root from its own location). The copy's
// bin/start.js writes a marker file if it is ever run, because running it is what would provision.

import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { detectRid, getBinaryName } from '../scripts/fetch-binary.js';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const RID = detectRid(process.platform, process.arch);
const skip = process.platform === 'win32' ? 'the hook scripts need bash; not run on Windows'
  : RID === null ? 'no engine RID on this platform' : false;

const HOOKS = [
  {
    name: 'prompt hook',
    script: 'hooks/user-prompt-submit/run.sh',
    command: 'pinned-floor',
    payload: '{"session_id":"s1","transcript_path":"/x/t.jsonl"}',
    output: '{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"## Pinned"}}',
    validatesJson: true,
  },
];

// Stands in for the engine: records its arguments and stdin, then prints what the test asks for.
const FAKE_ENGINE = `#!/usr/bin/env node
import fs from 'node:fs';
const dir = process.env.FAKE_ENGINE_DIR;
fs.appendFileSync(dir + '/argv', JSON.stringify(process.argv.slice(2)) + '\\n');
fs.writeFileSync(dir + '/stdin', fs.readFileSync(0, 'utf8'));
const mode = process.env.FAKE_ENGINE_MODE ?? 'ok';
if (mode === 'exit1') { process.stdout.write(process.env.FAKE_ENGINE_OUT ?? ''); process.exit(1); }
if (mode === 'partial') { process.stdout.write((process.env.FAKE_ENGINE_OUT ?? '').slice(0, 30)); process.exit(0); }
process.stdout.write((process.env.FAKE_ENGINE_OUT ?? '') + '\\n');
`;

// If this runs, the hook went through the launcher, which provisions. It prints a valid envelope so
// that the hook's output differs from `{}` too.
const FAKE_START = (marker) => `import fs from 'node:fs';
fs.writeFileSync(${JSON.stringify(marker)}, process.argv.slice(2).join(' '));
process.stdout.write('{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"from start.js"}}\\n');
`;

function copyInto(treeRoot, relative) {
  const dest = path.join(treeRoot, relative);
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.copyFileSync(path.join(repoRoot, relative), dest);
}

/**
 * engine: 'none' | 'fake' (a working engine, no models/) | 'registry-truncated' | 'model-wrong-size' |
 * 'registry-absent' (a working engine and a models/ tree with no registry.json).
 */
function makeTree(hook, { engine = 'none', helper = true } = {}) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-claude-hooks-'));
  const root = path.join(base, 'plugin');
  const outDir = path.join(base, 'out');
  fs.mkdirSync(outDir, { recursive: true });
  const files = [hook.script, 'scripts/fetch-binary.js'];
  if (helper) files.push('hooks/engine-path.js', 'pi/floor.js');
  for (const rel of files) {
    if (!fs.existsSync(path.join(repoRoot, rel))) continue; // red phase: the helper does not exist yet
    copyInto(root, rel);
  }
  fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: 'tree', version: '0.0.0', type: 'module' }));
  const marker = path.join(base, 'start-js-ran');
  fs.mkdirSync(path.join(root, 'bin'), { recursive: true });
  fs.writeFileSync(path.join(root, 'bin', 'start.js'), FAKE_START(marker));
  const binDir = path.join(root, 'binaries', RID);
  if (engine !== 'none') {
    fs.mkdirSync(binDir, { recursive: true });
    const bin = path.join(binDir, getBinaryName(RID));
    fs.writeFileSync(bin, FAKE_ENGINE);
    fs.chmodSync(bin, 0o755);
  }
  if (engine === 'registry-truncated') {
    fs.mkdirSync(path.join(binDir, 'models'), { recursive: true });
    fs.writeFileSync(path.join(binDir, 'models', 'registry.json'), '{"models":{"bge":{"sizeB');
  } else if (engine === 'model-wrong-size') {
    fs.mkdirSync(path.join(binDir, 'models', 'bge'), { recursive: true });
    fs.writeFileSync(path.join(binDir, 'models', 'registry.json'), JSON.stringify({ models: { bge: { sizeBytes: 1000 } } }));
    fs.writeFileSync(path.join(binDir, 'models', 'bge', 'model.onnx'), 'short');
  } else if (engine === 'registry-absent') {
    fs.mkdirSync(path.join(binDir, 'models', 'bge'), { recursive: true });
    fs.writeFileSync(path.join(binDir, 'models', 'bge', 'model.onnx'), 'whatever');
  }
  return {
    root,
    outDir,
    marker,
    binDir,
    script: path.join(root, hook.script),
    cleanup: () => fs.rmSync(base, { recursive: true, force: true }),
  };
}

/** Name, size and mtime of every file under `dir`, sorted. */
function snapshot(dir) {
  const out = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) { out.push(`${p}/`); walk(p); continue; }
      const st = fs.statSync(p);
      out.push(`${p} ${st.size} ${st.mtimeMs}`);
    }
  };
  if (fs.existsSync(dir)) walk(dir);
  return out.sort();
}

function runScript(tree, { stdin, env = {} }) {
  return new Promise((resolve) => {
    const clean = { ...process.env };
    for (const k of ['CURSOR_PLUGIN_ROOT', 'COPILOT_CLI']) delete clean[k];
    const t0 = Date.now();
    const child = spawn('bash', [tree.script], {
      env: { ...clean, FAKE_ENGINE_DIR: tree.outDir, ...env },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    let stdout = '';
    child.stdout.on('data', (c) => { stdout += c; });
    child.on('close', (code) => resolve({ code, stdout, ms: Date.now() - t0 }));
    child.stdin.on('error', () => undefined);
    child.stdin.end(stdin);
  });
}

const read = (tree, name) => {
  try { return fs.readFileSync(path.join(tree.outDir, name), 'utf8'); } catch { return undefined; }
};

for (const hook of HOOKS) {
  describe(hook.name, { skip }, () => {
    test('with no provisioned engine: prints {} within 1 s and never runs start.js', async () => {
      const tree = makeTree(hook, { engine: 'none' });
      try {
        const r = await runScript(tree, { stdin: hook.payload });
        assert.equal(r.stdout, '{}\n');
        assert.equal(r.code, 0);
        // The bound is the ticket's own (#98: "within 1 second"); the measured time is about 60 ms.
        assert.ok(r.ms < 1000, `took ${r.ms} ms`);
        assert.equal(fs.existsSync(tree.marker), false, 'bin/start.js must not run (it provisions)');
      } finally { tree.cleanup(); }
    });

    test('with a provisioned engine: runs it with the command, host and stdin, and forwards its output', async () => {
      const tree = makeTree(hook, { engine: 'fake' });
      try {
        const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_OUT: hook.output } });
        assert.equal(r.stdout, `${hook.output}\n`);
        assert.equal(r.code, 0);
        assert.deepEqual(JSON.parse(read(tree, 'argv')), [hook.command, '--host', 'claude-code']);
        assert.equal(read(tree, 'stdin'), hook.payload);
        assert.equal(fs.existsSync(tree.marker), false);
      } finally { tree.cleanup(); }
    });

    test('an engine that exits non-zero gives {}', async () => {
      const tree = makeTree(hook, { engine: 'fake' });
      try {
        const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_MODE: 'exit1', FAKE_ENGINE_OUT: hook.output } });
        assert.equal(r.stdout, '{}\n');
        assert.equal(r.code, 0);
      } finally { tree.cleanup(); }
    });

    if (hook.validatesJson) {
      test('an engine that prints half a JSON object gives {}', async () => {
        const tree = makeTree(hook, { engine: 'fake' });
        try {
          const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_MODE: 'partial', FAKE_ENGINE_OUT: hook.output } });
          assert.equal(r.stdout, '{}\n');
          assert.equal(r.code, 0);
        } finally { tree.cleanup(); }
      });
    }

    test('an engine that is present but not executable gives {}', { skip: skip || (process.getuid?.() === 0 ? 'root can run it anyway' : false) }, async () => {
      const tree = makeTree(hook, { engine: 'fake' });
      try {
        fs.chmodSync(path.join(tree.binDir, getBinaryName(RID)), 0o644);
        const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_OUT: hook.output } });
        assert.equal(r.stdout, '{}\n');
        assert.equal(r.code, 0);
        assert.equal(fs.existsSync(tree.marker), false);
      } finally { tree.cleanup(); }
    });

    test('a missing helper file gives {} and the engine is not run', async () => {
      const tree = makeTree(hook, { engine: 'fake', helper: false });
      try {
        const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_OUT: hook.output } });
        assert.equal(r.stdout, '{}\n');
        assert.equal(r.code, 0);
        assert.equal(read(tree, 'argv'), undefined);
        assert.equal(fs.existsSync(tree.marker), false);
      } finally { tree.cleanup(); }
    });

    for (const engine of ['registry-truncated', 'model-wrong-size']) {
      test(`a half-extracted payload (${engine}) gives {}, runs nothing and writes nothing`, async () => {
        const tree = makeTree(hook, { engine });
        try {
          const before = snapshot(path.join(tree.root, 'binaries'));
          const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_OUT: hook.output } });
          assert.equal(r.stdout, '{}\n');
          assert.equal(r.code, 0);
          assert.equal(read(tree, 'argv'), undefined, 'the engine must not run');
          assert.equal(fs.existsSync(tree.marker), false);
          assert.deepEqual(snapshot(path.join(tree.root, 'binaries')), before);
        } finally { tree.cleanup(); }
      });
    }

    test('with models/registry.json absent the engine runs (the shim\'s own fast path accepts that too)', async () => {
      const tree = makeTree(hook, { engine: 'registry-absent' });
      try {
        const r = await runScript(tree, { stdin: hook.payload, env: { FAKE_ENGINE_OUT: hook.output } });
        assert.equal(r.stdout, `${hook.output}\n`);
        assert.deepEqual(JSON.parse(read(tree, 'argv')), [hook.command, '--host', 'claude-code']);
      } finally { tree.cleanup(); }
    });
  });
}

describe('hooks/engine-path.js', { skip }, () => {
  const hook = HOOKS[0];

  test('prints the engine path with forward slashes, and nothing without an engine', async () => {
    const withEngine = makeTree(hook, { engine: 'fake' });
    const without = makeTree(hook, { engine: 'none' });
    try {
      const run = (tree) => new Promise((resolve) => {
        const child = spawn(process.execPath, [path.join(tree.root, 'hooks', 'engine-path.js')], { stdio: ['ignore', 'pipe', 'pipe'] });
        let out = '';
        child.stdout.on('data', (c) => { out += c; });
        child.on('close', (code) => resolve({ code, out }));
      });
      const a = await run(withEngine);
      assert.equal(a.code, 0);
      // engine-path.js finds the plugin root from import.meta.url, which Node resolves through symlinks
      // (macOS: os.tmpdir() is /var/..., a link to /private/var/...). Compare real paths.
      const expected = path.join(fs.realpathSync(withEngine.binDir), getBinaryName(RID)).replaceAll('\\', '/');
      assert.equal(a.out, `${expected}\n`);
      const b = await run(without);
      assert.equal(b.code, 0);
      assert.equal(b.out, '');
    } finally { withEngine.cleanup(); without.cleanup(); }
  });
});
