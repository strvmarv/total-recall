import { describe, it, expect } from "vitest";
import { getModelPath, isModelDownloaded } from "./model-manager.js";

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
