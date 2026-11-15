// scripts/release-notes.js — turns a release's annotated tag message into the release notes.
// Zero dependencies, ESM.
//
//   node scripts/release-notes.js <tag> [--check] [--notes-out <file>...] [--body-out <file>] [--cwd <dir>]
//
// Exit codes: 0 ok, 1 the tag cannot give release notes, 2 a usage error. Diagnostics go to stdout as
// GitHub Actions workflow commands (`::error::`, `::warning::`). Design: docs/superpowers/specs/2026-11-15-whats-new-notice-design.md, section 1.
//
// The summary sanitiser below is specified in that document (section 2, "Untrusted input") and is also
// implemented in the engine; both are tested against scripts/fixtures/summary-sanitiser.json.

import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const MAX_SUMMARY = 200;

// The whitespace set is spelled out (not JS `\s`) so that it matches .NET's char.IsWhiteSpace, which the
// engine's implementation uses: tab, LF, VT, FF, CR, space, U+0085, U+00A0, U+1680, U+2000-200A, U+2028,
// U+2029, U+202F, U+205F, U+3000. U+FEFF is not whitespace in .NET and is not here.
const WHITESPACE = /[ \t\n\v\f\r\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+/g;
// The same set as a character-class body, for patterns that must not cross whitespace.
const WS_CLASS = ' \\t\\n\\v\\f\\r\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000';
const MD_LINK = /\[([^\]]*)\]\([^)]*\)/g;
const ANGLE_URL = new RegExp(`<(https?://[^<>${WS_CLASS}]*)>`, 'gi');
// ESC [ parameter bytes, intermediate bytes, final byte (CSI sequences, such as colours).
const ANSI = /\u001b\[[0-?]*[ -/]*[@-~]/g;
// Control characters other than the whitespace ones above.
const CONTROL = /[\u0000-\u0008\u000e-\u001f\u007f-\u0084\u0086-\u009f]/g;

/**
 * One line of plain text, at most 200 characters, for a release summary or any text fetched for one.
 * The passes run in this order: (1) ANSI sequences, (2) control characters except whitespace, (3) a
 * markdown link `[text](url)` becomes `text` (repeated until none is left, so nested links go too; a URL
 * ends at its first `)`) and `<http(s)://...>` loses its angle brackets (scheme case-insensitive, other
 * schemes untouched), (4) whitespace runs become one space, (5) trim spaces only (a byte order mark is not
 * whitespace here, as in .NET), (6) cut at the last space at or before 200 UTF-16 code units (no ellipsis;
 * one longer word is cut at 200; a cut that would leave half of a surrogate pair drops that half).
 * Pass 1 comes before pass 2 because pass 2 removes the ESC character.
 */
export function sanitizeSummary(text) {
  let s = String(text ?? '');
  s = s.replace(ANSI, '');
  s = s.replace(CONTROL, '');
  // Each replacement shortens the string, so the loop ends.
  for (let prev = null; prev !== s;) {
    prev = s;
    s = s.replace(MD_LINK, '$1');
  }
  s = s.replace(ANGLE_URL, '$1');
  s = s.replace(WHITESPACE, ' ').replace(/^ | $/g, '');
  if (s.length > MAX_SUMMARY) {
    const cut = s.lastIndexOf(' ', MAX_SUMMARY);
    if (cut > 0) {
      s = s.slice(0, cut);
    } else {
      // A hard cut inside one long word: do not keep half of a surrogate pair.
      const splitsPair = /[\ud800-\udbff]/.test(s[MAX_SUMMARY - 1]) && /[\udc00-\udfff]/.test(s[MAX_SUMMARY]);
      s = s.slice(0, splitsPair ? MAX_SUMMARY - 1 : MAX_SUMMARY);
    }
  }
  return s;
}

// ---- the tag message ----

// A heading opens a section when, after optional `#`s, it starts with Added, Changed or Fixed
// ("Added (from 4.7.0-beta.1)" counts). Any other non-bullet line is a heading that closes the section.
const SECTION_HEADING = /^#*\s*(added|changed|fixed)\b/i;
// The shape of a heading line on its own: the word alone, with an optional parenthesised suffix and/or colon.
// "Fixed the release workflow." does not have this shape, so it stays a summary.
const HEADING_LINE = /^#*\s*(added|changed|fixed)(\s*\([^)]*\))?\s*:?\s*$/i;
// A bullet marker followed by text, or a bare marker (an empty bullet, skipped without closing the section).
const BULLET = /^[-*+](?:\s+(.*))?$/;

const isBlank = (line) => line.trim() === '';

/**
 * Splits an annotated tag message into its title (first line), summary (the first paragraph after the
 * title, sanitised; '' when there is none), the Added/Changed/Fixed bullets, and `body`: the message
 * without its title line and the blank line after it (the GitHub release body). Never throws.
 */
export function parseTagMessage(message) {
  const lines = String(message ?? '').replace(/\r\n?/g, '\n').split('\n');
  const title = (lines[0] ?? '').trim();
  const rest = lines.slice(1);
  const bodyStart = rest.length > 0 && isBlank(rest[0]) ? 1 : 0;
  const body = rest.slice(bodyStart).join('\n');

  let i = 0;
  while (i < rest.length && isBlank(rest[i])) i += 1;
  const paragraphStart = i;

  // A first paragraph is not a summary when its first line is a bullet or a section heading (whatever
  // follows); the section walk then starts at that paragraph so its bullets are kept. Otherwise the
  // summary ends at the first blank line or bullet line.
  const notSummary = paragraphStart < rest.length
    && (BULLET.test(rest[paragraphStart]) || HEADING_LINE.test(rest[paragraphStart].trim()));
  let summary = '';
  if (!notSummary) {
    while (i < rest.length && !isBlank(rest[i]) && !BULLET.test(rest[i])) i += 1;
    summary = sanitizeSummary(rest.slice(paragraphStart, i).join(' '));
  }

  const sections = { added: [], changed: [], fixed: [] };
  let current = null;
  let last = -1;
  for (const line of rest.slice(notSummary ? paragraphStart : i)) {
    if (isBlank(line)) continue;
    if (/^\s/.test(line)) {
      if (current !== null && last >= 0) sections[current][last] += ` ${line.trim()}`;
      continue;
    }
    const bullet = BULLET.exec(line);
    if (bullet) {
      const text = (bullet[1] ?? '').trim();
      if (current !== null && text !== '') last = sections[current].push(text) - 1;
      else last = -1;
      continue;
    }
    const heading = SECTION_HEADING.exec(line);
    current = heading ? heading[1].toLowerCase() : null;
    last = -1;
  }
  return { title, summary, sections, body };
}

// ---- version tags and the previous release ----

// vMAJOR.MINOR.PATCH[-prerelease], SemVer 2.0.0 without build metadata: no leading zeros in the numeric
// parts or in numeric prerelease identifiers, identifiers are non-empty [0-9A-Za-z-].
const NUM = '(?:0|[1-9]\\d*)';
const PRE_ID = '(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)';
const RELEASE_TAG = new RegExp(`^v(${NUM})\\.(${NUM})\\.(${NUM})(?:-(${PRE_ID}(?:\\.${PRE_ID})*))?$`);

/** The bare version of a `vMAJOR.MINOR.PATCH[-prerelease]` tag, or null for any other tag. */
export function parseReleaseTag(tag) {
  if (typeof tag !== 'string') return null;
  const m = RELEASE_TAG.exec(tag);
  return m === null ? null : m[0].slice(1);
}

// A bare version as [major, minor, patch] numbers and the prerelease identifiers ([] for a release).
function splitVersion(version) {
  const m = RELEASE_TAG.exec(`v${version}`);
  if (m === null) throw new TypeError(`not a release version: ${version}`);
  return { core: [m[1], m[2], m[3]], pre: m[4] === undefined ? [] : m[4].split('.') };
}

const NUMERIC_ID = /^\d+$/;

// Order of two decimal strings without leading zeros (the tag pattern forbids them), exact at any size.
const compareNumeric = (p, q) => (p.length !== q.length ? Math.sign(p.length - q.length) : p < q ? -1 : p > q ? 1 : 0);

/**
 * SemVer 2.0.0 precedence of two bare versions: negative, zero or positive. A release is above its
 * prereleases; prerelease identifiers compare one by one, numeric ones numerically and below alphanumeric
 * ones, and when all else is equal the shorter list is lower.
 */
export function compareVersions(a, b) {
  const x = splitVersion(a);
  const y = splitVersion(b);
  for (let i = 0; i < 3; i += 1) {
    const c = compareNumeric(x.core[i], y.core[i]);
    if (c !== 0) return c;
  }
  if (x.pre.length === 0 && y.pre.length === 0) return 0;
  if (x.pre.length === 0) return 1;
  if (y.pre.length === 0) return -1;
  for (let i = 0; i < Math.min(x.pre.length, y.pre.length); i += 1) {
    const p = x.pre[i];
    const q = y.pre[i];
    if (p === q) continue;
    const pNum = NUMERIC_ID.test(p);
    const qNum = NUMERIC_ID.test(q);
    if (pNum && qNum) return compareNumeric(p, q);
    if (pNum !== qNum) return pNum ? -1 : 1;
    return p < q ? -1 : 1;
  }
  return Math.sign(x.pre.length - y.pre.length);
}

/**
 * The version a release is compared with: for a stable `version` the highest lower stable version among
 * `tags`, for a prerelease the highest lower version of any kind. Tags that are not release tags are
 * ignored, as is `version` itself and anything above it. Null when there is none, or when `version` is
 * not a release version.
 */
export function previousVersion(version, tags) {
  if (typeof version !== 'string' || !Array.isArray(tags) || parseReleaseTag(`v${version}`) === null) return null;
  const stable = !version.includes('-');
  let best = null;
  for (const tag of tags) {
    const v = parseReleaseTag(tag);
    if (v === null || compareVersions(v, version) >= 0) continue;
    if (stable && v.includes('-')) continue;
    if (best === null || compareVersions(v, best) > 0) best = v;
  }
  return best;
}

// ---- the release notes ----

const REPO_URL = 'https://github.com/strvmarv/total-recall';

/**
 * The release notes of `tag` from its annotated tag `message` and the repository's tag list: the
 * `release-notes.json` object, the release body (the message without its title line and the blank line
 * after it, ending in one newline) and the warnings. A stable tag with no title line or no summary throws;
 * a prerelease gets a warning for each instead (and an empty summary).
 */
export function buildReleaseNotes({ tag, message, tags }) {
  const version = parseReleaseTag(tag);
  if (version === null) throw new Error(`${tag} is not a release tag`);
  const parsed = parseTagMessage(message);
  const problems = [];
  if (parsed.title === '') problems.push(`no title in the tag message of ${tag}`);
  if (parsed.summary === '') problems.push(`no summary in the tag message of ${tag}`);
  const warnings = [];
  if (problems.length > 0) {
    if (!version.includes('-')) throw new Error(problems[0]);
    warnings.push(...problems);
  }
  const trimmed = parsed.body.replace(/\s+$/, '');
  return {
    notes: {
      version,
      previous: previousVersion(version, tags),
      summary: parsed.summary,
      sections: parsed.sections,
      url: `${REPO_URL}/releases/tag/v${version}`,
      releasesUrl: `${REPO_URL}/releases`,
    },
    body: trimmed === '' ? '' : `${trimmed}\n`,
    warnings,
  };
}

function isDirectory(dir) {
  try { return fs.statSync(dir).isDirectory(); } catch { return false; }
}

// Runs git with an argument array and no shell.
function runGit(args, cwd) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (r.error) throw new Error(`cannot run git: ${r.error.message}`);
  return r;
}

// A signed tag keeps its signature at the end of the message (`%(contents)` includes it). The signature
// is cut from its first armour line to the end (PGP, SSH, or x509 from gpgsm), so it never reaches the
// summary or the release body.
const SIGNATURE_START = /^-----BEGIN (?:PGP SIGNATURE|SSH SIGNATURE|SIGNED MESSAGE)-----$/m;

/** The tag message without a trailing PGP, SSH or x509 signature block (and the blank line the cut leaves). */
export function stripSignature(message) {
  const m = SIGNATURE_START.exec(message);
  return m === null ? message : message.slice(0, m.index).replace(/\n{2,}$/, '\n');
}

/**
 * The message of the annotated tag `tag` and the repository's tag list. Fails closed: a name that is not
 * a release tag is refused before git runs, and the tag must be a tag object (`git cat-file -t` prints
 * `tag`), not a lightweight tag. The tag is only ever passed as the exact ref `refs/tags/<tag>`, so it is
 * never a pattern and a branch of the same name is not mistaken for it.
 */
export function readAnnotatedTag(tag, { cwd } = {}) {
  if (parseReleaseTag(tag) === null) throw new Error(`${tag} is not a release tag`);
  if (cwd !== undefined && !isDirectory(cwd)) throw new Error(`--cwd ${cwd} is not a directory`);
  const ref = `refs/tags/${tag}`;
  const type = runGit(['cat-file', '-t', ref], cwd);
  if (type.status !== 0) throw new Error(`cannot read ${ref}: ${String(type.stderr).trim() || 'git failed'}`);
  if (type.stdout.trim() !== 'tag') throw new Error(`${tag} is not an annotated tag`);
  const contents = runGit(['for-each-ref', '--format=%(contents)', ref], cwd);
  if (contents.status !== 0) throw new Error(`cannot read the message of ${ref}: ${String(contents.stderr).trim() || 'git failed'}`);
  const list = runGit(['tag', '-l'], cwd);
  if (list.status !== 0) throw new Error(`cannot list the tags: ${String(list.stderr).trim() || 'git failed'}`);
  return { message: stripSignature(contents.stdout), tags: list.stdout.split('\n').filter((t) => t !== '') };
}

// ---- command line ----

const USAGE = [
  'usage: node scripts/release-notes.js <tag> --check [--cwd <dir>]',
  '       node scripts/release-notes.js <tag> [--notes-out <file>...] [--body-out <file>] [--cwd <dir>]',
  '  --check validates only; output files are relative to the current directory, --cwd is the repository',
].join('\n');

// Workflow-command text: `%`, CR and LF are escaped so one diagnostic stays one line.
const annotate = (level, text) => `::${level}::${text.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A')}\n`;

/** Runs the command line. Returns {code, out, err}; it prints nothing itself. */
export function main(argv) {
  const usage = (msg) => ({ code: 2, out: '', err: `${msg}\n${USAGE}\n` });
  let check = false;
  let bodyOut;
  let cwd;
  const notesOut = [];
  const positional = [];
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    if (a === '--check') check = true;
    else if (a === '--notes-out' || a === '--body-out' || a === '--cwd') {
      const v = argv[i + 1];
      if (v === undefined || v.startsWith('--')) return usage(`${a} needs a value`);
      i += 1;
      if (a === '--notes-out') notesOut.push(v);
      else if (a === '--cwd') cwd = v;
      else bodyOut = v;
    } else if (a.startsWith('--')) return usage(`unknown option ${a}`);
    else positional.push(a);
  }
  if (positional.length !== 1) return usage(positional.length ? 'one tag at a time' : 'a tag is required');
  const writes = notesOut.length > 0 || bodyOut !== undefined;
  if (check && writes) return usage('--check writes no file');
  if (!check && !writes) return usage('--check, --notes-out or --body-out is required');

  const tag = positional[0];
  let built;
  try {
    const { message, tags } = readAnnotatedTag(tag, { cwd });
    built = buildReleaseNotes({ tag, message, tags });
  } catch (e) {
    return { code: 1, out: annotate('error', e.message), err: '' };
  }
  let out = built.warnings.map((w) => annotate('warning', w)).join('');
  if (check) return { code: 0, out: `${out}release notes of ${tag} are valid\n`, err: '' };
  try {
    const json = `${JSON.stringify(built.notes, null, 2)}\n`;
    for (const file of notesOut) fs.writeFileSync(file, json);
    if (bodyOut !== undefined) fs.writeFileSync(bodyOut, built.body);
  } catch (e) {
    return { code: 1, out: out + annotate('error', `cannot write the release notes: ${e.message}`), err: '' };
  }
  out += `wrote the release notes of ${tag}\n`;
  return { code: 0, out, err: '' };
}

// Run as a command, not when imported. Both paths are resolved, so a symlinked checkout or script
// still runs (a gate that exits 0 without running would fail open).
function isMainModule() {
  const self = fs.realpathSync(fileURLToPath(import.meta.url));
  // `node scripts/release-notes` (no extension) leaves argv[1] without `.js`.
  for (const candidate of [process.argv[1], `${process.argv[1]}.js`]) {
    try {
      if (candidate && fs.realpathSync(candidate) === self) return true;
    } catch { /* not a file; try the next */ }
  }
  return false;
}

if (isMainModule()) {
  const r = main(process.argv.slice(2));
  if (r.out) process.stdout.write(r.out);
  if (r.err) process.stderr.write(r.err);
  process.exitCode = r.code;
}
