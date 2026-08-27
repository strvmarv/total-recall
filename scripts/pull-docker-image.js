// scripts/pull-docker-image.js
//
// Install-time image pull for the Docker execution path. Called from
// scripts/postinstall.js so the container image is present before the first
// `serve` — the same reason the native path downloads the binary in postinstall
// (a first-run pull would block the MCP initialize handshake past the harness's
// hard startup timeout).
//
// Best-effort and non-fatal by design:
//   - skipped silently when docker is not on PATH (native-only users)
//   - skipped silently when the image is already present (idempotent)
//   - non-fatal on pull failure (offline install / registry unreachable) — the
//     wrapper surfaces a clear error at first run instead.

import { spawnSync } from 'node:child_process';
import { resolveDockerImage } from './docker-image.js';

export function pullDockerImage({
  env = process.env,
  spawnSyncFn = spawnSync,
  log = (msg) => process.stderr.write(msg),
} = {}) {
  const tag = resolveDockerImage(env);

  // Presence check first so a re-install does not hit the registry.
  const inspect = spawnSyncFn('docker', ['image', 'inspect', tag], { stdio: 'ignore' });
  if (inspect.error) return { skipped: 'no-docker' };   // docker not installed
  if (inspect.status === 0) return { skipped: 'present' };

  const pull = spawnSyncFn('docker', ['pull', tag], { stdio: 'inherit' });
  if (pull.error) {
    log(`[total-recall:postinstall] warning: docker pull failed: ${pull.error.message}\n`);
    return { ok: false };
  }
  if (pull.status !== 0) {
    log(`[total-recall:postinstall] warning: docker pull ${tag} failed (status ${pull.status}); the image will be pulled on first run.\n`);
    return { ok: false };
  }
  return { ok: true };
}
