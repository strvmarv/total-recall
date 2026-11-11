import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readGolden } from './golden.js';
import { readPages } from './pages.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const HANDOFF = path.join(HERE, '..', 'handoff.md');

const CASES = readGolden(HANDOFF, 'pages-cases');

test('pages-cases golden is non-empty and has both outcomes', () => {
  assert.ok(CASES.length >= 15);
  assert.ok(CASES.some((c) => c.expectPages));
  assert.ok(CASES.some((c) => c.expectError));
});

for (const c of CASES) {
  test(`readPages: ${c.name}`, () => {
    if (c.expectError) {
      assert.throws(() => readPages(c.raw));
    } else {
      assert.deepEqual(readPages(c.raw), c.expectPages);
    }
  });
}

test('readPages: an escaped quote then a brace does not end the object early', () => {
  const raw = '{"body":"5\\" pipe }"}{"n":2}';
  assert.deepEqual(readPages(raw), [{ body: '5" pipe }' }, { n: 2 }]);
});

test('readPages: a string ending in an escaped backslash closes at the next quote', () => {
  const raw = '{"body":"C:\\\\"}{"n":2}';
  assert.deepEqual(readPages(raw), [{ body: 'C:\\' }, { n: 2 }]);
});

test('readPages: a large page list is scanned in one pass', () => {
  const pages = [];
  for (let i = 0; i < 500; i += 1) pages.push({ i, text: `}{"${'x'.repeat(50)}\\` });
  const raw = pages.map((p) => JSON.stringify(p)).join('\n');
  assert.deepEqual(readPages(raw), pages);
});

test('readPages: a non-string argument throws', () => {
  assert.throws(() => readPages(undefined));
  assert.throws(() => readPages(null));
});
