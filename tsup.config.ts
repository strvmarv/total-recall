import { defineConfig } from "tsup";
import { copyFileSync } from "node:fs";

export default defineConfig([
  {
    entry: ["src-ts/index.ts"],
    format: ["esm"],
    platform: "node",
    target: "node20",
    external: ["bun:sqlite", "onnxruntime-node"],
    noExternal: ["smol-toml", "sqlite-vec", "@modelcontextprotocol/sdk"],
    banner: {
      js: "#!/usr/bin/env node",
    },
    clean: true,
    onSuccess: async () => {
      copyFileSync("src-ts/defaults.toml", "dist/defaults.toml");
    },
  },
  {
    entry: ["src-ts/eval/ci-smoke.ts"],
    format: ["esm"],
    platform: "node",
    target: "node20",
    external: ["bun:sqlite", "onnxruntime-node"],
    noExternal: ["smol-toml", "sqlite-vec", "@modelcontextprotocol/sdk"],
    outDir: "dist/eval",
    clean: false,
    onSuccess: async () => {
      copyFileSync("src-ts/defaults.toml", "dist/eval/defaults.toml");
    },
  },
]);
