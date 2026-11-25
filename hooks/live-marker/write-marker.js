// hooks/live-marker/write-marker.js — the live-session marker writer (Claude Code).
//
// Run by hooks/live-marker/run.sh as `node write-marker.js <hostPid>` with the hook payload on
// stdin (SessionStart and UserPromptSubmit). It writes `<dataDir>/live/claude-code-<hostPid>.json`
// atomically, so the engine can find the session its host is running; after a SessionStart write
// it prunes stale markers. Nothing is trusted from here: the engine re-checks every marker.
//
// Contract: this process NEVER fails a hook. Every path exits 0 and prints nothing to stdout or
// stderr (no console output anywhere). Overall deadline 3 s; stdin is read for at most 500 ms and
// 64 KiB. Plain ESM, no dependency.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

export const HOST = 'claude-code';
export const DEADLINE_MS = 3000;
export const STDIN_CAP_MS = 500;
export const STDIN_CAP_BYTES = 64 * 1024;
export const MAX_PATH_CHARS = 4096;
export const MAX_PID = 2147483647;
export const PRUNE_MAX_NAMES = 200;
export const MARKER_MAX_AGE_MS = 30 * 24 * 3600 * 1000;
export const TMP_MAX_AGE_MS = 3600 * 1000;

const SESSION_ID = /^[A-Za-z0-9._-]{1,128}$/;
const PID_ARG = /^[1-9][0-9]{0,9}$/;
const MARKER_NAME = /^claude-code-([0-9]{1,10})\.json$/;
const TMP_NAME = /^claude-code-[0-9]{1,10}\.json\.[A-Za-z0-9._-]+\.tmp$/;

/** The same rule as ClaudeCodeAdapter.IsValidId: 1 to 128 of A-Za-z0-9._- . */
export function isValidSessionId(id) {
  return typeof id === 'string' && SESSION_ID.test(id);
}

/**
 * The engine's data directory rule (ConfigLoader.GetDataDir): a non-empty TOTAL_RECALL_HOME as it
 * is, else `.total-recall` under HOME when HOME is set at all (even empty), else under
 * USERPROFILE. null when nothing is set or the result is not an absolute path.
 */
export function resolveDataDir(env) {
  let dir = null;
  const trh = env.TOTAL_RECALL_HOME;
  if (typeof trh === 'string' && trh !== '') dir = trh;
  else if (typeof env.HOME === 'string') dir = path.join(env.HOME, '.total-recall');
  else if (typeof env.USERPROFILE === 'string') dir = path.join(env.USERPROFILE, '.total-recall');
  if (dir === null || dir === '' || !path.isAbsolute(dir)) return null;
  return dir;
}

function isAbsolutePathText(value) {
  return typeof value === 'string'
    && value.length > 0
    && value.length <= MAX_PATH_CHARS
    && !value.includes('\u0000')
    && path.isAbsolute(value);
}

/** A valid host pid argument as a number, else null. */
export function parsePid(arg) {
  if (typeof arg !== 'string' || !PID_ARG.test(arg)) return null;
  const n = Number(arg);
  return n >= 1 && n <= MAX_PID ? n : null;
}

/** The marker object for a hook payload, or null when the payload may not be recorded. `now`: Date or ms. */
export function buildMarker(payload, pid, now) {
  if (typeof payload !== 'object' || payload === null || Array.isArray(payload)) return null;
  if (!Number.isInteger(pid) || pid < 1 || pid > MAX_PID) return null;
  if (!isValidSessionId(payload.session_id)) return null;
  if (!isAbsolutePathText(payload.transcript_path)) return null;
  if (!isAbsolutePathText(payload.cwd)) return null;
  const at = new Date(now instanceof Date ? now.getTime() : now);
  if (Number.isNaN(at.getTime())) return null;
  return {
    v: 1,
    host: HOST,
    host_pid: pid,
    session_id: payload.session_id,
    transcript_path: payload.transcript_path,
    cwd: payload.cwd,
    at: at.toISOString(),
  };
}

/** Writes the marker atomically (temp file mode 0600 in live/, then rename). Returns the file name. Throws on failure. */
export function writeMarker({ dataDir, marker }) {
  const liveDir = path.join(dataDir, 'live');
  fs.mkdirSync(liveDir, { recursive: true, mode: 0o700 });
  const name = `${HOST}-${marker.host_pid}.json`;
  const tmp = path.join(liveDir, `${name}.${process.pid}.${crypto.randomBytes(4).toString('hex')}.tmp`);
  try {
    fs.writeFileSync(tmp, JSON.stringify(marker), { flag: 'wx', mode: 0o600 });
    fs.renameSync(tmp, path.join(liveDir, name));
  } catch (e) {
    try { fs.unlinkSync(tmp); } catch { /* not created, or already moved */ }
    throw e;
  }
  return name;
}

function pidIsDead(pid) {
  try {
    process.kill(pid, 0);
    return false;
  } catch (e) {
    return e?.code === 'ESRCH'; // EPERM (or anything else) counts as alive
  }
}

/**
 * Deletes stale entries of `liveDir`, looking at no more than 200 names: a `claude-code-<pid>.json`
 * whose pid is gone or that is more than 30 days old, and a `claude-code-*.tmp` of the same family
 * more than an hour old. Never `enabled`, `selfName` or any other name. Returns the number removed.
 */
export function prune({ liveDir, now, selfName }) {
  let removed = 0;
  let names;
  try {
    names = fs.readdirSync(liveDir).slice(0, PRUNE_MAX_NAMES);
  } catch {
    return 0;
  }
  for (const name of names) {
    try {
      if (name === 'enabled' || name === selfName) continue;
      const marker = MARKER_NAME.exec(name);
      const isTmp = !marker && TMP_NAME.test(name);
      if (!marker && !isTmp) continue;
      const file = path.join(liveDir, name);
      const st = fs.lstatSync(file);
      if (!st.isFile()) continue;
      const age = now - st.mtimeMs;
      const stale = isTmp
        ? age > TMP_MAX_AGE_MS
        : age > MARKER_MAX_AGE_MS || pidIsDead(Number(marker[1]));
      if (!stale) continue;
      fs.unlinkSync(file);
      removed++;
    } catch {
      // one entry failing must not stop the rest
    }
  }
  return removed;
}

/**
 * All of the stream within `capMs` and `capBytes`; null when it was larger than the cap. Never
 * waits on a TTY. The stream is destroyed afterwards so an open pipe cannot keep node alive. Never rejects.
 */
export function readStdin(stream, capMs, capBytes) {
  return new Promise((resolve) => {
    if (!stream || stream.isTTY) { resolve(''); return; }
    const chunks = [];
    let size = 0;
    let overflow = false;
    let done = false;
    let timer;
    const finish = () => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { stream.destroy(); } catch { /* already closed */ }
      resolve(overflow ? null : Buffer.concat(chunks).toString('utf8'));
    };
    try {
      timer = setTimeout(finish, capMs);
      stream.on('data', (c) => {
        const buf = Buffer.isBuffer(c) ? c : Buffer.from(String(c));
        size += buf.length;
        if (size > capBytes) { overflow = true; finish(); return; }
        chunks.push(buf);
      });
      stream.on('end', finish);
      stream.on('error', finish);
      stream.on('close', finish);
    } catch {
      finish();
    }
  });
}

/** The whole job; resolves when done and never rejects. All dependencies are injectable. */
export async function run({ argv, env, stdin, now = Date.now } = {}) {
  try {
    const pid = parsePid(argv?.[0]);
    if (pid === null) return;
    const dataDir = resolveDataDir(env ?? {});
    if (dataDir === null) return;
    const text = await readStdin(stdin, STDIN_CAP_MS, STDIN_CAP_BYTES);
    if (text === null || text === '') return;
    let payload;
    try { payload = JSON.parse(text); } catch { return; }
    const t = now();
    const marker = buildMarker(payload, pid, t);
    if (marker === null) return;
    const selfName = writeMarker({ dataDir, marker });
    if (payload.hook_event_name === 'SessionStart') {
      prune({ liveDir: path.join(dataDir, 'live'), now: t, selfName });
    }
  } catch {
    // never fail a hook
  }
}

// Run as a command, not when imported (importing in a test must never read stdin). Both paths are
// resolved, so a symlinked checkout still runs.
function isMainModule() {
  try {
    const self = fs.realpathSync(fileURLToPath(import.meta.url));
    return !!process.argv[1] && fs.realpathSync(process.argv[1]) === self;
  } catch {
    return false;
  }
}

if (isMainModule()) {
  setTimeout(() => process.exit(0), DEADLINE_MS).unref();
  run({ argv: process.argv.slice(2), env: process.env, stdin: process.stdin })
    .finally(() => process.exit(0));
}
