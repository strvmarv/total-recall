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

test('hooks.json registers the live-marker wrapper second in SessionStart and UserPromptSubmit', () => {
  const config = loadHooksConfig();
  const original = {
    SessionStart: 'bash "$CLAUDE_PLUGIN_ROOT/hooks/session-start/run.sh"',
    UserPromptSubmit: 'bash "$CLAUDE_PLUGIN_ROOT/hooks/user-prompt-submit/run.sh"',
  };
  for (const event of ['SessionStart', 'UserPromptSubmit']) {
    const groups = config.hooks[event];
    // SessionStart also has the compact-only group of the compaction breadcrumb (next test), after this one.
    assert.equal(groups.length, event === 'SessionStart' ? 2 : 1, `${event} groups`);
    const entries = groups[0].hooks;
    assert.equal(entries.length, 2, `${event} holds the original entry and the live-marker entry`);
    assert.deepEqual(entries[0], { type: 'command', command: original[event] }, `${event}: the original entry is first and unchanged`);
    assert.deepEqual(entries[1], {
      type: 'command',
      command: 'bash "$CLAUDE_PLUGIN_ROOT/hooks/live-marker/run.sh"',
      timeout: 5,
    }, `${event}: the live-marker entry is second, with a 5 s timeout`);
  }
  assert.equal(config.hooks.SessionStart[0].matcher, 'startup|resume|clear|compact', 'the SessionStart matcher is unchanged');
  assert.equal(config.hooks.UserPromptSubmit[0].matcher, '');
});

test('hooks.json registers the compaction breadcrumb in its own compact-only SessionStart group', () => {
  const config = loadHooksConfig();
  const groups = config.hooks.SessionStart;
  assert.equal(groups.length, 2, 'SessionStart holds the original group and the breadcrumb group');
  assert.deepEqual(groups[1], {
    matcher: 'compact',
    hooks: [{
      type: 'command',
      command: 'bash "$CLAUDE_PLUGIN_ROOT/hooks/compaction-breadcrumb/run.sh"',
      timeout: 5,
    }],
  }, 'the matcher is exactly compact (the source is the signal) and the entry has a 5 s timeout');
  // The script the entry names exists and is the one the tests run.
  const script = path.join(__dirname, 'compaction-breadcrumb', 'run.sh');
  assert.ok(fs.existsSync(script));
  // No other event carries it: the breadcrumb is said after a compaction only.
  for (const [event, eventGroups] of Object.entries(config.hooks)) {
    for (const [i, group] of eventGroups.entries()) {
      const holds = group.hooks.some((h) => h.command.includes('compaction-breadcrumb'));
      assert.equal(holds, event === 'SessionStart' && i === 1, `${event}[${i}]`);
    }
  }
});
