#!/usr/bin/env node
// Installs total-recall MCP config and steering into ~/.kiro/ if Kiro is detected.
//
// Writes:
//   ~/.kiro/settings/mcp.json  — MCP server registration (merged, non-clobbering)
//   ~/.kiro/steering/total-recall.md — always-included steering doc
//
// Hooks are NOT copied — they ship in the repo's .kiro/hooks/ and activate
// per-workspace (Kiro's documented activation model).
//
// Non-fatal: always resolves. If Kiro isn't present, silently skips.
// Idempotent: re-running overwrites steering with the latest version.

import { cpSync, mkdirSync, existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mergeJsonConfig } from './lib/merge-json-config.js';
import { isRepoSelf, ROOT } from './lib/is-repo-self.js';

function kiroHome() {
  const env = process.env.KIRO_HOME;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  const kiroDir = join(home, '.kiro');
  if (existsSync(kiroDir)) return kiroDir;

  return null;
}

export async function installKiroHooks() {
  try {
    const khome = kiroHome();
    if (!khome) return;

    if (!isRepoSelf()) {
      // --- MCP config ---
      const mcpConfigPath = join(khome, 'settings', 'mcp.json');
      mergeJsonConfig(mcpConfigPath, 'mcpServers', {
        command: 'npx',
        args: ['--yes', '@strvmarv/total-recall'],
        cwd: '${workspaceFolder}'
      }, 'Kiro');

      // --- Steering ---
      const steeringSrc = join(ROOT, '.kiro', 'steering', 'total-recall.md');
      const steeringDest = join(khome, 'steering');

      if (existsSync(steeringSrc)) {
        mkdirSync(steeringDest, { recursive: true });
        cpSync(steeringSrc, join(steeringDest, 'total-recall.md'), { force: true });
        process.stderr.write(`[total-recall:postinstall] Kiro steering installed to ${steeringDest}\n`);
      }
    }
  } catch (e) {
    process.stderr.write(`[total-recall:postinstall] Kiro hook install failed (non-fatal): ${e.message}\n`);
  }
}