import { defineConfig } from "tsup";
import { copyFileSync } from "node:fs";

export default defineConfig([
  {
    entry: ["src/index.ts"],
    format: ["esm"],
    target: "node20",
    external: ["better-sqlite3", "onnxruntime-node"],
    banner: {
      js: "#!/usr/bin/env node",
    },
    clean: true,
    onSuccess: async () => {
      copyFileSync("src/defaults.toml", "dist/defaults.toml");
    },
  },
  {
    entry: ["src/eval/ci-smoke.ts"],
    format: ["esm"],
    target: "node20",
    external: ["better-sqlite3", "onnxruntime-node"],
    outDir: "dist/eval",
    clean: false,
    onSuccess: async () => {
      copyFileSync("src/defaults.toml", "dist/eval/defaults.toml");
    },
  },
]);
