// pi/floor.js — run the pinned-directive floor for the pi extension.
//
// The floor is `total-recall pinned-floor --host claude-code`, the same backend the
// Claude Code UserPromptSubmit hook uses. The extension spawns the engine binary
// DIRECTLY (not `node bin/start.js`): start.js/direct.js would provision in-process
// with no lock, and its child engine would survive a timeout kill. So the floor runs
// only when the engine is already provisioned; until then it stays silent and the MCP
// shim provisions as usual. Nothing here may throw, delay, or print into pi.

import { spawn as realSpawn } from 'node:child_process';
import { existsSync as realExistsSync } from 'node:fs';
import path from 'node:path';
import { detectRid, getBinaryPath, payloadIntact } from '../scripts/fetch-binary.js';

const DEFAULT_ARGS = ['pinned-floor', '--host', 'claude-code'];
const DEFAULT_TIMEOUT_MS = 10_000;

/**
 * Absolute path of the engine binary when it is usable, else null.
 * Gate: supported platform, binary present, payload intact (a truncated model is
 * caught when models/registry.json is present). Deliberately NOT gated on
 * `.verified.json`: only the shim's checksum-verified download writes it, never
 * postinstall or an npm tarball that ships the binary.
 */
export function resolveEngine(deps = {}) {
  const d = {
    platform: process.platform,
    arch: process.arch,
    detectRid,
    getBinaryPath,
    existsSync: realExistsSync,
    payloadIntact,
    ...deps,
  };
  try {
    const rid = d.detectRid(d.platform, d.arch);
    if (!rid) return null;
    const binaryPath = d.getBinaryPath(rid);
    if (!d.existsSync(binaryPath)) return null;
    if (!d.payloadIntact(path.dirname(binaryPath)).ok) return null;
    return binaryPath;
  } catch {
    return null;
  }
}

/**
 * `hookSpecificOutput.additionalContext` from the last line of stdout that starts
 * with `{`, or undefined for anything else (empty, non-JSON, non-object, missing,
 * blank, non-string).
 */
export function parseFloorOutput(stdout) {
  const text = String(stdout ?? '').trim();
  if (text === '') return undefined;
  const lastJson = text.split(/\r?\n/).filter((l) => l.trim().startsWith('{')).pop();
  if (lastJson === undefined) return undefined;
  let parsed;
  try {
    parsed = JSON.parse(lastJson);
  } catch {
    return undefined;
  }
  if (typeof parsed !== 'object' || parsed === null) return undefined;
  const ctx = parsed.hookSpecificOutput?.additionalContext;
  return typeof ctx === 'string' && ctx.trim() !== '' ? ctx : undefined;
}

/**
 * Spawn the engine's floor and resolve with its additionalContext, or undefined.
 * Never rejects. stdin is written and ended (the CLI reads it to EOF), stderr is
 * ignored, the child is killed at `timeoutMs`. `env` is layered over the process environment.
 */
export function runFloor({
  binaryPath,
  payload,
  args = DEFAULT_ARGS,
  env = {},
  timeoutMs = DEFAULT_TIMEOUT_MS,
  spawn = realSpawn,
}) {
  return new Promise((resolve) => {
    let settled = false;
    let timer;
    const finish = (value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(value);
    };
    try {
      const child = spawn(binaryPath, args, {
        stdio: ['pipe', 'pipe', 'ignore'],
        windowsHide: true,
        env: { ...process.env, ...env },
      });
      child.stdout.setEncoding('utf8');
      let stdout = '';
      child.stdout.on('data', (chunk) => { stdout += chunk; });
      child.on('error', () => finish(undefined));
      child.on('close', (code) => finish(code === 0 ? parseFloorOutput(stdout) : undefined));
      // The child may exit before reading stdin (EPIPE): that is its answer, not an error.
      child.stdin.on('error', () => undefined);
      child.stdin.end(JSON.stringify(payload));
      timer = setTimeout(() => {
        try { child.kill(); } catch { /* already gone */ }
        finish(undefined);
      }, timeoutMs);
    } catch {
      finish(undefined);
    }
  });
}
