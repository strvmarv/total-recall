import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { resolveUserServer } from './userServer.js';

const HOME = path.join(path.sep, 'home', 'u');
const CWD = path.join(path.sep, 'work', 'proj');
const USER_FILE = path.join(HOME, '.pi', 'agent', 'mcp.json');
const PROJECT_FILE = path.join(CWD, '.pi', 'mcp.json');

function run({ files = {}, env = {} } = {}) {
  const read = (p) => {
    const hit = files[path.normalize(p)];
    if (hit === undefined) { const e = new Error('ENOENT'); e.code = 'ENOENT'; throw e; }
    return typeof hit === 'string' ? hit : JSON.stringify(hit);
  };
  return resolveUserServer({ env, cwd: CWD, homedir: HOME, readFile: read });
}
const entry = (e, name = 'total-recall') => ({ mcpServers: { [name]: e } });

test('no mcp.json anywhere: nothing to match, floor runs with pi\'s own environment', () => {
  assert.deepEqual(run(), { skip: false, env: {} });
});

test('a user entry with TOTAL_RECALL_* env: exactly those plain values are passed to the floor', () => {
  const files = { [USER_FILE]: entry({ command: 'total-recall', env: { TOTAL_RECALL_HOME: 'D:/store', TOTAL_RECALL_DB_PATH: 'D:/x.db', OTHER: 'y' } }) };
  assert.deepEqual(run({ files }), { skip: false, env: { TOTAL_RECALL_HOME: 'D:/store', TOTAL_RECALL_DB_PATH: 'D:/x.db' } });
});

test('server names match the way pi matches them (- and _ are the same)', () => {
  for (const name of ['total_recall', 'total-recall']) {
    const files = { [USER_FILE]: entry({ command: 'x', env: { TOTAL_RECALL_HOME: 'D:/s' } }, name) };
    assert.deepEqual(run({ files }).env, { TOTAL_RECALL_HOME: 'D:/s' }, name);
  }
});

test('a dynamic value (${VAR} or !command) cannot be matched safely: the floor is skipped', () => {
  for (const v of ['${HOME}/tr', '!echo /tmp/tr', 'pre-${X}']) {
    const files = { [USER_FILE]: entry({ command: 'x', env: { TOTAL_RECALL_HOME: v } }) };
    assert.deepEqual(run({ files }), { skip: true, env: {} }, v);
  }
});

test('a remote (url) or disabled entry means the local default store is unrelated: the floor is skipped', () => {
  assert.equal(run({ files: { [USER_FILE]: entry({ url: 'https://example.com/mcp' }) } }).skip, true);
  assert.equal(run({ files: { [USER_FILE]: entry({ command: 'x', enabled: false }) } }).skip, true);
});

test('an entry without env changes nothing', () => {
  assert.deepEqual(run({ files: { [USER_FILE]: entry({ command: 'total-recall' }) } }), { skip: false, env: {} });
});

test('other servers in the file are ignored', () => {
  const files = { [USER_FILE]: entry({ command: 'x', env: { TOTAL_RECALL_HOME: 'D:/other' } }, 'something-else') };
  assert.deepEqual(run({ files }), { skip: false, env: {} });
});

test('a project entry replaces the user entry; an override-only project entry keeps the user env', () => {
  const user = entry({ command: 'x', env: { TOTAL_RECALL_HOME: 'D:/user' } });
  assert.deepEqual(
    run({ files: { [USER_FILE]: user, [PROJECT_FILE]: entry({ command: 'y', env: { TOTAL_RECALL_HOME: 'D:/proj' } }) } }).env,
    { TOTAL_RECALL_HOME: 'D:/proj' },
  );
  assert.deepEqual(
    run({ files: { [USER_FILE]: user, [PROJECT_FILE]: entry({ exposure: 'direct' }) } }).env,
    { TOTAL_RECALL_HOME: 'D:/user' },
  );
});

test('PI_CODING_AGENT_DIR moves the user mcp.json', () => {
  const dir = path.join(path.sep, 'custom', 'agent');
  const files = { [path.join(dir, 'mcp.json')]: entry({ command: 'x', env: { TOTAL_RECALL_HOME: 'D:/c' } }) };
  assert.deepEqual(run({ files, env: { PI_CODING_AGENT_DIR: dir } }).env, { TOTAL_RECALL_HOME: 'D:/c' });
});

test('malformed or unreadable files never throw and never skip', () => {
  assert.deepEqual(run({ files: { [USER_FILE]: '{ not json' } }), { skip: false, env: {} });
  assert.deepEqual(run({ files: { [USER_FILE]: '[]' } }), { skip: false, env: {} });
  assert.deepEqual(run({ files: { [USER_FILE]: JSON.stringify({ mcpServers: null }) } }), { skip: false, env: {} });
});
