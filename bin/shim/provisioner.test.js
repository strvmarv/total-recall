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

test('fast path: present binary with NO marker (npm tarball) trusts presence, no download', async () => {
  // npm ships the binary without a .verified marker. Presence alone must be
  // trusted — no re-hash, no manifest fetch. This documents the deliberate
  // trust tradeoff and guards against a future regression that re-verifies on
  // marker absence.
  let fetched = false;
  const r = await ensureProvisioned(deps({
    exists: () => true,
    readVerifiedMarker: () => null,
    fetchManifest: async () => { fetched = true; return {}; },
  }));
  assert.equal(r.ok, true);
  assert.equal(r.binaryPath, '/fake/binaries/win-x64/total-recall.exe');
  assert.equal(fetched, false);
});

test('missing binary triggers manifest fetch + verified download', async () => {
  const r = await ensureProvisioned(deps());
  assert.equal(r.ok, true);
  assert.equal(r.binaryPath, '/fake/binaries/win-x64/total-recall.exe');
});

test('manifest fetch failure is surfaced as retryable', async () => {
  const r = await ensureProvisioned(deps({
    fetchManifest: async () => { throw new Error('ENOTFOUND'); },
  }));
  assert.equal(r.ok, false);
  assert.equal(r.retryable, true);
  assert.match(r.error, /manifest/i);
});

test('non-checksum download failure is retryable', async () => {
  const r = await ensureProvisioned(deps({
    ensureBinary: async () => ({ ok: false, error: 'download failed: socket hang up' }),
  }));
  assert.equal(r.ok, false);
  assert.equal(r.retryable, true);
});

test('checksum mismatch is surfaced as a non-retryable failure', async () => {
  const r = await ensureProvisioned(deps({
    ensureBinary: async () => ({ ok: false, error: 'checksum mismatch', checksumMismatch: true }),
  }));
  assert.equal(r.ok, false);
  assert.equal(r.retryable, false);
});

test('fast path: present binary with BROKEN payload re-provisions (self-heal)', async () => {
  // A truncated/interrupted extraction leaves the engine binary present but the
  // model payload incomplete. Binary-presence alone must NOT be trusted — the
  // fast path must fall through to a fresh verified download instead of trusting
  // a permanently-broken tree (the 1.5MB model.onnx bug).
  let fetched = false;
  const r = await ensureProvisioned(deps({
    exists: () => true,
    payloadIntact: () => false,
    fetchManifest: async () => { fetched = true; return {
      version: '3.2.0',
      artifacts: { 'win-x64': { url: 'https://x/a.tar.gz', sha256: 'abc', sizeBytes: 10 } },
    }; },
  }));
  assert.equal(r.ok, true);
  assert.equal(fetched, true); // proved it did NOT short-circuit on presence
});

test('fast path: present binary with intact payload still skips download', async () => {
  let fetched = false;
  const r = await ensureProvisioned(deps({
    exists: () => true,
    payloadIntact: () => true,
    fetchManifest: async () => { fetched = true; return {}; },
  }));
  assert.equal(r.ok, true);
  assert.equal(fetched, false);
});

test('manifest missing our RID fails clearly', async () => {
  const r = await ensureProvisioned(deps({
    fetchManifest: async () => ({ version: '3.2.0', artifacts: {} }),
  }));
  assert.equal(r.ok, false);
  assert.match(r.error, /no artifact/i);
});
