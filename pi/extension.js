// pi/extension.js — total-recall as a pi host.
//
// Loaded by pi from the package's `pi` manifest (`pi install npm:@strvmarv/total-recall`).
// It maps the two Claude Code hooks total-recall has onto pi events, registers the MCP
// server, and adds a /total-recall command:
//
//   Claude Code hook                                  pi
//   SessionStart (inject skills/commands/SKILL.md)    session_start / session_compact -> queued, delivered
//                                                     with the next user prompt
//   UserPromptSubmit (pinned-directive floor)         before_agent_start -> engine `pinned-floor`
//   SessionEnd (compaction nudge)                     not mapped (pi tears down with no screen)
//
// Both pieces ride one hidden message returned from before_agent_start. Every hook
// degrades to "said nothing": a failure here must never delay or break a prompt.
//
// Imports no pi package (pi hands the extension its API; only the structural slice used
// is assumed). Plain ESM, no build step, no dependencies beyond this repo's own files.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolveEngine as realResolveEngine, runFloor as realRunFloor } from './floor.js';
import { registerCommands } from './command.js';

export const MCP_SERVER_NAME = 'total-recall';
export const CONTEXT_MESSAGE_TYPE = 'total-recall-context';
/** Tools the injected instructions name; every other tool is reached through pi's tool_search. */
export const DIRECT_TOOLS = ['session_start', 'session_end', 'session_refresh', 'memory_*', 'kb_search'];

/** pi's default MCP request timeout is 60 s; the first session_start (imports, embedder) can exceed it. */
const MCP_TIMEOUT_SECONDS = 120;

/**
 * The executable that runs bin/start.js. process.execPath only when it is node (pi can run as a
 * Bun-compiled or bundled binary, where execPath is pi itself); otherwise "node" as .mcp.json has.
 */
export function nodeCommand(execPath = process.execPath) {
  return /(^|[\\/])node(\.exe)?$/i.test(String(execPath)) ? execPath : 'node';
}

/**
 * The MCP server as pi should start it. NO `cwd`: the engine resolves the current project
 * (project-scoped pins, project docs) from its working directory, so it must inherit pi's.
 */
export function mcpServerConfig({ rootDir, execPath = process.execPath }) {
  const toolExposure = {};
  for (const t of DIRECT_TOOLS) toolExposure[t] = 'direct';
  return {
    command: nodeCommand(execPath),
    args: [path.join(rootDir, 'bin', 'start.js')],
    exposure: 'deferred',
    toolExposure,
    timeout: MCP_TIMEOUT_SECONDS,
    description: 'total-recall: tiered memory and knowledge base (search, store, pin, compact) for the coding session.',
  };
}

/** SKILL.md without its YAML frontmatter, as the Claude Code SessionStart hook's text minus the header. */
function stripFrontmatter(text) {
  return text.startsWith('---') ? text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '') : text;
}

/** True when the branch already holds a delivered context message after the last compaction. */
function alreadyDelivered(branch) {
  let lastCompaction = -1;
  branch.forEach((e, i) => { if (e.type === 'compaction') lastCompaction = i; });
  return branch.some((e, i) => i > lastCompaction && e.customType === CONTEXT_MESSAGE_TYPE);
}

export function createExtension({ rootDir, env, readFile, resolveEngine, runFloor, execPath = process.execPath }) {
  const skillPath = path.join(rootDir, 'skills', 'commands', 'SKILL.md');

  const loadSkillText = () => {
    try {
      return stripFrontmatter(readFile(skillPath, 'utf8')).trim();
    } catch {
      return '';
    }
  };

  return (pi) => {
    try {
      pi.registerMcpServer(MCP_SERVER_NAME, mcpServerConfig({ rootDir, execPath }));
    } catch {
      // The hooks still work; the user can add the server to mcp.json by hand.
    }

    // False in a subagent child (pi-herdr-agents sets PI_SUBAGENT_ID for a full pi process that has a
    // session file) and in an ephemeral session (--no-session, in-memory pi-subagents child): neither is
    // the user's conversation, and a child would otherwise open its own memory session.
    const hookable = (ctx) => !env.PI_SUBAGENT_ID && ctx.sessionManager.getSessionFile() !== undefined;

    try {
      registerCommands(pi, { hookable, skillText: loadSkillText() });
    } catch {
      // The command is a convenience.
    }

    // The session-start text, waiting for the next user prompt to carry it.
    let pending;

    pi.on('session_start', (event, ctx) => {
      pending = undefined;
      if (!hookable(ctx)) return undefined;
      // A reload rebuilds this instance (the queue is lost) but keeps the transcript: skip only when
      // the earlier message is still in context.
      if (event.reason === 'reload' && alreadyDelivered(ctx.sessionManager.getBranch())) return undefined;
      pending = loadSkillText() || undefined;
      return undefined;
    });

    // After a compaction the injected text is gone from the transcript.
    pi.on('session_compact', (_event, ctx) => {
      if (!hookable(ctx)) return undefined;
      pending = loadSkillText() || undefined;
      return undefined;
    });

    pi.on('before_agent_start', async (event, ctx) => {
      if (event.messageType !== 'user' || !hookable(ctx)) return undefined;
      // Take the queue synchronously, before the first await: concurrent prompts deliver it once.
      const queued = pending;
      pending = undefined;
      let floor;
      try {
        // Re-resolved every prompt: the extension loads before the MCP shim finishes a first-launch download.
        const engine = resolveEngine();
        if (engine) {
          floor = await runFloor({
            binaryPath: engine,
            payload: { session_id: ctx.sessionManager.getSessionId(), cwd: ctx.cwd },
          });
        }
      } catch {
        floor = undefined;
      }
      const parts = [queued, floor].filter((t) => typeof t === 'string' && t !== '');
      if (parts.length === 0) return undefined;
      return { message: { customType: CONTEXT_MESSAGE_TYPE, content: parts.join('\n\n'), display: false } };
    });
  };
}

/** What pi loads. `rootDir` is the package root: this file lives in <root>/pi/. */
export default function totalRecallPiExtension(pi) {
  const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
  createExtension({
    rootDir,
    env: process.env,
    readFile: (p) => fs.readFileSync(p, 'utf8'),
    resolveEngine: realResolveEngine,
    runFloor: realRunFloor,
  })(pi);
}
