// scripts/release-workflow.test.js — guards the supply-chain steps of .github/workflows/release.yml.
//
// There is no YAML parser (the repo has no dependencies for this), so the workflow is read as text.
// Every check is a function of the workflow text, so a test can also run it on a copy with one
// deliberate defect and see it fail.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { fileURLToPath } from 'node:url';

const REAL = fs.readFileSync(fileURLToPath(new URL('../.github/workflows/release.yml', import.meta.url)), 'utf8');

/** The body of a top-level job, or null. */
function jobBlock(text, name) {
  const m = new RegExp(`^  ${name}:\\n([\\s\\S]*?)(?=^  [a-z][a-z-]*:\\n|(?![\\s\\S]))`, 'm').exec(text);
  return m ? m[1] : null;
}

/** The job's own `permissions:` mapping (job level, 4-space indent), as a list of `key: value` strings. */
function jobPermissions(job) {
  const m = /^    permissions:\n((?:      [a-z-]+: \w+\n)+)/m.exec(job);
  return m ? m[1].split('\n').map((l) => l.trim()).filter(Boolean) : [];
}

// ---- Cycle 1: the publish job attests the release assets ----

function checkAttestStepExists(text) {
  const problems = [];
  const publish = jobBlock(text, 'publish');
  if (!publish) return ['release.yml has no publish job'];
  if (!/uses: actions\/attest@v4\b/.test(publish)) problems.push('the publish job has no actions/attest@v4 step');
  if (!jobPermissions(publish).includes('attestations: write')) problems.push('the publish job does not have attestations: write');
  return problems;
}

test('release.yml: the publish job attests the release assets and may write attestations', () => {
  assert.deepEqual(checkAttestStepExists(REAL), []);
});
