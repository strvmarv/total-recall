// scripts/release-notes.test.js — tests for scripts/release-notes.js (the release notes pipeline).
import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import {
  sanitizeSummary, parseTagMessage, parseReleaseTag, compareVersions, previousVersion, buildReleaseNotes, readAnnotatedTag, stripSignature,
} from './release-notes.js';
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

const HOSTILE = [
  '[[[a](b)](c)](d)', '[a](b', '\u001b', '\u001b[', '\u001b[1;31', '<<https://x.y>>', '<http://[a]>(b)', '\ufeff\ufeff x',
  'x'.repeat(199) + '\ud83d\ude00' + 'y'.repeat(50), '[' + 'a'.repeat(300) + '](u)', '\u0000'.repeat(500),
  'word '.repeat(100), '\r\n'.repeat(120), '[x](y)'.repeat(80),
];

test('sanitizeSummary keeps its invariants on hostile input', () => {
  for (const input of HOSTILE) {
    const out = sanitizeSummary(input);
    assert.ok(out.length <= 200, `length: ${JSON.stringify(input).slice(0, 30)}`);
    assert.ok(!/[\r\n]/.test(out), 'no line break');
    assert.ok(!/^ | $/.test(out), 'trimmed');
    assert.ok(!/[\u0000-\u0008\u000e-\u001f\u007f-\u0084\u0086-\u009f]/.test(out), 'no control character');
    assert.ok(!/\[[^\]]*\]\([^)]*\)/.test(out), 'no markdown link left');
    assert.ok(!/[\ud800-\udbff]$/.test(out), 'no lone high surrogate at the end');
  }
});

test('sanitizeSummary is idempotent on every fixture input and every hostile input', () => {
  for (const input of [...SANITISER_CASES.map((c) => c.input), ...HOSTILE]) {
    const once = sanitizeSummary(input);
    assert.equal(sanitizeSummary(once), once, JSON.stringify(input).slice(0, 40));
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

// ---- Task 2 review fixes ----

test('parseTagMessage: v4.6.0 summary starts with its real first words', () => {
  assert.match(parseTagMessage(tagMessage('v4.6.0')).summary, /^session_start now runs a version-gated/);
});

test('parseTagMessage: a first paragraph opening with a section heading is not a summary, whatever follows', () => {
  for (const msg of ['v1\n\nAdded\n\n- a\n', 'v1\n\nAdded:\n- a\n', 'v1\n\nAdded (from x)\n- a\n', 'v1\n\n## Added\n\n- a\n']) {
    const p = parseTagMessage(msg);
    assert.equal(p.summary, '', JSON.stringify(msg));
    assert.deepEqual(p.sections.added, ['a'], JSON.stringify(msg));
  }
  const p = parseTagMessage('v1\n\nAdded\nsome text\n\nFixed\n- z\n');
  assert.equal(p.summary, '');
  assert.deepEqual(p.sections.fixed, ['z']);
  assert.equal(parseTagMessage('v1\n\nFixed the release workflow.\n').summary, 'Fixed the release workflow.');
});

test('parseTagMessage: a summary ends at the first bullet line as well as at a blank line', () => {
  const p = parseTagMessage('v1\n\nSummary.\n- a\n- b\n\nAdded\n- c\n');
  assert.equal(p.summary, 'Summary.');
  assert.deepEqual(p.sections.added, ['c']);
  assert.deepEqual(p.sections.changed, []);
  assert.deepEqual(p.sections.fixed, []);
});

test('parseTagMessage: a Fixed heading at column 0 inside a bullet list opens the fixed section', () => {
  const p = parseTagMessage('v1\n\nSummary.\n\nAdded\n- a\nFixed\n- b\n');
  assert.deepEqual(p.sections.added, ['a']);
  assert.deepEqual(p.sections.fixed, ['b']);
});

test('parseTagMessage: a tab-indented continuation line joins the previous bullet', () => {
  const p = parseTagMessage('v1\n\nSummary.\n\nAdded\n- a\n\tmore\n- b\n');
  assert.deepEqual(p.sections.added, ['a more', 'b']);
});

test('parseTagMessage: a bullet with no text is skipped', () => {
  const p = parseTagMessage('v1\n\nSummary.\n\nAdded\n- \n- a\n');
  assert.deepEqual(p.sections.added, ['a']);
});

test('parseTagMessage: a summary that starts with a parenthesised word is still a summary', () => {
  const p = parseTagMessage('v1.0.0\n\nFixed (rare) crash on startup (see #12)\n');
  assert.equal(p.summary, 'Fixed (rare) crash on startup (see #12)');
});

// ---- version tags and the previous release ----

test('parseReleaseTag: a vMAJOR.MINOR.PATCH[-prerelease] tag gives the bare version, anything else null', () => {
  assert.equal(parseReleaseTag('v4.7.0-beta.1'), '4.7.0-beta.1');
  assert.equal(parseReleaseTag('v4.10.0'), '4.10.0');
  for (const tag of ['0.5.1', 'vNEXT', 'v1.2', 'v1.2.3+build', 'v1.2.3-beta+build', 'v01.2.3', 'v1.02.3', 'v1.2.3-', 'v1.2.3-01', 'v1.2.3-a..b', '', ' v1.2.3', 'v1.2.3\n']) {
    assert.equal(parseReleaseTag(tag), null, JSON.stringify(tag));
  }
});

test('compareVersions: SemVer precedence, numeric parts and identifiers compared numerically', () => {
  assert.ok(compareVersions('4.10.0', '4.9.0') > 0);
  assert.ok(compareVersions('4.9.0', '4.10.0') < 0);
  assert.ok(compareVersions('4.7.0-beta.10', '4.7.0-beta.2') > 0);
  assert.ok(compareVersions('4.7.0-beta.2', '4.7.0-beta.1') > 0);
  assert.ok(compareVersions('4.7.0', '4.7.0-beta.2') > 0);
  assert.ok(compareVersions('4.7.0-beta.2', '4.7.0') < 0);
  assert.equal(compareVersions('4.7.0-beta.2', '4.7.0-beta.2'), 0);
});

test('compareVersions: the SemVer 2.0.0 prerelease example ordering holds', () => {
  const ordered = ['1.0.0-alpha', '1.0.0-alpha.1', '1.0.0-alpha.beta', '1.0.0-beta', '1.0.0-beta.2', '1.0.0-beta.11', '1.0.0-rc.1', '1.0.0'];
  for (let i = 0; i + 1 < ordered.length; i += 1) {
    assert.ok(compareVersions(ordered[i], ordered[i + 1]) < 0, `${ordered[i]} < ${ordered[i + 1]}`);
    assert.ok(compareVersions(ordered[i + 1], ordered[i]) > 0, `${ordered[i + 1]} > ${ordered[i]}`);
  }
  // numeric identifiers are lower than alphanumeric ones
  assert.ok(compareVersions('1.0.0-1', '1.0.0-a') < 0);
});

test('previousVersion: a stable release looks at the highest lower stable version', () => {
  const tags = ['v4.6.0', 'v4.7.0-beta.1', 'v4.7.0-beta.2', 'v4.7.0', '0.5.1'];
  assert.equal(previousVersion('4.7.0', tags), '4.6.0');
  assert.equal(previousVersion('4.6.0', tags), null);
  assert.equal(previousVersion('4.6.0', ['v4.5.0', ...tags]), '4.5.0');
});

test('previousVersion: a prerelease looks at the highest lower version of any kind', () => {
  const tags = ['v4.6.0', 'v4.7.0-beta.1', 'v4.7.0-beta.2', 'v4.7.0', '0.5.1'];
  assert.equal(previousVersion('4.7.0-beta.2', tags), '4.7.0-beta.1');
  assert.equal(previousVersion('4.7.0-beta.1', tags), '4.6.0');
});

test('previousVersion: re-cutting a prerelease ignores a higher stable tag', () => {
  assert.equal(previousVersion('4.7.0-beta.2', ['v4.6.0', 'v4.7.0-beta.1', 'v4.7.0-beta.2', 'v4.7.0']), '4.7.0-beta.1');
});

test('previousVersion: SemVer order, not lexical order, decides (4.10.0 is above 4.9.0)', () => {
  assert.equal(previousVersion('4.10.0', ['v4.8.0', 'v4.9.0', 'v4.10.0']), '4.9.0');
  assert.equal(previousVersion('4.10.0', ['v4.10.0', 'v4.9.0', 'v4.8.0']), '4.9.0');
  assert.equal(previousVersion('4.7.0-beta.10', ['v4.7.0-beta.2', 'v4.7.0-beta.9', 'v4.7.0-beta.10']), '4.7.0-beta.9');
});

test('previousVersion: duplicates, non-semver tags, an unknown version and a missing history are handled', () => {
  assert.equal(previousVersion('4.7.0', ['v4.6.0', 'v4.6.0', '0.5.1', 'vNEXT', 'v1.2', 'v4.6.1+build']), '4.6.0');
  assert.equal(previousVersion('4.7.0', ['0.5.1', 'vNEXT']), null);
  assert.equal(previousVersion('4.7.0', []), null);
  assert.equal(previousVersion('4.7.0', ['v4.7.0']), null);
  // the version need not be tagged itself
  assert.equal(previousVersion('4.8.0', ['v4.7.0']), '4.7.0');
  assert.equal(previousVersion('not-a-version', ['v4.7.0']), null);
});

// ---- hardening of the version helpers and the bullet pattern ----

test('compareVersions: numbers above 2^53 are compared exactly, in the core and in prerelease identifiers', () => {
  assert.ok(compareVersions('9007199254740993.0.0', '9007199254740992.0.0') > 0);
  assert.ok(compareVersions('9007199254740992.0.0', '9007199254740993.0.0') < 0);
  assert.ok(compareVersions('1.0.0-9007199254740993', '1.0.0-9007199254740992') > 0);
  assert.ok(compareVersions('1.0.0-9007199254740992', '1.0.0-9007199254740993') < 0);
  assert.equal(compareVersions('9007199254740993.0.0', '9007199254740993.0.0'), 0);
  const big = `1${'0'.repeat(399)}`;
  const small = '9'.repeat(399);
  assert.ok(compareVersions(`${big}.0.0`, `${small}.0.0`) > 0);
  assert.ok(compareVersions(`${small}.0.0`, `${big}.0.0`) < 0);
});

test('previousVersion: anything but an array of tags gives null', () => {
  for (const tags of [null, undefined, {}, 'v4.6.0', 4]) {
    assert.equal(previousVersion('4.7.0', tags), null, String(tags));
  }
});

test('parseReleaseTag and previousVersion accept only strings', () => {
  assert.equal(parseReleaseTag(['v1.2.3']), null);
  assert.equal(parseReleaseTag(null), null);
  assert.equal(parseReleaseTag(undefined), null);
  assert.equal(parseReleaseTag({ toString: () => 'v1.2.3' }), null);
  assert.equal(previousVersion(['4.7.0'], ['v4.6.0']), null);
  assert.equal(previousVersion({ toString: () => '4.7.0' }, ['v4.6.0']), null);
});

test('parseTagMessage: a bare bullet marker is an empty bullet and does not close the section', () => {
  const p = parseTagMessage('v1\n\nSummary.\n\nAdded\n- a\n-\n- b\n');
  assert.deepEqual(p.sections.added, ['a', 'b']);
  assert.deepEqual(parseTagMessage('v1\n\nSummary.\n\nAdded\n*\n+\n- c\n').sections.added, ['c']);
});

// ---- Task 4: notes assembly, the annotated-tag guard and the CLI ----

const SCRIPT = here('./release-notes.js');
const NOTES_KEYS = ['version', 'previous', 'summary', 'sections', 'url', 'releasesUrl'];
const V47_TAGS = ['v4.6.0', 'v4.7.0-beta.1', 'v4.7.0-beta.2', 'v4.7.0', '0.5.1', 'vNEXT'];

test('buildReleaseNotes: the real v4.7.0 message gives the six keys, the previous release and the body', () => {
  const message = tagMessage('v4.7.0');
  const { notes, body, warnings } = buildReleaseNotes({ tag: 'v4.7.0', message, tags: V47_TAGS });
  assert.deepEqual(Object.keys(notes), NOTES_KEYS);
  assert.equal(notes.version, '4.7.0');
  assert.equal(notes.previous, '4.6.0');
  assert.equal(notes.url, 'https://github.com/strvmarv/total-recall/releases/tag/v4.7.0');
  assert.equal(notes.releasesUrl, 'https://github.com/strvmarv/total-recall/releases');
  assert.match(notes.summary, /^A release version bump is now one checked command/);
  assert.equal(notes.sections.added.length, 3);
  assert.deepEqual(Object.keys(notes.sections), SECTION_KEYS);
  assert.equal(body, `${message.split('\n').slice(2).join('\n').replace(/\s+$/, '')}\n`);
  assert.deepEqual(warnings, []);
});

test('buildReleaseNotes: a prerelease takes its previous version from every lower version', () => {
  const { notes } = buildReleaseNotes({ tag: 'v4.7.0-beta.2', message: tagMessage('v4.7.0-beta.2'), tags: V47_TAGS });
  assert.equal(notes.version, '4.7.0-beta.2');
  assert.equal(notes.previous, '4.7.0-beta.1');
  assert.equal(notes.url, 'https://github.com/strvmarv/total-recall/releases/tag/v4.7.0-beta.2');
});

test('buildReleaseNotes: a stable tag without a summary throws and names the tag (v4.2.1)', () => {
  assert.throws(() => buildReleaseNotes({ tag: 'v4.2.1', message: tagMessage('v4.2.1'), tags: [] }), /no summary in the tag message of v4\.2\.1/);
  // the first paragraph is a heading, so there is no summary either
  assert.throws(
    () => buildReleaseNotes({ tag: 'v1.0.0', message: 'v1.0.0\n\nAdded\n- a bullet\n', tags: [] }),
    /no summary in the tag message of v1\.0\.0/,
  );
});

test('buildReleaseNotes: a prerelease without a summary gives an empty summary and one warning', () => {
  const { notes, warnings } = buildReleaseNotes({ tag: 'v1.0.0-beta.1', message: 'v1.0.0-beta.1\n', tags: [] });
  assert.equal(notes.summary, '');
  assert.equal(notes.previous, null);
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /no summary in the tag message of v1\.0\.0-beta\.1/);
});

test('buildReleaseNotes: a tag that is not a release tag throws, and an empty title is an error for a stable tag', () => {
  assert.throws(() => buildReleaseNotes({ tag: 'vNEXT', message: 'vNEXT\n\nSummary.\n', tags: [] }), /^Error: vNEXT is not a release tag$/);
  assert.throws(() => buildReleaseNotes({ tag: 'v1.0.0', message: '\n\nSummary.\n', tags: [] }), /^Error: no title in the tag message of v1\.0\.0$/);
  const { warnings } = buildReleaseNotes({ tag: 'v1.0.0-rc.1', message: '\n\nSummary.\n', tags: [] });
  assert.equal(warnings.length, 1);
  assert.match(warnings[0], /no title in the tag message of v1\.0\.0-rc\.1/);
});

// A temporary repository. Every git call runs with a fixed identity, no user or system config and
// signing off, so a developer's own git configuration cannot change what the tags contain.
const tempDirs = [];
const tmp = (prefix) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
};
after(() => { for (const dir of tempDirs) fs.rmSync(dir, { recursive: true, force: true }); });

const emptyConfig = path.join(tmp('rn-gitconfig-'), 'empty');
fs.writeFileSync(emptyConfig, '');
const gitEnv = {
  ...process.env,
  GIT_AUTHOR_NAME: 'Test', GIT_AUTHOR_EMAIL: 'test@example.invalid',
  GIT_COMMITTER_NAME: 'Test', GIT_COMMITTER_EMAIL: 'test@example.invalid',
  GIT_CONFIG_GLOBAL: emptyConfig, GIT_CONFIG_SYSTEM: emptyConfig,
};
function git(cwd, ...args) {
  const r = spawnSync('git', ['-c', 'commit.gpgsign=false', '-c', 'tag.gpgsign=false', ...args], { cwd, env: gitEnv, encoding: 'utf8' });
  assert.equal(r.status, 0, `git ${args.join(' ')}: ${r.stderr}`);
  return r.stdout;
}
function makeRepo() {
  const repo = tmp('rn-repo-');
  git(repo, 'init', '-q', '-b', 'main');
  git(repo, 'commit', '-q', '--allow-empty', '-m', 'first');
  git(repo, 'tag', 'v1.0.0'); // lightweight
  git(repo, 'tag', '-a', 'v1.0.1', '-m', 'v1.0.1\n\nA real summary.\n\nAdded\n- one thing\n');
  git(repo, 'tag', '-a', 'v1.1.0', '-m', 'v1.1.0'); // stable, no summary
  git(repo, 'tag', '-a', 'v1.1.1-beta.1', '-m', 'v1.1.1-beta.1'); // prerelease, no summary
  git(repo, 'branch', 'v1.0.3'); // a branch is not a tag
  return repo;
}
const repo = makeRepo();

test('readAnnotatedTag: an annotated tag gives its message and the tag list', () => {
  const { message, tags } = readAnnotatedTag('v1.0.1', { cwd: repo });
  assert.match(message, /^v1\.0\.1\n\nA real summary\.\n\nAdded\n- one thing\n/);
  assert.deepEqual([...tags].sort(), ['v1.0.0', 'v1.0.1', 'v1.1.0', 'v1.1.1-beta.1']);
});

test('readAnnotatedTag: the tag list is one tag per entry even under git column.ui=always', () => {
  const colRepo = tmp('rn-col-');
  git(colRepo, 'init', '-q', '-b', 'main');
  git(colRepo, 'commit', '-q', '--allow-empty', '-m', 'first');
  const names = ['v1.0.0', 'v1.0.1', 'v1.1.0', 'v1.1.1', 'v2.0.0', 'v2.0.1', 'v2.1.0', 'v2.1.1'];
  for (const name of names) git(colRepo, 'tag', '-a', name, '-m', `${name}\n\nSummary.\n`);
  // With column.ui=always git prints columns even when its output is a pipe. readAnnotatedTag spawns git
  // with the process environment, so the setting is put there for the call.
  const columnEnv = { ...gitEnv, GIT_CONFIG_COUNT: '1', GIT_CONFIG_KEY_0: 'column.ui', GIT_CONFIG_VALUE_0: 'always' };
  const saved = process.env;
  process.env = { ...saved, ...columnEnv };
  try {
    const { tags } = readAnnotatedTag('v2.1.1', { cwd: colRepo });
    assert.deepEqual([...tags].sort(), names);
  } finally {
    process.env = saved;
  }
});

test('readAnnotatedTag: a lightweight tag fails closed with "not an annotated tag"', () => {
  assert.throws(() => readAnnotatedTag('v1.0.0', { cwd: repo }), /v1\.0\.0 is not an annotated tag/);
});

test('readAnnotatedTag: a missing tag, or a branch of that name, throws a different error', () => {
  for (const tag of ['v9.9.9', 'v1.0.3']) {
    assert.throws(() => readAnnotatedTag(tag, { cwd: repo }), (e) => e.message.includes(tag) && !/not an annotated tag/.test(e.message), tag);
  }
});

test('readAnnotatedTag: a name that is not a release tag throws before any git call', () => {
  const nowhere = path.join(os.tmpdir(), 'rn-no-such-directory');
  for (const tag of ['vNEXT', 'v1.0.*', '--help', '']) {
    assert.throws(() => readAnnotatedTag(tag, { cwd: nowhere }), /is not a release tag/, tag);
  }
});

test('stripSignature: a PGP or SSH signature block is cut from the message, nothing else is', () => {
  const body = 'v1.2.3\n\nA summary.\n\nAdded\n- one thing\n';
  const pgp = `${body}-----BEGIN PGP SIGNATURE-----\n\niQEzBAABCAAdFiEE\n=abcd\n-----END PGP SIGNATURE-----\n`;
  const ssh = `${body}-----BEGIN SSH SIGNATURE-----\nU1NIU0lH\n-----END SSH SIGNATURE-----\n`;
  assert.equal(stripSignature(pgp), body);
  assert.equal(stripSignature(ssh), body);
  assert.equal(stripSignature(body), body);
  assert.equal(stripSignature('x'), 'x');
  // An x509 (gpgsm) signature is cut too.
  const x509 = `${body}-----BEGIN SIGNED MESSAGE-----\nMIAGCSqGSIb3DQEHAqCAMIACAQEx\n-----END SIGNED MESSAGE-----\n`;
  assert.equal(stripSignature(x509), body);
  // A blank line left between the message and the armour does not stay at the end of the message.
  assert.equal(stripSignature(`${body}\n-----BEGIN PGP SIGNATURE-----\n\nabc\n-----END PGP SIGNATURE-----\n`), body);
  assert.equal(stripSignature(`${body}\n\n-----BEGIN SSH SIGNATURE-----\nabc\n-----END SSH SIGNATURE-----\n`), body);
  // A message that is not signed keeps its own trailing blank lines.
  assert.equal(stripSignature(`${body}\n`), `${body}\n`);
  // The summary and the release body never see the signature.
  const built = buildReleaseNotes({ tag: 'v1.2.3', message: stripSignature(pgp), tags: [] });
  assert.equal(built.notes.summary, 'A summary.');
  assert.ok(!built.body.includes('SIGNATURE'));
});

test('readAnnotatedTag: a nonexistent --cwd is reported as such, not blamed on git', () => {
  const nowhere = path.join(os.tmpdir(), 'rn-no-such-directory');
  assert.throws(() => readAnnotatedTag('v1.0.1', { cwd: nowhere }), (e) => e.message === `--cwd ${nowhere} is not a directory`);
  const file = path.join(tmp('rn-file-'), 'f');
  fs.writeFileSync(file, '');
  assert.throws(() => readAnnotatedTag('v1.0.1', { cwd: file }), /is not a directory/);
});

const runCli = (args, { cwd = os.tmpdir() } = {}) => spawnSync(process.execPath, [SCRIPT, ...args], { cwd, env: gitEnv, encoding: 'utf8' });

test('CLI: --notes-out may repeat and --body-out writes the body', () => {
  const out = tmp('rn-out-');
  const r = runCli(['v1.0.1', '--cwd', repo, '--notes-out', 'a.json', '--notes-out', 'b.json', '--body-out', 'body.md'], { cwd: out });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  const a = fs.readFileSync(path.join(out, 'a.json'), 'utf8');
  assert.equal(a, fs.readFileSync(path.join(out, 'b.json'), 'utf8'));
  assert.ok(a.endsWith('}\n'));
  assert.ok(a.startsWith('{\n  "version": "1.0.1",'));
  const notes = JSON.parse(a);
  assert.deepEqual(Object.keys(notes), NOTES_KEYS);
  assert.equal(notes.previous, '1.0.0');
  assert.equal(notes.summary, 'A real summary.');
  assert.deepEqual(notes.sections.added, ['one thing']);
  assert.equal(fs.readFileSync(path.join(out, 'body.md'), 'utf8'), 'A real summary.\n\nAdded\n- one thing\n');
});

test('CLI: a repeated --body-out or --cwd is a usage error', () => {
  const out = tmp('rn-out-');
  for (const [opt, second] of [['--body-out', 'b.md'], ['--cwd', repo]]) {
    const args = ['v1.0.1', '--cwd', repo, '--body-out', 'a.md', opt, second];
    const r = runCli(args, { cwd: out });
    assert.equal(r.status, 2, opt);
    assert.match(r.stderr, new RegExp(`^${opt} may be given only once$`, 'm'), opt);
    assert.match(r.stderr, /usage: node scripts\/release-notes\.js/, opt);
  }
  assert.deepEqual(fs.readdirSync(out), [], 'nothing written');
});

test('CLI --check: a valid tag prints what was parsed, and the writer line is unchanged', () => {
  const r = runCli(['v1.0.1', '--check', '--cwd', repo]);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.ok(r.stdout.includes('release notes of v1.0.1 are valid\n'), r.stdout);
  assert.ok(r.stdout.includes('release notes OK: v1.0.1: summary 15 characters; added/changed/fixed 1/0/0\n'), r.stdout);
  const out = tmp('rn-out-');
  const w = runCli(['v1.0.1', '--cwd', repo, '--body-out', 'b.md'], { cwd: out });
  assert.equal(w.stdout, 'wrote the release notes of v1.0.1\n');
});

test('CLI --check: a stable tag without a summary exits 1 with an ::error:: line', () => {
  const r = runCli(['v1.1.0', '--check', '--cwd', repo]);
  assert.equal(r.status, 1);
  assert.match(r.stdout, /^::error::no summary in the tag message of v1\.1\.0$/m);
});

test('CLI --check: a prerelease without a summary exits 0 with a ::warning:: line', () => {
  const r = runCli(['v1.1.1-beta.1', '--check', '--cwd', repo]);
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.match(r.stdout, /^::warning::no summary in the tag message of v1\.1\.1-beta\.1$/m);
});

test('CLI: a lightweight tag or a missing tag exits 1 with an ::error:: line and writes nothing', () => {
  const out = tmp('rn-out-');
  for (const tag of ['v1.0.0', 'v9.9.9', 'vNEXT']) {
    const r = runCli([tag, '--cwd', repo, '--notes-out', 'n.json'], { cwd: out });
    assert.equal(r.status, 1, tag);
    assert.match(r.stdout, /^::error::/m, tag);
  }
  assert.deepEqual(fs.readdirSync(out), []);
});

test('CLI: a writer run for a stable tag without a summary exits 1 and creates no file', () => {
  const out = tmp('rn-out-');
  const r = runCli(['v1.1.0', '--cwd', repo, '--notes-out', 'n.json', '--body-out', 'b.md'], { cwd: out });
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.match(r.stdout, /^::error::no summary in the tag message of v1\.1\.0$/m);
  assert.deepEqual(fs.readdirSync(out), []);
});

test('CLI: a writer run for a prerelease without a summary writes an empty summary and exits 0', () => {
  const out = tmp('rn-out-');
  const r = runCli(['v1.1.1-beta.1', '--cwd', repo, '--notes-out', 'n.json'], { cwd: out });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.match(r.stdout, /^::warning::no summary/m);
  const raw = fs.readFileSync(path.join(out, 'n.json'), 'utf8');
  assert.match(raw, /^ {2}"summary": "",$/m);
  assert.equal(JSON.parse(raw).summary, '');
});

test('CLI: an ::error:: message escapes %, CR and LF so it stays one line', () => {
  const bad = path.join(os.tmpdir(), 'rn-50%-done\nline2');
  const r = runCli(['v1.0.1', '--check', '--cwd', bad]);
  assert.equal(r.status, 1, r.stdout + r.stderr);
  assert.equal(r.stdout.split('\n').filter((l) => l !== '').length, 1);
  assert.ok(r.stdout.startsWith('::error::--cwd '), r.stdout);
  assert.ok(r.stdout.includes('rn-50%25-done%0Aline2 is not a directory'), r.stdout);
});

test('CLI: an option that is followed by another flag is a usage error, not a file name', () => {
  const out = tmp('rn-out-');
  for (const opt of ['--notes-out', '--body-out', '--cwd']) {
    const r = runCli(['v1.0.1', opt, '--check'], { cwd: out });
    assert.equal(r.status, 2, opt);
    assert.match(r.stderr, new RegExp(`^${opt} needs a value$`, 'm'), opt);
    assert.match(r.stderr, /usage: node scripts\/release-notes\.js/, opt);
  }
  assert.deepEqual(fs.readdirSync(out), [], 'no file named --check');
});

test('CLI: a usage error exits 2', () => {
  for (const args of [[], ['v1.0.1'], ['v1.0.1', '--bogus'], ['v1.0.1', '--notes-out'], ['v1.0.1', 'v1.0.0', '--check'], ['v1.0.1', '--check', '--body-out', 'b.md']]) {
    const r = runCli(args, { cwd: repo });
    assert.equal(r.status, 2, JSON.stringify(args));
    assert.match(r.stderr, /usage: node scripts\/release-notes\.js/, JSON.stringify(args));
  }
});

test('CLI: runs through a symlinked path (the main-module check resolves real paths)', (t) => {
  const link = path.join(tmp('rn-link-'), 'release-notes.js');
  try { fs.symlinkSync(SCRIPT, link); } catch { t.skip('symlinks are not available'); return; }
  const r = spawnSync(process.execPath, [link, 'v1.1.1-beta.1', '--check', '--cwd', repo], { env: gitEnv, encoding: 'utf8' });
  assert.equal(r.status, 0, r.stdout + r.stderr);
  assert.match(r.stdout, /::warning::/);
});

// ---- Task 6: AGENTS.md says what the release checks ----

test('AGENTS.md documents the tag message shape the release checks', () => {
  const agents = fs.readFileSync(here('../AGENTS.md'), 'utf8');
  const start = agents.indexOf('### Release flow');
  assert.ok(start >= 0, 'AGENTS.md has no "### Release flow" heading');
  const rest = agents.slice(start + '### Release flow'.length);
  const next = rest.search(/^### /m);
  const flow = next < 0 ? rest : rest.slice(0, next);
  for (const needle of ['release-notes.json', 'release-notes.js', '--check', 'summary paragraph', 'annotated', '-F', '--cleanup=whitespace']) {
    assert.ok(flow.includes(needle), `the Release flow section of AGENTS.md does not mention ${needle}`);
  }
  assert.ok(!flow.includes('`#` lines are kept'), 'the Release flow section of AGENTS.md says # lines are kept, which git does not do');
  const changelog = fs.readFileSync(here('../CHANGELOG.md'), 'utf8');
  assert.ok(changelog.includes('#91'), 'CHANGELOG.md does not mention #91');
});

// ---- Task 8: the what's-new notice is documented ----

test('the docs describe the what\'s-new notice and the updates.notice setting', () => {
  const config = fs.readFileSync(here('../docs/configuration.md'), 'utf8');
  assert.ok(config.includes('[updates]'), 'docs/configuration.md has no [updates] block');
  assert.ok(config.includes('notice'), 'docs/configuration.md does not mention the notice setting');
  const agents = fs.readFileSync(here('../AGENTS.md'), 'utf8');
  for (const needle of ['whatsNew', 'whats-new/', 'hermes-mcp-client', 'relay']) {
    assert.ok(agents.includes(needle), `AGENTS.md does not mention ${needle}`);
  }
  const start = agents.indexOf('### What happens on `session_start`');
  assert.ok(start >= 0, 'AGENTS.md has no "What happens on `session_start`" heading');
  const rest = agents.slice(start + 10);
  const next = rest.search(/^### /m);
  const lifecycle = next < 0 ? rest : rest.slice(0, next);
  const hostIds = ['claude-code', 'pi', 'copilot-cli', 'cursor', 'codex', 'gemini-cli', 'kiro', 'opencode', 'hermes', 'cline', 'windsurf', 'zed', 'vscode', 'other'];
  for (const needle of ['whatsNew', 'whats-new/', ...hostIds.map((id) => `\`${id}\``)]) {
    assert.ok(lifecycle.includes(needle), `the session_start section of AGENTS.md does not mention ${needle}`);
  }
  const changelog = fs.readFileSync(here('../CHANGELOG.md'), 'utf8');
  assert.ok(changelog.includes('#99'), 'CHANGELOG.md does not mention #99');
});
