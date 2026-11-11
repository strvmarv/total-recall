// skills/develop/scripts/counted.js — which comments count (spec section 9): write access, comment
// flattening, counted checkpoints, the governing pin and the latest progress. Zero dependencies, ESM,
// `node:fs` only.
//
//   node counted.js select <rawCommentsFile> <permissionsFile>
//   node counted.js write-access <permFile> [httpStatus]
//   node counted.js permissions <manifestFile> <outFile>

import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { isLogin, parseCheckpoint, PROGRESS_PHASES } from './checkpoints.js';
import { readPages } from './pages.js';

// `isLogin` is defined once, in checkpoints.js; this is the same function.
export { isLogin };

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

// ---- write access ----

const WRITE_ROLES = ['admin', 'maintain', 'write'];
const WRITE_PERMISSIONS = ['admin', 'write'];

/** True for a `role_name` of admin/maintain/write or a `permission` of admin/write. */
export function isWriteAccess(perm) {
  if (!isObject(perm)) return false;
  if (typeof perm.role_name === 'string' && WRITE_ROLES.includes(perm.role_name)) return true;
  if (typeof perm.permission === 'string' && WRITE_PERMISSIONS.includes(perm.permission)) return true;
  return false;
}

/** Parses the text of a permission answer; throws `unparseable` unless it is a permission object. */
function parsePermission(text) {
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    throw new Error('unparseable');
  }
  if (!isObject(value) || (typeof value.role_name !== 'string' && typeof value.permission !== 'string')) {
    throw new Error('unparseable');
  }
  return value;
}

/** Only the two fields `isWriteAccess` reads are kept in the permissions map. */
function pickPermission(value) {
  const picked = {};
  if (typeof value.permission === 'string') picked.permission = value.permission;
  if (typeof value.role_name === 'string') picked.role_name = value.role_name;
  return picked;
}

/**
 * The answer of one permission lookup. `permJson` is the text of the `out` file, or null when there
 * is none (`ghpin.js run` removes it on a failure). No file and a 404 is "no write access", not an
 * error; no file and anything else is `unavailable` (blocker 3); text that is not a permission object
 * is `unparseable`.
 */
export function isWriteAccessResult(permJson, httpStatus) {
  if (permJson === null || permJson === undefined) {
    if (httpStatus === 404) return false;
    throw new Error('unavailable');
  }
  return isWriteAccess(parsePermission(permJson));
}

// ---- reading comments ----

/**
 * Flattens the raw output of `gh api graphql --paginate` for `queries/comments.graphql` into comments
 * in posted order. Fails closed: it throws on everything `readPages` throws on, on a page with an
 * `errors` member, on a page without `data.repository.issue.comments.nodes` (including `issue: null`)
 * and when the last page says `hasNextPage` is not false.
 */
export function flattenComments(raw) {
  const pages = readPages(raw);
  const comments = [];
  for (const page of pages) {
    if (Object.hasOwn(page, 'errors')) throw new Error('comments page has errors');
    const conn = page?.data?.repository?.issue?.comments;
    if (!isObject(conn) || !Array.isArray(conn.nodes)) throw new Error('comments page has no nodes');
    for (const node of conn.nodes) comments.push(toComment(node));
  }
  const last = pages[pages.length - 1].data.repository.issue.comments;
  if (!isObject(last.pageInfo) || last.pageInfo.hasNextPage !== false) {
    throw new Error('comments: more pages than were fetched');
  }
  return comments;
}

function actor(value) {
  if (value === null || value === undefined) return { login: null, type: null };
  if (!isObject(value) || typeof value.login !== 'string') throw new Error('comment actor is malformed');
  return { login: value.login, type: typeof value.__typename === 'string' ? value.__typename : null };
}

function toComment(node) {
  if (!isObject(node) || typeof node.id !== 'string' || typeof node.createdAt !== 'string'
    || typeof node.body !== 'string' || typeof node.isMinimized !== 'boolean') {
    throw new Error('comment is malformed');
  }
  const author = actor(node.author);
  const editor = actor(node.editor);
  return {
    id: node.id,
    createdAt: node.createdAt,
    authorLogin: author.login,
    authorType: author.type,
    editorLogin: editor.login,
    editorType: editor.type,
    lastEditedAt: typeof node.lastEditedAt === 'string' ? node.lastEditedAt : null,
    isMinimized: node.isMinimized,
    body: node.body,
  };
}

// ---- which comments count ----

/** An actor passes when it is a GitHub `User`, has a valid login and `hasWrite(login)`. */
function actorPasses(login, type, hasWrite) {
  return login !== null && type === 'User' && isLogin(login) && hasWrite(login) === true;
}

/**
 * A comment counts when its author is a `User` with write access, its editor (if any) is too, an editor
 * that is null with `lastEditedAt` set (a deleted editor account) does not count, and it is not minimized.
 */
export function counts(c, hasWrite) {
  if (c.isMinimized) return false;
  if (!actorPasses(c.authorLogin, c.authorType, hasWrite)) return false;
  if (c.editorLogin !== null) return actorPasses(c.editorLogin, c.editorType, hasWrite);
  return c.lastEditedAt === null;
}

/** Counted comments that carry exactly one parseable checkpoint line, in posted order. */
export function countedCheckpoints(comments, hasWrite) {
  const list = [];
  for (const c of comments) {
    if (!counts(c, hasWrite)) continue;
    const parsed = parseCheckpoint(c.body);
    if (!parsed.ok) continue;
    list.push({ id: c.id, createdAt: c.createdAt, phase: parsed.phase, fields: parsed.fields });
  }
  return list;
}

function latestWhere(cps, predicate) {
  for (let i = cps.length - 1; i >= 0; i -= 1) {
    if (predicate(cps[i])) return cps[i];
  }
  return null;
}

/** The latest counted checkpoint with phase `defined` or `reapproved`. */
export function governingPin(cps) {
  return latestWhere(cps, (c) => c.phase === 'defined' || c.phase === 'reapproved');
}

/** The latest counted checkpoint with a phase in `PROGRESS_PHASES`. */
export function latestProgress(cps) {
  return latestWhere(cps, (c) => PROGRESS_PHASES.includes(c.phase));
}

// ---- the shared selection ----

/** The permissions map must be a plain object whose values are null or permission objects. */
function checkPermissions(map) {
  if (!isObject(map)) throw new Error('permissions must be an object');
  for (const key of Object.keys(map)) {
    if (map[key] !== null && !isObject(map[key])) throw new Error('permissions entry must be an object or null');
  }
  return map;
}

/**
 * The one selection that `select`, and later `live` and `route`, all call. `permissions` is
 * `{<login>: {role_name?, permission?} | null}`; a login absent from it (tested with `Object.hasOwn`,
 * so `constructor` and `toString` are logins like any other) has not been looked up, and its comments
 * do not count until it has. Comments by an unlisted login that carry a checkpoint line put that login
 * in `needPermissions` (only `User` actors with a valid login; a Bot's login is never looked up),
 * and `complete` is false: the caller fetches them and selects again, and never acts on `pin`,
 * `progress` or a route from an incomplete pass.
 */
export function selectState(raw, permissions) {
  const map = checkPermissions(permissions ?? {});
  const comments = flattenComments(raw);
  const looked = (login) => Object.hasOwn(map, login);
  const hasWrite = (login) => looked(login) && isWriteAccess(map[login]);

  const needPermissions = [];
  for (const c of comments) {
    if (!parseCheckpoint(c.body).ok) continue;
    for (const [login, type] of [[c.authorLogin, c.authorType], [c.editorLogin, c.editorType]]) {
      if (login !== null && type === 'User' && isLogin(login) && !looked(login) && !needPermissions.includes(login)) {
        needPermissions.push(login);
      }
    }
  }

  const cps = countedCheckpoints(comments, hasWrite);
  const latest = cps.length > 0 ? cps[cps.length - 1] : null;
  const pin = governingPin(cps);
  const progress = latestProgress(cps);
  return {
    complete: needPermissions.length === 0,
    needPermissions,
    counted: cps.map((c) => c.id),
    checkpoints: cps.map((c) => ({ id: c.id, phase: c.phase, createdAt: c.createdAt })),
    latest: latest && { id: latest.id, phase: latest.phase, createdAt: latest.createdAt, fields: latest.fields },
    pin: pin && { id: pin.id, phase: pin.phase, fields: pin.fields },
    progress: progress && { id: progress.id, phase: progress.phase, fields: progress.fields },
  };
}

// ---- CLI ----

export const SUBCOMMANDS = ['select', 'write-access', 'permissions'];

function readTextIfPresent(file) {
  return fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : null;
}

/** A missing permissions file is `{}`. */
function readPermissionsFile(file) {
  const text = file ? readTextIfPresent(file) : null;
  if (text === null) return {};
  return checkPermissions(JSON.parse(text));
}

function runSelect(args) {
  const [rawFile, permissionsFile] = args;
  if (!rawFile) throw new Error('usage: select <rawCommentsFile> <permissionsFile>');
  const raw = fs.readFileSync(rawFile, 'utf8');
  return selectState(raw, readPermissionsFile(permissionsFile));
}

function parseStatus(arg) {
  if (arg === undefined) return null;
  if (!/^\d{3}$/.test(arg)) throw new Error('bad status');
  return Number(arg);
}

function runWriteAccess(args) {
  const [permFile, status] = args;
  if (!permFile) throw new Error('usage: write-access <permFile> [httpStatus]');
  return { writeAccess: isWriteAccessResult(readTextIfPresent(permFile), parseStatus(status)) };
}

/** One manifest entry's value for the map: null for "no access" by 404, else the picked permission fields. */
function entryValue(entry) {
  if (!isObject(entry) || typeof entry.file !== 'string') throw new Error('bad manifest entry');
  if (typeof entry.login !== 'string' || !isLogin(entry.login)) throw new Error('bad login');
  const status = entry.httpStatus === undefined ? null : entry.httpStatus;
  if (status !== null && !Number.isInteger(status)) throw new Error('bad status');
  const text = readTextIfPresent(entry.file);
  isWriteAccessResult(text, status); // throws `unavailable` or `unparseable`
  return text === null ? null : pickPermission(parsePermission(text));
}

function runPermissions(args) {
  const [manifestFile, outFile] = args;
  if (!manifestFile || !outFile) throw new Error('usage: permissions <manifestFile> <outFile>');
  const manifest = JSON.parse(fs.readFileSync(manifestFile, 'utf8'));
  if (!Array.isArray(manifest)) throw new Error('manifest must be an array');
  // Every entry is resolved before anything is written: one `unavailable` fails the whole manifest.
  const entries = manifest.map((entry) => [entry.login, entryValue(entry)]);
  const map = readPermissionsFile(outFile);
  const merged = new Map(Object.entries(map));
  for (const [login, value] of entries) merged.set(login, value);
  const out = {};
  for (const [login, value] of merged) {
    Object.defineProperty(out, login, { value, enumerable: true, writable: true, configurable: true });
  }
  fs.writeFileSync(outFile, JSON.stringify(out));
  return { ok: true, logins: [...merged.keys()] };
}

function dispatch(argv) {
  const [cmd, ...rest] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'select': return runSelect(rest);
    case 'write-access': return runWriteAccess(rest);
    case 'permissions': return runPermissions(rest);
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
