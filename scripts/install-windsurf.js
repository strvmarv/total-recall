#!/usr/bin/env node
// Installs total-recall MCP config and rules into ~/.codeium/windsurf/
// if Windsurf is detected.
//
// Non-fatal: always resolves. If Windsurf isn't present, silently skips.

import { mkdirSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mergeJsonConfig } from './lib/merge-json-config.js';
import { isRepoSelf, ROOT } from './lib/is-repo-self.js';

function windsurfHome() {
  const env = process.env.WINDSURF_HOME;
  if (env) return env;

  const home = process.env.USERPROFILE || homedir();
  if (!home) return null;

  const windsurfDir = join(home, '.codeium', 'windsurf');
  if (existsSync(windsurfDir)) return windsurfDir;

  return null;
}

function stripFrontMatter(content) {
  // Remove YAML front matter (---\n...\n---\n) from the steering doc
  // so the Windsurf rules file is plain markdown.
  if (content.startsWith('---\n')) {
    const end = content.indexOf('\n---\n', 4);
    if (end !== -1) return content.slice(end + 5);
  }
  return content;
}

export async function installWindsurf() {
  try {
    const whome = windsurfHome();
    if (!whome) return;

    // --- MCP config ---
    const configPath = join(whome, 'mcp_config.json');
    mergeJsonConfig(configPath, 'mcpServers', {
      command: 'npx',
      args: ['--yes', '@strvmarv/total-recall']
    }, 'Windsurf');

    // --- Rules file ---
    // Copy the steering doc (stripped of YAML front matter) as a Windsurf
    // rules file for the session_start instruction. Skip for dev installs.
    if (!isRepoSelf()) {
      const steeringSrc = join(ROOT, '.kiro', 'steering', 'total-recall.md');
      const rulesDir = join(whome, 'rules');

      if (existsSync(steeringSrc)) {
        mkdirSync(rulesDir, { recursive: true });
        const content = stripFrontMatter(readFileSync(steeringSrc, 'utf8'));
        writeFileSync(join(rulesDir, 'total-recall.md'), content);
        process.stderr.write(`[total-recall:postinstall] Windsurf rules installed to ${rulesDir}\n`);
      }
    }
  } catch (e) {
    process.stderr.write(`[total-recall:postinstall] Windsurf install failed (non-fatal): ${e.message}\n`);
  }
}