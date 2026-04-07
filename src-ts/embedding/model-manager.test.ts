import { describe, it, expect, afterEach, vi, beforeEach } from "vitest";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, readdirSync, rmSync } from "node:fs";
import { copyFileSync, mkdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createHash } from "node:crypto";
import { getModelPath, sha256File, writeFileAtomic, isModelChecksumValid, downloadModel, getUserModelPath } from "./model-manager.js";
import { isModelStructurallyValid } from "./model-manager.js";
import { getModelSpec } from "./registry.js";
import * as registryModule from "./registry.js";
import * as configModule from "../config.js";

describe("getModelPath", () => {
  it("returns path containing model name and .total-recall", () => {
    const modelName = "sentence-transformers/all-MiniLM-L6-v2";
    const modelPath = getModelPath(modelName);
    expect(modelPath).toContain("all-MiniLM-L6-v2");
    expect(modelPath).toContain(".total-recall");
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
    copyFileSync("tests-ts/fixtures/lfs-pointer-model.onnx", join(dir, "model.onnx"));
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

// ---------------------------------------------------------------------------
// downloadModel (T12-T13)
// ---------------------------------------------------------------------------

/**
 * Build a minimal fake spec with the given files and sha256 for model.onnx.
 */
function makeFakeSpec(onnxContent: Buffer, extraFiles: string[] = []) {
  const sha256 = createHash("sha256").update(onnxContent).digest("hex");
  const files: Record<string, string> = {
    "model.onnx": "https://example.com/model.onnx",
    "tokenizer.json": "https://example.com/tokenizer.json",
    "tokenizer_config.json": "https://example.com/tokenizer_config.json",
  };
  for (const f of extraFiles) {
    files[f] = `https://example.com/${f}`;
  }
  return {
    name: "all-MiniLM-L6-v2",
    dimensions: 384,
    revision: "main",
    sha256,
    sizeBytes: onnxContent.byteLength,
    files,
  };
}

/**
 * Build a fake fetch response that returns the given buffer via arrayBuffer()
 * and has no streaming body (body: null), which triggers the fallback path.
 */
function makeFakeResponse(buf: Buffer, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "Internal Server Error",
    body: null,
    arrayBuffer: async () => buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength),
    headers: { get: () => null },
  };
}

/** No-op sleep to skip backoff delays in tests */
const noopSleep = () => Promise.resolve();

describe("downloadModel", () => {
  let tmpDir: string;
  let getDataDirSpy: ReturnType<typeof vi.spyOn>;
  let getModelSpecSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    tmpDir = mkdtempSync(join(tmpdir(), "tr-dl-"));
    // Redirect data dir to tmp
    getDataDirSpy = vi.spyOn(configModule, "getDataDir").mockReturnValue(tmpDir);
    // Default spec spy (overridden per-test as needed)
    getModelSpecSpy = vi.spyOn(registryModule, "getModelSpec");
  });

  afterEach(async () => {
    vi.restoreAllMocks();
    if (tmpDir) {
      rmSync(tmpDir, { recursive: true, force: true });
    }
  });

  it("happy path: all files downloaded, sha256 verified, .verified written", async () => {
    const onnxBuf = Buffer.from("fake-onnx-content");
    const tokBuf = Buffer.from('{"version":"1"}');
    const tokCfgBuf = Buffer.from('{"tokenizer_class":"BertTokenizer"}');
    const fakeSpec = makeFakeSpec(onnxBuf);
    getModelSpecSpy.mockReturnValue(fakeSpec);

    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
      const u = String(url);
      if (u.includes("model.onnx")) return makeFakeResponse(onnxBuf) as unknown as Response;
      if (u.includes("tokenizer_config")) return makeFakeResponse(tokCfgBuf) as unknown as Response;
      return makeFakeResponse(tokBuf) as unknown as Response;
    });

    const progressCalls: Array<{ file: string; bytesDone: number }> = [];

    const result = await downloadModel("all-MiniLM-L6-v2", {
      onProgress: (p) => progressCalls.push({ file: p.file, bytesDone: p.bytesDone }),
      _sleep: noopSleep,
    });

    const expectedDir = join(tmpDir, "models", "all-MiniLM-L6-v2");
    expect(result).toBe(expectedDir);

    // All files present
    expect(existsSync(join(expectedDir, "model.onnx"))).toBe(true);
    expect(existsSync(join(expectedDir, "tokenizer.json"))).toBe(true);
    expect(existsSync(join(expectedDir, "tokenizer_config.json"))).toBe(true);

    // .verified sidecar exists with expected hash
    const sidecarPath = join(expectedDir, ".verified");
    expect(existsSync(sidecarPath)).toBe(true);
    expect(readFileSync(sidecarPath, "utf8").trim()).toBe(fakeSpec.sha256);

    // No leftover .tmp files
    const allFiles = readdirSync(expectedDir);
    expect(allFiles.some((f) => f.includes(".tmp."))).toBe(false);

    // onProgress called at least once per file
    const fileNames = progressCalls.map((p) => p.file);
    expect(fileNames).toContain("model.onnx");
    expect(fileNames).toContain("tokenizer.json");
    expect(fileNames).toContain("tokenizer_config.json");

    fetchSpy.mockRestore();
  });

  it("fetch fails non-2xx then retries and succeeds", async () => {
    const onnxBuf = Buffer.from("fake-onnx");
    const tokBuf = Buffer.from("{}");
    const fakeSpec = makeFakeSpec(onnxBuf);
    getModelSpecSpy.mockReturnValue(fakeSpec);

    let callCount = 0;
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
      const u = String(url);
      if (u.includes("model.onnx")) {
        callCount++;
        if (callCount === 1) return makeFakeResponse(Buffer.alloc(0), 500) as unknown as Response;
        return makeFakeResponse(onnxBuf) as unknown as Response;
      }
      return makeFakeResponse(tokBuf) as unknown as Response;
    });

    const result = await downloadModel("all-MiniLM-L6-v2", { maxRetries: 3, _sleep: noopSleep });

    const expectedDir = join(tmpDir, "models", "all-MiniLM-L6-v2");
    expect(result).toBe(expectedDir);
    expect(existsSync(join(expectedDir, "model.onnx"))).toBe(true);
    // fetch called twice for model.onnx (1 failure + 1 success), other files once each
    expect(callCount).toBe(2);

    fetchSpy.mockRestore();
  });

  it("all retries exhausted: throws error mentioning URL/status, no tmp leftover", async () => {
    const onnxBuf = Buffer.from("fake-onnx");
    const fakeSpec = makeFakeSpec(onnxBuf);
    getModelSpecSpy.mockReturnValue(fakeSpec);

    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      makeFakeResponse(Buffer.alloc(0), 500) as unknown as Response,
    );

    await expect(
      downloadModel("all-MiniLM-L6-v2", { maxRetries: 2, _sleep: noopSleep }),
    ).rejects.toThrow(/500/);

    // No leftover tmp files in the model dir
    const modelDir = join(tmpDir, "models", "all-MiniLM-L6-v2");
    if (existsSync(modelDir)) {
      const files = readdirSync(modelDir);
      expect(files.some((f) => f.includes(".tmp."))).toBe(false);
    }

    fetchSpy.mockRestore();
  });

  it("sha256 mismatch: throws error with expected/actual, removes model.onnx, no .verified", async () => {
    const onnxBuf = Buffer.from("real-onnx-content");
    const wrongOnnxBuf = Buffer.from("wrong-bytes-that-do-not-match");
    const tokBuf = Buffer.from("{}");
    // Spec has the hash of the real content, but fetch returns wrong bytes
    const fakeSpec = makeFakeSpec(onnxBuf);
    getModelSpecSpy.mockReturnValue(fakeSpec);

    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
      const u = String(url);
      if (u.includes("model.onnx")) return makeFakeResponse(wrongOnnxBuf) as unknown as Response;
      return makeFakeResponse(tokBuf) as unknown as Response;
    });

    await expect(
      downloadModel("all-MiniLM-L6-v2", { maxRetries: 1, _sleep: noopSleep }),
    ).rejects.toThrow(/expected|actual|sha256|hash/i);

    const modelDir = join(tmpDir, "models", "all-MiniLM-L6-v2");
    // model.onnx should be removed (best-effort cleanup)
    expect(existsSync(join(modelDir, "model.onnx"))).toBe(false);
    // .verified must NOT exist
    expect(existsSync(join(modelDir, ".verified"))).toBe(false);

    fetchSpy.mockRestore();
  });
});
