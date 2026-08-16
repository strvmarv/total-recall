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
      process.stderr.write(`[total-recall:postinstall] Gemini GEMINI.md updated at ${filePath}\n`);
      return;
    }
  }

  // Append a new section
  const updated = existing.trimEnd() + '\n\n' + section + '\n';
  writeFileSync(filePath, updated);
  process.stderr.write(`[total-recall:postinstall] Gemini GEMINI.md written to ${filePath}\n`);
}

function mergeGeminiSettings(configPath) {
  // Single read-modify-write for both mcpServers and hooks.
  let config = {};
  if (existsSync(configPath)) {
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
  if (!existingServer) {
    config.mcpServers['total-recall'] = {
      command: 'npx',
      args: ['--yes', '@strvmarv/total-recall']
    };
  } else if (existingServer.command === 'npx' &&
             Array.isArray(existingServer.args) &&
             existingServer.args.includes('@strvmarv/total-recall')) {
    // Already configured correctly — leave it alone
  } else {
    process.stderr.write(`[total-recall:postinstall] Gemini already has a custom total-recall MCP entry — preserving user config.\n`);
  }

  // --- hooks ---
  if (!config.hooks) config.hooks = {};

  // BeforeAgent hook — shells out to the pinned floor
  const beforeAgentHook = {
    type: 'command',
    command: 'npx --yes @strvmarv/total-recall pinned-floor --host gemini-cli',
    timeout: 60000,
    name: 'total-recall Pinned Floor'
  };

  if (!config.hooks.BeforeAgent) config.hooks.BeforeAgent = { hooks: [] };
  const beforeAgentHooks = config.hooks.BeforeAgent.hooks || [];
  if (!beforeAgentHooks.some(h => h.name === 'total-recall Pinned Floor')) {
    beforeAgentHooks.push(beforeAgentHook);
    config.hooks.BeforeAgent.hooks = beforeAgentHooks;
  }

  // Write the merged config (mcpServers + hooks) in a single write
  mkdirSync(dirname(configPath), { recursive: true });
  writeFileSync(configPath, JSON.stringify(config, null, 2) + '\n');
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
    process.stderr.write(`[total-recall:postinstall] Gemini install failed (non-fatal): ${e.message}\n`);
  }
}