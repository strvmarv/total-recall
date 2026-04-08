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
 * Passes:
 *   1. Default DB path (TOTAL_RECALL_HOME=<tmp>, TOTAL_RECALL_DB_PATH unset).
 *   2. Custom DB path (TOTAL_RECALL_DB_PATH=<deeply-nested>/custom.db).
 *   3. (Task 6) Invalid env var → startup fail-fast.
 *
 * Run locally: `npm run smoke`
 * Run in CI: after `bun run build`, on every matrix leg.
 */

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { mkdtempSync, rmSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

const SCRIPT_DIR = fileURLToPath(new URL(".", import.meta.url));
const REPO_ROOT = join(SCRIPT_DIR, "..");
const START_CJS = join(REPO_ROOT, "bin", "start.cjs");

let exitCode = 0;

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

/**
 * Connect an MCP client to a freshly-spawned total-recall server.
 * Returns {client, cleanup}. cleanup() closes the client (which kills
 * the child). Tempdir removal is the caller's responsibility.
 */
async function spawnMcpClient({ env, label }) {
  process.stdout.write(`[smoke] launching (${label}): node ${START_CJS}\n`);
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [START_CJS],
    cwd: REPO_ROOT,
    env: { ...process.env, ...env },
  });
  const client = new Client(
    { name: "mcp-smoke-test", version: "1.0.0" },
    { capabilities: {} },
  );
  await client.connect(transport);
  return {
    client,
    cleanup: async () => {
      try { await client.close(); } catch (e) { fail(`client.close threw (${label})`, e); }
    },
  };
}

/**
 * Pass 1: default DB path (TOTAL_RECALL_HOME points at tmpdir,
 * TOTAL_RECALL_DB_PATH unset). Exercises the baseline MCP server
 * against a throwaway data dir, validating tools/list, status,
 * memory_store, memory_search (critical vector path), memory_delete.
 */
async function runPass1Default() {
  let tempHome = "";
  let cleanup = async () => {};
  try {
    tempHome = mkdtempSync(join(tmpdir(), "total-recall-smoke-pass1-"));
    process.stdout.write(`[smoke] pass 1: TOTAL_RECALL_HOME=${tempHome}\n`);
    const spawned = await spawnMcpClient({
      env: { TOTAL_RECALL_HOME: tempHome, TOTAL_RECALL_DB_PATH: "" },
      label: "pass 1 default",
    });
    const { client } = spawned;
    cleanup = spawned.cleanup;
    ok("pass 1: connected to MCP server");

    const { tools } = await client.listTools();
    if (!Array.isArray(tools) || tools.length === 0) {
      throw new Error(`expected non-empty tools list, got: ${JSON.stringify(tools)}`);
    }
    const expectedTools = ["status", "memory_store", "memory_search", "memory_delete"];
    const toolNames = new Set(tools.map((t) => t.name));
    for (const name of expectedTools) {
      if (!toolNames.has(name)) throw new Error(`expected tool '${name}' in tools/list`);
    }
    ok(`pass 1: tools/list returned ${tools.length} tools (including ${expectedTools.join(", ")})`);

    const statusResult = await parseToolResult(
      await client.callTool({ name: "status", arguments: {} }),
    );
    if (!statusResult?.db?.path) throw new Error(`status missing db.path: ${JSON.stringify(statusResult)}`);
    if (!statusResult.db.path.startsWith(tempHome)) {
      throw new Error(`pass 1: status db.path not in temp home — got: ${statusResult.db.path}`);
    }
    ok(`pass 1: status: db at ${statusResult.db.path} (${statusResult.db.sizeBytes} bytes)`);

    const storeResult = await parseToolResult(
      await client.callTool({
        name: "memory_store",
        arguments: {
          content: "MCP smoke pass 1: default DB path via TOTAL_RECALL_HOME.",
          entryType: "decision",
          tags: ["smoke-test", "ci", "pass1"],
          source: "mcp-smoke-test",
          project: "total-recall",
        },
      }),
    );
    if (!storeResult?.id) throw new Error(`memory_store returned no id: ${JSON.stringify(storeResult)}`);
    const storedId = storeResult.id;
    ok(`pass 1: memory_store: id=${storedId}`);

    const searchResult = await parseToolResult(
      await client.callTool({
        name: "memory_search",
        arguments: { query: "smoke test default path", topK: 3 },
      }),
    );
    if (!Array.isArray(searchResult) || searchResult.length === 0) {
      throw new Error(`memory_search returned no results: ${JSON.stringify(searchResult)}`);
    }
    const topMatch = searchResult[0];
    if (topMatch?.entry?.id !== storedId) {
      throw new Error(`pass 1: memory_search top result wrong — expected ${storedId}, got ${topMatch?.entry?.id}`);
    }
    ok(`pass 1: memory_search: top result id=${topMatch.entry.id} score=${topMatch.score.toFixed(3)}`);

    const deleteResult = await parseToolResult(
      await client.callTool({ name: "memory_delete", arguments: { id: storedId } }),
    );
    if (deleteResult?.deleted !== true) {
      throw new Error(`memory_delete did not confirm deletion: ${JSON.stringify(deleteResult)}`);
    }
    ok(`pass 1: memory_delete: deleted ${storedId}`);
  } finally {
    await cleanup();
    if (tempHome) {
      try { rmSync(tempHome, { recursive: true, force: true }); } catch {}
    }
  }
}

/**
 * Pass 2: custom DB path via TOTAL_RECALL_DB_PATH. Verifies that:
 *   - The server honors the env var end-to-end (status reports the override).
 *   - The parent directory of the custom path is auto-created (it does not
 *     exist before the server spawns).
 *   - The SQLite file is created at exactly the configured location.
 *   - Vector search still works against the relocated DB (the critical
 *     sqlite-vec + embeddings pipeline is unaffected by the path change).
 */
async function runPass2CustomDbPath() {
  let tempHome = "";
  let tempDbDir = "";
  let cleanup = async () => {};
  try {
    tempHome = mkdtempSync(join(tmpdir(), "total-recall-smoke-pass2-home-"));
    tempDbDir = mkdtempSync(join(tmpdir(), "total-recall-smoke-pass2-db-"));
    // Deliberately deeper than the temp root: mkdirSync(dirname(dbPath)) in
    // connection.ts should create the "nested/sub" segments on first run.
    const customDbPath = join(tempDbDir, "nested", "sub", "custom.db");
    process.stdout.write(`[smoke] pass 2: TOTAL_RECALL_HOME=${tempHome}\n`);
    process.stdout.write(`[smoke] pass 2: TOTAL_RECALL_DB_PATH=${customDbPath}\n`);

    // Sanity: parent dir must NOT exist before the server spawns.
    if (existsSync(dirname(customDbPath))) {
      throw new Error(`pass 2: precondition failed — parent dir already exists: ${dirname(customDbPath)}`);
    }

    const spawned = await spawnMcpClient({
      env: {
        TOTAL_RECALL_HOME: tempHome,
        TOTAL_RECALL_DB_PATH: customDbPath,
      },
      label: "pass 2 custom db path",
    });
    const { client } = spawned;
    cleanup = spawned.cleanup;
    ok("pass 2: connected to MCP server");

    const statusResult = await parseToolResult(
      await client.callTool({ name: "status", arguments: {} }),
    );
    // NOTE: This is strict equality against the raw env-var value. If
    // getDbPath() or connection.ts ever starts resolving symlinks (e.g.
    // realpathSync), this assertion will break on macOS where tmpdir()
    // returns /var/folders/... which is a symlink to /private/var/.... The
    // spec mandates passthrough of the literal value, so preserving this
    // strict check also guards that contract.
    if (statusResult?.db?.path !== customDbPath) {
      throw new Error(
        `pass 2: status db.path mismatch — expected ${customDbPath}, got ${statusResult?.db?.path}`,
      );
    }
    ok(`pass 2: status reports custom path: ${statusResult.db.path}`);

    // Assert the file actually exists on disk at the configured location.
    if (!existsSync(customDbPath)) {
      throw new Error(`pass 2: DB file not found at ${customDbPath} after handshake`);
    }
    ok(`pass 2: DB file exists at ${customDbPath}`);

    // Assert the parent dir was auto-created (was absent pre-spawn).
    if (!existsSync(dirname(customDbPath))) {
      throw new Error(`pass 2: parent dir not created at ${dirname(customDbPath)}`);
    }
    ok(`pass 2: parent directory was auto-created: ${dirname(customDbPath)}`);

    // Vector search against the relocated DB.
    const storeResult = await parseToolResult(
      await client.callTool({
        name: "memory_store",
        arguments: {
          content: "MCP smoke pass 2: custom DB path via TOTAL_RECALL_DB_PATH.",
          entryType: "decision",
          tags: ["smoke-test", "ci", "pass2"],
          source: "mcp-smoke-test",
          project: "total-recall",
        },
      }),
    );
    if (!storeResult?.id) throw new Error(`pass 2: memory_store returned no id`);
    const storedId = storeResult.id;
    ok(`pass 2: memory_store against relocated DB: id=${storedId}`);

    const searchResult = await parseToolResult(
      await client.callTool({
        name: "memory_search",
        arguments: { query: "smoke test custom path relocated", topK: 3 },
      }),
    );
    if (!Array.isArray(searchResult) || searchResult.length === 0) {
      throw new Error(`pass 2: memory_search returned no results`);
    }
    const topMatch = searchResult[0];
    if (topMatch?.entry?.id !== storedId) {
      throw new Error(
        `pass 2: memory_search top result wrong — expected ${storedId}, got ${topMatch?.entry?.id}`,
      );
    }
    ok(`pass 2: vector search hit the relocated DB: score=${topMatch.score.toFixed(3)}`);

    const deleteResult = await parseToolResult(
      await client.callTool({ name: "memory_delete", arguments: { id: storedId } }),
    );
    if (deleteResult?.deleted !== true) {
      throw new Error(`pass 2: memory_delete did not confirm deletion: ${JSON.stringify(deleteResult)}`);
    }
    ok(`pass 2: memory_delete: deleted ${storedId}`);
  } finally {
    await cleanup();
    if (tempHome) {
      try { rmSync(tempHome, { recursive: true, force: true }); } catch {}
    }
    if (tempDbDir) {
      try { rmSync(tempDbDir, { recursive: true, force: true }); } catch {}
    }
  }
}

async function runAllPasses() {
  await runPass1Default();
  await runPass2CustomDbPath();
  // Pass 3 (invalid env var fail-fast) added in Task 6.
}

try {
  await runAllPasses();
  process.stdout.write("[smoke] ALL CHECKS PASSED\n");
} catch (e) {
  fail("smoke test aborted", e);
} finally {
  process.exit(exitCode);
}
