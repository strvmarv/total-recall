import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ensureProvisioned } from './provisioner.js';

function deps(overrides = {}) {
  return {
    binaryPath: '/fake/binaries/win-x64/total-recall.exe',
    rid: 'win-x64',
    version: '3.2.0',
    exists: () => false,
    readVerifiedMarker: () => null,
    fetchManifest: async () => ({
      version: '3.2.0',
      artifacts: { 'win-x64': { url: 'https://x/a.tar.gz', sha256: 'abc', sizeBytes: 10 } },
    }),
    ensureBinary: async () => ({ ok: true, path: '/fake/binaries/win-x64/total-recall.exe' }),
    ...overrides,
  };
}

test('fast path: present binary with matching marker skips download', async () => {
  let fetched = false;
  const r = await ensureProvisioned(deps({
    exists: () => true,
    readVerifiedMarker: () => ({ version: '3.2.0', sha256: 'abc' }),
    fetchManifest: async () => { fetched = true; return {}; },
  }));
  assert.equal(r.ok, true);
  assert.equal(fetched, false);
});

test('missing binary triggers manifest fetch + verified download', async () => {
  const r = await ensureProvisioned(deps());
  assert.equal(r.ok, true);
  assert.equal(r.binaryPath, '/fake/binaries/win-x64/total-recall.exe');
});

test('checksum mismatch is surfaced as a non-retryable failure', async () => {
  const r = await ensureProvisioned(deps({
    ensureBinary: async () => ({ ok: false, error: 'checksum mismatch', checksumMismatch: true }),
  }));
  assert.equal(r.ok, false);
  assert.equal(r.retryable, false);
});

test('manifest missing our RID fails clearly', async () => {
  const r = await ensureProvisioned(deps({
    fetchManifest: async () => ({ version: '3.2.0', artifacts: {} }),
  }));
  assert.equal(r.ok, false);
  assert.match(r.error, /no artifact/i);
});
