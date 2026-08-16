#!/usr/bin/env node
// Installs total-recall MCP config into ~/.vscode/cline_mcp_settings.json
// if Cline (VS Code extension) is detected.
//
// Non-fatal: always resolves. If Cline isn't present, silently skips.

import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mergeJsonConfig } from './lib/merge-json-config.js';

function clineHome() {
  const env = process.env.CLINE_HOME;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  const vscodeDir = join(home, '.vscode');
  if (existsSync(vscodeDir)) return vscodeDir;

  return null;
}

export async function installCline() {
  try {
    const chome = clineHome();
    if (!chome) return;

    const configPath = join(chome, 'cline_mcp_settings.json');
    mergeJsonConfig(configPath, 'mcpServers', {
      command: 'npx',
      args: ['--yes', '@strvmarv/total-recall']
    }, 'Cline');
  } catch (e) {
    process.stderr.write(`[total-recall:postinstall] Cline install failed (non-fatal): ${e.message}\n`);
  }
}