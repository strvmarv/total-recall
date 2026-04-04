import { randomUUID } from "node:crypto";
import { loadConfig, getDataDir } from "./config.js";
import { getDb, closeDb } from "./db/connection.js";
import { Embedder } from "./embedding/embedder.js";
import { startServer } from "./tools/registry.js";

async function main(): Promise<void> {
  const config = loadConfig();
  const db = getDb();
  const embedder = new Embedder({
    model: config.embedding.model,
    dimensions: config.embedding.dimensions,
  });
  const sessionId = randomUUID();
  process.stderr.write(`total-recall: MCP server starting (db: ${getDataDir()}/total-recall.db)\n`);
  await startServer({ db, config, embedder, sessionId });
  const cleanup = () => { closeDb(); process.exit(0); };
  process.on("SIGINT", cleanup);
  process.on("SIGTERM", cleanup);
}

main().catch((err) => {
  process.stderr.write(`total-recall: fatal error: ${err}\n`);
  process.exit(1);
});
