// skills/develop/scripts/route.js — the resume position table, task and cycle counting, the
// concurrency check and issue routing (spec sections 5, 10 and 11). Zero dependencies, ESM,
// `node:fs` only.
//
//   node route.js count-tasks <planFile>
//   node route.js count-cycles <approachFile>
//   node route.js tasks-for <planShaFile> <tipFile|-> <tasksDone>
//   node route.js live <rawCommentsFile> <permissionsFile> <nowIso>
//   node route.js position <jsonFile>          {progress, counts}
//   node route.js route <rawCommentsFile> <permissionsFile> <stateFile> [bodyFile]

import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { outsideFences } from './regions.js';
import {
  counts,
  countedCheckpoints,
  flattenComments,
  governingPin,
  isWriteAccess,
  latestProgress,
  selectState,
} from './counted.js';

function isObject(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

// ---- counting ----

const TASK_RE = /^### Task (\d+):/;
const CYCLE_RE = /^(?:[-*]\s+)?Cycle (\d+):/;

/** The lines (outside fences) that match `re`, with the number as written. */
function numbered(text, re) {
  const found = [];
  for (const { line } of outsideFences(text)) {
    const m = re.exec(line);
    if (m) found.push({ line, num: m[1] });
  }
  return found;
}

/** Exactly 1, 2, 3, ... in file order. The number is compared as written, so `01` is not `1`. */
function isSequential(found) {
  return found.every((item, i) => item.num === String(i + 1));
}

/** The `### Task <n>:` headings outside fences, in file order. */
export function countTasks(planText) {
  const found = numbered(planText, TASK_RE);
  return { count: found.length, sequential: isSequential(found) };
}

/** The `Cycle <n>:` lines (optionally bulleted) outside fences, in file order. */
export function countCycles(approachText) {
  const found = numbered(approachText, CYCLE_RE);
  return { count: found.length, sequential: isSequential(found) };
}

/**
 * The task count to use. The tip's text wins when it is sequential and its first `tasksDone` headings
 * (number and text) equal the `planSha` version's, so a task appended before a crash is not lost;
 * otherwise the `planSha` version. `null` when the chosen text is not sequential or has no tasks.
 */
export function tasksFor(planShaText, tipText, tasksDone) {
  const base = numbered(planShaText, TASK_RE);
  let chosen = base;
  if (typeof tipText === 'string') {
    const tip = numbered(tipText, TASK_RE);
    const same = base.length >= tasksDone && tip.length >= tasksDone
      && base.slice(0, tasksDone).every((item, i) => item.line === tip[i].line);
    if (isSequential(tip) && same) chosen = tip;
  }
  if (chosen.length === 0 || !isSequential(chosen)) return null;
  return chosen.length;
}

// ---- concurrency ----

const WINDOW_MS = 24 * 60 * 60 * 1000;
const NOT_HEARTBEAT = ['defined', 'pr-open', 'blocked', 'closed'];
const ENDS_A_RUN = ['pr-open', 'blocked', 'closed'];
const ISO_RE = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})$/;

function parseTime(text) {
  if (typeof text !== 'string' || !ISO_RE.test(text)) return NaN;
  return Date.parse(text);
}

/**
 * True when some heartbeat-phase checkpoint is under 24 hours old and no later checkpoint in the list
 * (posted order) is `pr-open`, `blocked` or `closed`. A date that does not parse never makes a
 * checkpoint live; an unparseable `nowIso` is a caller error.
 */
export function isLive(checkpoints, nowIso) {
  const now = parseTime(nowIso);
  if (Number.isNaN(now)) throw new Error('now is not an ISO date');
  for (let i = 0; i < checkpoints.length; i += 1) {
    const cp = checkpoints[i];
    if (NOT_HEARTBEAT.includes(cp.phase)) continue;
    const created = parseTime(cp.createdAt);
    if (Number.isNaN(created) || now - created >= WINDOW_MS) continue;
    if (checkpoints.slice(i + 1).some((later) => ENDS_A_RUN.includes(later.phase))) continue;
    return true;
  }
  return false;
}

// ---- position ----

function counter(fields, key) {
  if (!Object.hasOwn(fields, key)) return 0;
  if (typeof fields[key] !== 'string' || !/^\d+$/.test(fields[key])) throw new Error(`${key} is not a counter`);
  return Number(fields[key]);
}

function countOrNull(value, key) {
  if (value === null || value === undefined) return null;
  if (!Number.isInteger(value) || value < 0) throw new Error(`counts.${key} must be a count or null`);
  return value;
}

/**
 * The next step, derived from the latest progress checkpoint (spec section 10). `counts.tasks` and
 * `counts.cycles` are null when the text is missing or not sequential; zero and null are both
 * blocker 6. A progress checkpoint that carries no known `track` is an error, not a guess.
 */
export function position(progress, countsIn) {
  if (progress === null || progress === undefined) return 'start';
  if (!isObject(progress) || typeof progress.phase !== 'string') throw new Error('progress is malformed');
  switch (progress.phase) {
    case 'closed': return 'terminal';
    case 'pr-open': return 'after-the-pr';
    case 'final-approved': return 'finish';
    default: break;
  }
  const fields = isObject(progress.fields) ? progress.fields : {};
  const c = isObject(countsIn) ? countsIn : {};
  if (fields.track === 'plan') {
    if (!Object.hasOwn(fields, 'planSha')) return 'write-plan';
    const tasks = countOrNull(c.tasks, 'tasks');
    if (tasks === null || tasks === 0) return 'block6';
    const done = counter(fields, 'tasksDone');
    return done < tasks ? `task:${done + 1}` : 'final-loop';
  }
  if (fields.track === 'small') {
    const cycles = countOrNull(c.cycles, 'cycles');
    if (cycles === null || cycles === 0) return 'block6';
    const done = counter(fields, 'cyclesDone');
    return done < cycles ? `cycle:${done + 1}` : 'final-loop';
  }
  throw new Error('progress has no track');
}

// ---- routing ----

/** True when a trimmed line of `text` starts with `tr-develop/`, fenced or not (CRLF handled). */
export function hasMarkerLine(text) {
  return String(text).split(/\r\n|\r|\n/).some((line) => line.trim().startsWith('tr-develop/'));
}

/**
 * True when a comment that does not count (spec section 9), or the issue body (`bodyText`, or null),
 * has a `tr-develop/` line: it is ignored, and the person is told once. A counted comment is never
 * "uncounted", whatever its line looks like.
 */
export function hasUncountedMarkers(comments, hasWrite, bodyText) {
  if (typeof bodyText === 'string' && hasMarkerLine(bodyText)) return true;
  return comments.some((c) => !counts(c, hasWrite) && hasMarkerLine(c.body));
}

/**
 * The route an issue takes (spec section 5). `hasUncountedMarkers` comes from the caller (the CLI
 * computes it); it is reported as `ignoredMarkers` whatever the route.
 */
export function route(input) {
  const { comments, hasWrite, pinValid, issueState, baseIsDefault } = input;
  if (issueState !== 'open' && issueState !== 'closed') throw new Error('issueState must be open or closed');
  const ignoredMarkers = input.hasUncountedMarkers === true;
  const cps = countedCheckpoints(comments, hasWrite);
  const done = (name, askClosed = false) => ({ route: name, askClosed, ignoredMarkers });

  if (cps.length === 0) return done('define');
  const progress = latestProgress(cps);
  if (progress && progress.phase === 'pr-open') return done('after-the-pr');
  if (progress && progress.phase === 'closed') {
    return done(issueState === 'open' && baseIsDefault === true ? 'new-issue-or-stop' : 'terminal');
  }
  if (typeof pinValid !== 'boolean') throw new Error('pinValid is required');
  if (governingPin(cps) === null || pinValid === false) {
    return done(progress ? 'repin-or-stop' : 'redefine-or-stop');
  }
  return done('deliver', issueState === 'closed');
}

// ---- CLI ----

export const SUBCOMMANDS = ['count-tasks', 'count-cycles', 'tasks-for', 'live', 'position', 'route'];

function readText(file) {
  return fs.readFileSync(file, 'utf8');
}

/** A missing permissions file is `{}`, as in counted.js; presence is tested with `Object.hasOwn`. */
function readPermissions(file) {
  if (!file || !fs.existsSync(file)) return {};
  const map = JSON.parse(readText(file));
  if (!isObject(map)) throw new Error('permissions must be an object');
  return map;
}

function hasWriteFrom(map) {
  return (login) => Object.hasOwn(map, login) && isWriteAccess(map[login]);
}

function usage(text) {
  throw new Error(`usage: ${text}`);
}

function runCountTasks(args) {
  if (!args[0]) usage('count-tasks <planFile>');
  return countTasks(readText(args[0]));
}

function runCountCycles(args) {
  if (!args[0]) usage('count-cycles <approachFile>');
  return countCycles(readText(args[0]));
}

function runTasksFor(args) {
  const [planShaFile, tipFile, doneArg] = args;
  if (!planShaFile || !tipFile || doneArg === undefined) usage('tasks-for <planShaFile> <tipFile|-> <tasksDone>');
  if (!/^\d+$/.test(doneArg)) throw new Error('tasksDone must be a counter');
  const tip = tipFile === '-' ? null : readText(tipFile);
  return { tasks: tasksFor(readText(planShaFile), tip, Number(doneArg)) };
}

function runLive(args) {
  const [rawFile, permissionsFile, nowIso] = args;
  if (!rawFile || !permissionsFile || !nowIso) usage('live <rawCommentsFile> <permissionsFile> <nowIso>');
  const state = selectState(readText(rawFile), readPermissions(permissionsFile));
  // The selection of an incomplete pass is never acted on: there is no answer yet.
  const live = state.complete ? isLive(state.checkpoints, nowIso) : null;
  return { complete: state.complete, needPermissions: state.needPermissions, live };
}

function runPosition(args) {
  if (!args[0]) usage('position <jsonFile>');
  const input = JSON.parse(readText(args[0]));
  if (!isObject(input)) throw new Error('position input must be an object');
  return { position: position(input.progress, input.counts) };
}

function readState(file) {
  const state = JSON.parse(readText(file));
  if (!isObject(state)) throw new Error('state must be an object');
  const issueState = typeof state.issueState === 'string' ? state.issueState.toLowerCase() : null;
  if (issueState !== 'open' && issueState !== 'closed') throw new Error('issueState must be open or closed');
  if (typeof state.baseIsDefault !== 'boolean') throw new Error('baseIsDefault must be a boolean');
  if (Object.hasOwn(state, 'pinValid') && typeof state.pinValid !== 'boolean') throw new Error('pinValid must be a boolean');
  return { pinValid: state.pinValid, issueState, baseIsDefault: state.baseIsDefault };
}

function runRoute(args) {
  const [rawFile, permissionsFile, stateFile, bodyFile] = args;
  if (!rawFile || !permissionsFile || !stateFile) usage('route <rawCommentsFile> <permissionsFile> <stateFile> [bodyFile]');
  const raw = readText(rawFile);
  const map = readPermissions(permissionsFile);
  const state = readState(stateFile);
  const body = bodyFile ? readText(bodyFile) : null;
  const selected = selectState(raw, map);
  if (!selected.complete) {
    // The pin and progress of an incomplete selection are never acted on.
    return {
      complete: false, needPermissions: selected.needPermissions, route: null, askClosed: false, ignoredMarkers: false,
    };
  }
  const comments = flattenComments(raw);
  const hasWrite = hasWriteFrom(map);
  const result = route({
    comments,
    hasWrite,
    pinValid: state.pinValid,
    issueState: state.issueState,
    baseIsDefault: state.baseIsDefault,
    hasUncountedMarkers: hasUncountedMarkers(comments, hasWrite, body),
  });
  return { complete: true, needPermissions: [], ...result };
}

function dispatch(argv) {
  const [cmd, ...rest] = argv;
  if (!SUBCOMMANDS.includes(cmd)) return { ok: false, error: 'unknown subcommand' };
  switch (cmd) {
    case 'count-tasks': return runCountTasks(rest);
    case 'count-cycles': return runCountCycles(rest);
    case 'tasks-for': return runTasksFor(rest);
    case 'live': return runLive(rest);
    case 'position': return runPosition(rest);
    case 'route': return runRoute(rest);
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
