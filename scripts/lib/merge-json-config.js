// Shared helper: merge a total-recall server entry into a JSON config file.
// Non-clobbering: if the key already has a total-recall entry, an entry in exactly the script's own
// form is moved to the version being installed (see package-spec.js) and any other entry is left alone.
// Non-fatal: on parse failure of an existing file, bails and logs.
//
// configPath  — absolute path to the JSON config file
// key         — the top-level key (e.g. "mcpServers", "context_servers", "servers")
// entry       — the server config object for "total-recall" (npxEntry() from package-spec.js)
// label       — host name for stderr log messages and the install summary (e.g. "Cline", "Windsurf")

import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { recordChange } from './change-log.js';
import { decide } from './package-spec.js';

export function mergeJsonConfig(configPath, key, entry, label) {
  let config = {};
  const existed = existsSync(configPath);
  if (existed) {
    try {
      config = JSON.parse(readFileSync(configPath, 'utf8'));
    } catch {
      process.stderr.write(`[total-recall:postinstall] ${label} config unreadable — leaving it untouched.\n`);
      return;
    }
  }

  const existing = config[key]?.['total-recall'];
  if (existing) {
    // An entry in exactly the script's own form is moved to the version being installed; an npx
    // entry that holds the package name among other arguments is kept; anything else is the user's.
    const verdict = decide(existing, entry.args[1]);
    if (verdict === 'custom') {
      process.stderr.write(`[total-recall:postinstall] ${label} already has a custom total-recall entry — preserving user config.\n`);
    }
    if (verdict !== 'refresh') return;
    existing.args = [...entry.args];
    writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
    recordChange(label, 'rewrote', configPath);
    process.stderr.write(`[total-recall:postinstall] ${label} MCP entry updated in ${configPath}\n`);
    return;
  }

  if (!config[key] || typeof config[key] !== 'object') config[key] = {};
  config[key]['total-recall'] = entry;
  mkdirSync(dirname(configPath), { recursive: true });
  writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
  // Recorded only after the write succeeded: a failed write throws before this line.
  recordChange(label, existed ? 'rewrote' : 'added', configPath);
  process.stderr.write(`[total-recall:postinstall] ${label} MCP config written to ${configPath}\n`);
}