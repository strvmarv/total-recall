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
  assert.deepEqual(SUBCOMMANDS, ['parse', 'verify-files']);
  for (const name of SUBCOMMANDS) {
    const { parsed } = run([name]);
    assert.notEqual(parsed && parsed.error, 'unknown subcommand', name);
  }
  const bad = run(['bogus']);
  assert.deepEqual(bad.parsed, { ok: false, error: 'unknown subcommand' });
  assert.equal(bad.status, 1);
  assert.deepEqual(run([]).parsed, { ok: false, error: 'unknown subcommand' });
});

// ---- this repository's own profile ----

test('.github/develop.md: the repository profile parses, pins the account and runs the .NET suite, the shim suites and the catalog guard', () => {
  const root = path.join(HERE, '..', '..', '..');
  const markdown = fs.readFileSync(path.join(root, '.github', 'develop.md'), 'utf8');
  const result = parseProfile(markdown);
  assert.equal(result.ok, true);
  assert.equal(result.profile.ghAccount, 'strvmarv');
  assert.equal(result.profile.ticketStandard, 'on');

  // The shim command as `npm run test:shim` lists it, with each directory entry as an unquoted glob
  // that `sh` expands (so no Node version matters; the skill's tests sit in scripts/).
  const shim = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8')).scripts['test:shim'];
  const entries = shim.replace(/^node --test /, '').split(/\s+/);
  const globs = entries.map((entry) => {
    if (!entry.endsWith('/')) return entry;
    const dir = entry === 'skills/develop/' ? 'skills/develop/scripts/' : entry;
    return `${dir}*.test.js`;
  });
  assert.deepEqual(result.profile.verify, [
    'dotnet test src/TotalRecall.sln --filter "Category!=Integration"',
    `node --test ${globs.join(' ')}`,
    // CI's catalog drift guard: build, then diff `dump-catalog` on stdout (no file written outside the worktree).
    [
      'dotnet build src/TotalRecall.Host/TotalRecall.Host.csproj --configuration Release -nologo -v q',
      'dotnet run --project src/TotalRecall.Host --configuration Release --no-build -- dump-catalog | diff -u catalog.json -',
    ].join('\n'),
  ]);
});

// ---- verify-files ----

test('verify-files: this repo profile writes each block byte-exact to verify-<i>.sh (1-based)', () => {
  const root = path.join(HERE, '..', '..', '..');
  const profileFile = path.join(root, '.github', 'develop.md');
  const dir = tmpDir();
  const { parsed, status } = run(['verify-files', profileFile, dir]);
  assert.equal(status, 0);
  assert.equal(parsed.ok, true);
  assert.deepEqual(parsed.files, [
    path.join(dir, 'verify-1.sh'),
    path.join(dir, 'verify-2.sh'),
    path.join(dir, 'verify-3.sh'),
  ]);
  const expected = parseProfile(fs.readFileSync(profileFile, 'utf8')).profile.verify;
  assert.equal(expected.length, 3);
  parsed.files.forEach((file, i) => {
    const bytes = fs.readFileSync(file);
    assert.equal(bytes.toString('utf8'), `${expected[i]}\n`);
    assert.ok(!bytes.includes(13), 'LF only');
    assert.notDeepEqual([...bytes.subarray(0, 3)], [0xef, 0xbb, 0xbf], 'no BOM');
  });
  assert.equal(
    fs.readFileSync(parsed.files[0], 'utf8'),
    'dotnet test src/TotalRecall.sln --filter "Category!=Integration"\n',
  );
});

test('verify-files: a multi-line block from a CRLF profile is written with LF only', () => {
  const file = writeText(
    ['```text', 'tr-develop/profile-v1', '```', '', '```sh verify', 'echo a', 'echo "b!" ', '```', ''].join('\r\n'),
  );
  const dir = tmpDir();
  const { parsed } = run(['verify-files', file, dir]);
  assert.equal(parsed.ok, true);
  assert.equal(fs.readFileSync(parsed.files[0], 'utf8'), 'echo a\necho "b!"\n');
});

test('verify-files: a profile with no verify blocks lists no files, and none are written', () => {
  const file = writeText('```text\ntr-develop/profile-v1\n```\n');
  const dir = tmpDir();
  const { parsed } = run(['verify-files', file, dir]);
  assert.deepEqual(parsed, { ok: true, files: [] });
  assert.deepEqual(fs.readdirSync(dir), []);
});

test('verify-files: no profile is an empty list; an invalid profile prints the parse error shape', () => {
  const none = run(['verify-files', writeText('# nothing here\n'), tmpDir()]);
  assert.deepEqual(none.parsed, { ok: true, files: [] });

  const invalid = writeText('```text\ntr-develop/profile-v1\nbase: bad base\n```\n');
  const dir = tmpDir();
  const result = run(['verify-files', invalid, dir]);
  assert.deepEqual(result.parsed, { ok: false, error: 'bad-value:base' });
  assert.equal(result.status, 1);
  assert.deepEqual(fs.readdirSync(dir), []);

  const empty = writeText('```text\ntr-develop/profile-v1\n```\n\n```sh verify\n\n```\n');
  assert.deepEqual(run(['verify-files', empty, tmpDir()]).parsed, { ok: false, error: 'bad-value:verify' });
});

test('verify-files: a missing profile file or a missing argument fails', () => {
  const missing = run(['verify-files', path.join(tmpDir(), 'absent.md'), tmpDir()]);
  assert.equal(missing.parsed.ok, false);
  assert.equal(missing.status, 1);
  assert.equal(run(['verify-files']).parsed.ok, false);
});
