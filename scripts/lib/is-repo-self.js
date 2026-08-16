// Shared helper: detect if ROOT is the total-recall repo itself (dev install)
// vs. inside node_modules (end-user install).
// Uses path-based check because package.json name is unreliable (tarball has same name).

import { join, dirname, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, '..', '..'); // scripts/lib/ → repo root

export function isRepoSelf() {
  return !ROOT.split(sep).includes('node_modules');
}

export { ROOT };