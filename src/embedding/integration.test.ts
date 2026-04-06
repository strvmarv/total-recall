/**
 * Integration test: session_start during bootstrap — LFS pointer scenario
 *
 * Demonstrates that when an LFS pointer (or any structurally invalid model) is
 * in the bundled location, calling ensureLoaded() does NOT crash with an
 * unstructured "Protobuf parsing failed" error. Instead, the ModelNotReadyError
 * propagates cleanly with structured metadata the dispatcher can convert into a
 * structured error response.
 */
import { describe, it, expect, vi } from "vitest";
import { Embedder } from "./embedder.js";
import { ModelNotReadyError } from "./errors.js";

const MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2";
const DIMENSIONS = 384;

describe("Integration: bootstrap path wiring", () => {
  it("surfaces ModelNotReadyError (reason: corrupted) when bundled model is an LFS pointer", async () => {
    const lfsHint =
      "Bundled model.onnx appears to be a Git LFS pointer (133 bytes) — bootstrap will download from HuggingFace";

    const stubBootstrap = {
      ensureReady: vi.fn<() => Promise<string>>().mockRejectedValue(
        new ModelNotReadyError({
          modelName: MODEL_NAME,
          reason: "corrupted",
          hint: lfsHint,
        }),
      ),
    };

    const embedder = new Embedder({
      model: MODEL_NAME,
      dimensions: DIMENSIONS,
      bootstrapFactory: () => stubBootstrap,
    });

    // Precondition: not loaded yet
    expect(embedder.isLoaded()).toBe(false);

    // ensureLoaded must reject with the structured error — NOT throw an
    // unhandled "Protobuf parsing failed" or similar native ONNX error.
    await expect(embedder.ensureLoaded()).rejects.toSatisfy((err: unknown) => {
      if (!(err instanceof ModelNotReadyError)) return false;
      if (err.reason !== "corrupted") return false;
      if (!err.hint?.includes("LFS pointer")) return false;
      return true;
    });

    // isLoaded() must remain false — no partial state
    expect(embedder.isLoaded()).toBe(false);

    // The bootstrap stub was called exactly once
    expect(stubBootstrap.ensureReady).toHaveBeenCalledTimes(1);
  });
});
