// total-recall Codex MCP launcher. Run as `node -e <this file>` from codex/mcp.json, which
// embeds it verbatim (codex/build-mcp.js generates that file; do not edit mcp.json by hand).
//
// Codex expands no variables in a bundled MCP server's command, args or env, so the plugin
// root is not available to name bin/start.js. Find the installed plugin in Codex's cache
// (<home>/plugins/cache/<marketplace>/total-recall/<version>/) instead and hand over to its
// bin/start.js, which runs the MCP shim exactly as under every other host.
//
// No cwd is configured on purpose: the engine resolves the current project (project-scoped
// pins, project docs) from the working directory it inherits, which must be the session's.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

function parseSemver(name) {
  const m = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(name);
  return m ? { core: [Number(m[1]), Number(m[2]), Number(m[3])], pre: m[4] ? m[4].split('.') : [] } : null;
}

// Semver precedence: positive when a is newer than b.
function compareSemver(a, b) {
  for (let i = 0; i < 3; i++) if (a.core[i] !== b.core[i]) return a.core[i] - b.core[i];
  if (a.pre.length === 0 && b.pre.length === 0) return 0;
  if (a.pre.length === 0) return 1;
  if (b.pre.length === 0) return -1;
  for (let i = 0; i < Math.max(a.pre.length, b.pre.length); i++) {
    const x = a.pre[i];
    const y = b.pre[i];
    if (x === undefined) return -1;
    if (y === undefined) return 1;
    const xNum = /^\d+$/.test(x);
    const yNum = /^\d+$/.test(y);
    if (xNum && yNum) { if (Number(x) !== Number(y)) return Number(x) - Number(y); }
    else if (xNum) return -1;
    else if (yNum) return 1;
    else if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

// Marketplaces with `enabled = true` under [plugins."total-recall@<marketplace>"] in config.toml.
function enabledMarketplaces(text) {
  const enabled = new Set();
  let current = null;
  for (const line of text.split(/\r?\n/)) {
    const header = /^\s*\[plugins\."total-recall@([^"]+)"\]\s*$/.exec(line);
    if (header) { current = header[1]; continue; }
    if (/^\s*\[/.test(line)) { current = null; continue; }
    if (current !== null) {
      const flag = /^\s*enabled\s*=\s*(true|false)\b/.exec(line);
      if (flag) { if (flag[1] === 'true') enabled.add(current); current = null; }
    }
  }
  return enabled;
}

// Newest first: a valid version before a non-version name (e.g. "local"); two versions by semver
// precedence; otherwise newer start.js first; the path settles the rest deterministically.
function newestFirst(a, b) {
  if (a.semver && b.semver) { const c = compareSemver(b.semver, a.semver); if (c !== 0) return c; }
  else if (a.semver) return -1;
  else if (b.semver) return 1;
  if (a.mtime !== b.mtime) return b.mtime - a.mtime;
  return a.start < b.start ? -1 : a.start > b.start ? 1 : 0;
}

function readDir(dir) {
  try { return fs.readdirSync(dir); } catch { return []; }
}

(async () => {
  const home = process.env.CODEX_HOME || path.join(os.homedir(), '.codex');
  const cache = path.join(home, 'plugins', 'cache');

  const candidates = [];
  for (const marketplace of readDir(cache)) {
    const pluginDir = path.join(cache, marketplace, 'total-recall');
    for (const version of readDir(pluginDir)) {
      const start = path.join(pluginDir, version, 'bin', 'start.js');
      let stat;
      try { stat = fs.statSync(start); } catch { continue; }
      candidates.push({ marketplace, start, mtime: stat.mtimeMs, semver: parseSemver(version) });
    }
  }

  let enabled = new Set();
  try { enabled = enabledMarketplaces(fs.readFileSync(path.join(home, 'config.toml'), 'utf8')); } catch { /* absent or unreadable: keep every candidate */ }
  const filtered = enabled.size > 0 ? candidates.filter((c) => enabled.has(c.marketplace)) : candidates;
  const pool = filtered.length > 0 ? filtered : candidates;

  if (pool.length === 0) {
    process.stderr.write(`total-recall plugin not found under ${cache}\n`);
    process.exit(1);
  }

  pool.sort(newestFirst);
  const start = pool[0].start;
  process.argv = [process.execPath, start];
  try {
    await import(pathToFileURL(start).href);
  } catch (err) {
    process.stderr.write(`total-recall failed to start ${start}: ${err && err.stack ? err.stack : err}\n`);
    process.exit(1);
  }
})();
