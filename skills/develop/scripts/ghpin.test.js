import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import { qualifies, pickAccount, command, runTool, scratch, SUBCOMMANDS } from './ghpin.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SKILL = path.join(HERE, '..', 'SKILL.md');
const GHPIN = path.join(HERE, 'ghpin.js');
const PINNED = readGolden(SKILL, 'pinned-commands');

const MIB = 1024 * 1024;

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'ghpin-test-'));
}

/** Runs `fn` with the given environment variables set (undefined removes) and restores them. */
function withEnv(vars, fn) {
  const before = {};
  for (const k of Object.keys(vars)) before[k] = process.env[k];
  try {
    for (const [k, v] of Object.entries(vars)) {
      if (v === undefined) delete process.env[k];
      else process.env[k] = v;
    }
    return fn();
  } finally {
    for (const [k, v] of Object.entries(before)) {
      if (v === undefined) delete process.env[k];
      else process.env[k] = v;
    }
  }
}

function cli(args) {
  const result = spawnSync(process.execPath, [GHPIN, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

function writeJson(value) {
  const file = path.join(tmpDir(), 'in.json');
  fs.writeFileSync(file, JSON.stringify(value));
  return file;
}

// ---- qualifies / pickAccount ----

test('qualifies: admin, maintain or push; anything else (or null) does not', () => {
  assert.equal(qualifies({ admin: true }), true);
  assert.equal(qualifies({ maintain: true }), true);
  assert.equal(qualifies({ push: true }), true);
  assert.equal(qualifies({ admin: false, maintain: false, push: false }), false);
  assert.equal(qualifies({ pull: true }), false);
  assert.equal(qualifies({}), false);
  assert.equal(qualifies(null), false);
  assert.equal(qualifies(undefined), false);
});

test('pickAccount: an active work account without push loses to strvmarv with push', () => {
  const r = pickAccount({
    accounts: [
      { login: 'jane-doe_acme', perms: { pull: true, push: false } },
      { login: 'strvmarv', perms: { pull: true, push: true } },
    ],
    active: 'jane-doe_acme',
  });
  assert.deepEqual(r, { ok: true, login: 'strvmarv', ask: [] });
});

test('pickAccount: two qualifying accounts, the active one wins and nothing is asked', () => {
  const r = pickAccount({
    accounts: [
      { login: 'strvmarv', perms: { push: true } },
      { login: 'second', perms: { admin: true } },
    ],
    active: 'second',
  });
  assert.deepEqual(r, { ok: true, login: 'second', ask: [] });
});

test('pickAccount: two qualifying accounts and a non-qualifying active one picks the first and asks', () => {
  const r = pickAccount({
    accounts: [
      { login: 'strvmarv', perms: { push: true } },
      { login: 'second', perms: { maintain: true } },
      { login: 'third', perms: null },
    ],
    active: 'third',
  });
  assert.deepEqual(r, { ok: true, login: 'strvmarv', ask: ['strvmarv', 'second'] });
});

test('pickAccount: the only qualifying account is chosen with no question', () => {
  const r = pickAccount({
    accounts: [
      { login: 'a', perms: null },
      { login: 'b', perms: { push: true } },
    ],
    active: 'a',
  });
  assert.deepEqual(r, { ok: true, login: 'b', ask: [] });
});

test('pickAccount: a qualifying profileAccount wins over the active account', () => {
  const r = pickAccount({
    accounts: [
      { login: 'strvmarv', perms: { push: true } },
      { login: 'other', perms: { push: true } },
    ],
    active: 'other',
    profileAccount: 'strvmarv',
  });
  assert.deepEqual(r, { ok: true, login: 'strvmarv', ask: [] });
});

test('pickAccount: a profileAccount that does not qualify (or is not logged in) is profile-account-not-qualifying', () => {
  const accounts = [
    { login: 'strvmarv', perms: { pull: true } },
    { login: 'other', perms: { push: true } },
  ];
  const r = pickAccount({ accounts, active: 'other', profileAccount: 'strvmarv' });
  assert.equal(r.ok, false);
  assert.equal(r.error, 'profile-account-not-qualifying');
  assert.match(r.detail, /strvmarv/);
  const missing = pickAccount({ accounts, active: 'other', profileAccount: 'nobody' });
  assert.equal(missing.ok, false);
  assert.equal(missing.error, 'profile-account-not-qualifying');
});

test('pickAccount: no qualifying account is none-qualify and detail names every login', () => {
  const r = pickAccount({
    accounts: [
      { login: 'jane-doe_acme', perms: { pull: true } },
      { login: 'strvmarv', perms: null },
    ],
    active: 'strvmarv',
  });
  assert.equal(r.ok, false);
  assert.equal(r.error, 'none-qualify');
  assert.match(r.detail, /jane-doe_acme/);
  assert.match(r.detail, /strvmarv/);
});

test('pickAccount: a hostile profileAccount or account login is bad-login', () => {
  const accounts = [{ login: 'strvmarv', perms: { push: true } }];
  for (const bad of ['x; rm', '$(id)', '', 'a b']) {
    const r = pickAccount({ accounts, active: 'strvmarv', profileAccount: bad });
    assert.equal(r.ok, false, bad);
    assert.equal(r.error, 'bad-login', bad);
  }
  const r = pickAccount({ accounts: [{ login: 'x; rm', perms: { push: true } }], active: 'x; rm' });
  assert.equal(r.ok, false);
  assert.equal(r.error, 'bad-login');
});

// ---- command: golden ----

test('command: the pinned-commands golden in SKILL.md matches exactly', () => {
  assert.equal(PINNED.length, 4);
  for (const g of PINNED) {
    assert.equal(command(g.shell, g.tool, g.login, g.args), g.expect, `${g.shell} ${g.tool}`);
  }
});

test('command: arguments are quoted per shell', () => {
  assert.equal(
    command('bash', 'gh', 'strvmarv', ['issue', "it's here"]),
    `t=$(gh auth token --user strvmarv) && [ -n "$t" ] && GH_TOKEN="$t" gh issue 'it'\\''s here'`,
  );
  assert.ok(command('bash', 'gh', 'strvmarv', ['issue', 'two words']).endsWith(` gh issue 'two words'`));
  assert.ok(command('bash', 'gh', 'strvmarv', ['a/b.c:d=e@f,g+h%i-j_k']).endsWith(' gh a/b.c:d=e@f,g+h%i-j_k'));
  assert.ok(command('bash', 'gh', 'strvmarv', ['*']).endsWith(` gh '*'`));
  assert.ok(command('bash', 'gh', 'strvmarv', ['a;b']).endsWith(` gh 'a;b'`));
});

test('command: bash git ends with the arguments', () => {
  const c = command('bash', 'git', 'jane-doe_acme', ['push', 'origin', 'x']);
  assert.ok(c.includes('--user jane-doe_acme'));
  assert.ok(c.endsWith('push origin x'));
});

test('command: every PowerShell argument is single-quoted', () => {
  const c = command('powershell', 'gh', 'strvmarv', ['issue', 'edit', '1', '--add-assignee', '@me']);
  assert.ok(c.includes(`gh 'issue' 'edit' '1' '--add-assignee' '@me';`), c);
  assert.ok(c.includes("'@me'"));
});

test('command: PowerShell doubles all five quote characters', () => {
  const c = command('powershell', 'gh', 'strvmarv', ['x’; echo INJECTED; ’']);
  assert.ok(c.includes(`'x’’; echo INJECTED; ’’'`), c);
  const q = command('powershell', 'gh', 'strvmarv', [`a'b‘c’d‚e‛f`]);
  assert.ok(q.includes(`'a''b‘‘c’’d‚‚e‛‛f'`), q);
});

test('command: bad arg for characters that cannot be quoted safely', () => {
  const bad = ['a"b', 'a`b', 'a$b', 'a\nb', 'a\rb', 'a\0b', '', 'a\\', '\\'];
  for (const shell of ['bash', 'powershell']) {
    for (const tool of ['gh', 'git']) {
      for (const arg of bad) {
        assert.throws(() => command(shell, tool, 'strvmarv', ['ok', arg]), { message: 'bad arg' }, `${shell} ${tool} ${JSON.stringify(arg)}`);
      }
    }
  }
  assert.throws(() => command('bash', 'gh', 'strvmarv', [5]), { message: 'bad arg' });
});

test('command: bad login for every shell and tool', () => {
  for (const shell of ['bash', 'powershell']) {
    for (const tool of ['gh', 'git']) {
      for (const login of ['x; rm -rf /', '$(id)', '', 'a b', '-x'.repeat(30), undefined, null]) {
        assert.throws(() => command(shell, tool, login, ['api', 'user']), { message: 'bad login' }, `${shell} ${tool} ${String(login)}`);
      }
    }
  }
});

test('command: a login with _ and - appears verbatim and only where the golden puts it', () => {
  for (const shell of ['bash', 'powershell']) {
    for (const tool of ['gh', 'git']) {
      const c = command(shell, tool, 'jane-doe_acme', ['api', 'user']);
      assert.equal(c.split('jane-doe_acme').length, 2, 'exactly one occurrence');
      assert.ok(c.includes('--user jane-doe_acme)'));
    }
  }
});

// ---- command: real PowerShell exit codes ----

function findPowerShell() {
  for (const exe of ['pwsh', 'powershell']) {
    const r = spawnSync(exe, ['-NoProfile', '-NonInteractive', '-Command', 'exit 0'], { encoding: 'utf8' });
    if (!r.error && r.status === 0) return exe;
  }
  return null;
}

const PS = findPowerShell();
const PS_SKIP = PS ? false : 'no powershell or pwsh on this machine';

function psAbsolute(exe) {
  const r = spawnSync(exe, ['-NoProfile', '-NonInteractive', '-Command', '(Get-Process -Id $PID).Path'], { encoding: 'utf8' });
  return r.stdout.trim();
}

function runPs(exe, script, env) {
  const encoded = Buffer.from(script, 'utf16le').toString('base64');
  return spawnSync(exe, ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], { encoding: 'utf8', env });
}

function makeStubGh(dir) {
  if (process.platform === 'win32') {
    fs.writeFileSync(path.join(dir, 'gh.cmd'), '@echo off\r\nif "%1"=="auth" (\r\n  echo tok\r\n  exit /b 0\r\n)\r\nexit /b 3\r\n');
  } else {
    const file = path.join(dir, 'gh');
    fs.writeFileSync(file, '#!/bin/sh\nif [ "$1" = "auth" ]; then echo tok; exit 0; fi\nexit 3\n');
    fs.chmodSync(file, 0o755);
  }
}

test('command: the PowerShell gh form keeps the tool exit code', { skip: PS_SKIP }, () => {
  const dir = tmpDir();
  makeStubGh(dir);
  const env = { ...process.env, PATH: `${dir}${path.delimiter}${process.env.PATH}` };
  const r = runPs(PS, command('powershell', 'gh', 'strvmarv', ['api', 'user']), env);
  assert.equal(r.status, 3, `stdout=${r.stdout} stderr=${r.stderr}`);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('command: the PowerShell gh form exits non-zero when gh is not found', { skip: PS_SKIP }, () => {
  const emptyDir = tmpDir();
  const exe = psAbsolute(PS) || PS;
  const env = { ...process.env, PATH: emptyDir };
  const r = runPs(exe, command('powershell', 'gh', 'strvmarv', ['api', 'user']), env);
  assert.notEqual(r.status, 0, `stdout=${r.stdout} stderr=${r.stderr}`);
  fs.rmSync(emptyDir, { recursive: true, force: true });
});

// ---- runTool: fake spawn ----

/** A fake deps.spawn: the first call (gh auth token) returns `tokenResult`; later calls return `childResult`. */
function fakeSpawn(tokenResult, childResult) {
  const calls = [];
  const spawn = (cmd, args, opts) => {
    calls.push({ cmd, args, opts });
    if (cmd === 'gh' && args[0] === 'auth') return tokenResult;
    return typeof childResult === 'function' ? childResult(cmd, args, opts) : childResult;
  };
  return { spawn, calls };
}

const OKTOKEN = { status: 0, stdout: Buffer.from('tok\n'), stderr: Buffer.alloc(0) };

test('runTool: an empty or whitespace-only token runs nothing and removes any existing out', () => {
  for (const stdout of ['', '\r\n', '  \n\t ']) {
    const dir = tmpDir();
    const out = path.join(dir, 'out.json');
    fs.writeFileSync(out, 'stale');
    const { spawn, calls } = fakeSpawn({ status: 1, stdout: Buffer.from(stdout), stderr: Buffer.from('not logged in') }, { status: 0 });
    const r = runTool({ spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'user'], out });
    assert.deepEqual(r, { ok: false, status: null, httpStatus: null, error: 'no token', stderr: 'not logged in' });
    assert.equal(calls.length, 1);
    assert.deepEqual(calls[0].args, ['auth', 'token', '--user', 'strvmarv']);
    assert.equal(fs.existsSync(out), false);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('runTool: the token is fetched without a shell and trimmed (tok CRLF reaches the child as tok)', () => {
  const { spawn, calls } = fakeSpawn(
    { status: 0, stdout: Buffer.from('tok\r\n'), stderr: Buffer.alloc(0) },
    { status: 0, stdout: Buffer.from('{}'), stderr: Buffer.alloc(0) },
  );
  const r = runTool({ spawn }, { tool: 'gh', login: 'jane-doe_acme', args: ['api', 'user'] });
  assert.equal(r.ok, true);
  assert.equal(r.status, 0);
  assert.equal(r.httpStatus, null);
  assert.equal(calls.length, 2);
  assert.equal(calls[0].cmd, 'gh');
  assert.deepEqual(calls[0].args, ['auth', 'token', '--user', 'jane-doe_acme']);
  assert.ok(!calls[0].opts || !calls[0].opts.shell);
  assert.equal(calls[1].cmd, 'gh');
  assert.deepEqual(calls[1].args, ['api', 'user']);
  assert.ok(!calls[1].opts.shell);
  assert.equal(calls[1].opts.env.GH_TOKEN, 'tok');
});

test('runTool: the child environment carries the parent environment (PATH, a sentinel)', () => {
  withEnv({ GHPIN_SENTINEL: 'sentinel-value' }, () => {
    const { spawn, calls } = fakeSpawn(OKTOKEN, { status: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
    runTool({ spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'user'] });
    const env = calls[1].opts.env;
    assert.equal(env.GHPIN_SENTINEL, 'sentinel-value');
    assert.equal(env.PATH ?? env.Path, process.env.PATH ?? process.env.Path);
    assert.equal(env.GH_TOKEN, 'tok');
  });
});

test('runTool: gh gets the arguments as given; git gets prompt off and the two -c options first', () => {
  const g = fakeSpawn(OKTOKEN, { status: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
  runTool({ spawn: g.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'user'] });
  assert.equal(g.calls[1].opts.env.GIT_TERMINAL_PROMPT, process.env.GIT_TERMINAL_PROMPT);
  const t = fakeSpawn(OKTOKEN, { status: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
  runTool({ spawn: t.spawn }, { tool: 'git', login: 'strvmarv', args: ['push', 'origin', 'x'] });
  assert.equal(t.calls[1].cmd, 'git');
  assert.deepEqual(t.calls[1].args, ['-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential', 'push', 'origin', 'x']);
  assert.equal(t.calls[1].opts.env.GIT_TERMINAL_PROMPT, '0');
  assert.equal(t.calls[1].opts.env.GH_TOKEN, 'tok');
});

test('runTool: maxBuffer is at least 2 MiB and a child with more than 2 MiB of stdout still lands whole in out', () => {
  const dir = tmpDir();
  const out = path.join(dir, 'big.bin');
  const big = Buffer.alloc(2 * MIB + 17, 0x61);
  const { spawn, calls } = fakeSpawn(OKTOKEN, { status: 0, stdout: big, stderr: Buffer.alloc(0) });
  const r = runTool({ spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'], out });
  assert.ok(calls[1].opts.maxBuffer >= 2 * MIB);
  assert.equal(calls[1].opts.maxBuffer, 512 * MIB);
  assert.equal(r.ok, true);
  assert.ok(fs.readFileSync(out).equals(big));
  fs.rmSync(dir, { recursive: true, force: true });
});

test('runTool: httpStatus comes from the last HTTP nnn in stderr and a failure is not ok', () => {
  const cases = [
    ['gh: Not Found (HTTP 404)\n', 404],
    ['gh: Forbidden (HTTP 403)\n', 403],
    ['gh: Server Error (HTTP 502)\n', 502],
    ['warning (HTTP 301)\ngh: Not Found (HTTP 404)\n', 404],
    ['some other failure\n', null],
  ];
  for (const [stderr, expected] of cases) {
    const { spawn } = fakeSpawn(OKTOKEN, { status: 1, stdout: Buffer.from('{"message":"x"}'), stderr: Buffer.from(stderr) });
    const r = runTool({ spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'] });
    assert.equal(r.ok, false);
    assert.equal(r.status, 1);
    assert.equal(r.httpStatus, expected);
    assert.equal(r.stderr, stderr);
  }
});

test('runTool: a spawn error with no stdout or stderr (missing gh) does not throw', () => {
  const err = Object.assign(new Error('spawn gh ENOENT'), { code: 'ENOENT' });
  const { spawn } = fakeSpawn(OKTOKEN, { error: err, status: null, stdout: undefined, stderr: undefined });
  const r = runTool({ spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'] });
  assert.equal(r.ok, false);
  assert.equal(r.status, null);
  assert.equal(r.httpStatus, null);
  assert.match(r.error, /ENOENT/);
  assert.equal(r.stderr, '');
});

test('runTool: a failing child leaves no out file even when one existed, and a successful one writes it', () => {
  const dir = tmpDir();
  const out = path.join(dir, 'out.json');
  fs.writeFileSync(out, 'stale data');
  const fail = fakeSpawn(OKTOKEN, { status: 1, stdout: Buffer.from('{"message":"Not Found"}'), stderr: Buffer.from('gh: Not Found (HTTP 404)') });
  const r = runTool({ spawn: fail.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'], out });
  assert.equal(r.ok, false);
  assert.equal(fs.existsSync(out), false);
  const ok = fakeSpawn(OKTOKEN, { status: 0, stdout: Buffer.from('fresh'), stderr: Buffer.alloc(0) });
  const r2 = runTool({ spawn: ok.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'], out });
  assert.equal(r2.ok, true);
  assert.equal(fs.readFileSync(out, 'utf8'), 'fresh');
  fs.rmSync(dir, { recursive: true, force: true });
});

test('runTool: a bad login throws bad login before anything is spawned', () => {
  const { spawn, calls } = fakeSpawn(OKTOKEN, { status: 0 });
  for (const login of ['x; rm -rf /', '$(id)', '', undefined]) {
    assert.throws(() => runTool({ spawn }, { tool: 'gh', login, args: ['api', 'user'] }), { message: 'bad login' });
  }
  assert.equal(calls.length, 0);
});

test('runTool: a token that appears in stderr is masked', () => {
  const { spawn } = fakeSpawn(OKTOKEN, { status: 1, stdout: Buffer.alloc(0), stderr: Buffer.from('bad credentials for tok here') });
  const r = runTool({ spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'] });
  assert.ok(!r.stderr.includes('tok'));
});

// ---- runTool: the real spawnSync against a stub script ----

const STUB = `
import fs from 'node:fs';
const [tool, ...argv] = process.argv.slice(2);
let stdin = Buffer.alloc(0);
try { stdin = fs.readFileSync(0); } catch { /* no stdin */ }
const report = {
  tool,
  argv,
  env: {
    GH_TOKEN: process.env.GH_TOKEN ?? null,
    GIT_TERMINAL_PROMPT: process.env.GIT_TERMINAL_PROMPT ?? null,
    SENTINEL: process.env.GHPIN_SENTINEL ?? null,
    PATH: process.env.PATH ?? process.env.Path ?? null,
  },
  stdin: stdin.toString('base64'),
};
if (process.env.STUB_REPORT) fs.writeFileSync(process.env.STUB_REPORT, JSON.stringify(report));
const mode = process.env.STUB_MODE || 'quiet';
if (mode === 'big') process.stdout.write(Buffer.alloc(3 * 1024 * 1024, 0x62));
if (mode === 'bytes') process.stdout.write(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('a’b — c ü', 'utf8')]));
if (mode === 'stdin') process.stdout.write(stdin);
if (mode === 'fail') {
  process.stdout.write('{"message":"Not Found"}');
  process.stderr.write('gh: Not Found (HTTP 404)\\n');
  process.exitCode = 1;
}
`;

/** A deps.spawn that keeps the token step fake (never the real gh) and runs the tool as a stub script. */
function stubSpawn(stubFile) {
  return (cmd, args, opts) => {
    if (cmd === 'gh' && args[0] === 'auth') {
      return { status: 0, stdout: Buffer.from('tok\r\n'), stderr: Buffer.alloc(0) };
    }
    assert.ok(cmd === 'gh' || cmd === 'git');
    return spawnSync(process.execPath, [stubFile, cmd, ...args], opts);
  };
}

function stubSetup() {
  const dir = tmpDir();
  const stubFile = path.join(dir, 'stub.mjs');
  fs.writeFileSync(stubFile, STUB);
  return { dir, stubFile, report: path.join(dir, 'report.json'), spawn: stubSpawn(stubFile) };
}

function readReport(file) {
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

test('runTool (real spawnSync): environment, argument bytes, git options and the parent stay clean', () => {
  const s = stubSetup();
  withEnv({ GH_TOKEN: undefined, GIT_TERMINAL_PROMPT: undefined, GHPIN_SENTINEL: 'real-sentinel', STUB_REPORT: s.report, STUB_MODE: 'quiet' }, () => {
    const weird = 'q"u$o`te\nline';
    const r = runTool({ spawn: s.spawn }, { tool: 'git', login: 'strvmarv', args: ['push', weird, 'it\'s'] });
    assert.equal(r.ok, true, JSON.stringify(r));
    const rep = readReport(s.report);
    assert.equal(rep.tool, 'git');
    assert.deepEqual(rep.argv, ['-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential', 'push', weird, 'it\'s']);
    assert.equal(rep.env.GH_TOKEN, 'tok');
    assert.equal(rep.env.GIT_TERMINAL_PROMPT, '0');
    assert.equal(rep.env.SENTINEL, 'real-sentinel');
    assert.ok(rep.env.PATH);
    assert.equal(process.env.GH_TOKEN, undefined);
    assert.equal(process.env.GIT_TERMINAL_PROMPT, undefined);
  });
  fs.rmSync(s.dir, { recursive: true, force: true });
});

test('runTool (real spawnSync): a gh call carries GH_TOKEN and the arguments unchanged', () => {
  const s = stubSetup();
  withEnv({ GH_TOKEN: undefined, STUB_REPORT: s.report, STUB_MODE: 'quiet' }, () => {
    const args = ['api', '-f', 'body=a"b$c`d\ne'];
    const r = runTool({ spawn: s.spawn }, { tool: 'gh', login: 'strvmarv', args });
    assert.equal(r.ok, true);
    const rep = readReport(s.report);
    assert.equal(rep.tool, 'gh');
    assert.deepEqual(rep.argv, args);
    assert.equal(rep.env.GH_TOKEN, 'tok');
    assert.equal(process.env.GH_TOKEN, undefined);
  });
  fs.rmSync(s.dir, { recursive: true, force: true });
});

test('runTool (real spawnSync): out holds the exact stdout bytes (BOM and UTF-8 intact)', () => {
  const s = stubSetup();
  const out = path.join(s.dir, 'out.bin');
  withEnv({ STUB_MODE: 'bytes', STUB_REPORT: undefined }, () => {
    const r = runTool({ spawn: s.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'], out });
    assert.equal(r.ok, true);
  });
  const expected = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from('a’b — c ü', 'utf8')]);
  assert.ok(fs.readFileSync(out).equals(expected));
  fs.rmSync(s.dir, { recursive: true, force: true });
});

test('runTool (real spawnSync): stdin file bytes reach the child', () => {
  const s = stubSetup();
  const stdinFile = path.join(s.dir, 'in.bin');
  const bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf, 0xff, 0x00, 0x0d, 0x0a]), Buffer.from('body ü', 'utf8')]);
  fs.writeFileSync(stdinFile, bytes);
  const out = path.join(s.dir, 'echo.bin');
  withEnv({ STUB_MODE: 'stdin', STUB_REPORT: s.report }, () => {
    const r = runTool({ spawn: s.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x', '--input', '-'], out, stdin: stdinFile });
    assert.equal(r.ok, true);
  });
  assert.equal(readReport(s.report).stdin, bytes.toString('base64'));
  assert.ok(fs.readFileSync(out).equals(bytes));
  fs.rmSync(s.dir, { recursive: true, force: true });
});

test('runTool (real spawnSync): more than 1 MiB of stdout does not hit ENOBUFS', () => {
  const s = stubSetup();
  const out = path.join(s.dir, 'big.bin');
  withEnv({ STUB_MODE: 'big', STUB_REPORT: undefined }, () => {
    const r = runTool({ spawn: s.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'], out });
    assert.equal(r.ok, true, JSON.stringify(r));
  });
  assert.ok(fs.readFileSync(out).equals(Buffer.alloc(3 * MIB, 0x62)));
  fs.rmSync(s.dir, { recursive: true, force: true });
});

test('runTool (real spawnSync): a failing child gives httpStatus, ok false and no out', () => {
  const s = stubSetup();
  const out = path.join(s.dir, 'out.json');
  fs.writeFileSync(out, 'stale');
  withEnv({ STUB_MODE: 'fail', STUB_REPORT: undefined }, () => {
    const r = runTool({ spawn: s.spawn }, { tool: 'gh', login: 'strvmarv', args: ['api', 'x'], out });
    assert.equal(r.ok, false);
    assert.equal(r.status, 1);
    assert.equal(r.httpStatus, 404);
  });
  assert.equal(fs.existsSync(out), false);
  fs.rmSync(s.dir, { recursive: true, force: true });
});

// ---- scratch ----

test('scratch: creates a fresh absolute directory under the OS temp directory', () => {
  const a = scratch();
  const b = scratch();
  try {
    assert.notEqual(a, b);
    for (const d of [a, b]) {
      assert.ok(path.isAbsolute(d));
      assert.ok(fs.statSync(d).isDirectory());
      assert.ok(path.relative(os.tmpdir(), d) && !path.relative(os.tmpdir(), d).startsWith('..'));
      assert.match(path.basename(d), /^develop-/);
    }
  } finally {
    fs.rmSync(a, { recursive: true, force: true });
    fs.rmSync(b, { recursive: true, force: true });
  }
});

// ---- CLI ----

test('SUBCOMMANDS dispatcher: every listed name dispatches, an unlisted word is rejected', () => {
  assert.deepEqual(SUBCOMMANDS, ['scratch', 'run', 'pick', 'command']);
  const made = [];
  for (const name of SUBCOMMANDS) {
    const { parsed } = cli([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
    if (parsed && parsed.dir) made.push(parsed.dir);
  }
  for (const d of made) fs.rmSync(d, { recursive: true, force: true });
  const bad = cli(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
  assert.deepEqual(cli([]).parsed, { ok: false, error: 'unknown subcommand' });
});

test('CLI scratch: two calls give two different absolute temp directories, usable as an out path', () => {
  const a = cli(['scratch']);
  const b = cli(['scratch']);
  const dirs = [a.parsed.dir, b.parsed.dir];
  try {
    assert.equal(a.status, 0);
    assert.deepEqual(Object.keys(a.parsed), ['dir']);
    assert.notEqual(dirs[0], dirs[1]);
    for (const d of dirs) {
      assert.ok(path.isAbsolute(d));
      if (process.platform === 'win32') assert.match(d, /^[A-Za-z]:\\/);
      assert.ok(fs.statSync(d).isDirectory());
      assert.ok(!path.relative(os.tmpdir(), d).startsWith('..'));
    }
    const file = path.join(dirs[0], 'note.txt');
    fs.writeFileSync(file, 'hello');
    const viaJson = JSON.parse(JSON.stringify({ out: file })).out;
    assert.equal(fs.readFileSync(viaJson, 'utf8'), 'hello');
  } finally {
    for (const d of dirs) fs.rmSync(d, { recursive: true, force: true });
  }
});

test('CLI pick: prints the pickAccount result and exits 1 on failure', () => {
  const ok = cli(['pick', writeJson({
    accounts: [{ login: 'jane-doe_acme', perms: { pull: true } }, { login: 'strvmarv', perms: { push: true } }],
    active: 'jane-doe_acme',
  })]);
  assert.equal(ok.status, 0);
  assert.deepEqual(ok.parsed, { ok: true, login: 'strvmarv', ask: [] });
  const none = cli(['pick', writeJson({ accounts: [{ login: 'a', perms: null }], active: 'a' })]);
  assert.equal(none.status, 1);
  assert.equal(none.parsed.error, 'none-qualify');
  const bad = cli(['pick', writeJson({ accounts: [], active: 'a', profileAccount: 'x; rm' })]);
  assert.equal(bad.status, 1);
  assert.equal(bad.parsed.error, 'bad-login');
  const missing = cli(['pick']);
  assert.equal(missing.status, 1);
  assert.equal(missing.parsed.ok, false);
});

test('CLI command: prints {command} or a bad login / bad arg object, never a stack trace', () => {
  for (const g of PINNED) {
    const r = cli(['command', writeJson({ shell: g.shell, tool: g.tool, login: g.login, args: g.args })]);
    assert.equal(r.status, 0);
    assert.deepEqual(r.parsed, { command: g.expect });
  }
  const login = cli(['command', writeJson({ shell: 'bash', tool: 'gh', login: 'x; rm -rf /', args: ['api'] })]);
  assert.equal(login.status, 1);
  assert.deepEqual(login.parsed, { ok: false, error: 'bad login' });
  const arg = cli(['command', writeJson({ shell: 'bash', tool: 'gh', login: 'strvmarv', args: ['a"b'] })]);
  assert.equal(arg.status, 1);
  assert.deepEqual(arg.parsed, { ok: false, error: 'bad arg' });
  assert.ok(!login.stderr.includes('at ') && !arg.stderr.includes('at '));
  const shell = cli(['command', writeJson({ shell: 'cmd', tool: 'gh', login: 'strvmarv', args: ['api'] })]);
  assert.equal(shell.status, 1);
  assert.equal(shell.parsed.ok, false);
});

test('CLI run: validation failures only (the real gh is never spawned here)', () => {
  const login = cli(['run', writeJson({ tool: 'gh', login: 'x; rm -rf /', args: ['api', 'user'] })]);
  assert.equal(login.status, 1);
  assert.deepEqual(login.parsed, { ok: false, error: 'bad login' });
  const tool = cli(['run', writeJson({ tool: 'curl', login: 'strvmarv', args: ['x'] })]);
  assert.equal(tool.status, 1);
  assert.equal(tool.parsed.ok, false);
  assert.equal(tool.parsed.error, 'bad tool');
  const args = cli(['run', writeJson({ tool: 'gh', login: 'strvmarv', args: 'api user' })]);
  assert.equal(args.status, 1);
  assert.equal(args.parsed.ok, false);
  assert.equal(args.parsed.error, 'bad args');
  const none = cli(['run']);
  assert.equal(none.status, 1);
  assert.equal(none.parsed.ok, false);
});

// ---- runTool: the profile read through git (ls-tree and show against origin/main) ----

const GIT_CRED_PREFIX = ['-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential'];
const PROFILE_READS = [
  ['ls-tree', '--name-only', 'origin/main', '--', '.github/develop.md'],
  ['show', 'origin/main:.github/develop.md'],
];

test('runTool (git profile reads): credential -c options precede the subcommand and no shell is used', () => {
  for (const args of PROFILE_READS) {
    const { spawn, calls } = fakeSpawn(OKTOKEN, { status: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
    const r = runTool({ spawn }, { tool: 'git', login: 'strvmarv', args });
    assert.equal(r.ok, true);
    assert.equal(calls.length, 2);
    assert.equal(calls[1].cmd, 'git');
    assert.deepEqual(calls[1].args, [...GIT_CRED_PREFIX, ...args]);
    assert.equal(calls[1].args.indexOf(args[0]), GIT_CRED_PREFIX.length);
    assert.ok(!calls[1].opts.shell);
    assert.equal(calls[1].opts.env.GIT_TERMINAL_PROMPT, '0');
  }
});

test('runTool (git profile reads): exit 0 with empty stdout writes an empty (zero-byte) out file, replacing a stale one', () => {
  for (const args of PROFILE_READS) {
    const dir = tmpDir();
    const out = path.join(dir, 'profile.out');
    fs.writeFileSync(out, 'stale');
    const { spawn } = fakeSpawn(OKTOKEN, { status: 0, stdout: Buffer.alloc(0), stderr: Buffer.alloc(0) });
    const r = runTool({ spawn }, { tool: 'git', login: 'strvmarv', args, out });
    assert.equal(r.ok, true);
    assert.equal(r.status, 0);
    assert.equal(fs.existsSync(out), true);
    assert.equal(fs.statSync(out).size, 0);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('runTool (git profile reads): exit 0 with output writes the bytes exactly (CRLF, BOM-looking bytes, no trailing newline)', () => {
  for (const args of PROFILE_READS) {
    const dir = tmpDir();
    const out = path.join(dir, 'profile.out');
    const bytes = Buffer.from([0xef, 0xbb, 0xbf, 0x74, 0x72, 0x0d, 0x0a, 0xc3, 0xa9, 0x0a, 0x78]);
    const { spawn } = fakeSpawn(OKTOKEN, { status: 0, stdout: bytes, stderr: Buffer.alloc(0) });
    const r = runTool({ spawn }, { tool: 'git', login: 'strvmarv', args, out });
    assert.equal(r.ok, true);
    assert.deepEqual(fs.readFileSync(out), bytes);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('runTool (git profile reads): a non-zero exit removes the out file and reports the status', () => {
  for (const args of PROFILE_READS) {
    const dir = tmpDir();
    const out = path.join(dir, 'profile.out');
    fs.writeFileSync(out, 'stale');
    const { spawn } = fakeSpawn(OKTOKEN, { status: 128, stdout: Buffer.from('partial'), stderr: Buffer.from('fatal: path \'.github/develop.md\' does not exist in \'origin/main\'') });
    const r = runTool({ spawn }, { tool: 'git', login: 'strvmarv', args, out });
    assert.equal(r.ok, false);
    assert.equal(r.status, 128);
    assert.equal(r.httpStatus, null);
    assert.match(r.stderr, /does not exist/);
    assert.equal(fs.existsSync(out), false);
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
