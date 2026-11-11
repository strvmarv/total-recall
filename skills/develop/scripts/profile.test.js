import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import { parseProfile, SUBCOMMANDS } from './profile.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const PROFILE_MD = path.join(HERE, '..', 'profile.md');
const PROFILE = path.join(HERE, 'profile.js');
const CASES = readGolden(PROFILE_MD, 'profile-cases');

function caseNamed(prefix) {
  const found = CASES.filter((c) => c.name.startsWith(prefix));
  assert.equal(found.length, 1, prefix);
  return found[0];
}

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'profile-test-'));
}

function run(args) {
  const result = spawnSync(process.execPath, [PROFILE, ...args], { encoding: 'utf8' });
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
  const file = path.join(tmpDir(), 'profile.md');
  fs.writeFileSync(file, text);
  return file;
}

// ---- parseProfile: the profile-cases golden ----

test('parseProfile: every case in the profile-cases golden gives its expected result', () => {
  assert.ok(CASES.length >= 11);
  for (const c of CASES) {
    assert.deepEqual(parseProfile(c.markdown), c.expect, c.name);
  }
});

test('parseProfile: the CRLF variant parses to the same result as the LF example', () => {
  const lf = caseNamed('full example with gh.account');
  const crlf = caseNamed('the same full example with CRLF');
  assert.ok(crlf.markdown.includes('\r\n'));
  assert.deepEqual(parseProfile(crlf.markdown), parseProfile(lf.markdown));
});

// ---- parseProfile: more input classes ----

test('parseProfile: keys and values are trimmed', () => {
  const md = '```text\ntr-develop/profile-v1\n   base:    main   \n   gh.account:  jane-doe_acme  \n```\n';
  assert.deepEqual(parseProfile(md), {
    ok: true,
    profile: { base: 'main', ticketStandard: 'on', templates: {}, ghAccount: 'jane-doe_acme', verify: [] },
  });
});

test('parseProfile: a leading byte-order mark does not hide the header', () => {
  const md = '\uFEFF```text\ntr-develop/profile-v1\nbase: main\n```\n';
  assert.deepEqual(parseProfile(md).ok, true);
  assert.equal(parseProfile(md).profile.base, 'main');
});

test('parseProfile: the profile is the first text block with the header, earlier text blocks are skipped', () => {
  const md = '```text\nsome other notes\n```\n\n```text\ntr-develop/profile-v1\nbase: dev\n```\n';
  assert.equal(parseProfile(md).profile.base, 'dev');
});

test('parseProfile: a header inside a non-text fence is not a profile', () => {
  const md = '```sh verify\ntr-develop/profile-v1\n```\n';
  assert.deepEqual(parseProfile(md), { ok: false, error: 'no-profile' });
});

test('parseProfile: templates for chore, and the .yaml extension, are accepted', () => {
  const md = '```text\ntr-develop/profile-v1\nticket.template.chore: chore.yaml\nticket.template.feature: f.yml\n```\n';
  assert.deepEqual(parseProfile(md).profile.templates, { chore: 'chore.yaml', feature: 'f.yml' });
});

test('parseProfile: a ticket.template key for another type is an unknown key and is ignored', () => {
  const md = '```text\ntr-develop/profile-v1\nticket.template.epic: epic.md\n```\n';
  assert.deepEqual(parseProfile(md).profile.templates, {});
});

test('parseProfile: a valid login with an underscore or a hyphen is accepted, a shell fragment is not', () => {
  assert.equal(parseProfile('```text\ntr-develop/profile-v1\ngh.account: jane-doe_acme\n```\n').profile.ghAccount, 'jane-doe_acme');
  assert.equal(parseProfile('```text\ntr-develop/profile-v1\ngh.account: $(id)\n```\n').error, 'bad-value:gh.account');
});

test('parseProfile: an empty value for a known key is a bad value, not a default', () => {
  assert.deepEqual(parseProfile('```text\ntr-develop/profile-v1\nbase:\n```\n'), { ok: false, error: 'bad-value:base' });
});

test('parseProfile: verify blocks are commands in document order, and a non-verify sh block is not one', () => {
  const md = '```text\ntr-develop/profile-v1\n```\n\n```sh verify\nfirst\n```\n\n```sh\nnot-a-verify\n```\n\n```sh verify\nsecond\n```\n';
  assert.deepEqual(parseProfile(md).profile.verify, ['first', 'second']);
});

// ---- CLI: parse ----

test('CLI parse: prints the parseProfile result for the full example, exit 0', () => {
  const { parsed, status } = run(['parse', writeText(caseNamed('full example with gh.account').markdown)]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, caseNamed('full example with gh.account').expect);
});

test('CLI parse: the CRLF file gives the same JSON object', () => {
  const { parsed, status } = run(['parse', writeText(caseNamed('the same full example with CRLF').markdown)]);
  assert.equal(status, 0);
  assert.deepEqual(parsed, caseNamed('full example with gh.account').expect);
});

test('CLI parse: a bad value prints the error object and exits 1', () => {
  const c = caseNamed('ticket.standard must be');
  const { parsed, status } = run(['parse', writeText(c.markdown)]);
  assert.equal(status, 1);
  assert.deepEqual(parsed, c.expect);
});

test('CLI parse: a missing argument is a usage error with exit 1', () => {
  const { parsed, status } = run(['parse']);
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
