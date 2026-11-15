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
    s = cut > 0 ? s.slice(0, cut) : s.slice(0, MAX_SUMMARY);
    if (/[\ud800-\udbff]$/.test(s)) s = s.slice(0, -1);
  }
  return s;
}
