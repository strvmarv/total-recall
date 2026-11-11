import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import { parseArg, SUBCOMMANDS } from './args.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SKILL = path.join(HERE, '..', 'SKILL.md');
const ARGS = path.join(HERE, 'args.js');
const ORIGIN = 'strvmarv/total-recall';

const ROUTING = readGolden(SKILL, 'routing');

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'args-test-'));
}

function run(args) {
  const result = spawnSync(process.execPath, [ARGS, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

/** Writes `text` byte-exact to a fresh file and returns its path. */
function writeText(text) {
  const file = path.join(tmpDir(), 'arg.txt');
  fs.writeFileSync(file, text);
  return file;
}

// ---- parseArg: the routing golden ----

test('parseArg: every pair in the routing golden gives its expected route', () => {
  assert.ok(ROUTING.length > 0);
  for (const [input, expected] of ROUTING) {
    assert.deepEqual(parseArg(input, ORIGIN), expected, JSON.stringify(input));
  }
});

test('parseArg: a number of ten or more digits is define, never a rounded issue', () => {
  assert.deepEqual(parseArg('1234567890', ORIGIN), { route: 'define', seed: '1234567890' });
});

test('parseArg: the issue number is a number, and the repository compare ignores case', () => {
  assert.deepEqual(parseArg('STRVMARV/Total-Recall#42', ORIGIN), { route: 'issue', number: 42 });
  assert.deepEqual(parseArg('https://github.com/STRVMARV/TOTAL-RECALL/issues/42', ORIGIN), { route: 'issue', number: 42 });
});

test('parseArg: the wrong-repo result names the repository as written', () => {
  assert.deepEqual(parseArg('Other/Repo#9', ORIGIN), { route: 'wrong-repo', repo: 'Other/Repo', number: 9 });
});

// ---- CLI: parse ----

test('CLI parse: prints the parseArg result for a text file', () => {
  const { parsed, status } = run(['parse', ORIGIN, writeText('strvmarv/total-recall#9\n')]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { route: 'issue', number: 9 });
});

test('CLI parse: CRLF and surrounding whitespace are trimmed, and a BOM is ignored', () => {
  const { parsed, status } = run(['parse', ORIGIN, writeText('\uFEFF  123\r\n')]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { route: 'issue', number: 123 });
});

test('CLI parse: an empty file is the tutorial', () => {
  const { parsed, status } = run(['parse', ORIGIN, writeText('')]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { route: 'tutorial' });
});

test('CLI parse: a missing argument is a usage error with exit 1', () => {
  const { parsed, status } = run(['parse', ORIGIN]);
  assert.equal(status, 1);
  assert.equal(parsed.ok, false);
  assert.match(parsed.error, /^usage: parse/);
});

// ---- SUBCOMMANDS dispatcher ----

test('SUBCOMMANDS dispatcher: every listed name dispatches, an unlisted word is rejected', () => {
  assert.deepEqual(SUBCOMMANDS, ['parse']);
  for (const name of SUBCOMMANDS) {
    const { parsed } = run([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
  }
  const bad = run(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
  assert.deepEqual(run([]).parsed, { ok: false, error: 'unknown subcommand' });
});
