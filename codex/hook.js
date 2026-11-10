// codex/hook.js — total-recall's Codex hook entry.
//
// Declared in codex/hooks.json and run by Codex as `node "${PLUGIN_ROOT}/codex/hook.js" <command>`.
// It is the Codex counterpart of hooks/session-start/run.sh and hooks/user-prompt-submit/run.sh,
// written in node because Codex runs hook commands through PowerShell on Windows (where the bash
// scripts' `$CLAUDE_PLUGIN_ROOT` expands to nothing) and `bash` may resolve to WSL.
//
//   session-start        print the commands skill (the instructions that make the model call
//                        session_start) plus a short Codex note, as SessionStart context
//   user-prompt-submit   run the engine's pinned-floor and print its text, when it has any
//
// Contract: this process NEVER fails a prompt — it exits 0 and prints either one JSON object or
// nothing, and writes nothing to stderr. Anything unexpected degrades to "said nothing".
//
// The floor reuses pi/floor.js (spawn the engine binary directly, only once it is provisioned).
// That module is loaded by dynamic import inside a try/catch so a broken or missing file cannot
// raise a link-time error here.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const SESSION_START = 'session-start';
export const USER_PROMPT_SUBMIT = 'user-prompt-submit';

/**
 * Set to true only if a real Codex does not forward the user's TOTAL_RECALL_ variables, HOME and XDG_ variables
 * to a plugin-bundled MCP server (see `env_vars` in codex/build-mcp.js). Codex gives the server
 * a sanitized environment but runs hooks with the normal one; without forwarding, the floor
 * would read a different store than the server, so the floor then ignores those variables too.
 */
export const STRIP_STORE_ENV = false;

const STDIN_CAP_MS = 2000;

/** SKILL.md without its YAML frontmatter. */
export function stripFrontmatter(text) {
  return text.startsWith('---') ? text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '') : text;
}

/** What the model needs to know about Codex that the shared instructions (written for Claude Code) do not say. */
export function codexNote(rootDir) {
  return [
    '',
    '---',
    'Codex notes for the instructions above:',
    `- The \`total-recall\` command may not be on PATH in this session. Run the CLI as \`node "${path.join(rootDir, 'bin', 'start.js')}" <args>\` (for example \`compact --run\` or \`ui\`), never a bare \`total-recall\`.`,
    '- MCP tool schemas are deferred in Codex: find the total-recall tools with tool search before calling `session_start`.',
    '- Invoke the commands skill as `$total-recall:commands` (or from /skills), not `/total-recall:commands`; advice that is specific to Claude Code (such as `/total-recall:commands setup`) does not apply here.',
  ].join('\n');
}

/**
 * `{ KEY: undefined }` for every environment key that decides where the engine keeps its store:
 * TOTAL_RECALL_*, XDG_DATA_HOME, XDG_CONFIG_HOME, HERMES_HOME, and HOME on Windows (where Codex's
 * sanitized server environment has none). Matched case-insensitively. Layered over the process
 * environment by runFloor, an undefined value removes the variable from the child.
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

const envelope = (hookEventName, additionalContext) =>
  `${JSON.stringify({ hookSpecificOutput: { hookEventName, additionalContext } })}\n`;

async function sessionStart({ rootDir, readFile }) {
  let text;
  try {
    text = stripFrontmatter(readFile(path.join(rootDir, 'skills', 'commands', 'SKILL.md'))).trim();
  } catch {
    return '';
  }
  if (text === '') return '';
  return envelope('SessionStart', `${text}\n${codexNote(rootDir)}`);
}

async function userPromptSubmit({ stdinText, loadFloor, environ, platform }) {
  let payload;
  try {
    payload = JSON.parse(stdinText);
  } catch {
    return '';
  }
  if (typeof payload !== 'object' || payload === null) return '';
  const sessionId = payload.session_id;
  if (typeof sessionId !== 'string' || sessionId.trim() === '') return '';
  const cwd = typeof payload.cwd === 'string' && payload.cwd.trim() !== '' ? payload.cwd : process.cwd();

  const floor = await loadFloor();
  const engine = floor.resolveEngine();
  if (!engine) return '';
  // transcript_path is deliberately not forwarded: a Codex rollout is far larger than the
  // conversation it records (fixed per-turn records, reasoning, token counts, tool output, and the
  // injected block itself), so the engine's byte-growth trigger would fire on most prompts. Without it
  // only the every-N-turns trigger applies.
  const text = await floor.runFloor({
    binaryPath: engine,
    payload: { session_id: sessionId, cwd },
    env: STRIP_STORE_ENV ? storeEnvStrip(environ, platform) : {},
  });
  return typeof text === 'string' && text.trim() !== '' ? envelope('UserPromptSubmit', text) : '';
}

/** Returns the text to print on stdout ('' for "say nothing"). Never rejects. */
export async function run(deps) {
  try {
    if (deps.command === SESSION_START) return await sessionStart(deps);
    if (deps.command === USER_PROMPT_SUBMIT) return await userPromptSubmit(deps);
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

async function main() {
  const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  const command = process.argv[2];
  const out = await run({
    command,
    stdinText: command === USER_PROMPT_SUBMIT ? await readStdin() : '',
    rootDir,
    readFile: (p) => fs.readFileSync(p, 'utf8'),
    loadFloor: () => import(pathToFileURL(path.join(rootDir, 'pi', 'floor.js')).href),
    environ: process.env,
    platform: process.platform,
  });
  if (out) process.stdout.write(out);
}

if (isMain(import.meta.url)) {
  main().catch(() => undefined).finally(() => { process.exitCode = 0; });
}
