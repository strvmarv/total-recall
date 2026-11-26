// hooks/compaction-breadcrumb/hosts.test.js — the registered SessionStart hooks, run the way Claude Code
// runs them (#153).
//
// Claude Code reads hooks/hooks.json, and for a SessionStart event runs every command of every group whose
// `matcher` matches the event's `source` (a regular expression that must match the whole source:
// observed on 2.1.332, where the group matching `compact` ran only on a compaction while the group
// `startup|resume|clear|compact` ran on all four). This test does the same over a temporary copy of the
// plugin files with a scrubbed environment, so the real hooks run (the startup instructions, the what's-new
// step, the live-marker writer and the breadcrumb) and nothing reads or writes the real data directory or
// ~/.claude. It looks for a unique string of the note in everything the hooks print.

import { test, describe, before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const skip = process.platform === 'win32' ? 'the hook scripts need bash; not run on Windows' : false;

const BASH = (() => {
  for (const dir of (process.env.PATH ?? '').split(path.delimiter)) {
    const candidate = path.join(dir, 'bash');
    if (dir && fs.existsSync(candidate)) return candidate;
  }
  return 'bash';
})();

/** A unique string of the note (it is in no other hook's output, the startup instructions included). */
const NOTE = 'this conversation was just compacted';

let root;
before(() => { root = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-breadcrumb-hosts-')); });
after(() => { fs.rmSync(root, { recursive: true, force: true }); });

let counter = 0;
function tmpDir(label) {
  const dir = path.join(root, `${label}-${++counter}`);
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

/** The plugin files the registered SessionStart hooks use, copied into a temporary tree. */
function pluginTree() {
  const tree = tmpDir('plugin');
  const copy = (rel) => {
    const from = path.join(repoRoot, rel);
    const to = path.join(tree, rel);
    fs.mkdirSync(path.dirname(to), { recursive: true });
    fs.cpSync(from, to, { recursive: true });
  };
  for (const rel of [
    'hooks/hooks.json',
    'hooks/session-start',
    'hooks/live-marker',
    'hooks/compaction-breadcrumb/run.sh',
    'hooks/engine-path.js',
    'skills/commands/SKILL.md',
    'pi/whatsNew.js',
    'pi/floor.js',
    'scripts/fetch-binary.js',
  ]) copy(rel);
  return tree;
}

function enable(dataDir) {
  fs.mkdirSync(path.join(dataDir, 'live'), { recursive: true });
  fs.writeFileSync(path.join(dataDir, 'live', 'enabled'), '');
}

const PAYLOAD = (source) => JSON.stringify({
  session_id: 'aaaaaaaa-1111-4222-8333-444444444444',
  transcript_path: '/home/u/.claude/projects/-proj/aaaaaaaa-1111-4222-8333-444444444444.jsonl',
  cwd: '/work/proj',
  hook_event_name: 'SessionStart',
  source,
});

/**
 * Runs the SessionStart hooks of `tree` for one event with `source`, as Claude Code would, and returns the
 * `additionalContext` of every command that printed one, plus how many commands ran.
 */
function sessionStart(tree, source, env) {
  const config = JSON.parse(fs.readFileSync(path.join(tree, 'hooks/hooks.json'), 'utf8'));
  const contexts = [];
  let ran = 0;
  for (const group of config.hooks.SessionStart) {
    // The matcher is a regular expression over the whole source; an empty or missing one matches all.
    if (group.matcher && !new RegExp(`^(?:${group.matcher})$`).test(source)) continue;
    for (const hook of group.hooks) {
      ran++;
      const r = spawnSync(BASH, ['-c', hook.command], {
        env: { ...env, CLAUDE_PLUGIN_ROOT: tree },
        cwd: tmpDir('cwd'),
        input: PAYLOAD(source),
        encoding: 'utf8',
        timeout: 20000,
      });
      assert.equal(r.status, 0, `${hook.command}: ${r.stderr}`);
      const text = r.stdout.trim();
      if (!text) continue;
      const out = JSON.parse(text);
      const context = out.hookSpecificOutput?.additionalContext ?? out.additionalContext;
      if (typeof context === 'string') contexts.push(context);
    }
  }
  return { contexts, ran };
}

function hostEnv(dataDir) {
  // Only what the scripts need from the caller's environment is kept: the search path (bash, node and the
  // coreutils), and a temporary HOME and TOTAL_RECALL_HOME.
  return { PATH: process.env.PATH ?? '', HOME: tmpDir('home'), TOTAL_RECALL_HOME: dataDir };
}

const mentions = (contexts) => contexts.filter((c) => c.includes(NOTE)).length;

describe('SessionStart hooks as Claude Code runs them', { skip }, () => {
  test('Compact_ArchiveOn_NoteOnce_OtherSourcesNone', () => {
    const tree = pluginTree();
    const dataDir = tmpDir('data');
    enable(dataDir);
    const env = hostEnv(dataDir);

    for (const source of ['startup', 'resume', 'clear']) {
      const { contexts, ran } = sessionStart(tree, source, env);
      assert.equal(mentions(contexts), 0, `${source} adds no note`);
      assert.ok(contexts.some((c) => c.includes('session_start')), `${source}: the startup instructions are still delivered`);
      assert.equal(ran, 2, `${source}: the original group runs (the startup script and the marker writer)`);
    }

    // Two compactions in a row: each run carries the note exactly once.
    for (let i = 0; i < 2; i++) {
      const { contexts, ran } = sessionStart(tree, 'compact', env);
      assert.equal(ran, 3, 'the original group (2 entries) and the breadcrumb entry run');
      assert.equal(mentions(contexts), 1, `compaction ${i + 1}: the note is in the output exactly once`);
      assert.ok(contexts.some((c) => c.includes('session_start')), 'the startup instructions are still delivered on compact');
    }
  });

  test('Compact_ArchiveOff_NothingAdded', () => {
    const tree = pluginTree();
    const dataDir = tmpDir('data'); // no sentinel
    const env = hostEnv(dataDir);
    const { contexts, ran } = sessionStart(tree, 'compact', env);
    assert.equal(ran, 3);
    assert.equal(mentions(contexts), 0, 'the archive is off: no note');
    assert.ok(contexts.some((c) => c.includes('session_start')), 'the original hooks are unchanged');
    assert.equal(fs.existsSync(path.join(dataDir, 'live')), false, 'the archive-off run creates no live directory');
  });

  test('Matcher_IsWholeSourceMatch_NoPartialSources', () => {
    // A source that merely contains the word is not a compaction.
    const tree = pluginTree();
    const dataDir = tmpDir('data');
    enable(dataDir);
    const env = hostEnv(dataDir);
    for (const source of ['compacted', 'precompact', 'compact-auto', '']) {
      const { contexts } = sessionStart(tree, source, env);
      assert.equal(mentions(contexts), 0, `source "${source}"`);
    }
  });
});
