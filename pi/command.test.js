import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { commandCompletions, forwardText, registerCommands } from './command.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const SKILL = fs.readFileSync(path.join(root, 'skills', 'commands', 'SKILL.md'), 'utf8');

function fakePi({ throwOn = [] } = {}) {
  const pi = { commands: new Map(), sent: [] };
  pi.registerCommand = (name, def) => {
    if (throwOn.includes(name)) throw new Error('cannot register ' + name);
    pi.commands.set(name, def);
  };
  pi.sendUserMessage = (text, opts) => { pi.sent.push([text, opts]); };
  return pi;
}
function fakeCtx({ idle = true } = {}) {
  const notes = [];
  return { notes, isIdle: () => idle, ui: { notify: (m, level) => notes.push([m, level]) } };
}

// ---- commandCompletions ----

test('commandCompletions: first tokens of the real SKILL.md command headings, minus setup and update', () => {
  const c = commandCompletions(SKILL);
  for (const want of ['help', 'status', 'search', 'store', 'forget', 'pin', 'kb', 'compact', 'ui', 'import-host']) {
    assert.ok(c.includes(want), want);
  }
  assert.equal(c.includes('setup'), false);
  assert.equal(c.includes('update'), false);
  assert.equal(c.filter((x) => x === 'kb').length, 1);
});

test('commandCompletions: text without a Commands section yields []', () => {
  assert.deepEqual(commandCompletions('# x\n\n### search <q>\n'), []);
  assert.deepEqual(commandCompletions(''), []);
});

test('commandCompletions: stops at the next level-2 heading', () => {
  const text = '## Commands\n\n### a <x>\n\n### b\n\n## Other\n\n### c\n';
  assert.deepEqual(commandCompletions(text), ['a', 'b']);
});

// ---- forwardText ----

test('forwardText: empty or blank args mean help', () => {
  assert.match(forwardText(''), /command: help\./);
  assert.match(forwardText('   '), /command: help\./);
  assert.match(forwardText(undefined), /command: help\./);
});

test('forwardText (RF3): quoted, multi-line and very long args are forwarded verbatim', () => {
  const args = 'search "a b"\nline2 ' + 'x'.repeat(5000);
  const text = forwardText(args);
  assert.ok(text.includes(args));
  assert.match(text, /^Run the total-recall command: /);
  assert.match(text, /Follow the Commands section of the total-recall instructions\.$/);
});

// ---- registerCommands ----

test('registerCommands: registers total-recall and the total-recall:commands alias, both with descriptions', () => {
  const pi = fakePi();
  registerCommands(pi, { hookable: () => true, skillText: SKILL });
  assert.deepEqual([...pi.commands.keys()], ['total-recall', 'total-recall:commands']);
  for (const def of pi.commands.values()) assert.ok(def.description.length > 0);
});

test('registerCommands: idle sends the forward text with no delivery option', async () => {
  const pi = fakePi();
  registerCommands(pi, { hookable: () => true, skillText: SKILL });
  await pi.commands.get('total-recall').handler('status', fakeCtx({ idle: true }));
  assert.deepEqual(pi.sent, [[forwardText('status'), undefined]]);
});

test('registerCommands: while streaming it queues as a follow-up', async () => {
  const pi = fakePi();
  registerCommands(pi, { hookable: () => true, skillText: SKILL });
  await pi.commands.get('total-recall:commands').handler('', fakeCtx({ idle: false }));
  assert.deepEqual(pi.sent, [[forwardText(''), { deliverAs: 'followUp' }]]);
});

test('registerCommands (RF5): not hookable means a warning and nothing sent', async () => {
  const pi = fakePi();
  const ctx = fakeCtx();
  registerCommands(pi, { hookable: () => false, skillText: SKILL });
  await pi.commands.get('total-recall').handler('help', ctx);
  assert.equal(pi.sent.length, 0);
  assert.equal(ctx.notes.length, 1);
  assert.equal(ctx.notes[0][1], 'warning');
});

test('registerCommands: a throwing alias registration does not disable the main command or throw', () => {
  const pi = fakePi({ throwOn: ['total-recall:commands'] });
  assert.doesNotThrow(() => registerCommands(pi, { hookable: () => true, skillText: SKILL }));
  assert.deepEqual([...pi.commands.keys()], ['total-recall']);
});

test('registerCommands: argument completions filter by prefix and return null when empty', () => {
  const pi = fakePi();
  registerCommands(pi, { hookable: () => true, skillText: SKILL });
  const { getArgumentCompletions } = pi.commands.get('total-recall');
  const co = getArgumentCompletions('co');
  assert.ok(co.some((i) => i.value === 'compact' && i.label === 'compact'));
  assert.ok(co.every((i) => i.value.startsWith('co')));
  assert.equal(getArgumentCompletions('zzz'), null);
});

test('registerCommands: empty skill text gives null completions, not a throw', () => {
  const pi = fakePi();
  registerCommands(pi, { hookable: () => true, skillText: '' });
  assert.equal(pi.commands.get('total-recall').getArgumentCompletions(''), null);
});

test('registerCommands: only the first word is completed (a prefix with a space gets none)', () => {
  const pi = fakePi();
  registerCommands(pi, { hookable: () => true, skillText: SKILL });
  assert.equal(pi.commands.get('total-recall').getArgumentCompletions('kb se'), null);
});

// ---- shared-text edits for pi (Task 6) ----

test('SKILL.md: the host table has a pi row describing the turn-count floor', () => {
  const row = SKILL.split(/\r?\n/).find((l) => /^\| pi \|/.test(l));
  assert.ok(row, 'pi row present');
  assert.match(row, /turn-count/);
  assert.match(row, /before_agent_start/);
});

test('SKILL.md: the growth-trigger sentence is qualified for hosts without a transcript', () => {
  assert.match(SKILL, /where the host exposes a transcript/);
});

test('SKILL.md: compact says what to do on hosts without the compactor agent', () => {
  assert.match(SKILL, /hosts without the compactor agent/);
});
