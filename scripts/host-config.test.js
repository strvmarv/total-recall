import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  recordChange, takeChanges, formatSummary, formatLogEntry, appendLog,
} from './lib/change-log.js';

const P = '[total-recall:postinstall]';

function tmp(prefix = 'tr-hostcfg-') {
  return fs.mkdtempSync(path.join(os.tmpdir(), prefix));
}

// --- change-log: the recorder, the summary text and the append-only log ---

test('recordChange / takeChanges: returns what was recorded, in order, and clears it', () => {
  takeChanges();
  recordChange('Zed', 'added', '/z/settings.json');
  recordChange('Hermes', 'switched', 'memory.provider');
  assert.deepEqual(takeChanges(), [
    { tool: 'Zed', action: 'added', detail: '/z/settings.json' },
    { tool: 'Hermes', action: 'switched', detail: 'memory.provider' },
  ]);
  assert.deepEqual(takeChanges(), []);
});

test('formatSummary: one line per change with tool, action and path', () => {
  const text = formatSummary([
    { tool: 'Zed', action: 'added', detail: '/z/settings.json' },
    { tool: 'Gemini CLI', action: 'rewrote', detail: '/g/settings.json' },
  ]);
  assert.equal(text, [
    `${P} Changed other tools (2 changes):`,
    `${P}   Zed: added /z/settings.json`,
    `${P}   Gemini CLI: rewrote /g/settings.json`,
  ].join('\n'));
});

test('formatSummary: a failed step is a line of its own with its message', () => {
  const text = formatSummary([{ tool: 'Hermes', action: 'failed', detail: 'EACCES: denied' }]);
  assert.match(text, /\n\[total-recall:postinstall\]   Hermes: failed: EACCES: denied$/);
});

test('formatSummary: nothing recorded says so in one line', () => {
  assert.equal(formatSummary([]), `${P} No changes were made to other tools.`);
});

test('formatLogEntry: heading with date, time and version, then the summary lines', () => {
  const entry = formatLogEntry('LINES', { version: '4.7.1', now: new Date('2026-11-14T10:20:30Z') });
  assert.equal(entry, '## 2026-11-14T10:20:30Z total-recall 4.7.1\nLINES\n\n');
});

test('appendLog: creates postinstall.log, and a second call appends without touching the first entry', () => {
  const dir = path.join(tmp(), 'data'); // does not exist yet
  assert.equal(appendLog(dir, 'first\n'), true);
  assert.equal(appendLog(dir, 'second\n'), true);
  assert.equal(fs.readFileSync(path.join(dir, 'postinstall.log'), 'utf8'), 'first\nsecond\n');
});

test('appendLog: an unknown or unwritable directory returns false and never throws', () => {
  assert.equal(appendLog(null, 'x'), false);
  const file = path.join(tmp(), 'a-file');
  fs.writeFileSync(file, '');
  assert.equal(appendLog(path.join(file, 'sub'), 'x'), false); // parent is a regular file
});
