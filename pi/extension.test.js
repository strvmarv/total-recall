import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  CONTEXT_MESSAGE_TYPE, DIRECT_TOOLS, MCP_SERVER_NAME,
  createExtension, mcpServerConfig, nodeCommand,
} from './extension.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const readFile = (p) => fs.readFileSync(p, 'utf8');
const SKILL_BODY_MARK = '# total-recall'; // first heading after the frontmatter

function ctx(o = {}) {
  const { branch = [], id = 'sid-1', idle = true } = o;
  const file = 'file' in o ? o.file : '/s.jsonl'; // an explicit undefined means an ephemeral session
  const notes = [];
  return {
    notes,
    cwd: '/proj',
    isIdle: () => idle,
    ui: { notify: (m, l) => notes.push([m, l]) },
    sessionManager: { getSessionId: () => id, getSessionFile: () => file, getBranch: () => branch },
  };
}

function boot(over = {}) {
  const handlers = new Map();
  const pi = {
    handlers,
    servers: [],
    commands: new Map(),
    sent: [],
    on: (ev, fn) => handlers.set(ev, fn),
    registerMcpServer: (name, cfg) => { pi.servers.push([name, cfg]); },
    registerCommand: (name, def) => { pi.commands.set(name, def); },
    sendUserMessage: (t, o) => pi.sent.push([t, o]),
  };
  const calls = { readFile: [], resolveEngine: 0, runFloor: [] };
  const deps = {
    rootDir: root,
    env: {},
    readFile: (p) => { calls.readFile.push(p); return readFile(p); },
    resolveEngine: () => { calls.resolveEngine += 1; return '/engine/total-recall.exe'; },
    runFloor: async (opts) => { calls.runFloor.push(opts); return 'FLOOR-TEXT'; },
    execPath: process.execPath,
    ...over,
  };
  createExtension(deps)(pi);
  const fire = (ev, event, c) => pi.handlers.get(ev)(event, c);
  return { pi, calls, fire };
}

// ---- nodeCommand / mcpServerConfig ----

test('nodeCommand: execPath only for a node basename, else the literal "node"', () => {
  assert.equal(nodeCommand('C:\\x\\node.exe'), 'C:\\x\\node.exe');
  assert.equal(nodeCommand('/usr/local/bin/node'), '/usr/local/bin/node');
  assert.equal(nodeCommand('/opt/pi/bin/pi'), 'node');
  assert.equal(nodeCommand('/usr/bin/bun'), 'node');
  assert.equal(nodeCommand('C:\\tools\\pi.exe'), 'node');
});

test('mcpServerConfig: deferred, absolute start.js, direct tools, long timeout, and never a cwd', () => {
  const cfg = mcpServerConfig({ rootDir: root, execPath: process.execPath });
  assert.equal(cfg.exposure, 'deferred');
  assert.ok(path.isAbsolute(cfg.args[0]));
  assert.equal(path.normalize(cfg.args[0]), path.join(root, 'bin', 'start.js'));
  for (const t of DIRECT_TOOLS) assert.equal(cfg.toolExposure[t], 'direct', t);
  assert.ok(DIRECT_TOOLS.includes('session_start') && DIRECT_TOOLS.includes('memory_*') && DIRECT_TOOLS.includes('kb_search'));
  assert.equal(cfg.timeout, 120);
  assert.equal('cwd' in cfg, false);
  assert.ok(typeof cfg.description === 'string' && cfg.description.length > 0);
});

// ---- registration ----

test('registration: server total-recall and both commands are registered', () => {
  const { pi } = boot();
  assert.equal(pi.servers.length, 1);
  assert.equal(pi.servers[0][0], MCP_SERVER_NAME);
  assert.equal(MCP_SERVER_NAME, 'total-recall');
  assert.deepEqual([...pi.commands.keys()], ['total-recall', 'total-recall:commands']);
});

test('registration: a throwing registerMcpServer does not throw and commands are still registered', () => {
  const handlers = new Map();
  const pi = {
    on: (e, f) => handlers.set(e, f),
    registerMcpServer: () => { throw new Error('bad config'); },
    registerCommand: (n, d) => handlers.set('cmd:' + n, d),
    sendUserMessage() {},
  };
  assert.doesNotThrow(() => createExtension({
    rootDir: root, env: {}, readFile, resolveEngine: () => null, runFloor: async () => undefined,
  })(pi));
  assert.ok(handlers.has('cmd:total-recall'));
  assert.ok(handlers.has('session_start'));
});

test('registration: an unreadable SKILL.md at load neither throws nor skips the MCP server', () => {
  const { pi } = boot({ readFile: () => { throw new Error('ENOENT'); } });
  assert.equal(pi.servers.length, 1);
  assert.equal(pi.commands.get('total-recall').getArgumentCompletions(''), null);
});

// ---- mapping ----

test('mapping: session text then floor text ride one hidden message; the next prompt carries only the floor', async () => {
  const { fire, calls } = boot();
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const r1 = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.equal(r1.message.customType, CONTEXT_MESSAGE_TYPE);
  assert.equal(CONTEXT_MESSAGE_TYPE, 'total-recall-context');
  assert.equal(r1.message.display, false);
  assert.ok(r1.message.content.includes(SKILL_BODY_MARK));
  assert.ok(!r1.message.content.includes('name: total-recall:commands'), 'frontmatter is stripped');
  assert.ok(r1.message.content.endsWith('FLOOR-TEXT'));
  assert.ok(r1.message.content.indexOf(SKILL_BODY_MARK) < r1.message.content.indexOf('FLOOR-TEXT'));
  const r2 = await fire('before_agent_start', { messageType: 'user', prompt: 'again' }, c);
  assert.equal(r2.message.content, 'FLOOR-TEXT');
  assert.equal(calls.runFloor.length, 2);
});

test('mapping: nothing queued and no floor output means undefined', async () => {
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx();
  assert.equal(await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c), undefined);
});

test('mapping: a custom-message run returns nothing and leaves the queue alone', async () => {
  const { fire } = boot();
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  assert.equal(await fire('before_agent_start', { messageType: 'custom' }, c), undefined);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
});

test('floor payload: session_id and cwd only (no transcript_path), binaryPath from resolveEngine', async () => {
  const { fire, calls } = boot();
  const c = ctx({ id: 'sid-9' });
  await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.deepEqual(calls.runFloor[0], {
    binaryPath: '/engine/total-recall.exe',
    payload: { session_id: 'sid-9', cwd: '/proj' },
  });
});

// ---- RF4: delivery exactly once ----

const count = (hay, needle) => hay.split(needle).length - 1;

test('RF4: two prompts awaited concurrently deliver the queued text exactly once in total', async () => {
  const { fire } = boot();
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const [a, b] = await Promise.all([
    fire('before_agent_start', { messageType: 'user', prompt: 'a' }, c),
    fire('before_agent_start', { messageType: 'user', prompt: 'b' }, c),
  ]);
  const all = [a, b].map((r) => r?.message.content ?? '').join('\n');
  assert.equal(count(all, SKILL_BODY_MARK), 1);
});

test('RF4: session_start twice (as pi does on a new session) still delivers the text exactly once', async () => {
  const { fire } = boot();
  await fire('session_start', { reason: 'new' }, ctx({ id: 'A' }));
  const c = ctx({ id: 'B' });
  await fire('session_start', { reason: 'new' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.equal(count(r.message.content, SKILL_BODY_MARK), 1);
  const r2 = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.equal(count(r2.message.content, SKILL_BODY_MARK), 0);
});

// ---- reasons ----

for (const reason of ['startup', 'new', 'resume', 'fork']) {
  test(`reasons: ${reason} queues the session text`, async () => {
    const { fire } = boot();
    const c = ctx();
    await fire('session_start', { reason }, c);
    const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
    assert.ok(r.message.content.includes(SKILL_BODY_MARK));
  });
}

const delivered = { type: 'custom_message', customType: 'total-recall-context' };
const compaction = { type: 'compaction' };
const msg = { type: 'message' };

async function reloadDelivers(branch) {
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx({ branch });
  await fire('session_start', { reason: 'reload' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  return r !== undefined && r.message.content.includes(SKILL_BODY_MARK);
}

test('reasons: reload with an empty branch queues (the first prompt never delivered it)', async () => {
  assert.equal(await reloadDelivers([]), true);
});
test('reasons: reload with a delivered message in the branch does not queue', async () => {
  assert.equal(await reloadDelivers([msg, delivered, msg]), false);
});
test('reasons: reload after compaction removed the delivered message queues again', async () => {
  assert.equal(await reloadDelivers([msg, delivered, compaction, msg]), true);
});
test('reasons: reload with a delivered message after the last compaction does not queue', async () => {
  assert.equal(await reloadDelivers([delivered, compaction, delivered, msg]), false);
});

test('reasons: session_compact queues the session text again', async () => {
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx();
  await fire('session_compact', { reason: 'manual' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
});

// ---- RF5: subagent children and ephemeral sessions ----

for (const [label, over, file] of [
  ['PI_SUBAGENT_ID set', { env: { PI_SUBAGENT_ID: 'child-1' } }, '/s.jsonl'],
  ['no session file', {}, undefined],
]) {
  test(`RF5: ${label}: no hook work, no message, MCP server still registered, command refuses`, async () => {
    const { pi, calls, fire } = boot(over);
    const c = ctx({ file });
    calls.readFile.length = 0; // count only calls made after load
    assert.equal(await fire('session_start', { reason: 'startup' }, c), undefined);
    assert.equal(await fire('session_compact', { reason: 'manual' }, c), undefined);
    assert.equal(await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c), undefined);
    assert.equal(calls.readFile.length, 0);
    assert.equal(calls.resolveEngine, 0);
    assert.equal(calls.runFloor.length, 0);
    assert.equal(pi.servers.length, 1);
    await pi.commands.get('total-recall').handler('help', c);
    assert.equal(pi.sent.length, 0);
    assert.equal(c.notes[0][1], 'warning');
  });
}

// ---- RF2: engine availability ----

test('RF2: no engine means no floor call, but the session text is still delivered', async () => {
  const { fire, calls } = boot({ resolveEngine: () => null });
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
  assert.ok(!r.message.content.includes('FLOOR-TEXT'));
  assert.equal(calls.runFloor.length, 0);
});

test('RF2: the engine is re-resolved per prompt (first-launch provisioning needs no restart)', async () => {
  let engine = null;
  const { fire, calls } = boot({ resolveEngine: () => engine });
  const c = ctx();
  assert.equal(await fire('before_agent_start', { messageType: 'user', prompt: '1' }, c), undefined);
  engine = '/engine/total-recall.exe';
  const r = await fire('before_agent_start', { messageType: 'user', prompt: '2' }, c);
  assert.equal(r.message.content, 'FLOOR-TEXT');
  assert.equal(calls.runFloor.length, 1);
});

test('RF2: a rejecting runFloor does not break the prompt', async () => {
  const { fire } = boot({ runFloor: async () => { throw new Error('spawn failed'); } });
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
  assert.equal(await fire('before_agent_start', { messageType: 'user', prompt: 'again' }, c), undefined);
});

// ---- default export ----

test('default export: wires real deps and registers the server without throwing', async () => {
  const mod = await import('./extension.js');
  const handlers = new Map();
  const pi = {
    on: (e, f) => handlers.set(e, f),
    registerMcpServer: (n, c) => handlers.set('mcp:' + n, c),
    registerCommand: (n, d) => handlers.set('cmd:' + n, d),
    sendUserMessage() {},
  };
  assert.doesNotThrow(() => mod.default(pi));
  assert.ok(handlers.has('mcp:total-recall'));
  assert.equal(path.normalize(handlers.get('mcp:total-recall').args[0]), path.join(root, 'bin', 'start.js'));
  assert.ok(handlers.has('before_agent_start'));
});
