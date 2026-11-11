// cursor/cli.js — run the total-recall CLI (`ui`, `compact --run`, ...) against the SAME store as Cursor's MCP server.
//
// Cursor starts a plugin's MCP server with a sanitized environment (no TOTAL_RECALL_*, no XDG_*, no HERMES_HOME, no
// HOME on Windows), while a command the model runs in its shell inherits the user's full environment. Run as
// `node bin/start.js`, such a command could open or modify a different database than the server uses. This wrapper
// removes the same variables (storeEnvStrip, shared with the floor hook) and then runs bin/start.js with inherited stdio.
//
//   node "<plugin root>/cursor/cli.js" <total-recall CLI args>

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { storeEnvStrip } from './hook.js';

/** A copy of `environ` without the store-determining variables (see storeEnvStrip). */
export function strippedEnv(environ, platform) {
  const env = { ...environ };
  for (const key of Object.keys(storeEnvStrip(environ, platform))) delete env[key];
  return env;
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

if (isMain(import.meta.url)) {
  const start = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', 'bin', 'start.js');
  const r = spawnSync(process.execPath, [start, ...process.argv.slice(2)], {
    stdio: 'inherit',
    env: strippedEnv(process.env, process.platform),
  });
  process.exitCode = r.status ?? 1;
}
