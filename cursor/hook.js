// cursor/hook.js — total-recall's Cursor CLI hook entry.
//
// Declared in cursor/hooks.json and run by Cursor as `node "${CURSOR_PLUGIN_ROOT}/cursor/hook.js" <command>`.
// Written in node because Cursor runs a hook `command` through PowerShell on Windows, where the bash
// scripts' unbraced `$CURSOR_PLUGIN_ROOT` expands to nothing.
//
//   session-start    print the commands skill (the instructions that make the model call
//                    session_start) plus a short Cursor note, as sessionStart context
//   post-tool-use    run the engine's pinned-floor on the FIRST tool call of each user message
//                    (generation) and print its text, when it has any
//
// Why postToolUse: Cursor's CLI never fires a prompt hook (beforeSubmitPrompt, stop and
// afterAgentResponse did not fire) and beforeSubmitPrompt cannot add context anyway; postToolUse is
// the one per-turn hook that can. A "turn" for the floor is therefore a generation with at least one
// successful tool call. The marker file below makes the first tool call of a generation the only one
// that counts, however many tools (in parallel too) the turn runs.
//
// Output is Cursor's top-level `{"additional_context": "..."}`. Contract: this process NEVER fails a
// prompt — it exits 0 and prints either one JSON object or nothing, and writes nothing to stderr.
//
// The floor reuses pi/floor.js (spawn the engine binary directly, only once it is provisioned), loaded
// by dynamic import inside a try/catch so a missing file cannot raise a link-time error. The helpers
// stripFrontmatter, readStdin, isMain and storeEnvStrip mirror codex/hook.js and copilot/hook.js; a
// shared module is tracked in docs/TODO.md (a static import of it would let a missing file crash a hook).

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const SESSION_START = 'session-start';
export const POST_TOOL_USE = 'post-tool-use';

export const FLOOR_TIMEOUT_MS = 8000; // 2 s stdin cap + 8 s engine + startup stays under the hook's 15 s
export const MARKER_MAX_AGE_MS = 3 * 24 * 60 * 60 * 1000;
const STDIN_CAP_MS = 2000;

/** SKILL.md without its YAML frontmatter. */
export function stripFrontmatter(text) {
  return text.startsWith('---') ? text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '') : text;
}

/** What the model needs to know about Cursor that the shared instructions (written for Claude Code) do not say. */
export function cursorNote(rootDir) {
  return [
    '',
    '---',
    'Cursor notes for the instructions above:',
    `- The \`total-recall\` command may not be on PATH in this session. Run the CLI as \`node "${path.join(rootDir, 'cursor', 'cli.js')}" <args>\` (for example \`compact --run\` or \`ui\`), never a bare \`total-recall\` and never \`bin/start.js\` directly: the wrapper drops \`TOTAL_RECALL_*\`, \`XDG_DATA_HOME\`, \`XDG_CONFIG_HOME\`, \`HERMES_HOME\` (and \`HOME\` on Windows) so the command uses the same store as Cursor's MCP server.`,
    '- If a total-recall tool call is refused for approval, tell the user to approve the `plugin-total-recall-total-recall` MCP server in Cursor, or to start the Cursor CLI with `--approve-mcps` (INSTALL.md, "Cursor").',
    '- If the very first `session_start` of a new store fails with a timeout (a large first-time import can outlast Cursor\'s 60 second MCP limit, which a plugin cannot raise), call `session_start` once more: the import is incremental, so the retry is usually much faster.',
  ].join('\n');
}

/**
 * `{ KEY: undefined }` for every environment key that decides where the engine keeps its store:
 * TOTAL_RECALL_*, XDG_DATA_HOME, XDG_CONFIG_HOME, HERMES_HOME, and HOME on Windows. Cursor gives a
 * plugin MCP server a sanitized environment (none of these) but runs hooks with the full one, so the
 * floor must ignore them too or it would read a different store than the server. Matched
 * case-insensitively. Layered over the process environment by runFloor, an undefined value removes
 * the variable from the child. Unlike codex/hook.js this is not behind a flag: it always applies.
 */
export function storeEnvStrip(environ, platform) {
  const strip = {};
  for (const key of Object.keys(environ)) {
    const upper = key.toUpperCase();
    if (
      upper.startsWith('TOTAL_RECALL_') ||
      upper === 'XDG_DATA_HOME' || upper === 'XDG_CONFIG_HOME' || upper === 'HERMES_HOME' ||
      (platform === 'win32' && upper === 'HOME')
    ) {
      strip[key] = undefined;
    }
  }
  return strip;
}

/** An identifier safe to use inside a file name: [A-Za-z0-9_-] only, at most 64 characters. */
export function sanitizeId(id) {
  return String(id).replace(/[^A-Za-z0-9_-]/g, '_').slice(0, 64);
}

/**
 * The per-user marker directory (created 0700), or null when it cannot be trusted. On POSIX it must be a
 * real directory (not a symlink) owned by the current user with no group/other permission bits, so a
 * directory squatted by another local user silently disables the floor instead of steering its state.
 */
export function markerDir({ tmpdir, username, platform, fsApi, getuid }) {
  try {
    const dir = path.join(tmpdir, `total-recall-cursor-${sanitizeId(username)}`);
    fsApi.mkdirSync(dir, { recursive: true, mode: 0o700 });
    if (platform !== 'win32') {
      const st = fsApi.lstatSync(dir);
      if (st.isSymbolicLink() || !st.isDirectory()) return null;
      if (typeof getuid === 'function' && st.uid !== getuid()) return null;
      if ((st.mode & 0o077) !== 0) return null;
    }
    return dir;
  } catch {
    return null;
  }
}

/**
 * Claim a generation: true only for the caller that creates the marker (exclusive create), false when it
 * already exists or anything fails. Claim first (the losing path does no directory scan); only after a
 * successful claim, prune markers older than MARKER_MAX_AGE_MS (best effort).
 */
export function claimGeneration({ dir, conversationId, generationId, now, fsApi }) {
  const file = path.join(dir, `${sanitizeId(conversationId)}__${sanitizeId(generationId)}`);
  try {
    const fd = fsApi.openSync(file, 'wx');
    fsApi.closeSync(fd);
  } catch {
    return false;
  }
  try {
    for (const name of fsApi.readdirSync(dir)) {
      const p = path.join(dir, name);
      try {
        if (now - fsApi.statSync(p).mtimeMs > MARKER_MAX_AGE_MS) fsApi.unlinkSync(p);
      } catch { /* best effort */ }
    }
  } catch { /* best effort */ }
  return true;
}

const envelope = (additionalContext) => `${JSON.stringify({ additional_context: additionalContext })}\n`;

async function sessionStart({ rootDir, readFile }) {
  let text;
  try {
    text = stripFrontmatter(readFile(path.join(rootDir, 'skills', 'commands', 'SKILL.md'))).trim();
  } catch {
    return '';
  }
  if (text === '') return '';
  return envelope(`${text}\n${cursorNote(rootDir)}`);
}

const nonBlank = (v) => typeof v === 'string' && v.trim() !== '';

async function postToolUse({ stdinText, loadFloor, environ, platform, fsApi, tmpdir, username, getuid, now }) {
  let payload;
  try {
    payload = JSON.parse(String(stdinText).replace(/^\uFEFF/, ''));
  } catch {
    return '';
  }
  if (typeof payload !== 'object' || payload === null) return '';
  const conversationId = payload.conversation_id;
  const generationId = payload.generation_id;
  if (!nonBlank(conversationId) || !nonBlank(generationId)) return '';

  const floor = await loadFloor();
  const engine = floor.resolveEngine();
  if (!engine) return ''; // nothing is consumed while the engine is not provisioned

  // The workspace: an existing workspace_roots[0], else CURSOR_PROJECT_DIR. Never process.cwd(): Cursor runs plugin
  // hooks from the plugin install directory (observed), which would scope the floor to the plugin's own repo.
  const root = Array.isArray(payload.workspace_roots) ? payload.workspace_roots[0] : undefined;
  let cwd;
  if (nonBlank(root) && fsApi.existsSync(root)) cwd = root;
  else if (nonBlank(environ.CURSOR_PROJECT_DIR)) cwd = environ.CURSOR_PROJECT_DIR;
  else return ''; // no usable workspace: say nothing and consume no marker

  const dir = markerDir({ tmpdir, username, platform, fsApi, getuid });
  if (!dir) return '';
  if (!claimGeneration({ dir, conversationId, generationId, now, fsApi })) return '';

  // No transcript_path: it is null in every observed run, so only the every-N-turns trigger applies.
  const text = await floor.runFloor({
    binaryPath: engine,
    payload: { session_id: conversationId, cwd },
    env: storeEnvStrip(environ, platform),
    timeoutMs: FLOOR_TIMEOUT_MS,
  });
  return typeof text === 'string' && text.trim() !== '' ? envelope(text) : '';
}

/** Returns the text to print on stdout ('' for "say nothing"). Never rejects. */
export async function run(deps) {
  try {
    if (deps.command === SESSION_START) return await sessionStart(deps);
    if (deps.command === POST_TOOL_USE) return await postToolUse(deps);
  } catch {
    // fall through: a hook must never fail the prompt
  }
  return '';
}

function isMain(metaUrl) {
  try {
    return fs.realpathSync(process.argv[1]) === fs.realpathSync(fileURLToPath(metaUrl));
  } catch {
    return false;
  }
}

/** All of stdin, or what arrived within the cap; never waits on a TTY. The stream is released after. */
function readStdin() {
  return new Promise((resolve) => {
    if (process.stdin.isTTY) { resolve(''); return; }
    const chunks = [];
    let done = false;
    const finish = () => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      try { process.stdin.destroy(); } catch { /* already closed */ }
      resolve(Buffer.concat(chunks).toString('utf8'));
    };
    const timer = setTimeout(finish, STDIN_CAP_MS);
    process.stdin.on('data', (c) => chunks.push(c));
    process.stdin.on('end', finish);
    process.stdin.on('error', finish);
  });
}

function currentUsername() {
  try { return os.userInfo().username || 'user'; } catch { return 'user'; }
}

async function main() {
  const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const command = process.argv[2];
  const out = await run({
    command,
    // session-start never reads stdin: nothing in its payload is used, and an open pipe would cost the full cap.
    stdinText: command === POST_TOOL_USE ? await readStdin() : '',
    rootDir,
    readFile: (p) => fs.readFileSync(p, 'utf8'),
    loadFloor: () => import(pathToFileURL(path.join(rootDir, 'pi', 'floor.js')).href),
    environ: process.env,
    platform: process.platform,
    fsApi: fs,
    tmpdir: os.tmpdir(),
    username: currentUsername(),
    getuid: typeof process.getuid === 'function' ? process.getuid.bind(process) : undefined,
    now: Date.now(),
  });
  process.stdout.on('error', () => undefined); // host closed the pipe early: nothing to do
  if (out) process.stdout.write(out);
}

if (isMain(import.meta.url)) {
  main().catch(() => undefined).finally(() => { process.exitCode = 0; });
}
