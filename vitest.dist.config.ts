import { defineConfig } from "vitest/config";

// Separate config for the dist smoke test. Kept out of the default `npm test`
// run because it builds and spawns the real MCP server, which is slower and
// has different setup semantics than the fast unit tests under src-ts/.
export default defineConfig({
  test: {
    globals: true,
    include: ["tests-ts/dist-smoke.test.ts"],
    testTimeout: 60_000,
  },
});
