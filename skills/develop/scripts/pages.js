// skills/develop/scripts/pages.js — the one scanner for the raw output of `gh api graphql --paginate`
// (spec sections 9 and 11). A library: it has no CLI. `counted.js` and `history.js` both call it.
// Zero dependencies, ESM.

/**
 * Splits the raw output of `gh api graphql --paginate` into its page objects. The output is several
 * JSON objects back to back, one per page, with whitespace (or nothing) between them. The scanner
 * tracks brace depth outside strings and honours `\"` and `\\` inside a string, so a body such as
 * `5" pipe }` or one ending in a backslash cannot end a page early.
 *
 * Throws on zero pages, on text between or after pages that is not whitespace, on a truncated object
 * and on an object that is not valid JSON.
 */
export function readPages(raw) {
  if (typeof raw !== 'string') throw new Error('pages: input is not text');
  const pages = [];
  let depth = 0;
  let inString = false;
  let escaped = false;
  let start = -1;
  for (let i = 0; i < raw.length; i += 1) {
    const ch = raw[i];
    if (depth === 0) {
      if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r') continue;
      if (ch !== '{') throw new Error('pages: text outside a JSON object');
      depth = 1;
      start = i;
      continue;
    }
    if (inString) {
      if (escaped) escaped = false;
      else if (ch === '\\') escaped = true;
      else if (ch === '"') inString = false;
      continue;
    }
    if (ch === '"') {
      inString = true;
    } else if (ch === '{') {
      depth += 1;
    } else if (ch === '}') {
      depth -= 1;
      if (depth === 0) {
        pages.push(JSON.parse(raw.slice(start, i + 1)));
        start = -1;
      }
    }
  }
  if (depth !== 0) throw new Error('pages: truncated object');
  if (pages.length === 0) throw new Error('pages: no pages');
  return pages;
}
