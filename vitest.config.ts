import { defineConfig } from "vitest/config";
import { resolve } from "node:path";

export default defineConfig({
  resolve: {
    alias: {
      // bun:sqlite is a Bun built-in; shim it for Node/vitest runs
      "bun:sqlite": resolve(__dirname, "tests-ts/helpers/bun-sqlite-shim.ts"),
    },
  },
  test: {
    globals: true,
    include: ["src-ts/**/*.test.ts"],
    testTimeout: 30000,
  },
});
