import { resolve, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import Database from "better-sqlite3";
import * as sqliteVec from "sqlite-vec";
import { initSchema } from "../db/schema.js";
import { loadConfig } from "../config.js";
import { Embedder } from "../embedding/embedder.js";
import { runBenchmark } from "./benchmark-runner.js";

const SMOKE_PASS_THRESHOLD = 0.8;

const __dirname = dirname(fileURLToPath(import.meta.url));
const PACKAGE_ROOT =
  basename(__dirname) === "dist"
    ? resolve(__dirname, "..")
    : resolve(__dirname, "..", "..");

async function main(): Promise<void> {
  const config = loadConfig();
  const db = new Database(":memory:");
  sqliteVec.load(db);
  initSchema(db);

  const embedder = new Embedder(config.embedding);
  const embed = (text: string) => embedder.embed(text);

  const corpusPath = resolve(PACKAGE_ROOT, "eval", "corpus", "memories.jsonl");
  const benchmarkPath = resolve(PACKAGE_ROOT, "eval", "benchmarks", "smoke.jsonl");

  const result = await runBenchmark(db, embed, { corpusPath, benchmarkPath });

  console.log(`Smoke benchmark: ${result.totalQueries} queries`);
  console.log(`  Exact match rate: ${(result.exactMatchRate * 100).toFixed(1)}%`);
  console.log(`  Fuzzy match rate: ${(result.fuzzyMatchRate * 100).toFixed(1)}%`);
  console.log(`  Negative pass rate: ${(result.negativePassRate * 100).toFixed(1)}%`);
  console.log(`  Avg latency: ${result.avgLatencyMs.toFixed(1)}ms`);

  db.close();

  if (result.exactMatchRate < SMOKE_PASS_THRESHOLD) {
    console.error(`\nFAIL: Exact match rate ${(result.exactMatchRate * 100).toFixed(1)}% < ${SMOKE_PASS_THRESHOLD * 100}% threshold`);
    process.exit(1);
  }

  console.log("\nPASS");
}

main().catch((err) => {
  console.error("Benchmark failed:", err);
  process.exit(1);
});
