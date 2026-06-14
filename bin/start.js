#!/usr/bin/env node
// Launcher for the total-recall MCP server (.NET AOT build).
//
// Responsibilities:
//   1. Ensure the correct .NET AOT binary is present under
//      binaries/<rid>/ — either because it was shipped in the npm
//      tarball / committed by an earlier postinstall run, or by
//      downloading it now from the matching GitHub Release.
//   2. Exec it with full stdio passthrough so the MCP JSON-RPC channel
//      (stdin/stdout) remains a raw byte stream.
//   3. Forward SIGINT/SIGTERM and propagate the child's exit code.
//
// The download-on-missing fallback exists because Claude Code's
// /plugin update flow clones the git repo (which does NOT contain
// binaries/) rather than installing the npm tarball. In that case
// ensureBinary() pulls the right binary from GitHub Releases on first
// launch. Users installing via npm get the same path as a no-op
// because scripts/postinstall.js already downloaded the binary.
//
// Zero runtime dependencies — Node built-ins only. Reuses the shared
// downloader at ../scripts/fetch-binary.js for RID detection and
// HTTP(S) fetch logic.

import { spawn } from 'node:child_process';
import fs from 'node:fs';
import process from 'node:process';

import { detectRid, getBinaryPath, provisionInBackground } from '../scripts/fetch-binary.js';

// Present-check fast path: never block the MCP startup handshake on the
// ~90 MB binary download. If the binary is missing (git/marketplace install
// path), kick a detached background downloader and exit fast with guidance;
// the next launch finds the binary and starts instantly.
const rid = detectRid(process.platform, process.arch);
const binaryPath = rid ? getBinaryPath(rid) : null;

if (!binaryPath || !fs.existsSync(binaryPath)) {
  const p = provisionInBackground({ logPrefix: '[total-recall]' });
  if (p.status === 'unsupported') {
    process.stderr.write(`[total-recall] unsupported platform: ${process.platform}/${process.arch}\n`);
  } else {
    process.stderr.write(
      '[total-recall] First-run setup: downloading the memory engine (~90 MB) in the background.\n' +
      '[total-recall] Memory becomes available once it finishes — reload the plugin or restart your\n' +
      '[total-recall] session in a minute. (One-time; only the git/marketplace install path needs it.)\n');
  }
  process.exit(1);
}

// Spawn with inherited stdio — MCP requires a raw, unbuffered byte channel.
const child = spawn(binaryPath, process.argv.slice(2), {
  stdio: 'inherit',
  env: process.env,
  windowsHide: false,
});

// Forward termination signals so the child can clean up its resources
// (SQLite WAL checkpoints, lockfiles, etc).
const forwardSignal = (signal) => {
  if (!child.killed) {
    try {
      child.kill(signal);
    } catch {
      // ignore — child may already be exiting
    }
  }
};
process.on('SIGINT', () => forwardSignal('SIGINT'));
process.on('SIGTERM', () => forwardSignal('SIGTERM'));

child.on('error', (err) => {
  if (err && err.code === 'ENOENT') {
    process.stderr.write(
      `[total-recall] Failed to exec binary (ENOENT): ${binaryPath}\n` +
        '  The file exists but could not be executed. On Unix, check the\n' +
        '  executable bit (chmod +x). On macOS, check Gatekeeper quarantine.\n'
    );
  } else if (err && err.code === 'EACCES') {
    process.stderr.write(
      `[total-recall] Permission denied executing binary: ${binaryPath}\n` +
        '  Fix: chmod +x "' + binaryPath + '"\n'
    );
  } else {
    process.stderr.write(
      `[total-recall] Failed to spawn binary: ${err && err.message ? err.message : String(err)}\n`
    );
  }
  process.exit(1);
});

child.on('exit', (code, signal) => {
  if (signal) {
    // Re-raise the signal on ourselves so the parent shell sees the right
    // termination cause (128 + signal number for bash-style exit codes).
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code ?? 1);
});
