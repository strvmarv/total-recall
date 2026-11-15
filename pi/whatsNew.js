// pi/whatsNew.js — shared helpers for the direct delivery of the "what's new" notice.
//
// A host that delivers the notice itself (pi, the Claude Code hook) first writes a
// `direct-<host>.json` stamp so the engine's session_start does not announce the same
// release a second time, then asks the engine's `whats-new` command for the line.
// Nothing here may throw, print into the host, or block past its timeout.

import { spawn as realSpawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const HOST_ID = /^[a-z][a-z-]{0,20}$/;
const FORMATS = ['text', 'json', 'claude-code'];
const DEFAULT_TIMEOUT_MS = 3000;
const MAX_STDOUT_CHARS = 8 * 1024;

/**
 * Record that this host delivers the notice directly:
 * `<dataDir>/whats-new/direct-<host>.json` = `{"at":"<ISO-8601 UTC>"}`, written through a
 * unique temporary file and a rename so a reader never sees a partial file. Returns true
 * on success; false (and nothing written) for an unusable data directory or host id, or
 * on any error. Never throws.
 */
export function writeDirectStamp({ dataDir, host, now = () => new Date(), fsApi = fs }) {
  if (typeof dataDir !== 'string' || dataDir === '' || !path.isAbsolute(dataDir)) return false;
  if (typeof host !== 'string' || !HOST_ID.test(host)) return false;
  let tmp;
  try {
    const dir = path.join(dataDir, 'whats-new');
    fsApi.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, `direct-${host}.json`);
    tmp = `${file}.${crypto.randomBytes(6).toString('hex')}.tmp`;
    fsApi.writeFileSync(tmp, JSON.stringify({ at: now().toISOString() }));
    fsApi.renameSync(tmp, file);
    return true;
  } catch {
    return false;
  } finally {
    if (tmp !== undefined) {
      try { fsApi.rmSync(tmp, { force: true }); } catch { /* best effort */ }
    }
  }
}

/**
 * Run `<binaryPath> whats-new --host <host> --format <format> [--no-mark]` and resolve with
 * its trimmed stdout, or undefined when the exit code is not 0, the output is empty, the
 * spawn fails or `timeoutMs` passes. Never rejects. stdout is capped at 8 KiB; the child is
 * killed at the timeout. `env` is layered over the process environment (an `undefined` value
 * removes the variable). The engine's output is untrusted text: the caller validates it.
 */
export function runWhatsNew({
  binaryPath,
  host,
  format,
  env = {},
  timeoutMs = DEFAULT_TIMEOUT_MS,
  noMark = false,
  spawn = realSpawn,
}) {
  return new Promise((resolve) => {
    let settled = false;
    let timer;
    let child;
    const finish = (value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(value);
    };
    try {
      if (typeof host !== 'string' || !HOST_ID.test(host) || !FORMATS.includes(format)) {
        finish(undefined);
        return;
      }
      const args = ['whats-new', '--host', host, '--format', format];
      if (noMark) args.push('--no-mark');
      const childEnv = { ...process.env, ...env };
      for (const key of Object.keys(childEnv)) {
        if (childEnv[key] === undefined) delete childEnv[key];
      }
      child = spawn(binaryPath, args, {
        stdio: ['ignore', 'pipe', 'ignore'],
        windowsHide: true,
        env: childEnv,
      });
      child.stdout.setEncoding('utf8');
      let stdout = '';
      child.stdout.on('data', (chunk) => {
        if (stdout.length < MAX_STDOUT_CHARS) stdout = (stdout + chunk).slice(0, MAX_STDOUT_CHARS);
      });
      child.stdout.on('error', () => undefined);
      child.on('error', () => finish(undefined));
      child.on('close', (code) => {
        const text = stdout.trim();
        finish(code === 0 && text !== '' ? text : undefined);
      });
      timer = setTimeout(() => {
        try { child.kill('SIGKILL'); } catch { /* already gone */ }
        try { child.stdout.destroy(); } catch { /* already closed */ }
        finish(undefined);
      }, timeoutMs);
    } catch {
      finish(undefined);
    }
  });
}
