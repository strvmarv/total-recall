#!/usr/bin/env node
// Pre-publish verification: confirm all 5 platform binaries exist in
// binaries/<rid>/ before `npm publish` proceeds.
//
// Invoked by the `prepublishOnly` script in package.json (Task 6.6).
// This protects manual `npm publish` invocations against accidentally
// shipping a package that is missing prebuilts. The matrix CI workflow
// at .github/workflows/release.yml (Task 6.5) already performs the same
// check in its publish job — this script is the belt to CI's suspenders.
//
// Zero dependencies — Node built-ins only.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..');

const expected = [
  { rid: 'linux-x64', exe: 'total-recall' },
  { rid: 'linux-arm64', exe: 'total-recall' },
  { rid: 'darwin-x64', exe: 'total-recall' },
  { rid: 'darwin-arm64', exe: 'total-recall' },
  { rid: 'win-x64', exe: 'total-recall.exe' },
];

const missing = [];
for (const { rid, exe } of expected) {
  const p = path.join(repoRoot, 'binaries', rid, exe);
  if (!fs.existsSync(p)) {
    missing.push(p);
  }
}

if (missing.length > 0) {
  process.stderr.write(
    '[verify-binaries] Refusing to publish — missing prebuilt binaries:\n'
  );
  for (const p of missing) {
    process.stderr.write(`  - ${p}\n`);
  }
  process.stderr.write(
    '\n  Run the matrix release workflow (.github/workflows/release.yml)\n' +
      '  to produce all 5 RIDs, or stage them locally before publishing.\n'
  );
  process.exit(1);
}

process.stdout.write(
  '[verify-binaries] OK — all 5 platform binaries present.\n'
);
