// Shared helper: merge a total-recall server entry into a JSON config file.
// Non-clobbering: if the key already has a total-recall entry, leaves it alone.
// Non-fatal: on parse failure of an existing file, bails and logs.
//
// configPath  — absolute path to the JSON config file
// key         — the top-level key (e.g. "mcpServers", "context_servers", "servers")
// entry       — the server config object for "total-recall"
// label       — host name for stderr log messages (e.g. "Cline", "Windsurf")

import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname } from 'node:path';

export function mergeJsonConfig(configPath, key, entry, label) {
  let config = {};
  if (existsSync(configPath)) {
    try {
      config = JSON.parse(readFileSync(configPath, 'utf8'));
    } catch {
      process.stderr.write(`[total-recall:postinstall] ${label} config unreadable — leaving it untouched.\n`);
      return;
    }
  }

  const existing = config[key]?.['total-recall'];
  if (existing) {
    if (existing.command === 'npx' &&
        Array.isArray(existing.args) &&
        existing.args.includes('@strvmarv/total-recall')) {
      return; // already configured correctly
    }
    process.stderr.write(`[total-recall:postinstall] ${label} already has a custom total-recall entry — preserving user config.\n`);
    return;
  }

  if (!config[key] || typeof config[key] !== 'object') config[key] = {};
  config[key]['total-recall'] = entry;
  mkdirSync(dirname(configPath), { recursive: true });
  writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
  process.stderr.write(`[total-recall:postinstall] ${label} MCP config written to ${configPath}\n`);
}