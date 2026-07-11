import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { sha256File, getManifestUrl, verifyArchiveChecksum, payloadIntact } from './fetch-binary.js';

// --- payloadIntact: cheap size-based integrity check of the extracted model ---
// tree against the registry that ships in it. This is the guard that catches an
// interrupted/truncated extraction (the 1.5MB-vs-133MB model.onnx bug) which the
// archive-sha check cannot see because it validates the tarball, not the tree.
function makeTree(modelBytes, { sizeBytes = 64, withRegistry = true } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-payload-'));
  const modelDir = path.join(dir, 'models', 'bge-small-en-v1.5');
  fs.mkdirSync(modelDir, { recursive: true });
  fs.writeFileSync(path.join(modelDir, 'model.onnx'), Buffer.alloc(modelBytes));
  if (withRegistry) {
    fs.writeFileSync(path.join(dir, 'models', 'registry.json'), JSON.stringify({
      version: 1,
      models: { 'bge-small-en-v1.5': { dimensions: 384, sizeBytes } },
    }));
  }
  return dir;
}

test('payloadIntact: model.onnx matching registry sizeBytes is intact', () => {
  const dir = makeTree(64, { sizeBytes: 64 });
  assert.equal(payloadIntact(dir).ok, true);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: truncated model.onnx (size mismatch) is NOT intact', () => {
  const dir = makeTree(10, { sizeBytes: 133093490 }); // extracted 10 bytes of a 133MB model
  const r = payloadIntact(dir);
  assert.equal(r.ok, false);
  assert.match(r.reason, /size/i);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: missing model.onnx is NOT intact', () => {
  const dir = makeTree(64, { sizeBytes: 64 });
  fs.rmSync(path.join(dir, 'models', 'bge-small-en-v1.5', 'model.onnx'));
  assert.equal(payloadIntact(dir).ok, false);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('payloadIntact: no registry present is treated as intact (non-model / legacy trees)', () => {
  // Absence of a registry is not evidence of corruption — some trees have no
  // model payload to validate. Must NOT false-positive into a re-download loop.
  const dir = makeTree(64, { withRegistry: false });
  assert.equal(payloadIntact(dir).ok, true);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('sha256File computes the hex digest of a file', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-sha-'));
  const f = path.join(dir, 'data.bin');
  fs.writeFileSync(f, 'hello total-recall');
  const expected = crypto.createHash('sha256').update('hello total-recall').digest('hex');
  assert.equal(await sha256File(f), expected);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('getManifestUrl points at the versioned release asset', () => {
  assert.equal(
    getManifestUrl('3.2.0'),
    'https://github.com/strvmarv/total-recall/releases/download/v3.2.0/provisioning.manifest.json');
});

// verifyArchiveChecksum is the security-critical gate ensureBinary runs before
// extraction. Testing it directly keeps the suite hermetic (no network) while
// actually exercising the match / mismatch / read-failure contract. The full
// download+extract path is covered offline in the golden shim test.
test('verifyArchiveChecksum: matching digest returns null', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-vsum-'));
  const f = path.join(dir, 'a.tar.gz');
  fs.writeFileSync(f, 'archive-bytes');
  const good = crypto.createHash('sha256').update('archive-bytes').digest('hex');
  assert.equal(await verifyArchiveChecksum(f, good.toUpperCase(), 'a.tar.gz'), null); // case-insensitive
  fs.rmSync(dir, { recursive: true, force: true });
});

test('verifyArchiveChecksum: mismatch flags checksumMismatch (non-retryable)', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tr-vsum-'));
  const f = path.join(dir, 'a.tar.gz');
  fs.writeFileSync(f, 'archive-bytes');
  const bad = await verifyArchiveChecksum(f, 'deadbeef', 'a.tar.gz');
  assert.equal(bad.ok, false);
  assert.equal(bad.checksumMismatch, true);
  assert.match(bad.error, /checksum mismatch/i);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('verifyArchiveChecksum: read failure is a plain error, NOT checksumMismatch', async () => {
  const r = await verifyArchiveChecksum(path.join(os.tmpdir(), 'tr-does-not-exist-xyz'), 'deadbeef');
  assert.equal(r.ok, false);
  assert.notEqual(r.checksumMismatch, true); // a missing file may be retried; a mismatch may not
  assert.match(r.error, /checksum read failed/i);
});
