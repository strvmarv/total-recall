import { resolve, dirname, basename } from "node:path";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type Database from "better-sqlite3";
import { runBenchmark, type BenchmarkResult } from "./benchmark-runner.js";

type EmbedFn = (text: string) => Float32Array | Promise<Float32Array>;

const __dirname = dirname(fileURLToPath(import.meta.url));
const PACKAGE_ROOT =
  basename(__dirname) === "dist"
    ? resolve(__dirname, "..")
    : resolve(__dirname, "..", "..");

const SMOKE_PASS_THRESHOLD = 0.8;

export interface SmokeTestResult {
  passed: boolean;
  exactMatchRate: number;
  avgLatencyMs: number;
}

export function getMetaValue(db: Database.Database, key: string): string | null {
  const row = db
    .prepare("SELECT value FROM _meta WHERE key = ?")
    .get(key) as { value: string } | undefined;
  return row?.value ?? null;
}

export function setMetaValue(db: Database.Database, key: string, value: string): void {
  db.prepare(
    "INSERT INTO _meta (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
  ).run(key, value);
}

export function getPackageVersion(): string {
  const pkgPath = resolve(PACKAGE_ROOT, "package.json");
  const pkg = JSON.parse(readFileSync(pkgPath, "utf-8")) as { version: string };
  return pkg.version;
}

export async function runSmokeTest(
  db: Database.Database,
  embed: EmbedFn,
  currentVersion: string,
): Promise<SmokeTestResult | null> {
  const lastVersion = getMetaValue(db, "smoke_test_version");
  if (lastVersion === currentVersion) return null;

  const corpusPath = resolve(PACKAGE_ROOT, "eval", "corpus", "memories.jsonl");
  const benchmarkPath = resolve(PACKAGE_ROOT, "eval", "benchmarks", "smoke.jsonl");

  const result: BenchmarkResult = await runBenchmark(db, embed, {
    corpusPath,
    benchmarkPath,
  });

  const passed = result.exactMatchRate >= SMOKE_PASS_THRESHOLD;

  setMetaValue(db, "smoke_test_version", currentVersion);

  return {
    passed,
    exactMatchRate: result.exactMatchRate,
    avgLatencyMs: result.avgLatencyMs,
  };
}
