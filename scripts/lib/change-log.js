// Shared helper: records what the install script does to other tools, so
// postinstall.js can print one summary at the end and keep a log of it.
//
// recordChange(tool, action, detail)
//   action — 'added' | 'rewrote' | 'overwrote' | 'switched' | 'failed'
//   detail — the path written (or, for 'switched', what was set; for 'failed', the message)
//
// Zero dependencies. appendLog never throws: a log that cannot be written
// must not fail an install.

import { appendFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const PREFIX = '[total-recall:postinstall]';
const LOG_NAME = 'postinstall.log';

let changes = [];

export function recordChange(tool, action, detail) {
  changes.push({ tool, action, detail });
}

/** Returns what was recorded since the last call and clears it. */
export function takeChanges() {
  const taken = changes;
  changes = [];
  return taken;
}

function line(c) {
  return c.action === 'failed'
    ? `${PREFIX}   ${c.tool}: failed: ${c.detail}`
    : `${PREFIX}   ${c.tool}: ${c.action} ${c.detail}`;
}

export function formatSummary(list) {
  if (list.length === 0) return `${PREFIX} No changes were made to other tools.`;
  const n = list.length;
  return [`${PREFIX} Changed other tools (${n} change${n === 1 ? '' : 's'}):`, ...list.map(line)].join('\n');
}

/** A log entry: a heading with the UTC date, time and package version, then the summary. */
export function formatLogEntry(summary, { version, now = new Date() }) {
  const stamp = now.toISOString().replace(/\.\d{3}Z$/, 'Z');
  return `## ${stamp} total-recall ${version}\n${summary}\n\n`;
}

/** Appends to `<dataDir>/postinstall.log`. Returns true when it was written. */
export function appendLog(dataDir, text) {
  if (typeof dataDir !== 'string' || dataDir === '') return false;
  try {
    mkdirSync(dataDir, { recursive: true });
    appendFileSync(join(dataDir, LOG_NAME), text);
    return true;
  } catch {
    return false;
  }
}
