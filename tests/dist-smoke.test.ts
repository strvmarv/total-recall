import { describe, it, expect, beforeAll, afterAll } from "vitest";
import { existsSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

// Regression test for the 0.6.6 session_start ENOENT bug.
//
// The bug lived in src/embedding/registry.ts: it computed the registry path
// with `../../models/registry.json` relative to `import.meta.url`. That was
// correct for `src/embedding/registry.ts` (the path the unit tests exercise)
// but wrong for the bundled `dist/index.js` (where everything is inlined, so
// `here = dist` and `../../models` escapes the package root). Unit tests ran
// against the source tree and missed the regression entirely.
//
// This test exercises the BUILT dist so that any future bundler-layout
// assumption of this kind fails loudly before publish.

const REPO_ROOT = resolve(__dirname, "..");
const DIST_ENTRY = resolve(REPO_ROOT, "dist", "index.js");

describe("dist smoke test", () => {
  let client: Client;

  beforeAll(async () => {
    if (!existsSync(DIST_ENTRY)) {
      // Build on demand so `npm run test:dist` works from a clean checkout.
      const result = spawnSync("npm", ["run", "build"], {
        cwd: REPO_ROOT,
        stdio: "inherit",
        shell: true,
      });
      if (result.status !== 0) {
        throw new Error("npm run build failed — cannot run dist smoke test");
      }
    }

    const transport = new StdioClientTransport({
      command: process.execPath,
      args: [DIST_ENTRY],
    });

    client = new Client(
      { name: "dist-smoke-test", version: "0.0.0" },
      { capabilities: {} },
    );

    await client.connect(transport);
  }, 60_000);

  afterAll(async () => {
    await client?.close();
  });

  it("loads the bundled server without a registry path error", async () => {
    // The bug reproduces on the first call that triggers registry loading.
    // `session_start` is the exact call that failed in 0.6.6.
    const result = await client.callTool({
      name: "session_start",
      arguments: {},
    });

    // MCP wraps tool errors in a result object with isError=true rather than
    // throwing, so assert on both shapes.
    expect(result.isError, JSON.stringify(result.content)).not.toBe(true);
  });
});
