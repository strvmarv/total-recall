// pi/userServer.js — match the store a hand-configured total-recall MCP server uses.
//
// pi lets an `mcp.json` entry named `total-recall` override the server the extension registers
// (user file `<agent dir>/mcp.json`, project file `<cwd>/.pi/mcp.json`; a project entry replaces a
// user one). If that entry sets TOTAL_RECALL_* in its `env` (for example TOTAL_RECALL_HOME or
// TOTAL_RECALL_DB_PATH), the MCP tools use that store, so the pinned floor, which runs the engine
// in the extension's own process, must use it too or it would inject another store's pins.
//
// pi evaluates `${VAR}` and `!command` env values itself; this module does not (it would mean
// running arbitrary commands), so a dynamic value means "cannot match safely": skip the floor.
// A remote (url) or disabled entry also means the local default store is unrelated: skip.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const NAMESPACE = 'total_recall';
const ownNamespace = (name) => String(name).replace(/[^A-Za-z0-9_]/g, '_');

function readServers(file, readFile) {
  try {
    const parsed = JSON.parse(readFile(file));
    const servers = parsed && typeof parsed === 'object' ? parsed.mcpServers : undefined;
    return servers && typeof servers === 'object' && !Array.isArray(servers) ? servers : {};
  } catch {
    return {};
  }
}

function findEntry(servers) {
  for (const [name, entry] of Object.entries(servers)) {
    if (ownNamespace(name) === NAMESPACE && entry && typeof entry === 'object') return entry;
  }
  return undefined;
}

/** A full definition (as opposed to an enabled/exposure-only override of a user-level entry). */
const isDefinition = (e) => e.command !== undefined || e.url !== undefined || e.type !== undefined;

/**
 * `{ skip, env }`: `env` holds the plain TOTAL_RECALL_* values of the effective user-configured
 * entry (empty when there is none); `skip` is true when the floor cannot safely follow it.
 */
export function resolveUserServer({
  env = process.env,
  cwd,
  homedir = os.homedir(),
  readFile = (p) => fs.readFileSync(p, 'utf8'),
}) {
  const agentDir = env.PI_CODING_AGENT_DIR || path.join(homedir, '.pi', 'agent');
  const project = cwd ? findEntry(readServers(path.join(cwd, '.pi', 'mcp.json'), readFile)) : undefined;
  const user = findEntry(readServers(path.join(agentDir, 'mcp.json'), readFile));

  // A project definition replaces the user entry; an override-only project entry layers onto it.
  const effective = project && isDefinition(project) ? project : user;
  if (!effective) return { skip: false, env: {} };
  if (effective.url !== undefined || effective.enabled === false || project?.enabled === false) {
    return { skip: true, env: {} };
  }

  const out = {};
  const declared = effective.env && typeof effective.env === 'object' ? effective.env : {};
  for (const [key, value] of Object.entries(declared)) {
    if (!key.startsWith('TOTAL_RECALL_')) continue;
    if (typeof value !== 'string' || value.includes('${') || value.startsWith('!')) return { skip: true, env: {} };
    out[key] = value;
  }
  return { skip: false, env: out };
}
