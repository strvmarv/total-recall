// scripts/release-notes.test.js — tests for scripts/release-notes.js (the release notes pipeline).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { sanitizeSummary, parseTagMessage } from './release-notes.js';
import { SHIM_TEST_TARGETS } from './run-shim-tests.js';

const here = (rel) => fileURLToPath(new URL(rel, import.meta.url));

// ---- Task 1: the summary sanitiser and its shared fixture ----

const SANITISER_CASES = JSON.parse(fs.readFileSync(here('./fixtures/summary-sanitiser.json'), 'utf8'));

test('sanitizeSummary matches every case in scripts/fixtures/summary-sanitiser.json', () => {
  assert.ok(SANITISER_CASES.length >= 12);
  for (const c of SANITISER_CASES) {
    assert.equal(sanitizeSummary(c.input), c.expected, c.name);
  }
});

test('sanitizeSummary keeps its invariants on hostile input', () => {
  const hostile = [
    '[[[a](b)](c)](d)', '[a](b', '\u001b', '\u001b[', '\u001b[1;31', '<<https://x.y>>', '\ufeff\ufeff x',
    'x'.repeat(199) + '\ud83d\ude00' + 'y'.repeat(50), '[' + 'a'.repeat(300) + '](u)', '\u0000'.repeat(500),
    'word '.repeat(100), '\r\n'.repeat(120), '[x](y)'.repeat(80),
  ];
  for (const input of hostile) {
    const out = sanitizeSummary(input);
    assert.ok(out.length <= 200, `length: ${JSON.stringify(input).slice(0, 30)}`);
    assert.ok(!/[\r\n]/.test(out), 'no line break');
    assert.ok(!/^ | $/.test(out), 'trimmed');
    assert.ok(!/[\u0000-\u0008\u000e-\u001f\u007f-\u0084\u0086-\u009f]/.test(out), 'no control character');
    assert.ok(!/\[[^\]]*\]\([^)]*\)/.test(out), 'no markdown link left');
    assert.ok(!/[\ud800-\udbff]$/.test(out), 'no lone high surrogate at the end');
  }
});

test('SHIM_TEST_TARGETS runs scripts/release-notes.test.js', () => {
  assert.ok(SHIM_TEST_TARGETS.includes('scripts/release-notes.test.js'));
});

// ---- Task 2: the tag message parser, on the real messages ----

const tagMessage = (tag) => fs.readFileSync(here(`./fixtures/tag-messages/${tag}.txt`), 'utf8');
const SECTION_KEYS = ['added', 'changed', 'fixed'];
const noSections = { added: [], changed: [], fixed: [] };

test('parseTagMessage: v4.2.1 (a title only) has no summary and no sections, and does not throw', () => {
  const p = parseTagMessage(tagMessage('v4.2.1'));
  assert.equal(p.summary, '');
  assert.deepEqual(p.sections, noSections);
});

test('parseTagMessage: v4.3.0 to v4.5.0 (bare title, headingless bullets) give a summary and no sections', () => {
  for (const tag of ['v4.3.0', 'v4.4.0', 'v4.5.0']) {
    const p = parseTagMessage(tagMessage(tag));
    assert.ok(p.summary.length > 0, tag);
    assert.deepEqual(p.sections, noSections, tag);
  }
  assert.equal(parseTagMessage(tagMessage('v4.4.0')).summary, 'The develop skill. No engine (C#) changes.');
});

test('parseTagMessage: v4.6.0, v4.7.0 and the betas give a summary and their Added/Changed/Fixed bullets', () => {
  const v460 = parseTagMessage(tagMessage('v4.6.0'));
  assert.ok(v460.summary.length > 0 && v460.sections.added.length > 0 && v460.sections.fixed.length > 0);
  const v470 = parseTagMessage(tagMessage('v4.7.0'));
  assert.match(v470.summary, /^A release version bump is now one checked command/);
  assert.equal(v470.sections.added.length, 3);
  assert.equal(v470.sections.changed.length, 1);
  assert.equal(v470.sections.fixed.length, 1);
  const beta1 = parseTagMessage(tagMessage('v4.7.0-beta.1'));
  assert.ok(beta1.summary.length > 0 && beta1.sections.added.length > 0);
});

test('parseTagMessage: a heading with a suffix opens its section (v4.7.0-beta.2: "Added (from 4.7.0-beta.1)")', () => {
  const p = parseTagMessage(tagMessage('v4.7.0-beta.2'));
  assert.equal(p.sections.added.length, 3);
  assert.equal(p.sections.fixed.length, 1);
});

test('parseTagMessage: no trailing "Observed on" or "Not yet observed" line reaches a section in any fixture', () => {
  for (const tag of ['v4.2.1', 'v4.3.0', 'v4.4.0', 'v4.5.0', 'v4.6.0', 'v4.7.0-beta.1', 'v4.7.0-beta.2', 'v4.7.0']) {
    const p = parseTagMessage(tagMessage(tag));
    for (const key of SECTION_KEYS) {
      for (const bullet of p.sections[key]) assert.doesNotMatch(bullet, /^(Observed on|Not yet observed)/, tag);
    }
  }
});

test('parseTagMessage: CRLF text parses like LF text', () => {
  const p = parseTagMessage('v1.0.0\r\n\r\nHello  there.\r\n');
  assert.equal(p.summary, 'Hello there.');
  assert.equal(p.title, 'v1.0.0');
});

test('parseTagMessage: the summary is not read as a heading even when it starts with "Fixed"', () => {
  const p = parseTagMessage('v1.0.0\n\nFixed the release workflow.\n\nAdded\n- one\n');
  assert.equal(p.summary, 'Fixed the release workflow.');
  assert.deepEqual(p.sections.added, ['one']);
  assert.deepEqual(p.sections.fixed, []);
});

test('parseTagMessage: markdown headings, other bullet markers and wrapped bullets', () => {
  const p = parseTagMessage('v1.0.0\n\nSummary.\n\n### Added\n* first\n  wrapped on\n+ second\n\nOther heading\n- ignored\n');
  assert.deepEqual(p.sections.added, ['first wrapped on', 'second']);
  assert.deepEqual(p.sections.changed, []);
});

test('parseTagMessage: a first paragraph that is a heading or a bullet is not a summary', () => {
  assert.equal(parseTagMessage('v1.0.0\n\nAdded\n- x\n').summary, '');
  assert.equal(parseTagMessage('v1.0.0\n\n- just a bullet\n').summary, '');
  assert.equal(parseTagMessage('v1.0.0\n').summary, '');
  assert.equal(parseTagMessage('').title, '');
});

test('parseTagMessage: body is the message without the title line and the blank line after it', () => {
  assert.equal(parseTagMessage('v1.0.0\n\nSummary.\n\nAdded\n- x\n').body, 'Summary.\n\nAdded\n- x\n');
  assert.equal(parseTagMessage(tagMessage('v4.7.0')).body, tagMessage('v4.7.0').split('\n').slice(2).join('\n'));
});
