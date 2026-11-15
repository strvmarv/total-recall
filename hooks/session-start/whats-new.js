// hooks/session-start/whats-new.js — the Claude Code SessionStart hook's "what's new" step.
//
// Run by hooks/session-start/run.sh (its Claude Code branch only) as `node whats-new.js`, with the
// hook payload on stdin. It prints either ONE JSON string literal (the notice line, escaped with
// JSON.stringify) or nothing; run.sh adds the literal to its own JSON as `systemMessage`.
//
//   1. write the `direct-claude-code` stamp first, on every source, so the engine's session_start
//      does not announce the same release a second time (direct wins);
//   2. for `compact` stop there;
//   3. otherwise run the provisioned engine directly (never bin/start.js, which would download
//      the engine inside a blocking hook) as `whats-new --host claude-code --format claude-code`.
//
// Contract: this process NEVER fails the hook. It exits 0 and any failure prints nothing, which
// leaves run.sh's output exactly what it was before. One overall deadline (2.5 s from the start of
// run) covers the stdin read and the engine; the engine is killed at the deadline.
//
// The helpers (pi/floor.js, pi/whatsNew.js, scripts/fetch-binary.js) are loaded by dynamic import
// inside a try, so a broken or missing file cannot raise a link-time error here (pattern: codex/hook.js).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const HOST = 'claude-code';
export const DEADLINE_MS = 2500;
export const STDIN_CAP_MS = 500;

// The shape check lives in pi/whatsNew.js (one function for both hosts). It is loaded by dynamic
// import inside a try, like the other helpers: if the file is broken nothing is accepted.
let isDisplayableNotice = () => false;
try {
  ({ isDisplayableNotice } = await import(new URL('../../pi/whatsNew.js', import.meta.url).href));
} catch {
  // leave the rejecting default
}

/**
 * The `systemMessage` of the engine's `claude-code` output, or undefined for anything else:
 * invalid JSON, a non-object (an array too), `{}`, a message that is not a string, is empty, is
 * longer than 1000 characters or holds a control character or a line or paragraph separator.
 * The engine's output is untrusted text.
 */
export function acceptSystemMessage(text) {
  if (typeof text !== 'string') return undefined;
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return undefined;
  const line = parsed.systemMessage;
  return isDisplayableNotice(line) ? line : undefined;
}

/**
 * All of the stream, or what arrived within `capMs`; never waits on a TTY. The stream is destroyed
 * afterwards so an open pipe cannot keep node alive. Never rejects.
 */
export function readStdin(stream, capMs) {
  return new Promise((resolve) => {
    if (!stream || stream.isTTY) { resolve(''); return; }
    const chunks = [];
    let done = false;
    let timer;
    const finish = () => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { stream.destroy(); } catch { /* already closed */ }
      resolve(Buffer.concat(chunks).toString('utf8'));
    };
    try {
      timer = setTimeout(finish, capMs);
      stream.on('data', (c) => chunks.push(Buffer.isBuffer(c) ? c : Buffer.from(String(c))));
      stream.on('end', finish);
      stream.on('error', finish);
      stream.on('close', finish);
    } catch {
      finish();
    }
  });
}

/** `payload.source` when it is a string, else "startup" (empty, malformed or timed-out payloads too). */
export function sourceOf(payloadText) {
  try {
    const payload = JSON.parse(payloadText);
    if (typeof payload === 'object' && payload !== null && !Array.isArray(payload) && typeof payload.source === 'string') {
      return payload.source;
    }
  } catch {
    // fall through
  }
  return 'startup';
}

/**
 * The text to print on stdout: a one-line JSON string literal or "". Never rejects.
 * Every dependency is injectable; `now` returns milliseconds.
 */
export async function run(deps = {}) {
  try {
    const now = deps.now ?? Date.now;
    const start = now();
    const deadlineMs = deps.deadlineMs ?? DEADLINE_MS;
    const stdinCapMs = deps.stdinCapMs ?? STDIN_CAP_MS;
    const env = deps.env ?? process.env;

    const source = sourceOf(await readStdin(deps.stdin, stdinCapMs));

    // Direct wins: the stamp first, on every source, before any engine is looked for.
    // Without a data directory the engine would fall back to a relative one and record the version
    // there, and with no stamp the model would relay the line again: write nothing, run nothing.
    const dataDir = deps.resolveDataDir(env);
    if (typeof dataDir !== 'string' || dataDir === '') return '';
    deps.writeDirectStamp({ dataDir, host: HOST });

    if (source === 'compact') return '';

    const binaryPath = deps.resolveEngine();
    if (!binaryPath) return '';

    const timeoutMs = Math.max(1, Math.floor(start + deadlineMs - now()));
    let guard;
    const outcome = await Promise.race([
      deps.runWhatsNew({ binaryPath, host: HOST, format: 'claude-code', timeoutMs }),
      new Promise((resolve) => { guard = setTimeout(() => resolve(undefined), timeoutMs + 100); }),
    ]);
    clearTimeout(guard);

    const line = acceptSystemMessage(outcome);
    return line === undefined ? '' : JSON.stringify(line);
  } catch {
    return '';
  }
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

async function main() {
  const here = path.dirname(fileURLToPath(import.meta.url));
  const rootDir = path.resolve(here, '..', '..');
  const load = (...parts) => import(pathToFileURL(path.join(rootDir, ...parts)).href);
  let out = '';
  try {
    const [floor, whatsNew, fetchBinary] = await Promise.all([
      load('pi', 'floor.js'),
      load('pi', 'whatsNew.js'),
      load('scripts', 'fetch-binary.js'),
    ]);
    out = await run({
      stdin: process.stdin,
      env: process.env,
      now: Date.now,
      resolveEngine: floor.resolveEngine,
      resolveDataDir: fetchBinary.resolveDataDir,
      writeDirectStamp: whatsNew.writeDirectStamp,
      runWhatsNew: whatsNew.runWhatsNew,
    });
  } catch {
    out = '';
  }
  if (!out) process.exit(0);
  process.stdout.on('error', () => process.exit(0)); // host closed the pipe early: nothing to do
  process.stdout.write(out, () => process.exit(0));
}

if (isMain(import.meta.url)) {
  main().catch(() => process.exit(0));
}
