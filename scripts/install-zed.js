#!/usr/bin/env node
// Installs total-recall MCP config into Zed's settings.json
// if Zed is detected.
//
// Non-fatal: always resolves. If Zed isn't present, silently skips.

import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mergeJsonConfig } from './lib/merge-json-config.js';

function zedConfigDir() {
  const env = process.env.ZED_SETTINGS;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  // Platform-specific config directory
  if (process.platform === 'darwin') {
    const macPath = join(home, 'Library', 'Application Support', 'Zed');
    if (existsSync(macPath)) return macPath;
    return null;
  }

  if (process.platform === 'win32') {
    const appdata = process.env.APPDATA;
    if (appdata) {
      const winPath = join(appdata, 'Zed');
      if (existsSync(winPath)) return winPath;
    }
    return null;
  }

  // Linux / other
  const linuxPath = join(home, '.config', 'zed');
  if (existsSync(linuxPath)) return linuxPath;

  return null;
}

export async function installZed() {
  try {
    const zdir = zedConfigDir();
    if (!zdir) return;

    const configPath = join(zdir, 'settings.json');
    // Zed uses "context_servers" key (NOT "mcpServers")
    mergeJsonConfig(configPath, 'context_servers', {
      command: 'npx',
      args: ['--yes', '@strvmarv/total-recall']
    }, 'Zed');
  } catch (e) {
    process.stderr.write(`[total-recall:postinstall] Zed install failed (non-fatal): ${e.message}\n`);
  }
}