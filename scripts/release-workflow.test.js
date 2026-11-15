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

/** A job's steps as [{name, text}] in order (the publish job's unless another job is named). */
function publishSteps(text, job = 'publish') {
  const block = jobBlock(text, job);
  if (!block) return [];
  const i = block.indexOf('\n    steps:\n');
  if (i < 0) return [];
  const body = block.slice(i + '\n    steps:\n'.length);
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
const escapeRegExp = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const PUBLISH_STEP = 'Publish the prerelease';

function checkPrereleaseIsPublishedAfterAttach(text) {
  const problems = [];
  const steps = publishSteps(text);
  const attachIdx = steps.findIndex((s) => s.name === ATTACH);
  if (attachIdx < 0) return [`no "${ATTACH}" step`];
  const attach = steps[attachIdx].text;
  if (!new RegExp(`^ +prerelease: \\$\\{\\{ ${escapeRegExp(PRERELEASE_EXPR)} \\}\\}$`, 'm').test(attach)) {
    problems.push('the attach step does not set prerelease from the tag');
  }
  if (!new RegExp(`^ +draft: \\$\\{\\{ ${escapeRegExp(PRERELEASE_EXPR)} \\}\\}$`, 'm').test(attach)) {
    problems.push('the attach step does not create a prerelease as a draft, so its assets cannot be uploaded to an immutable release');
  }
  const publishIdx = steps.findIndex((s) => s.name === PUBLISH_STEP);
  if (publishIdx < 0) return [...problems, `no "${PUBLISH_STEP}" step`];
  const publish = steps[publishIdx].text;
  if (publishIdx < attachIdx) problems.push('the draft is published before the assets are attached');
  if (!new RegExp(`^        if: ${escapeRegExp(PRERELEASE_EXPR)}$`, 'm').test(publish)) problems.push('the publish step does not run only for prereleases');
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

// ---- release notes: the tag message gates the release and the notes ship with every package ----
//
// check-version runs `release-notes.js --check` so a stable tag without a summary fails before four
// platform builds. The publish job writes `release-notes.json` into every binaries/<rid>/ and the
// release body into $RUNNER_TEMP. That has to happen after the artifact downloads and before the size
// guard and `npm publish`: npm publishes before the archives are staged, so this is the only position
// that reaches both the npm package and the four archives.

const RIDS = ['linux-x64', 'linux-arm64', 'osx-arm64', 'win-x64'];
const FETCH_TAGS = 'Fetch all tags';
const CHECK_NOTES = 'Check the release notes';
const WRITE_NOTES = 'Write release notes';
const SIZE_GUARD = 'Guard npm package size (pre-publish)';
const NPM_PUBLISH = 'npm publish';

/**
 * The text of a step's `run:` value (a one-liner or a block), line continuations joined, spaces collapsed.
 * A shell comment (a `#` that starts a word, to the end of the line) is dropped, so a command that
 * appears only in a comment is not seen; pass `keepComments` to read the raw text (GitHub expands
 * `${{ }}` inside comments too).
 */
function runText(step, keepComments = false) {
  const lines = step.text.split('\n');
  const start = lines.findIndex((l) => /^ {8}run:/.test(l));
  if (start < 0) return '';
  const out = [lines[start].replace(/^ {8}run:\s*\|?\s*/, '')];
  for (const l of lines.slice(start + 1)) {
    if (/^ {8}\S/.test(l) || /^ {6}\S/.test(l)) break;
    out.push(l);
  }
  const code = keepComments ? out : out.map((l) => l.replace(/(^|\s)#.*$/, '$1'));
  return code.join('\n').replace(/\\\n/g, ' ').replace(/\s+/g, ' ').trim();
}

/** The value of a `key: value` line at the given indent of a step: 8 spaces for its own keys, 10 for `env:` and `with:` entries. */
const stepKey = (step, key, indent = 8) => (new RegExp(`^ {${indent}}${key}: (.*)$`, 'm').exec(step.text) ?? [])[1]?.trim();

function checkNotesStepsOfJob(text, job, scriptName) {
  const problems = [];
  const steps = publishSteps(text, job);
  const at = (name) => steps.findIndex((s) => s.name === name);
  const checkout = steps.findIndex((s) => /uses: actions\/checkout@v5\b/.test(s.text));
  const fetch = at(FETCH_TAGS);
  const script = at(scriptName);
  if (checkout < 0) problems.push(`the ${job} job has no actions/checkout@v5 step`);
  else if (!/^ {10}fetch-depth: 0$/m.test(steps[checkout].text)) problems.push(`the ${job} checkout does not set fetch-depth: 0`);
  if (fetch < 0) problems.push(`the ${job} job has no "${FETCH_TAGS}" step`);
  else {
    if (!/git fetch --force --tags origin/.test(runText(steps[fetch]))) problems.push(`the ${job} "${FETCH_TAGS}" step does not run git fetch --force --tags origin`);
    if (checkout >= 0 && fetch < checkout) problems.push(`the ${job} job fetches tags before the checkout`);
  }
  if (script < 0) return [...problems, `the ${job} job has no "${scriptName}" step`];
  if (fetch >= 0 && fetch > script) problems.push(`the ${job} job fetches tags after "${scriptName}"`);
  const step = steps[script];
  if (stepKey(step, 'REF_NAME', 10) !== '${{ github.ref_name }}') {
    problems.push(`"${scriptName}" does not pass the tag through env: REF_NAME`);
  }
  for (const s of [step, ...(fetch >= 0 ? [steps[fetch]] : [])]) {
    if (runText(s, true).includes('${{')) problems.push(`"${s.name}" interpolates an expression into the shell`);
  }
  if (!/^ {8}run:/m.test(step.text)) problems.push(`"${scriptName}" has no run:`);
  return problems;
}

function checkReleaseNotesGate(text) {
  const problems = checkNotesStepsOfJob(text, 'check-version', CHECK_NOTES);
  const steps = publishSteps(text, 'check-version');
  const gate = steps.find((s) => s.name === CHECK_NOTES);
  if (gate) {
    if (!/^node scripts\/release-notes\.js "\$REF_NAME" --check$/.test(runText(gate))) {
      problems.push(`"${CHECK_NOTES}" does not run node scripts/release-notes.js "$REF_NAME" --check`);
    }
    const node = steps.findIndex((s) => /uses: actions\/setup-node@v5\b/.test(s.text));
    if (node < 0 || node > steps.indexOf(gate)) problems.push(`"${CHECK_NOTES}" does not come after Setup Node.js`);
    if (/^(?:      - |        )if:/m.test(gate.text)) problems.push(`"${CHECK_NOTES}" has an if: condition and can be skipped`);
    if (/^(?:      - |        )continue-on-error:/m.test(gate.text)) problems.push(`"${CHECK_NOTES}" sets continue-on-error, so its failure is ignored`);
  }
  const job = jobBlock(text, 'check-version');
  if (job && /^    continue-on-error:/m.test(job)) problems.push('the check-version job sets continue-on-error, so its failure is ignored');
  return problems;
}

/** The build job must wait for check-version, or a bad tag message fails only after four platform builds. */
function checkBuildWaitsForTheGate(text) {
  const build = jobBlock(text, 'build');
  if (!build) return ['release.yml has no build job'];
  return /^    needs: check-version$/m.test(build) ? [] : ['the build job does not have needs: check-version'];
}

/** The `--body-out` value of the write step, quotes removed. */
const bodyOutOf = (step) => (/--body-out (\S+)/.exec(runText(step)) ?? [])[1]?.replace(/^"|"$/g, '');

function checkReleaseNotesAreWritten(text) {
  const problems = checkNotesStepsOfJob(text, 'publish', WRITE_NOTES);
  const steps = publishSteps(text, 'publish');
  const at = (name) => steps.findIndex((s) => s.name === name);
  const write = at(WRITE_NOTES);
  if (write < 0) return problems;
  const cmd = runText(steps[write]);
  if (/^(?:      - |        )if:/m.test(steps[write].text)) problems.push(`"${WRITE_NOTES}" has an if: condition and can be skipped`);
  if (/^(?:      - |        )continue-on-error:/m.test(steps[write].text)) problems.push(`"${WRITE_NOTES}" sets continue-on-error, so its failure is ignored`);
  if (!/(?:^| )node scripts\/release-notes\.js "\$REF_NAME" /.test(cmd)) problems.push(`"${WRITE_NOTES}" does not run node scripts/release-notes.js "$REF_NAME"`);
  if (/ --check\b/.test(cmd)) problems.push(`"${WRITE_NOTES}" runs with --check, so it writes nothing`);
  const outs = [...cmd.matchAll(/--notes-out (\S+)/g)].map((m) => m[1].replace(/^"|"$/g, ''));
  for (const rid of RIDS) {
    if (!outs.includes(`binaries/${rid}/release-notes.json`)) problems.push(`"${WRITE_NOTES}" does not write binaries/${rid}/release-notes.json`);
  }
  const downloads = steps.map((s, i) => (/^Download .* artifact$/.test(s.name) ? i : -1)).filter((i) => i >= 0);
  if (downloads.length !== RIDS.length) problems.push(`expected ${RIDS.length} artifact download steps, found ${downloads.length}`);
  if (downloads.some((i) => i > write)) problems.push(`"${WRITE_NOTES}" runs before the artifact downloads, which would overwrite its files`);
  for (const later of [SIZE_GUARD, NPM_PUBLISH]) {
    const i = at(later);
    if (i < 0) problems.push(`no "${later}" step`);
    else if (write > i) problems.push(`"${WRITE_NOTES}" runs after "${later}", so the notes miss the npm package and the archives`);
  }
  const attach = steps.find((s) => s.name === ATTACH);
  if (!attach) return [...problems, `no "${ATTACH}" step`];
  // The attach step writes the path as `${{ runner.temp }}/...`, the write step as `$RUNNER_TEMP/...`: they are the
  // same file, so map the expression to the variable before comparing them.
  const bodyPath = stepKey(attach, 'body_path', 10)?.replace(/^"|"$/g, '').replace('${{ runner.temp }}', '$RUNNER_TEMP');
  const bodyOut = bodyOutOf(steps[write]);
  if (bodyOut === undefined) problems.push(`"${WRITE_NOTES}" has no --body-out`);
  if (bodyPath === undefined) problems.push('the attach step has no body_path, so the release body is not the tag message');
  else if (bodyOut !== undefined && bodyPath !== bodyOut) problems.push(`the attach step's body_path (${bodyPath}) is not the file --body-out writes (${bodyOut})`);
  return problems;
}

test('release.yml: check-version fetches the tags and checks the release notes of the tag', () => {
  assert.deepEqual(checkReleaseNotesGate(REAL), []);
});

test('release.yml: the notes are written into every binaries/<rid>/ before the size guard and npm publish, and the attach step uses the body', () => {
  assert.deepEqual(checkReleaseNotesAreWritten(REAL), []);
});

test('publishSteps: parses the steps of another job and keeps the publish default', () => {
  assert.ok(publishSteps(REAL, 'check-version').some((s) => s.name === CHECK_NOTES));
  assert.ok(publishSteps(REAL).some((s) => s.name === ATTACH));
  assert.deepEqual(publishSteps(REAL, 'no-such-job'), []);
});

/** A copy of the workflow with the step named `name` (of `job`) taken out. */
function withoutStep(text, name, job = 'publish') {
  const step = publishSteps(text, job).find((s) => s.name === name);
  assert.ok(step, `the real workflow has "${name}"`);
  return text.replace(step.text, '');
}

/** A copy of the workflow with the step `name` moved to right after the step `after` (both in the publish job). */
function moveStepAfter(text, name, after) {
  const steps = publishSteps(text);
  const moved = steps.find((s) => s.name === name).text.replace(/\n*$/, '\n');
  const anchor = steps.find((s) => s.name === after).text;
  return text.replace(steps.find((s) => s.name === name).text, '').replace(anchor, `${anchor.replace(/\n*$/, '\n')}\n${moved}\n`);
}

const mutate = (from, to) => {
  const mutated = REAL.replace(from, to);
  assert.notEqual(mutated, REAL, `the real workflow contains ${JSON.stringify(from)}`);
  return mutated;
};

test('mutation: the write step moved after npm publish is caught', () => {
  const mutated = moveStepAfter(REAL, WRITE_NOTES, NPM_PUBLISH);
  assert.notEqual(mutated, REAL);
  assert.ok(checkReleaseNotesAreWritten(mutated).some((p) => /after "npm publish"/.test(p)));
});

test('mutation: the write step moved before the artifact downloads is caught', () => {
  const mutated = moveStepAfter(REAL, WRITE_NOTES, 'Fetch bge-small ONNX model');
  assert.notEqual(mutated, REAL);
  assert.ok(checkReleaseNotesAreWritten(mutated).some((p) => /before the artifact downloads/.test(p)));
});

test('mutation: a missing rid is caught', () => {
  for (const rid of RIDS) {
    const mutated = REAL.replace(new RegExp(`^ +--notes-out binaries/${rid}/release-notes\\.json.*\\n`, 'm'), '');
    assert.notEqual(mutated, REAL, rid);
    assert.ok(checkReleaseNotesAreWritten(mutated).some((p) => p.includes(`binaries/${rid}/release-notes.json`)), rid);
  }
});

test('mutation: a missing body_path is caught, and so is one that names another file', () => {
  const removed = mutate(/^ +body_path: .*\n/m, '');
  assert.ok(checkReleaseNotesAreWritten(removed).some((p) => /no body_path/.test(p)));
  const other = mutate('${{ runner.temp }}/release-body.md', '${{ runner.temp }}/other.md');
  assert.ok(checkReleaseNotesAreWritten(other).some((p) => /not the file --body-out writes/.test(p)));
});

test('mutation: the gate step removed from check-version is caught', () => {
  const mutated = withoutStep(REAL, CHECK_NOTES, 'check-version');
  assert.ok(checkReleaseNotesGate(mutated).some((p) => /no "Check the release notes" step/.test(p)));
});

test('mutation: a gate step that no longer passes --check, or runs before Setup Node.js, is caught', () => {
  assert.ok(checkReleaseNotesGate(mutate('"$REF_NAME" --check', '"$REF_NAME"')).some((p) => /--check/.test(p)));
  const steps = publishSteps(REAL, 'check-version');
  const node = steps.find((s) => s.name === 'Setup Node.js').text;
  const early = REAL.replace(node, '').replace(steps.find((s) => s.name === CHECK_NOTES).text, (g) => `${g}${node}`);
  assert.notEqual(early, REAL);
  assert.ok(checkReleaseNotesGate(early).some((p) => /after Setup Node\.js/.test(p)));
});

test('mutation: a missing fetch step, a shallow checkout, or a fetch after the script is caught', () => {
  for (const [job, check] of [['check-version', checkReleaseNotesGate], ['publish', checkReleaseNotesAreWritten]]) {
    assert.ok(check(withoutStep(REAL, FETCH_TAGS, job)).some((p) => /no "Fetch all tags" step/.test(p)), job);
    const block = jobBlock(REAL, job);
    const shallow = REAL.replace(block, block.replace(/^ +with:\n +fetch-depth: 0\n/m, ''));
    assert.notEqual(shallow, REAL, job);
    assert.ok(check(shallow).some((p) => /fetch-depth: 0/.test(p)), job);
    const steps = publishSteps(REAL, job);
    const fetch = steps.find((s) => s.name === FETCH_TAGS).text;
    const script = steps.find((s) => s.name === (job === 'publish' ? WRITE_NOTES : CHECK_NOTES)).text;
    const late = REAL.replace(fetch, '').replace(script, (g) => `${g.replace(/\n*$/, '\n')}\n${fetch.replace(/\n*$/, '\n')}\n`);
    assert.notEqual(late, REAL, job);
    assert.ok(check(late).some((p) => /fetches tags after/.test(p)), job);
  }
});

test('mutation: the tag inlined into a run: block is caught', () => {
  assert.ok(checkReleaseNotesGate(mutate('"$REF_NAME" --check', '"${{ github.ref_name }}" --check')).some((p) => /interpolates an expression/.test(p)));
  assert.ok(checkReleaseNotesAreWritten(mutate('node scripts/release-notes.js "$REF_NAME" \\\n', 'node scripts/release-notes.js "${{ github.ref_name }}" \\\n')).some((p) => /interpolates an expression/.test(p)));
});

// ---- the gate cannot be skipped or ignored, and the builds wait for it ----

test('mutation: an if: or continue-on-error on the gate step is caught', () => {
  for (const [line, pattern] of [['if: always()', /if: condition/], ['continue-on-error: true', /continue-on-error/]]) {
    const mutated = mutate(`        run: node scripts/release-notes.js "$REF_NAME" --check\n`, `        ${line}\n        run: node scripts/release-notes.js "$REF_NAME" --check\n`);
    assert.ok(checkReleaseNotesGate(mutated).some((p) => pattern.test(p)), line);
  }
  const first = mutate('      - name: Check the release notes\n', '      - if: always()\n        name: Check the release notes\n');
  assert.ok(checkReleaseNotesGate(first).some((p) => /if: condition/.test(p)));
});

test('mutation: continue-on-error on the check-version job is caught', () => {
  const mutated = mutate('  check-version:\n    name: Check version files match the tag\n', '  check-version:\n    continue-on-error: true\n    name: Check version files match the tag\n');
  assert.ok(checkReleaseNotesGate(mutated).some((p) => /check-version job sets continue-on-error/.test(p)));
});

test('release.yml: the build job needs check-version', () => {
  assert.deepEqual(checkBuildWaitsForTheGate(REAL), []);
});

test('mutation: a build job that no longer needs check-version is caught', () => {
  assert.ok(checkBuildWaitsForTheGate(mutate('    needs: check-version\n', '')).some((p) => /needs: check-version/.test(p)));
  assert.ok(checkBuildWaitsForTheGate(mutate('    needs: check-version\n', '    needs: publish\n')).some((p) => /needs: check-version/.test(p)));
});

// ---- the write step cannot be skipped or ignored ----

test('mutation: an if: or continue-on-error on the write step is caught', () => {
  const run = '        run: |\n          set -euo pipefail\n          node scripts/release-notes.js "$REF_NAME" \\\n';
  for (const [line, pattern] of [['if: false', /if: condition/], ['continue-on-error: true', /continue-on-error/]]) {
    const mutated = mutate(run, `        ${line}\n${run}`);
    assert.ok(checkReleaseNotesAreWritten(mutated).some((p) => pattern.test(p)), line);
  }
  const first = mutate('      - name: Write release notes\n', '      - if: false\n        name: Write release notes\n');
  assert.ok(checkReleaseNotesAreWritten(first).some((p) => /if: condition/.test(p)));
});

// ---- a shell comment is not a command ----

test('runText drops a shell comment but keeps a # inside a word', () => {
  const step = (run) => ({ name: 'x', text: `      - name: x\n        run: ${run}\n` });
  assert.equal(runText(step('true # git fetch --force --tags origin')), 'true');
  assert.equal(runText(step('echo a#b "$X"')), 'echo a#b "$X"');
  assert.equal(runText({ name: 'x', text: '      - name: x\n        run: |\n          # note\n          echo hi # more\n          echo bye\n' }), 'echo hi echo bye');
});

test('mutation: a command that appears only after # in a run: is not accepted', () => {
  for (const [job, check] of [['check-version', checkReleaseNotesGate], ['publish', checkReleaseNotesAreWritten]]) {
    const block = jobBlock(REAL, job);
    const commented = REAL.replace(block, block.replace('run: git fetch --force --tags origin', 'run: true # git fetch --force --tags origin'));
    assert.notEqual(commented, REAL, job);
    assert.ok(check(commented).some((p) => /does not run git fetch --force --tags origin/.test(p)), job);
  }
  const gate = mutate('run: node scripts/release-notes.js "$REF_NAME" --check', 'run: true # node scripts/release-notes.js "$REF_NAME" --check');
  assert.ok(checkReleaseNotesGate(gate).some((p) => /does not run node scripts\/release-notes\.js "\$REF_NAME" --check/.test(p)));
  const out = mutate('            --notes-out binaries/linux-x64/release-notes.json \\\n', '            # --notes-out binaries/linux-x64/release-notes.json \\\n');
  assert.ok(checkReleaseNotesAreWritten(out).some((p) => p.includes('binaries/linux-x64/release-notes.json')));
  const body = mutate('            --body-out "$RUNNER_TEMP/release-body.md"\n', '            # --body-out "$RUNNER_TEMP/release-body.md"\n');
  assert.ok(checkReleaseNotesAreWritten(body).some((p) => /has no --body-out/.test(p)));
});
