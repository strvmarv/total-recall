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

test('sanitizeSummary never returns more than 200 characters or a line break', () => {
  for (const c of SANITISER_CASES) {
    const out = sanitizeSummary(c.input);
    assert.ok(out.length <= 200, c.name);
    assert.ok(!/[\r\n]/.test(out), c.name);
  }
});

test('SHIM_TEST_TARGETS runs scripts/release-notes.test.js', () => {
  assert.ok(SHIM_TEST_TARGETS.includes('scripts/release-notes.test.js'));
});
