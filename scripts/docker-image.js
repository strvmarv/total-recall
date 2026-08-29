// scripts/docker-image.js
//
// Resolves the container image tag for the Docker execution path. Shared by
// bin/start-docker.js (the wrapper) and scripts/pull-docker-image.js (the
// install-time pull) so the tag string lives in exactly one place.
//
// Precedence:
//   TOTAL_RECALL_DOCKER_IMAGE (explicit override, e.g. a digest-pinned tag)
//   ghcr.io/strvmarv/total-recall:<version> (version from package.json)

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..');

export function resolveDockerImage(env = process.env, readPkg = defaultReadPkg) {
  const explicit = env.TOTAL_RECALL_DOCKER_IMAGE;
  if (explicit && explicit.length > 0) return explicit;
  return `ghcr.io/strvmarv/total-recall:${readPkg().version}`;
}

function defaultReadPkg() {
  return JSON.parse(fs.readFileSync(path.join(repoRoot, 'package.json'), 'utf8'));
}
