#!/usr/bin/env node
// npm postinstall hook: downloads the correct platform binary into
// binaries/<rid>/ if it is not already present.
//
// This is the fast path for users who install via `npm install` (direct
// or via a plugin manager that runs lifecycle scripts). It runs once at
// install time, pulls the right binary from GitHub Releases, and writes
// it to the plugin directory.
//
// Failures are intentionally NON-FATAL (always exit 0). Reasons:
//   - `npm install --ignore-scripts` should not break the install
//   - Offline installs should still lay down the file tree
//   - Corporate networks that block github.com should not block npm install
//
// In all those cases, bin/start.js runs the same ensureBinary() as a
// fallback at first launch, so the failure here is recoverable later.

import { ensureBinary } from './fetch-binary.js';

const result = await ensureBinary({ logPrefix: '[total-recall:postinstall]' });

if (!result.ok) {
  process.stderr.write(`[total-recall:postinstall] warning: ${result.error}\n`);
  if (result.url) {
    process.stderr.write(`[total-recall:postinstall]   url: ${result.url}\n`);
  }
  process.stderr.write(
    '[total-recall:postinstall] The binary will be downloaded on first launch instead.\n'
  );
}

process.exit(0);
