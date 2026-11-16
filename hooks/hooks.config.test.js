// hooks/hooks.config.test.js
//
// Guards the Claude Code plugin's hooks/hooks.json (#97).
//
// total-recall used to register a SessionEnd hook that printed a user-facing
// compaction reminder. Claude Code runs a SessionEnd hook but never displays
// its output when it exits 0 (observed on Claude Code 2.1.325, interactive and
// `claude -p`: a JSON `systemMessage`, plain stdout and stderr were all
// invisible), so the hook only slowed every session end (about 1.2 s on
// Windows, which also needed an explicit `timeout`, #28). It was removed. The
// compaction reminder is the `hot_tier_compaction_suggested` hint of
// `session_start` (relayed by the model) plus one pinned-floor nudge per
// session, see AGENTS.md "Session Lifecycle".

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const hooksJsonPath = path.join(__dirname, 'hooks.json');

function loadHooksConfig() {
  return JSON.parse(fs.readFileSync(hooksJsonPath, 'utf8'));
}

test('hooks.json registers no SessionEnd hook and hooks/session-end is gone', () => {
  const config = loadHooksConfig();
  assert.equal(config.hooks.SessionEnd, undefined,
    'a SessionEnd hook runs but its output is never displayed (#97); do not register one for a reminder');
  assert.equal(fs.existsSync(path.join(__dirname, 'session-end')), false,
    'hooks/session-end/ must not exist');
});
