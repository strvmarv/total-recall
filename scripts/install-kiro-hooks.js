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

import { cpSync, mkdirSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, '..');

function kiroHome() {
  // Respect explicit override (useful for testing)
  const env = process.env.KIRO_HOME;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  // ~/.kiro is the standard location on all platforms
  const kiroDir = join(home, '.kiro');
  if (existsSync(kiroDir)) return kiroDir;

  return null;
}

function isRepoSelf() {
  // When the install script runs inside the total-recall repo itself
  // (dev install), skip the steering copy to avoid double-fire with the
  // workspace-level .kiro/steering/. End users have ROOT inside node_modules.
  try {
    const pkg = JSON.parse(readFileSync(join(ROOT, 'package.json'), 'utf8'));
    return pkg.name === '@strvmarv/total-recall';
  } catch {
    return false;
  }
}

function mergeMcpConfig(configPath) {
  const entry = {
    command: 'npx',
    args: ['--yes', '@strvmarv/total-recall'],
    cwd: '${workspaceFolder}'
  };

  let config = { mcpServers: {} };
  if (existsSync(configPath)) {
    try {
      config = JSON.parse(readFileSync(configPath, 'utf8'));
      if (!config.mcpServers) config.mcpServers = {};
    } catch {
      // Corrupt or empty file — start fresh
      config = { mcpServers: {} };
    }
  }

  const existing = config.mcpServers['total-recall'];
  if (existing) {
    // Preserve user config: only write if the key is missing or malformed
    if (existing.command === 'npx' &&
        Array.isArray(existing.args) &&
        existing.args.includes('@strvmarv/total-recall')) {
      // Already configured correctly — leave it alone
      return;
    }
    // User has a custom config (different command/fork) — preserve it
    process.stderr.write('[total-recall:postinstall] Kiro MCP config already has a custom total-recall entry — preserving user config.\n');
    return;
  }

  // Key is missing — add it
  config.mcpServers['total-recall'] = entry;
  mkdirSync(dirname(configPath), { recursive: true });
  writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
  process.stderr.write(`[total-recall:postinstall] Kiro MCP config written to ${configPath}\n`);
}

export async function installKiroHooks() {
  try {
    const khome = kiroHome();
    if (!khome) {
      // Kiro not detected — not an error, just skip
      return;
    }

    // --- MCP config ---
    // Write/merge ~/.kiro/settings/mcp.json with the total-recall server entry.
    // Skip when ROOT is the repo itself (dev install) to avoid same-name MCP
    // server collision with the workspace-level .kiro/settings/mcp.json.
    if (!isRepoSelf()) {
      const mcpConfigPath = join(khome, 'settings', 'mcp.json');
      mergeMcpConfig(mcpConfigPath);
    }

    // --- Steering ---
    // Copy the always-included steering file to ~/.kiro/steering/ so the
    // agent always receives the session_start instruction in every workspace.
    // Skip when ROOT is the repo itself (dev install) to avoid double-fire
    // with the workspace-level .kiro/steering/.
    if (!isRepoSelf()) {
      const steeringSrc = join(ROOT, '.kiro', 'steering', 'total-recall.md');
      const steeringDest = join(khome, 'steering');

      if (existsSync(steeringSrc)) {
        mkdirSync(steeringDest, { recursive: true });
        cpSync(steeringSrc, join(steeringDest, 'total-recall.md'), { force: true });
        process.stderr.write(`[total-recall:postinstall] Kiro steering installed to ${steeringDest}\n`);
      }
    }
  } catch (e) {
    // Non-fatal: log and resolve, never reject
    process.stderr.write(`[total-recall:postinstall] Kiro hook install failed (non-fatal): ${e.message}\n`);
  }
}