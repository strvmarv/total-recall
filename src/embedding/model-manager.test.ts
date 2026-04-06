import { describe, it, expect } from "vitest";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, readdirSync } from "node:fs";
import { copyFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { getModelPath, isModelDownloaded, sha256File, writeFileAtomic } from "./model-manager.js";
import { isModelStructurallyValid } from "./model-manager.js";
import { getModelSpec } from "./registry.js";

describe("getModelPath", () => {
  it("returns path containing model name and .total-recall", () => {
    const modelName = "sentence-transformers/all-MiniLM-L6-v2";
    const modelPath = getModelPath(modelName);
    expect(modelPath).toContain("all-MiniLM-L6-v2");
    expect(modelPath).toContain(".total-recall");
  });
});

describe("isModelDownloaded", () => {
  it("returns false for nonexistent path", () => {
    const result = isModelDownloaded("/tmp/nonexistent-path-that-does-not-exist-xyz");
    expect(result).toBe(false);
  });
});

describe("sha256File", () => {
  it("returns the hex sha256 of a file", async () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-sha-"));
    const f = join(dir, "x.bin");
    writeFileSync(f, "hello world");
    const hash = await sha256File(f);
    // sha256("hello world") = b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9
    expect(hash).toBe("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9");
  });
});

describe("writeFileAtomic", () => {
  it("writes via tmp file then renames", async () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-atomic-"));
    const dest = join(dir, "out.txt");
    await writeFileAtomic(dest, "payload");
    expect(readFileSync(dest, "utf8")).toBe("payload");
    // No leftover tmp files
    const leftovers = readdirSync(dir).filter((f) => f.includes(".tmp."));
    expect(leftovers).toEqual([]);
  });
});

describe("isModelStructurallyValid", () => {
  const spec = getModelSpec("all-MiniLM-L6-v2");

  it("returns false for nonexistent dir", () => {
    expect(isModelStructurallyValid("/tmp/does-not-exist-xyz-tr", spec)).toBe(false);
  });

  it("returns false when model.onnx is the LFS pointer fixture", () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-struct-"));
    copyFileSync("tests/fixtures/lfs-pointer-model.onnx", join(dir, "model.onnx"));
    writeFileSync(join(dir, "tokenizer.json"), "{}");
    writeFileSync(join(dir, "tokenizer_config.json"), "{}");
    expect(isModelStructurallyValid(dir, spec)).toBe(false);
  });

  it("returns false when a required file is missing", () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-struct-"));
    // Write a fake model.onnx with the right size but no tokenizers
    const fakeBuf = Buffer.alloc(spec.sizeBytes);
    writeFileSync(join(dir, "model.onnx"), fakeBuf);
    expect(isModelStructurallyValid(dir, spec)).toBe(false);
  });

  it("returns true when all files exist and model.onnx has expected size", () => {
    const dir = mkdtempSync(join(tmpdir(), "tr-struct-"));
    const fakeBuf = Buffer.alloc(spec.sizeBytes);
    writeFileSync(join(dir, "model.onnx"), fakeBuf);
    writeFileSync(join(dir, "tokenizer.json"), "{}");
    writeFileSync(join(dir, "tokenizer_config.json"), "{}");
    expect(isModelStructurallyValid(dir, spec)).toBe(true);
  });
});
