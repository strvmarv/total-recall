import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  recordChange, takeChanges, formatSummary, formatLogEntry, appendLog,
} from './lib/change-log.js';

const P = '[total-recall:postinstall]';
const NAME = '@strvmarv/total-recall';
const VERSION = '9.8.7'; // the version the temp package copy carries

const made = [];
after(() => { for (const d of made) fs.rmSync(d, { recursive: true, force: true }); });

function tmp(prefix = 'tr-hostcfg-') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  made.push(dir);
  return dir;
}

// --- change-log: the recorder, the summary text and the append-only log ---

test('recordChange / takeChanges: returns what was recorded, in order, and clears it', () => {
  takeChanges();
  recordChange('Zed', 'added', '/z/settings.json');
  recordChange('Hermes', 'switched', 'memory.provider');
  assert.deepEqual(takeChanges(), [
    { tool: 'Zed', action: 'added', detail: '/z/settings.json' },
    { tool: 'Hermes', action: 'switched', detail: 'memory.provider' },
  ]);
  assert.deepEqual(takeChanges(), []);
});

test('formatSummary: one line per change with tool, action and path', () => {
  const text = formatSummary([
    { tool: 'Zed', action: 'added', detail: '/z/settings.json' },
    { tool: 'Gemini CLI', action: 'rewrote', detail: '/g/settings.json' },
  ]);
  assert.equal(text, [
    `${P} Other tools (2 entries):`,
    `${P}   Zed: added /z/settings.json`,
    `${P}   Gemini CLI: rewrote /g/settings.json`,
  ].join('\n'));
});

test('formatSummary: a failed step is a line of its own with its message', () => {
  const text = formatSummary([{ tool: 'Hermes', action: 'failed', detail: 'EACCES: denied' }]);
  assert.match(text, /\n\[total-recall:postinstall\]   Hermes: failed: EACCES: denied$/);
});

test('formatSummary: nothing recorded says so in one line', () => {
  assert.equal(formatSummary([]), `${P} No changes were made to other tools.`);
});

test('formatLogEntry: heading with date, time and version, then the summary lines', () => {
  const entry = formatLogEntry('LINES', { version: '4.7.1', now: new Date('2026-11-14T10:20:30Z') });
  assert.equal(entry, '## 2026-11-14T10:20:30Z total-recall 4.7.1\nLINES\n\n');
});

test('appendLog: creates postinstall.log, and a second call appends without touching the first entry', () => {
  const dir = path.join(tmp(), 'data'); // does not exist yet
  assert.equal(appendLog(dir, 'first\n'), true);
  assert.equal(appendLog(dir, 'second\n'), true);
  assert.equal(fs.readFileSync(path.join(dir, 'postinstall.log'), 'utf8'), 'first\nsecond\n');
});

test('appendLog: an unknown or unwritable directory returns false and never throws', () => {
  assert.equal(appendLog(null, 'x'), false);
  const file = path.join(tmp(), 'a-file');
  fs.writeFileSync(file, '');
  assert.equal(appendLog(path.join(file, 'sub'), 'x'), false); // parent is a regular file
});

// --- runHostConfig: runs the installers, prints the summary, keeps the log ---

import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { runHostConfig } from './lib/host-config.js';

const REPO = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');

function fakeStderr() {
  const chunks = [];
  return { write: (s) => { chunks.push(s); return true; }, text: () => chunks.join('') };
}

async function installerThatRecords() {
  recordChange('Zed', 'added', '/z/settings.json');
}

test('runHostConfig: prints the summary and appends the same lines to the log under TOTAL_RECALL_HOME', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: err, version: '9.9.9' });
  const summary = formatSummary([{ tool: 'Zed', action: 'added', detail: '/z/settings.json' }]);
  assert.equal(err.text(), summary + '\n');
  const log = fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8');
  assert.match(log, /^## \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ total-recall 9\.9\.9\n/);
  assert.ok(log.includes(summary));
});

test('runHostConfig: a second run appends and leaves the first entry as it was', async () => {
  takeChanges();
  const home = tmp();
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: fakeStderr(), version: '1' });
  const first = fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8');
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: fakeStderr(), version: '2' });
  const both = fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8');
  assert.ok(both.startsWith(first));
  assert.ok(both.length > first.length);
});

test('runHostConfig: nothing written and nothing failed says so in one line and adds no log entry', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  await runHostConfig([async () => {}], { env: { TOTAL_RECALL_HOME: home }, stderr: err, version: '1' });
  assert.equal(err.text(), `${P} No changes were made to other tools.\n`);
  assert.equal(fs.existsSync(path.join(home, 'postinstall.log')), false);
});

test('runHostConfig: TOTAL_RECALL_SKIP_HOST_CONFIG=1 runs no installer, says why, and writes no log', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  let called = false;
  await runHostConfig([async () => { called = true; recordChange('Zed', 'added', '/x'); }],
    { env: { TOTAL_RECALL_HOME: home, TOTAL_RECALL_SKIP_HOST_CONFIG: '1' }, stderr: err, version: '1' });
  assert.equal(called, false);
  assert.match(err.text(), /TOTAL_RECALL_SKIP_HOST_CONFIG=1/);
  assert.match(err.text(), /skipped/i);
  assert.equal(fs.existsSync(path.join(home, 'postinstall.log')), false);
});

test('runHostConfig: the skip switch counts only the exact value 1', async () => {
  for (const value of ['0', 'true', '', 'yes']) {
    takeChanges();
    let called = false;
    await runHostConfig([async () => { called = true; }],
      { env: { TOTAL_RECALL_HOME: tmp(), TOTAL_RECALL_SKIP_HOST_CONFIG: value }, stderr: fakeStderr(), version: '1' });
    assert.equal(called, true, `value ${JSON.stringify(value)}`);
  }
});

test('runHostConfig: an unwritable data directory still prints the summary and does not throw', async () => {
  takeChanges();
  const file = path.join(tmp(), 'a-file');
  fs.writeFileSync(file, '');
  const err = fakeStderr();
  await runHostConfig([installerThatRecords], { env: { TOTAL_RECALL_HOME: path.join(file, 'sub') }, stderr: err, version: '1' });
  assert.match(err.text(), /Zed: added \/z\/settings\.json/);
});

test('runHostConfig: an unknown data directory (no home variables) still prints the summary', async () => {
  takeChanges();
  const err = fakeStderr();
  await runHostConfig([installerThatRecords], { env: {}, stderr: err, version: '1' });
  assert.match(err.text(), /Zed: added/);
});

test('runHostConfig: an installer that throws becomes a failed line and the others still run', async () => {
  takeChanges();
  const home = tmp();
  const err = fakeStderr();
  async function installExploding() { throw new Error('boom'); }
  await runHostConfig([installExploding, installerThatRecords], { env: { TOTAL_RECALL_HOME: home }, stderr: err, version: '1' });
  assert.match(err.text(), /installExploding: failed: boom/);
  assert.match(err.text(), /Zed: added/);
  assert.match(fs.readFileSync(path.join(home, 'postinstall.log'), 'utf8'), /failed: boom/);
});

// --- postinstall.js as npm runs it: one child process per case, from a temp copy of the package ---
// The copy sits under node_modules/ so isRepoSelf() is false (the Kiro steering, the Windsurf rules and
// GEMINI.md are written only then). fetch-binary.js is replaced by a stub that keeps resolveDataDir and
// writes a marker from ensureBinary, so nothing is downloaded. A fake `hermes` on PATH records its calls.

const STUB_FETCH = `export * from './fetch-binary.real.js';
import fs from 'node:fs';
export async function ensureBinary() {
  fs.writeFileSync(process.env.TR_ENGINE_MARKER, 'ran');
  return { ok: true };
}
`;

/**
 * A temp copy of the package. `version` is written into its package.json (`null` removes the field);
 * `underNodeModules: false` makes it look like a git checkout (isRepoSelf() is then true).
 */
function makePackage({ version = VERSION, underNodeModules = true } = {}) {
  const root = tmp('tr-pkg-');
  const pkg = underNodeModules ? path.join(root, 'node_modules', '@strvmarv', 'total-recall') : path.join(root, 'total-recall');
  fs.mkdirSync(pkg, { recursive: true });
  fs.cpSync(path.join(REPO, 'scripts'), path.join(pkg, 'scripts'), {
    recursive: true,
    filter: (src) => !src.endsWith('.test.js'),
  });
  fs.renameSync(path.join(pkg, 'scripts', 'fetch-binary.js'), path.join(pkg, 'scripts', 'fetch-binary.real.js'));
  fs.writeFileSync(path.join(pkg, 'scripts', 'fetch-binary.js'), STUB_FETCH);
  fs.cpSync(path.join(REPO, 'hermes-plugin'), path.join(pkg, 'hermes-plugin'), { recursive: true });
  fs.cpSync(path.join(REPO, '.kiro', 'steering'), path.join(pkg, '.kiro', 'steering'), { recursive: true });
  const manifest = JSON.parse(fs.readFileSync(path.join(REPO, 'package.json'), 'utf8'));
  if (version === null) delete manifest.version; else manifest.version = version;
  fs.writeFileSync(path.join(pkg, 'package.json'), JSON.stringify(manifest, null, 2));
  return pkg;
}

/**
 * Runs the package's postinstall.js in a fresh sandbox. `setup({ home })` creates what the case needs
 * (tool directories) and returns the environment variables that point the installers at them.
 */
function runPostinstall(setup = () => ({}), pkgOptions) {
  const pkg = makePackage(pkgOptions);
  const home = tmp('tr-home-');
  const bin = tmp('tr-bin-');
  const hermesLog = path.join(bin, 'hermes-calls.txt');
  fs.writeFileSync(path.join(bin, 'hermes'), `#!/bin/sh\necho "$@" >> "${hermesLog}"\nif [ "$1 $2" = "config set" ] && [ -n "$FAKE_HERMES_FAIL_SET" ]; then exit 1; fi\necho /fake/hermes/config.yaml\n`, { mode: 0o755 });
  const marker = path.join(home, 'engine-marker');
  const extraEnv = setup({ home }) ?? {};
  const r = spawnSync(process.execPath, [path.join(pkg, 'scripts', 'postinstall.js')], {
    encoding: 'utf8',
    env: {
      PATH: `${bin}:/usr/bin:/bin`,
      HOME: home,
      USERPROFILE: home,
      TR_ENGINE_MARKER: marker,
      ...extraEnv,
    },
  });
  const logFile = path.join(extraEnv.TOTAL_RECALL_HOME ?? path.join(home, '.total-recall'), 'postinstall.log');
  return {
    ...r, pkg, home, marker,
    hermesCalls: () => (fs.existsSync(hermesLog) ? fs.readFileSync(hermesLog, 'utf8') : ''),
    log: () => (fs.existsSync(logFile) ? fs.readFileSync(logFile, 'utf8') : null),
  };
}

test('postinstall.js: no other tool detected prints the one-line answer, runs the engine step, exits 0, writes no log', () => {
  const r = runPostinstall();
  assert.equal(r.status, 0, r.stderr);
  assert.ok(r.stderr.trimEnd().endsWith(`${P} No changes were made to other tools.`), r.stderr);
  assert.equal(fs.readFileSync(r.marker, 'utf8'), 'ran');
  assert.equal(r.log(), null);
});

test('postinstall.js: TOTAL_RECALL_SKIP_HOST_CONFIG=1 still runs the engine step, exits 0 and leaves every other tool alone', () => {
  let hermesHome;
  const r = runPostinstall(({ home }) => {
    fs.mkdirSync(path.join(home, '.gemini'), { recursive: true });
    hermesHome = path.join(home, 'hermes'); // detected, so the fake hermes would be called if the switch failed
    fs.mkdirSync(hermesHome);
    return { TOTAL_RECALL_SKIP_HOST_CONFIG: '1', HERMES_HOME: hermesHome };
  });
  assert.equal(r.status, 0, r.stderr);
  assert.equal(fs.readFileSync(r.marker, 'utf8'), 'ran');
  assert.match(r.stderr, /TOTAL_RECALL_SKIP_HOST_CONFIG=1/);
  assert.deepEqual(fs.readdirSync(path.join(r.home, '.gemini')), []);
  assert.equal(r.hermesCalls(), '');
  assert.deepEqual(fs.readdirSync(hermesHome), []);
  assert.equal(r.log(), null);
});

// --- what each installer records (through the real postinstall.js) ---

/** The summary lines of a run as `Tool: action detail` strings (the prefix and indent removed). */
function lines(r) {
  return r.stderr.split('\n').filter((l) => l.startsWith(`${P}   `)).map((l) => l.slice(`${P}   `.length));
}

const ENTRY = { command: 'npx', args: ['--yes', `${NAME}@${VERSION}`] }; // what the script writes now
const ENTRY_BARE = { command: 'npx', args: ['--yes', NAME] }; // what it wrote before

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(value, null, 2));
}

test('Cline: a new settings file is added, an existing one is rewrote, both with the path', () => {
  let file;
  const r = runPostinstall(({ home }) => { file = path.join(home, 'cline', 'cline_mcp_settings.json'); return { CLINE_HOME: path.dirname(file) }; });
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(lines(r), [`Cline: added ${file}`]);
  assert.ok(r.stderr.trimEnd().split('\n').pop().startsWith(P), 'the summary is the last output');
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')).mcpServers['total-recall'], ENTRY);

  const r2 = runPostinstall(({ home }) => {
    file = path.join(home, 'cline', 'cline_mcp_settings.json');
    writeJson(file, { mcpServers: { other: { command: 'x' } } });
    return { CLINE_HOME: path.dirname(file) };
  });
  assert.deepEqual(lines(r2), [`Cline: rewrote ${file}`]);
});

test('Zed, Windsurf and Kiro MCP entries are reported with the tool name and path', () => {
  const dirs = {};
  const r = runPostinstall(({ home }) => {
    dirs.zed = path.join(home, 'zed'); dirs.ws = path.join(home, 'ws'); dirs.kiro = path.join(home, 'kiro');
    for (const d of Object.values(dirs)) fs.mkdirSync(d);
    return { ZED_SETTINGS: dirs.zed, WINDSURF_HOME: dirs.ws, KIRO_HOME: dirs.kiro };
  });
  assert.equal(r.status, 0, r.stderr);
  const got = lines(r);
  assert.ok(got.includes(`Zed: added ${path.join(dirs.zed, 'settings.json')}`), got.join('\n'));
  assert.ok(got.includes(`Windsurf: added ${path.join(dirs.ws, 'mcp_config.json')}`), got.join('\n'));
  assert.ok(got.includes(`Kiro: added ${path.join(dirs.kiro, 'settings', 'mcp.json')}`), got.join('\n'));
});

test('VS Code and the Copilot CLI entry it also writes are two lines, the second named "Copilot CLI"', () => {
  let vs, copilot;
  const r = runPostinstall(({ home }) => {
    vs = path.join(home, 'vscode-user'); fs.mkdirSync(vs);
    copilot = path.join(home, '.copilot', 'mcp-config.json');
    return { VSCODE_USER_DIR: vs };
  });
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(lines(r), [`VS Code: added ${path.join(vs, 'mcp.json')}`, `Copilot CLI: added ${copilot}`]);
});

test('a kept total-recall entry and a file that does not parse record nothing', () => {
  let zedFile;
  const r = runPostinstall(({ home }) => {
    const cline = path.join(home, 'cline');
    writeJson(path.join(cline, 'cline_mcp_settings.json'), { mcpServers: { 'total-recall': ENTRY } });
    const zed = path.join(home, 'zed'); fs.mkdirSync(zed);
    zedFile = path.join(zed, 'settings.json');
    fs.writeFileSync(zedFile, '{ // a comment\n}');
    return { CLINE_HOME: cline, ZED_SETTINGS: zed };
  });
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(lines(r), []);
  assert.match(r.stderr, /No changes were made to other tools\./);
  assert.equal(r.log(), null);
  assert.equal(fs.readFileSync(zedFile, 'utf8'), '{ // a comment\n}'); // untouched
});

test('a write that fails is a failed line (never added), the other tools are still configured, exit is 0', () => {
  let zedDir;
  const r = runPostinstall(({ home }) => {
    const notADir = path.join(home, 'a-file'); fs.writeFileSync(notADir, '');
    zedDir = path.join(home, 'zed'); fs.mkdirSync(zedDir);
    return { CLINE_HOME: path.join(notADir, 'sub'), ZED_SETTINGS: zedDir };
  });
  assert.equal(r.status, 0, r.stderr);
  const got = lines(r);
  assert.ok(got.some((l) => /^Cline: failed: /.test(l)), got.join('\n'));
  assert.ok(!got.some((l) => /^Cline: added/.test(l)), got.join('\n'));
  assert.ok(got.includes(`Zed: added ${path.join(zedDir, 'settings.json')}`), got.join('\n'));
  assert.match(r.log(), /Cline: failed: /);
});

// --- the remaining writes: steering, rules, Gemini CLI, Hermes ---

const STEERING = fs.readFileSync(path.join(REPO, '.kiro', 'steering', 'total-recall.md'), 'utf8');
/** The steering doc without its YAML front matter, as Windsurf's rules file and the GEMINI.md block hold it. */
const STEERING_BODY = STEERING.startsWith('---\n') ? STEERING.slice(STEERING.indexOf('\n---\n', 4) + 5) : STEERING;

test('Kiro: the steering file is added, or overwrote when it was already there', () => {
  let kiro;
  const r = runPostinstall(({ home }) => { kiro = path.join(home, 'kiro'); fs.mkdirSync(kiro); return { KIRO_HOME: kiro }; });
  assert.deepEqual(lines(r), [`Kiro: added ${path.join(kiro, 'settings', 'mcp.json')}`, `Kiro: added ${path.join(kiro, 'steering', 'total-recall.md')}`]);
  assert.equal(fs.readFileSync(path.join(kiro, 'steering', 'total-recall.md'), 'utf8'), STEERING);

  const r2 = runPostinstall(({ home }) => {
    kiro = path.join(home, 'kiro');
    fs.mkdirSync(path.join(kiro, 'steering'), { recursive: true });
    fs.writeFileSync(path.join(kiro, 'steering', 'total-recall.md'), 'old');
    return { KIRO_HOME: kiro };
  });
  assert.ok(lines(r2).includes(`Kiro: overwrote ${path.join(kiro, 'steering', 'total-recall.md')}`), r2.stderr);
});

test('Windsurf: the rules file is added, or overwrote, and holds the steering text without its front matter', () => {
  let ws;
  const r = runPostinstall(({ home }) => { ws = path.join(home, 'ws'); fs.mkdirSync(ws); return { WINDSURF_HOME: ws }; });
  assert.deepEqual(lines(r), [`Windsurf: added ${path.join(ws, 'mcp_config.json')}`, `Windsurf: added ${path.join(ws, 'rules', 'total-recall.md')}`]);
  assert.equal(fs.readFileSync(path.join(ws, 'rules', 'total-recall.md'), 'utf8'), STEERING_BODY);

  const r2 = runPostinstall(({ home }) => {
    ws = path.join(home, 'ws');
    fs.mkdirSync(path.join(ws, 'rules'), { recursive: true });
    fs.writeFileSync(path.join(ws, 'rules', 'total-recall.md'), 'old');
    return { WINDSURF_HOME: ws };
  });
  assert.ok(lines(r2).includes(`Windsurf: overwrote ${path.join(ws, 'rules', 'total-recall.md')}`), r2.stderr);
});

/** Gemini CLI's settings.json as the script writes it, for an MCP entry and a hook command. */
function geminiSettings({ args, command }) {
  return {
    mcpServers: { 'total-recall': { command: 'npx', args } },
    hooks: {
      BeforeAgent: {
        hooks: [{ type: 'command', command, timeout: 60000, name: 'total-recall Pinned Floor' }],
      },
    },
  };
}
const HOOK_BARE = `npx --yes ${NAME} pinned-floor --host gemini-cli`;
const HOOK_PINNED = `npx --yes ${NAME}@${VERSION} pinned-floor --host gemini-cli`;
const GEMINI_SETTINGS = geminiSettings({ args: ['--yes', `${NAME}@${VERSION}`], command: HOOK_PINNED });
const GEMINI_SETTINGS_BARE = geminiSettings({ args: ['--yes', NAME], command: HOOK_BARE });

test('Gemini CLI, fresh: the settings file, the MCP entry, the per-prompt hook and GEMINI.md are separate lines', () => {
  let g;
  const r = runPostinstall(({ home }) => { g = path.join(home, 'gemini'); fs.mkdirSync(g); return { GEMINI_HOME: g }; });
  assert.equal(r.status, 0, r.stderr);
  const settings = path.join(g, 'settings.json');
  assert.deepEqual(lines(r), [
    `Gemini CLI: added ${settings}`,
    `Gemini CLI: added MCP entry in ${settings}`,
    `Gemini CLI: added BeforeAgent hook (runs on every prompt) in ${settings}`,
    `Gemini CLI: added ${path.join(g, 'GEMINI.md')}`,
  ]);
  // characterization: the same file as before, with the version added to the entry and the hook
  assert.equal(fs.readFileSync(settings, 'utf8'), JSON.stringify(GEMINI_SETTINGS, null, 2) + '\n');
  const md = fs.readFileSync(path.join(g, 'GEMINI.md'), 'utf8');
  assert.ok(md.includes(`<!-- total-recall -->\n${STEERING_BODY}\n<!-- /total-recall -->`));
});

test('Gemini CLI, already configured and current: settings.json and GEMINI.md are still rewritten, so they are still reported, with no entry or hook line', () => {
  let g;
  const r = runPostinstall(({ home }) => {
    g = path.join(home, 'gemini');
    writeJson(path.join(g, 'settings.json'), GEMINI_SETTINGS);
    fs.writeFileSync(path.join(g, 'GEMINI.md'), `# mine\n\n<!-- total-recall -->\nold\n<!-- /total-recall -->\n`);
    return { GEMINI_HOME: g };
  });
  assert.deepEqual(lines(r), [`Gemini CLI: rewrote ${path.join(g, 'settings.json')}`, `Gemini CLI: rewrote ${path.join(g, 'GEMINI.md')}`]);
  assert.ok(fs.readFileSync(path.join(g, 'GEMINI.md'), 'utf8').startsWith('# mine'));
});

test('Hermes: the plugin copy and the memory provider switch are two lines, and hermes was asked to switch', () => {
  let hh;
  const r = runPostinstall(({ home }) => { hh = path.join(home, 'hermes'); fs.mkdirSync(hh); return { HERMES_HOME: hh }; });
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(lines(r), [
    `Hermes: added ${path.join(hh, 'plugins', 'total-recall')}`,
    'Hermes: switched memory.provider to total-recall (hermes config set)',
  ]);
  assert.match(r.hermesCalls(), /config set memory\.provider total-recall/);
  // characterization: the plugin directory holds the shipped files
  assert.deepEqual(fs.readdirSync(path.join(hh, 'plugins', 'total-recall')).sort(), fs.readdirSync(path.join(REPO, 'hermes-plugin')).sort());
});

test('Hermes: a plugin directory that was already there is overwrote', () => {
  let hh;
  const r = runPostinstall(({ home }) => {
    hh = path.join(home, 'hermes');
    fs.mkdirSync(path.join(hh, 'plugins', 'total-recall'), { recursive: true });
    return { HERMES_HOME: hh };
  });
  assert.ok(lines(r).includes(`Hermes: overwrote ${path.join(hh, 'plugins', 'total-recall')}`), r.stderr);
});

test('Hermes: a failed config set is a failed line after the copy line', () => {
  const r = runPostinstall(({ home }) => {
    const hh = path.join(home, 'hermes'); fs.mkdirSync(hh);
    return { HERMES_HOME: hh, FAKE_HERMES_FAIL_SET: '1' };
  });
  assert.equal(r.status, 0, r.stderr);
  const got = lines(r);
  assert.match(got[0], /^Hermes: added /);
  assert.match(got[1], /^Hermes: failed: /);
  assert.ok(!got.some((l) => l.startsWith('Hermes: switched')), got.join('\n'));
});

test('Hermes: a failing plugin copy is a failed line, the other tools are still configured, the summary prints, exit 0', () => {
  let g;
  const r = runPostinstall(({ home }) => {
    const notADir = path.join(home, 'a-file'); fs.writeFileSync(notADir, '');
    g = path.join(home, 'gemini'); fs.mkdirSync(g);
    return { HERMES_HOME: notADir, GEMINI_HOME: g };
  });
  assert.equal(r.status, 0, r.stderr);
  const got = lines(r);
  assert.match(got[0], /^Hermes: failed: /);
  assert.ok(got.includes(`Gemini CLI: added ${path.join(g, 'settings.json')}`), got.join('\n'));
  assert.match(r.log(), /Hermes: failed: /);
});

test('a failing step of another tool (Zed) is a failed line too', () => {
  const r = runPostinstall(({ home }) => {
    const notADir = path.join(home, 'a-file'); fs.writeFileSync(notADir, '');
    return { ZED_SETTINGS: notADir };
  });
  assert.equal(r.status, 0, r.stderr);
  assert.ok(lines(r).some((l) => /^Zed: failed: /.test(l)), r.stderr);
});

// --- the documentation names the summary, the log and the skip switch ---

test('docs/npm-postinstall.md describes the summary, the log file and TOTAL_RECALL_SKIP_HOST_CONFIG', () => {
  const doc = fs.readFileSync(path.join(REPO, 'docs', 'npm-postinstall.md'), 'utf8');
  assert.ok(doc.includes('TOTAL_RECALL_SKIP_HOST_CONFIG'));
  assert.ok(doc.includes('postinstall.log'));
  assert.match(doc, /TOTAL_RECALL_HOME/);
});

test('docs/npm-postinstall.md no longer says that a failed Hermes copy ends the script', () => {
  const doc = fs.readFileSync(path.join(REPO, 'docs', 'npm-postinstall.md'), 'utf8');
  assert.ok(!/not wrapped and ends the script/.test(doc));
  assert.ok(!/except the Hermes file copy/.test(doc));
});

// --- package-spec: the pinned spec, the script's own entry form and what to do with an existing entry ---

import {
  pinnedSpec, npxEntry, isOwnNpxEntry, hookCommand, isOwnHookCommand, decide,
} from './lib/package-spec.js';

test('pinnedSpec: the version is added only for an installed package with a valid version', () => {
  assert.equal(pinnedSpec({ version: '4.8.0', repoSelf: false }), `${NAME}@4.8.0`);
  assert.equal(pinnedSpec({ version: '4.8.0-beta.1', repoSelf: false }), `${NAME}@4.8.0-beta.1`);
  assert.equal(pinnedSpec({ version: '4.8.0', repoSelf: true }), NAME);
  for (const bad of [undefined, null, '', 'unknown', 'x', 'latest', '4.8', '4.8.0+build', '4.8.0 && rm', '^4.8.0']) {
    assert.equal(pinnedSpec({ version: bad, repoSelf: false }), NAME, JSON.stringify(bad));
  }
});

test('npxEntry: command, args and then the extra keys, in that order', () => {
  const entry = npxEntry({ cwd: '${workspaceFolder}' }, { version: '9.8.7', repoSelf: false });
  assert.deepEqual(entry, { command: 'npx', args: ['--yes', `${NAME}@9.8.7`], cwd: '${workspaceFolder}' });
  assert.deepEqual(Object.keys(entry), ['command', 'args', 'cwd']);
  assert.deepEqual(npxEntry({}, { version: '9.8.7', repoSelf: true }), { command: 'npx', args: ['--yes', NAME] });
});

test('isOwnNpxEntry: exactly npx --yes <name>, bare or with a valid version', () => {
  const own = (args, command = 'npx') => isOwnNpxEntry({ command, args });
  assert.equal(own(['--yes', NAME]), true);
  assert.equal(own(['--yes', `${NAME}@4.7.0`]), true);
  assert.equal(own(['--yes', `${NAME}@4.8.0-beta.1`]), true);
  assert.equal(isOwnNpxEntry({ command: 'npx', args: ['--yes', NAME], cwd: '/x' }), true); // other keys do not matter
  assert.equal(own(['-y', NAME]), false);
  assert.equal(own(['--yes', NAME, '--x']), false);
  assert.equal(own(['--yes', `${NAME}@latest`]), false);
  assert.equal(own(['--yes', `${NAME}@^4`]), false);
  assert.equal(own(['--yes', `${NAME}@4.8.0+build`]), false);
  assert.equal(own(['--yes', '@other/pkg']), false);
  assert.equal(own(['--yes', NAME], 'node'), false);
  assert.equal(isOwnNpxEntry(null), false);
  assert.equal(isOwnNpxEntry({ command: 'npx' }), false);
});

test('hookCommand / isOwnHookCommand: the Gemini floor command with and without a version', () => {
  assert.equal(hookCommand({ version: '9.8.7', repoSelf: false }), `npx --yes ${NAME}@9.8.7 pinned-floor --host gemini-cli`);
  assert.equal(hookCommand({ version: '9.8.7', repoSelf: true }), `npx --yes ${NAME} pinned-floor --host gemini-cli`);
  assert.equal(isOwnHookCommand(`npx --yes ${NAME} pinned-floor --host gemini-cli`), true);
  assert.equal(isOwnHookCommand(`npx --yes ${NAME}@4.7.0 pinned-floor --host gemini-cli`), true);
  assert.equal(isOwnHookCommand(`npx --yes ${NAME}@latest pinned-floor --host gemini-cli`), false);
  assert.equal(isOwnHookCommand(`npx --yes ${NAME} pinned-floor --host gemini-cli --extra`), false);
  assert.equal(isOwnHookCommand('my-own-script'), false);
  assert.equal(isOwnHookCommand(undefined), false);
});

test('decide: keep, refresh or custom for an existing entry', () => {
  const spec = `${NAME}@9.8.7`;
  const e = (args, command = 'npx') => ({ command, args });
  assert.equal(decide(e(['--yes', NAME]), spec), 'refresh');
  assert.equal(decide(e(['--yes', `${NAME}@4.7.0`]), spec), 'refresh');
  assert.equal(decide(e(['--yes', spec]), spec), 'keep');
  assert.equal(decide(e(['-y', NAME]), spec), 'keep'); // the bare name among other arguments is kept as before
  assert.equal(decide(e(['--yes', NAME, '--x']), spec), 'keep');
  assert.equal(decide(e(['--yes', `${NAME}@latest`]), spec), 'custom');
  assert.equal(decide(e(['--yes', `${NAME}@4.7.0`, '--x']), spec), 'custom');
  assert.equal(decide(e(['--yes', '@other/pkg']), spec), 'custom');
  assert.equal(decide(e(['--yes', NAME], 'node'), spec), 'custom');
  assert.equal(decide({ command: 'npx' }, spec), 'custom');
  // an unversioned spec (git checkout, no valid version) never refreshes
  assert.equal(decide(e(['--yes', NAME]), NAME), 'keep');
  assert.equal(decide(e(['--yes', `${NAME}@4.7.0`]), NAME), 'keep');
});

// --- pinned entries: new, refreshed, kept, custom, and the cases that must not pin ---

const PINNED = `${NAME}@${VERSION}`;

function entryIn(file, key = 'mcpServers') {
  return JSON.parse(fs.readFileSync(file, 'utf8'))[key]['total-recall'];
}

test('every tool that goes through mergeJsonConfig writes a new entry pinned to the package version', () => {
  const f = {};
  const r = runPostinstall(({ home }) => {
    for (const k of ['cline', 'kiro', 'ws', 'zed', 'vs']) { f[k] = path.join(home, k); fs.mkdirSync(f[k]); }
    return { CLINE_HOME: f.cline, KIRO_HOME: f.kiro, WINDSURF_HOME: f.ws, ZED_SETTINGS: f.zed, VSCODE_USER_DIR: f.vs };
  });
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(entryIn(path.join(f.cline, 'cline_mcp_settings.json')), ENTRY);
  assert.deepEqual(entryIn(path.join(f.ws, 'mcp_config.json')), ENTRY);
  assert.deepEqual(entryIn(path.join(f.zed, 'settings.json'), 'context_servers'), ENTRY);
  assert.deepEqual(entryIn(path.join(f.vs, 'mcp.json'), 'servers'), ENTRY);
  assert.deepEqual(entryIn(path.join(r.home, '.copilot', 'mcp-config.json')), ENTRY);
  const kiro = entryIn(path.join(f.kiro, 'settings', 'mcp.json'));
  assert.deepEqual(kiro, { ...ENTRY, cwd: '${workspaceFolder}' });
  assert.deepEqual(Object.keys(kiro), ['command', 'args', 'cwd']);
});

test('an entry in the script\'s own older form is refreshed to the package version: other keys and the rest of the file stay', () => {
  for (const old of [[ '--yes', NAME ], [ '--yes', `${NAME}@4.0.4` ]]) {
    let file;
    const r = runPostinstall(({ home }) => {
      file = path.join(home, 'cline', 'cline_mcp_settings.json');
      writeJson(file, { other: [1, 2], mcpServers: { 'total-recall': { command: 'npx', args: old, alwaysAllow: ['x'] }, keep: { command: 'y' } } });
      return { CLINE_HOME: path.dirname(file) };
    });
    assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(lines(r), [`Cline: rewrote ${file}`]);
    assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), {
      other: [1, 2],
      mcpServers: { 'total-recall': { command: 'npx', args: ['--yes', PINNED], alwaysAllow: ['x'] }, keep: { command: 'y' } },
    });
  }
});

test('Kiro, Windsurf, Zed and VS Code entries in the own bare form are refreshed too', () => {
  const f = {};
  const r = runPostinstall(({ home }) => {
    f.kiro = path.join(home, 'kiro'); f.ws = path.join(home, 'ws'); f.zed = path.join(home, 'zed'); f.vs = path.join(home, 'vs');
    writeJson(path.join(f.kiro, 'settings', 'mcp.json'), { mcpServers: { 'total-recall': { ...ENTRY_BARE, cwd: '${workspaceFolder}' } } });
    writeJson(path.join(f.ws, 'mcp_config.json'), { mcpServers: { 'total-recall': ENTRY_BARE } });
    writeJson(path.join(f.zed, 'settings.json'), { context_servers: { 'total-recall': ENTRY_BARE } });
    writeJson(path.join(f.vs, 'mcp.json'), { servers: { 'total-recall': ENTRY_BARE } });
    writeJson(path.join(home, '.copilot', 'mcp-config.json'), { mcpServers: { 'total-recall': ENTRY_BARE } });
    return { KIRO_HOME: f.kiro, WINDSURF_HOME: f.ws, ZED_SETTINGS: f.zed, VSCODE_USER_DIR: f.vs };
  });
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(entryIn(path.join(f.kiro, 'settings', 'mcp.json')), { ...ENTRY, cwd: '${workspaceFolder}' });
  assert.deepEqual(entryIn(path.join(f.ws, 'mcp_config.json')), ENTRY);
  assert.deepEqual(entryIn(path.join(f.zed, 'settings.json'), 'context_servers'), ENTRY);
  assert.deepEqual(entryIn(path.join(f.vs, 'mcp.json'), 'servers'), ENTRY);
  assert.deepEqual(entryIn(path.join(r.home, '.copilot', 'mcp-config.json')), ENTRY);
  const got = lines(r);
  assert.ok(got.includes(`Copilot CLI: rewrote ${path.join(r.home, '.copilot', 'mcp-config.json')}`), got.join('\n'));
});

test('an entry already pinned to the package version is not rewritten and has no summary line', () => {
  let file, before;
  const r = runPostinstall(({ home }) => {
    file = path.join(home, 'cline', 'cline_mcp_settings.json');
    writeJson(file, { mcpServers: { 'total-recall': ENTRY } });
    before = fs.readFileSync(file, 'utf8');
    return { CLINE_HOME: path.dirname(file) };
  });
  assert.deepEqual(lines(r), []);
  assert.equal(fs.readFileSync(file, 'utf8'), before);
});

test('the bare name among other arguments is kept silently; any other form is custom and gets the existing line', () => {
  const cases = [
    { entry: { command: 'npx', args: ['-y', NAME] }, custom: false },
    { entry: { command: 'npx', args: ['--yes', NAME, '--x'] }, custom: false },
    { entry: { command: 'node', args: ['/x/start.js'] }, custom: true },
    { entry: { command: 'npx', args: ['--yes', '@other/pkg'] }, custom: true },
    { entry: { command: 'npx', args: ['--yes', `${NAME}@latest`] }, custom: true },
    { entry: { command: 'npx', args: ['--yes', `${NAME}@4.0.4`, '--x'] }, custom: true },
  ];
  for (const { entry, custom } of cases) {
    let file, before;
    const r = runPostinstall(({ home }) => {
      file = path.join(home, 'cline', 'cline_mcp_settings.json');
      writeJson(file, { mcpServers: { 'total-recall': entry } });
      before = fs.readFileSync(file, 'utf8');
      return { CLINE_HOME: path.dirname(file) };
    });
    assert.equal(r.status, 0, r.stderr);
    assert.deepEqual(lines(r), [], JSON.stringify(entry));
    assert.equal(fs.readFileSync(file, 'utf8'), before, JSON.stringify(entry));
    assert.equal(/already has a custom total-recall entry/.test(r.stderr), custom, JSON.stringify(entry));
  }
});

test('a git checkout, a missing version and an invalid version write the bare name and change no existing entry', () => {
  const variants = [
    { label: 'git checkout', opts: { underNodeModules: false } },
    { label: 'missing version', opts: { version: null } },
    { label: 'invalid version', opts: { version: 'x' } },
  ];
  for (const { label, opts } of variants) {
    let fresh, pinned, bare;
    const r = runPostinstall(({ home }) => {
      fresh = path.join(home, 'cline');
      pinned = path.join(home, 'ws', 'mcp_config.json');
      bare = path.join(home, 'zed', 'settings.json');
      writeJson(pinned, { mcpServers: { 'total-recall': { command: 'npx', args: ['--yes', `${NAME}@4.0.4`] } } });
      writeJson(bare, { context_servers: { 'total-recall': ENTRY_BARE } });
      return { CLINE_HOME: fresh, WINDSURF_HOME: path.dirname(pinned), ZED_SETTINGS: path.dirname(bare) };
    }, opts);
    assert.equal(r.status, 0, `${label}: ${r.stderr}`);
    assert.deepEqual(entryIn(path.join(fresh, 'cline_mcp_settings.json')), ENTRY_BARE, label);
    assert.deepEqual(entryIn(pinned), { command: 'npx', args: ['--yes', `${NAME}@4.0.4`] }, label); // kept, silently
    assert.deepEqual(entryIn(bare, 'context_servers'), ENTRY_BARE, label);
    assert.ok(!/custom total-recall entry/.test(r.stderr), `${label}: ${r.stderr}`);
  }
});

test('in a git checkout the Kiro step writes no MCP entry at all', () => {
  let kiro;
  const r = runPostinstall(({ home }) => { kiro = path.join(home, 'kiro'); fs.mkdirSync(kiro); return { KIRO_HOME: kiro }; }, { underNodeModules: false });
  assert.deepEqual(fs.readdirSync(kiro), []);
  assert.deepEqual(lines(r), []);
});

// --- Gemini CLI: the MCP entry and the hook are pinned and refreshed by the same rules ---

function runGemini(settings, pkgOptions, { extraSetup } = {}) {
  let g;
  const r = runPostinstall(({ home }) => {
    g = path.join(home, 'gemini');
    if (settings !== undefined) writeJson(path.join(g, 'settings.json'), settings); else fs.mkdirSync(g, { recursive: true });
    if (extraSetup) extraSetup(g);
    return { GEMINI_HOME: g };
  }, pkgOptions);
  const file = path.join(g, 'settings.json');
  return { r, file, got: lines(r), saved: () => JSON.parse(fs.readFileSync(file, 'utf8')) };
}

test('Gemini CLI: an entry and a hook in the own bare form are refreshed, with one line each besides the settings.json line', () => {
  const { r, file, got, saved } = runGemini(GEMINI_SETTINGS_BARE);
  assert.equal(r.status, 0, r.stderr);
  assert.deepEqual(got.slice(0, 3), [
    `Gemini CLI: rewrote ${file}`,
    `Gemini CLI: rewrote MCP entry in ${file}`,
    `Gemini CLI: rewrote BeforeAgent hook in ${file}`,
  ]);
  assert.deepEqual(saved(), GEMINI_SETTINGS);
});

test('Gemini CLI: an older pinned entry and hook are refreshed, and other hooks and keys stay', () => {
  const old = geminiSettings({ args: ['--yes', `${NAME}@4.0.4`], command: `npx --yes ${NAME}@4.0.4 pinned-floor --host gemini-cli` });
  old.theme = 'dark';
  old.hooks.BeforeAgent.hooks.unshift({ type: 'command', command: 'echo mine', name: 'mine' });
  const { got, file, saved } = runGemini(old);
  assert.deepEqual(got.slice(0, 3), [`Gemini CLI: rewrote ${file}`, `Gemini CLI: rewrote MCP entry in ${file}`, `Gemini CLI: rewrote BeforeAgent hook in ${file}`]);
  const after = saved();
  assert.equal(after.theme, 'dark');
  assert.deepEqual(after.hooks.BeforeAgent.hooks[0], { type: 'command', command: 'echo mine', name: 'mine' });
  assert.equal(after.hooks.BeforeAgent.hooks[1].command, HOOK_PINNED);
  assert.deepEqual(after.mcpServers['total-recall'].args, ['--yes', `${NAME}@${VERSION}`]);
});

test('Gemini CLI: a custom hook command is left alone, and the bare name among other arguments is kept silently', () => {
  const custom = geminiSettings({ args: ['-y', NAME], command: 'my-own-floor --x' });
  const { got, file, saved } = runGemini(custom);
  assert.deepEqual(got.slice(0, 1), [`Gemini CLI: rewrote ${file}`]);
  assert.ok(!got.some((l) => /MCP entry|BeforeAgent hook/.test(l)), got.join('\n'));
  assert.deepEqual(saved(), custom);
});

test('Gemini CLI: any other MCP entry form is custom, gets the existing line and is left alone', () => {
  for (const args of [['--yes', `${NAME}@latest`], ['--yes', '@other/pkg']]) {
    const settings = geminiSettings({ args, command: HOOK_PINNED });
    const { r, got, saved } = runGemini(settings);
    assert.match(r.stderr, /Gemini already has a custom total-recall MCP entry/);
    assert.ok(!got.some((l) => /MCP entry/.test(l)), got.join('\n'));
    assert.deepEqual(saved().mcpServers['total-recall'].args, args);
  }
});

test('Gemini CLI: a git checkout and a missing or invalid version write the bare entry and hook and change nothing that exists', () => {
  for (const [label, opts] of [['git checkout', { underNodeModules: false }], ['missing version', { version: null }], ['invalid version', { version: 'x' }]]) {
    const fresh = runGemini(undefined, opts);
    assert.deepEqual(fresh.saved(), GEMINI_SETTINGS_BARE, label);
    const kept = runGemini(GEMINI_SETTINGS, opts); // an existing pinned entry and hook stay as they are
    assert.deepEqual(kept.saved(), GEMINI_SETTINGS, label);
    assert.ok(!kept.got.some((l) => /MCP entry|BeforeAgent hook/.test(l)), `${label}: ${kept.got.join('\n')}`);
    const bare = runGemini(GEMINI_SETTINGS_BARE, opts);
    assert.deepEqual(bare.saved(), GEMINI_SETTINGS_BARE, label);
  }
});

// --- the documentation says the entries are pinned, and when they are refreshed ---

const ENTRY_PAGES = ['cline', 'copilot-cli', 'kiro', 'windsurf', 'zed', 'gemini-cli', 'vscode', 'opencode']
  .map((h) => path.join('docs', 'hosts', `${h}.md`));

test('no page that describes the script\'s entry says it has no version (Kiro\'s static workspace hooks aside)', () => {
  for (const page of ['docs/npm-postinstall.md', ...ENTRY_PAGES]) {
    const text = fs.readFileSync(path.join(REPO, page), 'utf8');
    for (const line of text.split('\n')) {
      if (/\bno version\b|\bunversioned\b|\bwithout a version\b/i.test(line)) {
        // Allowed: Kiro's static workspace hooks, a git checkout or an unreadable version (which write no version
        // by design), entries from an earlier release, and OpenCode's reason for not carrying an unversioned entry.
        assert.ok(/workspace hooks|git checkout|`node_modules`|earlier release|an unversioned `npx` entry/.test(line), `${page}: ${line.slice(0, 120)}`);
      }
    }
  }
});

test('docs/npm-postinstall.md states the pin, the refresh rule, the other forms and how to hold a version', () => {
  const doc = fs.readFileSync(path.join(REPO, 'docs', 'npm-postinstall.md'), 'utf8');
  assert.match(doc, /pinned to the version of the package that was installed/);
  assert.match(doc, /exactly the form the script writes/);
  assert.match(doc, /any other entry is left alone/i);
  assert.match(doc, /TOTAL_RECALL_SKIP_HOST_CONFIG=1[^\n]*(keep|hold) a version|(keep|hold) a version[^\n]*TOTAL_RECALL_SKIP_HOST_CONFIG=1/);
  assert.match(doc, /rewrites the whole file/);
  assert.match(doc, /with comments[^\n]*not refreshed|not refreshed[^\n]*with comments/);
});

test('the host pages that name the entry say it is pinned and refreshed', () => {
  for (const page of ['cline', 'kiro', 'windsurf', 'zed', 'gemini-cli', 'vscode'].map((h) => path.join('docs', 'hosts', `${h}.md`))) {
    const text = fs.readFileSync(path.join(REPO, page), 'utf8');
    assert.match(text, /pinned to the installed version/, page);
  }
});

// --- vscodeUserDir: VS Code's User directory, shared by the VS Code and Cline steps ---

import { vscodeUserDir } from './lib/vscode-user-dir.js';

test('vscodeUserDir: VSCODE_USER_DIR wins, then the platform default', () => {
  const home = '/h';
  assert.equal(vscodeUserDir({ env: { VSCODE_USER_DIR: '/custom' }, platform: 'darwin', home }), '/custom');
  assert.equal(vscodeUserDir({ env: {}, platform: 'darwin', home }), path.join(home, 'Library', 'Application Support', 'Code', 'User'));
  assert.equal(vscodeUserDir({ env: {}, platform: 'linux', home }), path.join(home, '.config', 'Code', 'User'));
  assert.equal(vscodeUserDir({ env: {}, platform: 'freebsd', home }), path.join(home, '.config', 'Code', 'User'));
  assert.equal(vscodeUserDir({ env: { APPDATA: '/a' }, platform: 'win32', home }), path.join('/a', 'Code', 'User'));
});

test('vscodeUserDir: no home gives null (after the override), and Windows without APPDATA gives null', () => {
  assert.equal(vscodeUserDir({ env: {}, platform: 'linux', home: '' }), null);
  assert.equal(vscodeUserDir({ env: {}, platform: 'darwin', home: null }), null);
  assert.equal(vscodeUserDir({ env: { VSCODE_USER_DIR: '/custom' }, platform: 'linux', home: '' }), '/custom');
  assert.equal(vscodeUserDir({ env: {}, platform: 'win32', home: '/h' }), null);
});

test('vscodeUserDir: with no arguments it reads the process environment, platform and home', () => {
  const saved = process.env.VSCODE_USER_DIR;
  process.env.VSCODE_USER_DIR = '/from-env';
  try {
    assert.equal(vscodeUserDir(), '/from-env');
  } finally {
    if (saved === undefined) delete process.env.VSCODE_USER_DIR; else process.env.VSCODE_USER_DIR = saved;
  }
});
