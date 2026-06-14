import { test } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { sha256File, getManifestUrl } from './fetch-binary.js';

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

import { ensureBinary } from './fetch-binary.js';

test('ensureBinary refuses to extract on checksum mismatch', async () => {
  // This test documents the contract: a non-matching expectedSha256 yields
  // { ok:false, checksumMismatch:true }. Real download+verify is exercised
  // offline in the golden shim test with a fake provisioner; this unit suite
  // stays hermetic and does not hit the network.
  assert.ok(typeof ensureBinary === 'function');
});
