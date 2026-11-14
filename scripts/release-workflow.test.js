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

// ---- Cycle 2: the attestation gates the release, and cannot be skipped ----

const STEP_START = /^      - name: /m;

/** The publish job's steps as [{name, text}] in order. */
function publishSteps(text) {
  const publish = jobBlock(text, 'publish');
  if (!publish) return [];
  const i = publish.indexOf('\n    steps:\n');
  if (i < 0) return [];
  const body = publish.slice(i + '\n    steps:\n'.length);
  return body.split(new RegExp(`(?=${STEP_START.source})`, 'm')).filter((c) => STEP_START.test(c)).map((c) => ({
    name: /^      - name: (.*)$/m.exec(c)[1].trim(),
    text: c,
  }));
}

const GENERATE = 'Generate provisioning manifest';
const ATTACH = 'Attach archives to GitHub release';

function checkAttestOrdering(text) {
  const problems = [];
  const steps = publishSteps(text);
  const at = (pred) => steps.findIndex(pred);
  const generate = at((s) => s.name === GENERATE);
  const attach = at((s) => s.name === ATTACH);
  const attest = at((s) => /uses: actions\/attest@v4\b/.test(s.text));
  if (generate < 0) problems.push(`no "${GENERATE}" step`);
  if (attach < 0) problems.push(`no "${ATTACH}" step`);
  if (attest < 0) return [...problems, 'no actions/attest@v4 step'];
  if (generate >= 0 && attest < generate) problems.push('the attestation runs before the manifest is generated');
  if (attach >= 0 && attest > attach) problems.push('the attestation runs after the assets are attached');
  const body = steps[attest].text;
  if (/^\s+if:/m.test(body)) problems.push('the attestation step has an if: condition and can be skipped');
  if (/^\s+continue-on-error:/m.test(body)) problems.push('the attestation step sets continue-on-error, so its failure is ignored');
  return problems;
}

/** A copy of the workflow with the attest step moved to the end of the (last) publish job. */
function moveAttestToEnd(text) {
  const step = publishSteps(text).find((s) => /uses: actions\/attest@v4\b/.test(s.text)).text;
  return text.replace(step, '').replace(/\n*$/, '\n\n') + step;
}

/** A copy of the workflow with `line` added to the attest step's `with:` neighbour (right under its `uses:`). */
function addToAttestStep(text, line) {
  return text.replace(/(        uses: actions\/attest@v4\n)/, `$1        ${line}\n`);
}

test('release.yml: the attestation runs after the manifest and before the assets are attached, and cannot be skipped', () => {
  assert.deepEqual(checkAttestOrdering(REAL), []);
});

test('mutation: an attestation moved after the attach step is caught', () => {
  assert.ok(checkAttestOrdering(moveAttestToEnd(REAL)).some((p) => /after the assets are attached/.test(p)));
});

test('mutation: continue-on-error on the attestation step is caught', () => {
  assert.ok(checkAttestOrdering(addToAttestStep(REAL, 'continue-on-error: true')).some((p) => /continue-on-error/.test(p)));
});

test('mutation: an if: condition on the attestation step is caught', () => {
  assert.ok(checkAttestOrdering(addToAttestStep(REAL, 'if: always()')).some((p) => /if: condition/.test(p)));
});

test('mutation: an attestation before the manifest is generated is caught', () => {
  const step = publishSteps(REAL).find((s) => /uses: actions\/attest@v4\b/.test(s.text)).text;
  const early = REAL.replace(step, '').replace(/(      - name: Generate provisioning manifest\n)/, `${step.replace(/\n*$/, '\n')}\n$1`);
  assert.ok(checkAttestOrdering(early).some((p) => /before the manifest is generated/.test(p)));
});
