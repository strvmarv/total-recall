#!/usr/bin/env node
// Installs total-recall MCP config for Cline, into the settings file of each Cline
// installation found on the machine:
//
//   ~/.cline/data/settings/cline_mcp_settings.json              when ~/.cline/data exists (current layout)
//   <VS Code User dir>/globalStorage/saoudrizwan.claude-dev/
//       settings/cline_mcp_settings.json                        when that extension folder exists (older layout)
//
// These are the files the repository's Cline importer reads. Cline is detected by the
// directories it creates, never by ~/.vscode (that is VS Code's own directory).
// CLINE_HOME is an explicit override: the directory that holds cline_mcp_settings.json
// itself; it is written (and created) without a detection check, and nothing else is.
//
// Non-fatal: always resolves. If Cline isn't present, silently skips.

import { existsSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mergeJsonConfig } from './lib/merge-json-config.js';
import { npxEntry } from './lib/package-spec.js';
import { recordChange } from './lib/change-log.js';
import { vscodeUserDir } from './lib/vscode-user-dir.js';

const FILE = 'cline_mcp_settings.json';

/** The settings files to write, in order. */
function clineTargets() {
  const override = process.env.CLINE_HOME;
  if (override) return [join(override, FILE)];

  const home = process.env.USERPROFILE || homedir();
  const targets = [];

  if (home) {
    const data = join(home, '.cline', 'data');
    if (existsSync(data)) targets.push(join(data, 'settings', FILE));
  }

  const userDir = vscodeUserDir();
  if (userDir) {
    const extension = join(userDir, 'globalStorage', 'saoudrizwan.claude-dev');
    if (existsSync(extension)) targets.push(join(extension, 'settings', FILE));
  }

  return targets;
}

export async function installCline() {
  let targets = [];
  try {
    targets = clineTargets();
  } catch (e) {
    recordChange('Cline', 'failed', e.message);
    process.stderr.write(`[total-recall:postinstall] Cline install failed (non-fatal): ${e.message}\n`);
    return;
  }

  // One try/catch per target, so a file that cannot be written does not stop the other.
  for (const configPath of targets) {
    try {
      mergeJsonConfig(configPath, 'mcpServers', npxEntry(), 'Cline');
    } catch (e) {
      recordChange('Cline', 'failed', `${configPath}: ${e.message}`);
      process.stderr.write(`[total-recall:postinstall] Cline install failed for ${configPath} (non-fatal): ${e.message}\n`);
    }
  }
}
