// Runs the installers that configure other tools, then reports what they did:
// one summary on stderr and, when something was written or failed, the same
// lines appended to <dataDir>/postinstall.log.
//
// TOTAL_RECALL_SKIP_HOST_CONFIG=1 (exactly that value) runs none of them.
//
// Never throws: a report that cannot be written must not fail an install.

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveDataDir } from '../fetch-binary.js';
import { appendLog, formatLogEntry, formatSummary, recordChange, takeChanges } from './change-log.js';

const PREFIX = '[total-recall:postinstall]';

function packageVersion() {
  try {
    const here = dirname(fileURLToPath(import.meta.url));
    const v = JSON.parse(readFileSync(join(here, '..', '..', 'package.json'), 'utf8')).version;
    return typeof v === 'string' && v ? v : 'unknown';
  } catch {
    return 'unknown';
  }
}

export async function runHostConfig(installers, { env = process.env, stderr = process.stderr, version } = {}) {
  try {
    if (env.TOTAL_RECALL_SKIP_HOST_CONFIG === '1') {
      stderr.write(`${PREFIX} Other tools skipped: TOTAL_RECALL_SKIP_HOST_CONFIG=1 is set.\n`);
      return;
    }

    takeChanges();
    for (const install of installers) {
      try {
        await install();
      } catch (e) {
        recordChange(install.name || 'installer', 'failed', e?.message ?? String(e));
      }
    }

    const changes = takeChanges();
    const summary = formatSummary(changes);
    stderr.write(`${summary}\n`);
    if (changes.length > 0) {
      appendLog(resolveDataDir(env), formatLogEntry(summary, { version: version ?? packageVersion() }));
    }
  } catch (e) {
    try {
      stderr.write(`${PREFIX} Could not report changes to other tools (non-fatal): ${e?.message ?? e}\n`);
    } catch {
      // nothing left to do
    }
  }
}
