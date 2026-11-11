// copilot/hook.js — total-recall's GitHub Copilot CLI hook entry.
//
// Declared in copilot/hooks.json and run by Copilot CLI as `node "${PLUGIN_ROOT}/copilot/hook.js" <command>`.
// It is the Copilot counterpart of hooks/session-start/run.sh and hooks/user-prompt-submit/run.sh,
// written in node because Copilot runs a hook `command` through PowerShell on Windows (where the bash
// scripts' `$CLAUDE_PLUGIN_ROOT` expands to nothing) and `bash` may resolve to WSL.
//
//   session-start           print the commands skill (the instructions that make the model call
//                           session_start) plus a short Copilot note, as sessionStart context
//   user-prompt-submitted   run the engine's pinned-floor and print its text, when it has any
//
// Output is Copilot's top-level `{"additionalContext": "..."}` object. Contract: this process NEVER
// fails a prompt — it exits 0 and prints either one JSON object or nothing, and writes nothing to
// stderr. Anything unexpected degrades to "said nothing".
//
// The floor reuses pi/floor.js (spawn the engine binary directly, only once it is provisioned). That
// module is loaded by dynamic import inside a try/catch so a broken or missing file cannot raise a
// link-time error here. The small helpers below (stripFrontmatter, readStdin, isMain) deliberately
// mirror codex/hook.js; extracting shared host-hook helpers is tracked in docs/TODO.md.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const SESSION_START = 'session-start';
export const USER_PROMPT_SUBMITTED = 'user-prompt-submitted';

const STDIN_CAP_MS = 2000;

/** SKILL.md without its YAML frontmatter. */
export function stripFrontmatter(text) {
  return text.startsWith('---') ? text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '') : text;
}

/** What the model needs to know about Copilot CLI that the shared instructions (written for Claude Code) do not say. */
export function copilotNote(rootDir) {
  return [
    '',
    '---',
    'Copilot CLI notes for the instructions above:',
    `- The \`total-recall\` command may not be on PATH in this session. Run the CLI as \`node "${path.join(rootDir, 'bin', 'start.js')}" <args>\` (for example \`compact --run\` or \`ui\`), never a bare \`total-recall\`.`,
    '- If a total-recall tool call is refused for approval, tell the user to allow the server, for example by starting Copilot with `--allow-tool=total-recall` (INSTALL.md, "Copilot CLI").',
  ].join('\n');
}

const envelope = (additionalContext) => `${JSON.stringify({ additionalContext })}\n`;

async function sessionStart({ rootDir, readFile }) {
  let text;
  try {
    text = stripFrontmatter(readFile(path.join(rootDir, 'skills', 'commands', 'SKILL.md'))).trim();
  } catch {
    return '';
  }
  if (text === '') return '';
  return envelope(`${text}\n${copilotNote(rootDir)}`);
}

async function userPromptSubmitted({ stdinText, loadFloor }) {
  let payload;
  try {
    payload = JSON.parse(stdinText);
  } catch {
    return '';
  }
  if (typeof payload !== 'object' || payload === null) return '';
  const sessionId = payload.sessionId;
  if (typeof sessionId !== 'string' || sessionId.trim() === '') return '';
  // A subagent's hook carries agentId (per Copilot's hooks docs); the floor belongs to the main conversation.
  if (typeof payload.agentId === 'string' && payload.agentId.trim() !== '') return '';
  const cwd = typeof payload.cwd === 'string' && payload.cwd.trim() !== '' ? payload.cwd : process.cwd();

  const floor = await loadFloor();
  const engine = floor.resolveEngine();
  if (!engine) return '';
  // No transcript_path exists on this event (only agentStop carries one), so only the every-N-turns
  // trigger applies, as for pi and Codex.
  const text = await floor.runFloor({
    binaryPath: engine,
    payload: { session_id: sessionId, cwd },
    env: {},
  });
  return typeof text === 'string' && text.trim() !== '' ? envelope(text) : '';
}

/** Returns the text to print on stdout ('' for "say nothing"). Never rejects. */
export async function run(deps) {
  try {
    if (deps.command === SESSION_START) return await sessionStart(deps);
    if (deps.command === USER_PROMPT_SUBMITTED) return await userPromptSubmitted(deps);
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
    // session-start never reads stdin: nothing in the payload is used, and an open pipe would cost the full cap.
    stdinText: command === USER_PROMPT_SUBMITTED ? await readStdin() : '',
    rootDir,
    readFile: (p) => fs.readFileSync(p, 'utf8'),
    loadFloor: () => import(pathToFileURL(path.join(rootDir, 'pi', 'floor.js')).href),
  });
  process.stdout.on('error', () => undefined); // host closed the pipe early: nothing to do
  if (out) process.stdout.write(out);
}

if (isMain(import.meta.url)) {
  main().catch(() => undefined).finally(() => { process.exitCode = 0; });
}
