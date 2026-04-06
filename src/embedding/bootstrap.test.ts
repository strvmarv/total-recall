import { describe, it, expect, vi } from "vitest";
import { ModelBootstrap } from "./bootstrap.js";
import type { BootstrapOptions } from "./bootstrap.js";
import type { ModelSpec } from "./registry.js";

const fakeSpec: ModelSpec = {
  name: "test-model",
  dimensions: 384,
  sha256: "abc123",
  sizeBytes: 1000,
  revision: "main",
  files: { "model.onnx": "https://example.com/{revision}/model.onnx" },
};

function makeOpts(overrides: Partial<BootstrapOptions> = {}): BootstrapOptions {
  return {
    getSpec: () => fakeSpec,
    getModelPath: () => "/fake/path",
    isStructurallyValid: () => true,
    isChecksumValid: async () => true,
    download: async () => "/fake/path",
    ...overrides,
  };
}

describe("ModelBootstrap", () => {
  it("already valid (bundled, no download)", async () => {
    const download = vi.fn().mockResolvedValue("/fake/path");
    const bootstrap = new ModelBootstrap("test-model", makeOpts({ download }));

    const result = await bootstrap.ensureReady();
    expect(result).toBe("/fake/path");
    expect(bootstrap.getStatus().state).toBe("ready");
    expect(bootstrap.getStatus().modelPath).toBe("/fake/path");
    expect(download).not.toHaveBeenCalled();
  });

  it("structural fail triggers download", async () => {
    const download = vi.fn().mockResolvedValue("/fake/path");
    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
      }),
    );

    const result = await bootstrap.ensureReady();
    expect(result).toBe("/fake/path");
    expect(bootstrap.getStatus().state).toBe("ready");
    expect(download).toHaveBeenCalledOnce();
    expect(download).toHaveBeenCalledWith("test-model", expect.objectContaining({ onProgress: expect.any(Function) }));
  });

  it("checksum fail triggers download", async () => {
    const download = vi.fn().mockResolvedValue("/fake/path");
    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => true,
        isChecksumValid: async () => false,
        download,
      }),
    );

    const result = await bootstrap.ensureReady();
    expect(result).toBe("/fake/path");
    expect(bootstrap.getStatus().state).toBe("ready");
    expect(download).toHaveBeenCalledOnce();
  });

  it("single-flight: concurrent calls share one download", async () => {
    let resolveDownload!: (v: string) => void;
    const downloadPromise = new Promise<string>((r) => {
      resolveDownload = r;
    });
    const download = vi.fn().mockReturnValue(downloadPromise);

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
      }),
    );

    const p1 = bootstrap.ensureReady();
    const p2 = bootstrap.ensureReady();
    resolveDownload("/fake/path");

    expect(await p1).toBe("/fake/path");
    expect(await p2).toBe("/fake/path");
    expect(download).toHaveBeenCalledTimes(1);
  });

  it("already-ready short-circuit: no re-validation on second call", async () => {
    const isStructurallyValid = vi.fn().mockReturnValue(true);
    const download = vi.fn().mockResolvedValue("/fake/path");
    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({ isStructurallyValid, download }),
    );

    await bootstrap.ensureReady();
    expect(isStructurallyValid).toHaveBeenCalledTimes(1);

    await bootstrap.ensureReady();
    // Should not call validation again on second call
    expect(isStructurallyValid).toHaveBeenCalledTimes(1);
    expect(download).not.toHaveBeenCalled();
  });

  it("progress is captured and kept after ready", async () => {
    let capturedOnProgress: ((p: { file: string; bytesDone: number; bytesTotal: number; fileIndex: number; fileCount: number }) => void) | undefined;

    const download = vi.fn().mockImplementation(
      async (_name: string, opts: { onProgress?: (p: { file: string; bytesDone: number; bytesTotal: number; fileIndex: number; fileCount: number }) => void }) => {
        capturedOnProgress = opts.onProgress;
        capturedOnProgress?.({ file: "model.onnx", bytesDone: 512, bytesTotal: 1024, fileIndex: 0, fileCount: 1 });
        capturedOnProgress?.({ file: "model.onnx", bytesDone: 1024, bytesTotal: 1024, fileIndex: 0, fileCount: 1 });
        return "/fake/path";
      },
    );

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
      }),
    );

    await bootstrap.ensureReady();

    // Progress is kept (not cleared) once ready so callers can inspect it
    const status = bootstrap.getStatus();
    expect(status.state).toBe("ready");
    expect(status.progress).toEqual({
      file: "model.onnx",
      bytesDone: 1024,
      bytesTotal: 1024,
      fileIndex: 0,
      fileCount: 1,
    });
  });
});
