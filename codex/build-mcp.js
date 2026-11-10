// codex/build-mcp.js — generates codex/mcp.json from codex/bootstrap.cjs.
//
//   node codex/build-mcp.js           write codex/mcp.json
//   node codex/build-mcp.js --check   exit 1 if codex/mcp.json is stale
//
// The bootstrap source is embedded VERBATIM (JSON string escaping only, CRLF normalized to LF),
// so --check is a byte comparison and there is no minifier to get wrong.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Environment variables Codex forwards from the user's shell to the bundled server (it otherwise
 * starts it with a sanitized environment). Everything the engine reads to find its data dir and
 * config or to reach Cortex/embedding endpoints, plus CODEX_HOME for the bootstrap itself and
 * proxy/CA settings. An unset name is simply not forwarded.
 */
export const ENV_VARS = [
  'TOTAL_RECALL_HOME',
  'TOTAL_RECALL_DB_PATH',
  'TOTAL_RECALL_CORTEX_URL',
  'TOTAL_RECALL_CORTEX_PAT',
  'TOTAL_RECALL_USER_ID',
  'TOTAL_RECALL_EMBEDDING_API_KEY',
  'CODEX_HOME',
  'HOME',
  'XDG_DATA_HOME',
  'XDG_CONFIG_HOME',
  'HERMES_HOME',
  'NODE_EXTRA_CA_CERTS',
  'HTTPS_PROXY',
  'HTTP_PROXY',
  'NO_PROXY',
  'https_proxy',
  'http_proxy',
  'no_proxy',
  'all_proxy',
];

/** The text of codex/mcp.json for a given bootstrap source. */
export function buildMcp(sourceText) {
  const bootstrap = sourceText.replace(/\r\n/g, '\n');
  const config = {
    mcpServers: {
      'total-recall': {
        command: 'node',
        args: ['-e', bootstrap],
        env_vars: ENV_VARS,
        tool_timeout_sec: 120,
      },
    },
  };
  return `${JSON.stringify(config, null, 2)}\n`;
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

if (isMain(import.meta.url)) {
  const dir = path.dirname(fileURLToPath(import.meta.url));
  const expected = buildMcp(fs.readFileSync(path.join(dir, 'bootstrap.cjs'), 'utf8'));
  const target = path.join(dir, 'mcp.json');
  if (process.argv.includes('--check')) {
    let actual = '';
    try { actual = fs.readFileSync(target, 'utf8').replace(/\r\n/g, '\n'); } catch { /* missing counts as stale */ }
    if (actual !== expected) {
      process.stderr.write('codex/mcp.json is stale; run node codex/build-mcp.js\n');
      process.exit(1);
    }
  } else {
    fs.writeFileSync(target, expected);
  }
}
