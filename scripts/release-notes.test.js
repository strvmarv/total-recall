// scripts/release-notes.test.js — tests for scripts/release-notes.js (the release notes pipeline).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { sanitizeSummary } from './release-notes.js';
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
    assert.ok(!/[\u0000-\u0008\u000e-\u001f\u007f-\u0084\u0086-\u009f]/.test(out), 'no control character');
    assert.ok(!/\[[^\]]*\]\([^)]*\)/.test(out), 'no markdown link left');
    assert.ok(!/[\ud800-\udbff]$/.test(out), 'no lone high surrogate at the end');
  }
});

test('SHIM_TEST_TARGETS runs scripts/release-notes.test.js', () => {
  assert.ok(SHIM_TEST_TARGETS.includes('scripts/release-notes.test.js'));
});
