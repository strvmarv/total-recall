// skills/develop/scripts/ghpin.js — account selection, pinned `gh`/`git` command builders, the
// no-shell `run` tool and the per-run scratch directory (spec section 6, check 4).
// Zero dependencies, ESM.
//
//   node ghpin.js scratch
//   node ghpin.js run <jsonFile>       {tool, login, args, out?, stdin?}
//   node ghpin.js pick <jsonFile>      {accounts: [{login, perms}], active, profileAccount?}
//   node ghpin.js command <jsonFile>   {shell, tool, login, args}

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { isLogin } from './checkpoints.js';

export const SUBCOMMANDS = ['scratch', 'run', 'pick', 'command'];

const MAX_BUFFER = 512 * 1024 * 1024;
const GIT_CRED_ARGS = ['-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential'];

// ---- account selection ----

/** True when the permissions object grants write access (`admin`, `maintain` or `push`). */
export function qualifies(perms) {
  return !!perms && (perms.admin === true || perms.maintain === true || perms.push === true);
}

function fail(error, detail) {
  return { ok: false, error, detail };
}

/** Picks the account to pin every `gh`/`git` call to (spec section 6, check 4). */
export function pickAccount(input) {
  const accounts = Array.isArray(input?.accounts) ? input.accounts : [];
  const active = input?.active;
  const profileAccount = input?.profileAccount;
  for (const a of accounts) {
    if (!a || !isLogin(a.login)) return fail('bad-login', 'an account login is not a valid GitHub login');
  }
  const qualifying = accounts.filter((a) => qualifies(a.perms)).map((a) => a.login);

  if (profileAccount !== undefined && profileAccount !== null) {
    if (!isLogin(profileAccount)) return fail('bad-login', 'the profile account is not a valid GitHub login');
    if (!qualifying.includes(profileAccount)) {
      return fail('profile-account-not-qualifying', `the profile account ${profileAccount} has no write access to this repository`);
    }
    return { ok: true, login: profileAccount, ask: [] };
  }

  if (qualifying.length === 0) {
    const found = accounts.map((a) => a.login).join(', ') || 'none';
    return fail('none-qualify', `no logged-in account has write access to this repository; accounts found: ${found}`);
  }
  if (typeof active === 'string' && qualifying.includes(active)) return { ok: true, login: active, ask: [] };
  if (qualifying.length === 1) return { ok: true, login: qualifying[0], ask: [] };
  return { ok: true, login: qualifying[0], ask: qualifying };
}

// ---- command builders ----

const BASH_SAFE_RE = /^[A-Za-z0-9_./:=@,+%-]+$/;
const BAD_ARG_RE = /["`$\r\n\0]/;

function checkArg(arg) {
  if (typeof arg !== 'string' || arg === '' || arg.endsWith('\\') || BAD_ARG_RE.test(arg)) {
    throw new Error('bad arg');
  }
}

function quoteBash(arg) {
  return BASH_SAFE_RE.test(arg) ? arg : `'${arg.replace(/'/g, `'\\''`)}'`;
}

function quotePowerShell(arg) {
  return `'${arg.replace(/['‘’‚‛]/g, (c) => c + c)}'`;
}

/** Builds the one-line shell command that runs `tool args` pinned to account `login`. */
export function command(shell, tool, login, args) {
  if (!isLogin(login)) throw new Error('bad login');
  if (shell !== 'bash' && shell !== 'powershell') throw new Error('bad shell');
  if (tool !== 'gh' && tool !== 'git') throw new Error('bad tool');
  if (!Array.isArray(args)) throw new Error('bad arg');
  for (const a of args) checkArg(a);
  const quote = shell === 'bash' ? quoteBash : quotePowerShell;
  const A = args.map(quote).join(' ');
  if (shell === 'bash') {
    const pin = `t=$(gh auth token --user ${login}) && [ -n "$t" ] && GH_TOKEN="$t"`;
    if (tool === 'gh') return `${pin} gh ${A}`;
    return `${pin} GIT_TERMINAL_PROMPT=0 git -c credential.helper= -c 'credential.helper=!gh auth git-credential' ${A}`;
  }
  const get = `$env:GH_TOKEN = (gh auth token --user ${login}); if (-not $env:GH_TOKEN) { throw 'no token' }`;
  if (tool === 'gh') {
    return `$e = 1; $p = $env:GH_TOKEN; try { ${get}; gh ${A}; $e = $LASTEXITCODE } finally { $env:GH_TOKEN = $p }; exit $e`;
  }
  return `$e = 1; $p = $env:GH_TOKEN; $q = $env:GIT_TERMINAL_PROMPT; try { ${get}; $env:GIT_TERMINAL_PROMPT = '0'; git -c credential.helper= -c 'credential.helper=!gh auth git-credential' ${A}; $e = $LASTEXITCODE } finally { $env:GH_TOKEN = $p; $env:GIT_TERMINAL_PROMPT = $q }; exit $e`;
}

// ---- run (no shell) ----

function text(value) {
  return value === undefined || value === null ? '' : value.toString();
}

function removeOut(out) {
  if (out) fs.rmSync(out, { force: true });
}

function lastHttpStatus(stderr) {
  let last = null;
  for (const m of stderr.matchAll(/HTTP (\d{3})/g)) last = Number(m[1]);
  return last;
}

/**
 * Runs `gh` or `git` pinned to `login` with no shell: the token comes from `gh auth token`,
 * the arguments are passed as an array, stdout goes only to `out` (and only on success).
 */
export function runTool(deps, input) {
  const spawn = (deps && deps.spawn) || spawnSync;
  const { tool, login, args, out, stdin } = input || {};
  if (!isLogin(login)) throw new Error('bad login');
  if (tool !== 'gh' && tool !== 'git') throw new Error('bad tool');
  if (!Array.isArray(args) || !args.every((a) => typeof a === 'string')) throw new Error('bad args');
  if (out !== undefined && typeof out !== 'string') throw new Error('bad out');
  if (stdin !== undefined && typeof stdin !== 'string') throw new Error('bad stdin');

  const tokenRun = spawn('gh', ['auth', 'token', '--user', login], { maxBuffer: MAX_BUFFER });
  const token = text(tokenRun && tokenRun.stdout).trim();
  if (token === '') {
    removeOut(out);
    return { ok: false, status: null, httpStatus: null, error: 'no token', stderr: text(tokenRun && tokenRun.stderr) };
  }

  let input_;
  try {
    if (stdin !== undefined) input_ = fs.readFileSync(stdin);
  } catch (err) {
    removeOut(out);
    return { ok: false, status: null, httpStatus: null, error: `cannot read stdin file: ${err.code || err.message}`, stderr: '' };
  }

  const env = { ...process.env, GH_TOKEN: token };
  let argv = args;
  if (tool === 'git') {
    env.GIT_TERMINAL_PROMPT = '0';
    argv = [...GIT_CRED_ARGS, ...args];
  }
  const opts = { env, maxBuffer: MAX_BUFFER, shell: false, windowsHide: true };
  if (input_ !== undefined) opts.input = input_;

  const r = spawn(tool, argv, opts) || {};
  const stderr = text(r.stderr).split(token).join('***');
  const status = typeof r.status === 'number' ? r.status : null;
  const httpStatus = lastHttpStatus(stderr);
  const ok = status === 0 && !r.error;
  if (!ok) {
    removeOut(out);
    const result = { ok: false, status, httpStatus, stderr };
    const error = r.error ? (r.error.code ? `${r.error.code}: ${r.error.message}` : r.error.message) : (r.signal ? `signal ${r.signal}` : null);
    if (error) result.error = String(error).split(token).join('***');
    return result;
  }
  if (out) fs.writeFileSync(out, r.stdout === undefined || r.stdout === null ? Buffer.alloc(0) : r.stdout);
  return { ok: true, status, httpStatus, stderr };
}

// ---- scratch ----

/** Creates this run's scratch directory and returns its absolute native path. */
export function scratch() {
  return path.resolve(fs.mkdtempSync(path.join(os.tmpdir(), 'develop-')));
}

// ---- CLI ----

function readJson(file, usage) {
  if (!file) throw new Error(usage);
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function dispatch(argv) {
  const [cmd, file] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'scratch': return { dir: scratch() };
    case 'run': return runTool({ spawn: spawnSync }, readJson(file, 'usage: run <jsonFile>'));
    case 'pick': return pickAccount(readJson(file, 'usage: pick <jsonFile>'));
    case 'command': {
      const i = readJson(file, 'usage: command <jsonFile>');
      return { command: command(i.shell, i.tool, i.login, i.args) };
    }
    default: return { ok: false, error: 'unknown subcommand' };
  }
}

function main() {
  let result;
  try {
    result = dispatch(process.argv.slice(2));
  } catch (err) {
    result = { ok: false, error: err && err.message ? err.message : 'failed' };
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
  if (result && result.ok === false) process.exitCode = 1;
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

if (isMain(import.meta.url)) main();
