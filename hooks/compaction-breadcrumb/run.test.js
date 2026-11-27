// hooks/compaction-breadcrumb/run.test.js — the SessionStart hook that tells Claude Code's agent, once per
// compaction, that the earlier text of the session is archived (transcript archive, decision 11, #153).
//
// Every spawn passes an explicit, scrubbed env (TOTAL_RECALL_HOME and HOME are temporary directories);
// nothing here reads or writes the real data directory or ~/.claude. The script is run for real.

import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../..');
const skip = process.platform === 'win32' ? 'the hook scripts need bash; not run on Windows' : false;

const BASH = (() => {
  for (const dir of (process.env.PATH ?? '').split(path.delimiter)) {
    const candidate = path.join(dir, 'bash');
    if (dir && fs.existsSync(candidate)) return candidate;
  }
  return 'bash';
})();

const RUN_SH = path.join(repoRoot, 'hooks/compaction-breadcrumb/run.sh');
const CASES = path.join(repoRoot, 'hooks/live-marker/data-dir-cases.json');

const PAYLOAD = JSON.stringify({
  session_id: 'aaaaaaaa-1111-4222-8333-444444444444',
  transcript_path: '/home/u/.claude/projects/-proj/aaaaaaaa-1111-4222-8333-444444444444.jsonl',
  cwd: '/work/proj',
  hook_event_name: 'SessionStart',
  source: 'compact',
});

let root;
before(() => { root = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-breadcrumb-')); });
after(() => { fs.rmSync(root, { recursive: true, force: true }); });

let counter = 0;
function tmpDir(label) {
  const dir = path.join(root, `${label}-${++counter}`);
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

function enable(dataDir) {
  fs.mkdirSync(path.join(dataDir, 'live'), { recursive: true });
  fs.writeFileSync(path.join(dataDir, 'live', 'enabled'), '');
}

/** A PATH directory holding recording shims for the commands a careless script might start. */
function recordingPath() {
  const dir = tmpDir('shim');
  const mark = path.join(dir, 'ran');
  for (const name of ['node', 'cat', 'sed', 'grep', 'awk', 'dirname', 'date', 'printf', 'echo', 'tr', 'jq']) {
    fs.writeFileSync(path.join(dir, name), `#!/bin/sh\necho ${name} >> "${mark}"\n`, { mode: 0o755 });
  }
  return { dir, mark };
}

function run({ env, input = PAYLOAD, cwd }) {
  const r = spawnSync(BASH, [RUN_SH], { env, cwd: cwd ?? tmpDir('cwd'), input, encoding: 'utf8', timeout: 20000 });
  return { code: r.status, stdout: r.stdout, stderr: r.stderr, error: r.error };
}

function expand(text, tmp) {
  return text.replaceAll('{tmp}', tmp);
}

describe('compaction-breadcrumb run.sh', { skip }, () => {
  test('Run_SentinelAbsent_PrintsNothingAndStartsNoProcess', () => {
    const dataDir = tmpDir('data');
    const shim = recordingPath();
    const before = fs.readdirSync(dataDir);
    for (const searchPath of [shim.dir, tmpDir('empty')]) {
      const r = run({ env: { PATH: searchPath, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') } });
      assert.equal(r.code, 0);
      assert.equal(r.stdout, '');
      assert.equal(r.stderr, '', 'an external command that is not found would print here');
      assert.equal(fs.existsSync(shim.mark), false, 'no recorded command may run');
    }
    assert.deepEqual(fs.readdirSync(dataDir), before, 'nothing may be created');
  });

  test('Run_SentinelIsADirectory_PrintsNothing', () => {
    const dataDir = tmpDir('data');
    fs.mkdirSync(path.join(dataDir, 'live', 'enabled'), { recursive: true });
    const r = run({ env: { PATH: tmpDir('empty'), TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') } });
    assert.equal(r.code, 0);
    assert.equal(r.stdout + r.stderr, '');
  });

  test('Run_SentinelPresent_PrintsTheNote_WithBuiltinsOnly', () => {
    const dataDir = tmpDir('data');
    enable(dataDir);
    const shim = recordingPath();
    // The PATH holds only recording shims: the note must come out although no external command exists.
    const r = run({ env: { PATH: shim.dir, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') } });
    assert.equal(r.code, 0);
    assert.equal(r.stderr, '');
    assert.equal(fs.existsSync(shim.mark), false, 'no recorded command may run');
    assert.ok(r.stdout.endsWith('}\n') && r.stdout.indexOf('\n') === r.stdout.length - 1, 'one line of JSON');

    const out = JSON.parse(r.stdout);
    assert.deepEqual(Object.keys(out), ['hookSpecificOutput']);
    assert.deepEqual(Object.keys(out.hookSpecificOutput).sort(), ['additionalContext', 'hookEventName']);
    assert.equal(out.hookSpecificOutput.hookEventName, 'SessionStart');

    const text = out.hookSpecificOutput.additionalContext;
    const lines = text.split('\n');
    assert.ok(lines.length >= 3 && lines.length <= 6, `at most 6 lines, got ${lines.length}`);
    assert.ok(lines.every((l) => l.trim().length > 0), 'no blank lines');
    assert.ok(text.includes('transcript_search'));
    assert.ok(text.includes('scope "session"'));
    assert.ok(text.includes('transcript_get'));
    assert.ok(text.includes('currentSession'));
    assert.match(text, /current session is not known/);
    assert.match(text, /without scope/);
    assert.doesNotMatch(text, /[0-9]/, 'no session id and no count of exchanges');
    assert.ok(text.length <= 700, `short: ${text.length}`);
  });

  test('Run_StdinIsIgnored', () => {
    const dataDir = tmpDir('data');
    enable(dataDir);
    const env = { PATH: tmpDir('empty'), TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') };
    const expected = run({ env }).stdout;
    assert.notEqual(expected, '');
    for (const input of ['', 'not json', '{"source":"startup"}', 'x'.repeat(4096)]) {
      const r = run({ env, input });
      assert.equal(r.code, 0);
      assert.equal(r.stdout, expected, 'the source is not read: the matcher chooses when the hook runs');
      assert.equal(r.stderr, '');
    }
  });

  test('Run_NoEnvironmentAtAll_ExitsZeroSilently', () => {
    const r = run({ env: { PATH: tmpDir('empty') } });
    assert.equal(r.code, 0);
    assert.equal(r.stdout + r.stderr, '');
  });

  test('Run_UnreadableLiveDirectory_PrintsNothing', (t) => {
    if (process.platform === 'win32' || (typeof process.getuid === 'function' && process.getuid() === 0)) {
      t.skip('a mode bit does not stop root or Windows');
      return;
    }
    const dataDir = tmpDir('data');
    enable(dataDir);
    fs.chmodSync(path.join(dataDir, 'live'), 0o000);
    try {
      const r = run({ env: { PATH: tmpDir('empty'), TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') } });
      assert.equal(r.code, 0);
      assert.equal(r.stdout + r.stderr, '');
    } finally {
      fs.chmodSync(path.join(dataDir, 'live'), 0o700);
    }
  });

  describe('Run_DataDirTable_MatchesTheEngineRule', () => {
    const cases = JSON.parse(fs.readFileSync(CASES, 'utf8'));
    for (const c of cases) {
      test(c.name, () => {
        const tmp = tmpDir('table');
        const env = { PATH: tmpDir('empty') };
        for (const [k, v] of Object.entries(c.env)) if (v !== null) env[k] = expand(v, tmp);
        const expected = c.dataDir === null ? null : expand(c.dataDir, tmp);

        // Where the engine's rule lands the sentinel exists; for an unusable row it is planted where a
        // relative reading would look (the working directory) and must be ignored.
        const cwd = path.join(tmp, 'cwd');
        fs.mkdirSync(cwd, { recursive: true });
        if (expected !== null) enable(expected);
        else {
          enable(path.join(cwd, '.total-recall'));
          enable(path.join(cwd, 'rel', 'dir'));
        }
        const r = run({ env, cwd });
        assert.equal(r.code, 0);
        assert.equal(r.stderr, '');
        assert.equal(r.stdout !== '', expected !== null,
          expected !== null ? 'the sentinel at the expected directory must be found' : 'an unusable directory must print nothing');
      });
    }

    test('a sentinel somewhere else than the expected directory is not used', () => {
      const tmp = tmpDir('table');
      enable(path.join(tmp, 'other'));
      const r = run({ env: { PATH: tmpDir('empty'), TOTAL_RECALL_HOME: path.join(tmp, 'trh'), HOME: path.join(tmp, 'other', '..') } });
      assert.equal(r.code, 0);
      assert.equal(r.stdout, '');
    });
  });
});
