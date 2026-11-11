// skills/develop/scripts/regions.js — ticket text normalization, fence detection, region
// extraction and hashing (spec section 8). Zero dependencies, ESM, `node:crypto`/`node:fs` only.
//
//   node regions.js body-json <issueJsonFile> <outTitleFile> <outBodyFile>
//   node regions.js body <titleFile> <bodyFile>
//   node regions.js ac <bodyFile> [outFile]
//   node regions.js approach <bodyFile> [outFile]
//   node regions.js neutralize <textFile>
//   node regions.js fence <textFile>

import fs from 'node:fs';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const AC_BEGIN = '<!-- tr-develop:ac-begin -->';
const AC_END = '<!-- tr-develop:ac-end -->';
const APPROACH_BEGIN = '<!-- tr-develop:approach-begin -->';
const APPROACH_END = '<!-- tr-develop:approach-end -->';

const MARKERS = {
  ac: { begin: AC_BEGIN, end: AC_END },
  approach: { begin: APPROACH_BEGIN, end: APPROACH_END },
};

const APPROACH_MAX_LINES = 60;
const APPROACH_MAX_CHARS = 4000;

/** Splits text on any line ending (\r\n, \r or \n) without keeping the terminator. */
function splitLines(text) {
  return text.split(/\r\n|\r|\n/);
}

/**
 * CommonMark opening-fence test: up to three spaces of indent, then three or more backticks or
 * tildes. A backtick fence's info string may not itself contain a backtick. Returns the fence
 * character and run length, or null when the line is not a valid opening fence.
 */
function matchFenceOpen(line) {
  const m = /^ {0,3}(`{3,}|~{3,})(.*)$/.exec(line);
  if (!m) return null;
  const run = m[1];
  const info = m[2];
  const ch = run[0];
  if (ch === '`' && info.includes('`')) return null;
  return { ch, len: run.length };
}

/**
 * CommonMark closing-fence test: up to three spaces of indent, the same character at least as
 * many times as the opening run, and nothing else but trailing whitespace.
 */
function matchFenceClose(line, ch, len) {
  const re = ch === '`'
    ? new RegExp(`^ {0,3}\`{${len},}[ \\t]*$`)
    : new RegExp(`^ {0,3}~{${len},}[ \\t]*$`);
  return re.test(line);
}

/**
 * Marks every line that is a fence delimiter or fence content as `true`. An unclosed fence runs to
 * the end of the text. This is the only place fence boundaries are decided; `outsideFences` and the
 * task-list-marker removal in `normalize` both use it.
 */
function computeFenceMask(lines) {
  const mask = new Array(lines.length).fill(false);
  let i = 0;
  while (i < lines.length) {
    const open = matchFenceOpen(lines[i]);
    if (!open) {
      i += 1;
      continue;
    }
    mask[i] = true;
    let j = i + 1;
    let closedAt = -1;
    while (j < lines.length) {
      mask[j] = true;
      if (matchFenceClose(lines[j], open.ch, open.len)) {
        closedAt = j;
        break;
      }
      j += 1;
    }
    i = closedAt === -1 ? lines.length : closedAt + 1;
  }
  return mask;
}

/**
 * Removes a task-list marker ([ ], [x], [X]) that directly follows a list bullet (-, *, +) or an
 * ordered number (digits followed by . or )) and whitespace, at the start of the line, at any
 * nesting depth. Only the bracketed token is removed; the bullet and surrounding whitespace stay.
 */
function stripTaskMarker(line) {
  return line.replace(/^(\s*(?:[-*+]|\d+[.)])\s+)\[[ xX]\]/, '$1');
}

/**
 * CRLF -> LF; removes a task-list marker outside fenced code blocks (a marker inside a fence is
 * content, not a checkbox); strips trailing whitespace on every line and trailing blank lines.
 * Nothing else.
 */
export function normalize(text) {
  const lines = splitLines(text);
  const mask = computeFenceMask(lines);
  const processed = lines.map((line, idx) => (mask[idx] ? line : stripTaskMarker(line)));
  const trimmed = processed.map((line) => line.replace(/[ \t]+$/, ''));
  while (trimmed.length > 0 && trimmed[trimmed.length - 1] === '') trimmed.pop();
  return trimmed.join('\n');
}

/** SHA-256 hex over UTF-8 of the normalized text. */
export function regionHash(text) {
  return crypto.createHash('sha256').update(normalize(text), 'utf8').digest('hex');
}

/** SHA-256 hex over UTF-8 of JSON.stringify([normalized title, normalized body]). */
export function bodyHash(title, body) {
  const payload = JSON.stringify([normalize(title), normalize(body)]);
  return crypto.createHash('sha256').update(payload, 'utf8').digest('hex');
}

/**
 * Lines (1-based `n`, CRLF already turned into LF) that are not inside a fenced code block and are
 * not fence delimiter lines themselves. The only place fences are decided.
 */
export function outsideFences(text) {
  const lines = splitLines(text);
  const mask = computeFenceMask(lines);
  const result = [];
  for (let i = 0; i < lines.length; i += 1) {
    if (!mask[i]) result.push({ n: i + 1, line: lines[i] });
  }
  return result;
}

/**
 * Finds the begin/end markers for `kind` only among `outsideFences` lines (matched trimmed), then
 * returns every RAW line strictly between their line numbers, fenced lines included.
 */
export function extractRegion(body, kind) {
  const markers = MARKERS[kind];
  const found = outsideFences(body);
  const begins = [];
  const ends = [];
  for (const { n, line } of found) {
    const trimmed = line.trim();
    if (trimmed === markers.begin) begins.push(n);
    else if (trimmed === markers.end) ends.push(n);
  }
  if (begins.length === 0 || ends.length === 0) return { ok: false, error: 'missing' };
  if (begins.length > 1 || ends.length > 1) return { ok: false, error: 'duplicate' };
  const beginN = begins[0];
  const endN = ends[0];
  if (endN <= beginN) return { ok: false, error: 'order' };
  const rawLines = splitLines(body);
  const text = rawLines.slice(beginN, endN - 1).join('\n');
  return { ok: true, text };
}

/** `ok` is false when the normalized text has more than 60 lines or more than 4,000 characters. */
export function approachLimits(text) {
  const normalized = normalize(text);
  const lines = normalized.length === 0 ? 0 : normalized.split('\n').length;
  const chars = normalized.length;
  const ok = lines <= APPROACH_MAX_LINES && chars <= APPROACH_MAX_CHARS;
  return { ok, lines, chars };
}

/**
 * Neutralizes outsider text kept verbatim in a collapsed "Original report" block: every `<!--`
 * becomes `&lt;!--`, and a line-leading `tr-develop` becomes `tr&#8209;develop`, so it can never
 * create or break a region or a checkpoint line.
 */
export function neutralize(text) {
  return text.replace(/<!--/g, '&lt;!--').replace(/^tr-develop/gm, 'tr&#8209;develop');
}

/** A backtick fence one longer than the longest backtick run in `text`, at least three. */
export function fenceFor(text) {
  const runs = text.match(/`+/g) || [];
  const maxLen = runs.reduce((m, s) => Math.max(m, s.length), 0);
  const len = Math.max(3, maxLen + 1);
  return '`'.repeat(len);
}

// ---- CLI ----

export const SUBCOMMANDS = ['body-json', 'body', 'ac', 'approach', 'neutralize', 'fence'];

function runBodyJson(args) {
  const [issueJsonFile, outTitleFile, outBodyFile] = args;
  if (!issueJsonFile || !outTitleFile || !outBodyFile) {
    throw new Error('usage: body-json <issueJsonFile> <outTitleFile> <outBodyFile>');
  }
  const issue = JSON.parse(fs.readFileSync(issueJsonFile, 'utf8'));
  const title = issue.title ?? '';
  const body = issue.body ?? '';
  fs.writeFileSync(outTitleFile, title);
  fs.writeFileSync(outBodyFile, body);
  return { ok: true };
}

function runBody(args) {
  const [titleFile, bodyFile] = args;
  if (!titleFile || !bodyFile) throw new Error('usage: body <titleFile> <bodyFile>');
  const title = fs.readFileSync(titleFile, 'utf8');
  const body = fs.readFileSync(bodyFile, 'utf8');
  return { ok: true, hash: bodyHash(title, body) };
}

function runRegion(kind, args) {
  const [bodyFile, outFile] = args;
  if (!bodyFile) throw new Error(`usage: ${kind} <bodyFile> [outFile]`);
  const body = fs.readFileSync(bodyFile, 'utf8');
  const result = extractRegion(body, kind);
  if (!result.ok) return { ok: false, error: result.error };
  if (outFile) fs.writeFileSync(outFile, result.text);
  if (kind === 'approach') {
    const limits = approachLimits(result.text);
    if (!limits.ok) return { ok: false, error: 'too-long', lines: limits.lines, chars: limits.chars };
    return { ok: true, hash: regionHash(result.text), lines: limits.lines, chars: limits.chars };
  }
  return { ok: true, hash: regionHash(result.text) };
}

function runNeutralize(args) {
  const [textFile] = args;
  if (!textFile) throw new Error('usage: neutralize <textFile>');
  const text = fs.readFileSync(textFile, 'utf8');
  return { text: neutralize(text) };
}

function runFence(args) {
  const [textFile] = args;
  if (!textFile) throw new Error('usage: fence <textFile>');
  const text = fs.readFileSync(textFile, 'utf8');
  return { fence: fenceFor(text) };
}

function dispatch(argv) {
  const [cmd, ...rest] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'body-json': return runBodyJson(rest);
    case 'body': return runBody(rest);
    case 'ac': return runRegion('ac', rest);
    case 'approach': return runRegion('approach', rest);
    case 'neutralize': return runNeutralize(rest);
    case 'fence': return runFence(rest);
    default: return { ok: false, error: 'unknown subcommand' };
  }
}

function main() {
  const argv = process.argv.slice(2);
  let result;
  try {
    result = dispatch(argv);
  } catch (err) {
    result = { ok: false, error: err.message };
  }
  process.stdout.write(`${JSON.stringify(result)}\n`);
  if (result && result.ok === false) process.exitCode = 1;
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

if (isMain(import.meta.url)) main();
