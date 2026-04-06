import { describe, it, expect } from "vitest";
import { loadRegistry, getModelSpec, expandUrl } from "./registry.js";

describe("loadRegistry", () => {
  it("returns a map of model specs", () => {
    const reg = loadRegistry();
    expect(reg["all-MiniLM-L6-v2"]).toBeDefined();
    expect(reg["all-MiniLM-L6-v2"]?.dimensions).toBe(384);
    expect(reg["all-MiniLM-L6-v2"]?.sha256).toMatch(/^[0-9a-f]{64}$/);
    expect(reg["all-MiniLM-L6-v2"]?.sizeBytes).toBeGreaterThan(1_000_000);
  });
});

describe("getModelSpec", () => {
  it("returns the named spec", () => {
    const spec = getModelSpec("all-MiniLM-L6-v2");
    expect(spec.name).toBe("all-MiniLM-L6-v2");
    expect(spec.files["model.onnx"]).toContain("huggingface.co");
  });

  it("throws with available names when unknown", () => {
    expect(() => getModelSpec("does-not-exist")).toThrow(/all-MiniLM-L6-v2/);
  });
});

describe("expandUrl", () => {
  it("substitutes {revision}", () => {
    expect(expandUrl("https://x/{revision}/y", "main")).toBe("https://x/main/y");
    expect(expandUrl("https://x/{revision}/y", "abc123")).toBe("https://x/abc123/y");
  });
});
