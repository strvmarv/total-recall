#!/usr/bin/env node
// Installs total-recall hooks and steering into ~/.kiro/ if Kiro is detected.
//
// Copies:
//   hooks/kiro/*.json  -> ~/.kiro/hooks/
//   .kiro/steering/total-recall.md -> ~/.kiro/steering/
//
// Non-fatal: always resolves. If Kiro isn't present, silently skips.
// Idempotent: re-running overwrites with the latest version.

import { cpSync, mkdirSync, existsSync, readdirSync } from 'node:fs';
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

export async function installKiroHooks() {
  const khome = kiroHome();
  if (!khome) {
    // Kiro not detected — not an error, just skip
    return;
  }

  // --- Hooks ---
  // Copy the v1 hook JSON files from .kiro/hooks/ in the package to
  // ~/.kiro/hooks/ so they are active globally across all workspaces.
  const hooksSrc = join(ROOT, '.kiro', 'hooks');
  const hooksDest = join(khome, 'hooks');

  if (existsSync(hooksSrc)) {
    mkdirSync(hooksDest, { recursive: true });
    const files = readdirSync(hooksSrc).filter(f => f.startsWith('total-recall-') && f.endsWith('.json'));
    for (const file of files) {
      cpSync(join(hooksSrc, file), join(hooksDest, file), { force: true });
    }
    if (files.length > 0) {
      process.stderr.write(`[total-recall:postinstall] Kiro hooks installed to ${hooksDest} (${files.join(', ')})\n`);
    }
  }

  // --- Steering ---
  // Copy the always-included steering file to ~/.kiro/steering/ so the
  // agent always receives the session_start instruction in every workspace.
  const steeringSrc = join(ROOT, '.kiro', 'steering', 'total-recall.md');
  const steeringDest = join(khome, 'steering');

  if (existsSync(steeringSrc)) {
    mkdirSync(steeringDest, { recursive: true });
    cpSync(steeringSrc, join(steeringDest, 'total-recall.md'), { force: true });
    process.stderr.write(`[total-recall:postinstall] Kiro steering installed to ${steeringDest}\n`);
  }
}
