// hooks/hooks.config.test.js
//
// Regression guard for issue #28: Claude Code enforces a hard-coded 1.5s
// SHARED budget across all SessionEnd hooks unless a hook's own hooks.json
// entry sets an explicit per-hook `timeout` (seconds), in which case Claude
// Code raises the budget to match — capped at 60s
// (https://code.claude.com/docs/en/hooks.md). This repo's SessionEnd hook
// (bash -> node -> self-contained .NET exe -> SQLite) measures ~1.2-1.3s
// end-to-end on Windows, which sits right at that 1.5s default ceiling and
// gets cancelled. Without an explicit `timeout` field here, that regresses
// silently — this test catches the field being dropped or set outside a
// sane range.

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

test('SessionEnd hook declares an explicit timeout above Claude Code\'s 1.5s default budget', () => {
  const config = loadHooksConfig();
  const sessionEndEntries = config.hooks.SessionEnd;
  assert.ok(Array.isArray(sessionEndEntries) && sessionEndEntries.length > 0,
    'hooks.json must declare at least one SessionEnd matcher group');

  for (const group of sessionEndEntries) {
    for (const hook of group.hooks) {
      assert.equal(typeof hook.timeout, 'number',
        `SessionEnd hook ${JSON.stringify(hook.command)} must set a numeric "timeout"`);
      // Above the 1.5s default shared budget, with real headroom over this
      // hook's measured ~1.2-1.3s cost.
      assert.ok(hook.timeout >= 5,
        `SessionEnd hook timeout ${hook.timeout}s is too close to Claude Code's 1.5s default budget`);
      // Claude Code caps the raised SessionEnd budget at 60s regardless of a
      // higher configured value — a typo'd value above that (e.g. someone
      // entering minutes) would silently buy nothing.
      assert.ok(hook.timeout <= 60,
        `SessionEnd hook timeout ${hook.timeout}s exceeds Claude Code's 60s cap for SessionEnd`);
    }
  }
});
