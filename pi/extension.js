// pi/extension.js — total-recall as a pi host.
//
// Loaded by pi from the package's `pi` manifest (`pi install npm:@strvmarv/total-recall`).
// It maps the two Claude Code hooks total-recall has onto pi events, registers the MCP
// server, and adds /total-recall and /develop commands:
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
import { registerDevelop } from './develop.js';
import { resolveUserServer as realResolveUserServer } from './userServer.js';
import {
  isDisplayableNotice, runWhatsNew as realRunWhatsNew, writeDirectStamp as realWriteDirectStamp,
} from './whatsNew.js';
import { resolveDataDir as realResolveDataDir } from '../scripts/fetch-binary.js';

export const MCP_SERVER_NAME = 'total-recall';
export const CONTEXT_MESSAGE_TYPE = 'total-recall-context';
/** Tools the injected instructions name; every other tool is reached through pi's tool_search. */
export const DIRECT_TOOLS = ['session_start', 'session_end', 'session_refresh', 'memory_*', 'kb_search'];

/** The host id the engine's what's-new notice and its stamp use for this harness. */
const HOST_ID = 'pi';

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
 * `env` names the host for the engine's what's-new notice (pi merges it over the parent environment).
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
    env: { TOTAL_RECALL_HOST: HOST_ID },
    description: 'total-recall: tiered memory and knowledge base (search, store, pin, compact) for the coding session.',
  };
}

/** SKILL.md without its YAML frontmatter, as the Claude Code SessionStart hook's text minus the header. */
function stripFrontmatter(text) {
  return text.startsWith('---') ? text.replace(/^---\r?\n[\s\S]*?\r?\n---\r?\n/, '') : text;
}

/** True when the branch already holds a delivered context message after the last compaction. A branch that cannot be read counts as not delivered. */
function deliveredInContext(ctx) {
  try {
    const branch = ctx.sessionManager.getBranch();
    let lastCompaction = -1;
    branch.forEach((e, i) => { if (e.type === 'compaction') lastCompaction = i; });
    return branch.some((e, i) => i > lastCompaction && e.customType === CONTEXT_MESSAGE_TYPE);
  } catch {
    return false;
  }
}

export function createExtension({
  rootDir, env, readFile, resolveEngine, runFloor, resolveUserServer,
  runWhatsNew, writeDirectStamp, resolveDataDir, execPath = process.execPath,
  // The session files that have started and not yet shut down in this process. A fresh set by default,
  // so one extension instance (and every test) stands alone; the default export shares one per process.
  liveSessions = new Set(),
}) {
  const skillPath = path.join(rootDir, 'skills', 'commands', 'SKILL.md');
  const developSkillPath = path.join(rootDir, 'skills', 'develop', 'SKILL.md');

  const loadSkillText = () => {
    try {
      return stripFrontmatter(readFile(skillPath, 'utf8')).trim();
    } catch {
      return '';
    }
  };

  // pi has no 'total-recall' on PATH after a plain `pi install`, but the instructions name CLI commands
  // (`compact --run`, `ui`): tell the model how to run this package's own CLI.
  const cliNote = () => [
    '',
    '---',
    'pi note: the `total-recall` command may not be on PATH in this session. Run the CLI as',
    `\`node "${path.join(rootDir, 'bin', 'start.js')}" <args>\` (for example \`compact --run\` or \`ui\`), never a bare \`total-recall\`.`,
  ].join('\n');

  const queuedText = () => {
    const text = loadSkillText();
    return text ? `${text}\n${cliNote()}` : undefined;
  };

  return (pi) => {
    try {
      pi.registerMcpServer(MCP_SERVER_NAME, mcpServerConfig({ rootDir, execPath }));
    } catch {
      // The hooks still work; the user can add the server to mcp.json by hand.
    }

    // False when PI_SUBAGENT_ID is set (a full pi process that has a session file; pi-herdr-agents is
    // said to set it for its children, which was not inspected) and in a session with no session file:
    // --no-session, or a pi-subagents child that is kept in memory (a nested child by default, or one
    // with persist_session: false). Such a child is not the user's conversation, and it would otherwise
    // open its own memory session.
    // It is NOT false for a top-level pi-subagents child: pi-subagents keeps that child's session in a
    // file by default (rememberAgents: true) and sets no PI_SUBAGENT_ID, so the child gets the
    // instructions and the floor. Observed, and the workarounds, in docs/hosts/pi.md.
    // Set at session_start and kept for the life of this session: its header names a parent session file
    // that has started and not yet shut down in this process, so it is a child of that session.
    let child = false;
    let ownFile;
    const hookable = (ctx) => !env.PI_SUBAGENT_ID && ctx.sessionManager.getSessionFile() !== undefined && !child;

    // Record this session before anything is skipped (a skipped child can be a parent in turn), then
    // decide whether it is a child. Any failure leaves it the user's session, as before.
    const noteSessionStart = (ctx) => {
      child = false;
      try {
        const file = ctx.sessionManager.getSessionFile();
        if (typeof file === 'string' && file !== '') {
          ownFile = file;
          liveSessions.add(file);
        }
        const parent = ctx.sessionManager.getHeader?.()?.parentSession;
        child = typeof parent === 'string' && liveSessions.has(parent);
      } catch {
        child = false;
      }
    };

    try {
      registerCommands(pi, { hookable, skillText: loadSkillText() });
    } catch {
      // The command is a convenience.
    }

    // Its own guard: neither command registration can disable the other.
    try {
      registerDevelop(pi, { hookable, developSkillPath });
    } catch {
      // The command is a convenience.
    }

    // The what's-new notice: the stamp first (so the engine's own session_start does not announce the
    // same release again), then the engine's line, shown through the UI without delaying startup. Every
    // step is guarded: nothing here may throw into pi. A missing dependency counts as "no followed server".
    const showNotice = (event, ctx) => {
      try {
        if (event.reason === 'reload' || ctx.hasUI !== true || !hookable(ctx)) return;
        const user = resolveUserServer ? resolveUserServer(ctx.cwd) : { skip: false, env: {} };
        if (user.skip) return;
        // Without a data directory the engine would fall back to a relative one and record the version
        // there, and with no stamp the model would relay the line again: write nothing, run nothing.
        const dataDir = resolveDataDir({ ...env, ...user.env });
        if (typeof dataDir !== 'string' || dataDir === '') return;
        writeDirectStamp({ dataDir, host: HOST_ID });
        const binaryPath = resolveEngine();
        if (!binaryPath) return;
        // Not awaited: the handler returns at once. A stale ctx (session replaced) may throw on use.
        Promise.resolve(runWhatsNew({ binaryPath, host: HOST_ID, format: 'text', env: user.env, timeoutMs: 3000 }))
          .then((text) => {
            if (!isDisplayableNotice(text)) return;
            try {
              ctx.ui.notify(text, 'info');
            } catch {
              // The notice is a convenience.
            }
          })
          .catch(() => {});
      } catch {
        // The notice is a convenience.
      }
    };

    // The session-start text, waiting for the next user prompt to carry it.
    let pending;

    // pi shuts a session down before it builds its replacement (/new, /resume, /fork, /reload), so a
    // session is a child only while its parent is between its own session_start and this event. The file
    // recorded at session_start is removed, not one read from a ctx that may be stale by now.
    pi.on('session_shutdown', () => {
      try {
        if (ownFile !== undefined) liveSessions.delete(ownFile);
      } catch {
        // Nothing here may throw into pi.
      }
      return undefined;
    });

    pi.on('session_start', (event, ctx) => {
      noteSessionStart(ctx);
      pending = undefined;
      if (!hookable(ctx)) return undefined;
      // A reload rebuilds this instance (the queue is lost) but keeps the transcript: skip only when
      // the earlier message is still in context.
      if (!(event.reason === 'reload' && deliveredInContext(ctx))) pending = queuedText();
      showNotice(event, ctx);
      return undefined;
    });

    // /tree can move the active branch to a point before the instructions were delivered (or past a
    // compaction that dropped them) without emitting session_start or session_compact.
    pi.on('session_tree', (_event, ctx) => {
      if (!hookable(ctx) || deliveredInContext(ctx)) return undefined;
      pending = queuedText();
      return undefined;
    });

    // After a compaction the injected text is gone from the transcript.
    pi.on('session_compact', (_event, ctx) => {
      if (!hookable(ctx)) return undefined;
      pending = queuedText();
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
          // A hand-configured mcp.json server may point the MCP tools at another store; follow it or stay silent.
          const user = resolveUserServer(ctx.cwd);
          if (!user.skip) {
            floor = await runFloor({
              binaryPath: engine,
              payload: { session_id: ctx.sessionManager.getSessionId(), cwd: ctx.cwd },
              env: user.env,
            });
          }
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
    resolveUserServer: (cwd) => realResolveUserServer({ env: process.env, cwd }),
    runWhatsNew: realRunWhatsNew,
    writeDirectStamp: realWriteDirectStamp,
    resolveDataDir: realResolveDataDir,
  })(pi);
}
