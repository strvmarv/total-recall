#!/usr/bin/env node
// Installs total-recall MCP config, hooks, and GEMINI.md into ~/.gemini/
// if Gemini CLI is detected.
//
// Writes:
//   ~/.gemini/settings.json — MCP server + hooks (BeforeAgent)
//   ~/.gemini/GEMINI.md — session_start instruction (marker-based merge, read from steering doc)
//
// Non-fatal: always resolves. If Gemini CLI isn't present, silently skips.

import { existsSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { isRepoSelf, ROOT } from './lib/is-repo-self.js';
import { recordChange } from './lib/change-log.js';
import { npxEntry, hookCommand, isOwnHookCommand, pinnedSpec, decide } from './lib/package-spec.js';

const GEMINI_START_MARKER = '<!-- total-recall -->';
const GEMINI_END_MARKER = '<!-- /total-recall -->';

function stripFrontMatter(content) {
  // Remove YAML front matter (---\n...\n---\n) from the steering doc
  if (content.startsWith('---\n')) {
    const end = content.indexOf('\n---\n', 4);
    if (end !== -1) return content.slice(end + 5);
  }
  return content;
}

function geminiHome() {
  const env = process.env.GEMINI_HOME;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  const geminiDir = join(home, '.gemini');
  if (existsSync(geminiDir)) return geminiDir;

  return null;
}

function mergeGeminiMd(filePath) {
  // Read the steering doc and strip front matter — same approach as Windsurf rules.
  const steeringSrc = join(ROOT, '.kiro', 'steering', 'total-recall.md');
  if (!existsSync(steeringSrc)) return;
  const content = stripFrontMatter(readFileSync(steeringSrc, 'utf8'));

  let existing = '';
  if (existsSync(filePath)) {
    existing = readFileSync(filePath, 'utf8');
  }

  const section = `${GEMINI_START_MARKER}\n${content}\n${GEMINI_END_MARKER}`;

  // Check if the marker already exists
  const startIdx = existing.indexOf(GEMINI_START_MARKER);
  if (startIdx !== -1) {
    const endIdx = existing.indexOf(GEMINI_END_MARKER, startIdx);
    if (endIdx !== -1) {
      // Replace the existing section
      const before = existing.slice(0, startIdx);
      const after = existing.slice(endIdx + GEMINI_END_MARKER.length);
      writeFileSync(filePath, before + section + after);
      recordChange('Gemini CLI', 'rewrote', filePath);
      process.stderr.write(`[total-recall:postinstall] Gemini GEMINI.md updated at ${filePath}\n`);
      return;
    }
  }

  // Append a new section
  const updated = existing.trimEnd() + '\n\n' + section + '\n';
  const existed = existsSync(filePath);
  writeFileSync(filePath, updated);
  recordChange('Gemini CLI', existed ? 'rewrote' : 'added', filePath);
  process.stderr.write(`[total-recall:postinstall] Gemini GEMINI.md written to ${filePath}\n`);
}

function mergeGeminiSettings(configPath) {
  // Single read-modify-write for both mcpServers and hooks.
  let config = {};
  const existed = existsSync(configPath);
  if (existed) {
    try {
      config = JSON.parse(readFileSync(configPath, 'utf8'));
    } catch {
      process.stderr.write(`[total-recall:postinstall] Gemini settings.json unreadable — leaving it untouched.\n`);
      return;
    }
  }

  // --- mcpServers ---
  if (!config.mcpServers) config.mcpServers = {};
  const existingServer = config.mcpServers['total-recall'];
  let serverAdded = false;
  let serverRefreshed = false;
  if (!existingServer) {
    serverAdded = true;
    config.mcpServers['total-recall'] = npxEntry();
  } else {
    // Same rules as the other tools: the script's own form is moved to the version being installed,
    // an npx entry that holds the package name among other arguments is kept, anything else is custom.
    const verdict = decide(existingServer, pinnedSpec());
    if (verdict === 'refresh') {
      existingServer.args = ['--yes', pinnedSpec()];
      serverRefreshed = true;
    } else if (verdict === 'custom') {
      process.stderr.write(`[total-recall:postinstall] Gemini already has a custom total-recall MCP entry — preserving user config.\n`);
    }
  }

  // --- hooks ---
  if (!config.hooks) config.hooks = {};

  // BeforeAgent hook — shells out to the pinned floor
  const beforeAgentHook = {
    type: 'command',
    command: hookCommand(),
    timeout: 60000,
    name: 'total-recall Pinned Floor'
  };

  if (!config.hooks.BeforeAgent) config.hooks.BeforeAgent = { hooks: [] };
  const beforeAgentHooks = config.hooks.BeforeAgent.hooks || [];
  let hookAdded = false;
  let hookRefreshed = false;
  const existingHook = beforeAgentHooks.find(h => h.name === 'total-recall Pinned Floor');
  if (!existingHook) {
    hookAdded = true;
    beforeAgentHooks.push(beforeAgentHook);
    config.hooks.BeforeAgent.hooks = beforeAgentHooks;
  } else if (isOwnHookCommand(existingHook.command) && pinnedSpec() !== '@strvmarv/total-recall' &&
             existingHook.command !== beforeAgentHook.command) {
    // The script's own command, with no version or another one: move it to the version being installed.
    existingHook.command = beforeAgentHook.command;
    hookRefreshed = true;
  }

  // Write the merged config (mcpServers + hooks) in a single write
  mkdirSync(dirname(configPath), { recursive: true });
  writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
  // The file is rewritten on every run; the entry and the hook are reported only when added or refreshed.
  recordChange('Gemini CLI', existed ? 'rewrote' : 'added', configPath);
  if (serverAdded) recordChange('Gemini CLI', 'added', `MCP entry in ${configPath}`);
  if (hookAdded) recordChange('Gemini CLI', 'added', `BeforeAgent hook (runs on every prompt) in ${configPath}`);
  if (serverRefreshed) recordChange('Gemini CLI', 'rewrote', `MCP entry in ${configPath}`);
  if (hookRefreshed) recordChange('Gemini CLI', 'rewrote', `BeforeAgent hook in ${configPath}`);
  process.stderr.write(`[total-recall:postinstall] Gemini settings.json written to ${configPath}\n`);
}

export async function installGemini() {
  try {
    const ghome = geminiHome();
    if (!ghome) return;

    // --- MCP config + hooks (single read-modify-write) ---
    const configPath = join(ghome, 'settings.json');
    mergeGeminiSettings(configPath);

    // --- GEMINI.md (session-start instruction, read from steering doc) ---
    if (!isRepoSelf()) {
      const geminiMdPath = join(ghome, 'GEMINI.md');
      mergeGeminiMd(geminiMdPath);
    }
  } catch (e) {
    recordChange('Gemini CLI', 'failed', e.message);
    process.stderr.write(`[total-recall:postinstall] Gemini install failed (non-fatal): ${e.message}\n`);
  }
}