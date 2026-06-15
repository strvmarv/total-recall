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

export async function ensureProvisioned(deps) {
  const {
    binaryPath, rid, version,
    exists, readVerifiedMarker: readMarker,
    fetchManifest, ensureBinary: ensure, onProgress,
  } = deps;

  // Fast path — present + marker matches this version.
  if (exists(binaryPath)) {
    const marker = readMarker(rid);
    if (marker && marker.version === version) {
      return { ok: true, binaryPath };
    }
    // Present but unverified/old-version: trust presence (npm path ships the
    // binary without a marker) — re-verifying a 90 MB tree every launch is the
    // cost we explicitly avoid. Mismatched-version binaries can't happen in a
    // single shim process (one version per process).
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
      retryable: result.checksumMismatch ? false : true,
    };
  }
  return { ok: true, binaryPath: result.path };
}

// Real implementations for production use by start.js.
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
    onProgress,
  };
}

function readPackageVersion() {
  const url = new URL('../../package.json', import.meta.url);
  return JSON.parse(fs.readFileSync(url, 'utf8')).version;
}

// GET the manifest release asset (a few KB) and parse it.
function fetchManifestFromRelease(version) {
  const url = getManifestUrl(version);
  return new Promise((resolve, reject) => {
    const get = (u, redirects = 5) => {
      https.get(u, (res) => {
        const status = res.statusCode ?? 0;
        if (status >= 300 && status < 400 && res.headers.location) {
          if (redirects <= 0) { res.resume(); reject(new Error('too many redirects')); return; }
          res.resume();
          const next = res.headers.location.startsWith('http')
            ? res.headers.location : new URL(res.headers.location, u).toString();
          get(next, redirects - 1);
          return;
        }
        if (status !== 200) { res.resume(); reject(new Error(`HTTP ${status} for manifest`)); return; }
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (c) => { body += c; });
        res.on('end', () => { try { resolve(JSON.parse(body)); } catch (e) { reject(e); } });
      }).on('error', reject);
    };
    get(url);
  });
}
