#!/usr/bin/env node
// Installs total-recall MCP config into VS Code's mcp.json
// (and ~/.copilot/mcp-config.json for Agent Host portability)
// if VS Code is detected.
//
// Non-fatal: always resolves. If VS Code isn't present, silently skips.

import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mergeJsonConfig } from './lib/merge-json-config.js';

function vscodeUserDir() {
  const env = process.env.VSCODE_USER_DIR;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  // Platform-specific user data directory
  if (process.platform === 'darwin') {
    return join(home, 'Library', 'Application Support', 'Code', 'User');
  }

  if (process.platform === 'win32') {
    const appdata = process.env.APPDATA;
    if (appdata) return join(appdata, 'Code', 'User');
    return null;
  }

  // Linux / other
  return join(home, '.config', 'Code', 'User');
}

export async function installVsCode() {
  try {
    const userDir = vscodeUserDir();
    if (!userDir) return;

    // Only write if VS Code is actually installed (the User dir exists)
    if (!existsSync(userDir)) return;

    // --- VS Code mcp.json (uses "servers" key, NOT "mcpServers") ---
    const mcpConfigPath = join(userDir, 'mcp.json');
    mergeJsonConfig(mcpConfigPath, 'servers', {
      command: 'npx',
      args: ['--yes', '@strvmarv/total-recall']
    }, 'VS Code');

    // --- ~/.copilot/mcp-config.json (for Agent Host portability) ---
    // Uses standard "mcpServers" key. The mergeJsonConfig helper's
    // non-clobbering check handles the case where the existing Copilot CLI
    // integration already registered the server.
    const home = process.env.USERPROFILE || homedir();
    if (home) {
      const copilotDir = join(home, '.copilot');
      const copilotConfigPath = join(copilotDir, 'mcp-config.json');
      mergeJsonConfig(copilotConfigPath, 'mcpServers', {
        command: 'npx',
        args: ['--yes', '@strvmarv/total-recall']
      }, 'VS Code (Copilot)');
    }
  } catch (e) {
    process.stderr.write(`[total-recall:postinstall] VS Code install failed (non-fatal): ${e.message}\n`);
  }
}