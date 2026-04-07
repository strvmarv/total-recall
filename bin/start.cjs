'use strict';
// Bootstrap wrapper for the total-recall MCP server.
//
// Why this exists: .mcp.json launches us under `node` (guaranteed present
// wherever Claude Code runs). Since 0.6.8 the server uses `bun:sqlite`, which
// only resolves under the Bun runtime, so this script locates bun and
// re-execs dist/index.js under it.
//
// Lookup order:
//   1. Bundled bun at ~/.total-recall/bun/<BUN_VERSION>/bun[.exe]
//      (downloaded by scripts/postinstall.js during `npm install`)
//   2. System bun on PATH
//   3. Hard fail — node cannot run dist/index.js (bun:sqlite import errors)
//
// Kept as plain CJS (no imports) so it works even when node_modules is absent.

const { existsSync } = require('fs');
const { join } = require('path');
const { spawnSync } = require('child_process');
const os = require('os');

// Keep in sync with scripts/postinstall.js BUN_VERSION.
const BUN_VERSION = '1.2.10';

const root = join(__dirname, '..');
const entry = join(root, 'dist', 'index.js');

if (!existsSync(entry)) {
  process.stderr.write(
    '[total-recall] dist/index.js not found. Run `npm install` (marketplace) or `npm run build` (git checkout).\n'
  );
  process.exit(1);
}

const bun = findBun();
if (!bun) {
  process.stderr.write(
    '[total-recall] bun runtime not found.\n' +
      `  Expected bundled bun at ~/.total-recall/bun/${BUN_VERSION}/bun (installed by \`npm install\`).\n` +
      '  Fix: run `npm install` inside the plugin directory, or install bun manually (https://bun.sh/install).\n'
  );
  process.exit(1);
}

// Re-exec dist/index.js under bun with inherited stdio so the MCP JSON-RPC
// channel passes through transparently.
const server = spawnSync(bun, [entry], {
  stdio: 'inherit',
  env: process.env,
});

process.exit(server.status ?? 1);

// ---------------------------------------------------------------------------

function findBun() {
  const isWin = process.platform === 'win32';
  const ext = isWin ? '.exe' : '';

  // 1. Bundled bun (preferred — version-pinned, matches what postinstall downloaded)
  const bundled = join(os.homedir(), '.total-recall', 'bun', BUN_VERSION, `bun${ext}`);
  if (existsSync(bundled)) return bundled;

  // 2. System bun on PATH — let the OS resolver find it
  const probe = spawnSync(isWin ? 'where' : 'which', ['bun'], { encoding: 'utf8' });
  if (probe.status === 0) {
    const first = String(probe.stdout || '').split(/\r?\n/).find(Boolean);
    if (first && existsSync(first)) return first;
  }

  return null;
}
