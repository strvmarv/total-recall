// scripts/release-notes.js — turns a release's annotated tag message into the release notes.
// Zero dependencies, ESM. Design: docs/superpowers/specs/2026-11-15-whats-new-notice-design.md, section 1.
//
// The summary sanitiser below is specified in that document (section 2, "Untrusted input") and is also
// implemented in the engine; both are tested against scripts/fixtures/summary-sanitiser.json.

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
const BULLET = /^[-*+]\s+(.*)$/;

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
      const text = bullet[1].trim();
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
  const m = RELEASE_TAG.exec(String(tag ?? ''));
  return m === null ? null : m[0].slice(1);
}

// A bare version as [major, minor, patch] numbers and the prerelease identifiers ([] for a release).
function splitVersion(version) {
  const m = RELEASE_TAG.exec(`v${version}`);
  if (m === null) throw new TypeError(`not a release version: ${version}`);
  return { core: [Number(m[1]), Number(m[2]), Number(m[3])], pre: m[4] === undefined ? [] : m[4].split('.') };
}

const NUMERIC_ID = /^\d+$/;

/**
 * SemVer 2.0.0 precedence of two bare versions: negative, zero or positive. A release is above its
 * prereleases; prerelease identifiers compare one by one, numeric ones numerically and below alphanumeric
 * ones, and when all else is equal the shorter list is lower.
 */
export function compareVersions(a, b) {
  const x = splitVersion(a);
  const y = splitVersion(b);
  for (let i = 0; i < 3; i += 1) {
    if (x.core[i] !== y.core[i]) return x.core[i] < y.core[i] ? -1 : 1;
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
    if (pNum && qNum) return Number(p) < Number(q) ? -1 : 1;
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
  if (parseReleaseTag(`v${version}`) === null) return null;
  const stable = !String(version).includes('-');
  let best = null;
  for (const tag of tags) {
    const v = parseReleaseTag(tag);
    if (v === null || compareVersions(v, version) >= 0) continue;
    if (stable && v.includes('-')) continue;
    if (best === null || compareVersions(v, best) > 0) best = v;
  }
  return best;
}
