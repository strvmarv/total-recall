import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import {
  normalize,
  bodyHash,
  regionHash,
  outsideFences,
  extractRegion,
  approachLimits,
  neutralize,
  fenceFor,
  SUBCOMMANDS,
} from './regions.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HANDOFF = path.join(HERE, '..', 'handoff.md');
const REGIONS = path.join(HERE, 'regions.js');

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'regions-test-'));
}

function writeFile(content) {
  const dir = tmpDir();
  const file = path.join(dir, 'file.txt');
  fs.writeFileSync(file, content);
  return file;
}

function run(args) {
  const result = spawnSync(process.execPath, [REGIONS, ...args], { encoding: 'utf8' });
  let parsed;
  try {
    parsed = JSON.parse(result.stdout);
  } catch {
    parsed = undefined;
  }
  return { ...result, parsed };
}

// ---- normalize-pairs ----

test('normalize-pairs golden: regionHash(a) === regionHash(b) matches `same`', () => {
  const cases = readGolden(HANDOFF, 'normalize-pairs');
  for (const c of cases) {
    assert.equal(regionHash(c.a) === regionHash(c.b), c.same, c.name);
  }
});

// ---- body-pairs ----

test('body-pairs golden: bodyHash(t1,b1) === bodyHash(t2,b2) matches `same`', () => {
  const cases = readGolden(HANDOFF, 'body-pairs');
  for (const c of cases) {
    assert.equal(bodyHash(c.t1, c.b1) === bodyHash(c.t2, c.b2), c.same, c.name);
  }
});

// ---- region-cases ----

test('region-cases golden: extractRegion(body, kind) matches `expect`', () => {
  const cases = readGolden(HANDOFF, 'region-cases');
  for (const c of cases) {
    assert.deepEqual(extractRegion(c.body, c.kind), c.expect, c.name);
  }
});

test('region-cases (RF1): a fenced json block inside the criteria region is in the text and changes regionHash when edited', () => {
  const cases = readGolden(HANDOFF, 'region-cases');
  const v1 = cases.find((c) => c.name === 'json-fence-v1');
  const v2 = cases.find((c) => c.name === 'json-fence-v2');
  const r1 = extractRegion(v1.body, v1.kind);
  const r2 = extractRegion(v2.body, v2.kind);
  assert.ok(r1.ok && r2.ok);
  assert.match(r1.text, /```json/);
  assert.match(r1.text, /"retries": 3/);
  assert.notEqual(regionHash(r1.text), regionHash(r2.text));
});

test('region-cases (RF4): a fenced block inside the approach region that pushes it over 60 lines fails approachLimits', () => {
  const cases = readGolden(HANDOFF, 'region-cases');
  const c = cases.find((c) => c.name === 'approach-over-limit');
  const r = extractRegion(c.body, c.kind);
  assert.ok(r.ok);
  const limits = approachLimits(r.text);
  assert.equal(limits.ok, false);
  assert.ok(limits.lines > 60);
});

// ---- fence-cases ----

test('fence-cases golden: outsideFences(text) matches the expected [n, line] pairs', () => {
  const cases = readGolden(HANDOFF, 'fence-cases');
  for (const c of cases) {
    const actual = outsideFences(c.text).map((l) => [l.n, l.line]);
    assert.deepEqual(actual, c.lines, c.name);
  }
});

// ---- limit-cases ----

test('limit-cases golden: approachLimits(text).ok matches `ok`', () => {
  const cases = readGolden(HANDOFF, 'limit-cases');
  for (const c of cases) {
    assert.equal(approachLimits(c.text).ok, c.ok, c.name);
  }
});

// ---- neutralize-cases ----

test('neutralize-cases golden: neutralize(in) equals `out`', () => {
  const cases = readGolden(HANDOFF, 'neutralize-cases');
  for (const c of cases) {
    assert.equal(neutralize(c.in), c.out, c.name);
  }
});

// ---- fenceFor ----

test('fenceFor: empty text returns three backticks', () => {
  assert.equal(fenceFor(''), '```');
});

test('fenceFor: a run of four backticks returns five', () => {
  assert.equal(fenceFor('a ```` b'), '`````');
});

// ---- normalize (unit, beyond the golden pairs) ----

test('normalize: CRLF becomes LF, trailing whitespace and trailing blank lines are stripped', () => {
  assert.equal(normalize('a  \r\nb\r\n\r\n'), 'a\nb');
});

// ---- SUBCOMMANDS dispatcher ----

test('SUBCOMMANDS dispatcher: every listed name dispatches, an unlisted word is rejected', () => {
  for (const name of SUBCOMMANDS) {
    const { parsed } = run([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
  }
  const bad = run(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
});

// ---- CLI: body-json ----

test('CLI body-json: a REST-shaped issue with CRLF, non-ASCII and a null body round-trips byte-exact', () => {
  const dir = tmpDir();
  const issueFile = path.join(dir, 'issue.json');
  const titleFile = path.join(dir, 'title.txt');
  const bodyFile = path.join(dir, 'body.txt');
  const title = 'Fix caf\u00e9 bug\r\nsecond line';
  fs.writeFileSync(issueFile, JSON.stringify({ title, body: null }));
  const { parsed, status } = run(['body-json', issueFile, titleFile, bodyFile]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { ok: true });
  assert.equal(fs.readFileSync(titleFile, 'utf8'), title);
  assert.equal(fs.readFileSync(bodyFile, 'utf8'), '');
  const titleBuf = fs.readFileSync(titleFile);
  assert.notEqual(titleBuf[0], 0xef); // no UTF-8 BOM
});

// ---- CLI: body ----

test('CLI body: prints a hash equal to bodyHash of the same title and body files', () => {
  const dir = tmpDir();
  const titleFile = path.join(dir, 'title.txt');
  const bodyFile = path.join(dir, 'body.txt');
  fs.writeFileSync(titleFile, 'Fix login');
  fs.writeFileSync(bodyFile, 'Crash on retry.\r\n');
  const { parsed, status } = run(['body', titleFile, bodyFile]);
  assert.equal(status, 0);
  assert.equal(parsed.ok, true);
  assert.equal(parsed.hash, bodyHash('Fix login', 'Crash on retry.\r\n'));
});

// ---- CLI: ac ----

test('CLI ac: ok writes the region text to outFile and prints its hash', () => {
  const body = 'Intro\n\n<!-- tr-develop:ac-begin -->\n- [ ] one\n<!-- tr-develop:ac-end -->\n\nTrailer\n';
  const dir = tmpDir();
  const bodyFile = path.join(dir, 'body.txt');
  const outFile = path.join(dir, 'out.txt');
  fs.writeFileSync(bodyFile, body);
  const { parsed, status } = run(['ac', bodyFile, outFile]);
  assert.equal(status, 0);
  const expected = extractRegion(body, 'ac');
  assert.equal(parsed.ok, true);
  assert.equal(parsed.hash, regionHash(expected.text));
  assert.equal(fs.readFileSync(outFile, 'utf8'), expected.text);
});

test('CLI ac: a missing marker prints {ok:false, error} and exits 1', () => {
  const body = 'Intro\n\nTrailer\n';
  const bodyFile = writeFile(body);
  const { parsed, status } = run(['ac', bodyFile]);
  assert.equal(status, 1);
  assert.deepEqual(parsed, { ok: false, error: 'missing' });
});

// ---- CLI: approach ----

test('CLI approach: over the limit prints {ok:false, error:"too-long", lines, chars} and exits 1', () => {
  const cases = readGolden(HANDOFF, 'region-cases');
  const c = cases.find((c) => c.name === 'approach-over-limit');
  const bodyFile = writeFile(c.body);
  const { parsed, status } = run(['approach', bodyFile]);
  assert.equal(status, 1);
  assert.equal(parsed.ok, false);
  assert.equal(parsed.error, 'too-long');
  assert.ok(parsed.lines > 60);
  assert.ok(typeof parsed.chars === 'number');
});

// ---- CLI: neutralize ----

test('CLI neutralize: prints {text} equal to neutralize(text)', () => {
  const text = '<!-- tr-develop:ac-begin -->\ntr-develop/v1 phase=kickoff\n';
  const textFile = writeFile(text);
  const { parsed, status } = run(['neutralize', textFile]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { text: neutralize(text) });
});

// ---- CLI: fence ----

test('CLI fence: prints {fence} equal to fenceFor(text)', () => {
  const text = 'a ```` b';
  const textFile = writeFile(text);
  const { parsed, status } = run(['fence', textFile]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, { fence: '`````' });
});
