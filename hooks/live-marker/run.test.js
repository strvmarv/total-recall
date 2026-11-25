// hooks/live-marker/run.test.js — the shell wrapper of the live-session marker writer.
//
// Every spawn passes an explicit, scrubbed env (TOTAL_RECALL_HOME and HOME are temporary
// directories); nothing here reads or writes the real data directory or ~/.claude. The wrapper
// and the writer are run for real, over a temporary copy of the plugin files where the tree
// matters. "Hosts" are small long-lived node processes that start `bash run.sh` as a direct child,
// so `$PPID` inside the script is the host, as it is under Claude Code.

import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { resolveDataDir } from '../../scripts/fetch-binary.js';
import { resolveDataDir as writerResolveDataDir } from './write-marker.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '../..');
const skip = process.platform === 'win32' ? 'the hook scripts need bash; not run on Windows' : false;

const NODE_DIR = path.dirname(process.execPath);
const BASH = (() => {
  for (const dir of (process.env.PATH ?? '').split(path.delimiter)) {
    const candidate = path.join(dir, 'bash');
    if (dir && fs.existsSync(candidate)) return candidate;
  }
  return 'bash';
})();

const SESSION = 'aaaaaaaa-1111-4222-8333-444444444444';
function payload(over = {}) {
  return JSON.stringify({
    session_id: SESSION,
    transcript_path: '/home/u/.claude/projects/-proj/' + SESSION + '.jsonl',
    cwd: '/work/proj',
    hook_event_name: 'UserPromptSubmit',
    ...over,
  });
}

let root;
before(() => { root = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-live-run-')); });
after(() => { fs.rmSync(root, { recursive: true, force: true }); });

let counter = 0;
function tmpDir(label) {
  const dir = path.join(root, `${label}-${++counter}`);
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

/** A plugin tree copy holding only what the wrapper needs: run.sh and write-marker.js. */
function pluginTree() {
  const tree = tmpDir('plugin');
  for (const rel of ['hooks/live-marker/run.sh', 'hooks/live-marker/write-marker.js']) {
    const dest = path.join(tree, rel);
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    fs.copyFileSync(path.join(repoRoot, rel), dest);
  }
  return tree;
}

/** A directory holding only a `node` that records that it ran (a /bin/sh script: builtins only). */
function recordingNode() {
  const dir = tmpDir('shim');
  const mark = path.join(dir, 'ran');
  const shim = path.join(dir, 'node');
  fs.writeFileSync(shim, `#!/bin/sh\necho "$@" > "${mark}"\n`, { mode: 0o755 });
  return { dir, mark };
}

function enable(dataDir) {
  fs.mkdirSync(path.join(dataDir, 'live'), { recursive: true });
  fs.writeFileSync(path.join(dataDir, 'live', 'enabled'), '');
}

/**
 * A script file that sets OSTYPE and then sources run.sh (OSTYPE is a plain shell variable, so the wrapper can fake
 * the platform). It is a file run like every other script here, not a `bash -c` command line built from a path.
 */
function sourcingWrapper(ostype) {
  const file = path.join(tmpDir('wrapper'), 'source-run.sh');
  const quote = (text) => `'${text.replace(/'/g, `'\\''`)}'`;
  fs.writeFileSync(file, `OSTYPE=${quote(ostype)}\n. ${quote(RUN_SH)}\n`);
  return file;
}

function runScript(script, { env, cwd, input = '' }) {
  const r = spawnSync(BASH, [script], { env, cwd, input, encoding: 'utf8', timeout: 20000 });
  return { code: r.status, stdout: r.stdout, stderr: r.stderr };
}

function expand(value, tmp) {
  return typeof value === 'string' ? value.split('{tmp}').join(tmp) : value;
}

/** A long-lived fake host: runs `bash <script>` as a direct child for every line it is sent. */
function startHost({ script, env, cwd }) {
  const hostFile = path.join(tmpDir('host'), 'host.mjs');
  fs.writeFileSync(hostFile, `
import { spawn } from 'node:child_process';
import readline from 'node:readline';
const [bash, script] = process.argv.slice(2);
const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  const child = spawn(bash, [script], { env: process.env, stdio: ['pipe', 'pipe', 'pipe'] });
  let out = '', err = '';
  child.stdout.on('data', (c) => { out += c; });
  child.stderr.on('data', (c) => { err += c; });
  child.on('close', (code) => process.stdout.write(JSON.stringify({ code, out, err }) + '\\n'));
  child.stdin.end(JSON.parse(line));
});
`);
  const proc = spawn(process.execPath, [hostFile, BASH, script], { env, cwd, stdio: ['pipe', 'pipe', 'inherit'] });
  const waiting = [];
  let buffer = '';
  proc.stdout.on('data', (c) => {
    buffer += c;
    let i;
    while ((i = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, i);
      buffer = buffer.slice(i + 1);
      waiting.shift()?.(JSON.parse(line));
    }
  });
  return {
    pid: proc.pid,
    send(text) {
      return new Promise((resolve) => {
        waiting.push(resolve);
        proc.stdin.write(JSON.stringify(text) + '\n');
      });
    },
    async kill() {
      proc.kill('SIGKILL');
      if (proc.exitCode === null && proc.signalCode === null) await once(proc, 'exit');
    },
  };
}

const RUN_SH = path.join(repoRoot, 'hooks/live-marker/run.sh');

describe('live-marker run.sh', { skip }, () => {
  test('Run_SentinelAbsent_StartsNoProcess', () => {
    const dataDir = tmpDir('data');
    fs.mkdirSync(dataDir, { recursive: true });
    const shim = recordingNode();
    const before = fs.readdirSync(dataDir);
    const r = runScript(RUN_SH, {
      env: { PATH: shim.dir, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
      cwd: tmpDir('cwd'),
      input: payload(),
    });
    assert.equal(r.code, 0);
    assert.equal(r.stdout, '');
    assert.equal(r.stderr, '');
    assert.equal(fs.existsSync(shim.mark), false, 'node must not be started');
    assert.deepEqual(fs.readdirSync(dataDir), before, 'nothing may be created');
  });

  test('Run_SentinelIsADirectory_Leaves', () => {
    const dataDir = tmpDir('data');
    fs.mkdirSync(path.join(dataDir, 'live', 'enabled'), { recursive: true });
    const shim = recordingNode();
    const r = runScript(RUN_SH, {
      env: { PATH: shim.dir, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
      cwd: tmpDir('cwd'),
      input: payload(),
    });
    assert.equal(r.code, 0);
    assert.equal(r.stdout + r.stderr, '');
    assert.equal(fs.existsSync(shim.mark), false);
  });

  test('Run_SentinelPresent_RunsWriterWithPpid', async () => {
    const tree = pluginTree();
    const dataDir = tmpDir('data');
    enable(dataDir);
    const host = startHost({
      script: path.join(tree, 'hooks/live-marker/run.sh'),
      env: { PATH: NODE_DIR, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
      cwd: tmpDir('cwd'),
    });
    try {
      const r = await host.send(payload());
      assert.deepEqual({ code: r.code, out: r.out, err: r.err }, { code: 0, out: '', err: '' });
      const file = path.join(dataDir, 'live', `claude-code-${host.pid}.json`);
      const marker = JSON.parse(fs.readFileSync(file, 'utf8'));
      assert.equal(marker.host_pid, host.pid);
      assert.equal(marker.session_id, SESSION);
      assert.deepEqual(fs.readdirSync(path.join(dataDir, 'live')).sort(), ['claude-code-' + host.pid + '.json', 'enabled']);
    } finally {
      await host.kill();
    }
  });

  describe('Run_DataDirTable_MatchesBothRules', () => {
    const cases = JSON.parse(fs.readFileSync(path.join(here, 'data-dir-cases.json'), 'utf8'));
    for (const c of cases) {
      test(c.name, () => {
        const tmp = tmpDir('table');
        const env = {};
        for (const [k, v] of Object.entries(c.env)) if (v !== null) env[k] = expand(v, tmp);
        const expected = c.dataDir === null ? null : expand(c.dataDir, tmp);

        // The JS rule of the provisioner agrees with the table (a relative result counts as null).
        assert.equal(resolveDataDir(env), expected, 'scripts/fetch-binary.js resolveDataDir');
        // And so does the writer's own copy of the rule.
        assert.equal(writerResolveDataDir(env), expected, 'hooks/live-marker/write-marker.js resolveDataDir');

        // Where the engine's rule would land, the sentinel exists; for an unusable row the sentinel
        // is planted where a relative reading would look (the working directory), and must be ignored.
        const cwd = path.join(tmp, 'cwd');
        fs.mkdirSync(cwd, { recursive: true });
        const shim = recordingNode();
        if (expected !== null) enable(expected);
        else {
          enable(path.join(cwd, '.total-recall'));
          enable(path.join(cwd, 'rel', 'dir'));
        }
        const r = runScript(RUN_SH, { env: { ...env, PATH: shim.dir }, cwd, input: payload() });
        assert.equal(r.code, 0);
        assert.equal(r.stdout + r.stderr, '');
        assert.equal(fs.existsSync(shim.mark), expected !== null,
          expected !== null ? 'the sentinel at the expected directory must be found' : 'an unusable directory must leave');
      });
    }

    test('a sentinel somewhere else than the expected directory is not used', () => {
      const tmp = tmpDir('table');
      const shim = recordingNode();
      enable(path.join(tmp, 'other'));
      const r = runScript(RUN_SH, { env: { TOTAL_RECALL_HOME: path.join(tmp, 'trh'), HOME: path.join(tmp, 'other', '..'), PATH: shim.dir }, cwd: tmp, input: payload() });
      assert.equal(r.code, 0);
      assert.equal(fs.existsSync(shim.mark), false);
    });
  });

  test('Run_WindowsOstype_Leaves', () => {
    const dataDir = tmpDir('data');
    enable(dataDir);
    for (const ostype of ['msys', 'cygwin', 'win32']) {
      const shim = recordingNode();
      const r = runScript(sourcingWrapper(ostype), {
        env: { PATH: shim.dir, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
        cwd: tmpDir('cwd'), input: payload(),
      });
      assert.equal(r.code, 0, ostype);
      assert.equal(r.stdout + r.stderr, '', ostype);
      assert.equal(fs.existsSync(shim.mark), false, `${ostype} must not start node`);
    }
    // The same sourcing form with a Unix OSTYPE does start it, so the leave above is the OSTYPE test.
    const shim = recordingNode();
    const r = runScript(sourcingWrapper('linux-gnu'), {
      env: { PATH: shim.dir, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
      cwd: tmpDir('cwd'), input: payload(),
    });
    assert.equal(r.code, 0);
    assert.equal(fs.existsSync(shim.mark), true);
  });

  test('Run_CdpathSet_RelativeScriptPath_StillFindsThePluginRoot', () => {
    // Run as `bash hooks/live-marker/run.sh` from the plugin root, with a CDPATH entry holding a decoy
    // hooks/live-marker: the wrapper's `cd` must not search CDPATH (which would also print the directory).
    const tree = pluginTree();
    const decoy = tmpDir('decoy');
    fs.mkdirSync(path.join(decoy, 'hooks', 'live-marker'), { recursive: true });
    const dataDir = tmpDir('data');
    enable(dataDir);
    const r = spawnSync(BASH, ['hooks/live-marker/run.sh'], {
      env: { PATH: NODE_DIR, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home'), CDPATH: decoy },
      cwd: tree, input: payload(), encoding: 'utf8', timeout: 20000,
    });
    assert.equal(r.status, 0);
    assert.equal(r.stdout + r.stderr, '');
    const written = fs.readdirSync(path.join(dataDir, 'live')).filter((n) => n.startsWith('claude-code-'));
    assert.equal(written.length, 1, 'the writer must have run from the real plugin root');
  });

  test('Run_NodeMissing_ExitsZeroSilently', () => {
    const dataDir = tmpDir('data');
    enable(dataDir);
    const r = runScript(RUN_SH, {
      env: { PATH: tmpDir('emptybin'), TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
      cwd: tmpDir('cwd'),
      input: payload(),
    });
    assert.equal(r.code, 0);
    assert.equal(r.stdout, '');
    assert.equal(r.stderr, '');
  });

  test('Run_WorksWithoutEngineOrFloor', () => {
    const tree = pluginTree();
    assert.equal(fs.existsSync(path.join(tree, 'binaries')), false);
    const dataDir = tmpDir('data');
    enable(dataDir);
    fs.writeFileSync(path.join(dataDir, 'config.toml'), '[tiers.pinned]\nfloor_enabled = false\n');
    const r = spawnSync(BASH, [path.join(tree, 'hooks/live-marker/run.sh')], {
      env: { PATH: NODE_DIR, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') },
      cwd: tmpDir('cwd'), input: payload(), encoding: 'utf8', timeout: 20000,
    });
    assert.equal(r.status, 0);
    assert.equal(r.stdout + r.stderr, '');
    const written = fs.readdirSync(path.join(dataDir, 'live')).filter((n) => n.startsWith('claude-code-'));
    assert.equal(written.length, 1);
  });

  test('TwoHosts_OneDirectory_TwoMarkers', async () => {
    const tree = pluginTree();
    const script = path.join(tree, 'hooks/live-marker/run.sh');
    const dataDir = tmpDir('data');
    enable(dataDir);
    const cwd = tmpDir('shared-cwd');
    const env = { PATH: NODE_DIR, TOTAL_RECALL_HOME: dataDir, HOME: tmpDir('home') };
    const a = startHost({ script, env, cwd });
    const b = startHost({ script, env, cwd });
    const idA = 'aaaaaaaa-0000-4000-8000-00000000000a';
    const idB = 'bbbbbbbb-0000-4000-8000-00000000000b';
    try {
      for (const [host, id] of [[a, idA], [b, idB]]) {
        const r = await host.send(payload({ session_id: id, transcript_path: `/home/u/.claude/projects/-proj/${id}.jsonl`, hook_event_name: 'SessionStart' }));
        assert.equal(r.code, 0);
        assert.equal(r.out + r.err, '');
      }
      const live = path.join(dataDir, 'live');
      assert.deepEqual(fs.readdirSync(live).filter((n) => n.startsWith('claude-code-')).sort(),
        [`claude-code-${a.pid}.json`, `claude-code-${b.pid}.json`].sort());
      assert.equal(JSON.parse(fs.readFileSync(path.join(live, `claude-code-${a.pid}.json`), 'utf8')).session_id, idA);
      assert.equal(JSON.parse(fs.readFileSync(path.join(live, `claude-code-${b.pid}.json`), 'utf8')).session_id, idB);

      await a.kill(); // reaped: its pid is gone, not a zombie
      const r = await b.send(payload({ session_id: idB, transcript_path: `/home/u/.claude/projects/-proj/${idB}.jsonl`, hook_event_name: 'SessionStart' }));
      assert.equal(r.code, 0);
      assert.deepEqual(fs.readdirSync(live).filter((n) => n.startsWith('claude-code-')), [`claude-code-${b.pid}.json`]);
      assert.equal(fs.existsSync(path.join(live, 'enabled')), true);
    } finally {
      await a.kill();
      await b.kill();
    }
  });
});
