import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { readGolden, goldenNames } from './golden.js';

function writeFixture(content) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'golden-test-'));
  const file = path.join(dir, 'fixture.md');
  fs.writeFileSync(file, content);
  return file;
}

const BASE = `# Fixture

\`\`\`json golden:a
[1,2]
\`\`\`

Some prose.

\`\`\`json
{"untagged": true}
\`\`\`

\`\`\`json golden:b
{"x":true}
\`\`\`

\`\`\`json golden:bad
{
\`\`\`
`;

test('readGolden: returns the parsed JSON for a named golden block', () => {
  const file = writeFixture(BASE);
  assert.deepEqual(readGolden(file, 'a'), [1, 2]);
  assert.deepEqual(readGolden(file, 'b'), { x: true });
});

test('goldenNames: lists every golden name in file order, ignoring untagged blocks', () => {
  const file = writeFixture(BASE);
  assert.deepEqual(goldenNames(file), ['a', 'b', 'bad']);
});

test('readGolden: throws "not found" for a name that is not present', () => {
  const file = writeFixture(BASE);
  assert.throws(() => readGolden(file, 'zz'), /not found/);
});

test('readGolden: throws "not valid JSON" for a golden block with invalid JSON', () => {
  const file = writeFixture(BASE);
  assert.throws(() => readGolden(file, 'bad'), /not valid JSON/);
});

test('readGolden: throws "duplicated" when a name appears twice in one file', () => {
  const file = writeFixture(`${BASE}\n\`\`\`json golden:a\n[3,4]\n\`\`\`\n`);
  assert.throws(() => readGolden(file, 'a'), /duplicated/);
});
