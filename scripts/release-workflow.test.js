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

/**
 * The job's own `permissions:` mapping (job level, 4-space indent), as a list of `key: value` strings.
 * Comment lines inside the block are ignored; `permissions: write-all` is returned as `write-all`
 * because it grants every permission, attestations included.
 */
function jobPermissions(job) {
  const flat = /^    permissions: (write-all)\s*$/m.exec(job);
  if (flat) return [flat[1]];
  const m = /^    permissions:\n((?:      (?:[a-z-]+: \w+|#.*)\n)+)/m.exec(job);
  return m ? m[1].split('\n').map((l) => l.trim()).filter((l) => l && !l.startsWith('#')) : [];
}

const grantsAttestations = (perms) => perms.includes('attestations: write') || perms.includes('write-all');

// ---- Cycle 1: the publish job attests the release assets ----

function checkAttestStepExists(text) {
  const problems = [];
  const publish = jobBlock(text, 'publish');
  if (!publish) return ['release.yml has no publish job'];
  if (!/uses: actions\/attest@v4\b/.test(publish)) problems.push('the publish job has no actions/attest@v4 step');
  if (!grantsAttestations(jobPermissions(publish))) problems.push('the publish job does not have attestations: write');
  return problems;
}

test('release.yml: the publish job attests the release assets and may write attestations', () => {
  assert.deepEqual(checkAttestStepExists(REAL), []);
});

// ---- Cycle 2: the attestation gates the release, and cannot be skipped ----

const STEP_START = /^      - /m; // every step is a `- ` list item at 6 spaces, whatever its first key is

/** The publish job's steps as [{name, text}] in order. */
function publishSteps(text) {
  const publish = jobBlock(text, 'publish');
  if (!publish) return [];
  const i = publish.indexOf('\n    steps:\n');
  if (i < 0) return [];
  const body = publish.slice(i + '\n    steps:\n'.length);
  return body.split(new RegExp(`(?=${STEP_START.source})`, 'm')).filter((c) => STEP_START.test(c)).map((c) => ({
    name: (/^(?:      - |        )name: (.*)$/m.exec(c) ?? [])[1]?.trim() ?? '',
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
  if (/^(?:      - |        )if:/m.test(body)) problems.push('the attestation step has an if: condition and can be skipped');
  if (/^(?:      - |        )continue-on-error:/m.test(body)) problems.push('the attestation step sets continue-on-error, so its failure is ignored');
  if (attach >= 0) {
    const attachText = steps[attach].text;
    if (/^(?:      - |        )if:/m.test(attachText)) problems.push('the attach step has an if: condition, so assets can be attached after a failed attestation');
    if (/^(?:      - |        )continue-on-error:/m.test(attachText)) problems.push('the attach step sets continue-on-error, so a failure there is ignored');
  }
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

// ---- Cycle 3: every attached file is attested, and only publish may write attestations ----

/** The lines of a `key: |` block scalar inside a step's text (the block ends at the first less-indented line). */
function blockLines(stepText, key) {
  const m = new RegExp(`^( +)${key}: \\|\\n((?:\\1 +.*\\n?)+)`, 'm').exec(stepText);
  return m ? m[2].split('\n').map((l) => l.trim()).filter(Boolean) : null;
}

function checkEveryAttachedFileIsASubject(text) {
  const steps = publishSteps(text);
  const attach = steps.find((s) => s.name === ATTACH);
  const attest = steps.find((s) => /uses: actions\/attest@v4\b/.test(s.text));
  if (!attach) return [`no "${ATTACH}" step`];
  if (!attest) return ['no actions/attest@v4 step'];
  const attached = blockLines(attach.text, 'files');
  const subjects = blockLines(attest.text, 'subject-path');
  if (!attached || attached.length === 0) return ['the attach step lists no files'];
  if (!subjects) return ['the attestation step has no subject-path list'];
  return attached.filter((f) => !subjects.includes(f)).map((f) => `${f} is attached to the release but is not an attestation subject`);
}

/** Jobs (and the workflow itself) whose permissions grant attestations: write, other than publish. */
function checkAttestationPermissionScope(text) {
  const problems = [];
  const top = /^permissions:\n((?:  .*\n)+)/m.exec(text);
  if (top && /attestations: write/.test(top[1])) problems.push('the workflow-level permissions grant attestations: write');
  const names = [...text.matchAll(/^  ([a-z][a-z-]*):\n/gm)].map((m) => m[1]);
  for (const name of names) {
    const job = jobBlock(text, name);
    const grants = grantsAttestations(jobPermissions(job));
    if (name === 'publish' && !grants) problems.push('the publish job does not have attestations: write');
    if (name !== 'publish' && grants) problems.push(`the ${name} job has attestations: write`);
  }
  return problems;
}

test('release.yml: every file attached to the release is an attestation subject', () => {
  assert.deepEqual(checkEveryAttachedFileIsASubject(REAL), []);
});

test('release.yml: attestations: write is granted to the publish job and to no other job', () => {
  assert.deepEqual(checkAttestationPermissionScope(REAL), []);
});

test('mutation: an attached file that is dropped from subject-path is caught', () => {
  const mutated = REAL.replace(/(subject-path: \|\n(?: +release-assets\/.*\n)*?) +release-assets\/provisioning\.manifest\.json\n/, '$1');
  assert.notEqual(mutated, REAL);
  assert.ok(checkEveryAttachedFileIsASubject(mutated).some((p) => /provisioning\.manifest\.json/.test(p)));
});

test('mutation: a new attached file with no attestation is caught', () => {
  const mutated = REAL.replace(/(          files: \|\n(?: +release-assets\/.*\n)+)/, '$1            release-assets/extra.bin\n');
  assert.notEqual(mutated, REAL);
  assert.ok(checkEveryAttachedFileIsASubject(mutated).some((p) => /extra\.bin/.test(p)));
});

test('mutation: attestations: write added to the build job is caught', () => {
  const mutated = REAL.replace(/(  build:\n    name: .*\n)/, '$1    permissions:\n      attestations: write\n');
  assert.notEqual(mutated, REAL);
  assert.ok(checkAttestationPermissionScope(mutated).some((p) => /build job/.test(p)));
});

test('mutation: attestations: write at workflow level is caught', () => {
  const mutated = REAL.replace(/^permissions:\n/m, 'permissions:\n  attestations: write\n');
  assert.notEqual(mutated, REAL);
  assert.ok(checkAttestationPermissionScope(mutated).some((p) => /workflow-level/.test(p)));
});

test('mutation: attestations: write removed from the publish job is caught', () => {
  const mutated = REAL.replace(/^      attestations: write\n/m, '');
  assert.notEqual(mutated, REAL);
  assert.ok(checkAttestationPermissionScope(mutated).some((p) => /publish job does not/.test(p)));
  assert.ok(checkAttestStepExists(mutated).some((p) => /attestations: write/.test(p)));
});

// ---- gaps found in review: harder-to-spot ways to defeat the checks ----

test('mutation: an if: written as the first key of the attest step is caught', () => {
  const mutated = REAL.replace('      - name: Attest release assets\n        uses: actions/attest@v4\n',
    '      - if: always()\n        name: Attest release assets\n        uses: actions/attest@v4\n');
  assert.notEqual(mutated, REAL);
  assert.ok(checkAttestOrdering(mutated).some((p) => /if: condition/.test(p)));
});

test('mutation: an if: or continue-on-error on the attach step is caught', () => {
  for (const line of ['if: always()', 'continue-on-error: true']) {
    const mutated = REAL.replace('        uses: softprops/action-gh-release@v3\n', `        uses: softprops/action-gh-release@v3\n        ${line}\n`);
    assert.notEqual(mutated, REAL);
    assert.ok(checkAttestOrdering(mutated).some((p) => /attach step/.test(p)), line);
  }
});

test('mutation: a comment inside another job\'s permissions block does not hide attestations: write', () => {
  const mutated = REAL.replace(/(  build:\n    name: .*\n)/, '$1    permissions:\n      contents: read\n      # keep this tight\n      attestations: write\n');
  assert.notEqual(mutated, REAL);
  assert.ok(checkAttestationPermissionScope(mutated).some((p) => /build job/.test(p)));
});

test('mutation: permissions: write-all on another job is caught', () => {
  const mutated = REAL.replace(/(  build:\n    name: .*\n)/, '$1    permissions: write-all\n');
  assert.notEqual(mutated, REAL);
  assert.ok(checkAttestationPermissionScope(mutated).some((p) => /build job/.test(p)));
});

// ---- immutable releases: a prerelease is created as a draft and published after its assets are attached ----
//
// This repository has immutable releases, which lock assets once a release is published. A stable
// release uploads its assets before publishing (softprops/action-gh-release does that itself), but a
// prerelease stays published by default, so the upload is refused ("Cannot upload asset ... to an
// immutable release"; this broke v4.7.0-beta.1). A prerelease must therefore be a draft while the
// assets are attached and be published by a later step.

const PRERELEASE_EXPR = "contains(github.ref_name, '-')";
const PUBLISH_STEP = 'Publish the prerelease';

function checkPrereleaseIsPublishedAfterAttach(text) {
  const problems = [];
  const steps = publishSteps(text);
  const attachIdx = steps.findIndex((s) => s.name === ATTACH);
  if (attachIdx < 0) return [`no "${ATTACH}" step`];
  const attach = steps[attachIdx].text;
  if (!new RegExp(`^ +prerelease: \\$\\{\\{ ${PRERELEASE_EXPR.replace(/[()]/g, '\\$&')} \\}\\}$`, 'm').test(attach)) {
    problems.push('the attach step does not set prerelease from the tag');
  }
  if (!new RegExp(`^ +draft: \\$\\{\\{ ${PRERELEASE_EXPR.replace(/[()]/g, '\\$&')} \\}\\}$`, 'm').test(attach)) {
    problems.push('the attach step does not create a prerelease as a draft, so its assets cannot be uploaded to an immutable release');
  }
  const publishIdx = steps.findIndex((s) => s.name === PUBLISH_STEP);
  if (publishIdx < 0) return [...problems, `no "${PUBLISH_STEP}" step`];
  const publish = steps[publishIdx].text;
  if (publishIdx < attachIdx) problems.push('the draft is published before the assets are attached');
  if (!new RegExp(`^        if: ${PRERELEASE_EXPR.replace(/[()]/g, '\\$&')}$`, 'm').test(publish)) problems.push('the publish step does not run only for prereleases');
  if (!/^        id: attach_release$/m.test(attach)) problems.push('the attach step has no id: attach_release, so its release id cannot be passed on');
  if (!/gh api --method PATCH "repos\/\$GITHUB_REPOSITORY\/releases\/\$RELEASE_ID" -F draft=false/.test(publish)) problems.push('the publish step does not PATCH the release with draft=false by its id');
  if (!/^          RELEASE_ID: \$\{\{ steps\.attach_release\.outputs\.id \}\}$/m.test(publish)) problems.push('the release id reaches the publish step other than through env: RELEASE_ID');
  const run = /run: (.*)/.exec(publish);
  if (run && run[1].includes('${{')) problems.push('the publish step interpolates an expression into the shell');
  if (/continue-on-error:/.test(publish)) problems.push('the publish step ignores its own failure');
  return problems;
}

test('release.yml: a prerelease is created as a draft and published after its assets are attached', () => {
  assert.deepEqual(checkPrereleaseIsPublishedAfterAttach(REAL), []);
});

test('mutation: an attach step that no longer drafts a prerelease is caught', () => {
  const mutated = REAL.replace(/^ +draft: .*\n/m, '');
  assert.notEqual(mutated, REAL);
  assert.ok(checkPrereleaseIsPublishedAfterAttach(mutated).some((p) => /as a draft/.test(p)));
});

test('mutation: a missing or misplaced publish step is caught', () => {
  const step = publishSteps(REAL).find((s) => s.name === PUBLISH_STEP);
  assert.ok(step, 'the real workflow has the publish step');
  assert.ok(checkPrereleaseIsPublishedAfterAttach(REAL.replace(step.text, '')).some((p) => /no "Publish the prerelease" step/.test(p)));
  const before = REAL.replace(step.text, '').replace(/(      - name: Attach archives to GitHub release\n)/, `${step.text.replace(/\n*$/, '\n')}\n$1`);
  assert.ok(checkPrereleaseIsPublishedAfterAttach(before).some((p) => /before the assets are attached/.test(p)));
});

test('mutation: a publish step that also runs for stable releases or interpolates the tag is caught', () => {
  const noIf = REAL.replace(/^        if: contains\(github\.ref_name, '-'\)\n/m, '');
  assert.notEqual(noIf, REAL);
  assert.ok(checkPrereleaseIsPublishedAfterAttach(noIf).some((p) => /only for prereleases/.test(p)));
  const inline = REAL.replace('releases/$RELEASE_ID"', 'releases/${{ steps.attach_release.outputs.id }}"');
  assert.notEqual(inline, REAL);
  assert.ok(checkPrereleaseIsPublishedAfterAttach(inline).length > 0);
});
