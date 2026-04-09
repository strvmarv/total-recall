#!/usr/bin/env node
// Launcher for the total-recall MCP server (.NET AOT build).
//
// Responsibilities:
//   1. Detect the host platform/arch and map to a .NET RID.
//   2. Resolve the matching prebuilt binary shipped in binaries/<rid>/.
//   3. Exec it with full stdio passthrough so the MCP JSON-RPC channel
//      (stdin/stdout) remains a raw byte stream.
//   4. Forward SIGINT/SIGTERM and propagate the child's exit code.
//
// Zero dependencies — Node built-ins only. This file is pointed at by the
// package.json "bin" field (Task 6.6) and runs directly from
// node_modules/.bin/total-recall after `npm install @strvmarv/total-recall`.

import path from 'node:path';
import fs from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import process from 'node:process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const { platform, arch } = process;

function detectRid(platform, arch) {
  if (platform === 'linux' && arch === 'x64') return 'linux-x64';
  if (platform === 'linux' && arch === 'arm64') return 'linux-arm64';
  if (platform === 'darwin' && arch === 'arm64') return 'osx-arm64';
  if (platform === 'win32' && arch === 'x64') return 'win-x64';
  return null;
}

const rid = detectRid(platform, arch);
if (!rid) {
  const isIntelMac = platform === 'darwin' && arch === 'x64';
  process.stderr.write(
    `[total-recall] Unsupported platform: ${platform}/${arch}\n` +
      '  Supported: linux-x64, linux-arm64, osx-arm64, win-x64\n' +
      (isIntelMac
        ? '  Note: Intel Macs (darwin-x64) are not shipped in this release.\n' +
          '        All modern Apple hardware is Apple Silicon (arm64) since\n' +
          '        Nov 2020. If you need Intel Mac support, file an issue at\n'
        : '  Please file an issue at\n') +
      '  https://github.com/strvmarv/total-recall/issues\n'
  );
  process.exit(1);
}

const binaryName = platform === 'win32' ? 'total-recall.exe' : 'total-recall';
const binaryPath = path.join(__dirname, '..', 'binaries', rid, binaryName);

if (!fs.existsSync(binaryPath)) {
  process.stderr.write(
    `[total-recall] Prebuilt binary not found for ${rid}.\n` +
      `  Expected: ${binaryPath}\n` +
      '  This usually means the npm package is missing a prebuilt for this\n' +
      '  platform/architecture. Please file an issue at\n' +
      '  https://github.com/strvmarv/total-recall/issues or build from source\n' +
      '  (see CONTRIBUTING.md).\n'
  );
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
