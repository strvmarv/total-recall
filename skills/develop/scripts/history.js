// skills/develop/scripts/history.js — recovers the text a pinned `bodyHash` was computed over
// (spec section 11, Re-approval step 1). Zero dependencies, ESM, `node:fs` only.
//
//   node history.js find <editsRawFile> <renamesRawFile> <issueJsonFile> <pinnedBodyHash|none> <outDir>
//
// `none` (or an absent, empty or non-64-hex pin) prints {found:false} without reading any file.
//
// The raw files are the output of `gh api graphql --paginate` for `queries/edits.graphql` and
// `queries/renames.graphql`; the issue file is the REST `issue` read (for the current title and body).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { bodyHash } from './regions.js';
import { readPages } from './pages.js';

const HASH_RE = /^[0-9a-f]{64}$/;

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

/** The last page of a non-null connection must say there is nothing more to fetch. */
function assertComplete(conn) {
  const next = isObject(conn.pageInfo) ? conn.pageInfo.hasNextPage : undefined;
  if (next === false) return;
  // An empty connection that carries no pageInfo at all (`{totalCount:0,nodes:[]}`) has nothing more.
  if (conn.pageInfo === undefined && conn.nodes.length === 0) return;
  throw new Error('history: more pages than were fetched');
}

/**
 * Concatenates the nodes of `issue[key]` over the pages. Fails closed on an `errors` member, a page
 * with no `data.repository.issue`, an issue without the connection member, a connection that is
 * neither null nor `{nodes:[...]}`, and a last page that still has a next page. A null connection
 * is an empty list (an issue never edited).
 */
function connectionNodes(pages, key) {
  if (!Array.isArray(pages) || pages.length === 0) throw new Error('history: no pages');
  const nodes = [];
  let last = null;
  for (const page of pages) {
    if (!isObject(page)) throw new Error('history: page is not an object');
    if (Object.hasOwn(page, 'errors')) throw new Error('history: page has errors');
    const issue = isObject(page.data) && isObject(page.data.repository) ? page.data.repository.issue : undefined;
    if (!isObject(issue)) throw new Error('history: page has no issue');
    if (!Object.hasOwn(issue, key)) throw new Error(`history: page has no ${key}`);
    const conn = issue[key];
    last = conn;
    if (conn === null) continue;
    if (!isObject(conn) || !Array.isArray(conn.nodes)) throw new Error(`history: ${key} has no nodes`);
    nodes.push(...conn.nodes);
  }
  if (last !== null) assertComplete(last);
  return nodes;
}

function time(value) {
  return typeof value === 'string' ? Date.parse(value) : Number.NaN;
}

/** Rename events (text fields present, parseable time) sorted ascending by `createdAt`, stable. */
function renameEvents(pages) {
  const events = [];
  for (const node of connectionNodes(pages, 'timelineItems')) {
    if (!isObject(node)) continue;
    if (typeof node.previousTitle !== 'string' || typeof node.currentTitle !== 'string') continue;
    const at = time(node.createdAt);
    if (!Number.isFinite(at)) continue;
    events.push({ at, previousTitle: node.previousTitle, currentTitle: node.currentTitle });
  }
  return events.map((e, i) => ({ ...e, i })).sort((a, b) => a.at - b.at || a.i - b.i);
}

/** Usable revisions (a string `diff`, no `deletedAt`), newest first, stable. */
function revisions(pages) {
  const list = [];
  for (const node of connectionNodes(pages, 'userContentEdits')) {
    if (!isObject(node)) continue;
    if (typeof node.diff !== 'string') continue;
    if (node.deletedAt !== null && node.deletedAt !== undefined) continue;
    list.push({
      editedAt: typeof node.editedAt === 'string' ? node.editedAt : null,
      at: time(node.editedAt),
      diff: node.diff,
    });
  }
  const key = (r) => (Number.isFinite(r.at) ? r.at : Number.NEGATIVE_INFINITY);
  return list.map((r, i) => ({ ...r, i })).sort((a, b) => {
    const ka = key(a);
    const kb = key(b);
    if (ka === kb) return a.i - b.i;
    return ka > kb ? -1 : 1;
  });
}

/** Titles to try, in order, for a body whose time is `at` (NaN: the current title first). */
function titleChain(at, currentTitle, events) {
  const first = Number.isFinite(at) ? (events.find((e) => e.at > at)?.previousTitle ?? currentTitle) : currentTitle;
  const chain = [first, currentTitle];
  for (const e of events) chain.push(e.previousTitle, e.currentTitle);
  return [...new Set(chain)];
}

/**
 * Finds the title and body that `pinnedBodyHash` was computed over. Candidate bodies, in order: the
 * issue's current body, then each revision newest first. Every candidate is tried against every title
 * in the chain; an exact SHA-256 match cannot be a false positive. A `pinnedBodyHash` that is not 64
 * hex is `{found:false}` (a re-pin with no usable pin must still proceed). Pages that fail closed throw.
 */
export function findRevision(editsPages, renamePages, current, pinnedBodyHash) {
  // An unusable pin is decided first: it must not depend on the pages being readable.
  if (typeof pinnedBodyHash !== 'string' || !HASH_RE.test(pinnedBodyHash)) return { found: false };
  const revs = revisions(editsPages);
  const events = renameEvents(renamePages);
  if (!isObject(current) || typeof current.title !== 'string'
    || (current.body !== null && current.body !== undefined && typeof current.body !== 'string')) {
    throw new Error('history: current issue needs a string title and a string or null body');
  }

  const candidates = [{ source: 'current', editedAt: null, at: Number.NaN, body: current.body ?? '' }];
  for (const r of revs) candidates.push({ source: 'revision', editedAt: r.editedAt, at: r.at, body: r.diff });

  for (const cand of candidates) {
    for (const title of titleChain(cand.at, current.title, events)) {
      if (bodyHash(title, cand.body) === pinnedBodyHash) {
        return { found: true, source: cand.source, editedAt: cand.editedAt, title, body: cand.body };
      }
    }
  }
  return { found: false };
}

// ---- CLI ----

export const SUBCOMMANDS = ['find'];

function readIssue(file) {
  const issue = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (!isObject(issue) || typeof issue.title !== 'string') throw new Error('issue has no string title');
  if (issue.body !== null && issue.body !== undefined && typeof issue.body !== 'string') {
    throw new Error('issue body must be a string or null');
  }
  return { title: issue.title, body: issue.body ?? '' };
}

function runFind(args) {
  const [editsFile, renamesFile, issueFile, pinned, outDir] = args;
  // No usable pin (absent, the literal `none`, or not 64 lowercase hex) is "original unavailable":
  // answer before reading any file, so a re-pin proceeds even when the history fetch failed.
  const fileArgs = args.length >= 3;
  if (fileArgs && (pinned === undefined || !HASH_RE.test(pinned))) return { found: false };
  if (!editsFile || !renamesFile || !issueFile || pinned === undefined || !outDir) {
    throw new Error('usage: find <editsRawFile> <renamesRawFile> <issueJsonFile> <pinnedBodyHash> <outDir>');
  }
  const editsPages = readPages(fs.readFileSync(editsFile, 'utf8'));
  const renamePages = readPages(fs.readFileSync(renamesFile, 'utf8'));
  const current = readIssue(issueFile);
  const result = findRevision(editsPages, renamePages, current, pinned);
  if (!result.found) return { found: false };
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, 'old.title'), result.title);
  fs.writeFileSync(path.join(outDir, 'old.body'), result.body);
  return { found: true, source: result.source, editedAt: result.editedAt };
}

function dispatch(argv) {
  const [cmd, ...rest] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'find': return runFind(rest);
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
