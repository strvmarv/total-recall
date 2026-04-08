import { randomUUID } from "node:crypto";
import { loadConfig, getDbPath, SqliteDbPathError } from "./config.js";
import { getDb, closeDb } from "./db/connection.js";
import { Embedder } from "./embedding/embedder.js";
import { startServer } from "./tools/registry.js";

async function main(): Promise<void> {
  // Validate TOTAL_RECALL_DB_PATH BEFORE anything else — before loadConfig,
  // before bun:sqlite bootstrap, before the embedding model loads, before
  // MCP transport bind. A bad env var must crash the process with a single
  // clear stderr line, not a half-broken MCP session.
  let dbPath: string;
  try {
    dbPath = getDbPath();
  } catch (e) {
    if (e instanceof SqliteDbPathError) {
      process.stderr.write(`total-recall: ${e.message}\n`);
      process.exit(1);
    }
    throw e;
  }

  const config = loadConfig();
  const db = getDb();
  const embedder = new Embedder({
    model: config.embedding.model,
    dimensions: config.embedding.dimensions,
  });
  const sessionId = randomUUID();
  process.stderr.write(`total-recall: MCP server starting (db: ${dbPath})\n`);
  await startServer({ db, config, embedder, sessionId, configSnapshotId: "default", sessionInitialized: false, sessionInitResult: null, sessionInitPromise: null });
  const cleanup = () => { closeDb(); process.exit(0); };
  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}

main().catch((err) => {
  process.stderr.write(`total-recall: fatal error: ${err}\n`);
  process.exit(1);
});
