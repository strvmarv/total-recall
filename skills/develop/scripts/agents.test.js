import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const AGENTS_DIR = path.join(HERE, '..', '..', '..', 'agents');
const NAMES = ['adversarial-reviewer', 'counterexample-reviewer'];

// Forbidden provenance strings are built from fragments so this file never contains them.
const FORBIDDEN = [
  'cor' + 'tex',
  'Ji' + 'ra',
  'AIO' + 'PS',
  'rai' + '-ops',
  'Rada' + 'ncy',
  '{no' + 'format}',
  'STR' + 'ATA',
];

function load(name) {
  const text = fs.readFileSync(path.join(AGENTS_DIR, `${name}.md`), 'utf8').replace(/\r\n/g, '\n');
  const match = /^---\n([\s\S]*?)\n---\n([\s\S]*)$/.exec(text);
  assert.ok(match, `${name}: frontmatter block`);
  const front = {};
  for (const line of match[1].split('\n')) {
    const kv = /^([a-z]+):\s*(.*)$/.exec(line);
    if (kv) front[kv[1]] = kv[2];
  }
  return { text, front, body: match[2] };
}

for (const name of NAMES) {
  test(`${name}: frontmatter`, () => {
    const { front } = load(name);
    assert.equal(front.name, name);
    assert.ok(front.description && front.description.length > 20, 'description present');
    assert.equal(front.tools, 'Read, Grep, Glob, Bash');
  });

  test(`${name}: no Edit or Write tool`, () => {
    const { front } = load(name);
    const tools = front.tools.split(',').map((t) => t.trim());
    assert.ok(!tools.includes('Edit'));
    assert.ok(!tools.includes('Write'));
  });

  test(`${name}: required ending and read-only contract`, () => {
    const { body } = load(name);
    assert.ok(body.includes('VERDICT: GO | NO-GO'));
    assert.ok(body.includes('FINDINGS:'));
    assert.ok(body.includes('- [blocker|major|minor]'));
    assert.ok(body.includes('- none'));
    assert.ok(body.includes('read-only'));
    assert.match(body, /round N of ≤ 6/);
    assert.match(body, /never dispatch/i);
  });

  test(`${name}: no forbidden provenance string`, () => {
    const { text } = load(name);
    for (const word of FORBIDDEN) {
      assert.ok(!text.toLowerCase().includes(word.toLowerCase()), `contains ${word}`);
    }
  });
}

test('counterexample-reviewer: traces defects to a concrete input or state', () => {
  const { body } = load('counterexample-reviewer');
  assert.ok(body.includes('concrete input or state'));
  assert.match(body, /does not run builds or tests/i);
});

test('adversarial-reviewer: covers its four review areas', () => {
  const { body } = load('adversarial-reviewer');
  for (const area of ['correctness', 'simplicity', 'security', 'fit with the codebase']) {
    assert.ok(body.toLowerCase().includes(area), area);
  }
});
