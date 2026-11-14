// scripts/sync-version.js — the one place that knows where the release version lives.
// Zero dependencies, ESM.
//
//   node scripts/sync-version.js <version>              set every version field (package.json too)
//   node scripts/sync-version.js --check                every field equals package.json's version
//   node scripts/sync-version.js --check --expect <v>   ... and package.json's version equals <v>
//
// `--root <dir>` runs against another tree (tests); `--no-git` skips the tracked-manifest guard.
// Exit codes: 0 ok, 1 a version problem, 2 a usage error.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

// SemVer 2.0 without build metadata, narrowed by what the release workflow does with the string:
// the publish step takes everything after the LAST '-' as the npm dist-tag, so the prerelease must
// start with a letter (a purely numeric tag is not a valid npm tag) and must hold no further '-'.
const VERSION_RE = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[A-Za-z][0-9A-Za-z]*(?:\.(?:0|[1-9]\d*|[0-9]*[A-Za-z][0-9A-Za-z]*))*)?$/;

export function isValidVersion(version) {
  return typeof version === 'string' && VERSION_RE.test(version);
}

// ---- the version files ----
// One entry per file; each entry lists the fields that carry the version. `read` returns the
// current value of the field, or null when the file does not carry it.

// A field is located by position, never by a global string replace: another entry in the same file
// (a dependency in the lockfile, say) may carry the same version string and must stay untouched.
const replaceValue = (re, version) => (text) => (re.test(text) ? text.replace(re, (_m, pre, _old, post) => pre + version + post) : null);

const jsonField = (name, pick, re) => ({
  name,
  read(text) {
    try { const v = pick(JSON.parse(text)); return typeof v === 'string' ? v : null; } catch { return null; }
  },
  write: (text, version) => replaceValue(re, version)(text),
});

const topLevel = (o) => o.version;
const lockRoot = (o) => o.packages?.['']?.version;

// the top-level key sits at the first indent level; the lockfile root is packages[""], before any nested object
const TOP_RE = /^(  "version"[ \t]*:[ \t]*")([^"\r\n]*)(")/m;
const LOCK_ROOT_RE = /("packages"\s*:\s*\{\s*""\s*:\s*\{[^{}]*?"version"\s*:\s*")([^"\r\n]*)(")/;
const YAML_RE = /^(version:[ \t]*["']?)([^"'\s#]+)(["']?[ \t]*(?:#.*)?)$/m;

const yamlField = {
  name: 'version',
  read(text) {
    const m = YAML_RE.exec(text);
    return m ? m[2] : null;
  },
  write: (text, version) => replaceValue(YAML_RE, version)(text),
};

export const VERSION_FILES = [
  { file: 'package.json', fields: [jsonField('version', topLevel, TOP_RE)] },
  { file: 'package-lock.json', fields: [jsonField('version', topLevel, TOP_RE), jsonField('packages[""].version', lockRoot, LOCK_ROOT_RE)] },
  { file: '.claude-plugin/plugin.json', fields: [jsonField('version', topLevel, TOP_RE)] },
  { file: '.github/plugin/plugin.json', fields: [jsonField('version', topLevel, TOP_RE)] },
  { file: '.cursor-plugin/plugin.json', fields: [jsonField('version', topLevel, TOP_RE)] },
  { file: '.codex-plugin/plugin.json', fields: [jsonField('version', topLevel, TOP_RE)] },
  { file: 'hermes-plugin/plugin.yaml', fields: [yamlField] },
];

function readText(root, rel) {
  try { return fs.readFileSync(path.join(root, rel), 'utf8'); } catch { return null; }
}

const PLUGIN_MANIFEST_RE = /(^|\/)plugin\.(json|ya?ml)$/;

// Every tracked file, from `git ls-files`.
// Throws when git cannot list the tree, so the guard fails closed instead of passing silently.
export function gitListManifests(root) {
  const r = spawnSync('git', ['-C', root, 'ls-files', '-z'], { encoding: 'utf8' });
  if (r.error || r.status !== 0) throw new Error(r.error ? r.error.message : (r.stderr || '').trim() || `git exited ${r.status}`);
  return r.stdout.split('\0').filter(Boolean);
}

/**
 * Compares every version field with package.json's version, and every tracked plugin manifest
 * with the list of version files. `expect` additionally pins package.json to one version
 * (the release workflow passes the git tag).
 * Returns {ok, problems}; each problem is {field, expected, actual, reason}.
 */
export function checkVersions({ root, listManifests, expect }) {
  const problems = [];
  const pkgText = readText(root, 'package.json');
  const pkgField = VERSION_FILES[0].fields[0];
  const current = pkgText === null ? null : pkgField.read(pkgText);
  if (current === null || !isValidVersion(current)) {
    problems.push({
      field: 'package.json: version',
      expected: null,
      actual: current,
      reason: current === null ? 'package.json has no version' : `invalid version ${JSON.stringify(current)}`,
    });
    return { ok: false, problems };
  }
  if (expect !== undefined) {
    if (!isValidVersion(expect)) {
      problems.push({ field: 'package.json: version', expected: expect, actual: current, reason: `the expected version ${JSON.stringify(expect)} is invalid` });
      return { ok: false, problems };
    }
    if (expect !== current) {
      problems.push({ field: 'package.json: version', expected: expect, actual: current, reason: 'package.json does not match the expected version' });
    }
  }
  for (const { file, fields } of VERSION_FILES) {
    if (file === 'package.json') continue;
    const text = readText(root, file);
    for (const f of fields) {
      const actual = text === null ? null : f.read(text);
      if (actual !== current) {
        problems.push({
          field: `${file}: ${f.name}`,
          expected: current,
          actual,
          reason: text === null ? 'file is missing' : actual === null ? 'no version field' : 'differs from package.json',
        });
      }
    }
  }
  let tracked;
  try {
    tracked = (listManifests ?? (() => gitListManifests(root)))();
  } catch (e) {
    problems.push({ field: 'plugin manifests', expected: null, actual: null, reason: `cannot list tracked files: ${e.message}` });
    return { ok: false, problems };
  }
  const known = new Set(VERSION_FILES.map((v) => v.file));
  for (const file of tracked.filter((f) => PLUGIN_MANIFEST_RE.test(f))) {
    if (!known.has(file)) {
      problems.push({ field: file, expected: null, actual: null, reason: 'a plugin manifest that is not in the version file list (add it to VERSION_FILES in scripts/sync-version.js)' });
    }
  }
  return { ok: problems.length === 0, problems };
}

/**
 * Sets every version field (package.json included) to `version`.
 * All edits are computed and verified first; nothing is written unless every one of them succeeds.
 * Returns {ok, changed} or {ok:false, error}.
 */
export function syncVersions({ root, version }) {
  if (!isValidVersion(version)) {
    return { ok: false, error: `invalid version ${JSON.stringify(version)}: expected X.Y.Z or X.Y.Z-<letter-first prerelease>, without build metadata` };
  }
  const edits = [];
  for (const { file, fields } of VERSION_FILES) {
    const original = readText(root, file);
    if (original === null) return { ok: false, error: `${file}: file is missing` };
    let text = original;
    for (const f of fields) {
      const next = f.write(text, version);
      if (next === null || f.read(next) !== version) {
        return { ok: false, error: `${file}: cannot locate the ${f.name} field to update` };
      }
      text = next;
    }
    edits.push({ file, original, text });
  }
  const changed = [];
  for (const { file, original, text } of edits) {
    if (text === original) continue;
    fs.writeFileSync(path.join(root, file), text);
    changed.push(file);
  }
  return { ok: true, changed };
}

// ---- command line ----

const USAGE = [
  'usage: node scripts/sync-version.js <version>',
  '       node scripts/sync-version.js --check [--expect <version>]',
  '  options: --root <dir> (another tree), --no-git (skip the tracked-manifest guard)',
].join('\n');

/** Runs the command line. Returns {code, out, err}; it prints nothing itself. */
export function main(argv, defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')) {
  let root = defaultRoot;
  let check = false;
  let noGit = false;
  let expect;
  const positional = [];
  const usage = (msg) => ({ code: 2, out: '', err: `${msg}\n${USAGE}\n` });
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--check') check = true;
    else if (a === '--no-git') noGit = true;
    else if (a === '--root' || a === '--expect') {
      const v = argv[++i];
      if (v === undefined) return usage(`${a} needs a value`);
      if (a === '--root') root = path.resolve(v); else expect = v;
    } else if (a.startsWith('--')) return usage(`unknown option ${a}`);
    else positional.push(a);
  }
  if (check) {
    if (positional.length) return usage('--check takes no version argument');
    const listManifests = noGit ? () => VERSION_FILES.map((v) => v.file) : undefined;
    const r = checkVersions({ root, listManifests, expect });
    if (r.ok) {
      const v = VERSION_FILES[0].fields[0].read(readText(root, 'package.json'));
      return { code: 0, out: `version files agree: ${v}\n`, err: '' };
    }
    const lines = r.problems.map((p) => `  ${p.field}: ${p.reason}${p.expected !== null || p.actual !== null ? ` (expected ${p.expected}, found ${p.actual})` : ''}`);
    return { code: 1, out: '', err: `version check failed:\n${lines.join('\n')}\n` };
  }
  if (expect !== undefined) return usage('--expect only works with --check');
  if (positional.length !== 1) return usage(positional.length ? 'one version at a time' : 'a version or --check is required');
  const r = syncVersions({ root, version: positional[0] });
  if (!r.ok) return { code: 1, out: '', err: `${r.error}\n` };
  return {
    code: 0,
    out: r.changed.length ? `set ${positional[0]} in ${r.changed.length} files:\n${r.changed.map((f) => `  ${f}`).join('\n')}\n` : `already at ${positional[0]}: nothing to change\n`,
    err: '',
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const r = main(process.argv.slice(2));
  if (r.out) process.stdout.write(r.out);
  if (r.err) process.stderr.write(r.err);
  process.exitCode = r.code;
}
