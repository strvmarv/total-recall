import { defineConfig } from "tsup";

export default defineConfig({
  entry: ["src/index.ts"],
  format: ["esm"],
  target: "node20",
  external: ["better-sqlite3", "onnxruntime-node"],
  banner: {
    js: "#!/usr/bin/env node",
  },
  clean: true,
});
