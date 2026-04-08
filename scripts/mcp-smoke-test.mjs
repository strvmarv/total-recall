#!/usr/bin/env node
/**
 * End-to-end MCP smoke test.
 *
 * Why this exists: `vitest.config.ts` aliases `bun:sqlite` to a
 * `better-sqlite3`-backed shim (`tests/helpers/bun-sqlite-shim.ts`), so the
 * entire vitest suite runs against better-sqlite3 — which ships its own
 * SQLite with extension loading enabled — NOT the real `bun:sqlite` that
 * production uses. That's how the macOS darwin extension-loading bug
 * (0.6.8-beta.6) slipped past green CI on all three matrix legs.
 *
 * This script plugs that hole. It launches the built MCP server via
 * `bin/start.cjs` (which re-execs `dist/index.js` under bundled bun,
 * exercising the real `src/db/sqlite-bootstrap.ts` → `Database.setCustomSQLite()`
 * → `sqliteVec.load()` → vector query path) and drives it over stdio with
 * the real MCP client SDK. Any platform-specific runtime regression in
 * the SQLite stack will fail this test — darwin, linux, windows, all
 * the same.
 *
 * Run locally: `npm run smoke`
 * Run in CI: after `bun run build`, on every matrix leg.
 */

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPT_DIR = fileURLToPath(new URL(".", import.meta.url));
const REPO_ROOT = join(SCRIPT_DIR, "..");
const START_CJS = join(REPO_ROOT, "bin", "start.cjs");

let exitCode = 0;
const tempHome = mkdtempSync(join(tmpdir(), "total-recall-smoke-"));
let client;
let transport;

function fail(msg, err) {
  exitCode = 1;
  process.stderr.write(`[smoke] FAIL: ${msg}\n`);
  if (err) process.stderr.write(`${err.stack ?? err}\n`);
}

function ok(msg) {
  process.stdout.write(`[smoke] ok: ${msg}\n`);
}

async function parseToolResult(result) {
  // Tool results come back as { content: [{ type: "text", text: "<json>" }] }
  const text = result?.content?.[0]?.text;
  if (typeof text !== "string") {
    throw new Error(`expected text content in tool result, got: ${JSON.stringify(result)}`);
  }
  return JSON.parse(text);
}

async function main() {
  process.stdout.write(`[smoke] temp TOTAL_RECALL_HOME=${tempHome}\n`);
  process.stdout.write(`[smoke] launching: node ${START_CJS}\n`);

  transport = new StdioClientTransport({
    command: process.execPath, // current node binary — cross-platform
    args: [START_CJS],
    cwd: REPO_ROOT,
    env: {
      ...process.env,
      TOTAL_RECALL_HOME: tempHome,
    },
  });

  client = new Client({ name: "mcp-smoke-test", version: "1.0.0" }, { capabilities: {} });
  await client.connect(transport);
  ok("connected to MCP server");

  // 1. tools/list — sanity check
  const { tools } = await client.listTools();
  if (!Array.isArray(tools) || tools.length === 0) {
    throw new Error(`expected non-empty tools list, got: ${JSON.stringify(tools)}`);
  }
  const expectedTools = ["status", "memory_store", "memory_search", "memory_delete"];
  const toolNames = new Set(tools.map((t) => t.name));
  for (const name of expectedTools) {
    if (!toolNames.has(name)) {
      throw new Error(`expected tool '${name}' in tools/list`);
    }
  }
  ok(`tools/list returned ${tools.length} tools (including ${expectedTools.join(", ")})`);

  // 2. status — exercises getDb() and therefore bootstrapSqlite()
  const statusResult = await parseToolResult(
    await client.callTool({ name: "status", arguments: {} }),
  );
  if (!statusResult?.db?.path) {
    throw new Error(`status missing db.path: ${JSON.stringify(statusResult)}`);
  }
  if (!statusResult.db.path.startsWith(tempHome)) {
    throw new Error(
      `status db.path not in temp dir — env isolation failed. got: ${statusResult.db.path}`,
    );
  }
  ok(`status: db at ${statusResult.db.path} (${statusResult.db.sizeBytes} bytes)`);

  // 3. memory_store — exercises sqlite-vec INSERT into hot_memories_vec
  const storeResult = await parseToolResult(
    await client.callTool({
      name: "memory_store",
      arguments: {
        content:
          "MCP smoke test: validates bun:sqlite + sqlite-vec + embeddings end-to-end under real bun runtime.",
        entryType: "decision",
        tags: ["smoke-test", "ci"],
        source: "mcp-smoke-test",
        project: "total-recall",
      },
    }),
  );
  if (!storeResult?.id) {
    throw new Error(`memory_store returned no id: ${JSON.stringify(storeResult)}`);
  }
  const storedId = storeResult.id;
  ok(`memory_store: id=${storedId}`);

  // 4. memory_search — THE critical path. This runs a vector kNN query
  //    against hot_memories_vec, which only works if sqlite-vec loaded
  //    successfully, which only works if extension loading works, which
  //    only works if bootstrapSqlite() resolved an extension-capable
  //    libsqlite3 on this platform. This is the test that would have
  //    caught the darwin regression.
  const searchResult = await parseToolResult(
    await client.callTool({
      name: "memory_search",
      arguments: { query: "smoke test sqlite vector embeddings", topK: 3 },
    }),
  );
  if (!Array.isArray(searchResult) || searchResult.length === 0) {
    throw new Error(`memory_search returned no results: ${JSON.stringify(searchResult)}`);
  }
  const topMatch = searchResult[0];
  if (topMatch?.entry?.id !== storedId) {
    throw new Error(
      `memory_search top result is not the stored entry — expected ${storedId}, got ${topMatch?.entry?.id}`,
    );
  }
  ok(`memory_search: top result id=${topMatch.entry.id} score=${topMatch.score.toFixed(3)}`);

  // 5. memory_delete — cleanup
  const deleteResult = await parseToolResult(
    await client.callTool({ name: "memory_delete", arguments: { id: storedId } }),
  );
  if (deleteResult?.deleted !== true) {
    throw new Error(`memory_delete did not confirm deletion: ${JSON.stringify(deleteResult)}`);
  }
  ok(`memory_delete: deleted ${storedId}`);
}

try {
  await main();
  process.stdout.write("[smoke] ALL CHECKS PASSED\n");
} catch (e) {
  fail("smoke test aborted", e);
} finally {
  try {
    if (client) await client.close();
  } catch (e) {
    fail("client.close threw", e);
  }
  try {
    rmSync(tempHome, { recursive: true, force: true });
  } catch {
    // non-fatal
  }
  process.exit(exitCode);
}
