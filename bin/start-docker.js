#!/usr/bin/env node
// bin/start-docker.js
//
// Docker launcher for total-recall. Runs the engine inside a container so hosts
// that cannot execute the unsigned NativeAOT binary (corporate Macs with
// signed-binary / Gatekeeper / MDM policy) can still use total-recall. The image
// is pulled at install time (scripts/postinstall.js -> scripts/pull-docker-image.js),
// so this wrapper is a thin `docker run` launcher — no shim reimplementation.
//
// Dispatch mirrors bin/start.js:
//   serve (or no args) -> docker run -i --rm --user <uid>:<gid> -v <data>:/data <image> serve
//   ui [--port N]      -> docker run --rm --user <uid>:<gid> -p N:N -v <data>:/data <image> ui --host 0.0.0.0 --no-open --port N
//   anything else      -> docker run --rm --user <uid>:<gid> -v <data>:/data <image> <args>
//
// The `ui` path injects `--host 0.0.0.0` (the engine binds 127.0.0.1 by default,
// which Docker's -p DNAT cannot reach) and `--no-open` (no browser in the
// container). See docs/superpowers/specs/2026-08-17-docker-container-option-design.md.

import { spawn as realSpawn, spawnSync as realSpawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveDockerImage } from '../scripts/docker-image.js';

export const DEFAULT_UI_PORT = 5577;

export function resolveDataDir(env = process.env) {
  const explicit = env.TOTAL_RECALL_HOME;
  if (explicit && explicit.length > 0) return path.resolve(explicit);
  const home = env.HOME || env.USERPROFILE || os.homedir();
  return path.join(home, '.total-recall');
}

// Returns the port number for a valid fixed port (1-65535), or null for an
// invalid/unsupported value (non-numeric, out of range, 0 = ephemeral, or a
// missing value after --port/-p).
export function parsePort(args) {
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--port' || args[i] === '-p') {
      if (i + 1 >= args.length) return null; // flag with no value
      const n = Number(args[i + 1]);
      if (Number.isInteger(n) && n >= 1 && n <= 65535) return n;
      return null;
    }
  }
  return DEFAULT_UI_PORT;
}

// Remove the ui flags the wrapper normalizes/injects itself, so the user's
// remaining flags (--token, --smoke, ...) are forwarded verbatim.
function stripUiArgs(args) {
  const out = [];
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === '--port' || a === '-p' || a === '--host') { i++; continue; } // flag + value
    if (a === '--no-open') continue;
    out.push(a);
  }
  return out;
}

export function buildDockerArgs({ args, dataDir, image, uid, gid }) {
  const flags = ['run', '--rm'];
  const first = args[0];
  if (first === undefined || first === 'serve') flags.push('-i');
  if (uid != null && gid != null) flags.push('--user', `${uid}:${gid}`);
  flags.push('-v', `${dataDir}:/data`);

  if (first === undefined || first === 'serve') {
    flags.push(image, 'serve');
  } else if (first === 'ui') {
    const port = parsePort(args);
    if (port == null) {
      return { ok: false, error: '--port 0 (ephemeral) and invalid ports are not supported in Docker mode; use a fixed port 1-65535.' };
    }
    flags.push('-p', `${port}:${port}`);
    const forwarded = stripUiArgs(args.slice(1));
    flags.push(image, 'ui', ...forwarded, '--host', '0.0.0.0', '--no-open', '--port', String(port));
  } else {
    flags.push(image, ...args);
  }
  return { ok: true, args: flags };
}

export async function runDocker({
  args,
  env = process.env,
  spawn = realSpawn,
  spawnSyncFn = realSpawnSync,
  stderr = process.stderr,
  onExit = (code) => process.exit(code),
  proc = process,
  mkdir = fs.mkdirSync,
} = {}) {
  // TOTAL_RECALL_DB_PATH is not supported in Docker mode: the container resolves
  // ~/ against its own filesystem, so the user's real DB would be silently ignored
  // and a fresh empty DB created. Refuse loudly instead.
  if (env.TOTAL_RECALL_DB_PATH) {
    stderr.write(
      '[total-recall:docker] TOTAL_RECALL_DB_PATH is not supported in Docker mode — ' +
      'the container resolves ~/ against its own filesystem. Unset it (or mount the DB ' +
      'explicitly) and retry.\n'
    );
    onExit(1);
    return undefined;
  }

  const dataDir = resolveDataDir(env);
  const image = resolveDockerImage(env);

  // Create the host data dir before docker run: a nonexistent bind-mount source is
  // created by Docker as root, which the non-root container then cannot write to.
  try {
    mkdir(dataDir, { recursive: true });
  } catch (e) {
    stderr.write(`[total-recall:docker] could not create data dir ${dataDir}: ${e.message}\n`);
    onExit(1);
    return undefined;
  }

  // Pre-flight (serve path only): fail fast with a clear message instead of
  // letting `docker run` pull the image inline (which would block the MCP
  // initialize handshake past the harness's hard startup timeout). One-shot
  // subcommands and `ui` are not on the handshake path, so they let `docker run`
  // pull inline as normal.
  const first = args[0];
  const isServe = first === undefined || first === 'serve';
  if (isServe) {
    const inspect = spawnSyncFn('docker', ['image', 'inspect', image], { stdio: ['ignore', 'ignore', 'pipe'], encoding: 'utf8' });
    if (inspect.error) {
      stderr.write(
        '[total-recall:docker] docker not found on PATH. install Docker Desktop ' +
        '(https://www.docker.com/products/docker-desktop/) to use the containerized total-recall.\n'
      );
      onExit(1);
      return undefined;
    }
    if (inspect.status !== 0) {
      const errText = (inspect.stderr || '').toString();
      if (/cannot connect to the docker daemon|is the docker daemon running/i.test(errText)) {
        stderr.write('[total-recall:docker] the Docker daemon is not running. Start Docker Desktop, then retry.\n');
      } else {
        stderr.write(`[total-recall:docker] image ${image} is not present. Run \`docker pull ${image}\` first, then retry.\n`);
      }
      onExit(1);
      return undefined;
    }
  }

  // --user matches the host uid:gid so bind-mount writes are owned by the host user,
  // not root. Only on POSIX (process.getuid is undefined on win32).
  const uid = typeof proc.getuid === 'function' ? proc.getuid() : null;
  const gid = typeof proc.getgid === 'function' ? proc.getgid() : null;

  const built = buildDockerArgs({ args, dataDir, image, uid, gid });
  if (!built.ok) {
    stderr.write(`[total-recall:docker] ${built.error}\n`);
    onExit(1);
    return undefined;
  }

  const child = spawn('docker', built.args, { stdio: ['inherit', 'inherit', 'pipe'] });

  // Capture the child's stderr so we can add a targeted hint on mount failures,
  // while still forwarding it so diagnostics are not swallowed.
  let childStderr = '';
  if (child.stderr && typeof child.stderr.on === 'function') {
    child.stderr.on('data', (chunk) => {
      childStderr += chunk.toString();
      stderr.write(chunk);
    });
  }

  // Forward terminal signals to the docker child so a harness shutdown does not
  // orphan the container (mirrors bin/shim/direct.js).
  for (const sig of ['SIGINT', 'SIGTERM']) {
    proc.on(sig, () => { try { child.kill(sig); } catch { /* best-effort */ } });
  }

  // Mirror the child's fate exactly once (a failed spawn fires 'error' then
  // 'exit' with (null, null)).
  let settled = false;
  const finish = (code, signal) => { if (settled) return; settled = true; onExit(code, signal); };
  child.on('error', (err) => {
    stderr.write(`[total-recall:docker] failed to start docker: ${err.message}\n`);
    finish(1);
  });
  child.on('exit', (code, signal) => {
    const exitCode = code == null ? (signal ? 1 : 0) : code;
    if (exitCode !== 0 && /file sharing|Mounts denied|not shared|invalid mount/i.test(childStderr)) {
      stderr.write(
        '[total-recall:docker] the data dir may be outside Docker Desktop\'s shared roots — ' +
        'add it under Docker Desktop > Settings > Resources > File Sharing.\n'
      );
    }
    finish(exitCode, signal);
  });

  return child;
}

// Run when invoked directly (the MCP config points at this file).
const isMain = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  runDocker({ args: process.argv.slice(2) });
}
