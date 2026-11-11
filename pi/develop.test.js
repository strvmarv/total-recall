import { test } from 'node:test';
import assert from 'node:assert/strict';
import { developText, registerDevelop } from './develop.js';

const SKILL = '/r/skills/develop/SKILL.md';

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

// ---- developText ----

test('developText: names the skill path and carries the arguments', () => {
  const text = developText('123', SKILL);
  assert.ok(text.includes(SKILL));
  assert.ok(text.includes('123'));
  assert.match(text, /read/i);
  assert.match(text, /follow/i);
});

test('developText: blank, whitespace or missing arguments say there are none and mention the tutorial', () => {
  for (const a of ['', '   ', '\n\t', undefined, null]) {
    const text = developText(a, SKILL);
    assert.match(text, /no arguments/i, JSON.stringify(a));
    assert.match(text, /tutorial/i, JSON.stringify(a));
    assert.ok(text.includes(SKILL));
  }
});

test('developText: non-blank arguments do not claim there are none', () => {
  assert.doesNotMatch(developText('123', SKILL), /no arguments/i);
});

test('developText: an argument with a newline and quotes is carried verbatim', () => {
  const args = 'fix the "cache" bug\nit\'s `bad` $(id); rm -rf /\n  indented';
  assert.ok(developText(args, SKILL).includes(args));
});

// ---- registerDevelop ----

test('registerDevelop: registers exactly one command named develop, with a description', () => {
  const pi = fakePi();
  registerDevelop(pi, { hookable: () => true, developSkillPath: SKILL });
  assert.deepEqual([...pi.commands.keys()], ['develop']);
  assert.ok(pi.commands.get('develop').description.length > 0);
});

test('registerDevelop: when hookable, the handler sends one message that names the skill and the arguments', async () => {
  const pi = fakePi();
  registerDevelop(pi, { hookable: () => true, developSkillPath: SKILL });
  const ctx = fakeCtx({ idle: true });
  await pi.commands.get('develop').handler('123', ctx);
  assert.deepEqual(pi.sent, [[developText('123', SKILL), undefined]]);
  assert.deepEqual(ctx.notes, []);
});

test('registerDevelop: while streaming the message queues as a follow-up', async () => {
  const pi = fakePi();
  registerDevelop(pi, { hookable: () => true, developSkillPath: SKILL });
  await pi.commands.get('develop').handler('123', fakeCtx({ idle: false }));
  assert.deepEqual(pi.sent, [[developText('123', SKILL), { deliverAs: 'followUp' }]]);
});

test('registerDevelop: when not hookable it only notifies and sends nothing', async () => {
  const pi = fakePi();
  registerDevelop(pi, { hookable: () => false, developSkillPath: SKILL });
  const ctx = fakeCtx();
  await pi.commands.get('develop').handler('123', ctx);
  assert.deepEqual(pi.sent, []);
  assert.deepEqual(ctx.notes, [['develop is not active in this session', 'warning']]);
});

test('registerDevelop: the handler passes its own ctx to hookable', async () => {
  const pi = fakePi();
  const seen = [];
  registerDevelop(pi, { hookable: (c) => { seen.push(c); return true; }, developSkillPath: SKILL });
  const ctx = fakeCtx();
  await pi.commands.get('develop').handler('', ctx);
  assert.deepEqual(seen, [ctx]);
});

test('registerDevelop: a registerCommand that throws does not propagate', () => {
  const pi = fakePi({ throwOn: ['develop'] });
  assert.doesNotThrow(() => registerDevelop(pi, { hookable: () => true, developSkillPath: SKILL }));
  assert.equal(pi.commands.size, 0);
});

test('registerDevelop: a multi-line argument with quotes reaches the model verbatim', async () => {
  const pi = fakePi();
  registerDevelop(pi, { hookable: () => true, developSkillPath: SKILL });
  const args = 'add "retry"\nwith \'backoff\'';
  await pi.commands.get('develop').handler(args, fakeCtx());
  assert.equal(pi.sent.length, 1);
  assert.ok(pi.sent[0][0].includes(args));
});
