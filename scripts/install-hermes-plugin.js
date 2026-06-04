#!/usr/bin/env node
// Installs the total-recall Hermes MemoryProvider plugin into
// ~/.hermes/plugins/total-recall/ if Hermes is detected.
//
// Non-fatal: always resolves. If Hermes isn't present, silently skips.
// If the plugin is already installed, skips (idempotent).

import { cpSync, mkdirSync, existsSync } from 'node:fs';
import { execSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));

function hermesHome() {
  const env = process.env.HERMES_HOME;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  // Windows: APPDATA/hermes/hermes-agent
  const appdata = process.env.APPDATA;
  if (appdata) {
    const win = join(appdata, 'hermes');
    if (existsSync(join(win, 'config.yaml'))) return win;
  }

  // macOS/Linux: ~/.hermes
  const unix = join(home, '.hermes');
  if (existsSync(join(unix, 'config.yaml'))) return unix;

  return null;
}

function hermesCli() {
  try {
    const out = execSync('hermes config path', { encoding: 'utf-8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
    if (out) return 'hermes';
  } catch {
    // hermes not on PATH — still might be installable via files
  }
  return null;
}

export async function installHermesPlugin() {
  const hhome = hermesHome();
  if (!hhome) {
    // Hermes not detected — not an error, just skip
    return;
  }

  const hermes = hermesCli();
  if (!hermes) {
    // hermes CLI not available — can't update config, but can still copy files
    process.stderr.write('[total-recall:postinstall] Hermes home detected but `hermes` CLI not on PATH. Plugin files will be copied but config must be set manually.\n');
  }

  // Copy plugin files from the shipped hermes-plugin/ directory
  const src = join(__dirname, '..', 'hermes-plugin');
  const dest = join(hhome, 'plugins', 'total-recall');

  if (!existsSync(src)) {
    // Not shipped in this build (e.g., dev install from git)
    return;
  }

  mkdirSync(dest, { recursive: true });
  cpSync(src, dest, { recursive: true, force: true });

  process.stderr.write(`[total-recall:postinstall] Hermes plugin installed to ${dest}\n`);

  // Flip Hermes config to use total-recall as memory provider
  if (hermes) {
    try {
      execSync(`${hermes} config set memory.provider total-recall`, { stdio: 'ignore' });
      process.stderr.write('[total-recall:postinstall] Hermes memory provider configured\n');
    } catch (e) {
      process.stderr.write(`[total-recall:postinstall] Config update partially failed: ${e.message}\n`);
    }
  }
}
