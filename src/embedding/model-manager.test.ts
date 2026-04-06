import { describe, it, expect, afterEach } from "vitest";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, readdirSync, rmSync } from "node:fs";
import { copyFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createHash } from "node:crypto";
import { getModelPath, isModelDownloaded, sha256File, writeFileAtomic, isModelChecksumValid } from "./model-manager.js";
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

describe("isModelChecksumValid", () => {
  let tmpDir: string;

  afterEach(() => {
    if (tmpDir) {
      rmSync(tmpDir, { recursive: true, force: true });
    }
  });

  it("happy path, no sidecar: returns true and writes .verified", async () => {
    tmpDir = mkdtempSync(join(tmpdir(), "tr-checksum-"));
    const onnxPath = join(tmpDir, "model.onnx");
    const content = Buffer.from("small test model content for checksum");
    writeFileSync(onnxPath, content);
    const expectedHash = createHash("sha256").update(content).digest("hex");
    const fakeSpec = {
      name: "test-model",
      dimensions: 384,
      revision: "main",
      sha256: expectedHash,
      sizeBytes: content.byteLength,
      files: { "model.onnx": "" },
    };

    const result = await isModelChecksumValid(tmpDir, fakeSpec);

    expect(result).toBe(true);
    const sidecarPath = join(tmpDir, ".verified");
    expect(existsSync(sidecarPath)).toBe(true);
    expect(readFileSync(sidecarPath, "utf8").trim()).toBe(expectedHash);
  });

  it("cached sidecar shortcut: returns true even when model.onnx has wrong bytes", async () => {
    tmpDir = mkdtempSync(join(tmpdir(), "tr-checksum-"));
    const cachedHash = "aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234";
    // Write sidecar with the spec hash
    writeFileSync(join(tmpDir, ".verified"), cachedHash);
    // Write model.onnx with WRONG bytes (hash won't match spec.sha256)
    writeFileSync(join(tmpDir, "model.onnx"), Buffer.from("wrong bytes that do not match"));
    const fakeSpec = {
      name: "test-model",
      dimensions: 384,
      revision: "main",
      sha256: cachedHash,
      sizeBytes: 0,
      files: {},
    };

    const result = await isModelChecksumValid(tmpDir, fakeSpec);

    // Should return true via the cached sidecar shortcut (no re-hash)
    expect(result).toBe(true);
  });

  it("mismatch: returns false and does not create .verified", async () => {
    tmpDir = mkdtempSync(join(tmpdir(), "tr-checksum-"));
    const onnxPath = join(tmpDir, "model.onnx");
    writeFileSync(onnxPath, Buffer.from("some bytes"));
    const wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";
    const fakeSpec = {
      name: "test-model",
      dimensions: 384,
      revision: "main",
      sha256: wrongHash,
      sizeBytes: 0,
      files: {},
    };

    const result = await isModelChecksumValid(tmpDir, fakeSpec);

    expect(result).toBe(false);
    expect(existsSync(join(tmpDir, ".verified"))).toBe(false);
  });

  it("missing model.onnx: returns false without throwing", async () => {
    tmpDir = mkdtempSync(join(tmpdir(), "tr-checksum-"));
    const fakeSpec = {
      name: "test-model",
      dimensions: 384,
      revision: "main",
      sha256: "aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234aaaa1234",
      sizeBytes: 0,
      files: {},
    };

    const result = await isModelChecksumValid(tmpDir, fakeSpec);

    expect(result).toBe(false);
  });
});
