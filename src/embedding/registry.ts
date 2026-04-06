import { readFileSync } from "node:fs";
import { pkgPath } from "../pkg-root.js";

export interface ModelSpec {
  name: string;
  dimensions: number;
  sha256: string;
  sizeBytes: number;
  revision: string;
  files: Record<string, string>;
}

interface RegistryFile {
  version: number;
  models: Record<string, Omit<ModelSpec, "name">>;
}

let cached: Record<string, ModelSpec> | null = null;

function findRegistryPath(): string {
  // Resolved via pkg-root walk so it works in both the source tree and the
  // bundled dist (which flattens to a single dist/index.js, making any
  // relative ../.. math from `import.meta.url` layout-dependent and fragile).
  return pkgPath("models", "registry.json");
}

export function loadRegistry(): Record<string, ModelSpec> {
  if (cached) return cached;
  const path = findRegistryPath();
  let raw: string;
  try {
    raw = readFileSync(path, "utf8");
  } catch (err) {
    throw new Error(`Failed to read model registry at ${path}: ${(err as Error).message}`);
  }
  let parsed: RegistryFile;
  try {
    parsed = JSON.parse(raw) as RegistryFile;
  } catch (err) {
    throw new Error(`Failed to parse model registry at ${path}: ${(err as Error).message}`);
  }
  if (parsed.version !== 1) {
    throw new Error(`Unsupported model registry version: ${parsed.version}`);
  }
  cached = {};
  for (const [name, spec] of Object.entries(parsed.models)) {
    cached[name] = { name, ...spec };
  }
  return cached;
}

export function getModelSpec(name: string): ModelSpec {
  const reg = loadRegistry();
  const spec = reg[name];
  if (!spec) {
    const available = Object.keys(reg).join(", ");
    throw new Error(`Unknown model "${name}". Available: ${available}`);
  }
  return spec;
}

export function expandUrl(template: string, revision: string): string {
  return template.replace(/\{revision\}/g, revision);
}

// For tests
export function _resetRegistryCache(): void {
  cached = null;
}
