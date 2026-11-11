// skills/develop/scripts/checkpoints.js — the checkpoint grammar (spec section 10): parse, validate,
// carry-forward check, format and read-back. Zero dependencies, ESM, `node:fs` only.
//
//   node checkpoints.js validate <jsonFile>    {checkpoint:{phase,fields}, repo, base, reachable:[sha,...]}
//   node checkpoints.js carry <jsonFile>       {checkpoint:{phase,fields}, prior}   (`prior` required, may be null)
//   node checkpoints.js format <jsonFile>      {phase, fields, summaryFile, outFile, lineFile}
//   node checkpoints.js readback <fetchedCommentFile> <lineFile>

import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { fenceFor } from './regions.js';

// ---- data: grammar, patterns and the per-phase table ----

export const PHASES = [
  'defined', 'reapproved', 'kickoff', 'plan-approved', 'tasks',
  'final-approved', 'pr-open', 'blocked', 'closed',
];

export const PROGRESS_PHASES = [
  'kickoff', 'plan-approved', 'tasks', 'final-approved', 'pr-open', 'closed', 'reapproved',
];

/** The checkpoint line, exactly as fixed by the spec. */
const LINE_RE = /^tr-develop\/v1 phase=(defined|reapproved|kickoff|plan-approved|tasks|final-approved|pr-open|blocked|closed)( [a-zA-Z][a-zA-Z0-9.]*=\S+)*$/;
const KEY_RE = /^[a-zA-Z][a-zA-Z0-9.]*$/;

const BRANCH_RE = /^(feat|fix)\/[a-z0-9][a-z0-9-]{0,62}$/;
const SPEC_RE = /^docs\/superpowers\/specs\/[A-Za-z0-9._-]+\.md$/;
const PLAN_RE = /^docs\/superpowers\/plans\/[A-Za-z0-9._-]+\.md$/;
const SHA_RE = /^[0-9a-f]{40}$/;
const HASH_RE = /^[0-9a-f]{64}$/;
const COUNTER_RE = /^\d+$/;
const LOGIN_RE = /^[A-Za-z0-9](?:[A-Za-z0-9_-]{0,38})$/;
const PR_NUMBER_RE = /^[1-9]\d{0,8}$/;

const TRACKS = ['plan', 'small'];
const STAGES = ['read', 'workspace', 'plan', 'build', 'final', 'finish'];
const BLOCKERS = ['1', '2', '3', '4', '5', '6'];
const REVIEWS = ['agent', 'self'];

const DEFINED_FIELDS = ['track', 'repo', 'base', 'branch', 'bodyHash', 'acHash', 'definedBy', 'definedAt'];
const PROGRESS_BASE = ['track', 'branch', 'head', 'review', 'gh'];

/** Required fields per phase: `all` for every track, `plan` only on the plan track, `small` only on small. */
const REQUIRED = {
  defined: { all: DEFINED_FIELDS, plan: ['spec', 'approvedSha'], small: [] },
  reapproved: {
    all: [...DEFINED_FIELDS, 'head', 'review', 'gh', 'deferred'],
    plan: ['spec', 'approvedSha'],
    small: [],
  },
  kickoff: { all: PROGRESS_BASE, plan: ['spec', 'approvedSha'], small: [] },
  'plan-approved': {
    all: [...PROGRESS_BASE, 'spec', 'approvedSha', 'plan', 'planSha', 'rounds.plan'],
    plan: [],
    small: [],
  },
  tasks: {
    all: [...PROGRESS_BASE, 'deferred'],
    plan: ['spec', 'approvedSha', 'plan', 'planSha', 'tasksDone'],
    small: ['cyclesDone'],
  },
  'final-approved': {
    all: [...PROGRESS_BASE, 'deferred', 'rounds.final'],
    plan: ['spec', 'approvedSha', 'plan', 'planSha', 'tasksDone'],
    small: [],
  },
  'pr-open': {
    all: [...PROGRESS_BASE, 'pr', 'deferred'],
    plan: ['spec', 'approvedSha', 'plan', 'planSha', 'tasksDone'],
    small: [],
  },
  blocked: { all: ['branch', 'stage', 'blocker'], plan: [], small: [] },
  closed: { all: ['branch', 'head', 'gh', 'pr'], plan: [], small: [] },
};

/** `reapproved` invalidates the final loop and has no PR (spec section 11 steps 3 and 6). */
const NEVER_ON_REAPPROVED = ['rounds.final', 'pr'];

/** Fields a new executor's `kickoff` or a `reapproved` must carry forward from the latest progress. */
const CARRY_KEYS = ['plan', 'planSha', 'tasksDone', 'cyclesDone', 'rounds.plan', 'deferred'];

/** A new executor's `kickoff` also resumes the final loop; a `reapproved` invalidates it and never carries it. */
const KICKOFF_CARRY_KEYS = [...CARRY_KEYS, 'rounds.final'];

/** Order of keys in a formatted line: `branch` first, then these, then unknown keys sorted. */
const KEY_ORDER = [
  'branch', 'track', 'repo', 'base', 'bodyHash', 'acHash', 'definedBy', 'definedAt',
  'spec', 'approvedSha', 'head', 'review', 'gh', 'plan', 'planSha', 'rounds.plan',
  'tasksDone', 'cyclesDone', 'deferred', 'rounds.final', 'pr', 'stage', 'blocker',
];

// ---- logins ----

/** The single definition of a valid GitHub login in this skill (re-exported by later scripts). */
export function isLogin(value) {
  return typeof value === 'string' && LOGIN_RE.test(value);
}

// ---- parse ----

/** Trims whitespace, NBSP (U+00A0) and a BOM (U+FEFF): `String.prototype.trim` covers all three. */
function trimLine(line) {
  return line.trim();
}

/** Every line of a text with every `\r` removed first, trimmed. */
function trimmedLines(text) {
  return text.replace(/\r/g, '').split('\n').map(trimLine);
}

export function parseCheckpoint(commentBody) {
  const matches = trimmedLines(String(commentBody)).filter((line) => LINE_RE.test(line));
  if (matches.length === 0) return { ok: false, error: 'no-line' };
  if (matches.length > 1) return { ok: false, error: 'multiple-lines' };
  const line = matches[0];
  const tokens = line.split(' ');
  const phase = tokens[1].slice('phase='.length);
  const fields = {};
  for (const token of tokens.slice(2)) {
    const eq = token.indexOf('=');
    const key = token.slice(0, eq);
    // A repeated key (or a second `phase`) is ambiguous: refuse it rather than guess which wins.
    if (key === 'phase' || Object.hasOwn(fields, key)) return { ok: false, error: 'no-line' };
    fields[key] = token.slice(eq + 1);
  }
  return { ok: true, phase, fields, line };
}

// ---- required fields ----

export function requiredFields(phase, track) {
  const entry = REQUIRED[phase];
  if (!entry) return [];
  if (track === 'plan') return [...entry.all, ...entry.plan];
  if (track === 'small') return [...entry.all, ...entry.small];
  return [...entry.all];
}

// ---- validate ----

function fail(error, key) {
  return { ok: false, error, key };
}

function ciEqual(a, b) {
  return a.toLowerCase() === b.toLowerCase();
}

function validPr(value, repo) {
  const prefix = `https://github.com/${repo}/pull/`;
  if (value.length <= prefix.length) return false;
  if (!ciEqual(value.slice(0, prefix.length), prefix)) return false;
  return PR_NUMBER_RE.test(value.slice(prefix.length));
}

/** Returns true when `value` is acceptable for `key` on `phase`. `ctx.isAncestor` decides reachability. */
function valueOk(key, value, phase, ctx) {
  const defineLike = phase === 'defined' || phase === 'reapproved';
  switch (key) {
    case 'track': return TRACKS.includes(value);
    case 'stage': return STAGES.includes(value);
    case 'blocker': return BLOCKERS.includes(value);
    case 'review': return REVIEWS.includes(value);
    case 'branch': return (phase === 'blocked' && value === '-') || BRANCH_RE.test(value);
    case 'spec': return SPEC_RE.test(value);
    case 'plan': return PLAN_RE.test(value);
    case 'head': return SHA_RE.test(value) && (phase === 'closed' || ctx.isAncestor(value));
    case 'planSha': return SHA_RE.test(value) && (phase === 'closed' || ctx.isAncestor(value));
    case 'approvedSha': return SHA_RE.test(value) && (!defineLike || ctx.isAncestor(value));
    case 'bodyHash':
    case 'acHash': return HASH_RE.test(value);
    case 'tasksDone':
    case 'cyclesDone':
    case 'deferred': return COUNTER_RE.test(value);
    case 'repo': return !defineLike || ciEqual(value, ctx.repo);
    case 'base': return !defineLike || value === ctx.base;
    case 'gh':
    case 'definedBy': return isLogin(value);
    case 'pr': return validPr(value, ctx.repo);
    default:
      if (key.startsWith('rounds.')) return COUNTER_RE.test(value);
      return true;
  }
}

export function validateCheckpoint(cp, ctx) {
  const phase = cp && cp.phase;
  const fields = cp && cp.fields;
  if (!PHASES.includes(phase)) return fail('bad-value', 'phase');
  if (fields === null || typeof fields !== 'object') return fail('bad-value', 'fields');
  const has = (key) => Object.hasOwn(fields, key);

  // Every present value must be a non-empty string: the line cannot carry anything else.
  for (const key of Object.keys(fields)) {
    if (typeof fields[key] !== 'string' || fields[key].length === 0) return fail('bad-value', key);
  }

  // The track decides which fields are required, so it is checked first.
  const trackRequired = REQUIRED[phase].all.includes('track');
  if (!has('track')) {
    if (trackRequired) return fail('missing', 'track');
  } else if (!TRACKS.includes(fields.track)) {
    return fail('bad-value', 'track');
  }
  const track = has('track') ? fields.track : null;

  for (const key of requiredFields(phase, track)) {
    if (!has(key)) return fail('missing', key);
  }
  if (phase === 'reapproved') {
    for (const key of NEVER_ON_REAPPROVED) {
      if (has(key)) return fail('bad-value', key);
    }
  }
  // `plan` and `planSha` are a pair on every phase.
  if (has('planSha') && !has('plan')) return fail('missing', 'plan');
  if (has('plan') && !has('planSha')) return fail('missing', 'planSha');

  const known = KEY_ORDER.filter(has);
  const rounds = Object.keys(fields).filter((key) => key.startsWith('rounds.') && !KEY_ORDER.includes(key)).sort();
  for (const key of [...known, ...rounds]) {
    if (!valueOk(key, fields[key], phase, ctx)) return fail('bad-value', key);
  }
  return { ok: true };
}

// ---- carry-forward (write-time only) ----

export function checkCarry(cp, prior) {
  if (prior === undefined || !cp || typeof cp !== 'object' || !cp.fields) {
    return { ok: false, error: 'bad-input' };
  }
  if (prior !== null && (typeof prior !== 'object' || prior.fields === null || typeof prior.fields !== 'object')) {
    return { ok: false, error: 'bad-input' };
  }
  if (cp.phase !== 'kickoff' && cp.phase !== 'reapproved') return { ok: true };
  if (prior === null) return { ok: true };
  const keys = cp.phase === 'kickoff' ? KICKOFF_CARRY_KEYS : CARRY_KEYS;
  for (const key of keys) {
    if (Object.hasOwn(prior.fields, key) && !Object.hasOwn(cp.fields, key)) {
      return { ok: false, error: 'missing', key };
    }
  }
  return { ok: true };
}

// ---- format ----

function formatError(error, key) {
  const err = new Error(key === undefined ? error : `${error}:${key}`);
  err.code = error;
  err.key = key;
  return err;
}

export function formatCheckpoint(phase, fields) {
  if (!PHASES.includes(phase)) throw formatError('bad-phase');
  if (fields === null || typeof fields !== 'object') throw formatError('bad-input');
  const keys = Object.keys(fields);
  for (const key of keys) {
    if (!KEY_RE.test(key) || key === 'phase') throw formatError('bad-key', key);
    const value = fields[key];
    if (typeof value !== 'string' || value.length === 0 || /\s/.test(value)) {
      throw formatError('bad-value', key);
    }
  }
  const known = KEY_ORDER.filter((key) => keys.includes(key));
  const unknown = keys.filter((key) => !KEY_ORDER.includes(key)).sort();
  const parts = [...known, ...unknown].map((key) => `${key}=${fields[key]}`);
  return ['tr-develop/v1', `phase=${phase}`, ...parts].join(' ');
}

/**
 * True when any line of the summary, read either way a reader could split it (a lone `\r` is a
 * line end for some renderers, and `parseCheckpoint` removes it), matches the grammar after trimming.
 */
function summaryHasCheckpointLine(summary) {
  const views = [summary.split(/\r\n|\r|\n/).map(trimLine), trimmedLines(summary)];
  return views.some((lines) => lines.some((line) => LINE_RE.test(line)));
}

// ---- CLI ----

export const SUBCOMMANDS = ['validate', 'carry', 'format', 'readback'];

function readJson(file) {
  if (!file) throw new Error('missing input file');
  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function isCheckpointInput(cp) {
  return isObject(cp) && typeof cp.phase === 'string' && isObject(cp.fields);
}

function runValidate(args) {
  const input = readJson(args[0]);
  if (!isObject(input) || !isCheckpointInput(input.checkpoint) || typeof input.repo !== 'string'
    || typeof input.base !== 'string' || !Array.isArray(input.reachable)
    || !input.reachable.every((sha) => typeof sha === 'string')) {
    return { ok: false, error: 'bad-input' };
  }
  const reachable = new Set(input.reachable);
  return validateCheckpoint(input.checkpoint, {
    repo: input.repo,
    base: input.base,
    isAncestor: (sha) => reachable.has(sha),
  });
}

function runCarry(args) {
  const input = readJson(args[0]);
  // `prior` must be present (null when there is no earlier progress), so a caller cannot forget it.
  if (!isObject(input) || !Object.hasOwn(input, 'prior') || !isCheckpointInput(input.checkpoint)) {
    return { ok: false, error: 'bad-input' };
  }
  if (input.prior !== null && !isCheckpointInput(input.prior)) return { ok: false, error: 'bad-input' };
  return checkCarry(input.checkpoint, input.prior);
}

function readTextFile(file) {
  return fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
}

function runFormat(args) {
  const input = readJson(args[0]);
  if (!isObject(input) || typeof input.phase !== 'string' || !isObject(input.fields)
    || typeof input.summaryFile !== 'string' || typeof input.outFile !== 'string'
    || typeof input.lineFile !== 'string') {
    return { ok: false, error: 'bad-input' };
  }
  let line;
  try {
    line = formatCheckpoint(input.phase, input.fields);
  } catch (err) {
    if (err.code) {
      return err.key === undefined ? { ok: false, error: err.code } : { ok: false, error: err.code, key: err.key };
    }
    throw err;
  }
  const summary = readTextFile(input.summaryFile).replace(/\s+$/, '');
  if (summaryHasCheckpointLine(summary)) return { ok: false, error: 'summary-has-checkpoint-line' };
  const fence = fenceFor(`${summary}\n${line}`);
  const comment = `${summary.length > 0 ? `${summary}\n\n` : ''}${fence}\n${line}\n${fence}\n`;
  fs.writeFileSync(input.outFile, comment);
  fs.writeFileSync(input.lineFile, line);
  return { line };
}

function runReadback(args) {
  const [fetchedFile, lineFile] = args;
  if (!fetchedFile || !lineFile) throw new Error('usage: readback <fetchedCommentFile> <lineFile>');
  const expected = trimLine(readTextFile(lineFile));
  const parsed = parseCheckpoint(readTextFile(fetchedFile));
  if (!parsed.ok) return { ok: false, error: parsed.error };
  if (parsed.line !== expected) return { ok: false, error: 'mismatch' };
  return { ok: true };
}

function dispatch(argv) {
  const [cmd, ...rest] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'validate': return runValidate(rest);
    case 'carry': return runCarry(rest);
    case 'format': return runFormat(rest);
    case 'readback': return runReadback(rest);
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
