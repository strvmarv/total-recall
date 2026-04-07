import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

let cached: string | null = null;

/**
 * Resolve the package root by walking up from this file's location until a
 * directory containing `package.json` is found. Works identically whether
 * this module is running from the source tree (`src/pkg-root.ts`) or inlined
 * into the bundled `dist/index.js`, because both layouts sit inside the same
 * package.
 *
 * The walk is bounded to 10 levels to prevent pathological loops on corrupt
 * installs. The result is cached, so the filesystem walk runs at most once
 * per process.
 */
export function getPackageRoot(): string {
  if (cached) return cached;
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 10; i++) {
    if (existsSync(join(dir, "package.json"))) {
      cached = dir;
      return dir;
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error(
    `Unable to locate package root from ${fileURLToPath(import.meta.url)}`,
  );
}

/** Resolve a path relative to the package root. */
export function pkgPath(...segments: string[]): string {
  return join(getPackageRoot(), ...segments);
}
