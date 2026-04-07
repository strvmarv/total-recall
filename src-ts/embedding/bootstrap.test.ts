import { describe, it, expect, vi } from "vitest";
import { ModelBootstrap, buildManualInstallHint } from "./bootstrap.js";
import type { BootstrapOptions, LockAcquirer } from "./bootstrap.js";
import type { ModelSpec } from "./registry.js";
import { ModelNotReadyError } from "./errors.js";

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

  it("download failure → throws ModelNotReadyError + status reflects failure", async () => {
    const download = vi.fn().mockRejectedValue(new Error("ECONNREFUSED"));
    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
      }),
    );

    await expect(bootstrap.ensureReady()).rejects.toBeInstanceOf(ModelNotReadyError);

    let thrown: ModelNotReadyError | undefined;
    try {
      const b2 = new ModelBootstrap("test-model", makeOpts({ isStructurallyValid: () => false, download }));
      await b2.ensureReady();
    } catch (err) {
      thrown = err as ModelNotReadyError;
    }

    expect(thrown).toBeInstanceOf(ModelNotReadyError);
    expect(thrown!.reason).toBe("failed");
    expect(thrown!.modelName).toBe("test-model");
    expect(thrown!.hint).toContain("manually");
    expect(thrown!.hint).toContain("https://example.com/");

    const status = bootstrap.getStatus();
    expect(status.state).toBe("failed");
    expect(status.error?.reason).toBe("failed");
    expect(status.error?.message).toContain("ECONNREFUSED");
  });

  it("sha256 mismatch → ModelNotReadyError with reason corrupted", async () => {
    const download = vi.fn().mockRejectedValue(
      new Error("sha256 mismatch for model.onnx: expected abc, actual def"),
    );
    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
      }),
    );

    let thrown: ModelNotReadyError | undefined;
    try {
      await bootstrap.ensureReady();
    } catch (err) {
      thrown = err as ModelNotReadyError;
    }

    expect(thrown).toBeInstanceOf(ModelNotReadyError);
    expect(thrown!.reason).toBe("corrupted");
    expect(bootstrap.getStatus().state).toBe("failed");
    expect(bootstrap.getStatus().error?.reason).toBe("corrupted");
  });

  it("retry after failure: second call retries and succeeds", async () => {
    const download = vi.fn()
      .mockRejectedValueOnce(new Error("ECONNREFUSED"))
      .mockResolvedValueOnce("/fake/path");

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
      }),
    );

    await expect(bootstrap.ensureReady()).rejects.toBeInstanceOf(ModelNotReadyError);
    expect(bootstrap.getStatus().state).toBe("failed");

    const result = await bootstrap.ensureReady();
    expect(result).toBe("/fake/path");
    expect(bootstrap.getStatus().state).toBe("ready");
    expect(bootstrap.getStatus().error).toBeUndefined();
    expect(download).toHaveBeenCalledTimes(2);
  });

  it("buildManualInstallHint contains URLs and model path", () => {
    const spec: ModelSpec = {
      name: "test-model",
      dimensions: 384,
      sha256: "abc123",
      sizeBytes: 1000,
      revision: "v1",
      files: {
        "model.onnx": "https://example.com/{revision}/model.onnx",
        "tokenizer.json": "https://example.com/{revision}/tokenizer.json",
      },
    };

    const hint = buildManualInstallHint("test-model", spec, "/tmp/test-model");

    expect(hint).toContain("https://example.com/v1/model.onnx");
    expect(hint).toContain("https://example.com/v1/tokenizer.json");
    expect(hint).toContain("/tmp/test-model");
    expect(hint.toLowerCase()).toMatch(/manual/);
  });

  it("lock acquired and released on happy download", async () => {
    const release = vi.fn().mockResolvedValue(undefined);
    const acquireLock: LockAcquirer = vi.fn().mockResolvedValue({ release });
    const download = vi.fn().mockResolvedValue("/fake/path");

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
        acquireLock,
        getUserModelPath: () => "/fake/user/path",
      }),
    );

    await bootstrap.ensureReady();

    expect(acquireLock).toHaveBeenCalledWith("/fake/user/path");
    expect(release).toHaveBeenCalledOnce();
  });

  it("lock released even on download failure", async () => {
    const release = vi.fn().mockResolvedValue(undefined);
    const acquireLock: LockAcquirer = vi.fn().mockResolvedValue({ release });
    const download = vi.fn().mockRejectedValue(new Error("ECONNREFUSED"));

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
        acquireLock,
        getUserModelPath: () => "/fake/user/path",
      }),
    );

    await expect(bootstrap.ensureReady()).rejects.toBeInstanceOf(ModelNotReadyError);
    expect(release).toHaveBeenCalledOnce();
  });

  it("post-lock re-check short-circuits download", async () => {
    let callCount = 0;
    const isStructurallyValid = vi.fn().mockImplementation(() => {
      callCount++;
      // First call (pre-lock check): false. Second call (post-lock re-check): true.
      return callCount >= 2;
    });
    const isChecksumValid = vi.fn().mockResolvedValue(true);
    const release = vi.fn().mockResolvedValue(undefined);
    const acquireLock: LockAcquirer = vi.fn().mockResolvedValue({ release });
    const download = vi.fn().mockResolvedValue("/fake/path");

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid,
        isChecksumValid,
        download,
        acquireLock,
        getUserModelPath: () => "/fake/user/path",
      }),
    );

    const result = await bootstrap.ensureReady();
    expect(result).toBe("/fake/path");
    expect(bootstrap.getStatus().state).toBe("ready");
    expect(download).not.toHaveBeenCalled();
    expect(release).toHaveBeenCalledOnce();
  });

  it("lock acquisition timeout → ModelNotReadyError with reason failed", async () => {
    const acquireLock: LockAcquirer = vi.fn().mockRejectedValue(new Error("ELOCKED: timeout"));
    const download = vi.fn();

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => false,
        download,
        acquireLock,
        getUserModelPath: () => "/fake/user/path",
      }),
    );

    let thrown: ModelNotReadyError | undefined;
    try {
      await bootstrap.ensureReady();
    } catch (err) {
      thrown = err as ModelNotReadyError;
    }

    expect(thrown).toBeInstanceOf(ModelNotReadyError);
    expect(thrown!.reason).toBe("failed");
    expect(thrown!.hint).toMatch(/another process/i);
    expect(download).not.toHaveBeenCalled();
  });

  it("lock not acquired when already-valid (no download needed)", async () => {
    const acquireLock: LockAcquirer = vi.fn().mockResolvedValue({ release: vi.fn() });

    const bootstrap = new ModelBootstrap(
      "test-model",
      makeOpts({
        isStructurallyValid: () => true,
        isChecksumValid: async () => true,
        acquireLock,
        getUserModelPath: () => "/fake/user/path",
      }),
    );

    await bootstrap.ensureReady();
    expect(acquireLock).not.toHaveBeenCalled();
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
