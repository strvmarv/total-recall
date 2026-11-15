// hooks/session-start/whats-new.test.js
//
// Unit tests (injected dependencies) for whats-new.js, and process-level tests that run the real
// run.sh against a temporary copy of the plugin tree with a fake engine. The tests never touch the
// real data directory: the child environment points TOTAL_RECALL_HOME at a temporary one.

import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { PassThrough } from 'node:stream';
import { fileURLToPath } from 'node:url';

import { run, acceptSystemMessage, readStdin } from './whats-new.js';
import { detectRid, getBinaryName } from '../../scripts/fetch-binary.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

// ---------------------------------------------------------------------------------------------
// Unit tests
// ---------------------------------------------------------------------------------------------

function stdinWith(text) {
  const s = new PassThrough();
  s.end(text);
  return s;
}

function neverEndingStdin() {
  return new PassThrough(); // nothing written, never ended
}

function makeDeps(overrides = {}) {
  const calls = [];
  const deps = {
    calls,
    stdin: stdinWith('{"source":"startup"}'),
    env: { TOTAL_RECALL_HOME: '/data' },
    now: Date.now,
    resolveDataDir: (env) => { calls.push('resolveDataDir'); return env.TOTAL_RECALL_HOME ?? null; },
    writeDirectStamp: (args) => { calls.push(['stamp', args]); return true; },
    resolveEngine: () => { calls.push('resolveEngine'); return '/engine'; },
    runWhatsNew: async (args) => { calls.push(['run', args]); return '{"systemMessage":"hello"}'; },
    ...overrides,
  };
  return deps;
}

const names = (calls) => calls.map((c) => (Array.isArray(c) ? c[0] : c));

describe('run: order and sources', () => {
  test('the stamp is written before resolveEngine is called, for host claude-code', async () => {
    const deps = makeDeps();
    const out = await run(deps);
    assert.equal(out, JSON.stringify('hello'));
    assert.deepEqual(names(deps.calls), ['resolveDataDir', 'stamp', 'resolveEngine', 'run']);
    const stamp = deps.calls.find((c) => c[0] === 'stamp')[1];
    assert.deepEqual(stamp, { dataDir: '/data', host: 'claude-code' });
    const runArgs = deps.calls.find((c) => c[0] === 'run')[1];
    assert.equal(runArgs.binaryPath, '/engine');
    assert.equal(runArgs.host, 'claude-code');
    assert.equal(runArgs.format, 'claude-code');
  });

  for (const source of ['startup', 'resume', 'clear', 'compact']) {
    test(`source ${source}: the stamp is written`, async () => {
      const deps = makeDeps({ stdin: stdinWith(JSON.stringify({ source })) });
      await run(deps);
      assert.ok(names(deps.calls).includes('stamp'));
      const order = names(deps.calls);
      if (order.includes('resolveEngine')) assert.ok(order.indexOf('stamp') < order.indexOf('resolveEngine'));
    });
  }

  test('compact never looks for the engine and never runs it', async () => {
    const deps = makeDeps({ stdin: stdinWith('{"source":"compact"}') });
    const out = await run(deps);
    assert.equal(out, '');
    assert.deepEqual(names(deps.calls), ['resolveDataDir', 'stamp']);
  });

  for (const [label, payload] of [
    ['empty', ''],
    ['not json', 'not json'],
    ['an array', '[]'],
    ['a numeric source', '{"source":5}'],
    ['null', 'null'],
  ]) {
    test(`a payload that is ${label} counts as startup`, async () => {
      const deps = makeDeps({ stdin: stdinWith(payload) });
      const out = await run(deps);
      assert.equal(out, JSON.stringify('hello'));
      assert.ok(names(deps.calls).includes('run'));
    });
  }

  test('a stdin that never ends counts as startup after stdinCapMs', async () => {
    const stdin = neverEndingStdin();
    const deps = makeDeps({ stdin, stdinCapMs: 30 });
    const t0 = Date.now();
    const out = await run(deps);
    assert.equal(out, JSON.stringify('hello'));
    assert.ok(Date.now() - t0 < 1000);
    assert.equal(stdin.destroyed, true, 'the stream is released when the cap expires');
  });

  test('a TTY stdin is not read', async () => {
    const stdin = neverEndingStdin();
    stdin.isTTY = true;
    const deps = makeDeps({ stdin, stdinCapMs: 5000 });
    const t0 = Date.now();
    await run(deps);
    assert.ok(Date.now() - t0 < 1000);
  });

  test('a missing stdin counts as startup', async () => {
    const deps = makeDeps({ stdin: undefined });
    assert.equal(await run(deps), JSON.stringify('hello'));
  });
});

describe('run: data directory and engine', () => {
  test('no data dir: no stamp is written but the engine still runs', async () => {
    const stamps = [];
    const deps = makeDeps({
      resolveDataDir: () => null,
      writeDirectStamp: (args) => { stamps.push(args); return false; },
    });
    const out = await run(deps);
    assert.equal(out, JSON.stringify('hello'));
    for (const s of stamps) assert.equal(s.dataDir, null);
  });

  test('no engine: returns "" after the stamp', async () => {
    const deps = makeDeps({ resolveEngine: () => { deps.calls.push('resolveEngine'); return null; } });
    assert.equal(await run(deps), '');
    assert.deepEqual(names(deps.calls), ['resolveDataDir', 'stamp', 'resolveEngine']);
  });

  for (const [label, value] of [['undefined', undefined], ['an empty string', '']]) {
    test(`runWhatsNew returning ${label} gives ""`, async () => {
      const deps = makeDeps({ runWhatsNew: async () => value });
      assert.equal(await run(deps), '');
    });
  }

  for (const [label, line] of [
    ['a double quote', 'say "hi"'],
    ['a backslash', 'C:\\path\\x'],
    ['percent signs', '100% %s %d %%'],
    ['a long line', 'x'.repeat(1000)],
    ['non-ASCII', 'total-recall 4.8.0 — café ✓'],
  ]) {
    test(`a line with ${label} round-trips through the returned literal`, async () => {
      const deps = makeDeps({ runWhatsNew: async () => JSON.stringify({ systemMessage: line }) });
      const out = await run(deps);
      assert.ok(out.startsWith('"') && out.endsWith('"'));
      assert.ok(!/[\r\n]/.test(out));
      assert.equal(JSON.parse(out), line);
    });
  }

  test('timeoutMs is what is left of the deadline, at least 1', async () => {
    let t = 1000;
    const clock = () => t;
    let seen;
    const deps = makeDeps({
      now: clock,
      deadlineMs: 2500,
      stdin: stdinWith('{"source":"startup"}'),
      writeDirectStamp: () => { t = 1700; return true; }, // 700 ms pass before the engine runs
      runWhatsNew: async (args) => { seen = args.timeoutMs; return undefined; },
    });
    await run(deps);
    assert.equal(seen, 1800);
  });

  test('timeoutMs is 1 when the deadline has already passed', async () => {
    let t = 0;
    let seen;
    const deps = makeDeps({
      now: () => t,
      deadlineMs: 100,
      writeDirectStamp: () => { t = 5000; return true; },
      runWhatsNew: async (args) => { seen = args.timeoutMs; return undefined; },
    });
    await run(deps);
    assert.equal(seen, 1);
  });

  test('the whole run stays inside the deadline with a never-ending stdin and an engine that hangs', async () => {
    const deps = makeDeps({
      stdin: neverEndingStdin(),
      stdinCapMs: 100,
      deadlineMs: 400,
      runWhatsNew: ({ timeoutMs }) => new Promise((resolve) => setTimeout(() => resolve(undefined), timeoutMs)),
    });
    const t0 = Date.now();
    const out = await run(deps);
    const took = Date.now() - t0;
    assert.equal(out, '');
    assert.ok(took < 400 + 150, `took ${took} ms`);
  });

  test('a runner that never settles is cut off shortly after the deadline', async () => {
    const deps = makeDeps({
      stdinCapMs: 10,
      deadlineMs: 200,
      runWhatsNew: () => new Promise(() => undefined),
    });
    const t0 = Date.now();
    assert.equal(await run(deps), '');
    assert.ok(Date.now() - t0 < 1000);
  });

  test('every dependency throwing returns ""', async () => {
    const boom = () => { throw new Error('boom'); };
    assert.equal(await run(makeDeps({ resolveDataDir: boom })), '');
    assert.equal(await run(makeDeps({ writeDirectStamp: boom })), '');
    assert.equal(await run(makeDeps({ resolveEngine: boom })), '');
    assert.equal(await run(makeDeps({ runWhatsNew: boom })), '');
    assert.equal(await run(makeDeps({ runWhatsNew: async () => boom() })), '');
    assert.equal(await run(makeDeps({ now: boom })), '');
    assert.equal(await run({}), '');
  });
});

describe('acceptSystemMessage', () => {
  const bad = [
    ['undefined', undefined],
    ['empty output', ''],
    ['help text', 'Usage: total-recall whats-new [options]'],
    ['invalid json', '{"systemMessage":'],
    ['an empty object', '{}'],
    ['an array', '[{"systemMessage":"x"}]'],
    ['a string', '"x"'],
    ['null', 'null'],
    ['a number message', '{"systemMessage":5}'],
    ['a null message', '{"systemMessage":null}'],
    ['an empty message', '{"systemMessage":""}'],
    ['a message with LF', '{"systemMessage":"a\\nb"}'],
    ['a message with CR', '{"systemMessage":"a\\rb"}'],
    ['a message with U+2028', '{"systemMessage":"a\\u2028b"}'],
    ['a message with U+2029', '{"systemMessage":"a\\u2029b"}'],
    ['a message with BEL', '{"systemMessage":"a\\u0007b"}'],
    ['a message with ESC', '{"systemMessage":"a\\u001b[31mb"}'],
    ['a message with DEL', '{"systemMessage":"a\\u007fb"}'],
    ['a message with a C1 control', '{"systemMessage":"a\\u0085b"}'],
    ['a message of 1001 characters', JSON.stringify({ systemMessage: 'x'.repeat(1001) })],
    ['stray log line before the object', 'warn: something\n{"systemMessage":"x"}'],
  ];
  for (const [label, text] of bad) {
    test(`rejects ${label}`, () => assert.equal(acceptSystemMessage(text), undefined));
  }
  test('accepts a message of exactly 1000 characters', () => {
    const line = 'x'.repeat(1000);
    assert.equal(acceptSystemMessage(JSON.stringify({ systemMessage: line })), line);
  });
  test('accepts an ordinary notice and ignores other members', () => {
    assert.equal(acceptSystemMessage('{"systemMessage":"total-recall 4.8.0: x.","other":1}'), 'total-recall 4.8.0: x.');
  });
});

describe('readStdin', () => {
  test('returns all of a stream that ends', async () => {
    assert.equal(await readStdin(stdinWith('{"source":"resume"}'), 1000), '{"source":"resume"}');
  });
  test('returns "" for a stream that errors', async () => {
    const s = new PassThrough();
    const p = readStdin(s, 1000);
    s.destroy(new Error('x'));
    assert.equal(await p, '');
  });
});

// ---------------------------------------------------------------------------------------------
// Process-level tests: the real run.sh over a temporary copy of the plugin tree
// ---------------------------------------------------------------------------------------------

const RID = detectRid(process.platform, process.arch);
const skipProcess = process.platform === 'win32' ? 'run.sh needs bash; not run on Windows'
  : RID === null ? 'no engine RID on this platform' : false;

const SKILL_TEXT = '# Stub skill\nsession_start say "hi" 100%\n';
const SKILL_JSON = '# Stub skill\\nsession_start say \\"hi\\" 100%';

// Golden outputs: the three printf templates of run.sh as they were before this change, copied
// verbatim and filled with the stub SKILL.md.
const GOLDEN_CLAUDE_CODE =
  `{\n  "hookSpecificOutput": {\n    "hookEventName": "SessionStart",\n    "additionalContext": "${SKILL_JSON}"\n  }\n}\n`;
const GOLDEN_CURSOR = `{\n  "additional_context": "${SKILL_JSON}"\n}\n`;
const GOLDEN_GENERIC = `{\n  "additionalContext": "${SKILL_JSON}"\n}\n`;

const FAKE_ENGINE = `#!/usr/bin/env node
import fs from 'node:fs';
const argvFile = process.env.FAKE_ENGINE_ARGV_FILE;
if (argvFile) fs.appendFileSync(argvFile, JSON.stringify(process.argv.slice(2)) + '\\n');
const mode = process.env.FAKE_ENGINE_MODE ?? 'notice';
if (mode === 'hang') {
  setInterval(() => undefined, 1000);
} else if (mode === 'raw') {
  process.stdout.write(process.env.FAKE_ENGINE_RAW ?? '');
} else if (mode === 'exit1') {
  process.stdout.write(JSON.stringify({ systemMessage: 'should not be shown' }));
  process.exit(1);
} else {
  process.stdout.write(JSON.stringify({ systemMessage: process.env.FAKE_ENGINE_TEXT ?? 'total-recall 9.9.9: a fake notice.' }) + '\\n');
}
`;

function copyInto(treeRoot, relative) {
  const dest = path.join(treeRoot, relative);
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.copyFileSync(path.join(repoRoot, relative), dest);
}

function makeTree({ withEngine = true } = {}) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-whatsnew-hook-'));
  const root = path.join(base, 'plugin');
  const dataDir = path.join(base, 'data');
  fs.mkdirSync(dataDir, { recursive: true });
  for (const rel of [
    'hooks/session-start/run.sh',
    'hooks/session-start/whats-new.js',
    'pi/floor.js',
    'pi/whatsNew.js',
    'scripts/fetch-binary.js',
  ]) copyInto(root, rel);
  fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: 'tree', version: '0.0.0', type: 'module' }));
  fs.mkdirSync(path.join(root, 'skills', 'commands'), { recursive: true });
  fs.writeFileSync(path.join(root, 'skills', 'commands', 'SKILL.md'), SKILL_TEXT);
  if (withEngine) {
    const binDir = path.join(root, 'binaries', RID);
    fs.mkdirSync(binDir, { recursive: true });
    const bin = path.join(binDir, getBinaryName(RID));
    fs.writeFileSync(bin, FAKE_ENGINE);
    fs.chmodSync(bin, 0o755);
  }
  return {
    root,
    dataDir,
    stamp: path.join(dataDir, 'whats-new', 'direct-claude-code.json'),
    script: path.join(root, 'hooks', 'session-start', 'run.sh'),
    cleanup: () => fs.rmSync(base, { recursive: true, force: true }),
  };
}

function baseEnv(tree, extra = {}) {
  const env = { ...process.env };
  for (const k of ['CURSOR_PLUGIN_ROOT', 'COPILOT_CLI', 'CLAUDE_PLUGIN_ROOT']) delete env[k];
  for (const k of Object.keys(env)) if (k.startsWith('FAKE_ENGINE_')) delete env[k];
  env.TOTAL_RECALL_HOME = tree.dataDir;
  return { ...env, ...extra };
}

/** Runs `bash run.sh`; `stdin` is a string (written and ended) or null (a pipe held open, never written). */
function runHook(tree, { env, stdin = '{"source":"startup"}' }) {
  return new Promise((resolve) => {
    const t0 = Date.now();
    const child = spawn('bash', [tree.script], { env, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', (c) => { stdout += c; });
    child.stderr.setEncoding('utf8').on('data', (c) => { stderr += c; });
    child.stdin.on('error', () => undefined);
    if (stdin !== null) child.stdin.end(stdin);
    const watchdog = setTimeout(() => child.kill('SIGKILL'), 15_000);
    child.on('close', (code) => {
      clearTimeout(watchdog);
      try { child.stdin.destroy(); } catch { /* closed */ }
      resolve({ code, stdout, stderr, ms: Date.now() - t0 });
    });
  });
}

const claudeEnv = (tree, extra = {}) => baseEnv(tree, { CLAUDE_PLUGIN_ROOT: tree.root, ...extra });

async function withTree(opts, fn) {
  const tree = makeTree(opts);
  try {
    await fn(tree);
  } finally {
    tree.cleanup();
  }
}

describe('run.sh with the notice step (process level)', { skip: skipProcess }, () => {
  test('a notice is added as systemMessage beside the unchanged additionalContext', async () => {
    await withTree({}, async (tree) => {
      const argvFile = path.join(tree.dataDir, 'argv.txt');
      const text = 'total-recall 9.9.9: a fake notice. More: https://example.invalid/r';
      const out = await runHook(tree, {
        env: claudeEnv(tree, { FAKE_ENGINE_TEXT: text, FAKE_ENGINE_ARGV_FILE: argvFile }),
      });
      assert.equal(out.code, 0);
      const parsed = JSON.parse(out.stdout);
      assert.equal(parsed.systemMessage, text);
      assert.deepEqual(parsed.hookSpecificOutput, JSON.parse(GOLDEN_CLAUDE_CODE).hookSpecificOutput);
      assert.deepEqual(Object.keys(parsed).sort(), ['hookSpecificOutput', 'systemMessage']);

      const stamp = JSON.parse(fs.readFileSync(tree.stamp, 'utf8'));
      assert.match(stamp.at, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/);

      const argv = fs.readFileSync(argvFile, 'utf8').trim().split('\n').map((l) => JSON.parse(l));
      assert.deepEqual(argv, [['whats-new', '--host', 'claude-code', '--format', 'claude-code']]);
    });
  });

  for (const source of ['resume', 'clear']) {
    test(`source ${source} also shows the notice`, async () => {
      await withTree({}, async (tree) => {
        const out = await runHook(tree, { env: claudeEnv(tree), stdin: JSON.stringify({ source }) });
        assert.equal(out.code, 0);
        assert.equal(JSON.parse(out.stdout).systemMessage, 'total-recall 9.9.9: a fake notice.');
      });
    });
  }

  test('compact prints exactly the baseline, never runs the engine, and still refreshes the stamp', async () => {
    await withTree({}, async (tree) => {
      const argvFile = path.join(tree.dataDir, 'argv.txt');
      const out = await runHook(tree, {
        env: claudeEnv(tree, { FAKE_ENGINE_ARGV_FILE: argvFile }),
        stdin: '{"source":"compact"}',
      });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
      assert.equal(fs.existsSync(argvFile), false, 'the engine was not run');
      assert.ok(fs.existsSync(tree.stamp));
    });
  });

  test('no engine binary: the baseline, and the stamp is still written', async () => {
    await withTree({ withEngine: false }, async (tree) => {
      const out = await runHook(tree, { env: claudeEnv(tree) });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
      assert.ok(fs.existsSync(tree.stamp));
    });
  });

  test('a hung engine costs under 3.5 s and leaves the baseline', async () => {
    await withTree({}, async (tree) => {
      const out = await runHook(tree, { env: claudeEnv(tree, { FAKE_ENGINE_MODE: 'hang' }) });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
      assert.ok(out.ms < 3500, `took ${out.ms} ms`);
    });
  });

  test('a held-open stdin costs under 3.5 s and still gives the notice', async () => {
    await withTree({}, async (tree) => {
      const out = await runHook(tree, { env: claudeEnv(tree), stdin: null });
      assert.equal(out.code, 0);
      assert.ok(out.ms < 3500, `took ${out.ms} ms`);
      assert.equal(JSON.parse(out.stdout).systemMessage, 'total-recall 9.9.9: a fake notice.');
    });
  });

  test('a held-open stdin and a hung engine still finish under 3.5 s with the baseline', async () => {
    await withTree({}, async (tree) => {
      const out = await runHook(tree, { env: claudeEnv(tree, { FAKE_ENGINE_MODE: 'hang' }), stdin: null });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
      assert.ok(out.ms < 3500, `took ${out.ms} ms`);
    });
  });

  const rawCases = [
    ['garbage', 'zzz\u0001garbage'],
    ['help text', 'Usage: total-recall whats-new --host <id>\n  --format <f>'],
    ['an empty object', '{}'],
    ['invalid json', '{"systemMessage":"x"'],
    ['a numeric systemMessage', '{"systemMessage":5}'],
    ['an empty systemMessage', '{"systemMessage":""}'],
    ['a multi-line systemMessage', '{"systemMessage":"a\\nb"}'],
    ['a U+2028 systemMessage', '{"systemMessage":"a\\u2028b"}'],
    ['an over-long systemMessage', JSON.stringify({ systemMessage: 'x'.repeat(1001) })],
    ['a control character in the message', '{"systemMessage":"a\\u0007b"}'],
    ['a raw control character in the output', '{"systemMessage":"a\u0007b"}'],
    ['a log line before the object', 'warn: x\n{"systemMessage":"x"}'],
    ['nothing', ''],
  ];
  for (const [label, raw] of rawCases) {
    test(`engine output: ${label} leaves the baseline`, async () => {
      await withTree({}, async (tree) => {
        const out = await runHook(tree, { env: claudeEnv(tree, { FAKE_ENGINE_MODE: 'raw', FAKE_ENGINE_RAW: raw }) });
        assert.equal(out.code, 0);
        assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
      });
    });
  }

  test('an engine that exits non-zero leaves the baseline', async () => {
    await withTree({}, async (tree) => {
      const out = await runHook(tree, { env: claudeEnv(tree, { FAKE_ENGINE_MODE: 'exit1' }) });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
    });
  });

  test('a hostile notice still yields valid JSON and round-trips', async () => {
    await withTree({}, async (tree) => {
      const text = 'a "quoted" \\ back\\slash %s %d 100% \\n literal \\u2028 end $(touch x) `x` \'q\'';
      const out = await runHook(tree, { env: claudeEnv(tree, { FAKE_ENGINE_TEXT: text }) });
      assert.equal(out.code, 0);
      const parsed = JSON.parse(out.stdout);
      assert.equal(parsed.systemMessage, text);
      assert.deepEqual(parsed.hookSpecificOutput, JSON.parse(GOLDEN_CLAUDE_CODE).hookSpecificOutput);
    });
  });

  test('node not on PATH: the baseline, exit 0', async () => {
    await withTree({}, async (tree) => {
      const bin = path.join(path.dirname(tree.root), 'bin-without-node');
      fs.mkdirSync(bin);
      for (const tool of ['bash', 'dirname', 'cat']) {
        const found = [...(process.env.PATH ?? '').split(path.delimiter), '/usr/bin', '/bin']
          .map((d) => path.join(d, tool))
          .find((p) => fs.existsSync(p));
        assert.ok(found, `${tool} not found`);
        fs.symlinkSync(found, path.join(bin, tool));
      }
      const out = await runHook(tree, { env: claudeEnv(tree, { PATH: bin }) });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CLAUDE_CODE);
      assert.equal(fs.existsSync(tree.stamp), false, 'node really was not run');
    });
  });

  test('the Cursor branch is byte-identical to the baseline and writes no stamp', async () => {
    await withTree({}, async (tree) => {
      const argvFile = path.join(tree.dataDir, 'argv.txt');
      const out = await runHook(tree, {
        env: baseEnv(tree, { CURSOR_PLUGIN_ROOT: tree.root, CLAUDE_PLUGIN_ROOT: tree.root, FAKE_ENGINE_ARGV_FILE: argvFile }),
      });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_CURSOR);
      assert.equal(fs.existsSync(tree.stamp), false);
      assert.equal(fs.existsSync(argvFile), false);
    });
  });

  test('the Copilot CLI branch is byte-identical to the baseline and writes no stamp', async () => {
    await withTree({}, async (tree) => {
      const argvFile = path.join(tree.dataDir, 'argv.txt');
      const out = await runHook(tree, {
        env: baseEnv(tree, { COPILOT_CLI: '1', CLAUDE_PLUGIN_ROOT: tree.root, FAKE_ENGINE_ARGV_FILE: argvFile }),
      });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_GENERIC);
      assert.equal(fs.existsSync(tree.stamp), false);
      assert.equal(fs.existsSync(argvFile), false);
    });
  });

  test('the generic branch (no host variables) is byte-identical to the baseline and writes no stamp', async () => {
    await withTree({}, async (tree) => {
      const out = await runHook(tree, { env: baseEnv(tree) });
      assert.equal(out.code, 0);
      assert.equal(out.stdout, GOLDEN_GENERIC);
      assert.equal(fs.existsSync(tree.stamp), false);
    });
  });
});
