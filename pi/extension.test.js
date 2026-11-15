import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import {
  CONTEXT_MESSAGE_TYPE, DIRECT_TOOLS, MCP_SERVER_NAME,
  createExtension, mcpServerConfig, nodeCommand,
} from './extension.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const readFile = (p) => fs.readFileSync(p, 'utf8');
const SKILL_BODY_MARK = '# total-recall'; // first heading after the frontmatter

function ctx(o = {}) {
  const { branch = [], id = 'sid-1', idle = true, hasUI = false, parent } = o;
  const file = 'file' in o ? o.file : '/s.jsonl'; // an explicit undefined means an ephemeral session
  const notes = [];
  const sessionManager = { getSessionId: () => id, getSessionFile: () => file, getBranch: () => branch };
  // `header` picks what getHeader does: a header (naming `parent`, when given), none, or a throw.
  // Left out, the session manager has no getHeader at all, as before pi-subagents children were handled.
  if (o.header === 'missing') sessionManager.getHeader = () => null;
  else if (o.header === 'throws') sessionManager.getHeader = () => { throw new Error('no header'); };
  else if (parent !== undefined || o.header === 'present') sessionManager.getHeader = () => ({ type: 'session', parentSession: parent });
  return {
    notes,
    cwd: '/proj',
    hasUI,
    isIdle: () => idle,
    ui: { notify: (m, l) => notes.push([m, l]) },
    sessionManager,
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
  const calls = { readFile: [], resolveEngine: 0, runFloor: [], runWhatsNew: [], stamps: [], order: [], dataDirEnvs: [] };
  const deps = {
    rootDir: root,
    env: {},
    readFile: (p) => { calls.readFile.push(p); return readFile(p); },
    resolveEngine: () => { calls.resolveEngine += 1; calls.order.push('resolveEngine'); return '/engine/total-recall.exe'; },
    runFloor: async (opts) => { calls.runFloor.push(opts); return 'FLOOR-TEXT'; },
    runWhatsNew: async (opts) => { calls.runWhatsNew.push(opts); calls.order.push('runWhatsNew'); return undefined; },
    writeDirectStamp: (opts) => { calls.stamps.push(opts); calls.order.push('stamp'); return true; },
    resolveDataDir: (e) => { calls.dataDirEnvs.push(e); return '/data'; },
    resolveUserServer: () => ({ skip: false, env: {} }),
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
  assert.deepEqual(cfg.env, { TOTAL_RECALL_HOST: 'pi' });
  assert.ok(typeof cfg.description === 'string' && cfg.description.length > 0);
});

// ---- registration ----

test('registration: server total-recall and the commands (including develop) are registered', () => {
  const { pi } = boot();
  assert.equal(pi.servers.length, 1);
  assert.equal(pi.servers[0][0], MCP_SERVER_NAME);
  assert.equal(MCP_SERVER_NAME, 'total-recall');
  assert.deepEqual([...pi.commands.keys()], ['total-recall', 'total-recall:commands', 'develop']);
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

test('develop: the handler sends a message naming skills/develop/SKILL.md, not the commands skill', async () => {
  const { pi } = boot();
  await pi.commands.get('develop').handler('123', ctx());
  assert.equal(pi.sent.length, 1);
  const text = pi.sent[0][0];
  assert.ok(text.includes(path.join(root, 'skills', 'develop', 'SKILL.md')));
  assert.ok(!text.includes(path.join(root, 'skills', 'commands', 'SKILL.md')));
  assert.ok(text.includes('123'));
});

test('develop: a registerCommands throw does not stop develop from registering', () => {
  const pi = {
    commands: new Map(),
    on() {},
    registerMcpServer() {},
    registerCommand: (n, d) => {
      if (n.startsWith('total-recall')) throw new Error('cannot register ' + n);
      pi.commands.set(n, d);
    },
    sendUserMessage() {},
  };
  assert.doesNotThrow(() => createExtension({
    rootDir: root, env: {}, readFile, resolveEngine: () => null, runFloor: async () => undefined,
  })(pi));
  assert.deepEqual([...pi.commands.keys()], ['develop']);
});

test('develop: a develop registration throw does not stop total-recall from registering', () => {
  const pi = {
    commands: new Map(),
    on() {},
    registerMcpServer() {},
    registerCommand: (n, d) => {
      if (n === 'develop') throw new Error('cannot register develop');
      pi.commands.set(n, d);
    },
    sendUserMessage() {},
  };
  assert.doesNotThrow(() => createExtension({
    rootDir: root, env: {}, readFile, resolveEngine: () => null, runFloor: async () => undefined,
  })(pi));
  assert.deepEqual([...pi.commands.keys()], ['total-recall', 'total-recall:commands']);
});

test('develop: a subagent child (PI_SUBAGENT_ID) gets the not-active notice and nothing is sent', async () => {
  const { pi } = boot({ env: { PI_SUBAGENT_ID: 'child-1' } });
  const c = ctx();
  await pi.commands.get('develop').handler('123', c);
  assert.deepEqual(pi.sent, []);
  assert.deepEqual(c.notes, [['develop is not active in this session', 'warning']]);
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
    env: {},
  });
});

test('review: the floor runs against the store the user-configured mcp.json server uses (env passed through)', async () => {
  const seenCwd = [];
  const { fire, calls } = boot({
    resolveUserServer: (cwd) => { seenCwd.push(cwd); return { skip: false, env: { TOTAL_RECALL_HOME: 'D:/store' } }; },
  });
  await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, ctx());
  assert.deepEqual(seenCwd, ['/proj']);
  assert.deepEqual(calls.runFloor[0].env, { TOTAL_RECALL_HOME: 'D:/store' });
});

test('review: a user server whose store cannot be matched safely silences the floor but not the instructions', async () => {
  const { fire, calls } = boot({ resolveUserServer: () => ({ skip: true, env: {} }) });
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
  assert.ok(!r.message.content.includes('FLOOR-TEXT'));
  assert.equal(calls.runFloor.length, 0);
});

test('review: a throwing resolveUserServer does not break the prompt', async () => {
  const { fire } = boot({ resolveUserServer: () => { throw new Error('boom'); } });
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
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

test('reasons: reload with a getBranch that throws still queues (never breaks the session start)', async () => {
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx();
  c.sessionManager.getBranch = () => { throw new Error('boom'); };
  assert.equal(await fire('session_start', { reason: 'reload' }, c), undefined);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
});

test('reasons: session_compact queues the session text again', async () => {
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx();
  await fire('session_compact', { reason: 'manual' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
});


// ---- review feedback: /tree navigation (Copilot thread) ----

async function treeQueues(branch, over = {}) {
  const { fire } = boot({ runFloor: async () => undefined, ...over });
  const c = ctx({ branch });
  await fire('session_tree', { newLeafId: 'x', oldLeafId: 'y' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  return r !== undefined && r.message.content.includes(SKILL_BODY_MARK);
}

test('session_tree: navigating to a branch without the instructions queues them again', async () => {
  assert.equal(await treeQueues([msg]), true);
  assert.equal(await treeQueues([delivered, compaction, msg]), true);
});
test('session_tree: a branch that still holds the instructions queues nothing', async () => {
  assert.equal(await treeQueues([msg, delivered, msg]), false);
});
test('session_tree: a subagent child or ephemeral session queues nothing', async () => {
  assert.equal(await treeQueues([msg], { env: { PI_SUBAGENT_ID: 'c' } }), false);
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx({ file: undefined });
  assert.equal(await fire('session_tree', {}, c), undefined);
  assert.equal(await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c), undefined);
});

// ---- review feedback: the CLI is not on PATH in pi (Copilot thread) ----

test('queued text tells the model how to run this package CLI, because total-recall is not on PATH', async () => {
  const { fire } = boot({ runFloor: async () => undefined });
  const c = ctx();
  await fire('session_start', { reason: 'startup' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(path.join(root, 'bin', 'start.js')));
  assert.match(r.message.content, /not be on PATH/);
  assert.match(r.message.content, /compact --run/);
  // and the note is only in the session text, not in floor-only messages
  const floorOnly = boot({}).fire;
  const c2 = ctx();
  const r2 = await floorOnly('before_agent_start', { messageType: 'user', prompt: 'hi' }, c2);
  assert.equal(r2.message.content, 'FLOOR-TEXT');
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

// ---- pi-subagents children: a session whose parent session is live in this process ----

/** A parent and a child extension instance that share one set of live session files, as in one pi process. */
function family(over = {}) {
  const liveSessions = new Set();
  return { liveSessions, parent: boot({ liveSessions, ...over }), child: boot({ liveSessions, ...over }) };
}
const PROMPT = { messageType: 'user', prompt: 'hi' };

test('child: a session whose parent session file is live gets no hook work, no message, no notice and refusing commands', async () => {
  const { parent, child } = family();
  const pc = ctx({ file: '/p.jsonl', hasUI: true });
  await parent.fire('session_start', { reason: 'startup' }, pc);
  const cc = ctx({ file: '/c.jsonl', parent: '/p.jsonl', hasUI: true });
  child.calls.readFile.length = 0;
  assert.equal(await child.fire('session_start', { reason: 'startup' }, cc), undefined);
  assert.equal(await child.fire('session_compact', { reason: 'manual' }, cc), undefined);
  assert.equal(await child.fire('session_tree', {}, cc), undefined);
  assert.equal(await child.fire('before_agent_start', PROMPT, cc), undefined);
  assert.equal(child.calls.readFile.length, 0);
  assert.equal(child.calls.resolveEngine, 0);
  assert.equal(child.calls.runFloor.length, 0);
  assert.equal(child.calls.stamps.length, 0);
  assert.equal(child.calls.runWhatsNew.length, 0);
  assert.equal(child.pi.servers.length, 1); // the MCP server stays registered: a child may use the tools
  await child.pi.commands.get('total-recall').handler('help', cc);
  await child.pi.commands.get('develop').handler('123', cc);
  assert.equal(child.pi.sent.length, 0);
  assert.deepEqual(cc.notes.map((n) => n[1]), ['warning', 'warning']);
  // the parent itself still gets the instructions, and its notice
  const r = await parent.fire('before_agent_start', PROMPT, pc);
  assert.match(r.message.content, /total-recall/);
  assert.equal(parent.calls.runWhatsNew.length, 1);
});

/** A user session is "hooked" when its first prompt carries the instructions. */
async function getsInstructions(instance, c) {
  const r = await instance.fire('before_agent_start', PROMPT, c);
  return r !== undefined && /total-recall/.test(r.message.content);
}

test('replacement: a fork whose header names its parent still gets everything once the old session shut down', async () => {
  const liveSessions = new Set();
  const old = boot({ liveSessions });
  const oldCtx = ctx({ file: '/a.jsonl', hasUI: true });
  await old.fire('session_start', { reason: 'startup' }, oldCtx);
  await old.fire('session_shutdown', { reason: 'fork' }, oldCtx); // pi tears the old session down first
  const fork = boot({ liveSessions });
  const forkCtx = ctx({ file: '/b.jsonl', parent: '/a.jsonl', hasUI: true });
  await fork.fire('session_start', { reason: 'fork' }, forkCtx);
  assert.equal(await getsInstructions(fork, forkCtx), true);
  assert.equal(fork.calls.runWhatsNew.length, 1);
  await fork.pi.commands.get('develop').handler('123', forkCtx);
  assert.equal(fork.pi.sent.length, 1);
});

test('replacement: /new, /resume and /reload after the old session shut down stay hooked', async () => {
  const liveSessions = new Set();
  const first = boot({ liveSessions });
  const firstCtx = ctx({ file: '/a.jsonl' });
  await first.fire('session_start', { reason: 'startup' }, firstCtx);
  for (const [reason, file, parent] of [
    ['new', '/b.jsonl', undefined],
    ['resume', '/c.jsonl', undefined],
    ['reload', '/c.jsonl', undefined],
    ['resume', '/d.jsonl', '/gone.jsonl'], // an old session that was itself a fork of a closed one
  ]) {
    await first.fire('session_shutdown', { reason }, firstCtx);
    const next = boot({ liveSessions });
    const nextCtx = ctx({ file, parent });
    await next.fire('session_start', { reason }, nextCtx);
    assert.equal(await getsInstructions(next, nextCtx), true, reason);
    await next.fire('session_shutdown', { reason: 'quit' }, nextCtx);
  }
  assert.equal(liveSessions.size, 0);
});

test('child: a child stays skipped when it takes another prompt after its parent shut down', async () => {
  const { parent, child, liveSessions } = family();
  const pc = ctx({ file: '/p.jsonl' });
  await parent.fire('session_start', { reason: 'startup' }, pc);
  const cc = ctx({ file: '/c.jsonl', parent: '/p.jsonl' });
  await child.fire('session_start', { reason: 'startup' }, cc);
  await parent.fire('session_shutdown', { reason: 'new' }, pc);
  assert.equal(liveSessions.has('/p.jsonl'), false);
  assert.equal(await child.fire('before_agent_start', PROMPT, cc), undefined);
  assert.equal(child.calls.runFloor.length, 0);
});

test('shutdown: removes the file captured at session_start, even when ctx is stale or throws', async () => {
  const liveSessions = new Set();
  const a = boot({ liveSessions });
  await a.fire('session_start', { reason: 'startup' }, ctx({ file: '/a.jsonl' }));
  assert.deepEqual([...liveSessions], ['/a.jsonl']);
  assert.doesNotThrow(() => a.pi.handlers.get('session_shutdown')({ reason: 'quit' }, { get sessionManager() { throw new Error('stale ctx'); } }));
  assert.equal(liveSessions.size, 0);
  // and a shutdown with nothing recorded is harmless
  const b = boot({ liveSessions });
  assert.doesNotThrow(() => b.pi.handlers.get('session_shutdown')({ reason: 'quit' }, undefined));
});

test('child: a skipped child still counts as a parent, so its own persisted child is skipped too', async () => {
  const liveSessions = new Set();
  const root = boot({ liveSessions });
  const child = boot({ liveSessions });
  const grandchild = boot({ liveSessions });
  await root.fire('session_start', { reason: 'startup' }, ctx({ file: '/r.jsonl' }));
  const cc = ctx({ file: '/c.jsonl', parent: '/r.jsonl' });
  await child.fire('session_start', { reason: 'startup' }, cc);
  const gc = ctx({ file: '/g.jsonl', parent: '/c.jsonl', hasUI: true });
  assert.equal(await grandchild.fire('session_start', { reason: 'startup' }, gc), undefined);
  assert.equal(await grandchild.fire('before_agent_start', PROMPT, gc), undefined);
  assert.equal(grandchild.calls.runWhatsNew.length, 0);
});

for (const header of ['missing', 'throws', undefined]) {
  test(`child: a session manager whose header is ${header ?? 'not offered at all'} is the user's session and never breaks the start`, async () => {
    const liveSessions = new Set(['/p.jsonl']);
    const s = boot({ liveSessions });
    const c = ctx({ file: '/s.jsonl', header });
    assert.doesNotThrow(() => s.pi.handlers.get('session_start')({ reason: 'startup' }, c));
    assert.equal(await getsInstructions(s, c), true);
  });
}

test('child: a header with no parent, or a parent that is not live, is the user\'s session', async () => {
  const liveSessions = new Set(['/other.jsonl']);
  for (const parent of [undefined, '/closed.jsonl', '']) {
    const s = boot({ liveSessions });
    const c = ctx({ file: '/s.jsonl', parent, header: 'present' });
    await s.fire('session_start', { reason: 'startup' }, c);
    assert.equal(await getsInstructions(s, c), true, String(parent));
  }
});

test('child: PI_SUBAGENT_ID and a missing session file are still skipped when the live set is in play', async () => {
  const liveSessions = new Set(['/p.jsonl']);
  const marked = boot({ liveSessions, env: { PI_SUBAGENT_ID: 'x' } });
  const mc = ctx({ file: '/m.jsonl' });
  await marked.fire('session_start', { reason: 'startup' }, mc);
  assert.equal(await marked.fire('before_agent_start', PROMPT, mc), undefined);
  const ephemeral = boot({ liveSessions });
  const ec = ctx({ file: undefined });
  await ephemeral.fire('session_start', { reason: 'startup' }, ec);
  assert.equal(await ephemeral.fire('before_agent_start', PROMPT, ec), undefined);
  assert.deepEqual([...liveSessions].sort(), ['/m.jsonl', '/p.jsonl']); // an ephemeral session has no file to record
});

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

// ---- the what's-new notice ----

const tick = () => new Promise((resolve) => setImmediate(resolve));
const settle = async () => { for (let i = 0; i < 5; i += 1) await tick(); };

/** Collects unhandled rejections for the duration of fn. */
async function watchUnhandled(fn) {
  const seen = [];
  const on = (e) => seen.push(e);
  process.on('unhandledRejection', on);
  try {
    await fn();
    await settle();
  } finally {
    process.off('unhandledRejection', on);
  }
  return seen;
}

test('notice: shows the engine line once as info, runner gets host pi, text format and the followed server env', async () => {
  const { fire, calls } = boot({
    env: { HOME: '/home/u' },
    resolveUserServer: () => ({ skip: false, env: { TOTAL_RECALL_HOME: '/store' } }),
    runWhatsNew: async (opts) => { calls.runWhatsNew.push(opts); return 'total-recall 4.8.0: hello'; },
  });
  const c = ctx({ hasUI: true });
  assert.equal(await fire('session_start', { reason: 'startup' }, c), undefined);
  await settle();
  assert.deepEqual(c.notes, [['total-recall 4.8.0: hello', 'info']]);
  assert.deepEqual(calls.runWhatsNew, [{
    binaryPath: '/engine/total-recall.exe', host: 'pi', format: 'text', env: { TOTAL_RECALL_HOME: '/store' }, timeoutMs: 3000,
  }]);
});

test('notice: the stamp is written for host pi with the data dir of {env, user env} before the engine is looked up', async () => {
  const { fire, calls } = boot({
    env: { HOME: '/home/u', TOTAL_RECALL_HOME: '/ignored' },
    resolveUserServer: () => ({ skip: false, env: { TOTAL_RECALL_HOME: '/store' } }),
    resolveDataDir: (e) => { calls.dataDirEnvs.push(e); return '/data'; },
  });
  await fire('session_start', { reason: 'startup' }, ctx({ hasUI: true }));
  await settle();
  assert.deepEqual(calls.dataDirEnvs, [{ HOME: '/home/u', TOTAL_RECALL_HOME: '/store' }]);
  assert.deepEqual(calls.stamps, [{ dataDir: '/data', host: 'pi' }]);
  assert.deepEqual(calls.order, ['stamp', 'resolveEngine', 'runWhatsNew']);
});

test('notice: a missing engine still leaves the stamp and runs nothing', async () => {
  const { fire, calls } = boot({ resolveEngine: () => { calls.order.push('resolveEngine'); return null; } });
  const c = ctx({ hasUI: true });
  await fire('session_start', { reason: 'startup' }, c);
  await settle();
  assert.deepEqual(calls.order, ['stamp', 'resolveEngine']);
  assert.equal(calls.runWhatsNew.length, 0);
  assert.deepEqual(c.notes, []);
});

test('notice: the handler returns while the runner is still pending', async () => {
  const { fire, calls } = boot({ runWhatsNew: (opts) => { calls.runWhatsNew.push(opts); return new Promise(() => {}); } });
  const c = ctx({ hasUI: true });
  const r = fire('session_start', { reason: 'startup' }, c);
  const winner = await Promise.race([
    Promise.resolve(r).then(() => 'handler'),
    new Promise((resolve) => setTimeout(() => resolve('timeout'), 500)),
  ]);
  assert.equal(winner, 'handler');
  assert.equal(r, undefined);
  assert.equal(calls.runWhatsNew.length, 1);
  assert.deepEqual(c.notes, []);
});

for (const [label, over, c] of [
  ['hasUI false', {}, () => ctx({ hasUI: false })],
  ['hasUI absent', {}, () => { const x = ctx(); delete x.hasUI; return x; }],
  ['hasUI truthy but not true', {}, () => ctx({ hasUI: 1 })],
  ['a subagent child', { env: { PI_SUBAGENT_ID: 'c' } }, () => ctx({ hasUI: true })],
  ['an ephemeral session', {}, () => ctx({ hasUI: true, file: undefined })],
  ['a user server that says skip', { resolveUserServer: () => ({ skip: true, env: {} }) }, () => ctx({ hasUI: true })],
]) {
  test(`notice: ${label}: no stamp, no engine lookup, no run, no notify`, async () => {
    const { fire, calls } = boot({
      ...over,
      runWhatsNew: async (opts) => { calls.runWhatsNew.push(opts); return 'LINE'; },
    });
    const cx = c();
    await fire('session_start', { reason: 'startup' }, cx);
    await settle();
    assert.deepEqual(calls.stamps, []);
    assert.equal(calls.resolveEngine, 0);
    assert.equal(calls.runWhatsNew.length, 0);
    assert.deepEqual(cx.notes, []);
  });
}

for (const branch of [[], [delivered]]) {
  test(`notice: reload never shows or stamps (context message ${branch.length ? 'delivered' : 'not delivered'})`, async () => {
    const { fire, calls } = boot({ runWhatsNew: async (opts) => { calls.runWhatsNew.push(opts); return 'LINE'; } });
    const c = ctx({ hasUI: true, branch });
    await fire('session_start', { reason: 'reload' }, c);
    await settle();
    assert.deepEqual(calls.stamps, []);
    assert.equal(calls.resolveEngine, 0);
    assert.equal(calls.runWhatsNew.length, 0);
    assert.deepEqual(c.notes, []);
  });
}

for (const reason of ['startup', 'new', 'resume', 'fork']) {
  test(`notice: reason ${reason} takes the notice path`, async () => {
    const { fire, calls } = boot({ runWhatsNew: async (opts) => { calls.runWhatsNew.push(opts); return 'LINE'; } });
    const c = ctx({ hasUI: true });
    await fire('session_start', { reason }, c);
    await settle();
    assert.equal(calls.stamps.length, 1);
    assert.deepEqual(c.notes, [['LINE', 'info']]);
  });
}

test('notice: an unresolvable data dir writes no stamp and neither looks up nor runs the engine', async () => {
  const { fire, calls } = boot({ resolveDataDir: () => null });
  const c = ctx({ hasUI: true });
  await fire('session_start', { reason: 'startup' }, c);
  await settle();
  assert.deepEqual(calls.stamps, []);
  assert.equal(calls.resolveEngine, 0);
  assert.equal(calls.runWhatsNew.length, 0);
  assert.deepEqual(c.notes, []);
});

for (const [label, text] of [
  ['a multi-line text', 'line one\nline two'],
  ['an over-long text', 'x'.repeat(1001)],
  ['a text with a control character', 'a\u001b[31mred'],
  ['a text with U+2028', 'a\u2028b'],
]) {
  test(`notice: ${label} from the runner is not passed to notify`, async () => {
    const { fire } = boot({ runWhatsNew: async () => text });
    const c = ctx({ hasUI: true });
    await fire('session_start', { reason: 'startup' }, c);
    await settle();
    assert.deepEqual(c.notes, []);
  });
}

for (const value of [undefined, '']) {
  test(`notice: a runner resolving ${JSON.stringify(value)} shows nothing`, async () => {
    const { fire } = boot({ runWhatsNew: async () => value });
    const c = ctx({ hasUI: true });
    await fire('session_start', { reason: 'startup' }, c);
    await settle();
    assert.deepEqual(c.notes, []);
  });
}

test('notice: a throwing notify (stale ctx) neither escapes nor leaves an unhandled rejection', async () => {
  const { fire } = boot({ runWhatsNew: async () => 'LINE' });
  const c = ctx({ hasUI: true });
  c.ui.notify = () => { throw new Error('stale ctx'); };
  const seen = await watchUnhandled(async () => {
    assert.equal(await fire('session_start', { reason: 'startup' }, c), undefined);
  });
  assert.deepEqual(seen, []);
});

test('notice: a rejecting runner neither escapes nor leaves an unhandled rejection', async () => {
  const { fire } = boot({ runWhatsNew: async () => { throw new Error('spawn'); } });
  const c = ctx({ hasUI: true });
  const seen = await watchUnhandled(async () => {
    assert.equal(await fire('session_start', { reason: 'startup' }, c), undefined);
  });
  assert.deepEqual(seen, []);
  assert.deepEqual(c.notes, []);
});

test('notice: a runner throwing synchronously does not escape', async () => {
  const { fire } = boot({ runWhatsNew: () => { throw new Error('sync'); } });
  assert.equal(await fire('session_start', { reason: 'startup' }, ctx({ hasUI: true })), undefined);
});

for (const dep of ['resolveUserServer', 'resolveEngine', 'writeDirectStamp', 'resolveDataDir']) {
  test(`notice: a throwing ${dep} does not escape and the session text is still queued`, async () => {
    const { fire } = boot({ [dep]: () => { throw new Error('boom'); } });
    const c = ctx({ hasUI: true });
    assert.equal(await fire('session_start', { reason: 'startup' }, c), undefined);
    await settle();
    assert.deepEqual(c.notes, []);
    const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
    assert.ok(r.message.content.includes(SKILL_BODY_MARK));
  });
}

test('notice: an extension built without resolveUserServer or the notice deps still works with a UI', async () => {
  const handlers = new Map();
  const pi = { on: (e, f) => handlers.set(e, f), registerMcpServer() {}, registerCommand() {}, sendUserMessage() {} };
  createExtension({ rootDir: root, env: {}, readFile, resolveEngine: () => null, runFloor: async () => undefined })(pi);
  const c = ctx({ hasUI: true });
  assert.equal(await handlers.get('session_start')({ reason: 'startup' }, c), undefined);
  await settle();
  assert.deepEqual(c.notes, []);
});

test('notice: a missing resolveUserServer counts as no followed server', async () => {
  const { fire, calls } = boot({ resolveUserServer: undefined, runWhatsNew: async (o) => { calls.runWhatsNew.push(o); return 'LINE'; } });
  const c = ctx({ hasUI: true });
  await fire('session_start', { reason: 'startup' }, c);
  await settle();
  assert.deepEqual(c.notes, [['LINE', 'info']]);
  assert.deepEqual(calls.runWhatsNew[0].env, {});
});

test('notice: the session text is queued and delivered as before alongside the notice', async () => {
  const { fire } = boot({ runWhatsNew: async () => 'LINE' });
  const c = ctx({ hasUI: true });
  await fire('session_start', { reason: 'startup' }, c);
  const r = await fire('before_agent_start', { messageType: 'user', prompt: 'hi' }, c);
  assert.ok(r.message.content.includes(SKILL_BODY_MARK));
  assert.ok(!r.message.content.includes('LINE'));
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

test('default export: the real notice deps write the direct-pi stamp (child process, temp data dir)', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-pi-default-'));
  try {
    const script = `
      import ext from ${JSON.stringify(pathToFileURL(path.join(root, 'pi', 'extension.js')).href)};
      const handlers = new Map();
      ext({ on: (e, f) => handlers.set(e, f), registerMcpServer() {}, registerCommand() {}, sendUserMessage() {} });
      const ctx = { cwd: ${JSON.stringify(tmp)}, hasUI: true, ui: { notify() {} },
        sessionManager: { getSessionId: () => 's', getSessionFile: () => 'f', getBranch: () => [] } };
      await handlers.get('session_start')({ reason: 'startup' }, ctx);
    `;
    const res = spawnSync(process.execPath, ['--input-type=module', '-e', script], {
      env: {
        PATH: process.env.PATH,
        HOME: tmp, USERPROFILE: tmp,
        TOTAL_RECALL_HOME: path.join(tmp, 'data'),
        PI_CODING_AGENT_DIR: path.join(tmp, 'agent'),
      },
      timeout: 20000,
      encoding: 'utf8',
    });
    assert.equal(res.status, 0, res.stderr);
    const stamp = JSON.parse(fs.readFileSync(path.join(tmp, 'data', 'whats-new', 'direct-pi.json'), 'utf8'));
    assert.ok(!Number.isNaN(Date.parse(stamp.at)));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});
