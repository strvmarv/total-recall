// The update check talks to GitHub from exactly one place: UpdateChecker, started once from
// Program.RunServeAsync. These tests read source files and fail when a second start site, a
// network call in a hook or in the pi extension, or a stdout write in the checker appears.
// Comments are stripped before every check, so a header comment that names the checker or GitHub
// cannot fail one.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');

function read(rel) {
  return fs.readFileSync(path.join(ROOT, rel), 'utf8');
}

// Removes comments from C# and JS source without touching string literals ("https://x" holds a
// double slash). A quote that is not closed ends at the end of its line, which bounds the damage of
// a regular-expression literal such as /['"]/ that this scanner does not understand.
function stripCode(src) {
  let out = '';
  let i = 0;
  while (i < src.length) {
    const c = src[i];
    const n = src[i + 1];
    if (c === '/' && n === '/') {
      while (i < src.length && src[i] !== '\n') i++;
    } else if (c === '/' && n === '*') {
      const end = src.indexOf('*/', i + 2);
      i = end < 0 ? src.length : end + 2;
      out += ' ';
    } else if (c === '"' || c === "'" || c === '`') {
      out += c;
      i++;
      while (i < src.length) {
        const d = src[i];
        if (d === '\\' && c !== '`' && i + 1 < src.length) {
          out += d + src[i + 1];
          i += 2;
          continue;
        }
        if (d === '\n' && c !== '`') break;
        out += d;
        i++;
        if (d === c) break;
      }
    } else {
      out += c;
      i++;
    }
  }
  return out;
}

function stripShell(src) {
  return src.split('\n').filter((line) => !/^\s*#/.test(line)).join('\n');
}

const NETWORK = /\bfetch\s*\(|node:https?\b|require\(\s*['"]https?['"]|from\s+['"]https?['"]|\bcurl\b|\bwget\b|api\.github\.com/;

test('Program.cs starts the update checker exactly once and disposes it', () => {
  const code = stripCode(read('src/TotalRecall.Host/Program.cs'));
  assert.equal(code.split('UpdateChecker.Start(').length - 1, 1);
  assert.ok(code.includes('updateCheck?.Dispose()'));
});

test('no other C# start site names the checker or an HttpClient', () => {
  for (const rel of [
    'src/TotalRecall.Server/ServerComposition.cs',
    'src/TotalRecall.Server/Handlers/SessionStartHandler.cs',
    'src/TotalRecall.Infrastructure/WhatsNew/WhatsNewService.cs',
    'src/TotalRecall.Cli/Commands/WhatsNewCommand.cs',
    'src/TotalRecall.Cli/CliApp.cs',
  ]) {
    const code = stripCode(read(rel));
    assert.ok(!code.includes('UpdateChecker'), `${rel} names UpdateChecker`);
    assert.ok(!code.includes('HttpClient'), `${rel} names HttpClient`);
  }
});

test('the pi extension and the session-start hook make no network call', () => {
  for (const rel of ['pi/extension.js', 'pi/whatsNew.js', 'hooks/session-start/whats-new.js']) {
    assert.ok(!NETWORK.test(stripCode(read(rel))), `${rel} reaches the network`);
  }
  const sh = stripShell(read('hooks/session-start/run.sh'));
  assert.ok(!NETWORK.test(sh), 'hooks/session-start/run.sh reaches the network');
});

test('pi/extension.js imports only resolveDataDir from scripts/fetch-binary.js', () => {
  const code = stripCode(read('pi/extension.js'));
  const imports = [...code.matchAll(/import\s*\{([^}]*)\}\s*from\s*['"]\.\.\/scripts\/fetch-binary\.js['"]/g)];
  assert.ok(imports.length >= 1, 'no import from ../scripts/fetch-binary.js');
  for (const m of imports) {
    const names = m[1].split(',').map((s) => s.trim().split(/\s+as\s+/)[0]).filter(Boolean);
    assert.deepEqual(names, ['resolveDataDir']);
  }
  assert.ok(!/import\s+[\w*]/.test(code.split('\n').filter((l) => /fetch-binary/.test(l)).join('\n')),
    'a default or namespace import of fetch-binary.js');
});

test('the session-start hook uses only resolveDataDir of scripts/fetch-binary.js', () => {
  const code = stripCode(read('hooks/session-start/whats-new.js'));
  const members = new Set([...code.matchAll(/\bfetchBinary\.(\w+)/g)].map((m) => m[1]));
  assert.ok(members.size > 0, 'the hook no longer uses fetchBinary');
  assert.deepEqual([...members], ['resolveDataDir']);
});

test('UpdateChecker.cs never writes to standard output', () => {
  const code = stripCode(read('src/TotalRecall.Infrastructure/WhatsNew/UpdateChecker.cs'));
  assert.ok(!code.includes('Console.Out'));
  assert.ok(!code.includes('Console.Write'));
});

test('stripCode keeps string literals and drops comments', () => {
  const out = stripCode('a("https://x/y"); // gone\n/* gone */ b(\'x\');');
  assert.ok(out.includes('https://x/y'));
  assert.ok(!out.includes('gone'));
});
