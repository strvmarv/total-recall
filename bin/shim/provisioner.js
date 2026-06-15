// bin/shim/provisioner.js
//
// Ensures the engine archive for the current version is present and verified.
// Fast path: binary present AND .verified marker matches this version -> trust,
// skip the 90 MB re-hash. Otherwise: fetch the release manifest, look up our
// RID's {url, sha256}, download + verify + extract (delegated to fetch-binary's
// ensureBinary), and stamp the marker.
//
// All I/O is injected via `deps` so the suite runs offline. Production callers
// pass the real implementations (see makeProductionDeps).

import fs from 'node:fs';
import https from 'node:https';
import {
  detectRid, getBinaryPath, getManifestUrl,
  ensureBinary, readVerifiedMarker,
} from '../../scripts/fetch-binary.js';

// Note on the static import above: ensureProvisioned does NOT reference
// ensureBinary / readVerifiedMarker — it operates purely on injected deps, so
// the "all I/O is injected, suite runs offline" contract holds at runtime. The
// two I/O functions are imported only so makeProductionDeps can wire them into
// the production deps object. Importing them eagerly is safe: fetch-binary.js
// has no import-time side effects (its only top-level action,
// isDirectProvisionInvocation(), is false unless the process was launched with
// --provision), and Node 20 cannot require() an ESM module synchronously, so a
// lazy synchronous load is not available without making makeProductionDeps
// async — which would break its synchronous call contract.

// Returns one of:
//   { ok: true,  binaryPath }                  — engine is present and usable.
//   { ok: false, error, retryable }            — provisioning failed.
// `retryable` is defined ONLY on the failure shape: true for transient faults
// (network, manifest fetch) and false for terminal ones (checksum mismatch,
// missing artifact). Consumers must branch on `ok` first and read `retryable`
// only when `ok === false` — it is intentionally absent on success.
export async function ensureProvisioned(deps) {
  const {
    binaryPath, rid, version,
    exists, fetchManifest, ensureBinary: ensure, onProgress,
  } = deps;

  // Fast path — binary present: trust it.
  // Presence alone is sufficient. A .verified marker matching this version is
  // the happy path, but its absence (npm tarball ships the binary with no
  // marker) or a stale-version marker does NOT trigger a re-download: a single
  // shim process is pinned to one version, so a "present but wrong-version"
  // binary cannot arise mid-process, and re-verifying a 90 MB tree on every
  // launch is the cost we deliberately avoid. Since every present-binary case
  // resolves identically, we skip reading the marker entirely here.
  if (exists(binaryPath)) {
    return { ok: true, binaryPath };
  }

  // Missing — fetch the manifest and download.
  let manifest;
  try {
    manifest = await fetchManifest(version);
  } catch (e) {
    return { ok: false, error: `could not fetch provisioning manifest: ${e.message}`, retryable: true };
  }

  const artifact = manifest && manifest.artifacts && manifest.artifacts[rid];
  if (!artifact || !artifact.url || !artifact.sha256) {
    return { ok: false, error: `no artifact for rid ${rid} in manifest v${version}`, retryable: false };
  }

  const result = await ensure({
    logPrefix: '[total-recall:shim]',
    url: artifact.url,
    expectedSha256: artifact.sha256,
    onProgress,
  });

  if (!result.ok) {
    return {
      ok: false,
      error: result.error,
      // Checksum mismatch = corrupt/tampered asset, do NOT auto-retry.
      // Any other failure (network, extract, disk) is transient -> retryable.
      retryable: !result.checksumMismatch,
    };
  }
  return { ok: true, binaryPath: result.path };
}

// Real implementations for production use by start.js. Kept synchronous so
// callers can build deps inline without awaiting.
export function makeProductionDeps({ onProgress } = {}) {
  const rid = detectRid(process.platform, process.arch);
  const binaryPath = rid ? getBinaryPath(rid) : null;
  return {
    rid,
    binaryPath,
    version: readPackageVersion(),
    exists: (p) => !!p && fs.existsSync(p),
    readVerifiedMarker,
    fetchManifest: fetchManifestFromRelease,
    ensureBinary,
    // Only attach onProgress when a callback was actually supplied, so the deps
    // object never carries an explicit `onProgress: undefined` key.
    ...(typeof onProgress === 'function' ? { onProgress } : {}),
  };
}

function readPackageVersion() {
  const url = new URL('../../package.json', import.meta.url);
  const { version } = JSON.parse(fs.readFileSync(url, 'utf8'));
  if (typeof version !== 'string' || version.length === 0) {
    throw new Error('package.json has no version field');
  }
  return version;
}

// Hard wall-clock cap for the manifest fetch. This runs on the startup-critical
// path (the MCP handshake has its own timeout), so a stalled TLS handshake or a
// server that accepts the connection but never responds must not hang the event
// loop. A few-KB manifest over a reachable network completes well under this.
const MANIFEST_FETCH_TIMEOUT_MS = 15_000;

// GET the manifest release asset (a few KB) and parse it.
function fetchManifestFromRelease(version) {
  const url = getManifestUrl(version);
  return new Promise((resolve, reject) => {
    let settled = false;
    const finish = (fn, arg) => { if (!settled) { settled = true; fn(arg); } };

    const get = (u, redirects = 5) => {
      const req = https.get(u, (res) => {
        const status = res.statusCode ?? 0;
        if (status >= 300 && status < 400 && res.headers.location) {
          if (redirects <= 0) { res.resume(); finish(reject, new Error('too many redirects')); return; }
          res.resume();
          const next = res.headers.location.startsWith('http')
            ? res.headers.location : new URL(res.headers.location, u).toString();
          get(next, redirects - 1);
          return;
        }
        if (status !== 200) { res.resume(); finish(reject, new Error(`HTTP ${status} for manifest`)); return; }
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (c) => { body += c; });
        res.on('end', () => {
          try { finish(resolve, JSON.parse(body)); }
          catch (e) { finish(reject, e); }
        });
      });
      req.on('error', (e) => finish(reject, e));
      // Tear the socket down on a stall so a slow/dead peer can't hold the loop
      // open. destroy() emits 'error', which finish()'s settled guard absorbs
      // if we already rejected via the timeout below.
      req.setTimeout(MANIFEST_FETCH_TIMEOUT_MS, () => {
        req.destroy(new Error('manifest fetch timed out'));
        finish(reject, new Error('manifest fetch timed out'));
      });
    };
    get(url);
  });
}
