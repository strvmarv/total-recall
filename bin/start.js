#!/usr/bin/env node
// Launcher for the total-recall engine binary.
//
// Dispatches on the first argument:
//
//   serve (or no args)  -> MCP shim. This process IS the MCP server from the
//       harness's point of view: it answers the initialize handshake instantly,
//       serves a static tools/list, returns a structured not_ready result for
//       tool calls while the engine provisions, then spawns and proxies to the
//       engine binary. Once the shim is running the MCP connection never drops;
//       all not-ready states are in-band tool results. See bin/shim/* and
//       docs/superpowers/specs/2026-06-14-mcp-bootstrap-shim-design.md.
//
//   anything else (ui, reindex-embeddings, config, dump-catalog, …) -> direct
//       passthrough. These subcommands are NOT MCP servers — they emit
//       human-readable output on stdout — so they must bypass the shim and run
//       as a plain child process. Wrapping them in the MCP handshake made the
//       shim parse their stdout as JSON-RPC ("dropping unparseable line" flood)
//       and kill/restart them on the never-answered handshake. See bin/shim/direct.js.
//
// The only hard exit before any work is an unsupported platform.

import fs from 'node:fs';
import process from 'node:process';
import { detectRid } from '../scripts/fetch-binary.js';
import { runShim } from './shim/orchestrator.js';
import { ensureProvisioned, makeProductionDeps } from './shim/provisioner.js';
import { isServeInvocation, runDirect } from './shim/direct.js';

const rid = detectRid(process.platform, process.arch);
if (!rid) {
  process.stderr.write(`[total-recall] unsupported platform: ${process.platform}/${process.arch}\n`);
  process.exit(1);
}

const engineArgs = process.argv.slice(2);

if (isServeInvocation(engineArgs)) {
  // --- MCP serve path: wrap the engine in the stdio shim. ---
  const catalogUrl = new URL('../catalog.json', import.meta.url);
  const pkg = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
  const catalog = JSON.parse(fs.readFileSync(catalogUrl, 'utf8'));

  const deps = makeProductionDeps();

  const shim = runShim({
    stdin: process.stdin,
    stdout: process.stdout,
    catalog,
    serverName: 'total-recall',
    serverVersion: pkg.version,
    protocolVersion: '2024-11-05',
    provision: (onProgress) => ensureProvisioned({ ...deps, onProgress }),
    engineCommand: deps.binaryPath,
    engineArgs,
  });

  const shutdown = () => { try { shim.stop(); } catch {} process.exit(0); };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
} else {
  // --- Direct path: non-serve subcommand runs as a plain child process. ---
  runDirect({
    args: engineArgs,
    provision: () => ensureProvisioned(makeProductionDeps({
      // First-run provisioning progress goes to stderr (never stdout — some
      // subcommands reserve stdout for machine-readable output, e.g. dump-catalog).
      onProgress: ({ bytes, total, phase }) => {
        const pct = total ? Math.floor((bytes / total) * 100) : 0;
        process.stderr.write(`[total-recall] ${phase} engine ${pct}%\r`);
      },
    })),
  });
}
