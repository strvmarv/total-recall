#!/usr/bin/env node
// Launcher/shim for the total-recall MCP server.
//
// Unlike the old "present-check or background-download-and-exit" gate, this
// process IS the MCP server from the harness's point of view: it answers the
// initialize handshake instantly, serves a static tools/list, returns a
// structured not_ready result for tool calls while the engine provisions, then
// spawns and proxies to the engine binary. Once the shim is running the MCP
// connection never drops; all not-ready states are in-band tool results. (The
// only hard exit is before any connection exists — an unsupported platform.)
// See bin/shim/* and
// docs/superpowers/specs/2026-06-14-mcp-bootstrap-shim-design.md.

import fs from 'node:fs';
import process from 'node:process';
import { detectRid } from '../scripts/fetch-binary.js';
import { runShim } from './shim/orchestrator.js';
import { ensureProvisioned, makeProductionDeps } from './shim/provisioner.js';

const rid = detectRid(process.platform, process.arch);
if (!rid) {
  process.stderr.write(`[total-recall] unsupported platform: ${process.platform}/${process.arch}\n`);
  process.exit(1);
}

const catalogUrl = new URL('../catalog.json', import.meta.url);
const pkg = JSON.parse(fs.readFileSync(new URL('../package.json', import.meta.url), 'utf8'));
const catalog = JSON.parse(fs.readFileSync(catalogUrl, 'utf8'));

const deps = makeProductionDeps();
const engineArgs = process.argv.slice(2); // pass through `serve` etc. (default = serve)

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
