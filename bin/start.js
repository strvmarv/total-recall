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
import process from 'node:process';

import { ensureBinary } from '../scripts/fetch-binary.js';

const result = await ensureBinary({ logPrefix: '[total-recall]' });

if (!result.ok) {
  process.stderr.write(`[total-recall] ${result.error}\n`);
  if (result.url) {
    process.stderr.write(`[total-recall]   url: ${result.url}\n`);
  }
  process.stderr.write(
    '[total-recall] File an issue: https://github.com/strvmarv/total-recall/issues\n'
  );
  process.exit(1);
}

const binaryPath = result.path;

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
