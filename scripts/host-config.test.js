import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  recordChange, takeChanges, formatSummary, formatLogEntry, appendLog,
} from './lib/change-log.js';

const P = '[total-recall:postinstall]';

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

function makePackage() {
  const root = tmp('tr-pkg-');
  const pkg = path.join(root, 'node_modules', '@strvmarv', 'total-recall');
  fs.mkdirSync(pkg, { recursive: true });
  fs.cpSync(path.join(REPO, 'scripts'), path.join(pkg, 'scripts'), {
    recursive: true,
    filter: (src) => !src.endsWith('.test.js'),
  });
  fs.renameSync(path.join(pkg, 'scripts', 'fetch-binary.js'), path.join(pkg, 'scripts', 'fetch-binary.real.js'));
  fs.writeFileSync(path.join(pkg, 'scripts', 'fetch-binary.js'), STUB_FETCH);
  fs.cpSync(path.join(REPO, 'hermes-plugin'), path.join(pkg, 'hermes-plugin'), { recursive: true });
  fs.cpSync(path.join(REPO, '.kiro', 'steering'), path.join(pkg, '.kiro', 'steering'), { recursive: true });
  fs.copyFileSync(path.join(REPO, 'package.json'), path.join(pkg, 'package.json'));
  return pkg;
}

/**
 * Runs the package's postinstall.js in a fresh sandbox. `setup({ home })` creates what the case needs
 * (tool directories) and returns the environment variables that point the installers at them.
 */
function runPostinstall(setup = () => ({})) {
  const pkg = makePackage();
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

const ENTRY = { command: 'npx', args: ['--yes', '@strvmarv/total-recall'] };

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

const GEMINI_SETTINGS = {
  mcpServers: { 'total-recall': ENTRY },
  hooks: {
    BeforeAgent: {
      hooks: [{
        type: 'command',
        command: 'npx --yes @strvmarv/total-recall pinned-floor --host gemini-cli',
        timeout: 60000,
        name: 'total-recall Pinned Floor',
      }],
    },
  },
};

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
  // characterization: what is written is what it was before this change
  assert.equal(fs.readFileSync(settings, 'utf8'), JSON.stringify(GEMINI_SETTINGS, null, 2) + '\n');
  const md = fs.readFileSync(path.join(g, 'GEMINI.md'), 'utf8');
  assert.ok(md.includes(`<!-- total-recall -->\n${STEERING_BODY}\n<!-- /total-recall -->`));
});

test('Gemini CLI, already configured: settings.json and GEMINI.md are still rewritten, so they are still reported', () => {
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

const NAME = '@strvmarv/total-recall';

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
