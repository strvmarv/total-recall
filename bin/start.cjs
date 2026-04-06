'use strict';
// Bootstrap wrapper for the total-recall MCP server.
// Runs as plain CJS (no imports) so it works even when node_modules is absent.
// Detects missing/incomplete node_modules and self-heals via npm install,
// then re-execs dist/index.js with inherited stdio as a transparent passthrough.

const { existsSync } = require('fs');
const { join, dirname } = require('path');
const { spawnSync } = require('child_process');

const root = join(__dirname, '..');

// Use better-sqlite3/lib as the canary — it only exists after a successful install.
const canary = join(root, 'node_modules', 'better-sqlite3', 'lib');

if (!existsSync(canary)) {
  process.stderr.write('[total-recall] node_modules missing or incomplete — running npm install...\n');

  const npm = findNpm();
  const install = spawnSync(npm, ['install', '--production', '--prefer-offline'], {
    cwd: root,
    stdio: 'inherit',
    shell: true, // required on Windows to execute .cmd files; harmless on macOS/Linux
  });

  if (install.status !== 0) {
    process.stderr.write('[total-recall] npm install failed — see output above for details\n');
    process.exit(1);
  }
}

// Re-exec dist/index.js as a subprocess with inherited stdio.
// spawnSync blocks until the server exits, transparently passing all I/O through.
const server = spawnSync(process.execPath, [join(root, 'dist', 'index.js')], {
  stdio: 'inherit',
  env: process.env,
});

process.exit(server.status ?? 1);

// ---------------------------------------------------------------------------

function findNpm() {
  const nodeDir = dirname(process.execPath);

  // npm installs alongside node. On Windows it's npm.cmd; on macOS/Linux it's npm.
  const candidates = process.platform === 'win32'
    ? [join(nodeDir, 'npm.cmd'), join(nodeDir, 'npm')]
    : [join(nodeDir, 'npm')];

  for (const candidate of candidates) {
    if (existsSync(candidate)) return candidate;
  }

  // Last resort: hope npm is somewhere on PATH.
  return 'npm';
}
