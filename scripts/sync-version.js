// scripts/sync-version.js — the one place that knows where the release version lives.
// Zero dependencies, ESM.
//
//   node scripts/sync-version.js <version>              set every version field (package.json too)
//   node scripts/sync-version.js --check                every field equals package.json's version
//   node scripts/sync-version.js --check --expect <v>   ... and package.json's version equals <v>

import fs from 'node:fs';
import path from 'node:path';

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

const jsonField = (name, pick) => ({
  name,
  read(text) {
    try { const v = pick(JSON.parse(text)); return typeof v === 'string' ? v : null; } catch { return null; }
  },
});

const topLevel = (o) => o.version;
const lockRoot = (o) => o.packages?.['']?.version;

const yamlField = {
  name: 'version',
  read(text) {
    const m = /^version:[ \t]*["']?([^"'\s#]+)["']?[ \t]*(?:#.*)?$/m.exec(text);
    return m ? m[1] : null;
  },
};

export const VERSION_FILES = [
  { file: 'package.json', fields: [jsonField('version', topLevel)] },
  { file: 'package-lock.json', fields: [jsonField('version', topLevel), jsonField('packages[""].version', lockRoot)] },
  { file: '.claude-plugin/plugin.json', fields: [jsonField('version', topLevel)] },
  { file: '.github/plugin/plugin.json', fields: [jsonField('version', topLevel)] },
  { file: '.cursor-plugin/plugin.json', fields: [jsonField('version', topLevel)] },
  { file: '.codex-plugin/plugin.json', fields: [jsonField('version', topLevel)] },
  { file: 'hermes-plugin/plugin.yaml', fields: [yamlField] },
];

function readText(root, rel) {
  try { return fs.readFileSync(path.join(root, rel), 'utf8'); } catch { return null; }
}

/**
 * Compares every version field with package.json's version.
 * Returns {ok, problems}; each problem is {field, expected, actual, reason}.
 */
export function checkVersions({ root }) {
  const problems = [];
  const pkgText = readText(root, 'package.json');
  const pkgField = VERSION_FILES[0].fields[0];
  const expected = pkgText === null ? null : pkgField.read(pkgText);
  if (expected === null || !isValidVersion(expected)) {
    problems.push({
      field: 'package.json: version',
      expected: null,
      actual: expected,
      reason: expected === null ? 'package.json has no version' : `invalid version ${JSON.stringify(expected)}`,
    });
    return { ok: false, problems };
  }
  for (const { file, fields } of VERSION_FILES) {
    const text = readText(root, file);
    for (const f of fields) {
      if (file === 'package.json') continue;
      const actual = text === null ? null : f.read(text);
      if (actual !== expected) {
        problems.push({
          field: `${file}: ${f.name}`,
          expected,
          actual,
          reason: text === null ? 'file is missing' : actual === null ? 'no version field' : 'differs from package.json',
        });
      }
    }
  }
  return { ok: problems.length === 0, problems };
}
